// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Targeted, exact-string replace inside an existing UTF-8 file. Safer than
/// <see cref="WriteFileTool"/> for surgical changes — the model has to quote
/// the surrounding text exactly, which forces it to look at the file rather
/// than hallucinate.
///
/// Uniqueness rule: unless <c>replace_all</c> is true, the tool errors when
/// <c>old_string</c> occurs more than once. This matches the convention used
/// by Claude Code / Cursor and prevents accidentally rewriting the wrong
/// occurrence.
/// </summary>
public sealed class EditFileTool : ITool
{
   public string Name => "edit_file";

   public string Description =>
      "Replace an exact substring in a project file. By default the substring " +
      "must occur exactly once; pass replace_all=true to replace every match.";

   public ToolCategory Category => ToolCategory.Write;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      string path = args.TryGetProperty("path",       out var p) ? p.GetString() ?? "" : "";
      string oldS = args.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : "";
      string newS = args.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "";
      bool   all  = args.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True;
      return "path=" + path
           + ", old=(" + oldS.Length + " chars)"
           + ", new=(" + newS.Length + " chars)"
           + (all ? ", replace_all=true" : "");
   }

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? rel = args.TryGetProperty("path", out var p) ? p.GetString() : null;
      if (string.IsNullOrWhiteSpace(rel))
         return Task.FromResult(ToolResult.Error("'path' is required"));

      string? oldS = args.TryGetProperty("old_string", out var o) ? o.GetString() : null;
      if (string.IsNullOrEmpty(oldS))
         return Task.FromResult(ToolResult.Error("'old_string' is required and must be non-empty"));

      string newS = args.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "";
      bool replaceAll = args.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True;

      var res = ctx.Resolve(rel, PathAccess.Write);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + rel));
      var abs = res.FullPath;
      if (!File.Exists(abs))
         return Task.FromResult(ToolResult.Error("file not found: " + rel));

      try
      {
         // read_file hands the model '\n'-only text, so match in that space and
         // restore the file's CRLF / BOM on write.
         var  bytes  = File.ReadAllBytes(abs);
         bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
         int  skip   = hasBom ? 3 : 0;
         var  raw    = new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);
         bool isCrlf = raw.Contains("\r\n");

         var original = raw.Replace("\r\n", "\n");
         oldS = oldS!.Replace("\r\n", "\n");
         newS = newS.Replace("\r\n", "\n");

         int matches  = CountOccurrences(original, oldS);

         if (matches == 0)
            return Task.FromResult(ToolResult.Error(
               "old_string not found in " + rel + ". Double-check whitespace and surrounding context."));

         if (matches > 1 && !replaceAll)
            return Task.FromResult(ToolResult.Error(
               "old_string occurs " + matches + " times in " + rel +
               ". Include more context to make it unique, or set replace_all=true."));

         string updated = replaceAll
            ? original.Replace(oldS!, newS)
            : ReplaceFirst(original, oldS!, newS);

         if (updated == original)
            return Task.FromResult(ToolResult.Error("no change after replace (old_string == new_string?)"));

         if (isCrlf)
            updated = updated.Replace("\n", "\r\n");

         // Atomic write (same pattern as WriteFileTool).
         var tempPath = abs + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 8);
         File.WriteAllText(tempPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom));
         File.Move(tempPath, abs, overwrite: true);

         return Task.FromResult(ToolResult.Ok(
            "edited " + ctx.ToRelative(abs) + ": " + matches + " occurrence(s) replaced"));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   // -- helpers --------------------------------------------------------------

   private static int CountOccurrences(string haystack, string needle)
   {
      if (needle.Length == 0) return 0;
      int count = 0, i = 0;
      while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
      {
         count++;
         i += needle.Length;
      }
      return count;
   }

   private static string ReplaceFirst(string s, string oldS, string newS)
   {
      int i = s.IndexOf(oldS, StringComparison.Ordinal);
      if (i < 0) return s;
      return s.Substring(0, i) + newS + s.Substring(i + oldS.Length);
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":        { "type": "string", "description": "Project-relative path to an existing file." },
         "old_string":  { "type": "string", "description": "Exact substring to find. Include enough context that it is unique unless replace_all=true." },
         "new_string":  { "type": "string", "description": "Replacement text (may be empty to delete the match). To delete whole lines, include their trailing newline in old_string." },
         "replace_all": { "type": "boolean", "description": "Replace every occurrence (default false — exactly-one match required)." }
      },
      "required": ["path", "old_string", "new_string"]
   }
   """;
}
