// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Pinned panel that mirrors the model's current task list. Populated
/// via the <c>set_tasks</c> tool — the model invokes it with the full
/// list each time progress changes, and the tool's executor pushes
/// the parsed items here.
///
/// "Last write wins" update model: every invocation replaces the panel
/// contents wholesale. Hidden until the first invocation. On resume,
/// the host walks history for the most recent <c>set_tasks</c> call
/// and replays it through <see cref="SetTasks"/>.
/// </summary>
public sealed class TaskListPanel : UserControl
{
   public enum Status
   {
      Pending,
      InProgress,
      Completed,
      Cancelled,
   }

   public readonly record struct TaskItem(Status Status, string Text);

   public TaskListPanel()
   {
      _chevron = new TextBlock
      {
         Text              = "▼",
         FontSize          = 10,
         VerticalAlignment = VerticalAlignment.Center,
         Width             = 12,
         Foreground        = ThemeBrushes.TextMuted,
         Margin            = new Thickness(0, 0, 6, 0),
      };
      _titleTb = new TextBlock
      {
         Text              = "Tasks",
         FontWeight        = FontWeight.Bold,
         FontSize          = 12,
         Foreground        = ThemeBrushes.TextPrimary,
         VerticalAlignment = VerticalAlignment.Center,
      };
      _summaryTb = new TextBlock
      {
         Text              = "",
         FontSize          = 11,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(8, 0, 0, 0),
      };

      var headerStack = new StackPanel
      {
         Orientation = Orientation.Horizontal,
      };
      headerStack.Children.Add(_chevron);
      headerStack.Children.Add(_titleTb);
      headerStack.Children.Add(_summaryTb);

      _header = new Border
      {
         Background = Brushes.Transparent,
         Padding    = new Thickness(10, 6),
         Cursor     = new Cursor(StandardCursorType.Hand),
         Child      = headerStack,
      };
      _header.PointerPressed += (_, _) => _SetExpanded(!_expanded);

      _bodyPanel = new StackPanel
      {
         Margin  = new Thickness(10, 0, 10, 8),
         Spacing = 2,
      };

      _root = new Border
      {
         Background      = ThemeBrushes.PanelBackground,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 0, 0, 1),
         Child           = new StackPanel
         {
            Children = { _header, _bodyPanel },
         },
      };

      Content   = _root;
      IsVisible = false;
   }

   // -- public surface -------------------------------------------------------

   /// <summary>Replace the panel contents with the given items, or clear when empty.</summary>
   public void SetTasks(IReadOnlyList<TaskItem>? items)
   {
      _bodyPanel.Children.Clear();
      if (items == null || items.Count == 0)
      {
         IsVisible = false;
         return;
      }

      int done = 0;
      int inProgress = 0;
      foreach (var it in items)
      {
         if (it.Status == Status.Completed) done++;
         if (it.Status == Status.InProgress) inProgress++;
         _bodyPanel.Children.Add(_BuildRow(it));
      }

      // Summary: "(2/5)" — counts completed against total. Optionally
      // hint at active work when something is in_progress.
      var summary = "(" + done + "/" + items.Count + ")";
      if (inProgress > 0) summary += " · " + inProgress + " active";
      _summaryTb.Text = summary;
      IsVisible = true;
   }

   /// <summary>Drop the panel entirely. Called on Reset / session switch.</summary>
   public void Clear() => SetTasks(null);

   // -- internals ------------------------------------------------------------

   private void _SetExpanded(bool expanded)
   {
      _expanded = expanded;
      _bodyPanel.IsVisible = expanded;
      _chevron.Text        = expanded ? "▼" : "▶";
   }

   private Control _BuildRow(TaskItem item)
   {
      // Glyph + color signal the status at a glance:
      //   completed  → ☑ accent, strikethrough text
      //   in_progress → ◐ accent, normal text (active highlight)
      //   pending    → ☐ muted, normal text
      //   cancelled  → ⊘ muted, strikethrough + extra-faded text
      string glyph;
      IBrush glyphBrush;
      IBrush textBrush;
      bool strike = false;
      switch (item.Status)
      {
         case Status.Completed:
            glyph = "☑";
            glyphBrush = ThemeBrushes.Accent;
            textBrush  = ThemeBrushes.TextMuted;
            strike = true;
            break;
         case Status.InProgress:
            glyph = "◐";
            glyphBrush = ThemeBrushes.Accent;
            textBrush  = ThemeBrushes.TextPrimary;
            break;
         case Status.Cancelled:
            glyph = "⊘";
            glyphBrush = ThemeBrushes.TextMuted;
            textBrush  = ThemeBrushes.TextMuted;
            strike = true;
            break;
         default:
            glyph = "☐";
            glyphBrush = ThemeBrushes.TextMuted;
            textBrush  = ThemeBrushes.TextPrimary;
            break;
      }

      var glyphTb = new TextBlock
      {
         Text              = glyph,
         FontSize          = 13,
         Width             = 18,
         Foreground        = glyphBrush,
         VerticalAlignment = VerticalAlignment.Top,
      };
      var textTb = new SelectableTextBlock
      {
         Text         = item.Text,
         FontSize     = 12,
         Foreground   = textBrush,
         TextWrapping = TextWrapping.Wrap,
      };
      if (strike) textTb.TextDecorations = TextDecorations.Strikethrough;
      if (item.Status == Status.InProgress) textTb.FontWeight = FontWeight.SemiBold;

      var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
      Grid.SetColumn(glyphTb, 0);
      Grid.SetColumn(textTb,  1);
      grid.Children.Add(glyphTb);
      grid.Children.Add(textTb);
      return grid;
   }

   // -- fields ---------------------------------------------------------------

   private readonly TextBlock  _chevron;
   private readonly TextBlock  _titleTb;
   private readonly TextBlock  _summaryTb;
   private readonly Border     _header;
   private readonly StackPanel _bodyPanel;
   private readonly Border     _root;
   private bool                _expanded = true;
}
