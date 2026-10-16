// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Tool definition advertised to the model. <see cref="ParametersJsonSchema"/>
/// is a raw JSON Schema object (serialized string) — the registry owns the schema
/// shape; the client just forwards it to the provider verbatim.
/// </summary>
public sealed class LlmToolSpec
{
   public string Name { get; set; } = "";
   public string Description { get; set; } = "";
   public string ParametersJsonSchema { get; set; } = "{}";
}

public sealed class LlmRequest
{
   public string Model { get; set; } = "";
   public IReadOnlyList<ChatMessage> Messages { get; set; } = Array.Empty<ChatMessage>();
   public IReadOnlyList<LlmToolSpec>? Tools { get; set; }
   public float?  Temperature { get; set; }
   public int?    MaxTokens { get; set; }

   /// <summary>
   /// Whether the assistant turns in <see cref="Messages"/> resend their
   /// captured reasoning. Never null — <see cref="ThinkingPolicy.Default"/>
   /// sends all of it.
   /// </summary>
   public ThinkingPolicy Thinking { get; set; } = ThinkingPolicy.Default;

   /// <summary>
   /// Raw JSON object forwarded as <c>chat_template_kwargs</c> (e.g.
   /// <c>{"enable_thinking": false}</c>). Null omits the field. Invalid JSON
   /// is dropped rather than failing the request.
   /// </summary>
   public string? ChatTemplateKwargsJson { get; set; }

   /// <summary>
   /// Resolved reasoning controls for this call, or null to say nothing about
   /// reasoning. Already clamped to the target model's declared style by the
   /// caller — the client sends what it is given, since only the caller knows
   /// which model this request is bound for. Its <c>enable_thinking</c> is
   /// merged over <see cref="ChatTemplateKwargsJson"/>, so the two can be set
   /// together without one silently dropping the other.
   /// </summary>
   public ReasoningRequest? Reasoning { get; set; }

   /// <summary>
   /// Raw JSON object forwarded as <c>response_format</c> for constrained
   /// decoding. Null omits the field.
   /// </summary>
   public string? ResponseFormatJson { get; set; }

   /// <summary>
   /// Stable per-conversation routing hint for prompt caching. Sent as
   /// <c>prompt_cache_key</c> so a load-balanced provider pins every turn of
   /// one conversation to the same cache partition. Must be constant for the
   /// session's lifetime — a value that changes per request scatters the
   /// cache instead of preserving it. Null omits the field.
   /// </summary>
   public string? CacheKey { get; set; }
}

/// <summary>
/// Provider-agnostic streaming chat client. Today the only implementation is
/// <see cref="OpenAiCompatClient"/> (covers llama.cpp, llama-swap, OpenAI,
/// OpenRouter, vLLM). An Anthropic implementation can be slotted in later
/// behind the same interface.
/// </summary>
public interface ILlmClient
{
   IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken cancel);
}
