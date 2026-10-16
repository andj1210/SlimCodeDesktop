// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Delete a file or directory inside the project. Deleting a non-empty
/// directory requires an explicit <c>recursive: true</c>; without it the
/// call fails and reports the entry count, so recursive deletion is always
/// a deliberate second decision by the model — and visibly flagged in the
/// permission prompt via <see cref="ArgsPreview"/>.
///
/// The delete is a hard delete (no recycle bin). The path is sandboxed via
/// <see cref="ToolContext.ResolvePath"/>; the project root itself cannot
/// be deleted.
/// </summary>
public sealed class DeleteFileTool : ITool
{
   public string Name => "delete_file";

   public string Description =>
      "Permanently delete a file or directory in the project. Deleting a " +
      "non-empty directory requires 'recursive' set to true.";

   public ToolCategory Category => ToolCategory.Write;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      string path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
      bool rec    = args.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True;
      return "path=" + path + (rec ? ", RECURSIVE" : "");
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

      var cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
      if (abs.Equals(ctx.ProjectRoot, cmp))
         return Task.FromResult(ToolResult.Error("refusing to delete the project root"));

      bool recursive = args.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True;

      try
      {
         if (File.Exists(abs))
         {
            long size = new FileInfo(abs).Length;
            File.Delete(abs);
            return Task.FromResult(ToolResult.Ok(
               "deleted " + ctx.ToRelative(abs) + " (" + ToolPreview.FormatBytes(size) + ")"));
         }

         if (Directory.Exists(abs))
         {
            int entries = _CountEntries(abs);
            if (entries > 0 && !recursive)
               return Task.FromResult(ToolResult.Error(
                  "directory not empty: " + ctx.ToRelative(abs) + " (" + entries +
                  " entries) — pass recursive:true to delete it and its contents"));

            Directory.Delete(abs, recursive);
            return Task.FromResult(ToolResult.Ok(entries > 0
               ? "deleted directory " + ctx.ToRelative(abs) + " (" + entries + " entries)"
               : "deleted empty directory " + ctx.ToRelative(abs)));
         }

         return Task.FromResult(ToolResult.Error("path not found: " + rel));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   // -- helpers ----------------------------------------------------------------

   // Total files + subdirectories beneath the directory, so the error and the
   // success message both state the blast radius.
   private static int _CountEntries(string dir)
   {
      int n = 0;
      try
      {
         foreach (var _ in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            n++;
      }
      catch { }
      return n;
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":      { "type": "string",  "description": "Project-relative path of the file or directory to delete." },
         "recursive": { "type": "boolean", "description": "Required as true to delete a non-empty directory together with its contents. Default false." }
      },
      "required": ["path"]
   }
   """;
}
