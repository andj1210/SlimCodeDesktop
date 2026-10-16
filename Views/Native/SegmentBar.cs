// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Thin stacked bar: each segment's width is its share of the total. Used
/// for the time-per-phase and token-provenance readouts; the semantics live
/// in the caller (see <see cref="TimingViz"/>), this only draws.
///
/// One segment may be marked live: its value grows with the control's own
/// clock between calls, repainted a few times a second, so a phase in
/// progress is visibly filling. The clock stops as soon as no segment is
/// live, so an idle bar costs nothing.
/// </summary>
public sealed class SegmentBar : Control
{
   public readonly struct Segment
   {
      public Segment(string label, IBrush brush, double value)
      {
         Label = label;
         Brush = brush;
         Value = value;
      }

      public string Label { get; }
      public IBrush Brush { get; }
      public double Value { get; }
   }

   private const double LIVE_REPAINT_MS = 250;

   public SegmentBar(double width, double height)
   {
      Width  = width;
      Height = height;
      VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
   }

   /// <summary>
   /// Replace the segments. <paramref name="liveIndex"/> names the one that
   /// keeps growing from now on (in the value unit per second), or -1 for a
   /// settled bar.
   /// </summary>
   public void SetSegments(IReadOnlyList<Segment> segments, int liveIndex = -1)
   {
      _segments = segments ?? Array.Empty<Segment>();
      int live  = liveIndex >= 0 && liveIndex < _segments.Count ? liveIndex : -1;
      // Same live segment as before: the caller refreshed the settled values
      // (a token count landed) while the phase goes on, so its clock keeps
      // running. Restarting it would snap the growing segment back to zero.
      if (live != _liveIndex)
      {
         _liveIndex = live;
         _liveStart = DateTime.UtcNow;
      }
      _SyncTimer();
      InvalidateVisual();
   }

   /// <summary>
   /// Hover text, rendered monospace so the padded columns of
   /// <see cref="TimingViz"/> line up. Kept separate from the segments so the
   /// caller can add rates.
   /// </summary>
   public void SetTooltip(string? text)
   {
      ToolTip.SetTip(this, string.IsNullOrEmpty(text) ? null : TimingViz.MonoTip(text!));
   }

   /// <summary>Seconds the live segment has been growing, zero when none is live.</summary>
   public double LiveSeconds =>
      _liveIndex >= 0 ? (DateTime.UtcNow - _liveStart).TotalSeconds : 0.0;

   /// <summary>Fires on every live repaint so the owner can refresh a tooltip in step.</summary>
   public event Action? LiveTick;

   protected override Size MeasureOverride(Size availableSize) => new(Width, Height);

   public override void Render(DrawingContext ctx)
   {
      double w = Bounds.Width;
      double h = Bounds.Height;
      double r = h / 2.0;

      ctx.DrawRectangle(ThemeBrushes.PanelBackground,
                        new Pen(ThemeBrushes.BorderColor, 1),
                        new Rect(0, 0, w, h), r, r);

      double live  = LiveSeconds;
      double total = 0;
      for (int i = 0; i < _segments.Count; i++)
         total += _ValueOf(i, live);
      if (total <= 0)
         return;

      // Clip to the rounded track so the first and last segment take its
      // corners without each needing its own radius bookkeeping.
      using (ctx.PushClip(new RoundedRect(new Rect(0, 0, w, h), r)))
      {
         double x = 0;
         for (int i = 0; i < _segments.Count; i++)
         {
            double v = _ValueOf(i, live);
            if (v <= 0) continue;
            double sw = w * v / total;
            ctx.DrawRectangle(_segments[i].Brush, null, new Rect(x, 0, sw, h));
            x += sw;
         }

         // A thin end cap on the growing segment, so it reads as in progress.
         if (_liveIndex >= 0)
         {
            double edge = 0;
            for (int i = 0; i <= _liveIndex; i++)
               edge += w * _ValueOf(i, live) / total;
            var pen = new Pen(Brushes.White, 1.0);
            ctx.DrawLine(pen, new Point(edge - 1, 0), new Point(edge - 1, h));
         }
      }
   }

   // -- private methods ------------------------------------------------------

   private double _ValueOf(int index, double liveSeconds) =>
      _segments[index].Value + (index == _liveIndex ? liveSeconds : 0.0);

   private void _SyncTimer()
   {
      if (_liveIndex >= 0)
      {
         if (_timer != null) return;
         _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(LIVE_REPAINT_MS),
                                      DispatcherPriority.Background,
                                      (_, _) =>
                                      {
                                         InvalidateVisual();
                                         LiveTick?.Invoke();
                                      });
         _timer.Start();
      }
      else
      {
         _timer?.Stop();
         _timer = null;
      }
   }

   // -- fields ---------------------------------------------------------------

   private IReadOnlyList<Segment> _segments = Array.Empty<Segment>();
   private int                    _liveIndex = -1;
   private DateTime               _liveStart;
   private DispatcherTimer?       _timer;
}
