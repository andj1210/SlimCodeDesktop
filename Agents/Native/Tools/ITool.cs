// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// One callable tool advertised to the model. Implementations are stateless
/// — shared state belongs in <see cref="ToolContext"/>, which is supplied
/// per call.
/// </summary>
public interface ITool
{
   /// <summary>Stable name. Sent to the model; must match across runs.</summary>
   string Name { get; }

   /// <summary>One-line description shown to the model in the tool catalog.</summary>
   string Description { get; }

   /// <summary>
   /// Coarse classification driving default permission policy. Read tools
   /// auto-allow at the global layer; write/execute fall through to the
   /// permission prompt.
   /// </summary>
   ToolCategory Category { get; }

   /// <summary>
   /// JSON Schema for the arguments object. Forwarded verbatim to the
   /// provider as the <c>parameters</c> field of the tool definition.
   /// </summary>
   string ParametersJsonSchema { get; }

   /// <summary>
   /// Compact, single-line, PII-light rendering of the arguments for the
   /// audit log and the permission prompt dialog. Default-style tools
   /// can delegate to <see cref="ToolPreview.DefaultPreview"/>; tools with
   /// large or sensitive args (write_file's content, bash's command) should
   /// return a hand-crafted summary instead.
   /// </summary>
   string ArgsPreview(JsonElement arguments);

   /// <summary>
   /// Execute the call. <paramref name="arguments"/> is the parsed JSON object
   /// the model emitted (already validated as JSON, not against the schema).
   /// </summary>
   Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext ctx);
}

/// <summary>Static helpers used by multiple <see cref="ITool"/> implementations.</summary>
public static class ToolPreview
{
   /// <summary>Truncated, single-line render of the raw JSON object.</summary>
   public static string DefaultPreview(JsonElement args)
   {
      var raw  = args.GetRawText();
      var flat = raw.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
      while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
      const int max = 160;
      return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
   }

   /// <summary>Format a byte count as "412 bytes" / "1.2 KB" / "3.4 MB".</summary>
   public static string FormatBytes(long n)
   {
      if (n < 1024)            return n + " bytes";
      if (n < 1024 * 1024)     return (n / 1024.0)        .ToString("0.#") + " KB";
      if (n < 1024L*1024*1024) return (n / 1048576.0)     .ToString("0.#") + " MB";
      return (n / 1073741824.0).ToString("0.##") + " GB";
   }
}
