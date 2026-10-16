// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Read-only viewer for one project's permission audit log. Renders each
/// entry as a small panel with timestamp, tool, color-coded decision /
/// scope badges, reason, and the truncated argument preview that was
/// captured at decision time.
///
/// "Clear log" truncates the file to zero bytes. Useful for getting a
/// clean slate before testing a policy change — the on-disk file is the
/// only record, so this is a permanent delete (until the next call writes
/// the first new entry).
/// </summary>
public partial class AuditLogWindow : Window
{
   public AuditLogWindow()
   {
      InitializeComponent();
   }

   public void Configure(
      string                 logFilePath,
      string?                activeSessionId = null,
      IReadOnlyCollection<string>? liveSessionIds = null)
   {
      _filePath        = logFilePath;
      _activeSessionId = activeSessionId;
      TxtPath.Text      = logFilePath;

      // Orphan GC: when the caller hands us the current set of live session
      // ids, prune entries whose session no longer exists. Entries without
      // a sessionId (legacy / non-session-attributed) are always kept.
      if (liveSessionIds != null)
      {
         var live = new HashSet<string>(liveSessionIds, StringComparer.Ordinal);
         PermissionAuditLog.Prune(logFilePath,
            e => string.IsNullOrEmpty(e.SessionId) || live.Contains(e.SessionId!));
      }

      // Only offer the session filter when we actually have a session to
      // filter against. Default-checked so a user opening the log mid-chat
      // sees only their own activity by default.
      ChkSessionOnly.IsVisible = !string.IsNullOrEmpty(activeSessionId);
      ChkSessionOnly.IsChecked = !string.IsNullOrEmpty(activeSessionId);

      Reload();
   }

   // -- actions --------------------------------------------------------------

   private void BtnRefresh_Click(object? sender, RoutedEventArgs e) => Reload();

   private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

   private void BtnClear_Click(object? sender, RoutedEventArgs e)
   {
      if (string.IsNullOrEmpty(_filePath)) return;
      PermissionAuditLog.Clear(_filePath);
      Reload();
   }

   // -- rendering ------------------------------------------------------------

   private void Reload()
   {
      RowsPanel.Children.Clear();
      if (string.IsNullOrEmpty(_filePath))
      {
         TxtCount.Text = "(no log path)";
         return;
      }

      var all = PermissionAuditLog.Read(_filePath);

      bool filterToSession = ChkSessionOnly.IsChecked == true
                          && !string.IsNullOrEmpty(_activeSessionId);

      var entries = new List<AuditEntry>(all.Count);
      foreach (var e in all)
      {
         if (filterToSession && e.SessionId != _activeSessionId) continue;
         entries.Add(e);
      }

      var label = entries.Count + " entr" + (entries.Count == 1 ? "y" : "ies");
      if (filterToSession) label += " (this session)";
      else if (!string.IsNullOrEmpty(_activeSessionId)) label += " (all sessions)";
      TxtCount.Text = label;

      if (entries.Count == 0)
      {
         var msg = filterToSession
            ? "(no entries for this session yet)"
            : "(no entries yet — the log is created when the first tool call is gated)";
         RowsPanel.Children.Add(new TextBlock
         {
            Text       = msg,
            Foreground = ThemeBrushes.TextMuted,
            FontStyle  = FontStyle.Italic,
            Margin     = new Thickness(12, 16),
         });
         return;
      }

      // Newest first — matches log-viewer conventions.
      entries.Reverse();
      foreach (var e in entries)
         RowsPanel.Children.Add(BuildRow(e));
   }

   private void ChkSessionOnly_Changed(object? sender, RoutedEventArgs e) => Reload();

   private Control BuildRow(AuditEntry entry)
   {
      // Decision badge color tracks risk: allow=accent, deny=red, prompt=muted.
      IBrush decBg = entry.Decision switch
      {
         PermissionDecision.Allow      => ThemeBrushes.Accent,
         PermissionDecision.Deny       => Brushes.IndianRed,
         PermissionDecision.AlwaysDeny => Brushes.DarkRed,
         PermissionDecision.AlwaysAsk  => Brushes.DarkOrange,
         _                             => ThemeBrushes.HoverBackground,
      };
      var decLbl = MakeBadge(entry.Decision.ToString(), decBg, Brushes.White);

      var scopeLbl = MakeBadge(entry.Scope.ToString(),
                               ThemeBrushes.HoverBackground,
                               ThemeBrushes.TextMutedStrong);

      var tsTb = new TextBlock
      {
         Text              = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
         FontFamily        = MONO,
         FontSize          = 10,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
      };

      // Tool name takes the same color as in the inline tool-call card
      // (read=green, glob/grep=cyan, write=yellow, bash=red). Gives a
      // category-at-a-glance signal when skimming a long audit list.
      var toolTb = new TextBlock
      {
         Text              = entry.Tool,
         FontFamily        = MONO,
         FontSize          = 12,
         FontWeight        = FontWeight.Bold,
         Foreground        = ToolCallCard.ToolNameBrush(entry.Tool),
         VerticalAlignment = VerticalAlignment.Center,
      };

      var reasonTb = new TextBlock
      {
         Text         = entry.Reason,
         FontSize     = 11,
         Foreground   = ThemeBrushes.TextMutedStrong,
         TextWrapping = TextWrapping.Wrap,
         Margin       = new Thickness(0, 2, 0, 0),
      };

      var argsTb = new SelectableTextBlock
      {
         Text         = string.IsNullOrEmpty(entry.ArgsPreview) ? "(no args)" : entry.ArgsPreview,
         FontFamily   = MONO,
         FontSize     = 10,
         Foreground   = ThemeBrushes.TextLog,
         TextWrapping = TextWrapping.Wrap,
         Margin       = new Thickness(0, 2, 0, 0),
      };

      // Top row: timestamp | tool | spacer | decision | scope
      var top = new Grid { ColumnDefinitions = new ColumnDefinitions("160,Auto,*,Auto,Auto") };
      Grid.SetColumn(tsTb,    0);
      Grid.SetColumn(toolTb,  1);
      Grid.SetColumn(decLbl,  3);
      Grid.SetColumn(scopeLbl,4);
      decLbl.Margin   = new Thickness(0, 0, 4, 0);
      scopeLbl.Margin = new Thickness(0);
      top.Children.Add(tsTb);
      top.Children.Add(toolTb);
      top.Children.Add(decLbl);
      top.Children.Add(scopeLbl);

      var body = new StackPanel
      {
         Spacing = 0,
         // Right gutter (~40px) so the ScrollViewer's overlay scrollbar never
         // clips the rightmost (Scope) badge when it expands on hover.
         Margin  = new Thickness(8, 6, 40, 6),
      };
      body.Children.Add(top);
      body.Children.Add(reasonTb);
      body.Children.Add(argsTb);

      return new Border
      {
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 0, 0, 1),
         Child           = body,
      };
   }

   private static Border MakeBadge(string text, IBrush bg, IBrush fg) => new()
   {
      Background          = bg,
      CornerRadius        = new CornerRadius(3),
      Padding             = new Thickness(8, 2),
      VerticalAlignment   = VerticalAlignment.Center,
      Child = new TextBlock
      {
         Text       = text,
         FontSize   = 12,
         FontWeight = FontWeight.SemiBold,
         Foreground = fg,
      },
   };

   // -- fields ---------------------------------------------------------------

   private static readonly FontFamily MONO =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private string? _filePath;
   private string? _activeSessionId;
}
