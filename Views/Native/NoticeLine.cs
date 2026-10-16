// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Neutral inline marker for informational events that aren't errors and
/// aren't part of the model's reply -- e.g. "model switched mid-session".
/// Visually muted so it doesn't compete with the chat itself.
/// </summary>
public sealed class NoticeLine : TranscriptItem
{
   public NoticeLine(string message)
   {
      var tb = new SelectableTextBlock
      {
         Text         = "— " + (message ?? "") + " —",
         Foreground   = ThemeBrushes.TextMuted,
         FontSize     = 11,
         FontStyle    = FontStyle.Italic,
         TextWrapping = TextWrapping.Wrap,
         HorizontalAlignment = HorizontalAlignment.Center,
      };

      View = new Border
      {
         Padding             = new Thickness(8, 4),
         Margin              = new Thickness(0, 6, 0, 6),
         HorizontalAlignment = HorizontalAlignment.Stretch,
         Child               = tb,
      };
   }
}
