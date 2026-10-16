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
/// Recursive content search. Pure managed implementation — no ripgrep
/// dependency. Sufficient for exploration mode on typical project sizes;
/// can be swapped for an rg shell-out later if needed.
///
/// Output format: <c>relative/path:lineNo: matched line</c> per hit.
/// </summary>
public sealed class GrepTool : ITool
{
   public string Name => "grep";

   public string Description =>
      "Search file contents with a .NET regex. Returns matches as " +
      "'path:line: content'. Optionally filter files by extension.";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args) => ToolPreview.DefaultPreview(args);

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? pattern = args.TryGetProperty("pattern", out var pp) ? pp.GetString() : null;
      if (string.IsNullOrWhiteSpace(pattern))
         return Task.FromResult(ToolResult.Error("'pattern' is required"));

      string? relRoot = args.TryGetProperty("path", out var p) ? p.GetString() : null;
      var res = ctx.Resolve(relRoot, PathAccess.Read);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + relRoot));
      var absRoot = res.FullPath;

      bool rootIsFile = File.Exists(absRoot);
      bool rootIsDir  = Directory.Exists(absRoot);
      if (!rootIsFile && !rootIsDir)
         return Task.FromResult(ToolResult.Error(
            "not a file or directory: " + (relRoot ?? ".") +
            " (omit 'path' to search the whole project)"));

      bool ignoreCase = args.TryGetProperty("ignore_case", out var ic) && ic.ValueKind == JsonValueKind.True;

      // Extension filter — accept ".cs" or "cs"; comma-separated also supported.
      var extFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (args.TryGetProperty("ext", out var e) && e.ValueKind == JsonValueKind.String)
      {
         foreach (var raw in e.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries))
         {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t[0] != '.') t = "." + t;
            extFilter.Add(t);
         }
      }

      Regex regex;
      try
      {
         var opts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
         regex = new Regex(pattern!, opts, REGEX_TIMEOUT);
      }
      catch (ArgumentException ex)
      {
         return Task.FromResult(ToolResult.Error("invalid regex: " + ex.Message));
      }

      try
      {
         var sb        = new StringBuilder();
         int hits      = 0;
         int filesScan = 0;

         // Single-file mode: skip the directory walk and the ext filter
         // entirely. If the model named one file, scan that file — they
         // already picked, no need to second-guess by extension.
         if (rootIsFile)
         {
            _ScanFile(absRoot, ctx, regex, sb, ref hits);
            return Task.FromResult(ToolResult.Ok(
               hits == 0 ? "(no matches)" : sb.ToString()));
         }

         foreach (var file in ctx.EnumerateFilesRecursive(absRoot, IsNoise))
         {
            ctx.Cancel.ThrowIfCancellationRequested();
            if (extFilter.Count > 0 && !extFilter.Contains(Path.GetExtension(file)))
               continue;

            filesScan++;
            if (filesScan > MAX_FILES)
            {
               sb.AppendLine("… (file scan capped at " + MAX_FILES + ")");
               break;
            }

            if (!_ScanFile(file, ctx, regex, sb, ref hits))
            {
               // Hit cap — _ScanFile already appended the cap notice.
               return Task.FromResult(ToolResult.Ok(sb.ToString()));
            }
         }

         // Check the buffer, not hits — a file-cap notice with zero hits
         // must survive so the model knows the search was incomplete.
         if (sb.Length == 0)
            return Task.FromResult(ToolResult.Ok("(no matches)"));

         return Task.FromResult(ToolResult.Ok(sb.ToString()));
      }
      catch (OperationCanceledException)
      {
         return Task.FromResult(ToolResult.Error("cancelled"));
      }
      catch (RegexMatchTimeoutException)
      {
         return Task.FromResult(ToolResult.Error("regex match timeout — pattern too expensive"));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   /// <summary>
   /// Scan one file for regex matches. Appends "rel:line: content" rows to
   /// <paramref name="sb"/> and increments <paramref name="hits"/>. Returns
   /// false when the global hit cap has been reached and the caller should
   /// stop walking files; true otherwise (including silent skip on read
   /// failure — binary file, permission denied, etc.).
   /// </summary>
   private static bool _ScanFile(string file, ToolContext ctx, Regex regex, StringBuilder sb, ref int hits)
   {
      var rel    = ctx.ToRelative(file);
      int lineNo = 0;
      try
      {
         using var sr = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
         string? line;
         while ((line = sr.ReadLine()) != null)
         {
            lineNo++;
            if (!regex.IsMatch(line))
               continue;

            var display = line.Length > MAX_LINE ? line.Substring(0, MAX_LINE) + "…" : line;
            sb.Append(rel).Append(':').Append(lineNo).Append(": ").Append(display).Append('\n');
            hits++;

            if (hits >= MAX_HITS)
            {
               sb.AppendLine("… (capped at " + MAX_HITS + " matches)");
               return false;
            }
         }
      }
      catch
      {
         // Binary file, permission denied, etc. — skip silently.
      }
      return true;
   }

   private static bool IsNoise(string name)
   {
      if (name.Length == 0) return false;
      if (name[0] == '.') return true;
      return name.Equals("bin", StringComparison.OrdinalIgnoreCase)
          || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
          || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
          || name.Equals("packages", StringComparison.OrdinalIgnoreCase)
          || name.Equals("dist", StringComparison.OrdinalIgnoreCase)
          || name.Equals("build", StringComparison.OrdinalIgnoreCase);
   }

   // -- constants ------------------------------------------------------------

   private const int MAX_HITS  = 300;
   private const int MAX_FILES = 50000;
   private const int MAX_LINE  = 300;

   private static readonly TimeSpan REGEX_TIMEOUT = TimeSpan.FromSeconds(2);

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "pattern":     { "type": "string", "description": ".NET regex pattern." },
         "path":        { "type": "string", "description": "File or subdirectory to search, project-relative. A file path scans just that file (and the 'ext' filter is ignored). A directory path walks it recursively. Omit to search the whole project." },
         "ext":         { "type": "string", "description": "Restrict by extension(s), e.g. 'cs' or 'cs,axaml'." },
         "ignore_case": { "type": "boolean", "description": "Case-insensitive match (default false)." }
      },
      "required": ["pattern"]
   }
   """;
}
