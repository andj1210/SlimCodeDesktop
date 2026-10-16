// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Run an arbitrary shell command through the OS shell in the project root.
/// Delegates execution to <see cref="ShellRunner"/>.
///
/// NOTE: as of the sandbox-by-default change this tool is intentionally NOT
/// registered (see <see cref="ToolRegistry.CreateFullDefaults"/>) — the model
/// can no longer run free-form shell commands. The class is kept dormant for
/// possible re-evaluation; user-driven build/test commands go through
/// <see cref="ProjectCommandTool"/> instead.
/// </summary>
public sealed class BashTool : ITool
{
   public string Name => "bash";

   // Platform-dependent: this text tells the model which shell syntax to
   // write, so it must track the shell ShellRunner actually invokes.
   public string Description =>
      "Run a shell command via " + Platform.ShellDescription + ", with the project " +
      "as the working directory. Returns combined stdout+stderr. Default timeout: 30s.";

   public ToolCategory Category => ToolCategory.Execute;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      var cmd = args.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
      var flat = cmd.Replace('\n', ' ').Replace('\r', ' ');
      const int max = 200;
      return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
   }

   public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      var command = args.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
      if (string.IsNullOrWhiteSpace(command))
         return ToolResult.Error("'command' is required");

      int timeoutSec = ShellRunner.DEFAULT_TIMEOUT_SEC;
      if (args.TryGetProperty("timeout_sec", out var t) && t.ValueKind == JsonValueKind.Number)
         timeoutSec = t.GetInt32();

      var r = await ShellRunner.RunAsync(command, ctx, timeoutSec).ConfigureAwait(false);

      var header = new StringBuilder();
      if (r.TimedOut)            header.AppendLine("[timed out — process killed]");
      else if (r.Cancelled)      header.AppendLine("[cancelled]");
      else if (r.ExitCode is int code && code != 0) header.AppendLine("[exit code " + code + "]");
      if (r.Truncated)           header.AppendLine("[output exceeded the capture limit and was cut]");

      // ShellRunner captures up to 10 MB; that is a memory ceiling, so the
      // context budget has to be applied here before the log reaches the model.
      var body    = ShellRunner.Elide(r.Output, HEAD_LINES, TAIL_LINES, HEAD_CHARS, TAIL_CHARS);
      var content = header.Length > 0 ? header.ToString() + body : body;
      if (string.IsNullOrEmpty(content)) content = "(no output)";

      return r.Success
         ? ToolResult.Ok(content)
         : new ToolResult { IsError = true, Content = content };
   }

   // -- constants ------------------------------------------------------------

   // Same budget as the project build/test tools.
   private const int HEAD_LINES = 120;
   private const int TAIL_LINES = 40;
   private const int HEAD_CHARS = 30_000;
   private const int TAIL_CHARS = 10_000;

   // $$ raw literal: {{ }} are interpolation holes, single braces stay literal
   // JSON. Not const -- the shell name is resolved per target OS.
   private static readonly string SCHEMA = $$"""
   {
      "type": "object",
      "properties": {
         "command":     { "type": "string",  "description": "Shell command line. Executed via {{Platform.ShellDescription}} with cwd = project root." },
         "timeout_sec": { "type": "integer", "minimum": 1, "maximum": 600, "description": "Kill the process if it runs longer than this. Default 30." }
      },
      "required": ["command"]
   }
   """;
}
