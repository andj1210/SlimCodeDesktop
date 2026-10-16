// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// What the agent loop is waiting on right now. Drives the wall-clock
/// accounting in <see cref="PhaseTimings"/>: time is credited to whichever
/// phase was current when it elapsed.
/// </summary>
public enum TurnPhase
{
   /// <summary>Between runs: nothing in flight, no time is credited.</summary>
   Idle,

   /// <summary>
   /// Bringing an MCP project's servers up on the session's first send:
   /// spawn / connect, initialize, tools/list. Seconds for a stdio server
   /// that has to fetch a package, milliseconds over HTTP. Credited by the
   /// host rather than the agent loop — it happens before the loop exists.
   /// </summary>
   McpConnect,

   /// <summary>
   /// Request sent, no token back yet. Honest name is "time to first token":
   /// on a llama-swap endpoint a cold model load lands here too, and so does
   /// server queueing.
   /// </summary>
   Prefill,

   /// <summary>Reasoning-channel tokens arriving.</summary>
   Thinking,

   /// <summary>Main-channel answer tokens arriving.</summary>
   Output,

   /// <summary>
   /// Tool-call name / argument tokens arriving. Still the model generating -
   /// for write_file and edit_file the arguments ARE the file body, so this
   /// is where a long write actually spends its time.
   /// </summary>
   ToolArgs,

   /// <summary>Blocked on the permission gate (usually: a prompt open for the user).</summary>
   PermissionWait,

   /// <summary>A tool body running. For a subagent tool this spans the whole nested run.</summary>
   ToolExec,
}

/// <summary>
/// Wall-clock time per <see cref="TurnPhase"/>. One instance describes one
/// unit of work at whatever granularity the holder chose: a single LLM call,
/// an assistant turn, a tool call, a whole user request, or a session. Units
/// add with <see cref="Add(in PhaseTimings)"/>.
///
/// Idle is deliberately not a slot: this measures busy time. Wall time across
/// a session includes the user typing, which is not something to optimize.
/// </summary>
public struct PhaseTimings
{
   public TimeSpan McpConnect;
   public TimeSpan Prefill;
   public TimeSpan Thinking;
   public TimeSpan Output;
   public TimeSpan ToolArgs;
   public TimeSpan PermissionWait;
   public TimeSpan ToolExec;

   /// <summary>Every phase in display order, for renderers and formatters.</summary>
   public static readonly TurnPhase[] Phases =
   {
      TurnPhase.McpConnect,
      TurnPhase.Prefill,
      TurnPhase.Thinking,
      TurnPhase.Output,
      TurnPhase.ToolArgs,
      TurnPhase.PermissionWait,
      TurnPhase.ToolExec,
   };

   public TimeSpan this[TurnPhase phase]
   {
      get => phase switch
      {
         TurnPhase.McpConnect     => McpConnect,
         TurnPhase.Prefill        => Prefill,
         TurnPhase.Thinking       => Thinking,
         TurnPhase.Output         => Output,
         TurnPhase.ToolArgs       => ToolArgs,
         TurnPhase.PermissionWait => PermissionWait,
         TurnPhase.ToolExec       => ToolExec,
         _                        => TimeSpan.Zero,
      };
      set
      {
         switch (phase)
         {
            case TurnPhase.McpConnect:     McpConnect     = value; break;
            case TurnPhase.Prefill:        Prefill        = value; break;
            case TurnPhase.Thinking:       Thinking       = value; break;
            case TurnPhase.Output:         Output         = value; break;
            case TurnPhase.ToolArgs:       ToolArgs       = value; break;
            case TurnPhase.PermissionWait: PermissionWait = value; break;
            case TurnPhase.ToolExec:       ToolExec       = value; break;
         }
      }
   }

   /// <summary>Time the model spent producing tokens of any kind - the decode denominator.</summary>
   public TimeSpan Generation => Thinking + Output + ToolArgs;

   /// <summary>Everything, i.e. the length of the bar.</summary>
   public TimeSpan Busy =>
      McpConnect + Prefill + Thinking + Output + ToolArgs + PermissionWait + ToolExec;

   public bool IsEmpty => Busy == TimeSpan.Zero;

   public void Add(TurnPhase phase, TimeSpan span)
   {
      if (span <= TimeSpan.Zero || phase == TurnPhase.Idle)
         return;
      this[phase] = this[phase] + span;
   }

   public void Add(in PhaseTimings other)
   {
      McpConnect     += other.McpConnect;
      Prefill        += other.Prefill;
      Thinking       += other.Thinking;
      Output         += other.Output;
      ToolArgs       += other.ToolArgs;
      PermissionWait += other.PermissionWait;
      ToolExec       += other.ToolExec;
   }

   /// <summary>Short lower-case label for a phase, as used in bars and status lines.</summary>
   public static string Label(TurnPhase phase) => phase switch
   {
      TurnPhase.McpConnect     => "mcp connect",
      TurnPhase.Prefill        => "prefill",
      TurnPhase.Thinking       => "thinking",
      TurnPhase.Output         => "output",
      TurnPhase.ToolArgs       => "tool args",
      TurnPhase.PermissionWait => "permission wait",
      TurnPhase.ToolExec       => "tool exec",
      _                        => "idle",
   };
}
