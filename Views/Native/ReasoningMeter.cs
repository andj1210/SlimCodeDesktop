// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Thin horizontal barometer for a reasoning trace's repetition score:
/// green while the thinking reads as fresh, ramping through amber to red as
/// it starts circling. A full red bar with the tick marker means the
/// overthinking heuristic actually tripped (repetition high AND the trace
/// past its length gate) — the point at which the turn is worth cancelling.
///
/// Purely derived from the trace text, so it reads the same on a replayed
/// session as it did live. Nothing here is persisted.
/// </summary>
public sealed class ReasoningMeter : Control
{
   private const double BAR_W = 44;
   private const double BAR_H = 6;

   public ReasoningMeter()
   {
      Width               = BAR_W;
      Height              = BAR_H;
      Margin              = new Thickness(8, 0, 0, 0);
      VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center;
      IsVisible           = false;
   }

   /// <summary>Feed the latest reading. Hides the bar until the trace is long enough to judge.</summary>
   public void SetStats(ReasoningStats stats)
   {
      _stats    = stats;
      IsVisible = stats.HasSignal;
      _UpdateTooltip();
      InvalidateVisual();
   }

   protected override Size MeasureOverride(Size availableSize) => new(BAR_W, BAR_H);

   public override void Render(DrawingContext ctx)
   {
      double w = Bounds.Width;
      double h = Bounds.Height;
      double r = h / 2.0;

      // -- track -------------------------------------------------------------
      ctx.DrawRectangle(ThemeBrushes.PanelBackground,
                        new Pen(ThemeBrushes.BorderColor, 1),
                        new Rect(0, 0, w, h), r, r);

      // -- fill --------------------------------------------------------------
      double frac = Math.Clamp(_stats.Score, 0.0, 1.0);
      if (frac > 0.0)
      {
         // Keep a visible stub at very low scores so the bar never looks broken.
         double fw = Math.Max(h, frac * w);
         ctx.DrawRectangle(_RampBrush(frac), null, new Rect(0, 0, fw, h), r, r);
      }

      // -- trip marker -------------------------------------------------------
      // Only when BOTH gates passed. A full bar on a short trace means
      // "repetitive", not "would have triggered".
      if (_stats.Tripped)
      {
         var pen = new Pen(Brushes.White, 1.0);
         double x = w - r;
         ctx.DrawLine(pen, new Point(x, 1), new Point(x, h - 1));
      }
   }

   // -- private methods ---------------------------------------------------------

   // green -> amber -> red, with amber at the midpoint.
   private static IBrush _RampBrush(double t)
   {
      Color c = t <= 0.5
         ? _Lerp(Color.FromRgb(0x4C, 0xAF, 0x50), Color.FromRgb(0xFF, 0xC1, 0x07), t / 0.5)
         : _Lerp(Color.FromRgb(0xFF, 0xC1, 0x07), Color.FromRgb(0xD3, 0x2F, 0x2F), (t - 0.5) / 0.5);

      // Cache the brush across renders — the score changes slowly and a new
      // SolidColorBrush per frame would churn during streaming.
      var cached = s_cached;
      if (cached == null || s_cachedColor != c)
      {
         cached        = new SolidColorBrush(c);
         s_cachedColor = c;
         s_cached      = cached;
      }
      return cached;
   }

   private static Color _Lerp(Color a, Color b, double t)
   {
      t = Math.Clamp(t, 0.0, 1.0);
      return Color.FromRgb(
         (byte)(a.R + (b.R - a.R) * t),
         (byte)(a.G + (b.G - a.G) * t),
         (byte)(a.B + (b.B - a.B) * t));
   }

   private void _UpdateTooltip()
   {
      var tip = "Thinking repetition "
              + (int)Math.Round(_stats.RepetitionRatio * 100) + "%"
              + "  (~" + _FormatTokens(_stats.ApproxTokens) + " tokens"
              + ", gate " + _FormatTokens(_stats.ThresholdTokens) + ")";

      tip += "\n----------------------------\n";
      if (_stats.Tripped)
         tip += "Overthink heuristic: TRIGGERED";
      else if (!_stats.LengthGatePassed)
         tip += "Overthink heuristic: armed above the gate (trace still short)";
      else
         tip += "Overthink heuristic: not triggered";

      ToolTip.SetTip(this, tip);
   }

   private static string _FormatTokens(int n)
   {
      if (n >= 1000) return (n / 1000.0).ToString("0.#") + "k";
      return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
   }

   // -- fields ------------------------------------------------------------------

   private static SolidColorBrush? s_cached;
   private static Color            s_cachedColor;

   private ReasoningStats _stats;
}
