// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// One arm as the elbow and hand positions; the shoulder is fixed by the
/// body. Points are in figure units (see <see cref="SlimmyRig"/>).
/// </summary>
public readonly record struct SlimmyArm(double Ex, double Ey, double Hx, double Hy);

/// <summary>
/// A bent body for sitting: the torso ends at the hip instead of the foot,
/// then thigh to the knee and shin to the foot.
/// </summary>
public readonly record struct SlimmyLeg(double HipX, double HipY, double KneeX, double KneeY,
                                        double FootX, double FootY);

/// <summary>
/// Everything that moves on the figure. Angles in radians, positive is
/// clockwise; lengths in figure units.
/// </summary>
public readonly record struct SlimmyPose
{
   /// <summary>The icon's own stance: the two chevrons of <c>&lt;/&gt;</c>.</summary>
   public static readonly SlimmyPose Rest = new()
   {
      L = new SlimmyArm(23, 68, 38, 83),
      R = new SlimmyArm(77, 68, 62, 83),
   };

   public SlimmyArm L        { get; init; }
   public SlimmyArm R        { get; init; }
   /// <summary>Null: the icon's straight slash from neck to foot.</summary>
   public SlimmyLeg? Leg     { get; init; }
   /// <summary>Whole figure, around the foot.</summary>
   public double    Tilt     { get; init; }
   public double    HeadTilt { get; init; }
   public double    HeadDy   { get; init; }
   public double    Hat      { get; init; }
   public double    HatLift  { get; init; }
   /// <summary>0..1: front of the hat tipped toward the viewer.</summary>
   public double    HatPitch { get; init; }
   /// <summary>How far the glint has crossed the glasses.</summary>
   public double    Glint    { get; init; }
   public double    Hop      { get; init; }
   /// <summary>Mirror horizontally (faces left).</summary>
   public bool      Flip     { get; init; }
}

/// <summary>Accent of one figure: arms and hat band.</summary>
public sealed class SlimmyPalette
{
   public SlimmyPalette(Color accent)
   {
      var brush = new ImmutableSolidColorBrush(accent);
      Band   = brush;
      ArmPen = new ImmutablePen(brush, 9, null, PenLineCap.Round, PenLineJoin.Round);
   }

   /// <summary>The app icon's blue.</summary>
   public static readonly SlimmyPalette Main = new(Color.Parse("#3DA8E5"));

   /// <summary>The subagent: a different color so the two read as two.</summary>
   public static readonly SlimmyPalette Sub = new(Color.Parse("#5DCAA5"));

   public IBrush Band   { get; }
   public IPen   ArmPen { get; }
}

/// <summary>
/// Draws Slimmy, the app icon's &lt;/&gt; robot, and his props straight into
/// a <see cref="DrawingContext"/>: a handful of lines, rounded rects and
/// cached geometries per figure, no bitmaps and no allocation per frame.
///
/// Figure units are the icon's 100x100 viewBox; the foot sits at (50, 92).
/// <see cref="Place"/> maps that onto the control: the foot lands on (x, y)
/// and 100 units become <c>scale * 100</c> pixels. Props drawn under the same
/// transform use figure units too, so a caller can add its own (the
/// permission break hands out clipboards that way).
///
/// <c>t</c> is seconds on the caller's clock. Every pose is a pure function
/// of it, so a frozen clock is a frozen frame.
/// </summary>
public static class SlimmyRig
{
   // -- colors ---------------------------------------------------------------

   private static readonly IImmutableBrush s_head      = _Brush("#2E2E30");
   private static readonly IImmutableBrush s_line      = _Brush("#CCCCCC");
   private static readonly IImmutableBrush s_shade     = _Brush("#0A0A0A");
   private static readonly IImmutableBrush s_glint     = _Brush("#E8F4FC");
   private static readonly IImmutableBrush s_white     = _Brush("#F4F4F2");
   private static readonly IImmutableBrush s_paper     = _Brush("#ECE7DA");
   private static readonly IImmutableBrush s_pageTurn  = _Brush("#DDD7C8");
   private static readonly IImmutableBrush s_ink       = _Brush("#55534E");
   private static readonly IImmutableBrush s_print     = _Brush("#9A968C");
   private static readonly IImmutableBrush s_wood      = _Brush("#9B6B3E");
   private static readonly IImmutableBrush s_brain     = _Brush("#F2A7BC");
   private static readonly IImmutableBrush s_amber     = _Brush("#EF9F27");
   private static readonly IImmutableBrush s_dim       = _Brush("#9A9A9A");
   private static readonly IImmutableBrush s_steel     = _Brush("#888780");
   private static readonly IImmutableBrush s_drink     = _Brush("#FAC775");
   private static readonly IImmutableBrush s_sweat     = _Brush("#85B7EB");
   private static readonly IImmutableBrush s_tile      = _Brush("#1E1E1F");

   private static readonly IPen s_torsoPen    = _Pen(s_line, 8);
   private static readonly IPen s_visorPen    = _Pen(s_line, 5);
   private static readonly IPen s_brimPen     = _Pen(s_line, 2.5);
   private static readonly IPen s_bridgePen   = _Pen(s_line, 2);
   private static readonly IPen s_glintPen    = _Pen(s_glint, 1.6);
   private static readonly IPen s_pencilPen   = _Pen(s_amber, 2.5);
   private static readonly IPen s_rulePen     = _Pen(s_ink, 0.8);
   private static readonly IPen s_foldPen     = _Pen(_Brush("#C9C3B4"), 1);
   private static readonly IPen s_brainPen    = _Pen(_Brush("#C2577A"), 1.4);
   private static readonly IPen s_questionPen = _Pen(_Brush("#412402"), 2.6);
   private static readonly IPen s_zPen        = _Pen(s_dim, 1.4);
   private static readonly IPen s_chairPen    = _Pen(s_wood, 4);
   private static readonly IPen s_clothPen    = _Pen(_Brush("#D85A30"), 5);
   private static readonly IPen s_strawPen    = _Pen(_Brush("#D4537E"), 1.5);
   private static readonly IPen s_seamPen     = _Pen(s_amber, 1.5);

   // -- shapes ---------------------------------------------------------------

   private static readonly Geometry s_crown =
      StreamGeometry.Parse("M37 26 L39 13 Q44.5 10 50 13 Q55.5 10 61 13 L63 26 Q50 29 37 26 Z");
   private static readonly Geometry s_band =
      StreamGeometry.Parse("M38.2 20 Q50 22.5 61.8 20 L62.5 24 Q50 27 37.5 24 Z");
   private static readonly Geometry s_speechTail = StreamGeometry.Parse("M72 12 L66 24 L80 13 Z");
   private static readonly Geometry s_letter     = StreamGeometry.Parse("M40 74 L78 74 L74 68 L44 68 Z");
   private static readonly Geometry s_brainLines =
      StreamGeometry.Parse("M0 -7 L0 7 M-9 -2 Q-5 -5 -3 0 M9 2 Q5 5 3 0");
   private static readonly Geometry s_question   =
      StreamGeometry.Parse("M77 3 Q77 -1 81 -1 Q85 -1 85 3 Q85 6 81 7 L81 9");
   private static readonly Geometry s_zGlyph     = StreamGeometry.Parse("M0 0 L5 0 L0 5 L5 5");
   // Origin at the hand / at the drop's center.
   private static readonly Geometry s_glass      = StreamGeometry.Parse("M-5 -14 L5 -14 L3 0 L-3 0 Z");
   private static readonly Geometry s_drop       = StreamGeometry.Parse("M0 -5 Q4 0 0 2 Q-4 0 0 -5 Z");
   private static readonly Geometry s_seam       = StreamGeometry.Parse("M-6 -3 Q0 2 4 6 M2 -6 Q6 -3 6 2");
   private static readonly Geometry s_gear       = _BuildGear();

   // Arms behind the head on one side, a drink in the other hand; leaning
   // back into the chair with the knee bent over the front of the seat.
   private static readonly SlimmyPose s_lounge = SlimmyPose.Rest with
   {
      Tilt     = -0.62,
      HeadTilt = 0.2,
      Leg      = new SlimmyLeg(50, 80, 74, 84, 78, 104),
      // Behind the head but below the backrest's top, which shows above it.
      L        = new SlimmyArm(31, 43, 43, 36),
      R        = new SlimmyArm(78, 62, 86, 52),
   };

   // -- placement ------------------------------------------------------------

   /// <summary>
   /// Figure units -> control pixels: foot on (x, y), then the pose's hop,
   /// tilt and mirror around it.
   /// </summary>
   public static Matrix Place(double x, double y, double scale, in SlimmyPose pose) =>
      Matrix.CreateTranslation(-50, -92 - pose.Hop)
    * Matrix.CreateRotation(pose.Tilt)
    * Matrix.CreateScale(pose.Flip ? -scale : scale, scale)
    * Matrix.CreateTranslation(x, y);

   // -- poses ----------------------------------------------------------------

   public static SlimmyPose PoseFor(SlimmyAct act, double t)
   {
      var rest = SlimmyPose.Rest;
      switch (act)
      {
         case SlimmyAct.Idle:
         {
            // One flourish per ten seconds each: the glint, then a hat tip.
            double c = t % 10;
            bool tip = c >= 5 && c < 5.8;
            return rest with
            {
               Glint   = c < 0.6 ? c / 0.6 * 10 : 0,
               Hat     = tip ? -0.3 : 0,
               HatLift = tip ? 3 : 0,
            };
         }
         case SlimmyAct.Sleep:
            return rest with { HeadTilt = 0.18, HeadDy = 2, Hat = 0.2 };
         case SlimmyAct.Prefill:
            return rest with
            {
               HeadTilt = 0.05 * Math.Sin(t * 1.3),
               L        = new SlimmyArm(26, 62, 22, 66),
               R        = new SlimmyArm(74, 62, 78, 66),
            };
         case SlimmyAct.Thinking:
            return rest with { HeadTilt = -0.08, R = new SlimmyArm(76, 64, 60, 49) };
         case SlimmyAct.Output:
            return rest with
            {
               HeadDy = Math.Sin(t * 9),
               R      = new SlimmyArm(76, 62, 86, 50 + 4 * Math.Sin(t * 5)),
            };
         case SlimmyAct.ToolArgs:
            return rest with
            {
               HeadTilt = 0.12,
               L        = new SlimmyArm(28, 66, 42, 72),
               R        = new SlimmyArm(74, 64, 62 + 3 * Math.Sin(t * 14), 71),
            };
         case SlimmyAct.ToolExec:
            return rest with
            {
               HeadTilt = -0.1,
               L        = new SlimmyArm(30, 68, 62, 64),
               R        = new SlimmyArm(70, 68, 38, 64),
            };
         case SlimmyAct.Permission:
            return rest with
            {
               Tilt     = 0.04 * Math.Sin(t * 7),
               HeadTilt = 0.1,
               R        = new SlimmyArm(78, 46, 67, 34),
            };
         default:
            return rest;
      }
   }

   /// <summary>Running, arms swinging; faces left when <paramref name="flip"/>.</summary>
   public static SlimmyPose Run(double t, bool flip)
   {
      double sw = 8 * Math.Sin(t * 14);
      return SlimmyPose.Rest with
      {
         Hop  = Math.Abs(Math.Sin(t * 14)) * 5,
         Tilt = (flip ? -1 : 1) * 0.15,
         Flip = flip,
         L    = new SlimmyArm(23 + sw, 66, 34 + sw * 1.5, 80),
         R    = new SlimmyArm(77 - sw, 66, 66 - sw * 1.5, 80),
      };
   }

   /// <summary>Volleyball stance; <paramref name="hop"/> above zero while hitting.</summary>
   public static SlimmyPose Volley(bool hitting, double hop, bool flip) =>
      SlimmyPose.Rest with
      {
         Hop      = hop,
         HeadTilt = -0.15,
         Flip     = flip,
         L        = hitting ? new SlimmyArm(30, 38, 36, 22) : SlimmyPose.Rest.L,
         R        = hitting ? new SlimmyArm(70, 38, 64, 22) : new SlimmyArm(74, 52, 80, 40),
      };

   /// <summary>Length of <see cref="Greeting"/>.</summary>
   public const double GREETING_SECONDS = 1.8;

   /// <summary>
   /// <paramref name="u"/> seconds into a greeting: the right hand comes up,
   /// tips the hat's front toward the viewer the way some tug a cap, lets go.
   /// A tilt rather than a drop: the brim opens up, the hat hardly sinks.
   /// Rest before 0 and after <see cref="GREETING_SECONDS"/>.
   /// </summary>
   public static SlimmyPose Greeting(double u)
   {
      double reach = _Smooth(_Envelope(u, 0.05, 0.35, 1.45, 1.75));
      double tug   = _Smooth(_Envelope(u, 0.35, 0.6, 1.2, 1.45));
      var rest = SlimmyPose.Rest;
      return rest with
      {
         HatPitch = tug,
         HatLift  = -1.5 * tug,
         HeadDy   = 0.5 * tug,
         R        = new SlimmyArm(_Lerp(rest.R.Ex, 82, reach),
                                  _Lerp(rest.R.Ey, 48, reach),
                                  _Lerp(rest.R.Hx, 68, reach),
                                  _Lerp(rest.R.Hy, 28 + 4 * tug, reach)),
      };
   }

   /// <summary>Busy with a clipboard (see <see cref="DrawClipboard"/>).</summary>
   public static SlimmyPose Work(double t, bool flip, double phase) =>
      SlimmyPose.Rest with
      {
         Flip     = flip,
         HeadTilt = 0.1 * Math.Sin(t * 1.7 + phase),
         L        = new SlimmyArm(28, 64, 34, 70),
         R        = new SlimmyArm(72, 70, 52 + 3 * Math.Sin(t * 14), 66),
      };

   // -- drawing --------------------------------------------------------------

   /// <summary>One figure acting out <paramref name="act"/>, props included.</summary>
   public static void DrawAct(DrawingContext ctx, SlimmyAct act, double x, double y,
                              double scale, double t, SlimmyPalette palette)
   {
      var pose = PoseFor(act, t);
      using (ctx.PushTransform(Place(x, y, scale, pose)))
      {
         DrawFigure(ctx, pose, palette);
         switch (act)
         {
            case SlimmyAct.Sleep:      _DrawZzz(ctx, t);      break;
            case SlimmyAct.Prefill:    _DrawNewspaper(ctx, t); break;
            case SlimmyAct.Thinking:   _DrawThought(ctx, t);  break;
            case SlimmyAct.Output:     _DrawSpeech(ctx, t);   break;
            case SlimmyAct.ToolArgs:   _DrawDeskLetter(ctx, t, pose.R); break;
            case SlimmyAct.ToolExec:
               _DrawGear(ctx, 84, 6, 7, t * 2);
               _DrawGear(ctx, 98, 18, 4.5, -t * 3.2);
               break;
            case SlimmyAct.Permission: _DrawQuestion(ctx, t); break;
         }
      }
   }

   /// <summary>Left of the duo's anchor, in figure units: the chair's backrest.</summary>
   public const double DUO_LEFT  = 76;

   /// <summary>Right of the duo's anchor, in figure units: the subagent's bubbles.</summary>
   public const double DUO_RIGHT = 101;

   /// <summary>
   /// The main figure lounging in a deck chair with a drink, while the
   /// subagent, only a little smaller and in its own color, does the work to
   /// its right. Spans about x - DUO_LEFT * scale .. x + DUO_RIGHT * scale.
   /// </summary>
   public static void DrawDuo(DrawingContext ctx, double x, double y, double scale,
                              double t, SlimmyAct subAct)
   {
      var chair = Matrix.CreateTranslation(-50, -92)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(x - 30 * scale, y);
      using (ctx.PushTransform(chair))
         _DrawChair(ctx);

      var lounge = s_lounge;
      using (ctx.PushTransform(Place(x - 26 * scale, y - 10 * scale, scale * 0.9, lounge)))
      {
         DrawFigure(ctx, lounge, SlimmyPalette.Main);
         _DrawDrink(ctx, lounge.R.Hx, lounge.R.Hy);
      }

      DrawAct(ctx, subAct, x + 54 * scale, y, scale * 0.8, t, SlimmyPalette.Sub);
   }

   /// <summary>
   /// The bare figure, in figure units (push <see cref="Place"/> first). The
   /// right arm goes over the head so a hand can reach the hat brim or chin.
   /// </summary>
   public static void DrawFigure(DrawingContext ctx, in SlimmyPose pose, SlimmyPalette palette)
   {
      var neck = new Point(55, 51);
      if (pose.Leg is { } leg)
      {
         var hip  = new Point(leg.HipX, leg.HipY);
         var knee = new Point(leg.KneeX, leg.KneeY);
         ctx.DrawLine(s_torsoPen, neck, hip);
         ctx.DrawLine(s_torsoPen, hip, knee);
         ctx.DrawLine(s_torsoPen, knee, new Point(leg.FootX, leg.FootY));
      }
      else
      {
         ctx.DrawLine(s_torsoPen, neck, new Point(45, 92));
      }
      _DrawArm(ctx, palette.ArmPen, 38, 53, pose.L);
      _DrawHead(ctx, pose, palette);
      _DrawArm(ctx, palette.ArmPen, 62, 53, pose.R);
   }

   /// <summary>
   /// The app icon at <paramref name="size"/> px, rounded tile included, with
   /// the figure in <paramref name="pose"/>. At rest it matches slimcode.svg.
   /// </summary>
   public static void DrawIcon(DrawingContext ctx, in SlimmyPose pose, double size)
   {
      double s = size / 100;
      using (ctx.PushTransform(Matrix.CreateScale(s, s)))
         ctx.DrawRectangle(s_tile, null, new Rect(2, 2, 96, 96), 22, 22);
      using (ctx.PushTransform(Place(50 * s, 92 * s, s, pose)))
         DrawFigure(ctx, pose, SlimmyPalette.Main);
   }

   /// <summary>Clipboard held against the body, pencil in the right hand.</summary>
   public static void DrawClipboard(DrawingContext ctx, double t, in SlimmyArm hand)
   {
      ctx.DrawRectangle(s_wood, null, new Rect(32, 52, 30, 36));
      ctx.DrawRectangle(s_white, null, new Rect(35, 57, 24, 28));
      ctx.DrawRectangle(s_steel, null, new Rect(42, 50, 10, 5));
      for (int i = 0; i < 4; i++)
      {
         double w = i == 3 ? 6 + (t * 6) % 12 : 18;
         ctx.DrawRectangle(s_print, null, new Rect(38, 61 + i * 6, w, 1.6));
      }
      _DrawPencil(ctx, hand);
   }

   public static void DrawSweat(DrawingContext ctx, double t)
   {
      double y = 24 + (t * 2) % 1 * 10;
      using (ctx.PushTransform(Matrix.CreateTranslation(70, y)))
         ctx.DrawGeometry(s_sweat, null, s_drop);
   }

   /// <summary>The volleyball, in control pixels.</summary>
   public static void DrawBall(DrawingContext ctx, double x, double y, double angle)
   {
      using (ctx.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(x, y)))
      {
         ctx.DrawEllipse(s_white, null, new Point(0, 0), 7, 7);
         ctx.DrawGeometry(null, s_seamPen, s_seam);
      }
   }

   // -- private methods ------------------------------------------------------

   private static void _DrawArm(DrawingContext ctx, IPen pen, double sx, double sy, in SlimmyArm a)
   {
      var elbow = new Point(a.Ex, a.Ey);
      ctx.DrawLine(pen, new Point(sx, sy), elbow);
      ctx.DrawLine(pen, elbow, new Point(a.Hx, a.Hy));
   }

   private static void _DrawHead(DrawingContext ctx, in SlimmyPose p, SlimmyPalette palette)
   {
      var head = Matrix.CreateTranslation(-50, -40)
               * Matrix.CreateRotation(p.HeadTilt)
               * Matrix.CreateTranslation(50, 40 + p.HeadDy);
      using (ctx.PushTransform(head))
      {
         ctx.DrawRectangle(s_head, s_visorPen, new Rect(34, 27, 32, 18), 9, 9);
         ctx.DrawRectangle(s_shade, null, new Rect(38, 31, 11, 8.5), 3.5, 3.5);
         ctx.DrawRectangle(s_shade, null, new Rect(51, 31, 11, 8.5), 3.5, 3.5);
         ctx.DrawLine(s_bridgePen, new Point(49, 34), new Point(51, 34));
         double g = p.Glint;
         ctx.DrawLine(s_glintPen, new Point(40.5 + g, 37.5), new Point(43.5 + g, 34));
         ctx.DrawLine(s_glintPen, new Point(53.5 + g, 37.5), new Point(56.5 + g, 34));

         // The icon's fedora sits tilted 4 degrees.
         var hat = Matrix.CreateTranslation(-50, -26)
                 * Matrix.CreateRotation(-4 * Math.PI / 180 + p.Hat)
                 * Matrix.CreateTranslation(50, 26 - p.HatLift);
         using (ctx.PushTransform(hat))
         {
            // Pitched toward the viewer the brim opens from a flat oval
            // toward a circle and the crown foreshortens behind it.
            var brimCenter = new Point(50, 26.5);
            double brimRy  = 4.5 + 2.5 * p.HatPitch;
            ctx.DrawEllipse(s_head, s_brimPen, brimCenter, 23, brimRy);
            var squash = Matrix.CreateTranslation(0, -26.5)
                       * Matrix.CreateScale(1, 1 - 0.15 * p.HatPitch)
                       * Matrix.CreateTranslation(0, 26.5);
            using (ctx.PushTransform(squash))
            {
               ctx.DrawGeometry(s_head, s_brimPen, s_crown);
               ctx.DrawGeometry(palette.Band, null, s_band);
            }
            // The near half of the brim passes in front of the crown.
            if (p.HatPitch > 0)
            {
               using (ctx.PushClip(new Rect(20, 26.5, 60, brimRy + 3)))
                  ctx.DrawEllipse(s_head, s_brimPen, brimCenter, 23, brimRy);
            }
         }
      }
   }

   private static void _DrawPencil(DrawingContext ctx, in SlimmyArm hand) =>
      ctx.DrawLine(s_pencilPen, new Point(hand.Hx, hand.Hy), new Point(hand.Hx + 5, hand.Hy - 9));

   private static void _DrawNewspaper(DrawingContext ctx, double t)
   {
      ctx.DrawRectangle(s_paper, null, new Rect(20, 48, 60, 38));
      ctx.DrawRectangle(s_ink, null, new Rect(24, 52, 52, 5));
      for (int i = 0; i < 4; i++)
      {
         ctx.DrawRectangle(s_print, null, new Rect(24, 61 + i * 6, 24, 2));
         ctx.DrawRectangle(s_print, null, new Rect(52, 61 + i * 6, 24, 2));
      }
      ctx.DrawLine(s_foldPen, new Point(50, 48), new Point(50, 86));

      // Turn a page every few seconds: the right half swings over to the left.
      double f = t % 3.5 / 3.5;
      if (f > 0.82)
      {
         double k = Math.Cos((f - 0.82) / 0.18 * Math.PI);
         double edge = 50 + 30 * k;
         ctx.DrawRectangle(s_pageTurn, null, new Rect(Math.Min(50, edge), 48, Math.Abs(30 * k), 38));
      }

      // The hands, over the paper's edges.
      ctx.DrawEllipse(SlimmyPalette.Main.Band, null, new Point(20, 66), 4.5, 4.5);
      ctx.DrawEllipse(SlimmyPalette.Main.Band, null, new Point(80, 66), 4.5, 4.5);
   }

   private static void _DrawThought(DrawingContext ctx, double t)
   {
      ctx.DrawEllipse(s_white, null, new Point(70, 22), 2.5, 2.5);
      ctx.DrawEllipse(s_white, null, new Point(77, 13), 4, 4);
      double k = 1 + 0.06 * Math.Sin(t * 4);
      using (ctx.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(90, -6)))
      {
         ctx.DrawEllipse(s_white, null, new Point(0, 0), 19, 14);
         ctx.DrawEllipse(s_brain, null, new Point(-5, 0), 8, 8);
         ctx.DrawEllipse(s_brain, null, new Point(5, 0), 8, 8);
         ctx.DrawGeometry(null, s_brainPen, s_brainLines);
      }
   }

   private static void _DrawSpeech(DrawingContext ctx, double t)
   {
      ctx.DrawRectangle(s_white, null, new Rect(68, -8, 38, 22), 7, 7);
      ctx.DrawGeometry(s_white, null, s_speechTail);
      int on = (int)(t * 4) % 3;
      for (int i = 0; i < 3; i++)
         ctx.DrawEllipse(i == on ? s_head : s_dim, null, new Point(78 + i * 9, 3), 2.6, 2.6);
   }

   private static void _DrawDeskLetter(DrawingContext ctx, double t, in SlimmyArm hand)
   {
      ctx.DrawRectangle(s_wood, null, new Rect(8, 74, 84, 6));
      ctx.DrawRectangle(s_wood, null, new Rect(14, 80, 6, 14));
      ctx.DrawRectangle(s_wood, null, new Rect(80, 80, 6, 14));
      ctx.DrawGeometry(s_white, null, s_letter);
      int lines = (int)(t % 6 / 1.5);
      for (int i = 0; i < lines; i++)
      {
         double y = 69.5 + i * 1.3;
         ctx.DrawLine(s_rulePen, new Point(47, y), new Point(71, y));
      }
      _DrawPencil(ctx, hand);
   }

   private static void _DrawQuestion(DrawingContext ctx, double t)
   {
      ctx.DrawRectangle(s_amber, null, new Rect(70, -6, 22, 24), 6, 6);
      double bob = Math.Round(Math.Sin(t * 5));
      using (ctx.PushTransform(Matrix.CreateTranslation(0, bob)))
      {
         ctx.DrawGeometry(null, s_questionPen, s_question);
         ctx.DrawEllipse(s_questionPen.Brush, null, new Point(81, 13), 1.6, 1.6);
      }
   }

   private static void _DrawZzz(DrawingContext ctx, double t)
   {
      for (int i = 0; i < 3; i++)
      {
         double u = (t * 0.5 + i / 3.0) % 1;
         double k = 1.2 + 0.4 * i;
         var m = Matrix.CreateScale(k, k)
               * Matrix.CreateTranslation(72 + i * 7 + u * 6, 14 - i * 9 - u * 10);
         using (ctx.PushOpacity(1 - u))
         using (ctx.PushTransform(m))
            ctx.DrawGeometry(null, s_zPen, s_zGlyph);
      }
   }

   private static void _DrawGear(DrawingContext ctx, double x, double y, double r, double angle)
   {
      var m = Matrix.CreateScale(r, r) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(x, y);
      using (ctx.PushTransform(m))
         ctx.DrawGeometry(s_steel, null, s_gear);
   }

   private static void _DrawChair(DrawingContext ctx)
   {
      // Side view, facing right: reclined backrest into the seat, a back leg
      // that stops at the backrest, a front leg under the seat.
      ctx.DrawLine(s_chairPen, new Point(16, 96), new Point(26, 70));
      ctx.DrawLine(s_chairPen, new Point(62, 96), new Point(56, 81));
      ctx.DrawLine(s_chairPen, new Point(8, 40),  new Point(34, 84));
      ctx.DrawLine(s_chairPen, new Point(34, 84), new Point(66, 81));
      ctx.DrawLine(s_clothPen, new Point(10, 44), new Point(33, 80));
      ctx.DrawLine(s_clothPen, new Point(33, 80), new Point(63, 78));
   }

   private static void _DrawDrink(DrawingContext ctx, double hx, double hy)
   {
      using (ctx.PushTransform(Matrix.CreateTranslation(hx, hy)))
      {
         ctx.DrawGeometry(s_drink, null, s_glass);
         ctx.DrawLine(s_strawPen, new Point(2, -14), new Point(6, -22));
      }
   }

   // Unit gear: eight teeth out to 1.45, body radius 1, a hole of 0.4.
   private static Geometry _BuildGear()
   {
      var geom = new StreamGeometry();
      using (var c = geom.Open())
      {
         c.SetFillRule(FillRule.EvenOdd);
         const int N = 32;
         for (int k = 0; k <= N; k++)
         {
            double a = k * 2 * Math.PI / N;
            double r = k % 4 < 2 ? 1.45 : 1.0;
            var p = new Point(r * Math.Cos(a), r * Math.Sin(a));
            if (k == 0) c.BeginFigure(p, isFilled: true);
            else        c.LineTo(p);
         }
         c.EndFigure(isClosed: true);

         const int H = 12;
         for (int k = 0; k <= H; k++)
         {
            double a = k * 2 * Math.PI / H;
            var p = new Point(0.4 * Math.Cos(a), 0.4 * Math.Sin(a));
            if (k == 0) c.BeginFigure(p, isFilled: true);
            else        c.LineTo(p);
         }
         c.EndFigure(isClosed: true);
      }
      return geom;
   }

   // 0 before a, ramps up to 1 at b, holds until c, back to 0 at d.
   private static double _Envelope(double u, double a, double b, double c, double d)
   {
      if (u <= a || u >= d) return 0;
      if (u < b) return (u - a) / (b - a);
      if (u <= c) return 1;
      return 1 - (u - c) / (d - c);
   }

   private static double _Smooth(double k) => k * k * (3 - 2 * k);

   private static double _Lerp(double from, double to, double k) => from + (to - from) * k;

   private static IImmutableBrush _Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

   private static IPen _Pen(IImmutableBrush brush, double width) =>
      new ImmutablePen(brush, width, null, PenLineCap.Round, PenLineJoin.Round);
}
