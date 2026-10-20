// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Collapsible inline card representing one tool call within an assistant
/// turn. Lifecycle: <c>Pending</c> (announced, args still streaming) →
/// <c>Streaming</c> (arguments arriving, rendered live) → <c>Ready</c> (args
/// complete, about to execute) → <c>Done</c> / <c>Failed</c>. Use the setter
/// methods to advance state; the visual updates in place.
/// </summary>
public sealed class ToolCallCard : TranscriptItem
{
   public enum Status { Pending, Streaming, Ready, Done, Failed }

   public string ToolCallId { get; }
   public string ToolName   { get; }

   /// <summary>
   /// Group this card was reparented into, if any. Set by
   /// <see cref="ToolCallGroupCard.AddCard"/> so a card that starts streaming
   /// can reveal itself — a collapsed group would otherwise hide the live view
   /// that is the whole point.
   /// </summary>
   internal ToolCallGroupCard? Group { get; set; }

   /// <param name="tool">Optional matching registry tool; when present its
   /// ArgsPreview supplies the human-readable header summary instead of the
   /// raw args JSON.</param>
   public ToolCallCard(string id, string toolName, ITool? tool = null)
   {
      ToolCallId = id;
      ToolName   = toolName ?? "";
      _tool      = tool;

      // -- header ------------------------------------------------------------
      _iconTb = new TextBlock
      {
         Text              = "⏳",
         FontSize          = 11,
         VerticalAlignment = VerticalAlignment.Center,
         Width             = 14,
         Foreground        = ThemeBrushes.TextMuted,
      };
      // Per-tool color coding: at-a-glance signal of risk class while
      // scanning the transcript. Tool families share a hue so unfamiliar
      // tools fall into a sensible bucket via the default branch.
      var nameTb = new TextBlock
      {
         FontFamily        = MONO_FONT,
         FontWeight        = FontWeight.Bold,
         FontSize          = 11,
         Foreground        = ToolNameBrush(ToolName),
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(2, 0, 0, 0),
      };
      // An MCP tool arrives as "<server>__<tool>". The server is context, not
      // identity: scanning a transcript you are looking for which tool ran, so
      // the prefix is dimmed and the tool name keeps the emphasis.
      int split = ToolName.IndexOf(MCP_PREFIX_SEPARATOR, StringComparison.Ordinal);
      if (split > 0)
      {
         nameTb.Inlines = new InlineCollection
         {
            new Run(ToolName.Substring(0, split) + MCP_PREFIX_SEPARATOR)
            {
               Foreground = ThemeBrushes.TextMuted,
               FontWeight = FontWeight.Normal,
            },
            new Run(ToolName.Substring(split + MCP_PREFIX_SEPARATOR.Length)),
         };
      }
      else
      {
         nameTb.Text = ToolName;
      }
      _argsPreview = new TextBlock
      {
         FontFamily        = MONO_FONT,
         FontSize          = 10,
         Foreground        = Brushes.White,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(8, 0, 0, 0),
         TextTrimming      = TextTrimming.CharacterEllipsis,
      };
      // Chevron rotates between ▶ (collapsed) and ▼ (expanded).
      _chevron = new TextBlock
      {
         Text              = "▶",
         FontSize          = 9,
         VerticalAlignment = VerticalAlignment.Center,
         Width             = 12,
         Foreground        = ThemeBrushes.TextMuted,
         Margin            = new Thickness(0, 0, 4, 0),
      };

      // Duration, right of the preview. Empty until the call completes.
      _timingTb = new TextBlock
      {
         FontSize          = 10,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(8, 0, 0, 0),
         IsVisible         = false,
      };

      _headerGrid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
      };
      Grid.SetColumn(_chevron,      0);
      Grid.SetColumn(_iconTb,       1);
      Grid.SetColumn(nameTb,         2);
      Grid.SetColumn(_argsPreview,  3);
      Grid.SetColumn(_timingTb,     4);
      _headerGrid.Children.Add(_chevron);
      _headerGrid.Children.Add(_iconTb);
      _headerGrid.Children.Add(nameTb);
      _headerGrid.Children.Add(_argsPreview);
      _headerGrid.Children.Add(_timingTb);

      // Header is a Border so it can carry the click handler. Background is
      // Transparent — the outer card colour shows through. PointerPressed
      // toggles the body below.
      _header = new Border
      {
         Background = Brushes.Transparent,
         Padding    = new Thickness(6, 3),
         Cursor     = new Cursor(StandardCursorType.Hand),
         Child      = _headerGrid,
      };
      _header.PointerPressed += (_, _) =>
      {
         _userToggled = true;
         SetExpanded(!_expanded);
      };

      // -- expanded body (args + result) ------------------------------------
      _argsTb   = MonoBlock();
      _resultTb = MonoBlock();

      var bodyPanel = new StackPanel { Spacing = 4 };
      _argsLabel = SectionLabel("args");
      bodyPanel.Children.Add(_argsLabel);
      bodyPanel.Children.Add(WrapScroll(_argsTb, 240, out _argsScroll));
      bodyPanel.Children.Add(SectionLabel("result"));
      bodyPanel.Children.Add(WrapScroll(_resultTb, 240, out _));

      _body = new Border
      {
         Background      = Brushes.Transparent,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 1, 0, 0),
         Padding         = new Thickness(8, 6),
         IsVisible       = false,
         Child           = bodyPanel,
      };
      // Double-click the body to collapse — gives the user an easy
      // way out when a huge args / result dump pushes the header off-screen.
      _body.DoubleTapped += (_, _) =>
      {
         if (!_expanded) return;
         _userToggled = true;
         SetExpanded(false);
      };

      // Images the call handed the model (read_image). Outside the body so
      // they stay visible on a collapsed card: what the model looked at is
      // the point of the call.
      _imagesHost = new ContentControl
      {
         Margin    = new Thickness(8, 0, 8, 0),
         IsVisible = false,
      };

      var stack = new StackPanel();
      stack.Children.Add(_header);
      stack.Children.Add(_imagesHost);
      stack.Children.Add(_body);

      // Left-aligned + same MaxWidth as AssistantBubble so tool cards
      // anchor under the model's reply text. Card background matches the
      // chat surface; the thin border is the only visual separator.
      View = new Border
      {
         Background          = ThemeBrushes.WindowBackground,
         BorderBrush         = ThemeBrushes.BorderColor,
         BorderThickness     = new Thickness(1),
         CornerRadius        = new CornerRadius(3),
         Margin              = new Thickness(0, 3, 60, 3),
         HorizontalAlignment = HorizontalAlignment.Left,
         MaxWidth            = 900,
         Child               = stack,
      };
   }

   private void SetExpanded(bool expanded)
   {
      _expanded = expanded;
      _body.IsVisible = expanded;
      _chevron.Text   = expanded ? "▼" : "▶";
   }

   // -- per-tool name color --------------------------------------------------

   internal static IBrush ToolNameBrush(string toolName) => toolName switch
   {
      "read_file" => s_green,
      "read_image"=> s_green,
      "list_dir"  => s_green,
      "glob"      => s_cyan,
      "grep"      => s_cyan,
      "write_file"=> s_yellow,
      "edit_file" => s_yellow,
      "move_file" => s_yellow,
      "copy_file" => s_yellow,
      "delete_file"=> s_red,
      "bash"      => s_red,
      "build"     => s_orange,
      "test"      => s_blue,
      "set_tasks" => s_magenta,
      "use_skill" => s_teal,
      // "start_subagent" is the read tool's former name; still colored so
      // pre-rename transcripts replay unchanged.
      "start_read_subagent" => s_teal,
      "start_subagent"      => s_teal,
      // Write subagent reads as a write, not as research — it can touch the tree.
      "start_write_subagent" => s_yellow,
      // An MCP tool has no fixed vocabulary to color by — every server names
      // its own. One hue for all of them at least says "this came from a
      // server", which is the distinction that matters in the transcript.
      _ => toolName.Contains(MCP_PREFIX_SEPARATOR, StringComparison.Ordinal)
           ? s_teal
           : ThemeBrushes.TextPrimary,
   };

   /// <summary>
   /// Separator between an MCP server's prefix and the tool name. Mirrors
   /// <c>McpServerHandle.NAME_SEPARATOR</c>; kept as a literal here so the
   /// view layer doesn't take a dependency on the MCP client to render a name.
   /// </summary>
   private const string MCP_PREFIX_SEPARATOR = "__";

   private static readonly SolidColorBrush s_green   = new(Color.FromRgb(0x6A, 0xC9, 0x6A));
   private static readonly SolidColorBrush s_cyan    = new(Color.FromRgb(0x4E, 0xC9, 0xD1));
   private static readonly SolidColorBrush s_yellow  = new(Color.FromRgb(0xE8, 0xC8, 0x55));
   private static readonly SolidColorBrush s_red     = new(Color.FromRgb(0xE0, 0x6C, 0x6C));
   private static readonly SolidColorBrush s_orange  = new(Color.FromRgb(0xE0, 0x8C, 0x3C));
   private static readonly SolidColorBrush s_blue    = new(Color.FromRgb(0x5C, 0x9C, 0xE0));
   private static readonly SolidColorBrush s_magenta = new(Color.FromRgb(0xC8, 0x88, 0xD4));
   private static readonly SolidColorBrush s_teal    = new(Color.FromRgb(0x5A, 0xD0, 0xB0));

   // -- live argument streaming ----------------------------------------------

   /// <summary>
   /// Feed one raw fragment of this call's <c>arguments</c> JSON as it arrives
   /// from the model. Decoding happens here; painting is deferred to
   /// <see cref="FlushStreaming"/> so a 40 kB file doesn't trigger a relayout
   /// per token. No-op once <see cref="SetArgs"/> has delivered the final args.
   /// </summary>
   public void AppendArgsDelta(string fragment)
   {
      if (_argsSettled || string.IsNullOrEmpty(fragment))
         return;

      if (_stream == null)
      {
         var keys = StreamKeysFor(ToolName);
         _multiKey = keys.Length > 1;
         _stream   = new ToolArgsStream(keys);
      }
      _stream.Feed(fragment, _onStreamText ??= AppendStreamText);
      _streamDirty = true;
   }

   /// <summary>True while this card is still rendering streamed arguments.</summary>
   public bool IsStreaming => _stream != null && !_argsSettled;

   /// <summary>
   /// Paint whatever has accumulated since the last call. Driven by the host's
   /// flush timer at the same cadence as streamed assistant text; cheap no-op
   /// when nothing changed.
   /// </summary>
   public void FlushStreaming()
   {
      if (!_streamDirty || _stream == null || _argsSettled)
         return;
      _streamDirty = false;

      var path = _stream.GetValue("path");

      // Header. Knowing WHICH file is being written lands within the first
      // fragment or two — long before the body means anything — and is most
      // of what makes the wait legible.
      if (!string.IsNullOrEmpty(path))
      {
         _argsPreview.Text = _stream.StreamedChars > 0
            ? "path=" + path + ", " + ToolPreview.FormatBytes(_stream.StreamedChars) + " so far…"
            : "path=" + path;
      }
      else if (_stream.RawChars >= QUIET_ARGS_CHARS)
      {
         // No path (bash, grep, …). Only speak up once the call is big enough
         // that the silence would be confusing on its own.
         _argsPreview.Text = "receiving arguments… " + ToolPreview.FormatBytes(_stream.RawChars);
      }

      if (_tail.Length == 0)
         return;

      // Reveal on the first body text — this is the file appearing as the
      // model writes it, and it is worthless behind a closed chevron.
      if (!_autoExpanded && !_expanded && !_userToggled)
      {
         _autoExpanded = true;
         SetExpanded(true);
         Group?.BeginLiveCard();
      }

      SetStatus(Status.Streaming);
      _argsLabel.Text = OneLine(
         (_lastStreamKey.Length > 0 ? _lastStreamKey : "arguments") + " (streaming)"
         + (string.IsNullOrEmpty(path) ? "" : " - " + path), 100);
      _argsTb.Text = _tailTrimmed ? "…\n" + _tail.ToString() : _tail.ToString();
      _argsScroll.ScrollToEnd();
   }

   // Which argument holds the bulk text worth watching, per tool. Everything
   // else still gets a parsed `path` in the header but no body view.
   private static string[] StreamKeysFor(string toolName) => toolName switch
   {
      "write_file" => new[] { "content" },
      "edit_file"  => new[] { "old_string", "new_string" },
      _            => Array.Empty<string>(),
   };

   // Appends decoded text to the bounded live-view buffer. Only a tail window
   // is kept: the full content arrives via SetArgs moments later anyway, and
   // an unbounded buffer would make every flush proportional to file size.
   private void AppendStreamText(string key, string text)
   {
      if (_multiKey && key != _lastStreamKey)
      {
         if (_tail.Length > 0) _tail.Append('\n');
         _tail.Append("-- ").Append(key).Append(" --\n");
      }
      _lastStreamKey = key;
      _tail.Append(text);

      if (_tail.Length > TAIL_MAX)
      {
         _tail.Remove(0, _tail.Length - TAIL_MAX);
         _tailTrimmed = true;
      }
   }

   // -- state transitions ----------------------------------------------------

   public void SetArgs(string argsJson)
   {
      // The complete arguments supersede the live view; drop its buffers so a
      // long write doesn't hold a second copy of the tail for the session.
      _argsSettled = true;
      _streamDirty = false;
      _tail.Clear();

      _argsTb.Text = string.IsNullOrEmpty(argsJson) ? "(none)" : argsJson;
      _basePreview = HeaderPreview(argsJson);
      _argsPreview.Text = _basePreview;
      _argsLabel.Text = "args";
      SpecializeArgsView(argsJson);
      SetStatus(Status.Ready);
   }

   /// <summary>
   /// Append a small action button at the right edge of the header (e.g.
   /// a subagent card's "view" button that opens the viewer tab). The press
   /// is swallowed so clicking the button doesn't also toggle the body.
   /// </summary>
   public void AddHeaderAction(string text, string tooltip, Action onClick)
   {
      var btn = new Button
      {
         Content           = text,
         FontSize          = 10,
         Padding           = new Thickness(8, 1),
         Margin            = new Thickness(6, 0, 0, 0),
         VerticalAlignment = VerticalAlignment.Center,
      };
      ToolTip.SetTip(btn, tooltip);
      btn.PointerPressed += (_, e) => e.Handled = true;
      btn.Click += (_, _) => onClick();

      _headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
      Grid.SetColumn(btn, _headerGrid.ColumnDefinitions.Count - 1);
      _headerGrid.Children.Add(btn);
   }

   /// <summary>
   /// Live progress line for long-running calls (subagents): appended
   /// after the args preview in the header. Cleared implicitly by the next
   /// <see cref="SetArgs"/>; the final counts stay visible after completion.
   /// </summary>
   public void SetProgress(string text)
   {
      _argsPreview.Text = string.IsNullOrEmpty(_basePreview)
         ? text
         : _basePreview + "  —  " + text;
   }

   /// <summary>
   /// Show how long the call took. Execution and the wait on a permission
   /// prompt are kept apart: "12 ms" for a plain call, "1.2 s · waited 48 s"
   /// when the user had to approve it first.
   /// </summary>
   public void SetTiming(PhaseTimings? timings)
   {
      if (!timings.HasValue || timings.Value.IsEmpty)
      {
         _timingTb.IsVisible = false;
         return;
      }
      var t = timings.Value;
      var text = TimingViz.FormatDuration(t.ToolExec);
      if (t.PermissionWait > TimeSpan.Zero)
         text += " · waited " + TimingViz.FormatDuration(t.PermissionWait);
      _timingTb.Text      = text;
      _timingTb.IsVisible = true;
      ToolTip.SetTip(_timingTb, TimingViz.MonoTip(
         "tool exec        " + TimingViz.FormatDuration(t.ToolExec).PadLeft(11)
         + (t.PermissionWait > TimeSpan.Zero
               ? "\npermission wait  " + TimingViz.FormatDuration(t.PermissionWait).PadLeft(11)
               : "")));
   }

   public void SetResult(ToolResult result)
   {
      _resultTb.Text = string.IsNullOrEmpty(result.Content) ? "(no output)" : result.Content;
      SetStatus(result.IsError ? Status.Failed : Status.Done);

      // Auto-expand failed calls so the user sees the error without an extra click.
      if (result.IsError)
      {
         SetExpanded(true);
         return;
      }

      // The live view opened itself; close it again now that the call is done,
      // or a run of streamed writes leaves the transcript full of open cards.
      // A manual toggle at any point takes the card out of this contract.
      if (_autoExpanded && !_userToggled)
      {
         _autoExpanded = false;
         SetExpanded(false);
         Group?.EndLiveCard();
      }
   }

   /// <summary>Show the images this call delivered to the model as thumbnails.</summary>
   public void SetImages(IReadOnlyList<ImagePart> images, Action<ImagePart>? onPreview)
   {
      if (images == null || images.Count == 0)
      {
         _imagesHost.Content   = null;
         _imagesHost.IsVisible = false;
         return;
      }
      _imagesHost.Content   = UserBubble.BuildThumbnails(images, onPreview);
      _imagesHost.IsVisible = true;
   }

   private void SetStatus(Status s)
   {
      (_iconTb.Text, _iconTb.Foreground) = s switch
      {
         Status.Pending   => ("⏳", ThemeBrushes.TextMuted),
         Status.Streaming => ("✎",  s_yellow),
         Status.Ready     => ("▶",  ThemeBrushes.Accent),
         Status.Done      => ("✓",  ThemeBrushes.Accent),
         Status.Failed    => ("✗",  Brushes.IndianRed),
         _                => ("?",  ThemeBrushes.TextMuted),
      };
   }

   // -- args presentation ----------------------------------------------------

   // Prefer the tool's own one-line summary (e.g. "path=…, old=(n chars)…")
   // over truncated raw JSON. Falls back to the JSON when no tool is known
   // or the args don't parse (mid-stream / malformed).
   private string HeaderPreview(string argsJson)
   {
      if (_tool != null && !string.IsNullOrEmpty(argsJson))
      {
         try
         {
            using var doc = JsonDocument.Parse(argsJson);
            return OneLine(_tool.ArgsPreview(doc.RootElement), 120);
         }
         catch { }
      }
      return OneLine(argsJson, 120);
   }

   // Tool-specific expanded args view: edit_file renders a line diff,
   // write_file the file content as plain text. Everything else (and any
   // parse failure) keeps the raw JSON set in SetArgs.
   private void SpecializeArgsView(string argsJson)
   {
      if (string.IsNullOrEmpty(argsJson)) return;
      try
      {
         if (ToolName == "edit_file")
         {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            string path = root.TryGetProperty("path",       out var p) ? p.GetString() ?? "" : "";
            string oldS = root.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : "";
            string newS = root.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "";
            RenderDiff(path, oldS, newS);
         }
         else if (ToolName == "write_file")
         {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            string path    = root.TryGetProperty("path",    out var p) ? p.GetString() ?? "" : "";
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            _argsLabel.Text = OneLine("content - " + path, 100);
            _argsTb.Text    = content.Length == 0 ? "(empty file)" : content;
         }
      }
      catch { }
   }

   private void RenderDiff(string path, string oldS, string newS)
   {
      _argsLabel.Text = OneLine("diff - " + path, 100);
      _argsTb.Text = string.Empty;
      var inlines = _argsTb.Inlines;
      if (inlines == null) return;
      inlines.Clear();

      // Consecutive lines with the same op merge into one Run; per-line Runs
      // would bloat the inline tree on big edits for no visual gain.
      var sb = new System.Text.StringBuilder();
      LineDiff.Op? runOp = null;

      void Flush()
      {
         if (runOp == null || sb.Length == 0) return;
         var brush = runOp switch
         {
            LineDiff.Op.Removed => s_red,
            LineDiff.Op.Added   => s_green,
            _                   => ThemeBrushes.TextLog,
         };
         inlines.Add(new Run(sb.ToString()) { Foreground = brush });
         sb.Clear();
      }

      bool first = true;
      foreach (var (op, line) in LineDiff.Compute(oldS, newS))
      {
         if (op != runOp)
         {
            Flush();
            runOp = op;
         }
         if (!first) sb.Append('\n');
         first = false;
         sb.Append(op switch
         {
            LineDiff.Op.Removed => "- ",
            LineDiff.Op.Added   => "+ ",
            _                   => "  ",
         });
         sb.Append(line);
      }
      Flush();
   }

   // -- helpers --------------------------------------------------------------

   private static SelectableTextBlock MonoBlock() => new()
   {
      FontFamily   = MONO_FONT,
      FontSize     = 11,
      TextWrapping = TextWrapping.NoWrap,
      Foreground   = ThemeBrushes.TextLog,
   };

   private static TextBlock SectionLabel(string s) => new()
   {
      Text       = s,
      FontSize   = 10,
      Foreground = ThemeBrushes.TextMuted,
      Margin     = new Thickness(0, 4, 0, 2),
   };

   private static Control WrapScroll(Control inner, double maxHeight, out ScrollViewer scroll)
   {
      // Wrap the inner content in a Border whose Padding reserves space
      // for Avalonia's overlay scrollbars on hover. Without this, the
      // expanded horizontal scrollbar lays over the last line of text and
      // the vertical one clips the rightmost columns. ~20 px on each
      // affected edge clears both cases.
      var inset = new Border
      {
         Padding = new Thickness(0, 0, 20, 20),
         Child   = inner,
      };
      scroll = new ScrollViewer
      {
         HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
         VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
         MaxHeight                     = maxHeight,
         Content                       = inset,
      };
      return new Border
      {
         Background      = ThemeBrushes.WindowBackground,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1),
         CornerRadius    = new CornerRadius(3),
         Padding         = new Thickness(6, 4),
         Child           = scroll,
      };
   }

   private static string OneLine(string s, int max)
   {
      if (string.IsNullOrEmpty(s)) return "";
      var flat = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
      while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
      return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
   }

   // -- fields ---------------------------------------------------------------

   private static readonly FontFamily MONO_FONT =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   // Live-view tail window. Bounded so flush cost stays constant no matter how
   // large the file is; the full text lands via SetArgs when the call closes.
   private const int TAIL_MAX = 16384;

   // Below this, a tool call's arguments are short enough that the header stays
   // quiet rather than flashing a byte counter for a fraction of a second.
   private const int QUIET_ARGS_CHARS = 256;

   private readonly TextBlock            _iconTb;
   private readonly TextBlock            _chevron;
   private readonly TextBlock            _argsPreview;
   private readonly TextBlock            _timingTb;
   private readonly TextBlock            _argsLabel;
   private readonly SelectableTextBlock  _argsTb;
   private readonly SelectableTextBlock  _resultTb;
   private readonly ScrollViewer         _argsScroll;
   private readonly Grid                 _headerGrid;
   private readonly Border               _header;
   private readonly Border               _body;
   private readonly ContentControl       _imagesHost;
   private readonly ITool?               _tool;
   private bool                          _expanded;
   private string                        _basePreview = "";

   // -- streaming state ------------------------------------------------------

   private readonly StringBuilder        _tail = new();
   private ToolArgsStream?               _stream;
   private Action<string, string>?       _onStreamText;
   private string                        _lastStreamKey = "";
   private bool                          _multiKey;
   private bool                          _tailTrimmed;
   private bool                          _streamDirty;
   private bool                          _argsSettled;
   private bool                          _autoExpanded;
   private bool                          _userToggled;
}
