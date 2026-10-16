// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Delegate a self-contained task to a subagent: a nested
/// <see cref="AgentLoop"/> with its own context window and tool set. Runs
/// synchronously inside this tool call; the subagent's final report is the
/// tool result.
///
/// Registered twice, once per <see cref="SubagentKind"/>, so the parent
/// model declares the capability it needs by picking a tool rather than by
/// passing an argument. That split is what keeps
/// <see cref="ITool.Category"/> honest: <c>start_read_subagent</c> is
/// <see cref="ToolCategory.Read"/> because its subagent cannot mutate
/// anything by construction, while <c>start_write_subagent</c> is
/// <see cref="ToolCategory.Write"/> because its subagent can. It also gives
/// the user two independent entries in the permission policy — "always
/// allow research, always ask for writes" needs no new machinery.
///
/// Instances are built per session by
/// <c>NativeAgentView._BuildToolRegistry</c> with a factory that builds the
/// nested loop from the session's provider settings and announces the run
/// to the UI (progress on the tool card, optional viewer tab).
/// </summary>
public sealed class SubagentTool : ITool
{
   /// <param name="kind">Capability level this instance delegates at.</param>
   /// <param name="startRun">Build a ready-to-run subagent for the task, or
   /// return an error string explaining why not. Called on the parent
   /// loop's background thread.</param>
   public SubagentTool(SubagentKind kind, Func<SubagentKind, string, string?, SubagentStart> startRun)
   {
      _kind     = kind;
      _startRun = startRun ?? throw new ArgumentNullException(nameof(startRun));
   }

   public string Name =>
      _kind == SubagentKind.Write ? "start_write_subagent" : "start_read_subagent";

   public string Description => _kind == SubagentKind.Write
      ? "Delegate a self-contained IMPLEMENTATION task (apply a refactor across files, " +
        "write a module to a stated spec) to a subagent with its own fresh context window " +
        "and the full file tools; its final report and the list of files it changed are " +
        "returned as this tool's result. Its writes are gated by the same permission " +
        "prompts and grants as your own, so prefer 'start_read_subagent' whenever reading " +
        "is enough. The subagent cannot ask questions — the task must contain every detail " +
        "it needs (background, goal, exact expected outcome). Do not delegate work you " +
        "have already half-finished: it cannot see this conversation."
      : "Delegate a self-contained READ-ONLY research task (codebase search, analysis, " +
        "summarization) to a subagent with its own fresh context window; its final report " +
        "is returned as this tool's result. Use it when the investigation would flood your " +
        "own context with file contents you don't need verbatim. The subagent can only " +
        "read/list/glob/grep files: it cannot write, edit, move or delete anything, so use " +
        "'start_write_subagent' when the task has to change the tree. It cannot ask " +
        "questions either — the task must contain every detail it needs (background, goal, " +
        "expected report format).";

   public ToolCategory Category =>
      _kind == SubagentKind.Write ? ToolCategory.Write : ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      string label = args.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
      if (!string.IsNullOrWhiteSpace(label)) return label;
      string task = args.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
      var flat = task.Replace('\n', ' ').Replace('\r', ' ');
      const int max = 120;
      return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
   }

   public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? task = args.TryGetProperty("task", out var t) ? t.GetString() : null;
      if (string.IsNullOrWhiteSpace(task))
         return ToolResult.Error("'task' is required");

      string? label = args.TryGetProperty("label", out var l) ? l.GetString() : null;

      SubagentStart start;
      try
      {
         start = _startRun(_kind, task!, label);
      }
      catch (Exception ex)
      {
         return ToolResult.Error("could not start subagent: " + ex.Message);
      }
      if (start.Run == null)
         return ToolResult.Error("could not start subagent: " +
                                 (start.Error ?? "no provider/model configured"));

      return await start.Run.RunAsync(ctx.Cancel).ConfigureAwait(false);
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "task":  { "type": "string", "description": "Complete, self-contained instructions for the subagent: background, what to do, and what the final report must contain (paths, line references, conclusions)." },
         "label": { "type": "string", "description": "3-6 word summary of the task, shown in the UI and in any permission prompt the subagent raises." }
      },
      "required": ["task"]
   }
   """;

   // -- fields -------------------------------------------------------------------

   private readonly SubagentKind _kind;
   private readonly Func<SubagentKind, string, string?, SubagentStart> _startRun;
}

/// <summary>
/// Outcome of asking the host to build a subagent: either a ready run, or
/// the reason there isn't one. The reason is surfaced to the model as a
/// recoverable tool error, so it must read as an instruction the model can
/// act on ("switch to Implement", "no model configured").
/// </summary>
public sealed class SubagentStart
{
   public SubagentRun? Run   { get; init; }
   public string?      Error { get; init; }

   public static SubagentStart Ok(SubagentRun run)      => new() { Run = run };
   public static SubagentStart Fail(string reason)      => new() { Error = reason };
}
