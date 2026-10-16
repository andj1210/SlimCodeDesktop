// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Media;

namespace SlimCodeDesktop.Themes;

/// <summary>
/// Typed accessors for the brushes defined in the active theme's
/// ResourceDictionary (e.g. <c>Themes/DarkTheme.axaml</c>). Each property
/// performs a dictionary lookup on the live <see cref="Application.Resources"/>,
/// so swapping the theme at runtime is reflected on the next read — equivalent
/// to <c>{DynamicResource …}</c> in XAML, but usable from code-behind.
///
/// Cost is one Dictionary.TryGetValue per call. Negligible for UI construction;
/// don't call from inner render loops.
/// </summary>
public static class ThemeBrushes
{
   public static IBrush Get(string key)
   {
      var app = Application.Current;
      if (app != null &&
          app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) &&
          v is IBrush b)
         return b;
      return Brushes.Transparent;
   }

   // -- slots (must match keys in Themes/*.axaml) ----------------------------

   public static IBrush WindowBackground         => Get("WindowBackgroundBrush");
   public static IBrush PanelBackground          => Get("PanelBackgroundBrush");
   public static IBrush HoverBackground          => Get("HoverBackgroundBrush");

   public static IBrush BorderColor              => Get("BorderColorBrush");
   public static IBrush NeutralButtonBackground  => Get("NeutralButtonBackgroundBrush");

   public static IBrush TextPrimary              => Get("TextPrimaryBrush");
   public static IBrush TextMuted                => Get("TextMutedBrush");
   public static IBrush TextMutedStrong          => Get("TextMutedStrongBrush");
   public static IBrush TextLog                  => Get("TextLogBrush");
   public static IBrush TextOnAccent             => Get("TextOnAccentBrush");

   public static IBrush Accent                   => Get("AccentBrush");
   public static IBrush AccentHover              => Get("AccentHoverBrush");
   public static IBrush AccentDark               => Get("AccentDarkBrush");
   public static IBrush AccentPressed            => Get("AccentPressedBrush");

   public static IBrush TooltipBackground        => Get("TooltipBackgroundBrush");
   public static IBrush TooltipText              => Get("TooltipTextBrush");
}
