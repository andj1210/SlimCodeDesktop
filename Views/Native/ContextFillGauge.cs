// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Small donut ring that shows how full the current context window is.
/// <see cref="UsedTokens"/> / <see cref="WindowTokens"/> drives the fill;
/// color steps from accent → orange → red as the fraction approaches 1.
///
/// Rendered with two arcs (track + fill) over a hairline circle, plus a
/// centered percentage label. ~36×36 px, intended for header placement.
/// </summary>
public sealed class ContextFillGauge : Control
{
   public static readonly StyledProperty<int> UsedTokensProperty =
      AvaloniaProperty.Register<ContextFillGauge, int>(nameof(UsedTokens));

   public static readonly StyledProperty<int> WindowTokensProperty =
      AvaloniaProperty.Register<ContextFillGauge, int>(nameof(WindowTokens), 32768);

   public int UsedTokens
   {
      get => GetValue(UsedTokensProperty);
      set => SetValue(UsedTokensProperty, value);
   }

   public int WindowTokens
   {
      get => GetValue(WindowTokensProperty);
      set => SetValue(WindowTokensProperty, value);
   }

   static ContextFillGauge()
   {
      AffectsRender<ContextFillGauge>(UsedTokensProperty, WindowTokensProperty);
   }

   public ContextFillGauge()
   {
      Width  = 36;
      Height = 36;
      UpdateTooltip();
   }

   /// <summary>
   /// Feed the latest turn's token breakdown so the hover tooltip can show
   /// input / cached / output / reasoning (and cache-write when reported).
   /// Does not affect the donut fill — that stays driven by
   /// <see cref="UsedTokens"/> (cached tokens still occupy the window).
   /// </summary>
   public void SetUsageBreakdown(int input, int cachedRead, int output, int reasoning, int cacheWrite)
   {
      _turn     = new Usage(input, cachedRead, output, reasoning, cacheWrite);
      _hasTurn  = true;
      UpdateTooltip();
   }

   /// <summary>
   /// Feed the running session totals (summed over every assistant turn in
   /// history) for the cumulative tooltip rows. This is the cost-relevant
   /// figure — input here grows with every resend, unlike the per-turn /
   /// occupancy numbers. Cached-token splits absent from older sessions
   /// fold in as zero.
   /// </summary>
   public void SetSessionTotals(int input, int cachedRead, int output, int reasoning, int cacheWrite)
   {
      _session     = new Usage(input, cachedRead, output, reasoning, cacheWrite);
      _hasSession  = true;
      UpdateTooltip();
   }

   /// <summary>Drop both breakdowns so a fresh session starts with a clean tooltip.</summary>
   public void ClearUsage()
   {
      _hasTurn    = false;
      _hasSession = false;
      UpdateTooltip();
   }

   protected override Size MeasureOverride(Size availableSize) => new(36, 36);

   protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   {
      base.OnPropertyChanged(change);
      if (change.Property == UsedTokensProperty || change.Property == WindowTokensProperty)
         UpdateTooltip();
   }

   public override void Render(DrawingContext ctx)
   {
      var size   = Math.Min(Bounds.Width, Bounds.Height);
      var stroke = 4.0;
      var radius = (size - stroke) / 2.0;
      var center = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);

      // -- background ring (always full circle) -----------------------------
      ctx.DrawEllipse(null,
                      new Pen(ThemeBrushes.BorderColor, stroke),
                      center, radius, radius);

      // -- foreground arc, scaled to fraction -------------------------------
      double frac = WindowTokens > 0 ? (double)UsedTokens / WindowTokens : 0;
      frac = Math.Clamp(frac, 0.0, 1.0);

      if (frac > 0.0)
      {
         var arcBrush = frac >= 0.90 ? Brushes.IndianRed
                      : frac >= 0.70 ? Brushes.Orange
                      :                ThemeBrushes.Accent;

         var arc = BuildArcGeometry(center, radius, frac);
         ctx.DrawGeometry(null,
                          new Pen(arcBrush, stroke, lineCap: PenLineCap.Round),
                          arc);
      }

      // -- center label (% used) -------------------------------------------
      int pct = (int)Math.Round(frac * 100);
      var text = new FormattedText(
         pct + "%",
         CultureInfo.CurrentCulture,
         FlowDirection.LeftToRight,
         new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
         10,
         ThemeBrushes.TextPrimary);
      var origin = new Point(
         center.X - text.Width  / 2.0,
         center.Y - text.Height / 2.0);
      ctx.DrawText(text, origin);
   }

   // -- helpers --------------------------------------------------------------

   // Builds a path that traces a clockwise arc starting at 12 o'clock,
   // covering `fraction` of the full circle.
   private static Geometry BuildArcGeometry(Point center, double radius, double fraction)
   {
      double angle = fraction * 2.0 * Math.PI;
      bool   isLargeArc = fraction > 0.5;

      // Start point: top of circle (12 o'clock). Sweep clockwise.
      var start = new Point(center.X, center.Y - radius);
      var end   = new Point(
         center.X + radius * Math.Sin(angle),
         center.Y - radius * Math.Cos(angle));

      var geom = new StreamGeometry();
      using (var c = geom.Open())
      {
         c.BeginFigure(start, isFilled: false);
         c.ArcTo(end,
                 new Size(radius, radius),
                 rotationAngle: 0,
                 isLargeArc: isLargeArc,
                 sweepDirection: SweepDirection.Clockwise);
         c.EndFigure(isClosed: false);
      }
      return geom;
   }

   private void UpdateTooltip()
   {
      var used = UsedTokens;
      var win  = WindowTokens;
      double frac = win > 0 ? (double)used / win : 0;
      var tip = "Context " + FormatTokens(used) + " / " + FormatTokens(win)
              + " (" + (int)Math.Round(frac * 100) + "%)";

      if (_hasTurn || _hasSession)
         tip += "\n----------------------------";
      if (_hasTurn)
         tip += "\n" + UsageLine("last turn", _turn);
      if (_hasSession)
         tip += "\n" + UsageLine("session  ", _session);

      ToolTip.SetTip(this, tip);
   }

   // One compact line: "<label>  in 80k (92% cached) · out 1.8k [· think 0.9k] [· cache-wr 2k]".
   private static string UsageLine(string label, Usage u)
   {
      int cachedPct = u.Input > 0 ? (int)Math.Round(100.0 * u.CachedRead / u.Input) : 0;
      var s = label + "  in " + FormatTokens(u.Input)
            + " (" + cachedPct + "% cached) · out " + FormatTokens(u.Output);
      if (u.Reasoning > 0)
         s += " · think " + FormatTokens(u.Reasoning);
      if (u.CacheWrite > 0)
         s += " · cache-wr " + FormatTokens(u.CacheWrite);
      return s;
   }

   private static string FormatTokens(int n)
   {
      if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#") + "M";
      if (n >= 1_000)     return (n / 1_000.0)    .ToString("0.#") + "k";
      return n.ToString();
   }

   // -- fields ---------------------------------------------------------------

   // Token breakdowns shown in the hover tooltip only (not in the donut).
   private readonly record struct Usage(int Input, int CachedRead, int Output, int Reasoning, int CacheWrite);

   private bool  _hasTurn;
   private Usage _turn;

   private bool  _hasSession;
   private Usage _session;
}
