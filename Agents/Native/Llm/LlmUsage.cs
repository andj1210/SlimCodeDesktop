// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Token accounting for one assistant turn, normalized across providers.
///
/// <see cref="CachedReadTokens"/> is the subset of <see cref="PromptTokens"/>
/// served from a prompt cache (billed at the cheaper cache-read rate). It is
/// sourced from whichever field the provider populates —
/// <c>prompt_tokens_details.cached_tokens</c> (OpenAI / cortecs) or the
/// DeepSeek-native <c>prompt_cache_hit_tokens</c>. Zero means either a full
/// cache miss or a provider that doesn't report caching.
///
/// <see cref="CacheWriteTokens"/> is only populated by providers that bill a
/// separate cache-creation step (Anthropic-style, sometimes surfaced over an
/// OpenAI-compatible schema). Zero on the automatic-caching providers.
/// </summary>
public readonly struct LlmUsage
{
   /// <summary>Full prompt size for the request, including any cached prefix.</summary>
   public int PromptTokens { get; init; }

   /// <summary>Tokens generated this turn (includes reasoning tokens, if any).</summary>
   public int CompletionTokens { get; init; }

   /// <summary>Subset of <see cref="CompletionTokens"/> spent on chain-of-thought. Null when not reported.</summary>
   public int? ReasoningTokens { get; init; }

   /// <summary>Subset of <see cref="PromptTokens"/> served from cache (cache-read rate).</summary>
   public int CachedReadTokens { get; init; }

   /// <summary>Tokens billed at the cache-creation rate, when the provider reports it.</summary>
   public int CacheWriteTokens { get; init; }

   // -- server-side timings (llama-server only) ------------------------------
   // llama-server attaches a `timings` object to its final stream chunk. The
   // prompt figures cover only the tokens it actually had to process (the
   // cached prefix is excluded), so they are the true prefill cost, as
   // opposed to the client-measured time to first token which also holds
   // model load, queueing and network. Null on every other provider.

   /// <summary>Prompt tokens the server actually processed this call (cache excluded).</summary>
   public int? ServerPromptTokens { get; init; }

   /// <summary>Server-measured prompt processing time.</summary>
   public TimeSpan? ServerPromptTime { get; init; }

   /// <summary>Tokens the server generated this call.</summary>
   public int? ServerPredictedTokens { get; init; }

   /// <summary>Server-measured generation time.</summary>
   public TimeSpan? ServerPredictedTime { get; init; }

   /// <summary>True when the server reported its own timings.</summary>
   public bool HasServerTimings => ServerPromptTime.HasValue || ServerPredictedTime.HasValue;

   /// <summary>Prompt tokens billed at the full (uncached) input rate.</summary>
   public int UncachedPromptTokens =>
      PromptTokens > CachedReadTokens ? PromptTokens - CachedReadTokens : 0;

   /// <summary>Fraction of the prompt served from cache, 0..1. Zero when the prompt is empty.</summary>
   public double CachedFraction =>
      PromptTokens > 0 ? (double)CachedReadTokens / PromptTokens : 0.0;
}
