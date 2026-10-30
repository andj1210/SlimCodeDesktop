// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Views.Native;
using Xunit;

namespace SlimCodeDesktop.Tests;

public sealed class SlimmyActsTests
{
   [Theory]
   [InlineData(TurnPhase.Idle,           SlimmyAct.Idle)]
   [InlineData(TurnPhase.Prefill,        SlimmyAct.Prefill)]
   [InlineData(TurnPhase.Thinking,       SlimmyAct.Thinking)]
   [InlineData(TurnPhase.Output,         SlimmyAct.Output)]
   [InlineData(TurnPhase.ToolArgs,       SlimmyAct.ToolArgs)]
   [InlineData(TurnPhase.ToolExec,       SlimmyAct.ToolExec)]
   [InlineData(TurnPhase.McpConnect,     SlimmyAct.ToolExec)]
   [InlineData(TurnPhase.PermissionWait, SlimmyAct.Permission)]
   public void For_MapsEveryPhase(TurnPhase phase, SlimmyAct act)
   {
      Assert.Equal(act, SlimmyActs.For(phase));
   }

   [Fact]
   public void Resolve_RunningSubagent_ShowsDuoWithSubagentsAct()
   {
      var (_, sub) = SlimmyActs.Resolve(TurnPhase.ToolExec, TurnPhase.Thinking, 0);
      Assert.Equal(SlimmyAct.Thinking, sub);
   }

   [Fact]
   public void Resolve_FinishedSubagent_EndsDuo()
   {
      var (main, sub) = SlimmyActs.Resolve(TurnPhase.ToolExec, TurnPhase.Idle, 0);
      Assert.Null(sub);
      Assert.Equal(SlimmyAct.ToolExec, main);
   }

   [Fact]
   public void Resolve_LongIdle_FallsAsleep()
   {
      Assert.Equal(SlimmyAct.Idle,
                   SlimmyActs.Resolve(TurnPhase.Idle, null, SlimmyActs.SLEEP_AFTER_SECONDS - 1).main);
      Assert.Equal(SlimmyAct.Sleep,
                   SlimmyActs.Resolve(TurnPhase.Idle, null, SlimmyActs.SLEEP_AFTER_SECONDS).main);
   }

   [Fact]
   public void Resolve_BusyNeverSleeps()
   {
      Assert.Equal(SlimmyAct.Prefill, SlimmyActs.Resolve(TurnPhase.Prefill, null, 1e6).main);
   }
}

public sealed class SlimmyBreakTests
{
   [Fact]
   public void ComesOutOnlyAfterAQuietMinute()
   {
      var b = new SlimmyBreak(0);
      b.Advance(SlimmyBreak.IDLE_SECONDS - 0.1);
      Assert.Equal(SlimmyBreakState.Waiting, b.State);
      b.Advance(SlimmyBreak.IDLE_SECONDS);
      Assert.Equal(SlimmyBreakState.Enter, b.State);
   }

   [Fact]
   public void InputWhileWaiting_RestartsTheCount()
   {
      var b = new SlimmyBreak(0);
      Assert.False(b.Activity(50));
      b.Advance(SlimmyBreak.IDLE_SECONDS + 1);
      Assert.Equal(SlimmyBreakState.Waiting, b.State);
      b.Advance(50 + SlimmyBreak.IDLE_SECONDS);
      Assert.Equal(SlimmyBreakState.Enter, b.State);
   }

   [Fact]
   public void FullCycle_PlayCaughtWorkBackPlay()
   {
      double t = SlimmyBreak.IDLE_SECONDS;
      var b = new SlimmyBreak(0);
      b.Advance(t);
      t += SlimmyBreak.ENTER_SECONDS + 0.01;
      b.Advance(t);
      Assert.Equal(SlimmyBreakState.Play, b.State);

      t += 5;
      Assert.True(b.Activity(t));
      Assert.Equal(SlimmyBreakState.Scatter, b.State);
      t += SlimmyBreak.SCATTER_SECONDS + 0.01;
      b.Advance(t);
      Assert.Equal(SlimmyBreakState.Work, b.State);

      // Still being watched: they keep pretending.
      Assert.False(b.Activity(t + 30));
      b.Advance(t + SlimmyBreak.QUIET_SECONDS);
      Assert.Equal(SlimmyBreakState.Work, b.State);

      t += 30 + SlimmyBreak.QUIET_SECONDS + 0.01;
      b.Advance(t);
      Assert.Equal(SlimmyBreakState.Back, b.State);
      t += SlimmyBreak.BACK_SECONDS + 0.01;
      b.Advance(t);
      Assert.Equal(SlimmyBreakState.Play, b.State);
   }

   [Fact]
   public void CaughtWhileWalkingBack_ScattersAgain()
   {
      var b = new SlimmyBreak(0);
      double t = SlimmyBreak.IDLE_SECONDS;
      b.Advance(t);
      Assert.True(b.Activity(t += 0.5));  // caught walking in
      b.Advance(t += SlimmyBreak.SCATTER_SECONDS + 0.01);
      Assert.Equal(SlimmyBreakState.Work, b.State);
      b.Advance(t += SlimmyBreak.QUIET_SECONDS);
      Assert.Equal(SlimmyBreakState.Back, b.State);
      Assert.True(b.Activity(t + 0.1));
      Assert.Equal(SlimmyBreakState.Scatter, b.State);
   }

   [Fact]
   public void Reset_ClearsTheStage()
   {
      var b = new SlimmyBreak(0);
      b.Advance(SlimmyBreak.IDLE_SECONDS);
      b.Reset(100);
      Assert.Equal(SlimmyBreakState.Waiting, b.State);
      b.Advance(100 + SlimmyBreak.IDLE_SECONDS - 1);
      Assert.Equal(SlimmyBreakState.Waiting, b.State);
   }
}
