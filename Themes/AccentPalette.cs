// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Themes;

/// <summary>
/// User-selectable accent hue. <see cref="Auto"/> derives one from the active
/// profile name so two profiles are visually distinct without anyone
/// configuring anything.
///
/// Deliberately amber-free: Plan mode tints the input box amber (#C8A050) and
/// the permission editor uses amber for "ask", so an amber accent would erase
/// two status signals. Same reasoning keeps mint-green (the "allow" color) off
/// the list -- Green here sits well clear of it.
/// </summary>
public enum AccentPreset
{
   [SettingChoice("Auto (from profile name)")] Auto = 0,
   [SettingChoice("Blue (classic)")]           Blue,
   [SettingChoice("Teal")]                     Teal,
   [SettingChoice("Green")]                    Green,
   [SettingChoice("Purple")]                   Purple,
   [SettingChoice("Magenta")]                  Magenta,
   [SettingChoice("Red")]                      Red,
   [SettingChoice("Slate (muted)")]            Slate,
}

/// <summary>
/// Derives the four accent brushes (plus the text color that sits on them)
/// from a single base color and writes them into the live application
/// resources, shadowing the values in <c>Themes/DarkTheme.axaml</c>.
///
/// Why a ramp rather than four settings: the stock quartet
/// (#3DA8E5 / #1F8FD0 / #0E639C / #094771) turns out to be one hue at four
/// lightnesses, so one color plus fixed lightness targets reproduces it and
/// generalizes to any hue. Targets are <em>absolute</em>, not offsets from the
/// base: that keeps button contrast constant no matter how light or dark the
/// picked color is, which is what makes the free-form hex override safe.
/// </summary>
public static class AccentPalette
{
   // Lightness of each ramp step. Chosen to reproduce the hand-picked dark
   // theme quartet from base #3DA8E5 (whose own lightness is 0.57).
   private const double HoverLightness   = 0.47;
   private const double DarkLightness    = 0.33;
   private const double PressedLightness = 0.24;

   // The base color doubles as foreground text on dark panels, so it may not
   // be arbitrarily dark; and above ~0.78 it glares against #1E1E1E.
   private const double MinAccentLightness = 0.45;
   private const double MaxAccentLightness = 0.78;

   // -- resolving ------------------------------------------------------------

   /// <summary>
   /// The base color implied by the settings: an explicit hex wins, else the
   /// chosen preset, else (Auto) one derived from <paramref name="profile"/>.
   /// </summary>
   public static Color Resolve(AccentPreset preset, string? hexOverride, string profile)
   {
      if (!string.IsNullOrWhiteSpace(hexOverride) &&
          Color.TryParse(hexOverride.Trim(), out var custom))
      {
         return custom;
      }

      if (preset == AccentPreset.Auto)
         preset = _PresetForProfile(profile);

      return _BaseColor(preset);
   }

   /// <summary>
   /// Overwrite the accent slots in <see cref="Application.Resources"/>. Entries
   /// set here shadow the merged theme dictionary, so <c>{DynamicResource}</c>
   /// consumers (every Button, TabItem) re-color immediately.
   ///
   /// Controls built from code-behind are a different story: they copied an
   /// <c>IBrush</c> out of <see cref="ThemeBrushes"/> at construction time and
   /// keep it until they are rebuilt. Calling this before the first window
   /// exists is therefore the only way to get a uniformly colored UI; a later
   /// call repaints the XAML-driven chrome and leaves already-drawn bubbles,
   /// cards and gauges on the previous color until restart.
   /// </summary>
   public static void Apply(Application app, Color baseColor)
   {
      if (app == null) return;

      var hsl = baseColor.ToHsl();

      var accent  = _WithLightness(hsl, Math.Clamp(hsl.L, MinAccentLightness, MaxAccentLightness));
      var hover   = _WithLightness(hsl, HoverLightness);
      var dark    = _WithLightness(hsl, DarkLightness);
      var pressed = _WithLightness(hsl, PressedLightness);

      app.Resources["AccentBrush"]        = new SolidColorBrush(accent);
      app.Resources["AccentHoverBrush"]   = new SolidColorBrush(hover);
      app.Resources["AccentDarkBrush"]    = new SolidColorBrush(dark);
      app.Resources["AccentPressedBrush"] = new SolidColorBrush(pressed);

      // The button fill is what labels actually sit on, so it decides.
      app.Resources["TextOnAccentBrush"] = new SolidColorBrush(
         _Luminance(dark) > 0.45 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White);
   }

   // -- private --------------------------------------------------------------

   private static Color _WithLightness(HslColor hsl, double lightness) =>
      new HslColor(hsl.A, hsl.H, hsl.S, lightness).ToRgb();

   // Hue / saturation / lightness per preset. Blue is exactly the stock
   // #3DA8E5 so the default profile looks unchanged.
   private static Color _BaseColor(AccentPreset p)
   {
      (double h, double s, double l) = p switch
      {
         AccentPreset.Teal    => (176.0, 0.62, 0.52),
         AccentPreset.Green   => (128.0, 0.48, 0.52),
         AccentPreset.Purple  => (272.0, 0.60, 0.64),
         AccentPreset.Magenta => (322.0, 0.62, 0.60),
         AccentPreset.Red     => (  2.0, 0.68, 0.60),
         AccentPreset.Slate   => (214.0, 0.22, 0.62),
         _                    => (202.0, 0.76, 0.57),   // Blue / Auto fallback
      };
      return new HslColor(1.0, h, s, l).ToRgb();
   }

   // Stable across machines and restarts: the same profile name always gets
   // the same color, which is the entire point of the visual separation.
   // "Default" is pinned to Blue so the untouched install looks as it always did.
   private static AccentPreset _PresetForProfile(string? profile)
   {
      if (string.IsNullOrEmpty(profile) ||
          string.Equals(profile, Core.AppPaths.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
      {
         return AccentPreset.Blue;
      }

      // FNV-1a over the lowercased name. Any stable hash would do; this one is
      // four lines and has no framework-version-dependent behavior (string
      // GetHashCode is randomized per process and would re-color every launch).
      uint hash = 2166136261u;
      foreach (var c in profile.ToLowerInvariant())
      {
         hash ^= c;
         hash *= 16777619u;
      }

      // Pick among the concrete presets, i.e. everything after Auto.
      var choices = (AccentPreset[])Enum.GetValues(typeof(AccentPreset));
      var count   = choices.Length - 1;
      return choices[1 + (int)(hash % (uint)count)];
   }

   // WCAG relative luminance. Only used to decide black-vs-white labels.
   private static double _Luminance(Color c)
   {
      static double Channel(byte v)
      {
         var x = v / 255.0;
         return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
      }
      return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
   }
}
