// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Non-recursive directory listing. Skips common noise folders (.git, bin,
/// obj, node_modules) by default so the model isn't drowned in build output.
/// </summary>
public sealed class ListDirTool : ITool
{
   public string Name => "list_dir";

   public string Description =>
      "List immediate entries of a directory in the project. Returns one " +
      "entry per line, suffixed with '/' for directories. Hides common " +
      "noise folders (.git, bin, obj, node_modules) unless 'show_hidden' is true.";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args) => ToolPreview.DefaultPreview(args);

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? rel = args.TryGetProperty("path", out var p) ? p.GetString() : null;
      bool showHidden = args.TryGetProperty("show_hidden", out var h) && h.ValueKind == JsonValueKind.True;

      var res = ctx.Resolve(rel, PathAccess.Read);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + rel));
      var abs = res.FullPath;
      if (!Directory.Exists(abs))
         return Task.FromResult(ToolResult.Error("directory not found: " + (rel ?? ".")));

      try
      {
         var dirs  = new List<string>();
         var files = new List<string>();
         foreach (var entry in ctx.ListVisible(abs))
         {
            if (!showHidden && IsNoise(entry.Name))
               continue;
            if (entry.IsDirectory)
               dirs.Add(entry.Name);
            else
               files.Add(entry.Name);
         }
         dirs.Sort(StringComparer.OrdinalIgnoreCase);
         files.Sort(StringComparer.OrdinalIgnoreCase);

         if (dirs.Count == 0 && files.Count == 0)
            return Task.FromResult(ToolResult.Ok("(empty directory)"));

         var sb = new StringBuilder();
         sb.AppendLine(ctx.ToRelative(abs) + (ctx.ToRelative(abs).EndsWith('/') ? "" : "/") + ":");
         foreach (var d in dirs)
            sb.Append("  ").Append(d).Append('/').Append('\n');
         foreach (var f in files)
            sb.Append("  ").Append(f).Append('\n');

         int total = dirs.Count + files.Count;
         if (total >= MAX_ENTRIES)
            sb.AppendLine("… (listing capped at " + MAX_ENTRIES + " entries)");

         return Task.FromResult(ToolResult.Ok(sb.ToString()));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   private static bool IsNoise(string name)
   {
      if (name.Length == 0) return false;
      if (name[0] == '.' && name != ".") return true;
      return s_noise.Contains(name);
   }

   // -- constants ------------------------------------------------------------

   private const int MAX_ENTRIES = 1000;

   private static readonly HashSet<string> s_noise = new(StringComparer.OrdinalIgnoreCase)
   {
      "bin", "obj", "node_modules", "packages", "__pycache__", "dist", "build",
   };

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":        { "type": "string", "description": "Project-relative directory path. Defaults to project root." },
         "show_hidden": { "type": "boolean", "description": "Include dotfiles and noise folders (default false)." }
      }
   }
   """;
}
