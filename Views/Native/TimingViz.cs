// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Everything that turns <see cref="PhaseTimings"/> and <see cref="TokenReads"/>
/// into pixels and prose: the phase palette, segment lists for a
/// <see cref="SegmentBar"/>, and the tooltip / status strings. Kept in one
/// place so the header bar, the bubble strip and the tool card agree on
/// colors and wording.
///
/// Palette rule: the two bars share hues where the meaning is the same
/// (thinking is violet in both, output is the accent in both, tool exec and
/// tool results are both green), and amber stays reserved for Plan mode.
/// </summary>
public static class TimingViz
{
   // -- phase palette ----------------------------------------------------------

   private static readonly SolidColorBrush s_mcpConn  = new(Color.FromRgb(0x5E, 0x6B, 0x80));
   private static readonly SolidColorBrush s_prefill  = new(Color.FromRgb(0x7A, 0x8C, 0xA8));
   private static readonly SolidColorBrush s_thinking = new(Color.FromRgb(0x9A, 0x7B, 0xD8));
   private static readonly SolidColorBrush s_toolArgs = new(Color.FromRgb(0x4E, 0xC9, 0xD1));
   private static readonly SolidColorBrush s_permWait = new(Color.FromRgb(0x8A, 0x8A, 0x8A));
   private static readonly SolidColorBrush s_toolExec = new(Color.FromRgb(0x6A, 0xC9, 0x6A));

   // -- token palette ----------------------------------------------------------

   private static readonly SolidColorBrush s_readPrompt = new(Color.FromRgb(0x5C, 0x9C, 0xE0));
   private static readonly SolidColorBrush s_readTools  = new(Color.FromRgb(0x6A, 0xC9, 0x6A));
   private static readonly SolidColorBrush s_readImages = new(Color.FromRgb(0xD4, 0x6F, 0xA8));

   public static IBrush BrushFor(TurnPhase phase) => phase switch
   {
      // A dimmer cousin of prefill: both are "waiting before any token", and
      // the pairing reads as one family on the bar.
      TurnPhase.McpConnect     => s_mcpConn,
      TurnPhase.Prefill        => s_prefill,
      TurnPhase.Thinking       => s_thinking,
      TurnPhase.Output         => ThemeBrushes.Accent,
      TurnPhase.ToolArgs       => s_toolArgs,
      TurnPhase.PermissionWait => s_permWait,
      TurnPhase.ToolExec       => s_toolExec,
      _                        => ThemeBrushes.BorderColor,
   };

   // -- segment lists ----------------------------------------------------------

   /// <summary>
   /// One segment per phase, in <see cref="PhaseTimings.Phases"/> order,
   /// values in seconds. Every phase is present (zero-width when unused) so a
   /// live index stays stable while phases come and go.
   /// </summary>
   public static List<SegmentBar.Segment> PhaseSegments(in PhaseTimings t)
   {
      var list = new List<SegmentBar.Segment>(PhaseTimings.Phases.Length);
      foreach (var p in PhaseTimings.Phases)
         list.Add(new SegmentBar.Segment(PhaseTimings.Label(p), BrushFor(p), t[p].TotalSeconds));
      return list;
   }

   /// <summary>Index of <paramref name="phase"/> in <see cref="PhaseSegments"/>, or -1 for Idle.</summary>
   public static int PhaseIndex(TurnPhase phase) =>
      Array.IndexOf(PhaseTimings.Phases, phase);

   /// <summary>
   /// Read categories first (cool hues), then what the model wrote: thinking
   /// and the rest of its completion. Values in tokens.
   /// </summary>
   public static List<SegmentBar.Segment> TokenSegments(in TokenReads reads, int thinkingTokens, int outputTokens)
   {
      return new List<SegmentBar.Segment>
      {
         new("prompt",       s_readPrompt,                    reads.Prompt),
         new("tool results", s_readTools,                     reads.ToolResults),
         new("images",       s_readImages,                    reads.Images),
         new("thinking",     BrushFor(TurnPhase.Thinking),    thinkingTokens),
         new("output",       BrushFor(TurnPhase.Output),      outputTokens),
      };
   }

   // -- text -------------------------------------------------------------------

   /// <summary>"12 ms", "0.8 s", "41 s", "1 m 12 s", "2 h 05 m".</summary>
   public static string FormatDuration(TimeSpan t)
   {
      var inv = CultureInfo.InvariantCulture;
      if (t < TimeSpan.FromSeconds(1))
         return ((int)Math.Round(t.TotalMilliseconds)).ToString(inv) + " ms";
      if (t < TimeSpan.FromSeconds(10))
         return t.TotalSeconds.ToString("0.0", inv) + " s";
      if (t < TimeSpan.FromMinutes(1))
         return ((int)Math.Round(t.TotalSeconds)).ToString(inv) + " s";
      if (t < TimeSpan.FromHours(1))
         return (int)t.TotalMinutes + " m " + t.Seconds.ToString("00", inv) + " s";
      return (int)t.TotalHours + " h " + t.Minutes.ToString("00", inv) + " m";
   }

   public static string FormatTokens(int n)
   {
      var inv = CultureInfo.InvariantCulture;
      if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", inv) + "M";
      if (n >= 1_000)     return (n / 1_000.0).ToString("0.#", inv) + "k";
      return n.ToString(inv);
   }

   /// <summary>"tokens / second" from a count and a span, or null when either is missing.</summary>
   public static string? FormatRate(int tokens, TimeSpan span)
   {
      if (tokens <= 0 || span <= TimeSpan.Zero) return null;
      double r = tokens / span.TotalSeconds;
      var inv = CultureInfo.InvariantCulture;
      return (r >= 100 ? ((int)Math.Round(r)).ToString(inv) : r.ToString("0.0", inv)) + " tok/s";
   }

   /// <summary>
   /// One compact line of the non-zero phases: "prefill 3.2 s · thinking 41 s
   /// · output 6 s". Empty string for empty timings.
   /// </summary>
   public static string Describe(in PhaseTimings t)
   {
      var sb = new StringBuilder();
      foreach (var p in PhaseTimings.Phases)
      {
         var v = t[p];
         if (v <= TimeSpan.Zero) continue;
         if (sb.Length > 0) sb.Append(" · ");
         sb.Append(PhaseTimings.Label(p)).Append(' ').Append(FormatDuration(v));
      }
      return sb.ToString();
   }

   /// <summary>Multi-line tooltip: per-phase rows, the total, and optional extra lines.</summary>
   public static string PhaseTooltip(string title, in PhaseTimings t, TurnPhase live,
                                     TimeSpan liveElapsed, string? extra)
   {
      var sb = new StringBuilder(title);
      sb.Append("\n----------------------------");
      var shown = t;
      shown.Add(live, liveElapsed);
      foreach (var p in PhaseTimings.Phases)
      {
         var v = shown[p];
         if (v <= TimeSpan.Zero && p != live) continue;
         sb.Append('\n').Append(_Pad(PhaseTimings.Label(p), 16)).Append(_Num(FormatDuration(v)));
         if (p == live) sb.Append("  (now)");
      }
      sb.Append('\n').Append(_Pad("busy", 16)).Append(_Num(FormatDuration(shown.Busy)));
      if (!string.IsNullOrEmpty(extra))
         sb.Append("\n----------------------------\n").Append(extra);
      return sb.ToString();
   }

   /// <summary>Multi-line tooltip for the token-provenance bar.</summary>
   public static string TokenTooltip(string title, in TokenReads reads, int thinkingTokens, int outputTokens)
   {
      var sb = new StringBuilder(title);
      sb.Append("\n----------------------------");
      sb.Append('\n').Append(_Pad("read", 16)).Append(_Num(FormatTokens(reads.Total)));
      if (reads.Estimated) sb.Append("  ~estimated");
      sb.Append('\n').Append(_Pad("  prompt", 16)).Append(_Num(FormatTokens(reads.Prompt)));
      sb.Append('\n').Append(_Pad("  tool results", 16)).Append(_Num(FormatTokens(reads.ToolResults)));
      if (reads.Images > 0)
         sb.Append('\n').Append(_Pad("  images", 16)).Append(_Num("~" + FormatTokens(reads.Images)));
      sb.Append('\n').Append(_Pad("written", 16)).Append(_Num(FormatTokens(thinkingTokens + outputTokens)));
      if (thinkingTokens > 0)
         sb.Append('\n').Append(_Pad("  thinking", 16)).Append(_Num(FormatTokens(thinkingTokens)));
      sb.Append('\n').Append(_Pad("  output", 16)).Append(_Num(FormatTokens(outputTokens)));
      return sb.ToString();
   }

   /// <summary>
   /// Rates for a call or a session: prefill throughput over the uncached
   /// prompt, decode throughput over the generation phases, plus the
   /// server's own figures when llama-server reported them.
   /// </summary>
   public static string? Rates(in PhaseTimings t, int uncachedPromptTokens, int completionTokens,
                               LlmUsage? server)
   {
      var sb = new StringBuilder();
      var pre = FormatRate(uncachedPromptTokens, t.Prefill);
      var gen = FormatRate(completionTokens, t.Generation);
      if (pre != null) _Line(sb, "prefill", pre, null);
      if (gen != null) _Line(sb, "decode", gen, null);

      if (server.HasValue && server.Value.HasServerTimings)
      {
         var s = server.Value;
         if (s.ServerPromptTime.HasValue && s.ServerPromptTokens.HasValue)
         {
            var r = FormatRate(s.ServerPromptTokens.Value, s.ServerPromptTime.Value);
            _Line(sb, "server prompt", r ?? "-",
                  FormatTokens(s.ServerPromptTokens.Value) + " tok in "
                  + FormatDuration(s.ServerPromptTime.Value));

            // What the client waited beyond the server's own prompt work:
            // model load, queueing, network. Worth a line once it is visible.
            var overhead = t.Prefill - s.ServerPromptTime.Value;
            if (overhead > TimeSpan.FromMilliseconds(250))
               _Line(sb, "unaccounted", FormatDuration(overhead), "load / queue / network");
         }
         if (s.ServerPredictedTime.HasValue && s.ServerPredictedTokens.HasValue)
         {
            var r = FormatRate(s.ServerPredictedTokens.Value, s.ServerPredictedTime.Value);
            _Line(sb, "server decode", r ?? "-",
                  FormatTokens(s.ServerPredictedTokens.Value) + " tok in "
                  + FormatDuration(s.ServerPredictedTime.Value));
         }
      }
      return sb.Length > 0 ? sb.ToString() : null;
   }

   /// <summary>
   /// Wrap tooltip text in a monospace block so the padded columns line up.
   /// Pass the result to <c>ToolTip.SetTip</c> in place of the string.
   /// </summary>
   public static Control MonoTip(string text) => new TextBlock
   {
      Text       = text,
      FontFamily = MONO_FONT,
      FontSize   = 11,
   };

   private static readonly FontFamily MONO_FONT =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   // Column layout for the monospace tooltips: a 16-wide label, a value
   // right-aligned in 11, and an optional note after two spaces.
   private const int LABEL_W = 16;
   private const int VALUE_W = 11;

   private static string _Pad(string s, int width) =>
      s.Length >= width ? s + " " : s.PadRight(width);

   private static string _Num(string s) => s.PadLeft(VALUE_W);

   private static void _Line(StringBuilder sb, string label, string value, string? note)
   {
      if (sb.Length > 0) sb.Append('\n');
      sb.Append(_Pad(label, LABEL_W)).Append(_Num(value));
      if (!string.IsNullOrEmpty(note)) sb.Append("  (").Append(note).Append(')');
   }
}
