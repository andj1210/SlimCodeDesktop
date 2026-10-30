// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using SlimCodeDesktop.Agents.Native.Llm;

namespace SlimCodeDesktop.Views.Native;

/// <summary>What Slimmy acts out. One per pose in <see cref="SlimmyRig"/>.</summary>
public enum SlimmyAct
{
   /// <summary>Stands; a glint crosses the glasses and the hat tips now and then.</summary>
   Idle,

   /// <summary>Idle for a long time: dozes off.</summary>
   Sleep,

   /// <summary>Waiting for the first token: reads the newspaper.</summary>
   Prefill,

   /// <summary>Reasoning tokens: hand on chin, brain in a thought bubble.</summary>
   Thinking,

   /// <summary>Answer tokens: talks.</summary>
   Output,

   /// <summary>Tool-call arguments streaming: writes a letter.</summary>
   ToolArgs,

   /// <summary>A tool body running: arms crossed, gears turning.</summary>
   ToolExec,

   /// <summary>Blocked on the permission prompt: looks at the user.</summary>
   Permission,
}

/// <summary>
/// Which act a phase maps to, and what the header shows as a whole. Kept
/// apart from the drawing so the rules can be tested without a renderer.
/// </summary>
public static class SlimmyActs
{
   /// <summary>Idle this long and Slimmy falls asleep.</summary>
   public const double SLEEP_AFTER_SECONDS = 10 * 60;

   public static SlimmyAct For(TurnPhase phase) => phase switch
   {
      TurnPhase.Prefill        => SlimmyAct.Prefill,
      TurnPhase.Thinking       => SlimmyAct.Thinking,
      TurnPhase.Output         => SlimmyAct.Output,
      TurnPhase.ToolArgs       => SlimmyAct.ToolArgs,
      TurnPhase.PermissionWait => SlimmyAct.Permission,
      // Bringing MCP servers up is the harness working, not the model.
      TurnPhase.McpConnect     => SlimmyAct.ToolExec,
      TurnPhase.ToolExec       => SlimmyAct.ToolExec,
      _                        => SlimmyAct.Idle,
   };

   /// <summary>
   /// The act the main figure shows, and the one a second, smaller figure
   /// shows while a subagent runs (null: no second figure). A subagent that
   /// has finished but whose parent has not moved on yet is
   /// <see cref="TurnPhase.Idle"/>: the duo is over, the parent is back to
   /// waiting on its tool call.
   /// </summary>
   public static (SlimmyAct main, SlimmyAct? sub) Resolve(TurnPhase main, TurnPhase? sub, double idleSeconds)
   {
      if (sub.HasValue && sub.Value != TurnPhase.Idle)
         return (SlimmyAct.Idle, For(sub.Value));

      var act = For(main);
      if (act == SlimmyAct.Idle && idleSeconds >= SLEEP_AFTER_SECONDS)
         act = SlimmyAct.Sleep;
      return (act, null);
   }
}
