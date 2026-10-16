// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Views.Native;
using SkiaSharp;

// `using Avalonia;` brings the Avalonia.Platform namespace into scope, which
// makes the bare name `Platform` ambiguous with our own static class.
using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views;

/// <summary>
/// AgentLoop event wiring and the visual transcript: bubbles, tool cards,
/// autoscroll and the streaming flush timer.
/// </summary>
public partial class NativeAgentView
{
   // -- AgentLoop event wiring -----------------------------------------------

   private void WireEvents(AgentLoop loop)
   {
      // Render mid-conversation system messages (mode announcements, injected
      // correction notes) as toggleable bubbles. The base system prompt is
      // added in the loop ctor without firing MessageAdded, so it never shows
      // here — only the in-flight system turns the model actually receives.
      loop.MessageAdded += msg =>
      {
         if (msg.Role == ChatRole.System)
            UiPost(() => AppendSystemBubble(msg.Content));
      };

      loop.TextDelta += s =>
         UiPost(() =>
         {
            EnsureAssistantBubble();
            _currentAssistant!.Append(s);
            EnsureFlushTimer();
         });

      loop.ReasoningDelta += s =>
         UiPost(() =>
         {
            // Reasoning often streams *before* the first text token; spin up
            // the bubble eagerly so the "Thinking" chip is visible while the
            // model is still composing its answer.
            EnsureAssistantBubble();
            _currentAssistant!.AppendReasoning(s);
            EnsureFlushTimer();
         });

      // -- overthinking meter -----------------------------------------------
      loop.ReasoningStatsChanged += stats =>
         UiPost(() =>
         {
            EnsureAssistantBubble();
            _currentAssistant!.SetReasoningStats(stats);
         });

      loop.ToolCallStarted += call =>
         UiPost(() =>
         {
            // Close out streaming text into the current bubble before
            // appending the tool card so render order matches event order.
            _currentAssistant?.Flush();

            var card = new ToolCallCard(call.Id, call.Name, _LookupTool(call.Name));
            _toolCards[call.Id] = card;
            AppendToolCard(card);
         });

      // For write_file / edit_file the arguments ARE the file body, so this is
      // the model typing the file out — minutes of it, previously rendered as
      // a motionless "⏳ write_file" card. Stream it into the card instead.
      loop.ToolCallArgsDelta += (id, fragment) =>
         UiPost(() =>
         {
            if (!_toolCards.TryGetValue(id, out var card)) return;
            card.AppendArgsDelta(fragment);
            if (!_streamingCards.Contains(card))
               _streamingCards.Add(card);
            EnsureFlushTimer();
         });

      loop.ToolCallReady += call =>
      {
         // Captured on the loop thread (not inside the UiPost): the subagent
         // factory reads this during ExecuteOne on the same thread, which can
         // run before the posted UI work below.
         if (_IsSubagentTool(call.Name))
            _lastSubagentCallId = call.Id;

         UiPost(() =>
         {
            if (_toolCards.TryGetValue(call.Id, out var card))
            {
               _streamingCards.Remove(card);
               card.SetArgs(call.ArgumentsJson);
            }

            // Remember the skill name a use_skill call targets so we can mark
            // it loaded once it completes successfully (below).
            if (string.Equals(call.Name, "use_skill", StringComparison.OrdinalIgnoreCase))
            {
               var name = _ParseUseSkillArgName(call.ArgumentsJson);
               if (!string.IsNullOrEmpty(name)) _skillCallArgNames[call.Id] = name!;
            }
         });
      };

      loop.ToolCallCompleted += (id, result) =>
         UiPost(() =>
         {
            if (_toolCards.TryGetValue(id, out var card))
               card.SetResult(result);

            // A model-loaded skill is now in context: reflect it in the picker.
            if (_skillCallArgNames.TryGetValue(id, out var skillName))
            {
               _skillCallArgNames.Remove(id);
               if (!result.IsError) _loadedSkills.Add(skillName);
            }
         });

      loop.UsageReported += usage =>
         UiPost(() =>
         {
            // OpenAI's prompt_tokens reflects the full prompt size including
            // history; adding completion gives the size of the conversation
            // after the turn — i.e. what the next request will resend.
            Gauge.UsedTokens = usage.PromptTokens + usage.CompletionTokens;
            Gauge.SetUsageBreakdown(
               usage.PromptTokens,
               usage.CachedReadTokens,
               usage.CompletionTokens,
               usage.ReasoningTokens ?? 0,
               usage.CacheWriteTokens);

            // One usage event per LLM iteration; summing them equals folding
            // the assistant messages, but without touching the history list
            // the loop thread is concurrently appending to.
            _sumInput      += usage.PromptTokens;
            _sumCached     += usage.CachedReadTokens;
            _sumOutput     += usage.CompletionTokens;
            _sumReasoning  += usage.ReasoningTokens ?? 0;
            _sumCacheWrite += usage.CacheWriteTokens;
            Gauge.SetSessionTotals(_sumInput, _sumCached, _sumOutput, _sumReasoning, _sumCacheWrite);

            // Main-loop denominators for the session throughput figures.
            _lastUsage            = usage;
            _mainOutputTokens    += usage.CompletionTokens;
            _mainReasoningTokens += usage.ReasoningTokens ?? 0;
            _mainUncachedInput   += usage.UncachedPromptTokens;

            int cachedPct = usage.PromptTokens > 0
               ? (int)Math.Round(usage.CachedFraction * 100)
               : 0;
            SetStatus("prompt=" + usage.PromptTokens
                      + " (" + cachedPct + "% cached)"
                      + " completion=" + usage.CompletionTokens);
         });

      // -- statistics -------------------------------------------------------
      // Closed phases of the run in flight arrive with every transition; the
      // open one grows on the bar's own clock until the next transition.
      loop.PhaseChanged += (phase, runSoFar) =>
         UiPost(() =>
         {
            _livePhase = phase;
            _runSoFar  = runSoFar;
            _RefreshStatsBars();
         });

      loop.ToolTimingReported += (id, timing) =>
         UiPost(() =>
         {
            if (_toolCards.TryGetValue(id, out var card))
               card.SetTiming(timing);
         });

      loop.TurnTimingsReported += timings =>
         UiPost(() =>
         {
            _sessionTimings.Add(timings);
            _runSoFar  = default;
            _livePhase = TurnPhase.Idle;
            _RefreshStatsBars();
         });

      loop.AssistantMessageCompleted += msg =>
         UiPost(() =>
         {
            // Seal the current bubble — next TextDelta starts a fresh one.
            // Lock the reasoning label to its final form (tokens if the
            // provider reported them, char count otherwise) so the live
            // "0.3k..." rolling counter resolves once thinking is done.
            _currentAssistant?.Flush();
            _currentAssistant?.FinalizeReasoning(msg.ReasoningTokens);

            // The turn's cost, now that it is known in full. The usage that
            // produced this message is the one reported last on this loop.
            _currentAssistant?.SetTimings(msg.Timings, msg.Reads,
                                          msg.PromptTokens, msg.CachedReadTokens,
                                          msg.CompletionTokens, msg.ReasoningTokens, _lastUsage);
            if (msg.Reads.HasValue)
            {
               _sessionReads.Add(msg.Reads.Value);
               _RefreshStatsBars();
            }
            if (msg.Timings.HasValue && msg.PromptTokens.HasValue)
            {
               var t = msg.Timings.Value;
               int cachedPct = msg.PromptTokens.Value > 0
                  ? (int)Math.Round(100.0 * (msg.CachedReadTokens ?? 0) / msg.PromptTokens.Value)
                  : 0;
               var status = "prompt=" + msg.PromptTokens.Value + " (" + cachedPct + "% cached)"
                          + " completion=" + (msg.CompletionTokens ?? 0)
                          + " · prefill " + TimingViz.FormatDuration(t.Prefill)
                          + " · gen " + TimingViz.FormatDuration(t.Generation);
               var rate = TimingViz.FormatRate(msg.CompletionTokens ?? 0, t.Generation);
               if (rate != null) status += " (" + rate + ")";
               SetStatus(status);
            }

            // This message is now a record in the file, so the bubble can be
            // mapped back to it and branched at. The counter advances for
            // every bubble-worthy message even in the (unexpected) case where
            // no bubble is holding it, so the ordinals stay aligned with what
            // a later replay of this file will produce.
            if (_RendersAsBubble(msg))
            {
               if (_currentAssistant != null)
                  _currentAssistant.RecordOrdinal = _committedAssistantTurns;
               _committedAssistantTurns++;
            }
            _currentAssistant = null;

            // Task panel updates flow through the `set_tasks` tool now,
            // not via markdown parsing — nothing to do here.
         });

      loop.Error += msg =>
         UiPost(() => AppendError(msg));

      loop.TurnCompleted += _ =>
         UiPost(() =>
         {
            // Flush anything still buffered and seal the bubble. A cancelled
            // turn never fires AssistantMessageCompleted, so without the
            // reset here the next turn's deltas would stream into this
            // stale bubble's thinking box instead of a fresh one.
            _currentAssistant?.Flush();
            _currentAssistant?.FinalizeReasoning(null);
            _currentAssistant = null;
            // The turn is committed, so its bubble becomes a branch point.
            // _RunTurn's finally repeats this once _running is false, which is
            // what an uncommitted (cancelled) tail bubble needs. Neither call
            // can be dropped.
            _RefreshBranchAffordances();
         });
   }

   // -- transcript management ------------------------------------------------

   private void EnsureAssistantBubble()
   {
      if (_currentAssistant != null) return;
      var bubble = _NewAssistantBubble();
      _currentAssistant = bubble;
      AppendItem(bubble);
   }

   /// <summary>
   /// Construct an <see cref="AssistantBubble"/> wired to the inspector and
   /// the branch actions, and track it for later affordance refreshes.
   ///
   /// Both the live path and the replay path go through here. Neither assigns
   /// the transcript ordinal — that is stamped separately, on commit, because
   /// a bubble can exist without ever becoming a record.
   /// </summary>
   private AssistantBubble _NewAssistantBubble()
   {
      var bubble = new AssistantBubble();
      bubble.BranchRequested += () => OnBranchRequested(bubble);
      _assistantBubbles.Add(bubble);
      return bubble;
   }

   private void AppendItem(TranscriptItem item)
   {
      // Any non-tool item ends the current consecutive-tool-call run.
      // Errors deliberately break the run too (a failed call shouldn't
      // pull surrounding successes into a group).
      _currentToolRun.Clear();
      _currentToolGroup = null;

      TranscriptPanel.Children.Add(item.View);
      ScrollToEnd();
   }

   /// <summary>
   /// Append a tool card. Cards 1 and 2 of a run land directly in the
   /// transcript; on card 3 they're retroactively reparented into a new
   /// <see cref="ToolCallGroupCard"/> and subsequent cards in the run join
   /// that group. The run is broken by any non-tool item via
   /// <see cref="AppendItem"/>.
   /// </summary>
   private void AppendToolCard(ToolCallCard card)
   {
      if (_currentToolGroup != null)
      {
         _currentToolGroup.AddCard(card);
         _currentToolRun.Add(card);
         ScrollToEnd();
         return;
      }

      _currentToolRun.Add(card);

      if (_currentToolRun.Count < GROUP_THRESHOLD)
      {
         TranscriptPanel.Children.Add(card.View);
         ScrollToEnd();
         return;
      }

      // Hit the threshold (default 3). Pull the prior cards out of the
      // transcript panel and into a new group; the just-added card joins
      // it as the third entry.
      var group = new ToolCallGroupCard();
      for (int i = 0; i < _currentToolRun.Count - 1; i++)
      {
         var prior = _currentToolRun[i];
         TranscriptPanel.Children.Remove(prior.View);
         group.AddCard(prior);
      }
      group.AddCard(card);

      TranscriptPanel.Children.Add(group.View);
      _currentToolGroup = group;
      ScrollToEnd();
   }

   private void AppendError(string msg) => AppendItem(new ErrorLine(msg));

   /// <summary>
   /// Construct a <see cref="UserBubble"/> wired with the revert callback
   /// and tracked in <see cref="_userBubbles"/>. Both the live send path
   /// and the resume-replay path go through here so the n-th bubble's
   /// position in the list matches the n-th user message in the session.
   /// </summary>
   private void AppendUserBubble(string text, AgentMode mode = AgentMode.Implement,
                                 IReadOnlyList<ImagePart>? images = null)
   {
      var bubble = new UserBubble(text, mode, OnRevertRequested, images, _ShowImagePreview);
      _userBubbles.Add(bubble);
      AppendItem(bubble);
   }

   /// <summary>
   /// Append a system-message bubble (always added, visibility gated by the
   /// header "sys" toggle). Tracked so toggling re-shows/hides all of them.
   /// </summary>
   private void AppendSystemBubble(string content, string badgeText = "SYSTEM")
   {
      var bubble = new SystemBubble(content ?? "", badgeText);
      bubble.View.IsVisible = _showSystemMessages;
      _systemBubbles.Add(bubble);
      AppendItem(bubble);
   }

   /// <summary>
   /// Append the tool-definitions block as a "sys"-gated bubble. Rendered
   /// right after the system prompt because that is where the server's chat
   /// template inlines the tools array — even though it travels as a
   /// request parameter, not a message. Regenerated from the live registry
   /// (never persisted): writing it as a system record would feed it into
   /// the model's history on resume and change the actual prompt.
   /// </summary>
   private void _AppendToolSpecsBubble()
   {
      var specs = (_viewToolRegistry ?? _BuildToolRegistry(withTaskCallback: true)).BuildSpecs();
      var sb = new System.Text.StringBuilder();
      sb.Append("Tool definitions advertised to the model with every request (");
      sb.Append(specs.Count);
      sb.Append(" tools). The provider's chat template inlines these next to the system prompt:\n");
      foreach (var t in specs)
      {
         sb.Append('\n');
         sb.Append(t.Name);
         sb.Append('\n');
         if (!string.IsNullOrWhiteSpace(t.Description))
         {
            sb.Append(t.Description);
            sb.Append('\n');
         }
         sb.Append("parameters: ");
         sb.Append(t.ParametersJsonSchema);
         sb.Append('\n');
      }
      AppendSystemBubble(sb.ToString(), "TOOLS");
   }

   private void TglSystem_Click(object? sender, RoutedEventArgs e)
   {
      _showSystemMessages = TglSystem.IsChecked == true;
      foreach (var b in _systemBubbles)
         b.View.IsVisible = _showSystemMessages;
   }

   // The splitter doubles as the latch indicator: green = following, gray =
   // the user is reading history.
   private void OnScrollLatchChanged(bool latched)
   {
      if (StatusSplitter != null) StatusSplitter.IsLatched = latched;
   }

   /// <summary>
   /// Request the transcript scroll to the bottom. No-op when the user has
   /// scrolled into history — in that case we leave them where they are
   /// until they come back to the bottom themselves.
   /// </summary>
   private void ScrollToEnd() => _scrollLatch.ScrollToEnd();

   // -- streaming flush timer ------------------------------------------------

   private void EnsureFlushTimer()
   {
      if (_flushTimer != null) return;
      _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50),
                                         DispatcherPriority.Background,
                                         (_, _) =>
                                         {
                                            _currentAssistant?.Flush();
                                            _FlushStreamingCards();
                                            ScrollToEnd();
                                         });
      _flushTimer.Start();
   }

   private void StopFlushTimer()
   {
      _flushTimer?.Stop();
      _flushTimer = null;
   }

   // Paint the cards whose arguments are still arriving, on the same 50 ms
   // cadence as streamed text. Cards drop out of the list once their arguments
   // are settled (ToolCallReady) or the turn ends mid-call.
   private void _FlushStreamingCards()
   {
      for (int i = _streamingCards.Count - 1; i >= 0; i--)
      {
         var card = _streamingCards[i];
         card.FlushStreaming();
         if (!card.IsStreaming)
            _streamingCards.RemoveAt(i);
      }
   }
}
