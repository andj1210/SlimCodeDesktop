// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// Resumable-session discovery for the native agent.
///
/// Layout on disk:
///   <c>%APPDATA%\SlimCode\&lt;profile&gt;\sessions\native\&lt;projectSlug&gt;\&lt;sessionId&gt;.jsonl</c>
///
/// Format: one JSON object per line. The first line is a <c>"meta"</c>
/// record (session id, model, system prompt snapshot, recorded project
/// path); subsequent lines are <c>"msg"</c> records mirroring
/// <see cref="ChatMessage"/>. Tool results are messages with role=tool
/// referencing the originating call id.
///
/// The slug is one-way (see <see cref="Sessions.TranscriptStore.ProjectSlug"/>);
/// collisions are detected by comparing each meta record's recorded ProjectPath
/// against the project we're enumerating for.
/// </summary>
public sealed partial class NativeAgent
{
   private const int TITLE_MAX_CHARS   = 80;
   private const int PREVIEW_MAX_CHARS = 4096;
   private const int CAPTION_MAX_CHARS = 120;

   public override IReadOnlyList<AgentSession> EnumerateSessions(string projectPath)
   {
      var dir = TranscriptStore.SessionsDirectory(projectPath);
      if (dir == null || !Directory.Exists(dir))
         return Array.Empty<AgentSession>();

      var result = new List<AgentSession>();
      var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      // The lock spans the parse pass rather than just the dictionary reads:
      // two enumerations of the same project would otherwise duplicate the
      // work they're here to avoid. Enumeration always runs off the UI thread.
      lock (s_parseLock)
      {
         if (!s_parseCache.TryGetValue(dir, out var byFile))
            s_parseCache[dir] = byFile = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);

         foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
         {
            // Subagent companion transcripts live beside their parent session
            // (<sid>.sub.<callId>.jsonl) and are never sessions of their own.
            if (TranscriptStore.IsSubagentFile(file)) continue;
            seen.Add(file);

            DateTime stamp;
            long     length;
            try
            {
               var info = new FileInfo(file);
               stamp  = info.LastWriteTimeUtc;
               length = info.Length;
            }
            catch { continue; }

            // Transcripts are append-only, so (mtime, length) is a sound
            // identity: an active session grows and re-parses, an idle one
            // costs a stat instead of a full JSON parse of every line.
            if (!byFile.TryGetValue(file, out var parsed) ||
                parsed.LastWriteUtc != stamp || parsed.Length != length)
            {
               parsed = new ParsedFile
               {
                  LastWriteUtc = stamp,
                  Length       = length,
                  Session      = TryReadSession(file, projectPath),
               };
               byFile[file] = parsed;
            }

            if (parsed.Session.HasValue) result.Add(parsed.Session.Value);
         }

         // Forget files that have gone away (deleted sessions). Cheap, and it
         // keeps the cache from growing without bound across a long run.
         if (byFile.Count > seen.Count)
         {
            var stale = new List<string>();
            foreach (var key in byFile.Keys)
               if (!seen.Contains(key)) stale.Add(key);
            foreach (var key in stale) byFile.Remove(key);
         }
      }

      result.Sort((a, b) => b.LastUsed.CompareTo(a.LastUsed));
      return result;
   }

   public override bool CanDeleteSessions => true;

   public override bool CanRenameSessions => true;

   /// <summary>
   /// Name a session by appending a "title" record. A blank
   /// <paramref name="caption"/> clears the name and the picker falls back to
   /// the first user prompt. Safe on a session that is open in a tab — the
   /// transcript is append-only and the write is one line.
   /// </summary>
   public override bool RenameSession(string projectPath, string sessionId, string caption)
   {
      if (string.IsNullOrWhiteSpace(sessionId)) return false;
      if (sessionId.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;

      var file = TranscriptStore.FileFor(projectPath, sessionId);
      if (file == null) return false;

      var clean = (caption ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
      if (clean.Length > CAPTION_MAX_CHARS) clean = clean.Substring(0, CAPTION_MAX_CHARS);
      return TranscriptStore.WriteTitle(file, clean);
   }

   public override bool DeleteSession(string projectPath, string sessionId)
   {
      if (string.IsNullOrWhiteSpace(sessionId)) return false;
      if (sessionId.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;

      var file = TranscriptStore.FileFor(projectPath, sessionId);
      if (file == null) return false;
      try
      {
         if (!File.Exists(file)) return false;
         File.Delete(file);
      }
      catch { return false; }

      // Forks of this session are deliberately NOT touched. A subagent
      // transcript is a fragment of its parent and is meaningless without it;
      // a fork is a whole conversation that merely started as a copy. Its
      // meta still names this session as its parent, which is fine — the
      // picker demotes an unresolvable parent to a root, and if the file ever
      // comes back (restore, undelete) the lineage comes back with it.
      //
      // The glob below cannot reach a fork by construction: subagent files are
      // "<thisId>.sub.<callId>.jsonl", while a fork carries a session id of
      // its own.

      // Take the session's subagent transcripts with it. Best effort — the
      // session itself is already gone.
      try
      {
         var dir = Path.GetDirectoryName(file);
         if (!string.IsNullOrEmpty(dir))
            foreach (var sub in Directory.EnumerateFiles(
                        dir, sessionId + TranscriptStore.SUB_FILE_MARK + "*.jsonl"))
               try { File.Delete(sub); } catch { }
      }
      catch { }

      // Audit-log cleanup: unless the user opted to keep audit history
      // forever, drop entries tied to the session we just deleted. Best
      // effort — the session is already gone, the deletion call shouldn't
      // fail just because the audit log couldn't be rewritten.
      if (!Settings.PreserveAuditOnSessionDelete)
      {
         var logPath = _AuditLogPath(projectPath);
         if (logPath != null)
            PermissionAuditLog.Prune(logPath, e => e.SessionId != sessionId);
      }
      return true;
   }

   internal static string? _AuditLogPath(string projectPath)
   {
      var metaDir = ProjectPolicyStore.ProjectMetadataDir(projectPath);
      if (string.IsNullOrEmpty(metaDir)) return null;
      return Path.Combine(metaDir, "permissions.log.jsonl");
   }

   // -- per-file parse -------------------------------------------------------

   // Populate an AgentSession card from a transcript: meta record, caption,
   // first user message. Cwd mismatch → discard (slug collision protection).
   private static AgentSession? TryReadSession(string filePath, string projectPath)
   {
      var id = Path.GetFileNameWithoutExtension(filePath);
      DateTime lastUsed;
      try { lastUsed = File.GetLastWriteTime(filePath); }
      catch { lastUsed = DateTime.MinValue; }

      var records = TranscriptStore.Read(filePath);
      if (records.Count == 0) return null;

      string? firstUserPrompt = null;
      string? caption         = null;
      string? parentId        = null;
      string? forkKind        = null;

      // Full scan rather than stopping at the first user message: "title"
      // records are appended, so a caption can sit anywhere in the file and
      // the last one wins. Read() has already parsed every line by now, so
      // there was never anything to save by breaking early.
      foreach (var r in records)
      {
         if (r.Type == "meta")
         {
            // Belt-and-suspenders to the file-name filter: a subagent
            // transcript is never a resumable session, however it's named.
            if (string.Equals(r.Kind, TranscriptStore.SUBAGENT_KIND, StringComparison.OrdinalIgnoreCase))
               return null;
            if (!string.IsNullOrEmpty(r.ProjectPath) &&
                !SamePath(r.ProjectPath!, projectPath))
            {
               return null; // slug collision — different project
            }
            // Lineage is carried through as-is. Whether the parent still
            // exists is decided by the picker (SessionLineage), not here:
            // this session is complete and resumable either way.
            parentId = string.IsNullOrEmpty(r.ParentSessionId) ? null : r.ParentSessionId;
            forkKind = string.IsNullOrEmpty(r.ForkKind)        ? null : r.ForkKind;
         }
         else if (r.Type == "title")
         {
            // Blank clears a previously set caption.
            caption = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title!.Trim();
         }
         else if (r.Type == "msg" && r.Role == "user" && firstUserPrompt == null)
         {
            firstUserPrompt = r.Content;
         }
      }

      // Title is what the picker renders: the caption when there is one,
      // otherwise the derived first-prompt label. Both go through MakeTitle so
      // an over-long caption is trimmed the same way.
      var title   = MakeTitle(caption ?? firstUserPrompt, id);
      var preview = MakePreview(firstUserPrompt);

      // SessionId is just the id again: the transcript filename is both the
      // display/delete key and the handle LaunchAgent resumes from.
      return new AgentSession(id, title, preview, lastUsed, id, ContextTokens: 0, Caption: caption,
                              ParentId: parentId, ForkKind: forkKind);
   }

   private static bool SamePath(string a, string b)
   {
      if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
      try
      {
         var na = Path.GetFullPath(a).TrimEnd('\\', '/');
         var nb = Path.GetFullPath(b).TrimEnd('\\', '/');
         return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
      }
      catch { return false; }
   }

   private static string MakeTitle(string? text, string fallbackId)
   {
      if (string.IsNullOrWhiteSpace(text)) return fallbackId;

      var sb = new StringBuilder(Math.Min(text.Length, TITLE_MAX_CHARS + 8));
      bool prevSpace = false;
      foreach (var ch in text)
      {
         if (char.IsWhiteSpace(ch))
         {
            if (!prevSpace && sb.Length > 0) sb.Append(' ');
            prevSpace = true;
         }
         else
         {
            sb.Append(ch);
            prevSpace = false;
         }
         if (sb.Length >= TITLE_MAX_CHARS) break;
      }
      var trimmed = sb.ToString().TrimEnd();
      if (text.Length > trimmed.Length) trimmed += "…";
      return trimmed.Length == 0 ? fallbackId : trimmed;
   }

   private static string MakePreview(string? text)
   {
      if (string.IsNullOrEmpty(text)) return "";
      if (text.Length <= PREVIEW_MAX_CHARS) return text;
      return text.Substring(0, PREVIEW_MAX_CHARS) + "\n…";
   }

   // -- parse cache ----------------------------------------------------------

   /// <summary>One transcript's parse result, keyed by the file stamp it came
   /// from. <see cref="Session"/> null = the file is not a resumable session
   /// (subagent companion, slug collision, empty) — worth caching too, so a
   /// foreign project's transcripts aren't re-parsed on every visit.</summary>
   private sealed class ParsedFile
   {
      public DateTime      LastWriteUtc;
      public long          Length;
      public AgentSession? Session;
   }

   // sessions directory -> file path -> parse. Sessions grow append-only for
   // the life of an open chat, so the picker cannot cache the *list*; caching
   // the per-file parse gets the same win without the staleness.
   private static readonly Dictionary<string, Dictionary<string, ParsedFile>> s_parseCache =
      new(StringComparer.OrdinalIgnoreCase);

   private static readonly object s_parseLock = new();
}
