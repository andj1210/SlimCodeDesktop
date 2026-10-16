// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// A <see cref="GridSplitter"/> that paints itself as a single horizontal
/// status line: green when the host's autoscroll latch is engaged
/// ("you're live"), gray when the user has scrolled into history. While
/// <see cref="IsWorking"/> is true, a "knight-rider" streak slides in
/// from each edge toward the center and collapses there — a one-line
/// echo of the terminal control's working-border animation.
///
/// Drag/resize behavior is fully inherited from <see cref="GridSplitter"/>;
/// only the visual is replaced.
/// </summary>
public sealed class AnimatedSplitter : GridSplitter
{
   // -- public state ---------------------------------------------------------

   public static readonly StyledProperty<bool> IsLatchedProperty =
      AvaloniaProperty.Register<AnimatedSplitter, bool>(nameof(IsLatched), defaultValue: true);

   /// <summary>True = autoscroll engaged (line painted green).</summary>
   public bool IsLatched
   {
      get => GetValue(IsLatchedProperty);
      set => SetValue(IsLatchedProperty, value);
   }

   public static readonly StyledProperty<bool> IsWorkingProperty =
      AvaloniaProperty.Register<AnimatedSplitter, bool>(nameof(IsWorking), defaultValue: false);

   /// <summary>True = agent loop is running. Drives the streak animation.</summary>
   public bool IsWorking
   {
      get => GetValue(IsWorkingProperty);
      set => SetValue(IsWorkingProperty, value);
   }

   // -- visual constants -----------------------------------------------------

   private static readonly Color s_latched   = Color.FromArgb(0xFF, 0x6E, 0xB0, 0x88); // muted green
   private static readonly Color s_unlatched = Color.FromArgb(0x80, 0x88, 0x88, 0x88); // subtle gray
   private static readonly Color s_streakHead = Color.FromArgb(0xFF, 0xFF, 0x5C, 0xE4); // bright magenta head
   private static readonly Color s_streakTail = s_latched;

   private const int    STOP_COUNT   = 10;
   private const double STOP_PX      = 18.0;
   private const double ACTIVE_END   = 0.85;   // remaining 0.15 of the cycle is the breath
   private const double PHASE_STEP   = 0.025;
   private const int    TICK_MS      = 50;

   private static readonly Color[] s_stops = _BuildStops();

   // -- animation ------------------------------------------------------------

   private DispatcherTimer? _timer;
   private double           _phase;

   public AnimatedSplitter()
   {
      Background = Brushes.Transparent;
   }

   protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
   {
      base.OnAttachedToVisualTree(e);
      _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(TICK_MS),
                                    DispatcherPriority.Background,
                                    (_, _) =>
                                    {
                                       if (!IsWorking) return;
                                       _phase += PHASE_STEP;
                                       if (_phase >= 1.0) _phase -= 1.0;
                                       InvalidateVisual();
                                    });
      _timer.Start();
   }

   protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   {
      _timer?.Stop();
      _timer = null;
      base.OnDetachedFromVisualTree(e);
   }

   protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   {
      base.OnPropertyChanged(change);
      if (change.Property == IsLatchedProperty || change.Property == IsWorkingProperty)
         InvalidateVisual();
   }

   public override void Render(DrawingContext ctx)
   {
      base.Render(ctx);

      var bounds = Bounds;
      double w = bounds.Width;
      double h = bounds.Height;
      if (w <= 0 || h <= 0) return;

      // Base line: 6 px tall when latched (loud "you're live" cue), 3 px
      // otherwise. Centered vertically inside the splitter's row.
      double thickness = IsLatched ? 6.0 : 3.0;
      double y = (h - thickness) / 2.0;
      var baseColor = IsLatched ? s_latched : s_unlatched;
      ctx.FillRectangle(new SolidColorBrush(baseColor), new Rect(0, y, w, thickness));

      if (!IsWorking) return;
      if (_phase >= ACTIVE_END) return; // cooldown breath

      // Streaks (same geometry as the terminal renderer, just on a thinner
      // strip). Two streaks slide inward and collapse at the centerline.
      double t        = _phase / ACTIVE_END;
      double streakLen = STOP_COUNT * STOP_PX;
      double halfW    = w / 2.0;
      double headXL   = t * (halfW + streakLen);
      double headXR   = w - headXL;
      double centerX  = halfW;

      // Streak matches the base-line thickness so it reads as the line
      // itself "lighting up", not as a separate band on top.
      double streakH  = Math.Min(h, thickness);
      double streakY  = (h - streakH) / 2.0;

      for (int i = 0; i < STOP_COUNT; i++)
      {
         var brush = new SolidColorBrush(s_stops[i]);

         // Left streak segment, clipped to [0, centerX].
         double lx     = headXL - streakLen + i * STOP_PX;
         double lStart = Math.Max(lx, 0);
         double lEnd   = Math.Min(lx + STOP_PX, centerX);
         if (lEnd > lStart)
            ctx.FillRectangle(brush, new Rect(lStart, streakY, lEnd - lStart, streakH));

         // Right streak segment, clipped to [centerX, w].
         double rx     = headXR + (STOP_COUNT - 1 - i) * STOP_PX;
         double rStart = Math.Max(rx, centerX);
         double rEnd   = Math.Min(rx + STOP_PX, w);
         if (rEnd > rStart)
            ctx.FillRectangle(brush, new Rect(rStart, streakY, rEnd - rStart, streakH));
      }
   }

   // Interpolate a gradient from tail to head across STOP_COUNT stops. Index
   // 0 = tail (matches the static "live" green); last index = head (bright).
   private static Color[] _BuildStops()
   {
      var stops = new Color[STOP_COUNT];
      for (int i = 0; i < STOP_COUNT; i++)
      {
         double tf = STOP_COUNT == 1 ? 1.0 : (double)i / (STOP_COUNT - 1);
         stops[i] = _Lerp(s_streakTail, s_streakHead, tf);
      }
      return stops;
   }

   private static Color _Lerp(Color a, Color b, double t)
   {
      byte L(byte aa, byte bb) => (byte)(aa + (bb - aa) * t);
      return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
   }
}
