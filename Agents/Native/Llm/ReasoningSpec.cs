// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Which reasoning controls a model's chat template understands. Declared by
/// the user per model — there is no way to ask. The only machine-readable
/// source is the Jinja template in llama-server's /props, and that requires
/// the model to already be loaded, which defeats the purpose of asking.
///
/// A style names the whole level set rather than a mechanism, because the
/// sets differ per template family and are not composable: gpt-oss takes
/// low/medium/high, Qwen 3.8 takes low/medium/xhigh (and aliases 'high' onto
/// 'xhigh'). A future family lands as a new member plus a row in
/// <see cref="ReasoningSpec.For"/>, never as a flag on an existing one.
/// </summary>
public enum ReasoningStyle
{
   /// <summary>
   /// No app-level control: the request carries no reasoning fields at all, so
   /// whatever the llama-swap entry / provider was started with stands. This is
   /// NOT "thinking off" — a server launched with an effort flag keeps applying
   /// it. Off is a level, and only the styles below can express it.
   /// </summary>
   [SettingChoice("Default (keep provider setting)")]
   None,

   /// <summary>Thinking is a boolean. Qwen 3.6 and friends.</summary>
   [SettingChoice("On / off (Qwen 3.6 style)")]
   OnOff,

   /// <summary>Off plus low / medium / xhigh. Qwen 3.8.</summary>
   [SettingChoice("Off, low, medium, xhigh (Qwen 3.8 style)")]
   EffortLMX,

   /// <summary>Off plus low / medium / high / xhigh. Muse.</summary>
   [SettingChoice("Off, low, medium, high, xhigh (Muse style)")]
   EffortLMHX,

   /// <summary>Off plus low / medium / high / xhigh / max. DeepSeek.</summary>
   [SettingChoice("Off, low, medium, high, xhigh, max (DeepSeek style)")]
   EffortLMHXM,
}

/// <summary>
/// One reasoning setting as the user picks it. Deliberately a flat union
/// across every style: the picker for a given model shows only that style's
/// <see cref="ReasoningSpec.Choices"/>, and anything else is folded in by
/// <see cref="ReasoningSpec.Clamp"/> rather than rejected. That keeps a
/// persisted level valid after the user re-declares a model's style, and
/// keeps a subagent on a different model from inheriting a level its
/// template would reject.
/// </summary>
public enum ReasoningLevel
{
   /// <summary>
   /// Whatever the declared style considers normal. First so it is also the
   /// zero value: declaring a style without touching the level must not
   /// silently disable thinking on a model whose point is reasoning.
   /// </summary>
   [SettingChoice("Model default")] Default,

   [SettingChoice("Off")]     Off,
   [SettingChoice("On")]      On,
   [SettingChoice("Low")]     Low,
   [SettingChoice("Medium")]  Medium,
   [SettingChoice("High")]    High,
   [SettingChoice("xHigh")]   XHigh,
   [SettingChoice("Max")]     Max,
}

/// <summary>
/// The two wire fields a resolved reasoning setting turns into. Both are
/// always sent together and always stated explicitly — never omitted to let
/// a server default decide. A per-request value overrides the flags the
/// llama-swap entry was started with (verified: an instance running
/// <c>--reasoning off</c> still thinks when the request says so), so relying
/// on the server default would let the UI claim one thing while the model
/// does another.
/// </summary>
/// <param name="Effort">Value for <c>reasoning_effort</c>; null omits it.</param>
/// <param name="EnableThinking">Merged into <c>chat_template_kwargs</c>.</param>
public sealed record ReasoningRequest(string? Effort, bool EnableThinking);

/// <summary>
/// What one <see cref="ReasoningStyle"/> supports, and how it reaches the
/// wire. Hardcoded: three entries cover every template family in use, and a
/// wrong declaration is caught by the catalog's verify probe rather than by
/// letting users invent level sets that no template implements.
///
/// Note "off" is NOT an effort value. Qwen 3.8's template raises a Jinja
/// exception -- surfacing as an HTTP 500, not a silent ignore -- for any
/// effort outside (xhigh, medium, low). Off is therefore a separate
/// transport: <c>enable_thinking: false</c>. The UI hides that seam and
/// presents one list; this type is where the seam actually lives.
/// </summary>
public sealed class ReasoningSpec
{
   private static readonly ReasoningLevel[] s_none      = Array.Empty<ReasoningLevel>();
   private static readonly ReasoningLevel[] s_onOff     = { ReasoningLevel.Off, ReasoningLevel.On };
   private static readonly ReasoningLevel[] s_effortLmx =
   {
      ReasoningLevel.Off, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.XHigh,
   };
   private static readonly ReasoningLevel[] s_effortLmhx =
   {
      ReasoningLevel.Off,  ReasoningLevel.Low, ReasoningLevel.Medium,
      ReasoningLevel.High, ReasoningLevel.XHigh,
   };
   private static readonly ReasoningLevel[] s_effortLmhxm =
   {
      ReasoningLevel.Off,   ReasoningLevel.Low,   ReasoningLevel.Medium,
      ReasoningLevel.High,  ReasoningLevel.XHigh, ReasoningLevel.Max,
   };

   private ReasoningSpec(ReasoningStyle style,
                         IReadOnlyList<ReasoningLevel> choices,
                         ReasoningLevel defaultLevel)
   {
      Style        = style;
      Choices      = choices;
      DefaultLevel = defaultLevel;
   }

   public ReasoningStyle Style { get; }

   /// <summary>Levels this style offers, in picker order. Empty for <see cref="ReasoningStyle.None"/>.</summary>
   public IReadOnlyList<ReasoningLevel> Choices { get; }

   /// <summary>Where an unsupported level folds to, and what a new session starts at.</summary>
   public ReasoningLevel DefaultLevel { get; }

   /// <summary>True when this style has anything to show the user.</summary>
   public bool HasControl => Choices.Count > 0;

   /// <summary>
   /// The <c>chat_template_kwargs</c> that silence thinking. One source for
   /// "how is this model silenced", since two could disagree and the only
   /// symptom would be an auxiliary call quietly costing double.
   /// </summary>
   public string OffKwargs => "{\"enable_thinking\": false}";

   // DefaultLevel is the template's own default where that is known. It is for
   // Qwen 3.8, whose template reads `reasoning_effort|default('xhigh')`. It is
   // NOT known for the Muse and DeepSeek families, so those take the middle of
   // their ladder — a neutral choice, not a measured one.
   public static ReasoningSpec For(ReasoningStyle style) => style switch
   {
      ReasoningStyle.OnOff       => new ReasoningSpec(style, s_onOff,       ReasoningLevel.On),
      ReasoningStyle.EffortLMX   => new ReasoningSpec(style, s_effortLmx,   ReasoningLevel.XHigh),
      ReasoningStyle.EffortLMHX  => new ReasoningSpec(style, s_effortLmhx,  ReasoningLevel.Medium),
      ReasoningStyle.EffortLMHXM => new ReasoningSpec(style, s_effortLmhxm, ReasoningLevel.Medium),
      _                          => new ReasoningSpec(ReasoningStyle.None, s_none, ReasoningLevel.Off),
   };

   /// <summary>
   /// Fold <paramref name="level"/> onto something this style actually
   /// supports. Off survives everywhere (it is in every style's choices);
   /// <see cref="ReasoningLevel.Default"/> and any level belonging to a
   /// different style land on <see cref="DefaultLevel"/>.
   /// </summary>
   public ReasoningLevel Clamp(ReasoningLevel level)
   {
      if (!HasControl)
         return ReasoningLevel.Off;
      foreach (var c in Choices)
      {
         if (c == level)
            return level;
      }
      return DefaultLevel;
   }

   /// <summary>
   /// The wire form of <paramref name="level"/>, or null when this model has
   /// no reasoning control and the request should say nothing at all.
   /// Clamps first, so an out-of-set level can never reach the endpoint.
   /// </summary>
   public ReasoningRequest? Resolve(ReasoningLevel level)
   {
      if (!HasControl)
         return null;

      var eff = Clamp(level);
      if (eff == ReasoningLevel.Off)
         return new ReasoningRequest(null, false);

      return new ReasoningRequest(_WireEffort(eff), true);
   }

   // -- private ----------------------------------------------------------

   /// <summary>
   /// Null for <see cref="ReasoningStyle.OnOff"/>: those templates take the
   /// boolean only, and sending an effort they don't read is noise at best.
   /// </summary>
   private string? _WireEffort(ReasoningLevel level)
   {
      if (Style == ReasoningStyle.OnOff)
         return null;

      // Safe as a total map because Resolve clamps before calling: the level
      // is guaranteed to be one this style lists.
      return level switch
      {
         ReasoningLevel.Low    => "low",
         ReasoningLevel.Medium => "medium",
         ReasoningLevel.High   => "high",
         ReasoningLevel.Max    => "max",
         _                     => "xhigh",
      };
   }
}
