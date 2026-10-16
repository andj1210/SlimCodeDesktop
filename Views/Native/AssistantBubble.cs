// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Markdown;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Left-aligned bubble for one assistant turn. Hosts an optional
/// collapsible reasoning ("Thinking") section above the rendered
/// markdown answer.
///
/// The overthinking barometer is fed by the loop's own watch via
/// <see cref="SetReasoningStats"/> rather than recomputed here, so the live
/// reading and the loop's own never disagree. Replayed sessions have no loop,
/// so the host computes the reading once and pushes it the same way.
///
/// The host is expected to debounce <see cref="Flush"/> calls (e.g. via
/// a DispatcherTimer) -- a full markdown re-parse per token would be
/// wasteful even for short documents.
/// </summary>
public sealed class AssistantBubble : TranscriptItem
{
   public AssistantBubble()
   {
      _md = new MarkdownView
      {
         Margin = new Thickness(0),
      };

      // Raw-Markdown view of the answer. One SelectableTextBlock for the whole
      // text, so a selection can span paragraphs, tables and code -- which the
      // rendered view (one control per block) cannot do.
      _source = new SelectableTextBlock
      {
         IsVisible    = false,
         FontFamily   = new FontFamily("Consolas, Cascadia Mono, Courier New, monospace"),
         FontSize     = 12,
         TextWrapping = TextWrapping.Wrap,
         Foreground   = ThemeBrushes.TextPrimary,
      };

      _BuildReasoningSection();
      _BuildTimingStrip();
      _BuildBranchBar();

      _stack = new StackPanel { Spacing = 6 };
      _stack.Children.Add(_reasoningRoot);
      _stack.Children.Add(_md);
      _stack.Children.Add(_source);
      _stack.Children.Add(_timingRoot);
      _stack.Children.Add(_branchBar);

      // Stretch across the row's available width; keep a 30 px right
      // gutter to mirror the user bubble's LEFT_MIN so the two sides feel
      // balanced. No MaxWidth — long answers and wide code fences get the
      // full screen.
      View = new Border
      {
         Background          = ThemeBrushes.PanelBackground,
         BorderBrush         = ThemeBrushes.BorderColor,
         BorderThickness     = new Thickness(1, 0, 0, 0),
         CornerRadius        = new CornerRadius(2, 8, 8, 8),
         Padding             = new Thickness(12, 8),
         Margin              = new Thickness(0, 2, 30, 2),
         HorizontalAlignment = HorizontalAlignment.Stretch,
         Child               = _stack,
      };
   }

   /// <summary>Accumulate a streaming text fragment. Does not touch the UI.</summary>
   public void Append(string delta)
   {
      if (string.IsNullOrEmpty(delta)) return;
      _text.Append(delta);
      _dirty = true;
   }

   /// <summary>Accumulate a streaming reasoning fragment. Does not touch the UI.</summary>
   public void AppendReasoning(string delta)
   {
      if (string.IsNullOrEmpty(delta)) return;
      _reasoning.Append(delta);
      _reasoningDirty = true;
   }

   /// <summary>
   /// Hydrate this bubble from a fully replayed message. Skips the
   /// per-character accumulation/dirty tracking and renders immediately.
   /// </summary>
   public void SetReasoning(string reasoning)
   {
      if (string.IsNullOrEmpty(reasoning)) return;
      _reasoning.Clear();
      _reasoning.Append(reasoning);
      _reasoningDirty  = true;
      _bodyStructureDirty = true;
      Flush();
   }

   /// <summary>
   /// Latest overthinking reading for this turn, from the loop's watch.
   /// Replay pushes a one-shot evaluation through the same door.
   /// </summary>
   public void SetReasoningStats(ReasoningStats stats)
   {
      _stats = stats;
      _reasoningDirty = true;
   }

   /// <summary>
   /// Show what this turn cost in time and tokens: a mini phase bar with a
   /// one-line summary, and the read / written split in the tooltip. Called
   /// once the message is committed (live) or from the stored record (replay);
   /// nothing is shown until then. <paramref name="usage"/> adds the server's
   /// own timings when the provider sent them (live only).
   /// </summary>
   public void SetTimings(PhaseTimings? timings, TokenReads? reads,
                          int? promptTokens, int? cachedTokens, int? completionTokens,
                          int? reasoningTokens, LlmUsage? usage)
   {
      if (!timings.HasValue || timings.Value.IsEmpty)
      {
         _timingRoot.IsVisible = false;
         return;
      }
      var t = timings.Value;

      _timingBar.SetSegments(TimingViz.PhaseSegments(t));

      var line = TimingViz.Describe(t);
      int completion = completionTokens ?? 0;
      var rate = TimingViz.FormatRate(completion, t.Generation);
      if (rate != null)
         line += "  (" + rate + ")";
      if (reads.HasValue && !reads.Value.IsEmpty)
         line += " · read " + (reads.Value.Estimated ? "~" : "")
               + TimingViz.FormatTokens(reads.Value.Total) + " tok";
      _timingText.Text = line;

      int uncached = Math.Max(0, (promptTokens ?? 0) - (cachedTokens ?? 0));
      var tip = TimingViz.PhaseTooltip("This turn", t, TurnPhase.Idle, TimeSpan.Zero,
                                       TimingViz.Rates(t, uncached, completion, usage));
      if (reads.HasValue)
      {
         int think = reasoningTokens ?? 0;
         tip += "\n\n" + TimingViz.TokenTooltip("Tokens this turn", reads.Value,
                                                 think, Math.Max(0, completion - think));
      }
      _timingBar.SetTooltip(tip);
      ToolTip.SetTip(_timingText, TimingViz.MonoTip(tip));

      _timingRoot.IsVisible = true;
   }

   /// <summary>
   /// Raised when the user wants this turn redone in a branch: fork here and
   /// re-run it against the same preceding context.
   /// </summary>
   public event Action? BranchRequested;

   /// <summary>
   /// Position of this turn among the assistant messages committed to the
   /// transcript, or -1 while it has none. Set by the host once the message
   /// is persisted.
   ///
   /// A bubble's position in the host's list is NOT the same number: a turn
   /// that was cancelled mid-stream leaves its bubble on screen but never
   /// writes a record, so from that point on the two would disagree and a
   /// branch would fork at the wrong turn. This field is the one that maps
   /// back to the file, and -1 means "no record of this turn exists" — which
   /// the host reads as a branch point at the tail, not as "not branchable".
   /// </summary>
   public int RecordOrdinal { get; set; } = -1;

   /// <summary>
   /// Offer the branch affordance on this turn. Off by default — the host
   /// decides which turns are re-runnable; the turn currently streaming
   /// never is.
   /// </summary>
   public void EnableBranching(bool canRegenerate)
   {
      _branchRegenerate.IsVisible = canRegenerate;
      _UpdateActionBar();
   }

   /// <summary>
   /// Lock the reasoning label to its final form. <paramref name="tokens"/>
   /// is the provider-reported reasoning-token count when available; null
   /// falls back to character count of the captured reasoning text. Drops
   /// the "..." progress indicator and pins the label so subsequent
   /// flushes don't recompute it from the streaming buffer.
   /// </summary>
   public void FinalizeReasoning(int? tokens)
   {
      if (_reasoning.Length == 0) return;
      _reasoningFinalized = true;

      _reasoningFinalLabel = "Thinking ("
         + (tokens.HasValue
              ? _FormatCount(tokens.Value) + " tokens"
              : _FormatCount(_reasoning.Length) + " chars")
         + ")";

      _reasoningDirty     = true;
      _bodyStructureDirty = true;
      _RefreshReasoningView();
   }

   /// <summary>
   /// Re-render markdown and refresh the reasoning section's word count
   /// and body. Cheap no-op when clean -- safe to call from a timer tick.
   /// </summary>
   public void Flush()
   {
      if (_dirty)
      {
         var text = _text.ToString();
         _md.Markdown = text;
         if (_showSource)
            _source.Text = text;
         _dirty = false;
         _UpdateActionBar();
      }
      if (_reasoningDirty)
      {
         _RefreshReasoningView();
         _reasoningDirty = false;
      }
   }

   /// <summary>True if at least one Append happened (used by host to skip empty bubbles).</summary>
   public bool HasContent => _text.Length > 0 || _reasoning.Length > 0;

   // -- reasoning section ----------------------------------------------------

   private void _BuildReasoningSection()
   {
      _reasoningChevron = new TextBlock
      {
         Text              = "▶",
         FontSize          = 9,
         VerticalAlignment = VerticalAlignment.Center,
         Width             = 12,
         Foreground        = ThemeBrushes.TextMuted,
         Margin            = new Thickness(0, 0, 4, 0),
      };
      _reasoningLabel = new TextBlock
      {
         Text              = "Thinking",
         FontStyle         = FontStyle.Italic,
         FontSize          = 11,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
      };

      _meter = new ReasoningMeter();

      var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
      headerStack.Children.Add(_reasoningChevron);
      headerStack.Children.Add(_reasoningLabel);
      headerStack.Children.Add(_meter);

      _reasoningHeader = new Border
      {
         Background = Brushes.Transparent,
         Padding    = new Thickness(0),
         Cursor     = new Cursor(StandardCursorType.Hand),
         Child      = headerStack,
      };
      _reasoningHeader.PointerPressed += (_, _) => _SetReasoningExpanded(!_reasoningExpanded);

      _bodyStack = new StackPanel { Spacing = 0 };

      _reasoningBodyHost = new Border
      {
         IsVisible       = false,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1, 0, 0, 0),
         Padding         = new Thickness(6, 2, 0, 2),
         Margin          = new Thickness(0, 2, 0, 0),
         Child           = _bodyStack,
      };
      // Double-click anywhere in the body collapses the section — useful
      // when a runaway reasoning dump makes the chevron hard to find.
      _reasoningBodyHost.DoubleTapped += (_, _) =>
      {
         if (_reasoningExpanded) _SetReasoningExpanded(false);
      };

      _reasoningRoot = new StackPanel
      {
         IsVisible = false,
         Spacing   = 0,
      };
      _reasoningRoot.Children.Add(_reasoningHeader);
      _reasoningRoot.Children.Add(_reasoningBodyHost);
   }

   // -- timing strip ---------------------------------------------------------

   // A muted footer line under the answer: the phase bar at bubble scale and
   // the same figures in words. Hidden until the turn is committed, so a
   // streaming bubble does not show a bar that is not yet true.
   private void _BuildTimingStrip()
   {
      _timingBar  = new SegmentBar(72, 5)
      {
         Margin = new Thickness(0, 0, 8, 0),
      };
      _timingText = new TextBlock
      {
         FontSize          = 10,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
         TextTrimming      = TextTrimming.CharacterEllipsis,
      };

      var row = new StackPanel { Orientation = Orientation.Horizontal };
      row.Children.Add(_timingBar);
      row.Children.Add(_timingText);

      _timingRoot = new Border
      {
         IsVisible = false,
         Margin    = new Thickness(0, 2, 0, 0),
         Child     = row,
      };
   }

   // -- branch bar -----------------------------------------------------------

   // A muted footer row rather than an icon in the header: the action throws
   // away everything below this turn, so it should read as a deliberate
   // choice, not as something to hit while scrolling.
   private void _BuildBranchBar()
   {
      _branchRegenerate = _NewBranchLink(
         "⑂ regenerate",
         "Branch here and re-run this turn from the same context. "
         + "The current version is kept in the session you are leaving.",
         () => BranchRequested?.Invoke());
      _copyLink = _NewBranchLink(
         "⧉ copy",
         "Copy this answer as Markdown source.",
         _CopyAnswer);
      _sourceLink = _NewBranchLink(
         "‹/› source",
         "Show the raw Markdown. Unlike the rendered view, it can be selected "
         + "across paragraphs, tables and code blocks.",
         _ToggleSource);

      var row = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         Spacing     = 12,
      };
      row.Children.Add(_copyLink);
      row.Children.Add(_sourceLink);
      row.Children.Add(_branchRegenerate);

      _branchBar = new Border
      {
         IsVisible       = false,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 1, 0, 0),
         Padding         = new Thickness(0, 4, 0, 0),
         Margin          = new Thickness(0, 2, 0, 0),
         Child           = row,
      };
   }

   private static TextBlock _NewBranchLink(string text, string tip, Action onClick)
   {
      var link = new TextBlock
      {
         Text       = text,
         FontSize   = 10,
         FontStyle  = FontStyle.Italic,
         Foreground = ThemeBrushes.TextMuted,
         Cursor     = new Cursor(StandardCursorType.Hand),
         IsVisible  = false,
      };
      ToolTip.SetTip(link, tip);
      link.PointerEntered += (_, _) => link.Foreground = ThemeBrushes.Accent;
      link.PointerExited  += (_, _) => link.Foreground = ThemeBrushes.TextMuted;
      link.PointerPressed += (_, e) =>
      {
         e.Handled = true;
         onClick();
      };
      return link;
   }

   // Copy / source only make sense once there is answer text; regenerate is
   // governed by EnableBranching. The bar shows when any of them does.
   private void _UpdateActionBar()
   {
      bool hasText = _text.Length > 0;
      _copyLink.IsVisible   = hasText;
      _sourceLink.IsVisible = hasText;
      _branchBar.IsVisible  = hasText || _branchRegenerate.IsVisible;
   }

   private async void _CopyAnswer()
   {
      var clipboard = TopLevel.GetTopLevel(View)?.Clipboard;
      if (clipboard == null) return;
      try
      {
         await clipboard.SetTextAsync(_text.ToString());
         _copyLink.Text = "✓ copied";
         DispatcherTimer.RunOnce(() => _copyLink.Text = "⧉ copy", TimeSpan.FromSeconds(1.5));
      }
      catch { }
   }

   private void _ToggleSource()
   {
      _showSource = !_showSource;
      if (_showSource)
         _source.Text = _text.ToString();
      _source.IsVisible = _showSource;
      _md.IsVisible     = !_showSource;
      _sourceLink.Text  = _showSource ? "‹/› rendered" : "‹/› source";
   }

   private void _SetReasoningExpanded(bool expanded)
   {
      _reasoningExpanded = expanded;
      _reasoningBodyHost.IsVisible = expanded;
      _reasoningChevron.Text       = expanded ? "▼" : "▶";
      _bodyStructureDirty          = true;
      _RefreshReasoningView();
   }

   private void _RefreshReasoningView()
   {
      if (_reasoning.Length == 0)
      {
         _reasoningRoot.IsVisible = false;
         return;
      }
      _reasoningRoot.IsVisible = true;

      // Once finalized, the label is frozen — don't recompute from the
      // (now-stable) buffer on every flush.
      _reasoningLabel.Text = _reasoningFinalized ? _reasoningFinalLabel : _BuildLabelText();

      _meter.SetStats(_stats);

      if (!_reasoningExpanded)
         return;

      if (_bodyStructureDirty)
      {
         _RebuildBody();
         _bodyStructureDirty = false;
         return;
      }

      // Streaming path: only the trailing run's text grows, so re-set that
      // one MarkdownView instead of rebuilding the stack per flush.
      if (_bodyStack.Children.Count > 0 &&
          _bodyStack.Children[^1] is MarkdownView tailRun)
      {
         tailRun.Markdown = _reasoning.ToString();
      }
      else
      {
         _RebuildBody();
      }
   }

   private void _RebuildBody()
   {
      _bodyStack.Children.Clear();
      _bodyStack.Children.Add(_NewRun(_reasoning.ToString()));
   }

   private static MarkdownView _NewRun(string text) => new()
   {
      Markdown       = text,
      Margin         = new Thickness(0),
      // Fluent's implicit TextBlock style hardcodes a foreground that beats
      // normal property inheritance; TextForeground forces the muted brush
      // onto each rendered paragraph / heading / list item.
      TextForeground = ThemeBrushes.TextMuted,
   };

   private static string _FormatCount(int n)
   {
      if (n < 1000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
      double k = n / 1000.0;
      return k.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
   }

   // Word count formatted as "Thinking (0.3k...)" collapsed,
   // "Thinking (0.3k)" expanded -- the ellipsis hints that more content
   // lives behind the chevron.
   private string _BuildLabelText()
   {
      int words = _CountWords(_reasoning);
      double k   = words / 1000.0;
      // Always show k-style with one decimal -- matches the user-requested
      // "0.3k" format and stays compact for big chains too.
      var num = k.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
      return _reasoningExpanded
         ? "Thinking (" + num + ")"
         : "Thinking (" + num + "...)";
   }

   private static int _CountWords(StringBuilder sb)
   {
      int count = 0;
      bool inWord = false;
      for (int i = 0; i < sb.Length; i++)
      {
         char c = sb[i];
         bool ws = char.IsWhiteSpace(c);
         if (!ws && !inWord)
         {
            count++;
            inWord = true;
         }
         else if (ws)
         {
            inWord = false;
         }
      }
      return count;
   }

   // -- fields ---------------------------------------------------------------

   private readonly MarkdownView    _md;
   private readonly SelectableTextBlock _source;
   private readonly StackPanel      _stack;
   private readonly StringBuilder   _text = new();
   private readonly StringBuilder   _reasoning = new();
   private bool                     _dirty;
   private bool                     _reasoningDirty;

   // Reasoning section widgets — built once in ctor, populated lazily.
   private TextBlock                _reasoningChevron = null!;
   private TextBlock                _reasoningLabel   = null!;
   private Border                   _reasoningHeader  = null!;
   private StackPanel               _bodyStack        = null!;
   private Border                   _reasoningBodyHost= null!;
   private StackPanel               _reasoningRoot    = null!;
   private ReasoningMeter           _meter            = null!;

   // Timing footer — built once, revealed by SetTimings.
   private Border                   _timingRoot       = null!;
   private SegmentBar               _timingBar        = null!;
   private TextBlock                _timingText       = null!;

   // Branch footer — built once, revealed by EnableBranching.
   private Border                   _branchBar        = null!;
   private TextBlock                _branchRegenerate = null!;
   private TextBlock                _copyLink         = null!;
   private TextBlock                _sourceLink       = null!;
   private bool                     _showSource;

   private bool                     _reasoningExpanded;
   private bool                     _reasoningFinalized;
   private bool                     _bodyStructureDirty = true;
   private string                   _reasoningFinalLabel = "";

   private ReasoningStats _stats;
}
