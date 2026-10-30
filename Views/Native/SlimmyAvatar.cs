// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Core;

using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Slimmy in the session header, acting out the live phase (see
/// <see cref="SlimmyActs"/>). Shown only with the "Show Slimmy" app setting.
///
/// Cost is kept near zero on purpose, the UI renders in software:
///   - one timer, ~12 frames a second, and only while an act moves;
///   - idle runs no frames at all between its two short flourishes;
///   - a hidden tab (or minimized window) does not repaint, it only checks
///     once a second whether it became visible again;
///   - with the OS asking for reduced motion every act is one still frame
///     and the timer only runs to notice the switch to sleeping.
/// A repaint covers this control's ~60x32 px and nothing else.
/// </summary>
public sealed class SlimmyAvatar : Control
{
   private const double FRAME_MS       = 83;    // ~12 fps; a cartoon needs no more
   private const double SLEEP_FRAME_MS = 250;   // the Zzz drift slowly
   private const double HIDDEN_POLL_MS = 1000;
   // Figure units -> px: ~30 px tall. Bubbles and the hat may reach a few
   // px past the top edge into the header's padding; nothing clips them.
   private const double SCALE          = 0.36;
   private const double FOOT_Y         = 35;
   // Foot positions that put the leftmost ink at x = 0. Solo spans figure
   // units 0..109 (the thought bubble), the duo the chair's backrest
   // through the subagent's bubble.
   private const double SOLO_X         = 50 * SCALE;
   private const double SOLO_W         = 109 * SCALE;
   private const double DUO_X          = SlimmyRig.DUO_LEFT * SCALE;
   private const double DUO_W          = (SlimmyRig.DUO_LEFT + SlimmyRig.DUO_RIGHT) * SCALE;

   public SlimmyAvatar()
   {
      Width             = SOLO_W;
      Height            = 36;
      VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
      IsHitTestVisible  = false;
      IsVisible         = false;
      _idleSince        = _Now();
   }

   /// <summary>
   /// The loop's live phase, and the running subagent's (null when none).
   /// Cheap to call on every stats refresh; only a change restarts anything.
   /// </summary>
   public void SetPhase(TurnPhase main, TurnPhase? sub)
   {
      bool wasIdle = _main == TurnPhase.Idle && _sub == null;
      bool isIdle  = main  == TurnPhase.Idle && sub  == null;
      if (isIdle && !wasIdle)
         _idleSince = _Now();
      if (main == _main && sub == _sub)
         return;
      _main = main;
      _sub  = sub;
      // The duo needs the chair beside the subagent; the header's
      // status text gives up the room while it runs.
      Width = sub.HasValue && sub.Value != TurnPhase.Idle ? DUO_W : SOLO_W;
      _Sync();
   }

   protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
   {
      base.OnAttachedToVisualTree(e);
      _attached = true;
      AppSettingsStore.Current.Changed += Settings_Changed;
      _Sync();
   }

   protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   {
      AppSettingsStore.Current.Changed -= Settings_Changed;
      _attached = false;
      _StopTimer();
      base.OnDetachedFromVisualTree(e);
   }

   public override void Render(DrawingContext ctx)
   {
      double now = _Now();
      var (main, sub) = SlimmyActs.Resolve(_main, _sub, now - _idleSince);
      double t = _reduced ? 0 : now;
      if (sub.HasValue)
         SlimmyRig.DrawDuo(ctx, DUO_X, FOOT_Y, SCALE, t, sub.Value);
      else
         SlimmyRig.DrawAct(ctx, main, SOLO_X, FOOT_Y, SCALE, t, SlimmyPalette.Main);
   }

   // -- private methods ------------------------------------------------------

   private static double _Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

   private void _Sync()
   {
      bool on = AppSettingsStore.Current.Values.ShowSlimmy;
      IsVisible = on;
      if (!on || !_attached)
      {
         _StopTimer();
         return;
      }
      // Asked again on every change rather than once, so flipping the OS
      // switch takes effect at the next phase change.
      _reduced = CorePlatform.PrefersReducedMotion();
      InvalidateVisual();
      _Schedule();
   }

   // Milliseconds until the next frame worth drawing; null when the picture
   // will not change on its own.
   private double? _NextDelayMs()
   {
      double now  = _Now();
      double idle = now - _idleSince;
      var (main, sub) = SlimmyActs.Resolve(_main, _sub, idle);
      bool idling = sub == null && main == SlimmyAct.Idle;
      double untilSleep = (SlimmyActs.SLEEP_AFTER_SECONDS - idle) * 1000;

      if (_reduced)
         return idling ? untilSleep : null;
      if (sub != null)
         return FRAME_MS;
      switch (main)
      {
         case SlimmyAct.Sleep:
            return SLEEP_FRAME_MS;
         case SlimmyAct.Idle:
         {
            // Flourishes at [0, 0.6) and [5, 5.8) of every ten seconds (see
            // SlimmyRig.PoseFor); a frame just past a window draws its end.
            double c = now % 10;
            if (c < 0.7 || (c >= 5 && c < 5.9))
               return FRAME_MS;
            double next = c < 5 ? 5 - c : 10 - c;
            return Math.Max(FRAME_MS, Math.Min(next * 1000, untilSleep));
         }
         default:
            return FRAME_MS;
      }
   }

   private void _Schedule()
   {
      var delay = _NextDelayMs();
      if (delay == null)
      {
         _StopTimer();
         return;
      }
      if (_timer == null)
      {
         _timer = new DispatcherTimer(DispatcherPriority.Background);
         _timer.Tick += Timer_Tick;
      }
      _timer.Interval = TimeSpan.FromMilliseconds(delay.Value);
      _timer.Start();
   }

   private void _StopTimer() => _timer?.Stop();

   // -- private event handlers -----------------------------------------------

   private void Timer_Tick(object? sender, EventArgs e)
   {
      // A hidden tab keeps its phase but draws nothing; poll slowly until
      // it is shown again, then catch up with one fresh frame.
      if (!IsEffectivelyVisible)
      {
         _timer!.Interval = TimeSpan.FromMilliseconds(HIDDEN_POLL_MS);
         return;
      }
      InvalidateVisual();
      _Schedule();
   }

   // Raised by the settings page, on the UI thread.
   private void Settings_Changed() => _Sync();

   // -- fields ---------------------------------------------------------------

   private TurnPhase        _main = TurnPhase.Idle;
   private TurnPhase?       _sub;
   private double           _idleSince;
   private bool             _attached;
   private bool             _reduced;
   private DispatcherTimer? _timer;
}
