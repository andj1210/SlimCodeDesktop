// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Accent-tinted bubble carrying one user prompt. Plain text only —
/// users don't author markdown.
///
/// Layout rules for the left edge of the bubble in container coordinates:
///   - <c>x = LEFT_MAX (300)</c> when the text fits in the default box;
///   - <c>LEFT_MIN (30) .. LEFT_MAX</c> when more room is needed to keep
///     the message on a single line (bubble grows leftward as required);
///   - <c>x = LEFT_MIN (30)</c> when the text is long enough to require
///     wrapping; the bubble caps there and lines wrap inside.
///
/// Implemented by re-deriving <see cref="Layoutable.MinWidth"/> and
/// <see cref="Layoutable.MaxWidth"/> on every parent-bounds change:
///   bubble.MinWidth = parent.width - LEFT_MAX   → default 300 px gap
///   bubble.MaxWidth = parent.width - LEFT_MIN   → ceiling for adaptive growth
/// With <c>HorizontalAlignment.Right</c> the bubble takes its natural
/// width clamped to that range and stays glued to the right edge.
///
/// A small revert affordance lives at the left edge of the row. When
/// supplied via <paramref name="onRevert"/>, clicking it asks the host
/// to fork the conversation at this message: history truncates just
/// before this turn, a new session id is minted, and the bubble's text
/// is dropped back into the chat input for editing.
/// </summary>
public sealed class UserBubble : TranscriptItem
{
   private const double LEFT_MIN = 30;
   private const double LEFT_MAX = 300;

   private const double THUMB_HEIGHT    = 120;
   private const double THUMB_MAX_WIDTH = 200;

   /// <summary>Original prompt text. Read back by the revert flow.</summary>
   public string Text { get; }

   /// <summary>
   /// Images sent with this turn, empty when there were none. Read back by
   /// the revert flow so re-editing a message restores its attachments too —
   /// re-dropping them by hand would be the tedious part of a revert.
   /// </summary>
   public IReadOnlyList<ImagePart> Images { get; }

   public UserBubble(string text, AgentMode mode = AgentMode.Implement, Action<UserBubble>? onRevert = null,
                     IReadOnlyList<ImagePart>? images = null, Action<ImagePart>? onPreview = null)
   {
      Text   = text ?? "";
      Images = images ?? Array.Empty<ImagePart>();

      var content = new SelectableTextBlock
      {
         Text          = Text,
         TextWrapping  = TextWrapping.Wrap,
         TextAlignment = TextAlignment.Left,
         Foreground    = ThemeBrushes.TextOnAccent,
         FontSize      = 13,
         // An images-only turn has no text to show; keeping the empty block
         // would leave a stray blank line under the thumbnails.
         IsVisible     = Text.Length > 0,
      };

      // Mode badge: shown on every bubble so the user always knows which
      // mode that turn was sent in. Right-aligned inside the bubble.
      Control modeBadge = _BuildModeBadge(mode);

      var stack = new StackPanel();
      stack.Children.Add(modeBadge);
      if (Images.Count > 0)
         stack.Children.Add(_BuildThumbnails(Images, onPreview));
      stack.Children.Add(content);
      Control bubbleChild = stack;

      var bubble = new Border
      {
         // Background is mode-tinted: Implement keeps the default blue;
         // Plan switches to a muted ochre matched in luminance so the
         // bubble's silhouette doesn't shift — only its hue does.
         Background          = mode == AgentMode.Plan
                                  ? s_planBubbleBg
                                  : ThemeBrushes.AccentDark,
         CornerRadius        = new CornerRadius(8, 8, 2, 8),
         Padding             = new Thickness(12, 8),
         Margin              = new Thickness(0, 2, 0, 2),
         HorizontalAlignment = HorizontalAlignment.Right,
         Child               = bubbleChild,
      };

      // Revert button: a discreet "↶" glyph at the row's left edge. Muted
      // by default; brightens on hover so it's easy to find but doesn't
      // compete with the conversation. The button is hidden when no
      // revert callback is provided (e.g. legacy code paths).
      var revertBtn = new Button
      {
         Content             = "↶",
         FontSize            = 14,
         Padding             = new Thickness(4, 0),
         Width               = 22,
         Height              = 22,
         Background          = Brushes.Transparent,
         BorderBrush         = Brushes.Transparent,
         Foreground          = ThemeBrushes.TextMuted,
         HorizontalAlignment = HorizontalAlignment.Left,
         VerticalAlignment   = VerticalAlignment.Top,
         Margin              = new Thickness(2, 4, 0, 0),
         IsVisible           = onRevert != null,
         Cursor              = new Cursor(StandardCursorType.Hand),
      };
      ToolTip.SetTip(revertBtn,
         "Revert: fork the conversation at this point and put this message back in the input box.");
      if (onRevert != null)
         revertBtn.Click += (_, _) => onRevert(this);

      // Host the button and the bubble in a single Grid cell. Distinct
      // HorizontalAlignments park them at opposite edges; the bubble's
      // adaptive MaxWidth keeps a gap so they never overlap.
      var host = new Grid();
      host.Children.Add(revertBtn);
      host.Children.Add(bubble);

      // Bind Min/MaxWidth to the host's current width. The host fills the
      // transcript-panel row, so its width is what we want to size against.
      void ApplyConstraints()
      {
         double w = host.Bounds.Width;
         bubble.MinWidth = Math.Max(0, w - LEFT_MAX);
         bubble.MaxWidth = Math.Max(0, w - LEFT_MIN);
      }

      EventHandler<AvaloniaPropertyChangedEventArgs> onHostPropChanged = (_, e) =>
      {
         if (e.Property == Visual.BoundsProperty) ApplyConstraints();
      };
      host.PropertyChanged += onHostPropChanged;
      host.AttachedToVisualTree += (_, _) => ApplyConstraints();

      View = host;
   }

   // Attachments sit above the prompt text, matching the order they were
   // staged in below the input box. Sized generously enough that a screenshot
   // is recognizable without opening anything, and wrapped so several fit.
   private static Control _BuildThumbnails(IReadOnlyList<ImagePart> images, Action<ImagePart>? onPreview)
   {
      var panel = new WrapPanel
      {
         Orientation = Orientation.Horizontal,
         Margin      = new Thickness(0, 0, 0, 6),
      };

      foreach (var img in images)
      {
         var bmp = ImageThumb.Decode(img);
         if (bmp == null) continue;

         var frame = new Border
         {
            Background      = ThemeBrushes.WindowBackground,
            BorderBrush     = ThemeBrushes.BorderColor,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(2),
            Margin          = new Thickness(0, 0, 6, 6),
            Child = new Image
            {
               Source    = bmp,
               MaxHeight = THUMB_HEIGHT,
               MaxWidth  = THUMB_MAX_WIDTH,
               Stretch   = Stretch.Uniform,
            },
         };
         ToolTip.SetTip(frame, ImageThumb.Caption(img));
         if (onPreview != null)
         {
            frame.Cursor = new Cursor(StandardCursorType.Hand);
            frame.PointerPressed += (_, e) =>
            {
               onPreview(img);
               e.Handled = true;
            };
         }
         panel.Children.Add(frame);
      }
      return panel;
   }

   private static Control _BuildModeBadge(AgentMode mode)
   {
      bool isPlan = mode == AgentMode.Plan;
      var label = new TextBlock
      {
         Text       = isPlan ? "PLAN" : "IMPLEMENT",
         FontSize   = 9,
         FontWeight = FontWeight.Bold,
         Foreground = ThemeBrushes.TextOnAccent,
      };
      var badge = new Border
      {
         // Plan = amber accent (matches the bubble's ochre tint);
         // Implement = blue accent (matches the bubble's default tint).
         Background          = isPlan ? PlanAccent : ThemeBrushes.Accent,
         CornerRadius        = new CornerRadius(2),
         Padding             = new Thickness(5, 0, 5, 1),
         Margin              = new Thickness(0, 0, 0, 4),
         HorizontalAlignment = HorizontalAlignment.Right,
         Child               = label,
      };
      ToolTip.SetTip(badge, isPlan
         ? "Sent in plan mode — write/exec tools required explicit re-approval for this turn."
         : "Sent in implement mode — write/exec tools use cached approvals where available.");
      return badge;
   }

   // Mode-tinted brushes. Hardcoded here because they're tightly coupled
   // to the Plan-mode feature; promoting them to ThemeBrushes would imply
   // they're a general palette concept they aren't. Ochre matches the
   // amber the terminal already uses for its "session ended" border so
   // the eye has a precedent for this hue in the app.
   internal static readonly IBrush PlanBubbleBackground = new SolidColorBrush(Color.FromRgb(0x5C, 0x4A, 0x2E));
   internal static readonly IBrush PlanAccent           = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x50));

   private static readonly IBrush s_planBubbleBg = PlanBubbleBackground;
   private static readonly IBrush s_planBadgeBg  = PlanAccent;
}
