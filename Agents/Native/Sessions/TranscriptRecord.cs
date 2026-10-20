// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SlimCodeDesktop.Agents.Native.Sessions;

/// <summary>
/// One line in a session's JSONL transcript. Two flavors share the shape so
/// the file is a uniform stream of objects rather than two parallel formats:
///   - Type = "meta": session header (first record). Carries model, system
///     prompt snapshot, project path, start time.
///   - Type = "msg":  one chat message (system/user/assistant/tool).
///
/// Property naming is camelCase to match the wider settings JSON convention.
/// Unused fields are omitted by the serializer (DefaultIgnoreCondition).
/// </summary>
public sealed class TranscriptRecord
{
   [JsonPropertyName("type")] public string Type { get; set; } = "msg";

   /// <summary>
   /// UTC write time of this record. Stamped on every line so the gap between
   /// turns can be reconstructed (e.g. to correlate a prompt-cache cliff with
   /// an idle period exceeding the provider's cache TTL). Null on records
   /// written before this field existed.
   /// </summary>
   [JsonPropertyName("ts")] public DateTime? Ts { get; set; }

   // -- meta -----------------------------------------------------------------

   [JsonPropertyName("sessionId")]    public string?   SessionId { get; set; }
   [JsonPropertyName("startedAt")]    public DateTime? StartedAt { get; set; }
   [JsonPropertyName("model")]        public string?   Model { get; set; }

   /// <summary>
   /// Name of the provider hosting <see cref="Model"/>. Present on "meta" and
   /// "model" records so a resume reconnects to the right endpoint (the same
   /// model id can exist on multiple providers).
   /// </summary>
   [JsonPropertyName("provider")]     public string?   Provider { get; set; }

   /// <summary>"Plan" or "Implement"; only present on Type="mode" records.</summary>
   [JsonPropertyName("mode")]         public string?   Mode { get; set; }

   /// <summary>
   /// The session's chosen <c>ReasoningLevel</c> name ("Default", "High", …);
   /// only present on Type="reasoning" records. Last one wins on resume.
   /// </summary>
   [JsonPropertyName("reasoning")]    public string?   Reasoning { get; set; }

   /// <summary>
   /// Names of the MCP tools advertised to the model, on Type="mcpTools"
   /// records. One is written on the session's first send and again whenever
   /// the set changes, so a resume can diff what the servers offer NOW against
   /// what this conversation was last told about and announce the difference.
   ///
   /// The tool definitions themselves are deliberately not persisted: they
   /// travel in every request's tools array and are never part of the
   /// transcript, so there is nothing to keep in sync — only the names, which
   /// is all the diff needs.
   /// </summary>
   [JsonPropertyName("mcpTools")]     public List<string>? McpTools { get; set; }
   [JsonPropertyName("projectPath")]  public string?   ProjectPath { get; set; }
   [JsonPropertyName("systemPrompt")] public string?   SystemPrompt { get; set; }

   /// <summary>
   /// Transcript flavor on "meta" records: null/absent = a regular session,
   /// "subagent" = a subagent run's companion transcript (skipped by session
   /// enumeration regardless of its file name).
   /// </summary>
   [JsonPropertyName("kind")]         public string?   Kind { get; set; }

   /// <summary>
   /// Session this one was forked from, on "meta" records. Absent = not a
   /// fork.
   ///
   /// Purely descriptive: a fork copies every record it inherits, so the
   /// child is a complete standalone transcript and stays fully resumable
   /// after the parent is deleted. Nothing may resolve this id in order to
   /// READ a session — it exists so the picker can show lineage, and a
   /// dangling id simply renders as a root.
   /// </summary>
   [JsonPropertyName("parentSessionId")] public string? ParentSessionId { get; set; }

   /// <summary>
   /// What produced the fork, on "meta" records: see
   /// <see cref="TranscriptStore.FORK_REVERT"/> and friends. Display only.
   /// </summary>
   [JsonPropertyName("forkKind")]     public string?   ForkKind { get; set; }

   /// <summary>
   /// Human-readable session caption; only present on Type="title" records.
   /// Append-only like "model"/"mode": the LAST such record in a file wins, so
   /// a rename is one more line rather than a rewrite. Absent everywhere =
   /// the session has no caption and the picker falls back to the first user
   /// prompt.
   /// </summary>
   [JsonPropertyName("title")]        public string?   Title { get; set; }

   // -- msg ------------------------------------------------------------------

   [JsonPropertyName("role")]       public string?               Role { get; set; }
   [JsonPropertyName("content")]    public string?               Content { get; set; }
   /// <summary>
   /// Images attached to a user message, base64 inline. Absent on every other
   /// record. Inline rather than side files: attachments are occasional, and
   /// a transcript that stays a single self-contained file survives being
   /// copied, forked or mailed around — which side files would not.
   /// </summary>
   [JsonPropertyName("images")]     public List<ImageRecord>?    Images { get; set; }

   /// <summary>
   /// True on a user record the loop synthesized to deliver tool-produced
   /// images (see <see cref="ChatMessage.IsToolImages"/>). Absent otherwise.
   /// </summary>
   [JsonPropertyName("toolImages")] public bool?                 ToolImages { get; set; }

   [JsonPropertyName("toolCalls")]  public List<ToolCallRecord>? ToolCalls { get; set; }
   [JsonPropertyName("toolCallId")] public string?               ToolCallId { get; set; }

   /// <summary>
   /// Captured chain-of-thought for assistant messages, when the provider
   /// streamed a dedicated reasoning channel. Persisted so replays restore
   /// the collapsible "Thinking" section; never re-sent on the wire.
   /// </summary>
   [JsonPropertyName("reasoningContent")] public string? ReasoningContent { get; set; }

   /// <summary>Reasoning token count, when the provider reported it. Null otherwise.</summary>
   [JsonPropertyName("reasoningTokens")] public int? ReasoningTokens { get; set; }

   /// <summary>Prompt-token count for the request that produced this assistant turn.</summary>
   [JsonPropertyName("promptTokens")] public int? PromptTokens { get; set; }

   /// <summary>Completion-token count for this assistant turn.</summary>
   [JsonPropertyName("completionTokens")] public int? CompletionTokens { get; set; }

   /// <summary>Cache-read subset of <see cref="PromptTokens"/>, when reported.</summary>
   [JsonPropertyName("cachedReadTokens")] public int? CachedReadTokens { get; set; }

   /// <summary>Cache-creation tokens for this turn, when the provider reports them.</summary>
   [JsonPropertyName("cacheWriteTokens")] public int? CacheWriteTokens { get; set; }

   /// <summary>
   /// Tool-result error flag. True if the originating <c>ToolResult</c>
   /// signaled failure. Persisted only on Role=Tool messages so a resume
   /// can re-render the right ✓/✗ status on the tool card.
   /// </summary>
   [JsonPropertyName("isError")] public bool? IsError { get; set; }

   /// <summary>
   /// Wall-clock phases behind this message (assistant: model phases; tool:
   /// wait + exec). Absent on records written before timing existed.
   /// </summary>
   [JsonPropertyName("timing")] public TimingRecord? Timing { get; set; }

   /// <summary>Non-generated tokens the call behind this assistant turn consumed.</summary>
   [JsonPropertyName("reads")] public ReadsRecord? Reads { get; set; }

   // -- perm (Type="perm") ---------------------------------------------------

   /// <summary>Tool name for a persisted session-scope permission grant.</summary>
   [JsonPropertyName("permTool")] public string? PermTool { get; set; }

   /// <summary>Decision ("Allow"/"Deny") for the session grant above.</summary>
   [JsonPropertyName("permDecision")] public string? PermDecision { get; set; }
}

/// <summary>Serialized form of one attached image (see <see cref="ImagePart"/>).</summary>
public sealed class ImageRecord
{
   [JsonPropertyName("mediaType")] public string  MediaType { get; set; } = "image/png";
   [JsonPropertyName("b64")]       public string  Base64 { get; set; } = "";
   [JsonPropertyName("w")]         public int     Width { get; set; }
   [JsonPropertyName("h")]         public int     Height { get; set; }
   [JsonPropertyName("name")]      public string? FileName { get; set; }
   [JsonPropertyName("callId")]    public string? ToolCallId { get; set; }
}

/// <summary>
/// Serialized <see cref="Llm.PhaseTimings"/>, in whole milliseconds. Only the
/// phases that actually took time are written, so a tool record carries two
/// keys and an assistant record three or four.
/// </summary>
public sealed class TimingRecord
{
   [JsonPropertyName("mcpConnectMs")] public int? McpConnectMs { get; set; }
   [JsonPropertyName("prefillMs")]  public int? PrefillMs { get; set; }
   [JsonPropertyName("thinkMs")]    public int? ThinkingMs { get; set; }
   [JsonPropertyName("outputMs")]   public int? OutputMs { get; set; }
   [JsonPropertyName("toolArgsMs")] public int? ToolArgsMs { get; set; }
   [JsonPropertyName("permWaitMs")] public int? PermissionWaitMs { get; set; }
   [JsonPropertyName("execMs")]     public int? ToolExecMs { get; set; }
}

/// <summary>Serialized <see cref="Llm.TokenReads"/>.</summary>
public sealed class ReadsRecord
{
   [JsonPropertyName("prompt")]    public int   Prompt { get; set; }
   [JsonPropertyName("tools")]     public int   ToolResults { get; set; }
   [JsonPropertyName("images")]    public int?  Images { get; set; }
   [JsonPropertyName("estimated")] public bool? Estimated { get; set; }
}

/// <summary>Serialized form of one tool call (name + raw JSON arguments).</summary>
public sealed class ToolCallRecord
{
   [JsonPropertyName("id")]            public string Id { get; set; } = "";
   [JsonPropertyName("name")]          public string Name { get; set; } = "";
   [JsonPropertyName("argumentsJson")] public string ArgumentsJson { get; set; } = "";
}
