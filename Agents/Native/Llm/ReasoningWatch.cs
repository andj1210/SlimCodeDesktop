// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Repetition metrics for one reasoning trace, as of the last fed fragment.
/// Purely derived — never persisted; recomputed from the trace text on
/// replay so a stored session shows the same reading it did live.
/// </summary>
public readonly struct ReasoningStats
{
   /// <summary>
   /// Fraction of the recent shingle window that repeats text from far enough
   /// back to look like a cycle rather than local emphasis (0..1).
   /// </summary>
   public double RepetitionRatio { get; init; }

   /// <summary>
   /// How many consecutive shingles the ratio has held above the trip level.
   /// A real loop sustains; a dense restating patch spikes and decays.
   /// </summary>
   public int SustainRun { get; init; }

   /// <summary>Rough token count of the trace so far (character heuristic).</summary>
   public int ApproxTokens { get; init; }

   /// <summary>Length gate the trace is measured against.</summary>
   public int ThresholdTokens { get; init; }

   /// <summary>True once the trace is long enough for the trip to be armed.</summary>
   public bool LengthGatePassed { get; init; }

   /// <summary>True when enough shingles exist for the ratio to mean anything.</summary>
   public bool HasSignal { get; init; }

   /// <summary>Both gates passed: this trace looks like an overthinking loop.</summary>
   public bool Tripped { get; init; }

   /// <summary>
   /// Barometer fill, 0..1. Scaled so 1.0 lands exactly on the trip ratio —
   /// a full bar means "repetitive enough to trip", independent of whether
   /// the length gate has armed yet.
   /// </summary>
   public double Score { get; init; }
}

/// <summary>
/// Cheap, incremental overthinking-loop detector for a model's reasoning
/// channel. Feed streamed fragments with <see cref="Append"/>; read
/// <see cref="Snapshot"/> whenever a reading is wanted.
///
/// Purely an observation tool: nothing acts on a trip. It exists to make a
/// circling trace visible in the UI while it is happening, so the reader can
/// decide whether to cancel the turn.
///
/// Three gates, all deliberately local (no inference involved):
///   * length  — the trace exceeds <see cref="ThresholdTokens"/>. A short
///               trace is considered sane no matter how it reads.
///   * repetition — of the last <see cref="RECENT_SHINGLES"/> word-shingles,
///               what fraction repeats text from at least
///               <see cref="MIN_REPEAT_DISTANCE"/> shingles back. The
///               distance rule is what separates a cycle from legitimate
///               local restating: a model re-stating a constraint within the
///               same paragraph repeats at a distance of tens of words, while
///               a genuine loop reaches back hundreds.
///   * sustain — the ratio has to HOLD above the trip level for
///               <see cref="SUSTAIN_SHINGLES"/> consecutive shingles. A dense
///               patch spikes across the line and decays; a loop does not.
///
/// The distance and sustain rules exist because the first field trial tripped
/// on legitimately dense reasoning: a bar that goes red on a trace that is
/// merely dense stops carrying information.
///
/// Cost is O(1) amortized per appended character with no per-call
/// allocation, so it is safe to run on every streamed delta.
/// </summary>
public sealed class ReasoningWatch
{
   /// <summary>Traces shorter than this are never flagged, however repetitive.</summary>
   public const int DEFAULT_THRESHOLD_TOKENS = 4096;

   // -- tuning ---------------------------------------------------------------
   // Deliberately conservative: a bar that cries wolf is worse than one that
   // is late, so precision matters more than recall. These are the knobs to
   // move once real sessions have been observed through the UI meter.

   private const int    SHINGLE_WORDS   = 8;    // words per hashed window
   private const int    RECENT_SHINGLES = 512;  // sliding window for the ratio
   private const int    MIN_SHINGLES    = 64;   // below this the ratio is noise
   private const double TRIP_RATIO      = 0.40; // duplicate fraction that trips
   private const double CHARS_PER_TOKEN = 3.6;  // token estimate for the gate

   // A repeat closer than this is treated as local emphasis, not a cycle, and
   // does not count toward the ratio at all.
   private const int MIN_REPEAT_DISTANCE = 120;

   // Hysteresis. The sustain run survives dips down to RELEASE_RATIO, and only
   // resets below it — without that, a loop with intervening text oscillates
   // across TRIP_RATIO and the run never accumulates (measured: a genuine
   // 8-cycle loop escaped detection entirely on a plain >= TRIP_RATIO run).
   private const double RELEASE_RATIO   = 0.28;
   private const int    SUSTAIN_SHINGLES = 64;

   private const ulong FNV_OFFSET = 14695981039346656037;
   private const ulong FNV_PRIME  = 1099511628211;

   public ReasoningWatch(int thresholdTokens = DEFAULT_THRESHOLD_TOKENS)
   {
      ThresholdTokens = thresholdTokens > 0 ? thresholdTokens : DEFAULT_THRESHOLD_TOKENS;
   }

   /// <summary>Length gate in tokens. Settable so per-model config can drive it.</summary>
   public int ThresholdTokens { get; set; }

   /// <summary>
   /// Consume the next fragment of reasoning text. Fragments may split words
   /// arbitrarily — a partial word is buffered until its terminator arrives.
   /// </summary>
   public void Append(string? delta)
   {
      if (string.IsNullOrEmpty(delta))
         return;

      _totalChars += delta!.Length;

      for (int i = 0; i < delta.Length; i++)
      {
         char c = delta[i];
         if (char.IsLetterOrDigit(c))
            _pending.Append(char.ToLowerInvariant(c));
         else
            _FlushWord();   // whitespace and punctuation both separate words
      }
   }

   /// <summary>
   /// Current reading. Pure arithmetic over already-accumulated counters —
   /// cheap enough to call once per streamed delta.
   /// </summary>
   public ReasoningStats Snapshot()
   {
      double rep = _recentFilled > 0 ? (double)_recentDupes / _recentFilled : 0.0;
      int    tok = (int)(_totalChars / CHARS_PER_TOKEN);

      // The sliding window must ALSO be adequately filled, not just the total
      // count — a 1-of-2 reading would otherwise register as a 50% ratio.
      bool signal   = _shingleCount >= MIN_SHINGLES && _recentFilled >= MIN_SHINGLES;
      bool lengthOk = tok >= ThresholdTokens;

      return new ReasoningStats
      {
         RepetitionRatio  = rep,
         SustainRun       = _sustainRun,
         ApproxTokens     = tok,
         ThresholdTokens  = ThresholdTokens,
         LengthGatePassed = lengthOk,
         HasSignal        = signal,
         Tripped          = signal && lengthOk && rep >= TRIP_RATIO
                            && _sustainRun >= SUSTAIN_SHINGLES,
         Score            = Math.Clamp(rep / TRIP_RATIO, 0.0, 1.0),
      };
   }

   /// <summary>One-shot evaluation of a complete trace (replay path).</summary>
   public static ReasoningStats Evaluate(string? text, int thresholdTokens = DEFAULT_THRESHOLD_TOKENS)
   {
      var w = new ReasoningWatch(thresholdTokens);
      w.Append(text);
      return w.Snapshot();
   }

   // -- private methods ------------------------------------------------------

   // Close the buffered word and, once SHINGLE_WORDS of them exist, hash the
   // trailing window into one shingle and record whether it is a repeat.
   private void _FlushWord()
   {
      if (_pending.Length == 0)
         return;

      ulong h = FNV_OFFSET;
      for (int i = 0; i < _pending.Length; i++)
         h = (h ^ _pending[i]) * FNV_PRIME;
      _pending.Clear();

      _wordRing[_wordCount % SHINGLE_WORDS] = h;
      _wordCount++;
      if (_wordCount < SHINGLE_WORDS)
         return;

      // Combine the trailing SHINGLE_WORDS hashes in order, so the shingle
      // is sensitive to word order (a reordered sentence is not a repeat).
      ulong s = FNV_OFFSET;
      for (int i = 0; i < SHINGLE_WORDS; i++)
         s = (s ^ _wordRing[(_wordCount - SHINGLE_WORDS + i) % SHINGLE_WORDS]) * FNV_PRIME;

      _PushShingle(s);
   }

   private void _PushShingle(ulong h)
   {
      // Only a repeat of something far enough back counts. Local restating
      // ("...so again, the path must be absolute...") is normal reasoning and
      // used to trip the detector on its own.
      bool dup = false;
      if (_firstSeen.TryGetValue(h, out int firstAt))
         dup = (_shingleCount - firstAt) >= MIN_REPEAT_DISTANCE;
      else
         _firstSeen[h] = _shingleCount;

      if (_recentFilled == RECENT_SHINGLES)
      {
         if (_recent[_recentIdx]) _recentDupes--;
      }
      else
      {
         _recentFilled++;
      }

      _recent[_recentIdx] = dup;
      if (dup) _recentDupes++;

      _recentIdx = (_recentIdx + 1) % RECENT_SHINGLES;
      _shingleCount++;

      // Sustain: the run grows while the ratio stays elevated, and only resets
      // when it falls below the release level (see RELEASE_RATIO). Tripping
      // still requires the higher TRIP_RATIO — this only keeps micro-dips from
      // discarding an accumulated run.
      double rep = _recentFilled > 0 ? (double)_recentDupes / _recentFilled : 0.0;
      if (_shingleCount >= MIN_SHINGLES && rep >= RELEASE_RATIO)
         _sustainRun++;
      else
         _sustainRun = 0;
   }

   // -- fields ---------------------------------------------------------------

   private readonly StringBuilder            _pending   = new();
   // shingle -> index of its first occurrence, so repeat DISTANCE is known.
   private readonly Dictionary<ulong, int>   _firstSeen = new();
   private readonly ulong[]                  _wordRing  = new ulong[SHINGLE_WORDS];
   private readonly bool[]                   _recent    = new bool[RECENT_SHINGLES];

   private int  _wordCount;
   private int  _shingleCount;
   private int  _recentIdx;
   private int  _recentFilled;
   private int  _recentDupes;
   private int  _sustainRun;
   private long _totalChars;
}
