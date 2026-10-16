// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// A system-role message rendered in the transcript when the user enables
/// "show system messages" via the header toggle. Styled like a chat bubble
/// (à la <see cref="UserBubble"/>) but in a distinct slate/teal tint and
/// left-aligned, so out-of-band context — mode announcements, injected
/// correction notes the model sees — is unmistakable from the blue/ochre
/// user bubbles (right) and the plain assistant text (left).
///
/// Visibility is toggled by the host via <c>View.IsVisible</c>; the bubble
/// is always appended so flipping the toggle just shows/hides it in place.
/// </summary>
public sealed class SystemBubble : TranscriptItem
{
   /// <param name="badgeText">Badge label; default "SYSTEM". "TOOLS" marks the
   /// tool-definitions block, which travels beside the messages on the wire
   /// but is inlined next to the system prompt by the server's template.</param>
   public SystemBubble(string text, string badgeText = "SYSTEM")
   {
      var badge = new Border
      {
         Background          = s_badgeBg,
         CornerRadius        = new CornerRadius(2),
         Padding             = new Thickness(5, 0, 5, 1),
         Margin              = new Thickness(0, 0, 0, 4),
         HorizontalAlignment = HorizontalAlignment.Right,
         Child = new TextBlock
         {
            Text       = badgeText,
            FontSize   = 9,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrushes.TextOnAccent,
         },
      };

      var content = new SelectableTextBlock
      {
         Text          = text ?? "",
         TextWrapping  = TextWrapping.Wrap,
         TextAlignment = TextAlignment.Left,
         Foreground    = ThemeBrushes.TextPrimary,
         FontSize      = 12,
      };

      var stack = new StackPanel();
      stack.Children.Add(badge);
      stack.Children.Add(content);

      // Right-aligned with a left gutter, same as the user bubble — both are
      // inbound to the model (system->model, user->model). Only the tint sets
      // them apart. Corner notch on the bottom-right mirrors the user bubble.
      View = new Border
      {
         Background          = s_bubbleBg,
         CornerRadius        = new CornerRadius(8, 8, 2, 8),
         Padding             = new Thickness(12, 8),
         Margin              = new Thickness(30, 2, 0, 2),
         HorizontalAlignment = HorizontalAlignment.Right,
         Child               = stack,
      };
   }

   // Distinct slate/teal tint — deliberately not the blue (implement) or
   // ochre (plan) of user bubbles, so system context can't be mistaken for
   // a user or assistant turn.
   private static readonly IBrush s_bubbleBg = new SolidColorBrush(Color.FromRgb(0x2C, 0x3A, 0x3D));
   private static readonly IBrush s_badgeBg  = new SolidColorBrush(Color.FromRgb(0x4E, 0x8C, 0x8C));
}
