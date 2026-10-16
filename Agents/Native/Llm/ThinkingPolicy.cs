// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>How much of the conversation's captured reasoning is resent.</summary>
public enum ThinkingScope
{
   /// <summary>Never attach <c>reasoning_content</c> to a history message.</summary>
   None,

   /// <summary>
   /// Only the most recent assistant turn carries its trace. The bounded
   /// variant: it covers interleaved-thinking models (which need the previous
   /// turn's thinking to keep tool-calling coherent) without growing the
   /// prompt by every trace the session ever produced.
   /// </summary>
   LastAssistantTurn,

   /// <summary>Every assistant turn carries its own trace.</summary>
   All,
}

/// <summary>
/// Whether captured reasoning is sent back to the provider on later turns.
///
/// Models differ on what they want here and the wire gives no way to ask:
/// some templates re-render a prior <c>reasoning_content</c>, most drop it,
/// and a few endpoints (DeepSeek's own API) reject it outright. The working
/// assumption for now is the benign one — a server that does not support
/// preserved thinking strips the field rather than failing — so the default
/// sends everything and there is deliberately no per-model UI yet. If that
/// assumption breaks on a real endpoint, this is the object that grows a
/// per-model resolution step.
/// </summary>
public sealed class ThinkingPolicy
{
   /// <summary>Which assistant turns carry their trace. Defaults to all of them.</summary>
   public ThinkingScope Scope { get; init; } = ThinkingScope.All;

   /// <summary>Send every captured trace — the current default everywhere.</summary>
   public static ThinkingPolicy Default { get; } = new();

   /// <summary>Send none. For one-off calls that must not inherit conversation state.</summary>
   public static ThinkingPolicy Off { get; } = new() { Scope = ThinkingScope.None };
}
