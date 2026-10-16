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
/// Read a text file (or a line range from one). Lines are 1-indexed and
/// prefixed with their line number in the output, matching the convention
/// most coding agents use so the model can later reference exact lines.
/// </summary>
public sealed class ReadFileTool : ITool
{
   public string Name => "read_file";

   public string Description =>
      "Read a UTF-8 text file from the project. Returns the contents with " +
      "1-indexed line-number prefixes. Use offset/limit for large files.";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args) => ToolPreview.DefaultPreview(args);

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? rel = args.TryGetProperty("path", out var p) ? p.GetString() : null;
      if (string.IsNullOrWhiteSpace(rel))
         return Task.FromResult(ToolResult.Error("'path' is required"));

      var res = ctx.Resolve(rel, PathAccess.Read);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + rel));
      var abs = res.FullPath;
      if (!File.Exists(abs))
         return Task.FromResult(ToolResult.Error("file not found: " + rel));

      int offset = args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number
                   ? Math.Max(1, o.GetInt32()) : 1;
      int limit  = args.TryGetProperty("limit",  out var l) && l.ValueKind == JsonValueKind.Number
                   ? Math.Max(1, l.GetInt32()) : DEFAULT_LIMIT;

      try
      {
         var sb = new StringBuilder();
         int lineNo = 0;
         int emitted = 0;
         int budget  = MAX_BYTES;

         using var sr = new StreamReader(abs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
         string? line;
         while ((line = sr.ReadLine()) != null)
         {
            lineNo++;
            if (lineNo < offset)
               continue;
            if (emitted >= limit)
               break;

            // 6-wide right-aligned line number + tab matches existing Read tool style.
            var prefix = lineNo.ToString().PadLeft(6) + "\t";
            int cost = prefix.Length + line.Length + 1;
            if (cost > budget)
            {
               sb.AppendLine("… (output truncated to " + MAX_BYTES + " bytes)");
               break;
            }
            sb.Append(prefix).Append(line).Append('\n');
            budget -= cost;
            emitted++;
         }

         if (emitted == 0)
            return Task.FromResult(ToolResult.Ok("(empty range)"));

         return Task.FromResult(ToolResult.Ok(sb.ToString()));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   // -- constants ------------------------------------------------------------

   private const int DEFAULT_LIMIT = 2000;
   private const int MAX_BYTES     = 100_000;

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":   { "type": "string", "description": "Project-relative path to the file." },
         "offset": { "type": "integer", "minimum": 1, "description": "1-indexed first line to return." },
         "limit":  { "type": "integer", "minimum": 1, "description": "Max lines to return (default 2000)." }
      },
      "required": ["path"]
   }
   """;
}
