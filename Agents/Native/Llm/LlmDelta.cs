// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>Why the model stopped emitting tokens for this turn.</summary>
public enum FinishReason
{
   None,
   Stop,
   ToolCalls,
   Length,
   Error,
}

/// <summary>
/// One chunk from a streaming LLM response. Exactly one of the payload
/// fields is populated per instance; consumers switch on <see cref="Kind"/>.
///
/// Tool call deltas may arrive piecewise: the same <see cref="ToolCallIndex"/>
/// is reused while <see cref="ToolCallNameDelta"/> and
/// <see cref="ToolCallArgsDelta"/> are appended over multiple chunks.
/// </summary>
public readonly struct LlmDelta
{
   public enum DeltaKind
   {
      Text,
      ReasoningText,
      ToolCallStart,
      ToolCallArgs,
      Finish,
      Usage,
      Error,
   }

   public DeltaKind Kind { get; init; }

   // -- Text -----------------------------------------------------------------
   public string? TextDelta { get; init; }

   // -- ReasoningText --------------------------------------------------------
   public string? ReasoningDelta { get; init; }

   // -- ToolCall* ------------------------------------------------------------
   public int     ToolCallIndex { get; init; }
   public string? ToolCallId { get; init; }
   public string? ToolCallNameDelta { get; init; }
   public string? ToolCallArgsDelta { get; init; }

   // -- Finish ---------------------------------------------------------------
   public FinishReason Finish { get; init; }

   // -- Usage ----------------------------------------------------------------
   public LlmUsage Usage { get; init; }

   // -- Error ----------------------------------------------------------------
   public string? ErrorMessage { get; init; }

   public static LlmDelta Text(string s) =>
      new() { Kind = DeltaKind.Text, TextDelta = s };

   public static LlmDelta Reasoning(string s) =>
      new() { Kind = DeltaKind.ReasoningText, ReasoningDelta = s };

   public static LlmDelta ToolStart(int idx, string id, string nameDelta) =>
      new() { Kind = DeltaKind.ToolCallStart, ToolCallIndex = idx, ToolCallId = id, ToolCallNameDelta = nameDelta };

   public static LlmDelta ToolArgs(int idx, string argsDelta) =>
      new() { Kind = DeltaKind.ToolCallArgs, ToolCallIndex = idx, ToolCallArgsDelta = argsDelta };

   public static LlmDelta FromFinish(FinishReason r) =>
      new() { Kind = DeltaKind.Finish, Finish = r };

   public static LlmDelta FromUsage(LlmUsage usage) =>
      new() { Kind = DeltaKind.Usage, Usage = usage };

   public static LlmDelta FromError(string msg) =>
      new() { Kind = DeltaKind.Error, ErrorMessage = msg };
}
