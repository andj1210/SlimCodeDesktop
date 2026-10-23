// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Token totals of one whole subagent run, summed over its calls. Carried on
/// the parent's tool-result message so a resumed session can fold the
/// subagent's share into the session totals and rates without opening the
/// sub-transcript. Its time needs no such carrier: it is already part of the
/// tool message's <see cref="PhaseTimings"/>.
/// </summary>
public struct SubagentTokens
{
   public int PromptTokens;
   public int CachedReadTokens;
   public int CompletionTokens;
   public int ReasoningTokens;
   public int CacheWriteTokens;

   /// <summary>Non-generated tokens the run fed its model, summed over its calls.</summary>
   public TokenReads Reads;

   public int UncachedPromptTokens =>
      PromptTokens > CachedReadTokens ? PromptTokens - CachedReadTokens : 0;

   public bool IsEmpty => PromptTokens == 0 && CompletionTokens == 0;

   public void Add(in LlmUsage usage)
   {
      PromptTokens     += usage.PromptTokens;
      CachedReadTokens += usage.CachedReadTokens;
      CompletionTokens += usage.CompletionTokens;
      ReasoningTokens  += usage.ReasoningTokens ?? 0;
      CacheWriteTokens += usage.CacheWriteTokens;
   }
}
