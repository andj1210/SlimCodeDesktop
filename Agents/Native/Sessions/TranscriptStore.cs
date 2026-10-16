// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Sessions;

/// <summary>
/// Append-only JSONL writer + one-shot reader for native-agent session
/// transcripts. On-disk layout:
///   <c>%APPDATA%\SlimCode\&lt;profile&gt;\sessions\native\&lt;projectSlug&gt;\&lt;sessionId&gt;.jsonl</c>
///
/// Every write opens the file, appends one UTF-8 line, and closes — slow per
/// call but trivially crash-safe (the file always ends on a complete record)
/// and the per-turn message rate is low enough that the cost doesn't matter.
///
/// Project paths are encoded by <see cref="ProjectSlug"/> (lowercase, anything
/// non-alphanumeric → <c>-</c>), so the slug is a stable one-way hash.
/// Collisions are vanishingly rare and detected on read by
/// comparing each meta record's <see cref="TranscriptRecord.ProjectPath"/>
/// against the requested project.
/// </summary>
public sealed class TranscriptStore
{
   public TranscriptStore(string filePath)
   {
      _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
   }

   public string FilePath => _filePath;

   // -- writers --------------------------------------------------------------

   /// <summary>Write the session header. Call exactly once before any messages.
   /// <paramref name="kind"/> tags non-session transcripts (see
   /// <see cref="SUBAGENT_KIND"/>) so enumeration can skip them regardless
   /// of file naming. <paramref name="parentSessionId"/> +
   /// <paramref name="forkKind"/> record lineage for a forked session; both
   /// are display-only (see
   /// <see cref="TranscriptRecord.ParentSessionId"/>).</summary>
   public void AppendMeta(string sessionId, string model, string provider, string systemPrompt, string projectPath,
                          string? kind = null, string? parentSessionId = null, string? forkKind = null)
   {
      var rec = new TranscriptRecord
      {
         Type            = "meta",
         SessionId       = sessionId,
         StartedAt       = DateTime.UtcNow,
         Model           = model,
         Provider        = provider,
         ProjectPath     = projectPath,
         SystemPrompt    = systemPrompt,
         Kind            = kind,
         ParentSessionId = string.IsNullOrEmpty(parentSessionId) ? null : parentSessionId,
         ForkKind        = string.IsNullOrEmpty(forkKind) ? null : forkKind,
      };
      AppendRecord(rec);
   }

   /// <summary>
   /// Persist a mid-session model switch. On resume the latest such record
   /// (or, lacking any, the meta) governs which model the loop continues with.
   /// Position matters: written after the user message that prompted the
   /// switch but before the next assistant response so replay rebuilds the
   /// visual marker in the right place.
   /// </summary>
   public void AppendModelChange(string model, string provider)
   {
      AppendRecord(new TranscriptRecord
      {
         Type     = "model",
         Model    = model ?? "",
         Provider = provider ?? "",
      });
   }

   /// <summary>
   /// Persist a mid-session mode switch (Plan ↔ Implement). On resume
   /// the latest such record governs the session's effective mode.
   /// </summary>
   public void AppendModeChange(AgentMode mode)
   {
      AppendRecord(new TranscriptRecord
      {
         Type = "mode",
         Mode = mode.ToString(),
      });
   }

   /// <summary>
   /// Persist the set of MCP tool names the model is being advertised. Written
   /// on an MCP session's first send and again whenever the set changes, so a
   /// later resume can diff against the last known list.
   ///
   /// Appended like "model" and "mode" rather than patched into the meta: the
   /// file is append-only, and the LAST such record is the one that counts.
   /// </summary>
   public void AppendMcpTools(IReadOnlyList<string> toolNames)
   {
      AppendRecord(new TranscriptRecord
      {
         Type     = "mcpTools",
         McpTools = new List<string>(toolNames ?? Array.Empty<string>()),
      });
   }

   /// <summary>
   /// Persist a session caption. Appended rather than patched into the meta
   /// record so renaming stays a one-line write on an append-only file;
   /// readers take the last such record. An empty/blank caption clears it
   /// (the picker falls back to the first user prompt again).
   /// </summary>
   public void AppendTitle(string title)
   {
      AppendRecord(new TranscriptRecord
      {
         Type  = "title",
         Title = title ?? "",
      });
   }

   /// <summary>
   /// Append a caption to a session file without holding a store instance —
   /// the rename path in the session picker, which never opens the session.
   /// Returns false if the file doesn't exist.
   /// </summary>
   public static bool WriteTitle(string filePath, string title)
   {
      if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
      new TranscriptStore(filePath).AppendTitle(title);
      return true;
   }

   /// <summary>
   /// Persist a session-scope permission grant so a later resume of this
   /// session can restore it (re-seeding the gate's session cache). The
   /// in-memory cache is otherwise lost on close. Replay takes the last
   /// record per tool, so re-granting simply appends a newer record.
   /// </summary>
   public void AppendSessionGrant(string toolName, PermissionDecision decision)
   {
      AppendRecord(new TranscriptRecord
      {
         Type         = "perm",
         PermTool     = toolName ?? "",
         PermDecision = decision.ToString(),
      });
   }

   private static List<ImageRecord>? ToImageRecords(List<ImagePart>? images)
   {
      if (images == null || images.Count == 0) return null;
      var list = new List<ImageRecord>(images.Count);
      foreach (var img in images)
      {
         if (img == null || string.IsNullOrEmpty(img.Base64)) continue;
         list.Add(new ImageRecord
         {
            MediaType = img.MediaType,
            Base64    = img.Base64,
            Width     = img.Width,
            Height    = img.Height,
            FileName  = img.FileName,
         });
      }
      return list.Count > 0 ? list : null;
   }

   /// <summary>Rebuild attached images from a stored record. Null when it has none.</summary>
   public static List<ImagePart>? ImagesFrom(TranscriptRecord r)
   {
      if (r.Images == null || r.Images.Count == 0) return null;
      var list = new List<ImagePart>(r.Images.Count);
      foreach (var img in r.Images)
      {
         if (img == null || string.IsNullOrEmpty(img.Base64)) continue;
         list.Add(new ImagePart
         {
            MediaType = string.IsNullOrEmpty(img.MediaType) ? "image/png" : img.MediaType,
            Base64    = img.Base64,
            Width     = img.Width,
            Height    = img.Height,
            FileName  = img.FileName,
         });
      }
      return list.Count > 0 ? list : null;
   }

   private static TimingRecord? ToTimingRecord(Llm.PhaseTimings? timings)
   {
      if (!timings.HasValue || timings.Value.IsEmpty) return null;
      var t = timings.Value;
      return new TimingRecord
      {
         McpConnectMs     = Ms(t.McpConnect),
         PrefillMs        = Ms(t.Prefill),
         ThinkingMs       = Ms(t.Thinking),
         OutputMs         = Ms(t.Output),
         ToolArgsMs       = Ms(t.ToolArgs),
         PermissionWaitMs = Ms(t.PermissionWait),
         ToolExecMs       = Ms(t.ToolExec),
      };

      static int? Ms(TimeSpan s) => s > TimeSpan.Zero ? (int)Math.Round(s.TotalMilliseconds) : null;
   }

   /// <summary>Rebuild the phase timings from a stored record. Null when it has none.</summary>
   public static Llm.PhaseTimings? TimingsFrom(TranscriptRecord r)
   {
      var t = r.Timing;
      if (t == null) return null;
      var p = new Llm.PhaseTimings
      {
         McpConnect     = Span(t.McpConnectMs),
         Prefill        = Span(t.PrefillMs),
         Thinking       = Span(t.ThinkingMs),
         Output         = Span(t.OutputMs),
         ToolArgs       = Span(t.ToolArgsMs),
         PermissionWait = Span(t.PermissionWaitMs),
         ToolExec       = Span(t.ToolExecMs),
      };
      return p.IsEmpty ? null : p;

      static TimeSpan Span(int? ms) => ms.HasValue ? TimeSpan.FromMilliseconds(ms.Value) : TimeSpan.Zero;
   }

   private static ReadsRecord? ToReadsRecord(Llm.TokenReads? reads)
   {
      if (!reads.HasValue) return null;
      var r = reads.Value;
      return new ReadsRecord
      {
         Prompt      = r.Prompt,
         ToolResults = r.ToolResults,
         Images      = r.Images > 0 ? r.Images : null,
         Estimated   = r.Estimated ? true : null,
      };
   }

   /// <summary>Rebuild the read accounting from a stored record. Null when it has none.</summary>
   public static Llm.TokenReads? ReadsFrom(TranscriptRecord r)
   {
      var x = r.Reads;
      if (x == null) return null;
      return new Llm.TokenReads
      {
         Prompt      = x.Prompt,
         ToolResults = x.ToolResults,
         Images      = x.Images ?? 0,
         Estimated   = x.Estimated == true,
      };
   }

   /// <summary>Persist one chat message. Maps <see cref="ChatMessage"/> verbatim.</summary>
   public void AppendMessage(ChatMessage msg)
   {
      var rec = new TranscriptRecord
      {
         Type             = "msg",
         Role             = RoleToString(msg.Role),
         Content          = msg.Content,
         ToolCallId       = msg.ToolCallId,
         ReasoningContent = msg.ReasoningContent,
         ReasoningTokens  = msg.ReasoningTokens,
         PromptTokens     = msg.PromptTokens,
         CompletionTokens = msg.CompletionTokens,
         CachedReadTokens = msg.CachedReadTokens,
         CacheWriteTokens = msg.CacheWriteTokens,
         // Persist IsError only on tool-result messages — false is the
         // default elsewhere and would clutter every assistant/user line.
         IsError          = (msg.Role == ChatRole.Tool && msg.IsError) ? true : (bool?)null,
         Images           = ToImageRecords(msg.Images),
         Timing           = ToTimingRecord(msg.Timings),
         Reads            = ToReadsRecord(msg.Reads),
      };
      if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
      {
         rec.ToolCalls = new List<ToolCallRecord>(msg.ToolCalls.Count);
         foreach (var c in msg.ToolCalls)
         {
            rec.ToolCalls.Add(new ToolCallRecord
            {
               Id            = c.Id,
               Name          = c.Name,
               ArgumentsJson = c.ArgumentsJson,
            });
         }
      }
      AppendRecord(rec);
   }

   /// <summary>Persist one tool execution result (matches a prior tool call by id).</summary>
   public void AppendToolResult(string toolCallId, string content)
   {
      AppendMessage(new ChatMessage
      {
         Role       = ChatRole.Tool,
         ToolCallId = toolCallId,
         Content    = content ?? "",
      });
   }

   private void AppendRecord(TranscriptRecord rec)
   {
      // Stamp every line with its write time (unless a caller already set one).
      rec.Ts ??= DateTime.UtcNow;
      try
      {
         var dir = Path.GetDirectoryName(_filePath);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

         var line = JsonSerializer.Serialize(rec, s_jsonOpts);
         File.AppendAllText(_filePath, line + "\n", Encoding.UTF8);
      }
      catch
      {
         // Swallow — a transient disk error must not crash an active turn.
         // The in-memory transcript remains intact; resumability is the
         // only thing degraded.
      }
   }

   // -- reader ---------------------------------------------------------------

   /// <summary>
   /// Parse all records from a session file. Lines that fail to parse are
   /// silently skipped — keeps a partially-corrupted file resumable.
   /// </summary>
   public static IReadOnlyList<TranscriptRecord> Read(string filePath)
   {
      var result = new List<TranscriptRecord>();
      if (!File.Exists(filePath)) return result;

      try
      {
         using var sr = new StreamReader(filePath, Encoding.UTF8);
         string? line;
         while ((line = sr.ReadLine()) != null)
         {
            if (line.Length == 0) continue;
            TranscriptRecord? rec;
            try { rec = JsonSerializer.Deserialize<TranscriptRecord>(line, s_jsonOpts); }
            catch { continue; }
            if (rec != null) result.Add(rec);
         }
      }
      catch
      {
         // File unreadable — return whatever we got.
      }
      return result;
   }

   // -- path helpers ---------------------------------------------------------

   /// <summary>
   /// Resolve the session directory for one project, e.g.
   /// <c>%APPDATA%\SlimCode\&lt;profile&gt;\sessions\native\&lt;slug&gt;</c>.
   /// </summary>
   public static string? SessionsDirectory(string projectPath)
   {
      var root = AppPaths.AppDataRootOrNull;
      if (root == null) return null;
      var slug = ProjectSlug(projectPath);
      if (slug.Length == 0) return null;
      return Path.Combine(root, "sessions", "native", slug);
   }

   /// <summary>Build the canonical session file path for a given id under a project.</summary>
   public static string? FileFor(string projectPath, string sessionId)
   {
      var dir = SessionsDirectory(projectPath);
      if (dir == null) return null;
      return Path.Combine(dir, sessionId + ".jsonl");
   }

   /// <summary>Meta 'kind' marker for subagent transcripts.</summary>
   public const string SUBAGENT_KIND = "subagent";

   /// <summary>
   /// Marker embedded in every subagent transcript's file name. Files whose
   /// name contains it are companions of a parent session, never sessions of
   /// their own — see <see cref="IsSubagentFile"/>.
   /// </summary>
   public const string SUB_FILE_MARK = ".sub.";

   /// <summary>
   /// Path of a subagent transcript, derived deterministically from the
   /// parent session id and the subagent tool-call id:
   /// <c>&lt;parentSessionId&gt;.sub.&lt;callId&gt;.jsonl</c> next to the
   /// parent's file. Determinism is the correlation mechanism — a resumed
   /// session finds a call's transcript without any extra record.
   /// </summary>
   public static string? SubFileFor(string projectPath, string parentSessionId, string toolCallId)
   {
      var dir = SessionsDirectory(projectPath);
      if (dir == null) return null;
      return Path.Combine(dir, parentSessionId + SUB_FILE_MARK + _SafeCallId(toolCallId) + ".jsonl");
   }

   /// <summary>True when the file name marks a subagent companion transcript.</summary>
   public static bool IsSubagentFile(string filePath) =>
      Path.GetFileName(filePath).Contains(SUB_FILE_MARK, StringComparison.OrdinalIgnoreCase);

   // Provider call ids are typically [A-Za-z0-9_-], but don't trust that:
   // anything else becomes '-', and length is capped to keep paths sane.
   private static string _SafeCallId(string id)
   {
      var sb = new StringBuilder(Math.Min(id.Length, 64));
      foreach (var ch in id)
      {
         bool keep = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                     (ch >= '0' && ch <= '9') || ch == '_' || ch == '-';
         sb.Append(keep ? ch : '-');
         if (sb.Length >= 64) break;
      }
      return sb.Length > 0 ? sb.ToString() : "call";
   }

   /// <summary>
   /// Slug for a project path, with the kind resolved from the live project
   /// list. Everything that keys storage off a bare path goes through here.
   /// </summary>
   public static string ProjectSlug(string projectPath) =>
      ProjectSlug(projectPath, Core.ProjectList.Current.GetKind(projectPath));

   /// <summary>
   /// Path → folder-name encoding: lowercase, every char outside
   /// <c>[a-z0-9-]</c> becomes a literal <c>-</c>. One-way; the
   /// host verifies each session's recorded ProjectPath after parsing.
   ///
   /// An MCP project gets a <c>-mcp</c> trailer, so the same folder used as
   /// both a coding and an MCP project keeps two separate sets of config,
   /// permissions and transcripts. (Adding one folder twice is not supported
   /// today — this is what makes it a list-level question rather than a
   /// storage-level one when it is.)
   /// </summary>
   public static string ProjectSlug(string projectPath, Core.ProjectKind kind)
   {
      string full;
      try { full = Path.GetFullPath(projectPath); }
      catch { full = projectPath; }
      full = full.TrimEnd('\\', '/');

      var sb = new StringBuilder(full.Length);
      foreach (var ch in full)
      {
         char c = char.ToLowerInvariant(ch);
         bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
         sb.Append(keep ? c : '-');
      }
      if (kind == Core.ProjectKind.Mcp) sb.Append(MCP_SLUG_SUFFIX);
      return sb.ToString();
   }

   /// <summary>Trailer distinguishing an MCP project's storage from a coding
   /// project's on the same folder.</summary>
   public const string MCP_SLUG_SUFFIX = "-mcp";

   /// <summary>Generate a filesystem-safe session id from the current UTC time.</summary>
   public static string NewSessionId() =>
      DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

   // -- fork lineage ---------------------------------------------------------

   /// <summary>Fork produced by reverting to a user message.</summary>
   public const string FORK_REVERT = "revert";

   /// <summary>Fork produced by re-running an assistant turn unchanged.</summary>
   public const string FORK_REGENERATE = "regenerate";

   /// <summary>
   /// A session id that is not yet taken under <paramref name="projectPath"/>.
   ///
   /// <see cref="NewSessionId"/> is millisecond-resolution, which was ample
   /// while sessions were only minted by hand. Branching mints one per click
   /// on an assistant turn, so a collision is no longer purely theoretical —
   /// and colliding would append a second session's records onto the first
   /// file. Suffixes the id rather than spinning on the clock.
   /// </summary>
   public static string MintSessionId(string projectPath)
   {
      var baseId = NewSessionId();
      var dir    = SessionsDirectory(projectPath);
      if (dir == null) return baseId;

      var id = baseId;
      for (int n = 2; n < 1000; n++)
      {
         try
         {
            if (!File.Exists(Path.Combine(dir, id + ".jsonl")))
               return id;
         }
         catch { return id; }
         id = baseId + "-" + n;
      }
      return id;
   }

   public static ChatRole ParseRole(string? s) => s switch
   {
      "system"    => ChatRole.System,
      "user"      => ChatRole.User,
      "assistant" => ChatRole.Assistant,
      "tool"      => ChatRole.Tool,
      _           => ChatRole.User,
   };

   private static string RoleToString(ChatRole r) => r switch
   {
      ChatRole.System    => "system",
      ChatRole.User      => "user",
      ChatRole.Assistant => "assistant",
      ChatRole.Tool      => "tool",
      _ => "user",
   };

   // -- fields ---------------------------------------------------------------

   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      PropertyNameCaseInsensitive = true,
   };

   private readonly string _filePath;
}
