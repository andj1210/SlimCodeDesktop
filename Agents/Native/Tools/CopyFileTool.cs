// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Copy a file or directory inside the project. Destination parent
/// directories are created on demand. Overwriting an existing destination
/// file requires an explicit <c>overwrite: true</c>; directories are never
/// overwritten or merged.
///
/// The source only needs read access, the destination write access. A
/// directory copy walks the source via <see cref="ToolContext.ListVisible"/>,
/// so it sees exactly what the read tools see: no-access folders and linked
/// directories are left behind (and counted in the result), otherwise a copy
/// would lift protected content into a readable folder. The whole plan is
/// built before the first byte is written, and capped at
/// <see cref="MAX_ENTRIES"/> entries.
/// </summary>
public sealed class CopyFileTool : ITool
{
   /// <summary>Upper bound on files + directories a single directory copy may create.</summary>
   public const int MAX_ENTRIES = 10000;

   public string Name => "copy_file";

   public string Description =>
      "Copy a file or directory within the project. Destination parent directories " +
      "are created if missing. Fails if the destination exists unless 'overwrite' is " +
      "true (files only — directories are never overwritten). Directory copies skip " +
      "no-access folders and linked directories, and are limited to " + MAX_ENTRIES + " entries.";

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

      // The source stays where it is, so reading it is enough.
      var srcRes = ctx.Resolve(srcRel, PathAccess.Read);
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

      var cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
      if (string.Equals(src, dst, cmp))
         return Task.FromResult(ToolResult.Error("source and destination are the same path"));

      try
      {
         if (Directory.Exists(dst))
            return Task.FromResult(ToolResult.Error(
               "destination is an existing directory: " + ctx.ToRelative(dst)));

         if (srcIsFile)
            return Task.FromResult(_CopyFile(src, dst, overwrite, ctx));

         if (dst.StartsWith(Path.TrimEndingDirectorySeparator(src) + Path.DirectorySeparatorChar, cmp))
            return Task.FromResult(ToolResult.Error(
               "cannot copy a directory into itself: " + ctx.ToRelative(dst)));
         if (File.Exists(dst))
            return Task.FromResult(ToolResult.Error(
               "cannot copy directory onto existing file: " + ctx.ToRelative(dst)));

         return Task.FromResult(_CopyDirectory(src, dst, ctx));
      }
      catch (OperationCanceledException)
      {
         return Task.FromResult(ToolResult.Error("cancelled"));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   // -- helpers ----------------------------------------------------------------

   private static ToolResult _CopyFile(string src, string dst, bool overwrite, ToolContext ctx)
   {
      if (File.Exists(dst) && !overwrite)
         return ToolResult.Error(
            "destination exists: " + ctx.ToRelative(dst) + " — pass overwrite:true to replace it");

      var dir = Path.GetDirectoryName(dst);
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

      File.Copy(src, dst, overwrite);
      long size = new FileInfo(dst).Length;
      return ToolResult.Ok(
         "copied " + ctx.ToRelative(src) + " -> " + ctx.ToRelative(dst) +
         " (" + ToolPreview.FormatBytes(size) + ")");
   }

   private static ToolResult _CopyDirectory(string src, string dst, ToolContext ctx)
   {
      // Plan first: the cap must fail before anything lands on disk.
      var dirs    = new List<string>();      // source-relative, parents before children
      var files   = new List<string>();
      int skipped = 0;

      var stack = new Stack<string>();
      stack.Push("");
      while (stack.Count > 0)
      {
         ctx.Cancel.ThrowIfCancellationRequested();
         var rel     = stack.Pop();
         var absDir  = rel.Length == 0 ? src : Path.Combine(src, rel);
         var visible = ctx.ListVisible(absDir);
         skipped += Math.Max(0, _RawEntryCount(absDir) - visible.Count);

         foreach (var entry in visible)
         {
            var entryRel = rel.Length == 0 ? entry.Name : Path.Combine(rel, entry.Name);
            if (entry.IsDirectory)
            {
               // Linked directories are not entered (see EnumerateFilesRecursive).
               if (entry.IsLink)
               {
                  skipped++;
                  continue;
               }
               dirs.Add(entryRel);
               stack.Push(entryRel);
            }
            else
            {
               files.Add(entryRel);
            }

            if (dirs.Count + files.Count > MAX_ENTRIES)
               return ToolResult.Error(
                  "directory too large to copy: more than " + MAX_ENTRIES +
                  " entries below " + ctx.ToRelative(src) + " — copy subfolders separately");
         }
      }

      int  copied = 0;
      long bytes  = 0;
      try
      {
         Directory.CreateDirectory(dst);
         foreach (var rel in dirs)
            Directory.CreateDirectory(Path.Combine(dst, rel));

         foreach (var rel in files)
         {
            ctx.Cancel.ThrowIfCancellationRequested();
            var from = Path.Combine(src, rel);
            File.Copy(from, Path.Combine(dst, rel), false);
            bytes += new FileInfo(from).Length;
            copied++;
         }
      }
      catch (OperationCanceledException)
      {
         return ToolResult.Error(
            "cancelled after copying " + copied + " of " + files.Count + " files into " +
            ctx.ToRelative(dst) + " — partial copy left in place");
      }
      catch (Exception ex)
      {
         return ToolResult.Error(
            "copy failed after " + copied + " of " + files.Count + " files into " +
            ctx.ToRelative(dst) + " — partial copy left in place: " + ex.Message);
      }

      return ToolResult.Ok(
         "copied directory " + ctx.ToRelative(src) + " -> " + ctx.ToRelative(dst) +
         " (" + files.Count + " files, " + dirs.Count + " subdirectories, " +
         ToolPreview.FormatBytes(bytes) + ")" +
         (skipped > 0 ? "; skipped " + skipped + " no-access or linked entries" : ""));
   }

   // Entries the OS reports, so the result can state what the gate held back.
   private static int _RawEntryCount(string dir)
   {
      try
      {
         return new DirectoryInfo(dir).GetFileSystemInfos().Length;
      }
      catch
      {
         return 0;
      }
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "source":      { "type": "string",  "description": "Project-relative path of the file or directory to copy." },
         "destination": { "type": "string",  "description": "Project-relative target path (the copy's own name/location, not a parent directory)." },
         "overwrite":   { "type": "boolean", "description": "Replace an existing destination file. Default false. Ignored for directories — they are never overwritten." }
      },
      "required": ["source", "destination"]
   }
   """;
}
