// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Inline transcript entry for unrecoverable stream / loop errors
/// (network failure, malformed response, iteration cap, cancellation).
/// Distinct visual treatment from <see cref="ToolCallCard"/> failures so
/// the user can tell apart "the model's tool blew up but we'll continue"
/// from "the whole turn died".
/// </summary>
public sealed class ErrorLine : TranscriptItem
{
   public ErrorLine(string message)
   {
      var tb = new SelectableTextBlock
      {
         Text         = "⚠ " + (message ?? ""),
         Foreground   = Brushes.IndianRed,
         FontSize     = 12,
         TextWrapping = TextWrapping.Wrap,
      };

      View = new Border
      {
         Background          = ThemeBrushes.HoverBackground,
         BorderBrush         = Brushes.IndianRed,
         BorderThickness     = new Thickness(1, 0, 0, 0),
         CornerRadius        = new CornerRadius(2),
         Padding             = new Thickness(10, 6),
         Margin              = new Thickness(0, 4, 60, 4),
         HorizontalAlignment = HorizontalAlignment.Stretch,
         MaxWidth            = 900,
         Child               = tb,
      };
   }
}
