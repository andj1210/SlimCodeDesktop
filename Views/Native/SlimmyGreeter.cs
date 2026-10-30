// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Core;

using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// The app icon on the About page, drawn rather than loaded so it can move:
/// with "Show Slimmy" on, every 10 to 15 seconds he tugs the front of his
/// hat (<see cref="SlimmyRig.Greeting"/>). Otherwise, and when the OS asks
/// for reduced motion, it is the still icon and no timer runs.
/// </summary>
public sealed class SlimmyGreeter : Control
{
   private const double FRAME_MS      = 83;
   private const double MIN_PAUSE_SEC = 10;
   private const double MAX_PAUSE_SEC = 15;

   public SlimmyGreeter(double size)
   {
      Width            = size;
      Height           = size;
      IsHitTestVisible = false;
   }

   protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
   {
      base.OnAttachedToVisualTree(e);
      AppSettingsStore.Current.Changed += Settings_Changed;
      _Sync();
   }

   protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   {
      AppSettingsStore.Current.Changed -= Settings_Changed;
      _timer?.Stop();
      _greeting = false;
      base.OnDetachedFromVisualTree(e);
   }

   public override void Render(DrawingContext ctx)
   {
      var pose = _greeting ? SlimmyRig.Greeting(_Now() - _greetStart) : SlimmyPose.Rest;
      SlimmyRig.DrawIcon(ctx, pose, Width);
   }

   // -- private methods ------------------------------------------------------

   private static double _Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

   private void _Sync()
   {
      _greeting = false;
      InvalidateVisual();
      if (!AppSettingsStore.Current.Values.ShowSlimmy || CorePlatform.PrefersReducedMotion())
      {
         _timer?.Stop();
         return;
      }
      _Pause();
   }

   // Wait a random 10..15 s before the next greeting.
   private void _Pause()
   {
      if (_timer == null)
      {
         _timer = new DispatcherTimer(DispatcherPriority.Background);
         _timer.Tick += Timer_Tick;
      }
      double sec = MIN_PAUSE_SEC + Random.Shared.NextDouble() * (MAX_PAUSE_SEC - MIN_PAUSE_SEC);
      _timer.Interval = TimeSpan.FromSeconds(sec);
      _timer.Start();
   }

   // -- private event handlers -----------------------------------------------

   private void Timer_Tick(object? sender, EventArgs e)
   {
      double now = _Now();
      if (!_greeting)
      {
         // Nobody to greet on a page that is not showing.
         if (!IsEffectivelyVisible)
         {
            _Pause();
            return;
         }
         _greeting   = true;
         _greetStart = now;
         _timer!.Interval = TimeSpan.FromMilliseconds(FRAME_MS);
      }
      else if (now - _greetStart >= SlimmyRig.GREETING_SECONDS)
      {
         _greeting = false;
         _Pause();
      }
      InvalidateVisual();
   }

   // Raised by the settings page, on the UI thread.
   private void Settings_Changed() => _Sync();

   // -- fields ---------------------------------------------------------------

   private DispatcherTimer? _timer;
   private bool             _greeting;
   private double           _greetStart;
}
