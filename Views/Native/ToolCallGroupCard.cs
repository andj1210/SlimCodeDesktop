// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Collapsible container for a run of consecutive <see cref="ToolCallCard"/>s.
/// Created retroactively once 3+ tool calls have accumulated without an
/// intervening assistant message, error, or user turn. The header shows a
/// rolled-up summary (e.g. "3x read_file, 2x edit_file"); the body holds
/// the individual cards, each still independently expandable.
/// </summary>
public sealed class ToolCallGroupCard : TranscriptItem
{
   public ToolCallGroupCard()
   {
      _chevron = new TextBlock
      {
         Text              = "▶",
         FontSize          = 9,
         VerticalAlignment = VerticalAlignment.Center,
         Width             = 12,
         Foreground        = ThemeBrushes.TextMuted,
         Margin            = new Thickness(0, 0, 4, 0),
      };
      _label = new TextBlock
      {
         Text              = "tool calls",
         FontFamily        = MONO_FONT,
         FontWeight        = FontWeight.Bold,
         FontSize          = 11,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
      };
      _summary = new TextBlock
      {
         Text              = "",
         FontFamily        = MONO_FONT,
         FontSize          = 10,
         Foreground        = Brushes.White,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(8, 0, 0, 0),
         TextTrimming      = TextTrimming.CharacterEllipsis,
      };

      var headerGrid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
      };
      Grid.SetColumn(_chevron, 0);
      Grid.SetColumn(_label,   1);
      Grid.SetColumn(_summary, 2);
      headerGrid.Children.Add(_chevron);
      headerGrid.Children.Add(_label);
      headerGrid.Children.Add(_summary);

      _header = new Border
      {
         Background = Brushes.Transparent,
         Padding    = new Thickness(6, 3),
         Cursor     = new Cursor(StandardCursorType.Hand),
         Child      = headerGrid,
      };
      _header.PointerPressed += (_, _) =>
      {
         _userToggled = true;
         SetExpanded(!_expanded);
      };

      // Body hosts the individual ToolCallCard views, stacked vertically.
      _bodyPanel = new StackPanel { Margin = new Thickness(8, 4, 4, 4) };
      _body = new Border
      {
         Background      = Brushes.Transparent,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 1, 0, 0),
         Padding         = new Thickness(0),
         IsVisible       = false,
         Child           = _bodyPanel,
      };
      // Double-click anywhere in the group body collapses the whole group.
      // Individual ToolCallCards inside still handle their own double-click
      // for self-collapse; that fires first and stops propagation, so a
      // double-click on an expanded inner card collapses just that card
      // rather than the entire group.
      _body.DoubleTapped += (_, _) =>
      {
         if (!_expanded) return;
         _userToggled = true;
         SetExpanded(false);
      };

      var stack = new StackPanel();
      stack.Children.Add(_header);
      stack.Children.Add(_body);

      View = new Border
      {
         Background          = ThemeBrushes.WindowBackground,
         BorderBrush         = ThemeBrushes.BorderColor,
         BorderThickness     = new Thickness(1),
         CornerRadius        = new CornerRadius(3),
         Margin              = new Thickness(0, 3, 60, 3),
         HorizontalAlignment = HorizontalAlignment.Left,
         MaxWidth            = 920,
         Child               = stack,
      };
   }

   public IReadOnlyList<ToolCallCard> Cards => _cards;

   /// <summary>Adopt a card whose View is currently parented elsewhere. Caller
   /// must remove the card from its previous parent first.</summary>
   public void AddCard(ToolCallCard card)
   {
      _cards.Add(card);
      card.Group = this;
      _bodyPanel.Children.Add(card.View);
      _UpdateSummary();
   }

   /// <summary>
   /// A contained card started rendering streamed arguments. The group opens
   /// so the live view is actually visible — a write_file landing as card 4 of
   /// a run would otherwise stream into a collapsed container.
   /// </summary>
   internal void BeginLiveCard()
   {
      _liveCards++;
      if (!_expanded)
      {
         _autoExpanded = true;
         SetExpanded(true);
      }
   }

   /// <summary>
   /// A contained card finished streaming. Collapses again once the last one
   /// is done — counted rather than paired one-to-one so a run of streamed
   /// writes doesn't flicker the group shut between calls.
   /// </summary>
   internal void EndLiveCard()
   {
      if (_liveCards > 0) _liveCards--;
      if (_liveCards > 0 || !_autoExpanded || _userToggled)
         return;
      _autoExpanded = false;
      SetExpanded(false);
   }

   private void SetExpanded(bool expanded)
   {
      _expanded = expanded;
      _body.IsVisible = expanded;
      _chevron.Text   = expanded ? "▼" : "▶";
   }

   // -- helpers --------------------------------------------------------------

   private void _UpdateSummary()
   {
      // Tally tool names in insertion order; first occurrence governs order.
      var counts = new Dictionary<string, int>();
      var order  = new List<string>();
      int maxRank = 0;
      foreach (var c in _cards)
      {
         var n = string.IsNullOrEmpty(c.ToolName) ? "(tool)" : c.ToolName;
         if (counts.TryGetValue(n, out var k))
         {
            counts[n] = k + 1;
         }
         else
         {
            counts[n] = 1;
            order.Add(n);
            var r = _RiskRank(n);
            if (r > maxRank) maxRank = r;
         }
      }

      // Highest-risk tool tints the count label: a single bash buried in 20
      // read_files shows up immediately.
      _label.Text       = _cards.Count + " tool calls";
      _label.Foreground = _BrushForRank(maxRank);

      // Inline runs so each tool name carries its own color while the
      // separators / counts stay neutral white.
      _summary.Inlines ??= new InlineCollection();
      _summary.Inlines.Clear();
      for (int i = 0; i < order.Count; i++)
      {
         if (i > 0)
            _summary.Inlines.Add(new Run(", ") { Foreground = Brushes.White });
         var name = order[i];
         _summary.Inlines.Add(new Run(counts[name] + "x ") { Foreground = Brushes.White });
         _summary.Inlines.Add(new Run(name)
         {
            Foreground = ToolCallCard.ToolNameBrush(name),
            FontWeight = FontWeight.Bold,
         });
      }
   }

   // Same ordering as ToolCallCard's color map: bash > write > search > read.
   // The numeric rank lets us compare across tools without hardcoding a
   // priority list at every call site.
   private static int _RiskRank(string toolName) => toolName switch
   {
      "bash"        => 4,
      "delete_file" => 4,
      "write_file"  => 3,
      "edit_file"   => 3,
      "move_file"   => 3,
      "copy_file"   => 3,
      "glob"        => 2,
      "grep"        => 2,
      "start_read_subagent"  => 2,
      "start_subagent"       => 2,   // former name of the read tool
      "start_write_subagent" => 3,
      "read_file"  => 1,
      "read_image" => 1,
      "list_dir"   => 1,
      _            => 0,
   };

   private static IBrush _BrushForRank(int rank) => rank switch
   {
      4 => ToolCallCard.ToolNameBrush("bash"),
      3 => ToolCallCard.ToolNameBrush("write_file"),
      2 => ToolCallCard.ToolNameBrush("glob"),
      1 => ToolCallCard.ToolNameBrush("read_file"),
      _ => ThemeBrushes.TextMuted,
   };

   // -- fields ---------------------------------------------------------------

   private static readonly FontFamily MONO_FONT =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private readonly TextBlock              _chevron;
   private readonly TextBlock              _label;
   private readonly TextBlock              _summary;
   private readonly Border                 _header;
   private readonly Border                 _body;
   private readonly StackPanel             _bodyPanel;
   private readonly List<ToolCallCard>     _cards = new();
   private bool                            _expanded = false;
   private bool                            _autoExpanded;
   private bool                            _userToggled;
   private int                             _liveCards;
}
