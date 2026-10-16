// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Recursive glob across the project. Supports <c>*</c>, <c>?</c>, and
/// <c>**</c> (matches across path segments). Results sorted by mtime
/// descending so the most-recently-touched matches surface first —
/// matches what the model expects from other agent toolchains.
/// </summary>
public sealed class GlobTool : ITool
{
   public string Name => "glob";

   public string Description =>
      "Find files by glob pattern (e.g. '**/*.cs', 'Agents/**/*.axaml'). " +
      "Returns project-relative paths sorted by modification time (newest first).";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args) => ToolPreview.DefaultPreview(args);

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
      if (string.IsNullOrWhiteSpace(pattern))
         return Task.FromResult(ToolResult.Error("'pattern' is required"));

      string? relRoot = args.TryGetProperty("path", out var pp) ? pp.GetString() : null;
      var res = ctx.Resolve(relRoot, PathAccess.Read);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + relRoot));
      var absRoot = res.FullPath;
      if (!Directory.Exists(absRoot))
         return Task.FromResult(ToolResult.Error("directory not found: " + (relRoot ?? ".")));

      try
      {
         var regex = GlobToRegex(pattern!);
         var hits  = new List<(string Path, DateTime Mtime)>();

         foreach (var file in ctx.EnumerateFilesRecursive(absRoot, IsNoiseDir))
         {
            ctx.Cancel.ThrowIfCancellationRequested();
            var rel = ctx.ToRelative(file);
            if (!regex.IsMatch(rel))
               continue;
            try
            {
               hits.Add((rel, File.GetLastWriteTimeUtc(file)));
            }
            catch { }
            if (hits.Count >= MAX_HITS)
               break;
         }
         hits.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));

         if (hits.Count == 0)
            return Task.FromResult(ToolResult.Ok("(no matches)"));

         var sb = new StringBuilder();
         foreach (var h in hits)
            sb.Append(h.Path).Append('\n');
         if (hits.Count >= MAX_HITS)
            sb.AppendLine("… (capped at " + MAX_HITS + " matches)");

         return Task.FromResult(ToolResult.Ok(sb.ToString()));
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

   // -- enumeration ----------------------------------------------------------

   // Skipped during the walk so we avoid entering millions of
   // node_modules / .git entries.
   private static bool IsNoiseDir(string name)
   {
      if (name.Length == 0) return false;
      if (name[0] == '.') return true;
      return name.Equals("bin", StringComparison.OrdinalIgnoreCase)
          || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
          || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
          || name.Equals("packages", StringComparison.OrdinalIgnoreCase)
          || name.Equals("dist", StringComparison.OrdinalIgnoreCase)
          || name.Equals("build", StringComparison.OrdinalIgnoreCase)
          || name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase);
   }

   // -- glob → regex ---------------------------------------------------------

   // Translates a glob with **/*/? into an anchored regex over forward-slash paths.
   // '**' matches zero or more path segments; '*' matches anything except '/';
   // '?' matches a single non-'/' char.
   private static Regex GlobToRegex(string glob)
   {
      var normalized = glob.Replace('\\', '/');
      var sb = new StringBuilder();
      sb.Append('^');
      for (int i = 0; i < normalized.Length; i++)
      {
         char c = normalized[i];
         if (c == '*')
         {
            if (i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
               // '**' — match across slashes. Swallow an optional trailing '/'.
               sb.Append(".*");
               i++;
               if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                  i++;
            }
            else
            {
               sb.Append("[^/]*");
            }
         }
         else if (c == '?')
         {
            sb.Append("[^/]");
         }
         else if ("+()^$.{}[]|\\".IndexOf(c) >= 0)
         {
            sb.Append('\\').Append(c);
         }
         else
         {
            sb.Append(c);
         }
      }
      sb.Append('$');
      return new Regex(sb.ToString(),
                       RegexOptions.Compiled |
                       (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
   }

   // -- constants ------------------------------------------------------------

   private const int MAX_HITS = 500;

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "pattern": { "type": "string", "description": "Glob pattern. Supports *, ?, and ** (cross-segment)." },
         "path":    { "type": "string", "description": "Subdirectory to search under, project-relative. Defaults to project root." }
      },
      "required": ["pattern"]
   }
   """;
}
