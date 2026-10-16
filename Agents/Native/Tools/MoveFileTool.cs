// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Move or rename a file or directory inside the project. Destination
/// parent directories are created on demand. Overwriting an existing
/// destination file requires an explicit <c>overwrite: true</c> so the
/// model must state clobbering intent; directories are never overwritten.
///
/// Both paths are sandboxed via <see cref="ToolContext.ResolvePath"/> —
/// anything resolving outside the project root is rejected with an error
/// the model can recover from.
/// </summary>
public sealed class MoveFileTool : ITool
{
   public string Name => "move_file";

   public string Description =>
      "Move or rename a file or directory within the project. Destination parent " +
      "directories are created if missing. Fails if the destination exists unless " +
      "'overwrite' is true (files only — directories are never overwritten).";

   public ToolCategory Category => ToolCategory.Write;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      string source = args.TryGetProperty("source",      out var s) ? s.GetString() ?? "" : "";
      string dest   = args.TryGetProperty("destination", out var d) ? d.GetString() ?? "" : "";
      bool over     = args.TryGetProperty("overwrite",   out var o) && o.ValueKind == JsonValueKind.True;
      return source + " -> " + dest + (over ? " (overwrite)" : "");
   }

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? srcRel = args.TryGetProperty("source",      out var s) ? s.GetString() : null;
      string? dstRel = args.TryGetProperty("destination", out var d) ? d.GetString() : null;
      if (string.IsNullOrWhiteSpace(srcRel))
         return Task.FromResult(ToolResult.Error("'source' is required"));
      if (string.IsNullOrWhiteSpace(dstRel))
         return Task.FromResult(ToolResult.Error("'destination' is required"));

      // Both ends need write access: the source is removed from its folder.
      var srcRes = ctx.Resolve(srcRel, PathAccess.Write);
      if (!srcRes.IsAllowed)
         return Task.FromResult(ToolResult.Error("source " + srcRes.Reason + ": " + srcRel));
      var dstRes = ctx.Resolve(dstRel, PathAccess.Write);
      if (!dstRes.IsAllowed)
         return Task.FromResult(ToolResult.Error("destination " + dstRes.Reason + ": " + dstRel));
      var src = srcRes.FullPath;
      var dst = dstRes.FullPath;

      bool overwrite = args.TryGetProperty("overwrite", out var o) && o.ValueKind == JsonValueKind.True;

      bool srcIsFile = File.Exists(src);
      bool srcIsDir  = Directory.Exists(src);
      if (!srcIsFile && !srcIsDir)
         return Task.FromResult(ToolResult.Error("source not found: " + srcRel));

      if (string.Equals(src, dst, OperatingSystem.IsWindows()
                                  ? StringComparison.OrdinalIgnoreCase
                                  : StringComparison.Ordinal))
         return Task.FromResult(ToolResult.Error("source and destination are the same path"));

      try
      {
         if (Directory.Exists(dst))
            return Task.FromResult(ToolResult.Error(
               "destination is an existing directory: " + ctx.ToRelative(dst)));

         if (File.Exists(dst) && !overwrite)
            return Task.FromResult(ToolResult.Error(
               "destination exists: " + ctx.ToRelative(dst) + " — pass overwrite:true to replace it"));

         var dir = Path.GetDirectoryName(dst);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

         if (srcIsDir)
         {
            if (File.Exists(dst))
               return Task.FromResult(ToolResult.Error(
                  "cannot move directory onto existing file: " + ctx.ToRelative(dst)));
            Directory.Move(src, dst);
            return Task.FromResult(ToolResult.Ok(
               "moved directory " + ctx.ToRelative(src) + " -> " + ctx.ToRelative(dst)));
         }

         File.Move(src, dst, overwrite);
         return Task.FromResult(ToolResult.Ok(
            "moved " + ctx.ToRelative(src) + " -> " + ctx.ToRelative(dst)));
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
         "source":      { "type": "string",  "description": "Project-relative path of the file or directory to move." },
         "destination": { "type": "string",  "description": "Project-relative target path (the new name/location itself, not a parent directory)." },
         "overwrite":   { "type": "boolean", "description": "Replace an existing destination file. Default false. Ignored for directories — they are never overwritten." }
      },
      "required": ["source", "destination"]
   }
   """;
}
