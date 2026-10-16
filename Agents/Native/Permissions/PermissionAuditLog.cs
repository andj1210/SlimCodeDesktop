// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>One parsed audit record (mirrors the on-disk JSONL shape).</summary>
public sealed class AuditEntry
{
   public DateTime           Timestamp { get; init; }
   public string?            SessionId { get; init; }
   public string             Tool { get; init; } = "";
   public PermissionDecision Decision { get; init; }
   public PermissionScope    Scope { get; init; }
   public string             Reason { get; init; } = "";
   public string             ArgsPreview { get; init; } = "";
}

/// <summary>
/// Append-only JSONL writer for gate decisions. One file per project at
/// <c>&lt;projectMetadataDir&gt;\permissions.log.jsonl</c>; one line per
/// decision (whether the answer came from cache, project policy, global
/// policy, or a user prompt).
///
/// Writes are best-effort — a transient disk error must not crash an
/// active turn; the in-memory decision still propagates.
/// </summary>
public sealed class PermissionAuditLog
{
   public PermissionAuditLog(string filePath)
   {
      _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
   }

   public string FilePath => _filePath;

   public void Append(
      string         tool,
      PermissionGrant grant,
      string         argsPreview,
      string?        sessionId)
   {
      var rec = new AuditRecord
      {
         Timestamp   = grant.TimestampUtc,
         SessionId   = sessionId,
         Tool        = tool,
         Decision    = grant.Decision,
         Scope       = grant.Scope,
         Reason      = grant.Reason,
         ArgsPreview = argsPreview,
      };
      try
      {
         var dir = Path.GetDirectoryName(_filePath);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

         var line = JsonSerializer.Serialize(rec, s_jsonOpts);
         File.AppendAllText(_filePath, line + "\n", Encoding.UTF8);
      }
      catch
      {
         // Best-effort. Could surface via an event later if forensic
         // integrity becomes a hard requirement.
      }
   }

   // -- reader ---------------------------------------------------------------

   /// <summary>
   /// Parse all records in the log. Missing file → empty list. Lines that
   /// fail to parse are silently skipped (one bad line shouldn't lock the
   /// viewer out of the rest).
   /// </summary>
   public static IReadOnlyList<AuditEntry> Read(string filePath)
   {
      var result = new List<AuditEntry>();
      if (!File.Exists(filePath)) return result;

      try
      {
         using var sr = new StreamReader(filePath, Encoding.UTF8);
         string? line;
         while ((line = sr.ReadLine()) != null)
         {
            if (line.Length == 0) continue;
            AuditRecord? rec;
            try { rec = JsonSerializer.Deserialize<AuditRecord>(line, s_jsonOpts); }
            catch { continue; }
            if (rec == null) continue;
            result.Add(new AuditEntry
            {
               Timestamp   = rec.Timestamp,
               SessionId   = rec.SessionId,
               Tool        = rec.Tool,
               Decision    = rec.Decision,
               Scope       = rec.Scope,
               Reason      = rec.Reason,
               ArgsPreview = rec.ArgsPreview,
            });
         }
      }
      catch { }
      return result;
   }

   /// <summary>
   /// Rewrite the log keeping only entries for which <paramref name="keep"/>
   /// returns true. Returns the number of entries removed, or -1 on I/O error.
   /// Missing file → 0. Failed line parses are dropped (same policy as
   /// <see cref="Read"/>) and counted as removed.
   /// </summary>
   public static int Prune(string filePath, System.Func<AuditEntry, bool> keep)
   {
      if (!File.Exists(filePath)) return 0;
      try
      {
         var entries = Read(filePath);
         var kept    = new List<AuditEntry>(entries.Count);
         foreach (var e in entries)
            if (keep(e)) kept.Add(e);

         var removed = entries.Count - kept.Count;
         if (removed <= 0) return 0;

         // Write to a sibling temp file then atomic-replace, so a crash mid-
         // rewrite can't leave the log half-truncated.
         var tmp = filePath + ".tmp";
         using (var sw = new StreamWriter(tmp, append: false, Encoding.UTF8))
         {
            foreach (var e in kept)
            {
               var rec = new AuditRecord
               {
                  Timestamp   = e.Timestamp,
                  SessionId   = e.SessionId,
                  Tool        = e.Tool,
                  Decision    = e.Decision,
                  Scope       = e.Scope,
                  Reason      = e.Reason,
                  ArgsPreview = e.ArgsPreview,
               };
               sw.WriteLine(JsonSerializer.Serialize(rec, s_jsonOpts));
            }
         }
         File.Move(tmp, filePath, overwrite: true);
         return removed;
      }
      catch { return -1; }
   }

   /// <summary>Truncate the log to zero bytes. Returns true if the file was reachable.</summary>
   public static bool Clear(string filePath)
   {
      try
      {
         if (File.Exists(filePath)) File.WriteAllText(filePath, "");
         return true;
      }
      catch { return false; }
   }

   // -- record shape ---------------------------------------------------------

   private sealed class AuditRecord
   {
      [JsonPropertyName("timestamp")]    public DateTime           Timestamp { get; set; }
      [JsonPropertyName("sessionId")]    public string?            SessionId { get; set; }
      [JsonPropertyName("tool")]         public string             Tool { get; set; } = "";
      [JsonPropertyName("decision")]     public PermissionDecision Decision { get; set; }
      [JsonPropertyName("scope")]        public PermissionScope    Scope { get; set; }
      [JsonPropertyName("reason")]       public string             Reason { get; set; } = "";
      [JsonPropertyName("argsPreview")]  public string             ArgsPreview { get; set; } = "";
   }

   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      Converters             = { new JsonStringEnumConverter() },
   };

   private readonly string _filePath;
}
