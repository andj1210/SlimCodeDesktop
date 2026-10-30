// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Core;

using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// The permission-prompt easter egg: left alone with a prompt open, two
/// Slimmys come out and play volleyball, the prompt card being the net.
/// The first mouse move or key press anywhere in the window catches them;
/// they drop the ball and stand at the card with clipboards until things
/// have been quiet again. Timeline in <see cref="SlimmyBreak"/>.
///
/// Fills the overlay above the dim layer and never takes input. Runs only
/// with the "Show Slimmy" setting on and the OS not asking for reduced
/// motion. While nobody is on stage the timer ticks once a second and draws
/// nothing; frames only run while the figures are out.
/// </summary>
public sealed class SlimmyPlayground : Control
{
   private const double FRAME_MS     = 83;
   private const double WAIT_POLL_MS = 1000;
   private const double SCALE        = 0.8;
   private const double HOME_GAP_MIN = 62;    // card edge -> player while playing...
   private const double HOME_GAP_MAX = 130;   // ...as far out as the tab allows
   private const double EDGE_ROOM    = 40;    // player half width plus a little air
   private const double WORK_GAP     = 34;    // card edge -> player while "working"
   private const double MIN_ROOM     = HOME_GAP_MIN + EDGE_ROOM;
   private const double RALLY_SECONDS = 1.3;  // one crossing of the ball
   private const double BALL_R       = 7;
   private const double HAND_HEIGHT  = 78;    // floor -> raised hands, where the ball is hit
   private const double CLEARANCE    = 10;    // ball edge -> card corner at the closest

   public SlimmyPlayground()
   {
      IsHitTestVisible = false;
      _break = new SlimmyBreak(_Now());
   }

   /// <summary>The prompt card; same parent as this control, so its Bounds share our coordinates.</summary>
   public Control? Card { get; set; }

   /// <summary>A prompt opened: start counting.</summary>
   public void Start()
   {
      Stop();
      if (!AppSettingsStore.Current.Values.ShowSlimmy || CorePlatform.PrefersReducedMotion())
         return;

      _break.Reset(_Now());
      _lastTick = _Now();
      _top = TopLevel.GetTopLevel(this);
      if (_top != null)
      {
         var routes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
         _top.AddHandler(PointerMovedEvent,        Input_Activity, routes, handledEventsToo: true);
         _top.AddHandler(PointerPressedEvent,      Input_Activity, routes, handledEventsToo: true);
         _top.AddHandler(PointerWheelChangedEvent, Input_Activity, routes, handledEventsToo: true);
         _top.AddHandler(KeyDownEvent,             Input_Activity, routes, handledEventsToo: true);
      }
      if (_timer == null)
      {
         _timer = new DispatcherTimer(DispatcherPriority.Background);
         _timer.Tick += Timer_Tick;
      }
      _timer.Interval = TimeSpan.FromMilliseconds(WAIT_POLL_MS);
      _timer.Start();
      _running = true;
   }

   /// <summary>The prompt closed: clear the stage and stop every timer.</summary>
   public void Stop()
   {
      if (_top != null)
      {
         _top.RemoveHandler(PointerMovedEvent,        Input_Activity);
         _top.RemoveHandler(PointerPressedEvent,      Input_Activity);
         _top.RemoveHandler(PointerWheelChangedEvent, Input_Activity);
         _top.RemoveHandler(KeyDownEvent,             Input_Activity);
         _top = null;
      }
      _timer?.Stop();
      _running = false;
      _ballFalling = false;
      InvalidateVisual();
   }

   protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   {
      Stop();
      base.OnDetachedFromVisualTree(e);
   }

   public override void Render(DrawingContext ctx)
   {
      if (!_running || _break.State == SlimmyBreakState.Waiting || !_TryLayout(out var lay))
         return;

      double now = _Now();
      double t   = now;
      // The second player wears the subagent's green.
      var palL   = SlimmyPalette.Main;
      var palR   = SlimmyPalette.Sub;
      switch (_break.State)
      {
         case SlimmyBreakState.Enter:
            _DrawPose(ctx, _lx, lay.Floor, SlimmyRig.Run(t, false), palL);
            _DrawPose(ctx, _rx, lay.Floor, SlimmyRig.Run(t, true),  palR);
            break;

         case SlimmyBreakState.Play:
         {
            _Rally(now, lay, out double u, out bool rightward);
            // The sender just hit (start of a crossing), the receiver is
            // about to (end of it): both jump with their arms up.
            bool leftHits  = rightward ? u < 0.15 : u > 0.85;
            bool rightHits = rightward ? u > 0.85 : u < 0.15;
            double hop = 10 * Math.Sin(Math.PI * Math.Min(1, (u < 0.5 ? u : 1 - u) * 6));
            _DrawPose(ctx, _lx, lay.Floor, SlimmyRig.Volley(leftHits,  leftHits  ? hop : 0, false), palL);
            _DrawPose(ctx, _rx, lay.Floor, SlimmyRig.Volley(rightHits, rightHits ? hop : 0, true),  palR);
            SlimmyRig.DrawBall(ctx, _ballX, _ballY, _break.Elapsed(now) * 6);
            break;
         }

         case SlimmyBreakState.Scatter:
         case SlimmyBreakState.Back:
         {
            // Scatter runs toward the card, Back away from it.
            bool toCard = _break.State == SlimmyBreakState.Scatter;
            _DrawPose(ctx, _lx, lay.Floor, SlimmyRig.Run(t, !toCard), palL);
            _DrawPose(ctx, _rx, lay.Floor, SlimmyRig.Run(t, toCard),  palR);
            _DrawLooseBall(ctx);
            break;
         }

         case SlimmyBreakState.Work:
            _DrawWorker(ctx, _lx, lay.Floor, SlimmyRig.Work(t, false, 0), t, palL);
            _DrawWorker(ctx, _rx, lay.Floor, SlimmyRig.Work(t, true, 1),  t, palR);
            _DrawLooseBall(ctx);
            break;
      }
   }

   // -- private methods ------------------------------------------------------

   private static double _Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

   private readonly record struct Layout(double HomeL, double HomeR, double WorkL, double WorkR,
                                         double Floor, double Apex);

   // Where everyone stands, from the card's current bounds. False when the
   // tab is too narrow to fit a player beside the card.
   private bool _TryLayout(out Layout lay)
   {
      lay = default;
      if (Card == null) return false;
      var card = Card.Bounds;
      double w = Bounds.Width;
      if (card.Left < MIN_ROOM || w - card.Right < MIN_ROOM) return false;

      // Players stand as far out as the narrower side allows: the farther
      // apart, the flatter the arc that still clears the card.
      double room  = Math.Min(card.Left, w - card.Right);
      double gap   = Math.Clamp(room - EDGE_ROOM, HOME_GAP_MIN, HOME_GAP_MAX);
      double floor = Math.Min(card.Bottom + 70, Bounds.Height - 12);
      double homeL = card.Left - gap, homeR = card.Right + gap;

      // Apex high enough that the ball passes the card's top corners with
      // CLEARANCE to spare. The arc is symmetric, so one corner decides.
      double x0 = homeL + 14, x1 = homeR - 14, y0 = floor - HAND_HEIGHT;
      double uc     = (card.Left - x0) / (x1 - x0);
      double corner = card.Top - BALL_R - CLEARANCE;
      double rise   = Math.Max(y0 - (card.Top - 34), (y0 - corner) / (4 * uc * (1 - uc)));
      double apex   = Math.Max(BALL_R + 4, y0 - rise);

      lay = new Layout(homeL, homeR, card.Left - WORK_GAP, card.Right + WORK_GAP,
                       floor, apex);
      return true;
   }

   // Position along the current crossing (0..1) and its direction; puts the
   // ball on its parabola from one player's raised hands to the other's.
   private void _Rally(double now, in Layout lay, out double u, out bool rightward)
   {
      double el = _break.Elapsed(now);
      int    n  = (int)(el / RALLY_SECONDS);
      u         = el % RALLY_SECONDS / RALLY_SECONDS;
      rightward = n % 2 == 0;

      double x0 = lay.HomeL + 14, x1 = lay.HomeR - 14;
      double y0 = lay.Floor - HAND_HEIGHT;
      _ballX  = rightward ? x0 + (x1 - x0) * u : x1 - (x1 - x0) * u;
      _ballY  = y0 - 4 * (y0 - lay.Apex) * u * (1 - u);
      _ballVx = (rightward ? 1 : -1) * (x1 - x0) / RALLY_SECONDS;
   }

   // Moves the players and the ball one frame on.
   private void _Step(double now, double dt, in Layout lay)
   {
      double el = _break.Elapsed(now);
      switch (_break.State)
      {
         case SlimmyBreakState.Enter:
         {
            double u = Math.Min(1, el / SlimmyBreak.ENTER_SECONDS);
            _lx = -40 + (lay.HomeL + 40) * u;
            _rx = Bounds.Width + 40 - (Bounds.Width + 40 - lay.HomeR) * u;
            break;
         }
         case SlimmyBreakState.Play:
            _lx = lay.HomeL;
            _rx = lay.HomeR;
            _ballFalling = false;
            _ballServed  = true;
            _Rally(now, lay, out _, out _);
            break;
         case SlimmyBreakState.Scatter:
         {
            double u = Math.Min(1, el / SlimmyBreak.SCATTER_SECONDS);
            _lx = _lFrom + (lay.WorkL - _lFrom) * u;
            _rx = _rFrom + (lay.WorkR - _rFrom) * u;
            break;
         }
         case SlimmyBreakState.Work:
            _lx = lay.WorkL;
            _rx = lay.WorkR;
            break;
         case SlimmyBreakState.Back:
         {
            double u = Math.Min(1, el / SlimmyBreak.BACK_SECONDS);
            _lx = lay.WorkL + (lay.HomeL - lay.WorkL) * u;
            _rx = lay.WorkR + (lay.HomeR - lay.WorkR) * u;
            break;
         }
      }

      // A dropped ball falls, bounces lower each time and rolls out.
      if (_ballFalling)
      {
         double ground = lay.Floor - BALL_R + 1;
         _ballVy += 900 * dt;
         _ballX  += _ballVx * dt;
         _ballY  += _ballVy * dt;
         _ballVx *= Math.Pow(0.83, dt);
         if (_ballY > ground)
         {
            _ballY  = ground;
            _ballVy = -0.45 * _ballVy;
         }
         _ballX = Math.Clamp(_ballX, BALL_R, Bounds.Width - BALL_R);
      }
   }

   // Caught before the first serve there is no ball to drop.
   private void _DrawLooseBall(DrawingContext ctx)
   {
      if (_ballServed)
         SlimmyRig.DrawBall(ctx, _ballX, _ballY, _ballX / 6);
   }

   private static void _DrawPose(DrawingContext ctx, double x, double floor, in SlimmyPose pose,
                                 SlimmyPalette pal)
   {
      using (ctx.PushTransform(SlimmyRig.Place(x, floor, SCALE, pose)))
         SlimmyRig.DrawFigure(ctx, pose, pal);
   }

   private static void _DrawWorker(DrawingContext ctx, double x, double floor, in SlimmyPose pose,
                                   double t, SlimmyPalette pal)
   {
      using (ctx.PushTransform(SlimmyRig.Place(x, floor, SCALE, pose)))
      {
         SlimmyRig.DrawFigure(ctx, pose, pal);
         SlimmyRig.DrawClipboard(ctx, t, pose.R);
         SlimmyRig.DrawSweat(ctx, t);
      }
   }

   // -- private event handlers -----------------------------------------------

   private void Timer_Tick(object? sender, EventArgs e)
   {
      double now = _Now();
      double dt  = Math.Min(0.2, now - _lastTick);
      _lastTick  = now;

      // A hidden tab, or no room beside the card: nobody plays, and the
      // count starts over once they could.
      Layout lay = default;
      if (!IsEffectivelyVisible || !_TryLayout(out lay))
      {
         if (_break.State != SlimmyBreakState.Waiting)
         {
            _break.Reset(now);
            InvalidateVisual();
         }
         _timer!.Interval = TimeSpan.FromMilliseconds(WAIT_POLL_MS);
         return;
      }

      var before = _break.State;
      _break.Advance(now);
      if (_break.State == SlimmyBreakState.Waiting)
      {
         _timer!.Interval = TimeSpan.FromMilliseconds(WAIT_POLL_MS);
         return;
      }
      if (before == SlimmyBreakState.Waiting)
      {
         _lx          = -40;
         _rx          = Bounds.Width + 40;
         _ballServed  = false;
         _ballFalling = false;
      }
      _Step(now, dt, lay);
      _timer!.Interval = TimeSpan.FromMilliseconds(FRAME_MS);
      InvalidateVisual();
   }

   private void Input_Activity(object? sender, RoutedEventArgs e)
   {
      if (!_running) return;
      if (!_break.Activity(_Now())) return;

      // Caught: everyone runs from where they are, the ball drops from
      // wherever it was.
      _lFrom = _lx;
      _rFrom = _rx;
      if (!_ballFalling)
      {
         _ballFalling = true;
         _ballVy      = 0;
      }
   }

   // -- fields ---------------------------------------------------------------

   private readonly SlimmyBreak _break;
   private DispatcherTimer?     _timer;
   private TopLevel?            _top;
   private bool                 _running;
   private double               _lastTick;

   private double _lx, _rx;          // player foot positions
   private double _lFrom, _rFrom;    // where the scatter started
   private double _ballX, _ballY, _ballVx, _ballVy;
   private bool   _ballFalling;
   private bool   _ballServed;
}
