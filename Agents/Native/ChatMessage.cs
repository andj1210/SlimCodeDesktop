// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>Role of a chat message in the OpenAI chat shape.</summary>
public enum ChatRole
{
   System,
   User,
   Assistant,
   Tool,
}

/// <summary>One pending or completed tool invocation requested by the model.</summary>
public sealed class ToolCall
{
   /// <summary>Provider-assigned id, echoed back in the matching tool result.</summary>
   public string Id { get; set; } = "";

   /// <summary>Tool name (must exist in the ToolRegistry).</summary>
   public string Name { get; set; } = "";

   /// <summary>Raw JSON argument object as emitted by the model.</summary>
   public string ArgumentsJson { get; set; } = "";
}

/// <summary>
/// One entry in the conversation transcript. Shape mirrors OpenAI
/// chat.completions so it round-trips through llama.cpp / llama-swap
/// without translation.
///
/// - User / System / Assistant text-only messages: set <see cref="Content"/>.
/// - Assistant tool-use turn: set <see cref="ToolCalls"/> (Content may be empty).
/// - Tool result turn: Role = Tool, <see cref="ToolCallId"/> = matching call id,
///   <see cref="Content"/> = result payload (string).
/// </summary>
public sealed class ChatMessage
{
   public ChatRole Role { get; set; }
   public string   Content { get; set; } = "";

   /// <summary>
   /// Images attached to this turn. Non-null only on user messages — that is
   /// the one role where every provider in scope accepts a multimodal
   /// content array (tool results are text-only on the OpenAI schema).
   ///
   /// <see cref="Content"/> stays the canonical text either way: everything
   /// that reads a transcript (the gauge, replay, search) keeps
   /// working, and a turn without images serializes byte-identically to what
   /// it did before images existed — which is what keeps the prompt cache
   /// warm across a text-only session.
   /// </summary>
   public List<ImagePart>? Images { get; set; }

   /// <summary>True when this turn carries at least one image.</summary>
   public bool HasImages => Images is { Count: > 0 };

   /// <summary>Non-null on an assistant turn that invoked tools.</summary>
   public List<ToolCall>? ToolCalls { get; set; }

   /// <summary>Set on Role=Tool messages to reference the originating call.</summary>
   public string? ToolCallId { get; set; }

   /// <summary>
   /// Server-side reasoning / chain-of-thought captured from a streaming
   /// <c>reasoning_content</c> channel. Stored alongside the message for UI
   /// display and JSONL persistence, and resent to the provider on later
   /// turns as far as the request's <see cref="Llm.ThinkingPolicy"/> allows —
   /// models with preserved thinking need their own trace back, and a server
   /// that doesn't support it drops the field.
   /// </summary>
   public string? ReasoningContent { get; set; }

   /// <summary>
   /// Reasoning token count reported by the provider for this turn, when
   /// available. Used by the UI to label the collapsed "Thinking" section
   /// in tokens rather than characters. Null when the provider doesn't
   /// split reasoning tokens out of <c>completion_tokens</c>.
   /// </summary>
   public int? ReasoningTokens { get; set; }

   /// <summary>
   /// Total prompt-token count reported for the request that produced
   /// this assistant turn. Captured for resume-time gauge prefill — null
   /// when the provider didn't send a usage chunk for the turn.
   /// </summary>
   public int? PromptTokens { get; set; }

   /// <summary>Completion-token count reported for this turn. Null when absent.</summary>
   public int? CompletionTokens { get; set; }

   /// <summary>
   /// Subset of <see cref="PromptTokens"/> served from the provider's prompt
   /// cache (billed at the cheaper cache-read rate). Captured for the usage
   /// breakdown / cache-hit display. Null when the provider doesn't report it.
   /// </summary>
   public int? CachedReadTokens { get; set; }

   /// <summary>
   /// Tokens billed at the cache-creation rate for this turn, when the
   /// provider exposes a separate cache-write step. Null / 0 otherwise.
   /// </summary>
   public int? CacheWriteTokens { get; set; }

   /// <summary>
   /// Wall-clock phases of the work that produced this message. On an
   /// assistant turn: prefill, thinking, output and tool-args time.
   /// On a tool result: permission wait and
   /// execution time. Null on user / system messages and on turns recorded
   /// before timing existed. Never sent on the wire.
   /// </summary>
   public Llm.PhaseTimings? Timings { get; set; }

   /// <summary>
   /// Non-generated tokens that entered the context for the call that
   /// produced this assistant turn (see <see cref="Llm.TokenReads"/>). Null
   /// when the provider reported no usage or the turn predates the field.
   /// </summary>
   public Llm.TokenReads? Reads { get; set; }

   /// <summary>
   /// Only meaningful on <see cref="ChatRole.Tool"/> messages: mirrors the
   /// originating <c>ToolResult.IsError</c> so a resumed session can
   /// re-render the tool card with the right ✓/✗ status. Not sent on the
   /// wire (OpenAI tool messages don't carry an error flag — the content
   /// itself signals failure).
   /// </summary>
   public bool IsError { get; set; }
}
