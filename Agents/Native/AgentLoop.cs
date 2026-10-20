// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Tools;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// One conversation's turn loop: build a request, stream the response,
/// dispatch any tool calls the model emits, append the results, and
/// repeat until the model finishes without requesting more tools.
///
/// Owns the message history for the conversation. Stateless across
/// conversations — instantiate one per session.
///
/// Events fire on the thread that drives the stream (i.e. the caller of
/// <see cref="RunAsync"/>). UI consumers must marshal to the Avalonia
/// dispatcher themselves.
/// </summary>
public sealed class AgentLoop
{
   public AgentLoop(
      ILlmClient client,
      ToolRegistry tools,
      ToolContext toolContext,
      string model,
      string systemPrompt,
      IReadOnlyList<ChatMessage>? initialHistory = null,
      PermissionGate? gate = null)
   {
      _client       = client ?? throw new ArgumentNullException(nameof(client));
      _tools        = tools  ?? throw new ArgumentNullException(nameof(tools));
      _toolContext  = toolContext ?? throw new ArgumentNullException(nameof(toolContext));
      _model        = model ?? "";
      _systemPrompt = systemPrompt ?? "";
      _gate         = gate;

      if (initialHistory != null && initialHistory.Count > 0)
      {
         // Resumed session: trust the persisted history as-is. It already
         // carries the system message; injecting another would duplicate it
         // and confuse the model.
         foreach (var msg in initialHistory)
            _history.Add(msg);

         // Read accounting resumes from the last turn that reported usage:
         // everything up to it was attributed when it was live, and its
         // prompt + completion is the size of the context it left behind.
         // Without such a turn there is no baseline, and the next call
         // falls back to a character estimate for what it appends.
         _readCursor = _history.Count;
         for (int i = _history.Count - 1; i >= 0; i--)
         {
            var m = _history[i];
            if (m.Role != ChatRole.Assistant || !m.PromptTokens.HasValue)
               continue;
            _readBaseline = m.PromptTokens.Value + (m.CompletionTokens ?? 0);
            _readCursor   = i + 1;
            break;
         }
      }
      else
      {
         _freshSession = true;
         if (!string.IsNullOrEmpty(_systemPrompt))
         {
            _history.Add(new ChatMessage
            {
               Role = ChatRole.System,
               Content = _systemPrompt,
            });
         }
      }
   }

   // -- public properties ----------------------------------------------------

   /// <summary>Full conversation transcript including system + tool turns.</summary>
   public IReadOnlyList<ChatMessage> History => _history;

   /// <summary>
   /// Append a mid-conversation system message (e.g. a mode-change
   /// announcement). Mirrors the user-message path: pushed onto history,
   /// raised via <see cref="MessageAdded"/> so the persistence layer
   /// writes it to JSONL alongside chat turns.
   /// </summary>
   public void AppendSystemMessage(string content)
   {
      var msg = new ChatMessage
      {
         Role    = ChatRole.System,
         Content = content ?? "",
      };
      _history.Add(msg);
      MessageAdded?.Invoke(msg);
   }

   /// <summary>
   /// Wire id of the model used for subsequent requests. Settable so the
   /// host can hot-swap models mid-conversation without rebuilding the loop
   /// (history stays intact, the next turn just goes to a different model).
   /// </summary>
   public string Model
   {
      get => _model;
      set
      {
         var next = value ?? "";
         // A different model tokenizes the same history differently, so the
         // previous call's prompt size says nothing about the next one's.
         if (!string.Equals(next, _model, StringComparison.Ordinal))
            _readBaseline = null;
         _model = next;
      }
   }

   /// <summary>
   /// Swap the wire client used for subsequent turns — needed when the host
   /// switches to a model hosted on a different provider (different base URL /
   /// key). History stays intact; only the next turn's transport changes.
   /// The replaced client is disposed if it owns resources (e.g. an HttpClient).
   /// </summary>
   public void SetClient(ILlmClient client)
   {
      if (client == null) throw new ArgumentNullException(nameof(client));
      if (ReferenceEquals(client, _client)) return;
      var previous = _client;
      _client = client;
      _readBaseline = null;   // different endpoint: token counts may not compare
      if (previous is IDisposable d)
      {
         try { d.Dispose(); } catch { }
      }
   }

   /// <summary>
   /// Swap the tool set used for subsequent turns (the next <see cref="RunAsync"/>
   /// rebuilds its specs from this registry). Lets the host expose newly-
   /// configured project commands mid-session without rebuilding the loop.
   /// </summary>
   public void UpdateTools(ToolRegistry tools)
   {
      _tools = tools ?? throw new ArgumentNullException(nameof(tools));
   }

   /// <summary>Temperature passed on every request. Null = provider default.</summary>
   public float? Temperature { get; set; }

   /// <summary>Max tokens per assistant turn. Null = provider default.</summary>
   public int? MaxTokens { get; set; }

   /// <summary>
   /// Stable per-conversation prompt-cache routing key, forwarded on every
   /// request. Set once when the loop is built; do not vary it per turn.
   /// </summary>
   public string? CacheKey { get; set; }

   /// <summary>
   /// Whether history's assistant turns resend their captured reasoning.
   /// Defaults to sending all of it; no per-model configuration yet, on the
   /// assumption that an endpoint without preserved thinking simply drops
   /// the field. Changing it mid-session rewrites the resent prefix and will
   /// cost one full prompt-cache miss.
   /// </summary>
   public ThinkingPolicy Thinking { get; set; } = ThinkingPolicy.Default;

   /// <summary>
   /// Hard cap on tool-call/response iterations per <see cref="RunAsync"/>
   /// call. A runaway backstop, not a budget: this harness keeps a human in
   /// the loop (permission gate, visible transcript, Cancel button), so the
   /// cap is set high enough that legitimate long work never trips it.
   /// </summary>
   public int MaxIterations { get; set; } = 500;

   /// <summary>
   /// Short label identifying this loop when it is a nested subagent, null
   /// for a session's main loop. Forwarded to the permission gate so a
   /// prompt can name who is asking; it has no effect on how the gate
   /// resolves or caches the decision.
   /// </summary>
   public string? SubagentLabel { get; set; }

   // -- events ---------------------------------------------------------------

   /// <summary>Fires for each streamed text fragment of the current assistant turn.</summary>
   public event Action<string>? TextDelta;

   /// <summary>
   /// Fires for each streamed reasoning fragment when the provider sends a
   /// dedicated <c>reasoning_content</c> channel (OpenAI o-series, R1, etc.).
   /// Models that emit <c>&lt;think&gt;</c>-tagged reasoning inside the
   /// regular content channel do not trigger this — those fragments arrive
   /// through <see cref="TextDelta"/> like ordinary text.
   /// </summary>
   public event Action<string>? ReasoningDelta;

   /// <summary>Fires once per tool call as soon as its name + id are known (before args complete).</summary>
   public event Action<ToolCall>? ToolCallStarted;

   /// <summary>
   /// Fires for each streamed fragment of a tool call's arguments, carrying
   /// (call id, raw JSON fragment). The fragments are pieces of an incomplete
   /// JSON object and mean nothing individually — see
   /// <see cref="Tools.ToolArgsStream"/> for decoding them into live text.
   ///
   /// This is the only progress signal a long tool call has: for write_file /
   /// edit_file the arguments ARE the file body, so the whole generation shows
   /// up here and nowhere else.
   ///
   /// Fragments that arrive before <see cref="ToolCallStarted"/> (the id and
   /// name can trail the first arguments delta) are buffered and replayed once
   /// the call is announced, so a subscriber never sees a truncated prefix.
   /// Not raised at all for a call that is never announced.
   /// </summary>
   public event Action<string, string>? ToolCallArgsDelta;

   /// <summary>Fires after a tool call's arguments stream is complete and just before execution.</summary>
   public event Action<ToolCall>? ToolCallReady;

   /// <summary>Fires after a tool finishes; carries the call id and its result.</summary>
   public event Action<string, ToolResult>? ToolCallCompleted;

   /// <summary>Fires when an assistant message (text + any tool calls) is finalized into history.</summary>
   public event Action<ChatMessage>? AssistantMessageCompleted;

   /// <summary>
   /// Fires for every message appended to <see cref="History"/> (user,
   /// assistant, tool) after the loop is constructed — i.e. excluding
   /// any system / replay messages supplied to the constructor.
   /// The persistence layer subscribes to this to write JSONL records.
   /// </summary>
   public event Action<ChatMessage>? MessageAdded;

   /// <summary>Fires once per stream when the provider reports token usage.</summary>
   public event Action<LlmUsage>? UsageReported;

   /// <summary>Fires for unrecoverable errors (network, malformed responses, iteration cap).</summary>
   public event Action<string>? Error;

   /// <summary>Fires once when <see cref="RunAsync"/> finishes for any reason.</summary>
   public event Action<FinishReason>? TurnCompleted;

   // -- timing ---------------------------------------------------------------

   /// <summary>
   /// Fires on every phase transition (not per delta), carrying the new phase
   /// and the current run's timings accumulated up to this instant. A UI can
   /// draw the closed phases from the second argument and grow the open one
   /// from its own clock. <see cref="TurnPhase.Idle"/> marks the run's end.
   /// </summary>
   public event Action<TurnPhase, PhaseTimings>? PhaseChanged;

   /// <summary>
   /// Fires after each tool call with (call id, its permission-wait and
   /// execution time). Precedes <see cref="ToolCallCompleted"/> for the same
   /// call.
   /// </summary>
   public event Action<string, PhaseTimings>? ToolTimingReported;

   /// <summary>
   /// Fires once per <see cref="RunAsync"/> / <see cref="ContinueAsync"/> with
   /// the whole run's timings, just before <see cref="TurnCompleted"/>. Also
   /// fires for a cancelled or failed run, with what it managed to measure.
   /// </summary>
   public event Action<PhaseTimings>? TurnTimingsReported;

   // -- reasoning --------------------------------------------------------------

   /// <summary>
   /// This loop's reasoning capability, resolved from the catalog entry of
   /// the model this loop actually runs — which for a subagent is not the
   /// session's model. Never null; a model with no declared style yields a
   /// spec whose <see cref="ReasoningSpec.Resolve"/> sends nothing.
   /// </summary>
   public ReasoningSpec ReasoningCaps { get; set; } = ReasoningSpec.For(ReasoningStyle.None);

   /// <summary>
   /// Current level, applied from the next turn on. Stored unclamped so the
   /// user's intent survives a model switch, and clamped at send time against
   /// <see cref="ReasoningCaps"/> — an undeclared level is an HTTP 500 from
   /// the template, not a silent ignore, so the clamp is load-bearing.
   /// </summary>
   public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Default;

   /// <summary>What <see cref="Reasoning"/> actually resolves to for this loop's model.</summary>
   public ReasoningLevel EffectiveReasoning => ReasoningCaps.Clamp(Reasoning);

   // -- overthinking meter ---------------------------------------------------

   /// <summary>
   /// Live overthinking metrics for the turn in flight. Purely an observation
   /// signal: nothing in the loop acts on a trip. It exists so a trace that
   /// has started circling is visible while it happens, which is the point at
   /// which cancelling the turn is still worth something.
   /// </summary>
   public event Action<ReasoningStats>? ReasoningStatsChanged;

   // -- entry point ----------------------------------------------------------

   /// <summary>
   /// Append <paramref name="userMessage"/> to history and drive the loop
   /// until the model stops requesting tools or an error / cancel occurs.
   ///
   /// <paramref name="images"/> rides along on the user turn as multimodal
   /// content parts. They stay in history for the rest of the session, same
   /// as the text — dropping them after the first turn would leave the model
   /// discussing a picture it can no longer see.
   /// </summary>
   public async Task RunAsync(string userMessage, IReadOnlyList<ImagePart>? images,
                              CancellationToken cancel)
   {
      var userMsg = new ChatMessage
      {
         Role    = ChatRole.User,
         Content = userMessage ?? "",
         Images  = images is { Count: > 0 } ? new List<ImagePart>(images) : null,
      };
      _history.Add(userMsg);
      MessageAdded?.Invoke(userMsg);

      await _DriveLoop(cancel).ConfigureAwait(false);
   }

   /// <summary>
   /// Re-run the next assistant turn against history exactly as it stands —
   /// no new user message. History must already have been truncated to end
   /// where the turn being replaced began (a user message, or a complete
   /// block of tool results).
   ///
   /// Sampling is what makes the re-run differ from the original; at
   /// temperature 0 it will reproduce it.
   /// </summary>
   public async Task ContinueAsync(CancellationToken cancel)
   {
      await _DriveLoop(cancel).ConfigureAwait(false);
   }

   private async Task _DriveLoop(CancellationToken cancel)
   {
      var specs = _tools.BuildSpecs();
      var finish = FinishReason.None;

      _runTimings  = default;
      _unitTimings = default;
      _phase       = TurnPhase.Idle;
      _toolSpecChars = _SpecChars(specs);

      try
      {
         for (int iter = 0; iter < MaxIterations; iter++)
         {
            cancel.ThrowIfCancellationRequested();

            var request = new LlmRequest
            {
               Model       = _model,
               Messages    = _history,
               Tools       = specs.Count > 0 ? specs : null,
               Temperature = Temperature,
               MaxTokens   = MaxTokens,
               CacheKey    = CacheKey,
               Thinking    = Thinking,
               // Read per turn, not per loop: a level changed mid-session
               // takes effect on the next turn without rebuilding the loop.
               Reasoning   = ReasoningCaps.Resolve(Reasoning),
            };

            var turn = await DriveOneTurn(request, cancel).ConfigureAwait(false);
            finish = turn.Finish;

            // Finalize the assistant message from the accumulated turn state.
            var assistant = BuildAssistantMessage(turn);

            // Strip any tool call whose arguments aren't valid JSON BEFORE the
            // message enters (and is persisted from) history. An unparseable
            // arguments string does not round-trip through the provider's
            // prompt cache: once such a call is in the resent prefix, every
            // later turn misses the cache for the rest of the session. The
            // stripped calls are handed to the model as a plain-text note
            // (quoted verbatim) further below, which round-trips cleanly.
            List<ToolCall>? malformedCalls = null;
            Dictionary<string, string>? coercedArgs = null; // callId -> original raw args (no-arg tools)
            if (assistant.ToolCalls != null)
            {
               var valid = new List<ToolCall>(assistant.ToolCalls.Count);
               foreach (var c in assistant.ToolCalls)
               {
                  if (IsZeroParamTool(c.Name))
                  {
                     // Tool takes no arguments. Ignore whatever the model sent
                     // (even if malformed) and run it with {}. Resending {} keeps
                     // the prompt cache clean; a warning prepended to the result
                     // at execution time preserves the signal that args were sent.
                     var raw = c.ArgumentsJson;
                     bool trivial = string.IsNullOrWhiteSpace(raw) || raw.Trim() == "{}";
                     if (!trivial)
                        (coercedArgs ??= new Dictionary<string, string>())[c.Id] = raw;
                     c.ArgumentsJson = "{}";
                     valid.Add(c);
                  }
                  else if (IsValidJsonArgs(c.ArgumentsJson))
                  {
                     valid.Add(c);
                  }
                  else
                  {
                     (malformedCalls ??= new List<ToolCall>()).Add(c);
                  }
               }
               assistant.ToolCalls = valid.Count > 0 ? valid : null;
            }

            _history.Add(assistant);
            MessageAdded?.Invoke(assistant);
            AssistantMessageCompleted?.Invoke(assistant);

            // A turn that exhausts its output cap while still inside the think
            // block commits an EMPTY assistant message and returns — which
            // renders as nothing at all and reads like the agent hung. Say so.
            if (finish == FinishReason.Length &&
                string.IsNullOrWhiteSpace(assistant.Content) &&
                assistant.ToolCalls == null)
            {
               Error?.Invoke(
                  "the model hit its output cap while still thinking and produced no answer"
                  + " — raise Max Tokens, or lower this model's reasoning level.");
            }

            if (finish == FinishReason.Error)
               return;

            if (finish != FinishReason.ToolCalls)
               return;

            // Execute the valid calls in order, append their results. Kept
            // contiguous with the assistant message so each tool_call stays
            // adjacent to its matching tool result (required by the wire shape).
            if (assistant.ToolCalls != null)
            {
               foreach (var call in assistant.ToolCalls)
               {
                  cancel.ThrowIfCancellationRequested();

                  // For a coerced no-arg tool, show the model's ORIGINAL args on
                  // the UI card (the resent copy is {}), so the visual truth of
                  // what the model emitted isn't lost.
                  string? rawArgs = null;
                  bool coerced = coercedArgs != null && coercedArgs.TryGetValue(call.Id, out rawArgs);
                  ToolCallReady?.Invoke(coerced
                     ? new ToolCall { Id = call.Id, Name = call.Name, ArgumentsJson = rawArgs ?? "" }
                     : call);

                  _unitTimings = default;
                  var result = await ExecuteOne(call, cancel).ConfigureAwait(false);
                  var toolTiming = _TakeUnitTimings();

                  // Prepend a warning when args were ignored for a no-arg tool
                  // (quoting the raw bytes if they were also malformed JSON).
                  if (coerced)
                     result = WithArgIgnoredWarning(call.Name, rawArgs ?? "", result);

                  var toolMsg = new ChatMessage
                  {
                     Role       = ChatRole.Tool,
                     ToolCallId = call.Id,
                     Content    = result.Content,
                     IsError    = result.IsError,
                     Timings    = toolTiming,
                  };
                  _history.Add(toolMsg);
                  MessageAdded?.Invoke(toolMsg);
                  ToolTimingReported?.Invoke(call.Id, toolTiming);
                  ToolCallCompleted?.Invoke(call.Id, result);

                  if (result.Images is { Count: > 0 })
                  {
                     foreach (var img in result.Images)
                     {
                        img.ToolCallId = call.Id;
                        (_pendingToolImages ??= new List<ImagePart>()).Add(img);
                     }
                  }
               }
            }

            // Images a tool produced go out as one user turn after the whole
            // block of results: a tool message can't carry an image part, and
            // templates expect a call's results to follow it back to back.
            _FlushToolImages();

            // Surface stripped malformed-JSON calls: resolve their UI cards with
            // an error, then append one plain-text system note quoting the raw
            // arguments so the model can correct itself. The note goes after the
            // valid results so the assistant/tool pairing above stays intact.
            if (malformedCalls != null)
            {
               foreach (var call in malformedCalls)
               {
                  ToolCallReady?.Invoke(call);
                  ToolCallCompleted?.Invoke(call.Id,
                     ToolResult.Error("ill-formed JSON arguments — call not executed"));
               }
               AppendSystemMessage(BuildMalformedCallNote(malformedCalls));
            }
            else if (assistant.ToolCalls == null)
            {
               // ToolCalls finish but nothing actionable — avoid spinning.
               return;
            }
         }

         // Iteration cap exhausted.
         finish = FinishReason.Error;
         Error?.Invoke("agent exceeded MaxIterations (" + MaxIterations + ")");
      }
      catch (OperationCanceledException)
      {
         finish = FinishReason.Error;
         _RepairDanglingToolCalls();
         // Results that already said "the image follows" must not be left
         // without it.
         _FlushToolImages();
         Error?.Invoke("cancelled");
      }
      catch (Exception ex)
      {
         finish = FinishReason.Error;
         Error?.Invoke(ex.Message);
      }
      finally
      {
         // Close whatever phase was open (a cancel can land mid-stream) so
         // the run's total is honest, then hand the total over before the
         // completion event so a subscriber to both sees them in order.
         _EnterPhase(TurnPhase.Idle);
         TurnTimingsReported?.Invoke(_runTimings);
         TurnCompleted?.Invoke(finish);
      }
   }

   // -- one streaming turn ---------------------------------------------------

   // Drives one assistant turn: one streamed request, accumulated into a
   // TurnState the caller turns into a ChatMessage.
   private async Task<TurnState> DriveOneTurn(LlmRequest request, CancellationToken cancel)
   {
      // One accumulator for the whole assistant turn; it is read out once
      // when the turn returns.
      _unitTimings = default;

      var state = new TurnState();

      // Feeds the UI meter only — see ReasoningStatsChanged. Per turn, so the
      // reading describes the trace on screen and not the session so far.
      var watch = new ReasoningWatch();

      await _StreamOnce(request, state, watch, cancel).ConfigureAwait(false);

      state.Timings = _TakeUnitTimings();
      return state;
   }

   // Streams one request into `state`, running to the stream's natural end
   // (or to an error delta, which is reported and ends the turn).
   private async Task _StreamOnce(
      LlmRequest request,
      TurnState state,
      ReasoningWatch watch,
      CancellationToken cancel)
   {
      // Time to first token starts now, request serialization included: from
      // the user's point of view that is all "waiting for the model".
      _EnterPhase(TurnPhase.Prefill);

      await foreach (var delta in _client.StreamAsync(request, cancel).ConfigureAwait(false))
      {
         switch (delta.Kind)
         {
            case LlmDelta.DeltaKind.Text:
               if (!string.IsNullOrEmpty(delta.TextDelta))
               {
                  _EnterPhase(TurnPhase.Output);
                  state.Text.Append(delta.TextDelta);
                  TextDelta?.Invoke(delta.TextDelta!);
               }
               break;

            case LlmDelta.DeltaKind.ReasoningText:
               if (!string.IsNullOrEmpty(delta.ReasoningDelta))
               {
                  _EnterPhase(TurnPhase.Thinking);
                  state.Reasoning.Append(delta.ReasoningDelta);
                  ReasoningDelta?.Invoke(delta.ReasoningDelta!);

                  watch.Append(delta.ReasoningDelta);
                  ReasoningStatsChanged?.Invoke(watch.Snapshot());
               }
               break;

            case LlmDelta.DeltaKind.ToolCallStart:
            {
               _EnterPhase(TurnPhase.ToolArgs);
               var acc = state.GetOrCreateCall(delta.ToolCallIndex);
               if (!string.IsNullOrEmpty(delta.ToolCallId))
                  acc.Id = delta.ToolCallId!;
               if (!string.IsNullOrEmpty(delta.ToolCallNameDelta))
                  acc.Name.Append(delta.ToolCallNameDelta);

               // First time we have both id+name, announce it.
               if (!acc.Announced &&
                   !string.IsNullOrEmpty(acc.Id) &&
                   acc.Name.Length > 0)
               {
                  acc.Announced = true;
                  ToolCallStarted?.Invoke(new ToolCall
                  {
                     Id            = acc.Id,
                     Name          = acc.Name.ToString(),
                     ArgumentsJson = "",
                  });

                  // Args can legally precede the delta that makes the call
                  // announceable. Replay what was held back so the live view
                  // starts at byte zero — a JSON prefix parser handed a
                  // truncated start would never recover.
                  if (acc.Buffered != null && acc.Buffered.Length > 0)
                  {
                     ToolCallArgsDelta?.Invoke(acc.Id, acc.Buffered.ToString());
                     acc.Buffered = null;
                  }
               }
               break;
            }

            case LlmDelta.DeltaKind.ToolCallArgs:
            {
               var acc = state.GetOrCreateCall(delta.ToolCallIndex);
               if (!string.IsNullOrEmpty(delta.ToolCallArgsDelta))
               {
                  _EnterPhase(TurnPhase.ToolArgs);
                  acc.Args.Append(delta.ToolCallArgsDelta);
                  if (acc.Announced)
                     ToolCallArgsDelta?.Invoke(acc.Id, delta.ToolCallArgsDelta!);
                  else
                     (acc.Buffered ??= new StringBuilder()).Append(delta.ToolCallArgsDelta);
               }
               break;
            }

            case LlmDelta.DeltaKind.Finish:
               state.Finish = delta.Finish;
               break;

            case LlmDelta.DeltaKind.Usage:
               state.Usage = delta.Usage;
               state.Reads = _AttributeReads(delta.Usage);
               UsageReported?.Invoke(delta.Usage);
               break;

            case LlmDelta.DeltaKind.Error:
               state.Finish = FinishReason.Error;
               Error?.Invoke(delta.ErrorMessage ?? "<unknown stream error>");
               return;
         }
      }
   }

   // -- helpers --------------------------------------------------------------

   private static ChatMessage BuildAssistantMessage(TurnState turn)
   {
      var msg = new ChatMessage
      {
         Role              = ChatRole.Assistant,
         Content           = turn.Text.ToString(),
         ReasoningContent  = turn.Reasoning.Length > 0 ? turn.Reasoning.ToString() : null,
         ReasoningTokens   = turn.Usage?.ReasoningTokens,
         PromptTokens      = turn.Usage?.PromptTokens,
         CompletionTokens  = turn.Usage?.CompletionTokens,
         CachedReadTokens  = turn.Usage?.CachedReadTokens,
         CacheWriteTokens  = turn.Usage?.CacheWriteTokens,
         Timings           = turn.Timings,
         Reads             = turn.Reads,
      };

      if (turn.Calls.Count > 0)
      {
         msg.ToolCalls = new List<ToolCall>(turn.Calls.Count);
         // Order by stream index, not insertion order, in case deltas arrived
         // out of order (rare but allowed by the spec).
         var indices = new List<int>(turn.Calls.Keys);
         indices.Sort();
         foreach (var idx in indices)
         {
            var acc = turn.Calls[idx];
            msg.ToolCalls.Add(new ToolCall
            {
               Id            = acc.Id,
               Name          = acc.Name.ToString(),
               ArgumentsJson = acc.Args.Length > 0 ? acc.Args.ToString() : "{}",
            });
         }
      }
      return msg;
   }

   private async Task<ToolResult> ExecuteOne(ToolCall call, CancellationToken cancel)
   {
      if (string.IsNullOrEmpty(call.Name))
         return ToolResult.Error("tool call had no name");

      if (!_tools.TryGet(call.Name, out var tool))
         return ToolResult.Error("unknown tool: " + call.Name);

      JsonElement args;
      try
      {
         var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
         args = doc.RootElement.Clone();
      }
      catch (JsonException ex)
      {
         return ToolResult.Error("invalid tool arguments JSON: " + ex.Message);
      }

      // Permission gate. Skipped when no gate is wired (legacy / tests).
      // A Deny returns immediately so the tool body never runs; the model
      // sees the denial as an ordinary tool error and can recover.
      if (_gate != null)
      {
         // Timed apart from the tool body: a prompt the user leaves open for
         // ten minutes must not read as a slow tool.
         _EnterPhase(TurnPhase.PermissionWait);
         var preview = SafeArgsPreview(tool, args);
         var grant   = await _gate.CheckAsync(tool.Name, tool.Category, preview, cancel, SubagentLabel)
                                   .ConfigureAwait(false);
         if (grant.Decision == PermissionDecision.Deny)
            return ToolResult.Error("permission denied: " + grant.Reason);
      }

      // Build a per-call context so cancellation can be chained without
      // exposing the loop's token to the tool directly.
      var perCallCtx = new ToolContext(_toolContext.ProjectRoot, cancel, _toolContext.Folders);

      // For a subagent tool this phase spans the whole nested run; the nested
      // loop keeps its own clock, so nothing is counted twice at either level.
      _EnterPhase(TurnPhase.ToolExec);
      try
      {
         return await tool.ExecuteAsync(args, perCallCtx).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
         return ToolResult.Error("tool threw: " + ex.Message);
      }
   }

   // A cancel that lands after the assistant message entered history but
   // before every tool result was appended leaves tool_calls with no
   // matching tool messages. That shape is invalid on the wire (and gets
   // persisted), so every later turn — and any resumed session — would
   // resend it. Close each unanswered call with a synthetic "cancelled"
   // result so history stays well-formed.
   private void _RepairDanglingToolCalls()
   {
      ChatMessage? assistant = null;
      var answered = new HashSet<string>();
      for (int i = _history.Count - 1; i >= 0; i--)
      {
         var msg = _history[i];
         if (msg.Role == ChatRole.Assistant)
         {
            assistant = msg;
            break;
         }
         if (msg.Role == ChatRole.User)
            return; // cancelled mid-stream: no assistant turn was committed
         if (msg.Role == ChatRole.Tool && !string.IsNullOrEmpty(msg.ToolCallId))
            answered.Add(msg.ToolCallId!);
      }
      if (assistant?.ToolCalls == null)
         return;

      foreach (var call in assistant.ToolCalls)
      {
         if (answered.Contains(call.Id))
            continue;

         var result  = ToolResult.Error("cancelled by user before execution");
         var toolMsg = new ChatMessage
         {
            Role       = ChatRole.Tool,
            ToolCallId = call.Id,
            Content    = result.Content,
            IsError    = true,
         };
         _history.Add(toolMsg);
         MessageAdded?.Invoke(toolMsg);
         ToolCallCompleted?.Invoke(call.Id, result);
      }
   }

   // Append the images collected from this block of tool results as a single
   // synthesized user turn (ChatMessage.IsToolImages). No-op when none were.
   private void _FlushToolImages()
   {
      var images = _pendingToolImages;
      _pendingToolImages = null;
      if (images == null || images.Count == 0)
         return;

      var sb = new StringBuilder("[tool output] ");
      sb.Append(images.Count == 1 ? "Image" : "Images")
        .Append(" requested by the tool call(s) above:");
      foreach (var img in images)
      {
         sb.Append("\n- ").Append(img.FileName ?? "image")
           .Append(" (").Append(img.Width).Append('×').Append(img.Height).Append(')');
      }

      var msg = new ChatMessage
      {
         Role         = ChatRole.User,
         Content      = sb.ToString(),
         Images       = images,
         IsToolImages = true,
      };
      _history.Add(msg);
      MessageAdded?.Invoke(msg);
   }

   private static string SafeArgsPreview(ITool tool, JsonElement args)
   {
      try { return tool.ArgsPreview(args); }
      catch { return "(args preview unavailable)"; }
   }

   // True when the named tool advertises no parameters (empty-properties
   // schema) — e.g. the project build/test commands. Such tools ignore any
   // arguments, so a malformed/extra arguments string is harmless to drop.
   private bool IsZeroParamTool(string name)
   {
      return _tools.TryGet(name, out var tool) && SchemaHasNoProperties(tool.ParametersJsonSchema);
   }

   private static bool SchemaHasNoProperties(string? schemaJson)
   {
      if (string.IsNullOrWhiteSpace(schemaJson))
         return true;
      try
      {
         using var doc = JsonDocument.Parse(schemaJson);
         if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return false;
         if (!doc.RootElement.TryGetProperty("properties", out var props))
            return true;
         if (props.ValueKind != JsonValueKind.Object)
            return false;
         foreach (var _ in props.EnumerateObject())
            return false; // has at least one declared property
         return true;
      }
      catch (JsonException)
      {
         return false;
      }
   }

   // Prepend a "your arguments were ignored" warning to a no-arg tool's result
   // (the tool ran with {} regardless). Quotes the raw bytes when they were
   // also malformed JSON, so the model still sees what it did wrong.
   private static ToolResult WithArgIgnoredWarning(string toolName, string rawArgs, ToolResult result)
   {
      var warn = "Warning: the '" + toolName + "' tool takes no arguments; the arguments you sent were ignored.";
      if (!IsValidJsonArgs(rawArgs))
         warn += " They were also not valid JSON (raw received: " + rawArgs + "). Call this tool with no arguments.";
      return new ToolResult { IsError = result.IsError, Content = warn + "\n\n" + result.Content };
   }

   // True when the tool-call arguments are valid JSON. Empty/whitespace counts
   // as valid — BuildAssistantMessage already normalizes that to "{}".
   private static bool IsValidJsonArgs(string? args)
   {
      if (string.IsNullOrWhiteSpace(args))
         return true;
      try
      {
         using var _ = JsonDocument.Parse(args);
         return true;
      }
      catch (JsonException)
      {
         return false;
      }
   }

   // Plain-text correction note for tool calls whose arguments failed to parse.
   // The raw bytes are quoted inside ordinary message content (a JSON string on
   // the wire), so they round-trip cleanly and don't poison the prompt cache.
   private static string BuildMalformedCallNote(List<ToolCall> calls)
   {
      var sb = new StringBuilder();
      sb.Append("The following tool call(s) were rejected because their arguments were not ");
      sb.Append("valid JSON and could not be parsed, so they were not executed. ");
      sb.Append("Reissue each call with a single valid JSON object as its arguments.");
      foreach (var c in calls)
      {
         sb.Append("\n\n- tool: ").Append(c.Name);
         sb.Append("\n  raw arguments received: ").Append(c.ArgumentsJson);
      }
      return sb.ToString();
   }

   // -- per-turn accumulator -------------------------------------------------

   private sealed class TurnState
   {
      public readonly StringBuilder Text      = new();
      public readonly StringBuilder Reasoning = new();
      public readonly Dictionary<int, CallAcc> Calls = new();
      public FinishReason Finish = FinishReason.None;

      // Null until the provider sends a usage chunk for the turn — keeps
      // ChatMessage token fields null (not 0) when usage is never reported.
      public LlmUsage? Usage;

      /// <summary>Wall-clock phases of this turn, filled in when it returns.</summary>
      public PhaseTimings? Timings;

      /// <summary>Non-generated tokens this turn's call consumed; null without usage.</summary>
      public TokenReads? Reads;

      public CallAcc GetOrCreateCall(int idx)
      {
         if (!Calls.TryGetValue(idx, out var c))
         {
            c = new CallAcc();
            Calls[idx] = c;
         }
         return c;
      }
   }

   private sealed class CallAcc
   {
      public string Id = "";
      public readonly StringBuilder Name = new();
      public readonly StringBuilder Args = new();
      public bool Announced;

      /// <summary>Argument fragments received before the call was announced.
      /// Null once replayed (the common case never allocates).</summary>
      public StringBuilder? Buffered;
   }

   // -- timing ---------------------------------------------------------------

   // Credits the time since the last transition to the phase that was open,
   // in both accumulators, then opens `phase`. Called on every delta, but the
   // event only fires when the phase actually changes, so a stream of ten
   // thousand thinking tokens costs ten thousand subtractions and one event.
   private void _EnterPhase(TurnPhase phase)
   {
      var now = _clock.Elapsed;
      if (_phase != TurnPhase.Idle)
      {
         var span = now - _phaseStart;
         _runTimings.Add(_phase, span);
         _unitTimings.Add(_phase, span);
      }
      _phaseStart = now;
      if (_phase == phase)
         return;
      _phase = phase;
      PhaseChanged?.Invoke(phase, _runTimings);
   }

   // Closes the open interval without leaving the phase, hands back what the
   // current unit (assistant turn or tool call) accumulated, and starts the
   // next unit from zero.
   private PhaseTimings _TakeUnitTimings()
   {
      _EnterPhase(_phase);
      var unit = _unitTimings;
      _unitTimings = default;
      return unit;
   }

   // -- read accounting ------------------------------------------------------

   // Attributes the non-generated tokens of the call that just reported
   // `usage` to what was appended to history since the previous call. See
   // TokenReads for the model; the mechanics:
   //
   //   delta = prompt(now) - (prompt + completion)(previous call)
   //
   // is exact for the appended messages (modulo chat-template framing) as
   // long as the same model tokenized both. Text categories are split in
   // proportion to their character counts, which is exact when only one
   // category was appended - the common case: a user prompt, or a block of
   // tool results. Images take the remainder over the text estimate, since
   // there is no client-side way to know how a vision encoder chunks them.
   private TokenReads _AttributeReads(LlmUsage usage)
   {
      long promptChars = 0;
      long toolChars   = 0;
      int  imageCount  = 0;

      // The first call of a fresh session also carries the tool schemas,
      // which live in the request rather than in history.
      if (_readCursor == 0)
         promptChars += _toolSpecChars;

      for (int i = _readCursor; i < _history.Count; i++)
      {
         var m = _history[i];
         switch (m.Role)
         {
            case ChatRole.User:
               promptChars += m.Content.Length;
               if (m.Images != null) imageCount += m.Images.Count;
               break;
            case ChatRole.System:
               promptChars += m.Content.Length;
               break;
            case ChatRole.Tool:
               toolChars += m.Content.Length;
               break;
            // Assistant turns were written, not read; they are what the
            // baseline's completion count already covers.
         }
      }
      _readCursor = _history.Count;

      var reads = new TokenReads();
      int? baseline = _readBaseline;
      bool fresh    = _freshSession;
      _readBaseline = usage.PromptTokens + usage.CompletionTokens;
      _freshSession = false;

      if (fresh)
      {
         // First call of a new session: nothing in the prompt was generated,
         // so the provider's count IS the read total, exactly.
         _Attribute(ref reads, usage.PromptTokens,
                    promptChars, toolChars, imageCount);
      }
      else if (!baseline.HasValue)
      {
         // Resumed without a usage-bearing turn, or the model was swapped:
         // the previous size does not compare. Character estimate only;
         // image cost is unknowable here and stays at zero.
         reads.Prompt      = _EstimateTokens(promptChars);
         reads.ToolResults = _EstimateTokens(toolChars);
         reads.Estimated   = true;
      }
      else
      {
         // Framing noise or a shrunk prompt (thinking policy dropped an old
         // trace) can push the delta below zero; that is not a read.
         int delta = Math.Max(0, usage.PromptTokens - baseline.Value);
         _Attribute(ref reads, delta, promptChars, toolChars, imageCount);
      }
      return reads;
   }

   // Distributes an exact token count over the categories that were
   // appended. Images get the remainder over the text estimate, which makes
   // the whole call an estimate; without images the split is by chars only.
   private static void _Attribute(ref TokenReads reads, int tokens,
                                  long promptChars, long toolChars, int imageCount)
   {
      if (imageCount > 0)
      {
         int textEst = _EstimateTokens(promptChars + toolChars);
         reads.Images    = Math.Max(0, tokens - textEst);
         reads.Estimated = true;
         tokens         -= reads.Images;
      }
      _SplitByChars(ref reads, tokens, promptChars, toolChars);
   }

   // Divides `tokens` between the two text categories by character share.
   // Nothing appended at all (a pure framing delta) counts as prompt.
   private static void _SplitByChars(ref TokenReads reads, int tokens, long promptChars, long toolChars)
   {
      if (tokens <= 0) return;
      long total = promptChars + toolChars;
      if (total <= 0)
      {
         reads.Prompt += tokens;
         return;
      }
      int tool = (int)Math.Round(tokens * (double)toolChars / total);
      reads.ToolResults += tool;
      reads.Prompt      += tokens - tool;
   }

   private static int _EstimateTokens(long chars) => (int)Math.Round(chars / 3.6);

   // Rough size of the advertised tool schemas, for the first-call estimate.
   private static long _SpecChars(IReadOnlyList<LlmToolSpec> specs)
   {
      long n = 0;
      foreach (var s in specs)
         n += s.Name.Length + s.Description.Length + s.ParametersJsonSchema.Length;
      return n;
   }

   // -- fields ---------------------------------------------------------------

   private readonly Stopwatch          _clock = Stopwatch.StartNew();
   private TurnPhase                   _phase = TurnPhase.Idle;
   private TimeSpan                    _phaseStart;
   private PhaseTimings                _runTimings;    // whole RunAsync
   private PhaseTimings                _unitTimings;   // current assistant turn or tool call

   // Read accounting: context size after the previous call (null = no
   // comparable call), and the history index its attribution stopped at.
   private int?                        _readBaseline;
   private int                         _readCursor;
   private long                        _toolSpecChars;
   // True until the first usage report of a session started from scratch,
   // whose whole prompt is by construction non-generated.
   private bool                        _freshSession;

   private ILlmClient                 _client;
   private ToolRegistry               _tools;
   private readonly ToolContext       _toolContext;
   private string                     _model;
   private readonly string            _systemPrompt;
   private readonly List<ChatMessage> _history = new();
   private readonly PermissionGate?   _gate;

   // Images returned by the current block of tool calls, not yet delivered.
   private List<ImagePart>?           _pendingToolImages;
}
