// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Tokens that entered the context for one LLM call WITHOUT being generated
/// by the model: the user's prompt and system notes, tool results, and
/// attached images. The counterpart of completion tokens ("written").
///
/// Tokens the model wrote on an earlier turn are resent as prompt on every
/// later call, but that is a resend, not a read, and is deliberately not
/// counted here - this measures what the session fed the model, once.
///
/// Derived from the provider's usage report, not from a tokenizer: the
/// prompt grew by exactly the messages appended since the previous call, so
/// (prompt now) - (prompt + completion of the previous call) is the new
/// non-generated token count. The split by category follows what was
/// appended; when only one category was, it is exact. Images cannot be
/// character-estimated, so they take whatever the text estimate leaves
/// over, which is why <see cref="Estimated"/> is set on those calls.
/// </summary>
public struct TokenReads
{
   /// <summary>User prompt text plus system notes (mode switches, skills, corrections).</summary>
   public int Prompt;

   /// <summary>Tool results fed back to the model.</summary>
   public int ToolResults;

   /// <summary>Image attachments, as the provider tokenized them (estimate).</summary>
   public int Images;

   /// <summary>
   /// True when the count could not be derived from usage deltas (no
   /// baseline: first call after a model swap) or when image tokens were
   /// inferred from the remainder. A character heuristic filled the gap.
   /// </summary>
   public bool Estimated;

   public int Total => Prompt + ToolResults + Images;

   public bool IsEmpty => Total == 0;

   public void Add(in TokenReads other)
   {
      Prompt      += other.Prompt;
      ToolResults += other.ToolResults;
      Images      += other.Images;
      Estimated   |= other.Estimated;
   }
}
