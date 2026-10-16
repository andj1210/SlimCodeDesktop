// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Raw outcome of one side call: the text the model produced plus everything
/// needed to explain a bad result. Never null — failures carry a reason and
/// whatever partial text arrived before things went wrong.
/// </summary>
public sealed class SideCallResult
{
   public string Text { get; init; } = "";

   public LlmUsage? Usage { get; init; }

   /// <summary>Why the model stopped. <see cref="FinishReason.Length"/> means the cap was too small.</summary>
   public FinishReason Finish { get; init; }

   /// <summary>Null on success.</summary>
   public string? FailureReason { get; init; }

   public bool Ok => FailureReason == null;
}

/// <summary>
/// Shared plumbing for one-shot model calls made *beside* a conversation
/// rather than inside it — session titling, reasoning compression, and
/// whatever comes next. What they have in common is what this class owns:
///
///   - A bare request. No tools, no session system prompt, no project
///     context, temperature 0. Given any of those, a model tends to try
///     solving the user's actual task instead of doing the small mechanical
///     job it was handed.
///   - Its own cache partition. A fresh <c>CacheKey</c> per call keeps a
///     one-off from scattering the conversation's warm prefix.
///   - No thinking. The caller passes the model's own "off" template kwargs
///     (<see cref="ReasoningSpec.OffKwargs"/>) and <see cref="ThinkingPolicy.Off"/>
///     stops any captured reasoning from travelling along.
///   - Streaming aggregation with the failure paths already handled:
///     transport, HTTP, endpoint error deltas, output-cap truncation.
///     Cancellation propagates; everything else degrades to a reason string.
///
/// Subclasses own the prompt, the output schema, and how the text is parsed —
/// which is the part that differs, and the only part worth writing twice.
///
/// The model is always the CALLER's model, never a cheaper one: with
/// llama-swap a different id evicts the resident model and pays a full
/// reload, which costs far more than the call saves.
/// </summary>
public abstract class SideCall
{
   /// <summary>Deltas between progress notifications. Per-delta would bury the UI thread.</summary>
   private const int PROGRESS_EVERY = 24;

   protected SideCall(ILlmClient client, string model, string? noThinkKwargs)
   {
      _client        = client ?? throw new ArgumentNullException(nameof(client));
      _model         = model  ?? "";
      _noThinkKwargs = string.IsNullOrWhiteSpace(noThinkKwargs) ? null : noThinkKwargs;
   }

   /// <summary>Wire id this call targets — the caller's own model.</summary>
   protected string Model => _model;

   /// <summary>
   /// Run one system+user exchange and hand back the raw text.
   /// <paramref name="onProgress"/> is invoked on a cadence with the text so
   /// far, and once more with the final text, so an inspector can follow a
   /// long call without a notification per token.
   /// </summary>
   protected async Task<SideCallResult> CallAsync(
      string             systemPrompt,
      string             userMessage,
      int                maxTokens,
      string?            responseFormatJson,
      CancellationToken  cancel,
      Action<string>?    onProgress = null)
   {
      var request = new LlmRequest
      {
         Model    = _model,
         Messages = new[]
         {
            new ChatMessage { Role = ChatRole.System, Content = systemPrompt },
            new ChatMessage { Role = ChatRole.User,   Content = userMessage  },
         },
         Tools                  = null,
         Temperature            = 0.0f,
         MaxTokens              = maxTokens,
         CacheKey               = Guid.NewGuid().ToString("N"),
         ChatTemplateKwargsJson = _noThinkKwargs,
         ResponseFormatJson     = responseFormatJson,
         Thinking               = ThinkingPolicy.Off,
      };

      var sb = new StringBuilder();
      LlmUsage?    usage       = null;
      FinishReason finish      = FinishReason.None;
      int          sinceNotify = 0;

      try
      {
         await foreach (var delta in _client.StreamAsync(request, cancel).ConfigureAwait(false))
         {
            switch (delta.Kind)
            {
               case LlmDelta.DeltaKind.Text:
                  sb.Append(delta.TextDelta);
                  if (onProgress != null && ++sinceNotify >= PROGRESS_EVERY)
                  {
                     sinceNotify = 0;
                     onProgress(sb.ToString());
                  }
                  break;
               case LlmDelta.DeltaKind.Usage:
                  usage = delta.Usage;
                  break;
               case LlmDelta.DeltaKind.Finish:
                  finish = delta.Finish;
                  break;
               case LlmDelta.DeltaKind.Error:
                  onProgress?.Invoke(sb.ToString());
                  return new SideCallResult
                  {
                     Text          = sb.ToString(),
                     Usage         = usage,
                     Finish        = FinishReason.Error,
                     FailureReason = "endpoint error",
                  };
               // Reasoning deltas are ignored: if the no-think kwarg was not
               // honored we still only care about the main channel.
            }
         }
      }
      catch (OperationCanceledException)
      {
         throw;      // user cancel must propagate, not degrade into a failure string
      }
      catch (Exception ex)
      {
         onProgress?.Invoke(sb.ToString());
         return new SideCallResult
         {
            Text          = sb.ToString(),
            Usage         = usage,
            Finish        = FinishReason.Error,
            FailureReason = "call failed: " + ex.Message,
         };
      }

      // Final notification carries the tail the cadence never reached.
      onProgress?.Invoke(sb.ToString());

      return new SideCallResult { Text = sb.ToString(), Usage = usage, Finish = finish };
   }

   /// <summary>
   /// First balanced <c>{...}</c> run in the text, ignoring braces inside
   /// strings. Constrained decoding should give bare JSON, but a model that
   /// ignored the schema often still emits a fenced object — salvage that
   /// rather than throwing away a usable answer. Null when there is none.
   /// </summary>
   protected static string? ExtractJsonObject(string s)
   {
      int start = s.IndexOf('{');
      if (start < 0) return null;

      int depth = 0;
      bool inStr = false, esc = false;
      for (int i = start; i < s.Length; i++)
      {
         char c = s[i];
         if (esc)                { esc = false; continue; }
         if (c == '\\' && inStr) { esc = true; continue; }
         if (c == '"')           { inStr = !inStr; continue; }
         if (inStr)              continue;
         if (c == '{')           depth++;
         else if (c == '}')
         {
            depth--;
            if (depth == 0)
               return s.Substring(start, i - start + 1);
         }
      }
      return null;
   }

   protected static readonly JsonSerializerOptions JsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
   };

   // -- fields ------------------------------------------------------------------

   private readonly ILlmClient _client;
   private readonly string     _model;
   private readonly string?    _noThinkKwargs;
}
