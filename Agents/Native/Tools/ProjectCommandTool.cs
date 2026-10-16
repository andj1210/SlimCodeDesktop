// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Runs a fixed, user-authored multi-step project command (build / test). The
/// model supplies NO command — it can only trigger the steps the human
/// configured in project settings, which is the core safety property that
/// replaces the free-form <c>bash</c> tool. Steps + per-step timeout are read
/// fresh from the project config on each call, so edits take effect without
/// rebuilding.
///
/// Steps run in order and stop at the first non-zero exit. Each executed step
/// reports its name, command and exit code; its log is forwarded only when the
/// step failed OR the step opts in via <see cref="CommandStep.LogOnSucceed"/>.
/// Attached logs are head+tail-elided to bound context.
///
/// Gated as <see cref="ToolCategory.Execute"/>: the first call prompts; the
/// user typically grants "Allow project" once.
/// </summary>
public sealed class ProjectCommandTool : ITool
{
   private readonly string _name;
   private readonly string _description;
   private readonly Func<ProjectConfig, (IReadOnlyList<CommandStep> steps, int timeoutSec)> _selector;

   public ProjectCommandTool(
      string name,
      string description,
      Func<ProjectConfig, (IReadOnlyList<CommandStep> steps, int timeoutSec)> selector)
   {
      _name        = name;
      _description = description;
      _selector    = selector;
   }

   public string Name => _name;
   public string Description => _description;
   public ToolCategory Category => ToolCategory.Execute;
   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement arguments) => "(runs the project's " + _name + " steps)";

   public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext ctx)
   {
      var cfg = ProjectConfigStore.LoadOrCreate(ctx.ProjectRoot).Config;
      var (steps, timeoutSec) = _selector(cfg);

      var runnable = new List<CommandStep>();
      foreach (var s in steps)
         if (!string.IsNullOrWhiteSpace(s.Command)) runnable.Add(s);

      if (runnable.Count == 0)
         return ToolResult.Error(
            "No " + _name + " steps configured for this project. " +
            "Ask the user to set them in Project settings.");

      var sb = new StringBuilder();
      int failedIndex = -1;

      for (int i = 0; i < runnable.Count; i++)
      {
         var step = runnable[i];
         var r    = await ShellRunner.RunAsync(step.Command, ctx, timeoutSec).ConfigureAwait(false);

         if (i > 0) sb.Append('\n');
         _AppendStep(sb, step, i, r, timeoutSec);

         if (!r.Success)
         {
            failedIndex = i;
            int remaining = runnable.Count - 1 - i;
            if (remaining > 0)
               sb.Append("\n[").Append(remaining).Append(" later step(s) skipped]\n");
            break;
         }
      }

      // A summary on the FIRST line states the outcome and, on failure, exactly
      // which step failed — so it can't be misread as the first step failing.
      string summary;
      if (failedIndex < 0)
         summary = _name + ": all " + runnable.Count + " step(s) succeeded.";
      else
         summary = _name + " FAILED at step " + (failedIndex + 1) + "/" + runnable.Count +
                   " (\"" + StepLabel(runnable[failedIndex], failedIndex) + "\"). Earlier steps succeeded.";

      var content = summary + "\n\n" + sb.ToString().TrimEnd('\n');
      return failedIndex < 0
         ? ToolResult.Ok(content)
         : new ToolResult { IsError = true, Content = content };
   }

   private static string StepLabel(CommandStep step, int index) =>
      string.IsNullOrWhiteSpace(step.Name) ? ("step " + (index + 1)) : step.Name;

   private void _AppendStep(StringBuilder sb, CommandStep step, int index, ShellResult r, int timeoutSec)
   {
      sb.Append('"').Append(StepLabel(step, index)).Append("\" -> ").Append(step.Command).Append('\n');
      sb.Append("Exit code: ").Append(_ExitText(r, timeoutSec)).Append('\n');

      bool attachLog = !r.Success || step.LogOnSucceed;
      if (attachLog)
      {
         var log = string.IsNullOrEmpty(r.Output)
            ? "(no output)"
            : ShellRunner.Elide(r.Output, HEAD_LINES, TAIL_LINES, HEAD_CHARS, TAIL_CHARS);
         if (r.Truncated) log += "\n[output exceeded the capture limit and was cut]";
         sb.Append("Log:\n").Append(log).Append('\n');
      }
   }

   private static string _ExitText(ShellResult r, int timeoutSec)
   {
      if (r.TimedOut)  return "timed out after " + timeoutSec + "s (process killed)";
      if (r.Cancelled) return "cancelled";
      return r.ExitCode?.ToString() ?? "n/a";
   }

   // Failure-output budget. Head catches root errors (C++); tail catches
   // summaries (dotnet/test). The char budgets bound the same ends for output
   // that does not break into lines — see ShellRunner.Elide.
   private const int HEAD_LINES = 120;
   private const int TAIL_LINES = 40;
   private const int HEAD_CHARS = 30_000;
   private const int TAIL_CHARS = 10_000;

   // No model-supplied arguments: the steps are fixed by the user.
   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {},
      "additionalProperties": false
   }
   """;
}
