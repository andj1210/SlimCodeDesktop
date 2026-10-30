// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Views.Native;

public enum SlimmyBreakState
{
   /// <summary>Nobody on stage; counting quiet seconds.</summary>
   Waiting,

   /// <summary>Both figures walk in from the edges.</summary>
   Enter,

   /// <summary>Volleyball over the prompt card.</summary>
   Play,

   /// <summary>Caught: they drop the ball and run for the card.</summary>
   Scatter,

   /// <summary>Standing at the card with clipboards, pretending to work.</summary>
   Work,

   /// <summary>The coast is clear: back to their places.</summary>
   Back,
}

/// <summary>
/// Timeline of the permission-prompt easter egg, kept free of any drawing so
/// it can be tested on a fake clock. Times are seconds on any monotonic
/// clock; the owner calls <see cref="Advance"/> every frame and
/// <see cref="Activity"/> on every mouse move or key press.
///
/// Quiet for <see cref="IDLE_SECONDS"/> after the prompt opened (or after
/// the last input) and the two figures come out to play. Any input while
/// they play sends them to work; once input has stopped for
/// <see cref="QUIET_SECONDS"/> they go back to the game.
/// </summary>
public sealed class SlimmyBreak
{
   public const double IDLE_SECONDS    = 60;
   public const double QUIET_SECONDS   = 60;
   public const double ENTER_SECONDS   = 1.4;
   public const double SCATTER_SECONDS = 0.7;
   public const double BACK_SECONDS    = 0.8;

   public SlimmyBreak(double now)
   {
      Reset(now);
   }

   public SlimmyBreakState State => _state;

   /// <summary>Seconds since the current state began.</summary>
   public double Elapsed(double now) => now - _since;

   /// <summary>Back to an empty stage, counting from <paramref name="now"/>.</summary>
   public void Reset(double now)
   {
      _state        = SlimmyBreakState.Waiting;
      _since        = now;
      _lastActivity = now;
   }

   /// <summary>
   /// The user did something. True when that caught the figures playing,
   /// i.e. the state just became <see cref="SlimmyBreakState.Scatter"/>.
   /// </summary>
   public bool Activity(double now)
   {
      _lastActivity = now;
      switch (_state)
      {
         case SlimmyBreakState.Enter:
         case SlimmyBreakState.Play:
         case SlimmyBreakState.Back:
            _Go(SlimmyBreakState.Scatter, now);
            return true;
         default:
            return false;
      }
   }

   public void Advance(double now)
   {
      double elapsed = now - _since;
      double quiet   = now - _lastActivity;
      switch (_state)
      {
         case SlimmyBreakState.Waiting:
            if (quiet >= IDLE_SECONDS) _Go(SlimmyBreakState.Enter, now);
            break;
         case SlimmyBreakState.Enter:
            if (elapsed >= ENTER_SECONDS) _Go(SlimmyBreakState.Play, now);
            break;
         case SlimmyBreakState.Scatter:
            if (elapsed >= SCATTER_SECONDS) _Go(SlimmyBreakState.Work, now);
            break;
         case SlimmyBreakState.Work:
            if (quiet >= QUIET_SECONDS) _Go(SlimmyBreakState.Back, now);
            break;
         case SlimmyBreakState.Back:
            if (elapsed >= BACK_SECONDS) _Go(SlimmyBreakState.Play, now);
            break;
      }
   }

   // -- private methods ------------------------------------------------------

   private void _Go(SlimmyBreakState state, double now)
   {
      _state = state;
      _since = now;
   }

   // -- fields ---------------------------------------------------------------

   private SlimmyBreakState _state;
   private double           _since;
   private double           _lastActivity;
}
