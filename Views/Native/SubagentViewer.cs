// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Read-only live transcript of one <see cref="SubagentRun"/> — the optional
/// subagent tab's content. Pure observer: it renders the nested loop's
/// events (streamed text, tool cards, errors) but owns nothing; closing the
/// viewer never affects the run, which belongs to the parent session's
/// subagent tool call.
///
/// <see cref="Detach"/> unsubscribes every handler so a closed viewer stops
/// receiving events and can be collected while the run keeps going.
/// </summary>
public sealed class SubagentViewer : UserControl
{
   /// <summary>Live mode: observe an in-flight (or just-finished) run.</summary>
   public SubagentViewer(SubagentRun run)
      : this((run ?? throw new ArgumentNullException(nameof(run))).TaskText,
             run.Kind == SubagentKind.Write)
   {
      _run = run;
      _Subscribe();
   }

   /// <summary>
   /// Replay mode: render a stored subagent transcript (a resumed session's
   /// subagent card). Static — no live events, nothing to detach.
   /// </summary>
   public SubagentViewer(string task, IReadOnlyList<TranscriptRecord> records, bool isWrite)
      : this(task, isWrite)
   {
      _ReplayRecords(records);
   }

   private SubagentViewer(string task, bool isWrite)
   {
      // -- header: task text + live status -------------------------------------
      // No MaxHeight here: the block must lay out its full text so the
      // ScrollViewer below can actually scroll through it. Capping the text
      // block instead of the viewport clips the tail beyond reach.
      var taskTb = new SelectableTextBlock
      {
         Text         = task,
         FontSize     = 12,
         Foreground   = ThemeBrushes.TextPrimary,
         TextWrapping = TextWrapping.Wrap,
      };
      _statusTb = new TextBlock
      {
         Text       = "subagent running…",
         FontSize   = 11,
         Foreground = ThemeBrushes.TextMuted,
         Margin     = new Avalonia.Thickness(0, 4, 0, 0),
      };
      // The handover prompt is the one thing the user opens this tab to read,
      // so the title row toggles between a compact preview and the full text.
      _taskChevron = new TextBlock
      {
         Text              = "▶",
         FontSize          = 9,
         Width             = 12,
         Foreground        = ThemeBrushes.Accent,
         VerticalAlignment = VerticalAlignment.Center,
      };
      var titleTb = new TextBlock
      {
         // Name the subagent kind only. A "(read-only viewer)" note here read
         // as a claim about the RUN — badly wrong on a write subagent — and
         // the missing prompt box already tells the user the tab is passive.
         Text              = isWrite ? "WRITE SUBAGENT TASK" : "READ SUBAGENT TASK",
         FontSize          = 10,
         FontWeight        = FontWeight.Bold,
         Foreground        = ThemeBrushes.Accent,
         VerticalAlignment = VerticalAlignment.Center,
      };
      _taskHint = new TextBlock
      {
         Text              = "click to expand",
         FontSize          = 9,
         Foreground        = ThemeBrushes.TextMuted,
         Margin            = new Avalonia.Thickness(8, 0, 0, 0),
         VerticalAlignment = VerticalAlignment.Center,
      };
      var titleRow = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         Margin      = new Avalonia.Thickness(0, 0, 0, 4),
      };
      titleRow.Children.Add(_taskChevron);
      titleRow.Children.Add(titleTb);
      titleRow.Children.Add(_taskHint);

      var titleBorder = new Border
      {
         Background = Brushes.Transparent,
         Cursor     = new Cursor(StandardCursorType.Hand),
         Child      = titleRow,
      };
      titleBorder.PointerPressed += (_, _) => _SetTaskExpanded(!_taskExpanded);

      _taskScroll = new ScrollViewer
      {
         MaxHeight = TASK_COLLAPSED_HEIGHT,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         Content   = taskTb,
      };

      var headerPanel = new StackPanel();
      headerPanel.Children.Add(titleBorder);
      headerPanel.Children.Add(_taskScroll);
      headerPanel.Children.Add(_statusTb);

      var header = new Border
      {
         Background      = ThemeBrushes.PanelBackground,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
         Padding         = new Avalonia.Thickness(12, 8),
         Child           = headerPanel,
      };

      // -- transcript -----------------------------------------------------------
      _transcript = new StackPanel { Margin = new Avalonia.Thickness(14, 10), Spacing = 0 };
      _scroll = new ScrollViewer
      {
         HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
         VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
         Content = _transcript,
      };
      // Same follow-the-bottom rules as the main transcript: a growing tool
      // card (an auto-expanded write streaming its body) keeps itself in view
      // instead of running off the bottom edge, and the user can scroll back
      // through a running subagent without being yanked down again.
      _latch = new ScrollLatch(_scroll);

      var root = new DockPanel { LastChildFill = true };
      DockPanel.SetDock(header, Dock.Top);
      root.Children.Add(header);
      root.Children.Add(_scroll);
      Content    = root;
      Background = ThemeBrushes.WindowBackground;

      // Open expanded: the handover prompt is what the user came to read, and
      // a task that outgrows even the tall viewport still scrolls inside it.
      _SetTaskExpanded(true);
   }

   /// <summary>Fires when the run finishes, after the status line update (UI thread).</summary>
   public event Action? RunCompleted;

   /// <summary>
   /// Unsubscribe from the run's loop. Idempotent. Call when the viewer tab
   /// closes — the run may still be in flight and must not keep pushing
   /// events into a dead control.
   /// </summary>
   public void Detach()
   {
      if (_detached) return;
      _detached = true;

      _flushTimer?.Stop();
      _flushTimer = null;
      if (_run == null) return; // replay mode: nothing subscribed

      var loop = _run.Loop;
      loop.TextDelta                 -= _onText;
      loop.ReasoningDelta            -= _onReasoning;
      loop.ToolCallStarted           -= _onCallStarted;
      loop.ToolCallArgsDelta         -= _onCallArgs;
      loop.ToolCallReady             -= _onCallReady;
      loop.ToolCallCompleted         -= _onCallCompleted;
      loop.ReasoningStatsChanged     -= _onStats;
      loop.Error                     -= _onError;
      loop.AssistantMessageCompleted -= _onAssistantDone;
      loop.ToolTimingReported        -= _onToolTiming;
      loop.TurnTimingsReported       -= _onTurnTimings;
      _run.Progress                  -= _onProgress;
      _run.Completed                 -= _onCompleted;
   }

   // -- private methods ----------------------------------------------------------

   /// <summary>
   /// Replay mode: rebuild the transcript from stored records. Mirrors the
   /// live event rendering — assistant text/reasoning as bubbles, tool calls
   /// as cards resolved by their results. The task (user message) and the
   /// canned system prompt are skipped: the header already shows the task.
   /// </summary>
   private void _ReplayRecords(IReadOnlyList<TranscriptRecord> records)
   {
      int  toolCalls = 0;
      long outTokens = 0;
      int  lastPrompt = 0;

      foreach (var r in records)
      {
         if (r.Type != "msg") continue;
         var role = TranscriptStore.ParseRole(r.Role);

         switch (role)
         {
            case SlimCodeDesktop.Agents.Native.ChatRole.Assistant:
               if (!string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ReasoningContent))
               {
                  var bubble = new AssistantBubble();
                  _transcript.Children.Add(bubble.View);
                  if (!string.IsNullOrEmpty(r.ReasoningContent))
                  {
                     bubble.SetReasoning(r.ReasoningContent!);
                     bubble.SetReasoningStats(ReasoningWatch.Evaluate(r.ReasoningContent!));
                     bubble.FinalizeReasoning(r.ReasoningTokens);
                  }
                  if (!string.IsNullOrEmpty(r.Content))
                  {
                     bubble.Append(r.Content!);
                     bubble.Flush();
                  }
                  bubble.SetTimings(TranscriptStore.TimingsFrom(r), TranscriptStore.ReadsFrom(r),
                                    r.PromptTokens, r.CachedReadTokens,
                                    r.CompletionTokens, r.ReasoningTokens, null);
               }
               var turnTiming = TranscriptStore.TimingsFrom(r);
               if (turnTiming.HasValue) _totalTimings.Add(turnTiming.Value);
               if (r.ToolCalls != null)
               {
                  foreach (var tc in r.ToolCalls)
                  {
                     toolCalls++;
                     var card = new ToolCallCard(tc.Id, tc.Name);
                     card.SetArgs(tc.ArgumentsJson);
                     _cards[tc.Id] = card;
                     _transcript.Children.Add(card.View);
                  }
               }
               outTokens  += r.CompletionTokens ?? 0;
               lastPrompt  = r.PromptTokens ?? lastPrompt;
               break;

            case SlimCodeDesktop.Agents.Native.ChatRole.Tool:
               if (!string.IsNullOrEmpty(r.ToolCallId) &&
                   _cards.TryGetValue(r.ToolCallId!, out var match))
               {
                  var content = r.Content ?? "";
                  match.SetResult(r.IsError == true
                                  ? ToolResult.Error(content)
                                  : ToolResult.Ok(content));
                  var toolTiming = TranscriptStore.TimingsFrom(r);
                  match.SetTiming(toolTiming);
                  if (toolTiming.HasValue) _totalTimings.Add(toolTiming.Value);
               }
               break;
         }
      }

      _statusTb.Text = "subagent finished (stored transcript)"
                       + " · " + toolCalls + " tool calls"
                       + " · prompt=" + lastPrompt
                       + " · output=" + outTokens + " tok"
                       + _BusySuffix();
      _latch.Arm();
   }

   /// <summary>
   /// Collapsed shows a few lines of the handover prompt; expanded gives it
   /// a tall (still scrollable) viewport so the whole task can be read.
   /// </summary>
   private void _SetTaskExpanded(bool expanded)
   {
      _taskExpanded         = expanded;
      _taskScroll.MaxHeight = expanded ? TASK_EXPANDED_HEIGHT : TASK_COLLAPSED_HEIGHT;
      _taskChevron.Text     = expanded ? "▼" : "▶";
      _taskHint.Text        = expanded ? "click to collapse" : "click to expand";
   }

   private void _Subscribe()
   {
      var loop = _run!.Loop;
      loop.TextDelta                 += _onText          = s    => _Ui(() => { _EnsureBubble().Append(s); _EnsureFlushTimer(); });
      loop.ReasoningDelta            += _onReasoning     = s    => _Ui(() => { _EnsureBubble().AppendReasoning(s); _EnsureFlushTimer(); });
      loop.ToolCallStarted           += _onCallStarted   = call => _Ui(() => _OnCallStarted(call));
      loop.ToolCallArgsDelta         += _onCallArgs      = (id, frag) => _Ui(() =>
      {
         if (!_cards.TryGetValue(id, out var card)) return;
         card.AppendArgsDelta(frag);
         if (!_streamingCards.Contains(card)) _streamingCards.Add(card);
         _EnsureFlushTimer();
      });
      loop.ToolCallReady             += _onCallReady     = call => _Ui(() =>
      {
         if (!_cards.TryGetValue(call.Id, out var card)) return;
         _streamingCards.Remove(card);
         card.SetArgs(call.ArgumentsJson);
      });
      loop.ToolCallCompleted         += _onCallCompleted = (id, result) => _Ui(() =>
      {
         if (_cards.TryGetValue(id, out var card)) card.SetResult(result);
      });
      loop.ToolTimingReported        += _onToolTiming    = (id, timing) => _Ui(() =>
      {
         if (_cards.TryGetValue(id, out var card)) card.SetTiming(timing);
      });
      loop.TurnTimingsReported       += _onTurnTimings   = t => _Ui(() =>
      {
         _totalTimings.Add(t);
         _UpdateStatus();
      });
      loop.ReasoningStatsChanged     += _onStats         = s    => _Ui(() => _EnsureBubble().SetReasoningStats(s));
      loop.Error                     += _onError         = msg  => _Ui(() => _Append(new ErrorLine(msg).View));
      loop.AssistantMessageCompleted += _onAssistantDone = msg  => _Ui(() =>
      {
         _bubble?.Flush();
         _bubble?.FinalizeReasoning(msg.ReasoningTokens);
         _bubble?.SetTimings(msg.Timings, msg.Reads, msg.PromptTokens, msg.CachedReadTokens,
                             msg.CompletionTokens, msg.ReasoningTokens, null);
         _bubble = null;
      });
      _run.Progress                  += _onProgress      = ()   => _Ui(_UpdateStatus);
      _run.Completed                 += _onCompleted     = ()   => _Ui(() =>
      {
         _bubble?.Flush();
         _UpdateStatus();
         RunCompleted?.Invoke();
      });
   }

   private void _OnCallStarted(ToolCall call)
   {
      _bubble?.Flush();
      var card = new ToolCallCard(call.Id, call.Name);
      _cards[call.Id] = card;
      _Append(card.View);
   }

   private AssistantBubble _EnsureBubble()
   {
      if (_bubble == null)
      {
         _bubble = new AssistantBubble();
         _Append(_bubble.View);
      }
      return _bubble;
   }

   // Coalesce streamed-text markdown re-parses to ~10 FPS, mirroring the
   // main chat view's flush timer (a per-token re-parse would burn CPU).
   private void _EnsureFlushTimer()
   {
      if (_flushTimer != null) return;
      _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
      _flushTimer.Tick += (_, _) =>
      {
         _bubble?.Flush();
         for (int i = _streamingCards.Count - 1; i >= 0; i--)
         {
            var card = _streamingCards[i];
            card.FlushStreaming();
            if (!card.IsStreaming) _streamingCards.RemoveAt(i);
         }
         if (_run == null || _run.IsCompleted)
         {
            _flushTimer?.Stop();
            _flushTimer = null;
         }
      };
      _flushTimer.Start();
   }

   private void _Append(Control view)
   {
      _transcript.Children.Add(view);
      _latch.ScrollToEnd();
   }

   private void _UpdateStatus()
   {
      if (_run == null) return; // replay mode sets its status once
      var state = _run.IsCompleted ? "finished" : "running";
      _statusTb.Text = "subagent " + state
                       + " · " + _run.ToolCallCount + " tool calls"
                       + " · prompt=" + _run.LastPromptTokens
                       + " · output=" + _run.TotalOutputTokens + " tok"
                       + _BusySuffix();
   }

   // " · busy 4 m 12 s (thinking 2 m 40 s · ...)" once anything was timed.
   private string _BusySuffix()
   {
      if (_totalTimings.IsEmpty) return "";
      return " · busy " + TimingViz.FormatDuration(_totalTimings.Busy)
             + " (" + TimingViz.Describe(_totalTimings) + ")";
   }

   private void _Ui(Action a)
   {
      if (_detached) return;
      if (Dispatcher.UIThread.CheckAccess()) a();
      else Dispatcher.UIThread.Post(() => { if (!_detached) a(); });
   }

   // -- fields ---------------------------------------------------------------------

   private const double TASK_COLLAPSED_HEIGHT = 90;
   private const double TASK_EXPANDED_HEIGHT  = 400;

   // Null in replay mode (stored transcript, no live run to observe).
   private readonly SubagentRun? _run;
   private readonly StackPanel  _transcript;
   private readonly ScrollViewer _scroll;
   private readonly ScrollLatch  _latch;
   private readonly TextBlock   _statusTb;
   private readonly ScrollViewer _taskScroll;
   private readonly TextBlock    _taskChevron;
   private readonly TextBlock    _taskHint;
   private bool                  _taskExpanded;

   private readonly System.Collections.Generic.Dictionary<string, ToolCallCard> _cards = new();
   private readonly List<ToolCallCard> _streamingCards = new();
   private AssistantBubble? _bubble;
   private DispatcherTimer? _flushTimer;
   private volatile bool    _detached;

   // Stored delegate instances so Detach can unsubscribe exactly what was added.
   private Action<string>?             _onText;
   private Action<string>?             _onReasoning;
   private Action<ToolCall>?           _onCallStarted;
   private Action<string, string>?     _onCallArgs;
   private Action<ToolCall>?           _onCallReady;
   private Action<string, ToolResult>? _onCallCompleted;
   private Action<ReasoningStats>?     _onStats;
   private Action<string>?             _onError;
   private Action<ChatMessage>?        _onAssistantDone;
   private Action<string, PhaseTimings>? _onToolTiming;
   private Action<PhaseTimings>?       _onTurnTimings;
   private Action?                     _onProgress;
   private Action?                     _onCompleted;

   // Busy time of the whole run (live: summed from its RunAsync; replay:
   // summed from the stored per-message timings).
   private PhaseTimings                _totalTimings;
}
