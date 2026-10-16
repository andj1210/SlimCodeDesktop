// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SlimCodeDesktop.Agents.Native.Llm;

// DTOs for the OpenAI chat.completions wire format. Property names map to the
// snake_case wire shape via [JsonPropertyName]. Nullable optional fields are
// omitted by the serializer when null (configured in OpenAiCompatClient).
//
// Reference: https://platform.openai.com/docs/api-reference/chat
// llama.cpp llama-server implements the same surface for tools + streaming.

internal sealed class OaChatRequest
{
   [JsonPropertyName("model")]        public string Model { get; set; } = "";
   [JsonPropertyName("messages")]     public List<OaMessage> Messages { get; set; } = new();
   [JsonPropertyName("stream")]       public bool Stream { get; set; } = true;
   [JsonPropertyName("temperature")]  public float? Temperature { get; set; }
   [JsonPropertyName("max_tokens")]   public int? MaxTokens { get; set; }
   [JsonPropertyName("tools")]        public List<OaTool>? Tools { get; set; }
   [JsonPropertyName("stream_options")] public OaStreamOptions? StreamOptions { get; set; }

   // Prompt-cache routing affinity: keeps a conversation pinned to one warm
   // cache partition on load-balanced providers. Omitted when null.
   [JsonPropertyName("prompt_cache_key")] public string? PromptCacheKey { get; set; }

   // Template-level switches (llama.cpp / vLLM): e.g. {"enable_thinking": false}.
   [JsonPropertyName("chat_template_kwargs")]
   public System.Text.Json.JsonElement? ChatTemplateKwargs { get; set; }

   // Reasoning effort per the OpenAI spec. llama-server accepts it top-level
   // AND as a chat_template_kwargs entry (both verified against Qwen 3.8);
   // the spec form is sent because it is what non-llama.cpp endpoints read.
   [JsonPropertyName("reasoning_effort")]
   public string? ReasoningEffort { get; set; }

   // Constrained decoding (json_object / json_schema).
   [JsonPropertyName("response_format")]
   public System.Text.Json.JsonElement? ResponseFormat { get; set; }
}

internal sealed class OaStreamOptions
{
   // Ask the server to include a final usage chunk after streaming.
   [JsonPropertyName("include_usage")] public bool IncludeUsage { get; set; } = true;
}

internal sealed class OaMessage
{
   [JsonPropertyName("role")]         public string Role { get; set; } = "user";

   /// <summary>
   /// Either a plain string (every text-only turn, which is nearly all of
   /// them) or a <c>List&lt;OaContentPart&gt;</c> when the turn carries
   /// images. Declared as object so System.Text.Json emits the runtime shape;
   /// the OpenAI schema genuinely is a union here, and modelling it as one
   /// keeps text-only requests byte-identical to what they were before
   /// multimodal support existed.
   ///
   /// Use <see cref="Text"/> for the string case rather than casting.
   /// </summary>
   [JsonPropertyName("content")]      public object? Content { get; set; }

   /// <summary>
   /// <see cref="Content"/> as text. Null when the message carries a parts
   /// array instead — callers that fold or rewrite content (system-message
   /// normalization) only ever deal with text turns.
   /// </summary>
   [JsonIgnore]
   public string? Text
   {
      get => Content as string;
      set => Content = value;
   }

   [JsonPropertyName("tool_calls")]   public List<OaToolCall>? ToolCalls { get; set; }
   [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

   // Outbound reasoning. Written on ordinary assistant history turns when the
   // request's ThinkingPolicy says to resend their trace.
   [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
}

// One element of a multimodal content array. Only the two kinds SlimCode
// produces are modelled: "text" carries Text, "image_url" carries ImageUrl.
internal sealed class OaContentPart
{
   [JsonPropertyName("type")]      public string Type { get; set; } = "text";
   [JsonPropertyName("text")]      public string? Text { get; set; }
   [JsonPropertyName("image_url")] public OaImageUrl? ImageUrl { get; set; }
}

internal sealed class OaImageUrl
{
   // A data: URL. The schema also allows an http(s) URL the server fetches
   // itself; SlimCode never sends one — a local model has no route to the
   // user's machine, and inlining keeps the request self-contained.
   [JsonPropertyName("url")] public string Url { get; set; } = "";
}

internal sealed class OaTool
{
   [JsonPropertyName("type")]     public string Type { get; set; } = "function";
   [JsonPropertyName("function")] public OaToolFunction Function { get; set; } = new();
}

internal sealed class OaToolFunction
{
   [JsonPropertyName("name")]        public string Name { get; set; } = "";
   [JsonPropertyName("description")] public string? Description { get; set; }
   // Raw JSON Schema object. We hand-serialize the registry's schema string
   // through a JsonElement so we don't double-escape it.
   [JsonPropertyName("parameters")]  public System.Text.Json.JsonElement Parameters { get; set; }
}

internal sealed class OaToolCall
{
   [JsonPropertyName("index")]    public int? Index { get; set; }
   [JsonPropertyName("id")]       public string? Id { get; set; }
   [JsonPropertyName("type")]     public string? Type { get; set; }
   [JsonPropertyName("function")] public OaToolCallFunction? Function { get; set; }
}

internal sealed class OaToolCallFunction
{
   [JsonPropertyName("name")]      public string? Name { get; set; }
   [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

// -- streaming response shapes --------------------------------------------

internal sealed class OaStreamChunk
{
   [JsonPropertyName("choices")] public List<OaStreamChoice>? Choices { get; set; }
   [JsonPropertyName("usage")]   public OaUsage? Usage { get; set; }

   // llama-server extension: server-side prompt / generation timings on the
   // final chunk (and on every chunk when timings_per_token is requested).
   // Absent on every other provider; the deserializer simply leaves it null.
   [JsonPropertyName("timings")] public OaTimings? Timings { get; set; }
}

internal sealed class OaTimings
{
   [JsonPropertyName("prompt_n")]     public int?    PromptN { get; set; }
   [JsonPropertyName("prompt_ms")]    public double? PromptMs { get; set; }
   [JsonPropertyName("predicted_n")]  public int?    PredictedN { get; set; }
   [JsonPropertyName("predicted_ms")] public double? PredictedMs { get; set; }
}

internal sealed class OaStreamChoice
{
   [JsonPropertyName("index")]         public int Index { get; set; }
   [JsonPropertyName("delta")]         public OaStreamDelta? Delta { get; set; }
   [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

internal sealed class OaStreamDelta
{
   [JsonPropertyName("role")]       public string? Role { get; set; }
   [JsonPropertyName("content")]    public string? Content { get; set; }
   [JsonPropertyName("tool_calls")] public List<OaToolCall>? ToolCalls { get; set; }

   // Reasoning channel. Emitted by OpenAI o-series, DeepSeek R1, newer
   // Qwen/QwQ builds, and some vLLM / llama.cpp servers configured to
   // split chain-of-thought from the user-visible answer. When absent,
   // models that "think" stream the reasoning inline through Content
   // (often wrapped in <think>...</think> tags). We accept either shape.
   [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
}

internal sealed class OaUsage
{
   [JsonPropertyName("prompt_tokens")]     public int PromptTokens { get; set; }
   [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
   [JsonPropertyName("total_tokens")]      public int TotalTokens { get; set; }

   // Some providers (OpenAI o-series, DeepSeek reasoner, a few local builds)
   // split out reasoning tokens via this nested object. Null on providers
   // that don't expose it; callers fall back to a character count.
   [JsonPropertyName("completion_tokens_details")]
   public OaCompletionTokensDetails? CompletionTokensDetails { get; set; }

   // Cached-prefix accounting. OpenAI-style endpoints (incl. cortecs) report
   // the cache-read subset under prompt_tokens_details.cached_tokens; some
   // DeepSeek-native endpoints report it top-level as prompt_cache_hit_tokens.
   // We read whichever is present. A separate cache-creation count is rare
   // over the OpenAI schema but captured defensively when a provider sends it.
   [JsonPropertyName("prompt_tokens_details")]
   public OaPromptTokensDetails? PromptTokensDetails { get; set; }

   [JsonPropertyName("prompt_cache_hit_tokens")]    public int? PromptCacheHitTokens { get; set; }
   [JsonPropertyName("cache_creation_input_tokens")] public int? CacheCreationInputTokens { get; set; }
}

internal sealed class OaCompletionTokensDetails
{
   [JsonPropertyName("reasoning_tokens")] public int? ReasoningTokens { get; set; }
}

internal sealed class OaPromptTokensDetails
{
   [JsonPropertyName("cached_tokens")] public int? CachedTokens { get; set; }
}
