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
/// Create or overwrite a UTF-8 text file. Writes atomically (temp file in
/// the same directory + <see cref="File.Move(string,string,bool)"/>) so a
/// crash mid-write never leaves the target half-written. Parent directories
/// are created on demand.
///
/// Path is sandboxed via <see cref="ToolContext.ResolvePath"/> — any path
/// that resolves outside the project root is rejected with a clear error
/// the model can recover from.
/// </summary>
public sealed class WriteFileTool : ITool
{
   public string Name => "write_file";

   public string Description =>
      "Create or overwrite a UTF-8 text file in the project. Parent directories " +
      "are created if missing. Writes atomically (rename-after-write).";

   public ToolCategory Category => ToolCategory.Write;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      string path    = args.TryGetProperty("path",    out var p) ? p.GetString() ?? "" : "";
      string content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
      int bytes = Encoding.UTF8.GetByteCount(content);
      return "path=" + path + ", content=(" + ToolPreview.FormatBytes(bytes) + ")";
   }

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? rel = args.TryGetProperty("path", out var p) ? p.GetString() : null;
      if (string.IsNullOrWhiteSpace(rel))
         return Task.FromResult(ToolResult.Error("'path' is required"));

      var res = ctx.Resolve(rel, PathAccess.Write);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + rel));
      var abs = res.FullPath;

      string content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

      try
      {
         var dir = Path.GetDirectoryName(abs);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

         // Write to a sibling temp file in the same directory so the final
         // Move is a same-volume rename (atomic on NTFS).
         var tempPath = abs + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 8);
         File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
         File.Move(tempPath, abs, overwrite: true);

         var bytes = Encoding.UTF8.GetByteCount(content);
         return Task.FromResult(ToolResult.Ok(
            "wrote " + ToolPreview.FormatBytes(bytes) + " to " + ctx.ToRelative(abs)));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":    { "type": "string", "description": "Project-relative path to the file." },
         "content": { "type": "string", "description": "Full UTF-8 contents of the file. Existing file is overwritten." }
      },
      "required": ["path", "content"]
   }
   """;
}
