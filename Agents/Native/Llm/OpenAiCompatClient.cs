// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// <see cref="ILlmClient"/> against any OpenAI-compatible chat.completions
/// endpoint. Verified shapes: llama.cpp llama-server, llama-swap, OpenAI,
/// OpenRouter, vLLM. The API key is optional (local servers ignore it).
///
/// Streaming: parses SSE chunks, fans them out as <see cref="LlmDelta"/>
/// events. Tool-call argument fragments are forwarded unmerged — the
/// caller (AgentLoop) is responsible for concatenating per-index.
/// </summary>
public sealed class OpenAiCompatClient : ILlmClient, IDisposable
{
   public OpenAiCompatClient(string baseUrl, string? apiKey = null, HttpClient? http = null)
   {
      if (string.IsNullOrWhiteSpace(baseUrl))
         throw new ArgumentException("baseUrl required", nameof(baseUrl));

      _baseUrl = baseUrl.TrimEnd('/');
      _apiKey  = apiKey;
      _http    = http ?? new HttpClient();
      _ownsHttp = http == null;

      // Streaming requests can run for many minutes; the default 100s
      // timeout would abort long agent turns.
      if (_ownsHttp)
         _http.Timeout = Timeout.InfiniteTimeSpan;
   }

   public void Dispose()
   {
      if (_ownsHttp)
      {
         try { _http.Dispose(); } catch { }
      }
   }

   public async IAsyncEnumerable<LlmDelta> StreamAsync(
      LlmRequest request,
      [EnumeratorCancellation] CancellationToken cancel)
   {
      var body = BuildRequest(request);
      var json = JsonSerializer.Serialize(body, s_jsonOpts);

      using var http = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/chat/completions");
      http.Content = new StringContent(json, Encoding.UTF8, "application/json");
      http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
      if (!string.IsNullOrEmpty(_apiKey))
         http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

      HttpResponseMessage? resp = null;
      try
      {
         resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancel)
                            .ConfigureAwait(false);

         if (!resp.IsSuccessStatusCode)
         {
            var errBody = await SafeReadBody(resp).ConfigureAwait(false);
            yield return LlmDelta.FromError("HTTP " + (int)resp.StatusCode + ": " + errBody);
            yield return LlmDelta.FromFinish(FinishReason.Error);
            yield break;
         }

         using var stream = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
         await foreach (var payload in SseReader.ReadAsync(stream, cancel).ConfigureAwait(false))
         {
            if (payload == "[DONE]")
               yield break;

            foreach (var delta in ParseChunk(payload))
               yield return delta;
         }
      }
      finally
      {
         resp?.Dispose();
      }
   }

   // -- request construction -------------------------------------------------

   private static OaChatRequest BuildRequest(LlmRequest req)
   {
      var oa = new OaChatRequest
      {
         Model       = req.Model,
         Stream      = true,
         Temperature = req.Temperature,
         MaxTokens   = req.MaxTokens,
         StreamOptions = new OaStreamOptions { IncludeUsage = true },
      };

      // Pin the conversation to one cache partition on routed providers.
      if (!string.IsNullOrEmpty(req.CacheKey))
         oa.PromptCacheKey = req.CacheKey;

      oa.ChatTemplateKwargs = MergeReasoning(req.ChatTemplateKwargsJson, req.Reasoning);
      oa.ResponseFormat     = TryParseObject(req.ResponseFormatJson);
      oa.ReasoningEffort    = req.Reasoning?.Effort;

      // Which assistant turns get their trace back (see ThinkingPolicy). The
      // last-turn index is resolved up front so the loop stays a single pass.
      var  scope        = (req.Thinking ?? ThinkingPolicy.Default).Scope;
      int  traceOnlyAt  = scope == ThinkingScope.LastAssistantTurn
                             ? LastTracedAssistant(req.Messages) : -1;

      for (int i = 0; i < req.Messages.Count; i++)
      {
         var m = req.Messages[i];
         bool withTrace = scope switch
         {
            ThinkingScope.All               => true,
            ThinkingScope.LastAssistantTurn => i == traceOnlyAt,
            _                               => false,
         };
         oa.Messages.Add(ConvertMessage(m, withTrace));
      }

      NormalizeSystemMessages(oa.Messages);

      if (req.Tools != null && req.Tools.Count > 0)
      {
         oa.Tools = new List<OaTool>(req.Tools.Count);
         foreach (var t in req.Tools)
            oa.Tools.Add(ConvertTool(t));
      }
      return oa;
   }

   // Some OpenAI-compatible chat templates (Mistral- and llama-derived) accept
   // only a single system message, at index 0, and raise "System message must
   // be at the beginning" on any later one. SlimCode legitimately emits several
   // system turns — the base prompt, per-skill injections, mode-switch
   // announcements — so normalize before sending:
   //   * Fold the run of consecutive leading system messages into one block.
   //   * Demote every remaining (mid-conversation) system message to a user
   //     turn, tagged so its origin stays legible to the model.
   // Content-only system turns are assumed (they carry no tool_calls /
   // tool_call_id), which holds for every producer above.
   private static void NormalizeSystemMessages(List<OaMessage> messages)
   {
      if (messages.Count == 0) return;

      // -- fold the leading system run into messages[0] ----------------------
      if (messages[0].Role == "system")
      {
         int end = 1;                       // first index past the leading run
         while (end < messages.Count && messages[end].Role == "system")
            end++;

         if (end > 1)
         {
            var sb = new System.Text.StringBuilder(messages[0].Text ?? "");
            for (int i = 1; i < end; i++)
            {
               if (sb.Length > 0) sb.Append("\n\n");
               sb.Append(messages[i].Text ?? "");
            }
            messages[0].Text = sb.ToString();
            messages.RemoveRange(1, end - 1);
         }
      }

      // -- demote any system message that isn't at index 0 -------------------
      for (int i = 1; i < messages.Count; i++)
      {
         if (messages[i].Role != "system") continue;
         messages[i].Role = "user";
         messages[i].Text = "[system] " + (messages[i].Text ?? "");
      }
   }

   // Parse a caller-supplied raw JSON object into a JsonElement for verbatim
   // forwarding. Anything unparseable (or not an object) is dropped — a bad
   // template kwarg must not take the whole request down.
   private static JsonElement? TryParseObject(string? json)
   {
      if (string.IsNullOrWhiteSpace(json))
         return null;
      try
      {
         using var doc = JsonDocument.Parse(json!);
         if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;
         return doc.RootElement.Clone();
      }
      catch (JsonException)
      {
         return null;
      }
   }

   // Overlay the resolved reasoning controls onto a caller-supplied kwargs
   // object. Reasoning wins on a key collision: it is this call's clamped
   // intent, whereas the raw JSON is a static per-model escape hatch.
   //
   // The effort is written here as well as top-level. Both forms reach the
   // Jinja template on a current llama-server, but only the kwargs form is
   // reliable on older builds, and the duplication cannot disagree — one
   // resolved value feeds both.
   private static JsonElement? MergeReasoning(string? json, ReasoningRequest? reasoning)
   {
      var raw = TryParseObject(json);
      if (reasoning == null)
         return raw;

      var merged = new Dictionary<string, object?>();
      if (raw != null)
      {
         foreach (var p in raw.Value.EnumerateObject())
            merged[p.Name] = p.Value;
      }

      merged["enable_thinking"] = reasoning.EnableThinking;
      if (!string.IsNullOrEmpty(reasoning.Effort))
         merged["reasoning_effort"] = reasoning.Effort;

      using var doc = JsonDocument.Parse(JsonSerializer.Serialize(merged));
      return doc.RootElement.Clone();
   }

   // Index of the last assistant message that actually carries a trace, or -1.
   private static int LastTracedAssistant(IReadOnlyList<ChatMessage> messages)
   {
      for (int i = messages.Count - 1; i >= 0; i--)
      {
         if (messages[i].Role == ChatRole.Assistant &&
             !string.IsNullOrEmpty(messages[i].ReasoningContent))
            return i;
      }
      return -1;
   }

   // withTrace decides whether this turn's captured reasoning goes back on
   // the wire. Only assistant turns can carry it: the
   // field is meaningless on user/tool messages and some templates choke on it
   // there. An assistant message that would otherwise be empty keeps its ""
   // content — llama-server rejects a message carrying reasoning and nothing
   // else.
   private static OaMessage ConvertMessage(ChatMessage m, bool withTrace)
   {
      var oa = new OaMessage
      {
         Role       = RoleToString(m.Role),
         Text       = m.Content,
         ToolCallId = m.ToolCallId,
      };

      // Images promote content from a string to the parts array. User turns
      // only: that is the sole role the OpenAI schema (and llama-server)
      // accepts an image part on, and ChatMessage.Images is never populated
      // elsewhere. A model without vision support answers this with a server
      // error, which is the honest outcome — there is no capability flag to
      // second-guess it with.
      if (m.Role == ChatRole.User && m.Images is { Count: > 0 })
      {
         var parts = BuildContentParts(m.Content, m.Images);
         if (parts != null)
            oa.Content = parts;
      }

      if (withTrace &&
          m.Role == ChatRole.Assistant &&
          !string.IsNullOrEmpty(m.ReasoningContent))
      {
         oa.ReasoningContent = m.ReasoningContent;
         oa.Content ??= "";
      }
      if (m.ToolCalls != null && m.ToolCalls.Count > 0)
      {
         oa.ToolCalls = new List<OaToolCall>(m.ToolCalls.Count);
         for (int i = 0; i < m.ToolCalls.Count; i++)
         {
            var c = m.ToolCalls[i];
            oa.ToolCalls.Add(new OaToolCall
            {
               Index = i,
               Id    = c.Id,
               Type  = "function",
               Function = new OaToolCallFunction
               {
                  Name      = c.Name,
                  Arguments = c.ArgumentsJson,
               },
            });
         }
         // OpenAI requires content be present (may be empty string) on
         // assistant turns that emit tool calls.
         oa.Content ??= "";
      }
      return oa;
   }

   // Text part first, then one image_url part per attachment. Ordering is
   // deliberate: some chat templates render only the leading text when they
   // don't understand the rest, and the prompt is the part worth surviving.
   // Returns null when nothing usable is left (all attachments empty), so the
   // caller keeps the plain string form.
   private static List<OaContentPart>? BuildContentParts(string? text, List<ImagePart> images)
   {
      var parts = new List<OaContentPart>(images.Count + 1);
      if (!string.IsNullOrEmpty(text))
         parts.Add(new OaContentPart { Type = "text", Text = text });

      int imageCount = 0;
      foreach (var img in images)
      {
         if (img == null || string.IsNullOrEmpty(img.Base64)) continue;
         parts.Add(new OaContentPart
         {
            Type     = "image_url",
            ImageUrl = new OaImageUrl { Url = img.ToDataUrl() },
         });
         imageCount++;
      }
      return imageCount > 0 ? parts : null;
   }

   private static OaTool ConvertTool(LlmToolSpec t)
   {
      // Parse the schema string into a JsonElement so it serializes as
      // a JSON object, not as an escaped string literal.
      JsonElement schema;
      try
      {
         using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(t.ParametersJsonSchema)
                                            ? "{}" : t.ParametersJsonSchema);
         schema = doc.RootElement.Clone();
      }
      catch
      {
         using var doc = JsonDocument.Parse("{}");
         schema = doc.RootElement.Clone();
      }

      return new OaTool
      {
         Type = "function",
         Function = new OaToolFunction
         {
            Name        = t.Name,
            Description = t.Description,
            Parameters  = schema,
         },
      };
   }

   private static string RoleToString(ChatRole r) => r switch
   {
      ChatRole.System    => "system",
      ChatRole.User      => "user",
      ChatRole.Assistant => "assistant",
      ChatRole.Tool      => "tool",
      _ => "user",
   };

   // -- chunk parsing --------------------------------------------------------

   private static IEnumerable<LlmDelta> ParseChunk(string json)
   {
      OaStreamChunk? chunk = null;
      string? parseError = null;
      try
      {
         chunk = JsonSerializer.Deserialize<OaStreamChunk>(json, s_jsonOpts);
      }
      catch (JsonException ex)
      {
         parseError = ex.Message;
      }
      if (parseError != null)
      {
         yield return LlmDelta.FromError("malformed SSE chunk: " + parseError);
         yield break;
      }
      if (chunk == null)
         yield break;

      if (chunk.Choices != null)
      {
         foreach (var ch in chunk.Choices)
         {
            var d = ch.Delta;
            if (d != null)
            {
               if (!string.IsNullOrEmpty(d.ReasoningContent))
                  yield return LlmDelta.Reasoning(d.ReasoningContent!);

               if (!string.IsNullOrEmpty(d.Content))
                  yield return LlmDelta.Text(d.Content!);

               if (d.ToolCalls != null)
               {
                  foreach (var tc in d.ToolCalls)
                  {
                     int idx = tc.Index ?? 0;
                     // A tool-call delta with an id/name marks the start
                     // of that call; subsequent deltas only carry args.
                     if (!string.IsNullOrEmpty(tc.Id) || !string.IsNullOrEmpty(tc.Function?.Name))
                     {
                        yield return LlmDelta.ToolStart(
                           idx,
                           tc.Id ?? "",
                           tc.Function?.Name ?? "");
                     }
                     if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                        yield return LlmDelta.ToolArgs(idx, tc.Function!.Arguments!);
                  }
               }
            }

            if (!string.IsNullOrEmpty(ch.FinishReason))
               yield return LlmDelta.FromFinish(MapFinish(ch.FinishReason!));
         }
      }

      if (chunk.Usage != null)
      {
         var u = chunk.Usage;
         // Read the cache-read subset from whichever field the provider sends.
         int cachedRead = u.PromptTokensDetails?.CachedTokens
                          ?? u.PromptCacheHitTokens
                          ?? 0;
         // Server timings ride on the same final chunk as usage on
         // llama-server; folded into the usage record so one event carries
         // both. A timings object on a chunk without usage is dropped.
         var t = chunk.Timings;
         yield return LlmDelta.FromUsage(new LlmUsage
         {
            PromptTokens          = u.PromptTokens,
            CompletionTokens      = u.CompletionTokens,
            ReasoningTokens       = u.CompletionTokensDetails?.ReasoningTokens,
            CachedReadTokens      = cachedRead,
            CacheWriteTokens      = u.CacheCreationInputTokens ?? 0,
            ServerPromptTokens    = t?.PromptN,
            ServerPromptTime      = t?.PromptMs    is double pm ? TimeSpan.FromMilliseconds(pm) : (TimeSpan?)null,
            ServerPredictedTokens = t?.PredictedN,
            ServerPredictedTime   = t?.PredictedMs is double gm ? TimeSpan.FromMilliseconds(gm) : (TimeSpan?)null,
         });
      }
   }

   private static FinishReason MapFinish(string s) => s switch
   {
      "stop"        => FinishReason.Stop,
      "tool_calls"  => FinishReason.ToolCalls,
      "length"      => FinishReason.Length,
      _             => FinishReason.None,
   };

   private static async Task<string> SafeReadBody(HttpResponseMessage resp)
   {
      try
      {
         var s = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
         if (s.Length > 500)
            s = s.Substring(0, 500) + "…";
         return s;
      }
      catch
      {
         return "<unreadable>";
      }
   }

   // -- model discovery ------------------------------------------------------

   /// <summary>
   /// One-shot fetch of the server's <c>/v1/models</c> list. Returns the
   /// raw model ids in the order the server reports them. Most local
   /// servers (llama.cpp, llama-swap, vLLM) implement this endpoint;
   /// cloud providers (OpenAI, OpenRouter) do too. Throws on transport
   /// or HTTP errors so the caller can show a clear failure in the UI.
   /// </summary>
   public static async Task<IReadOnlyList<string>> ListModelsAsync(
      string baseUrl,
      string? apiKey,
      CancellationToken cancel,
      HttpClient? http = null)
   {
      if (string.IsNullOrWhiteSpace(baseUrl))
         throw new ArgumentException("baseUrl required", nameof(baseUrl));

      var url = baseUrl.TrimEnd('/') + "/v1/models";
      var ownsHttp = http == null;
      http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

      try
      {
         using var req = new HttpRequestMessage(HttpMethod.Get, url);
         if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

         using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
         if (!resp.IsSuccessStatusCode)
         {
            var body = await SafeReadBody(resp).ConfigureAwait(false);
            throw new HttpRequestException("GET /v1/models → " + (int)resp.StatusCode + ": " + body);
         }

         var json = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
         var doc  = JsonSerializer.Deserialize<OaModelsResponse>(json, s_jsonOpts);

         var ids = new List<string>();
         if (doc?.Data != null)
         {
            foreach (var m in doc.Data)
            {
               if (!string.IsNullOrWhiteSpace(m.Id))
                  ids.Add(m.Id);
            }
         }
         return ids;
      }
      finally
      {
         if (ownsHttp)
         {
            try { http.Dispose(); } catch { }
         }
      }
   }

   private sealed class OaModelsResponse
   {
      [JsonPropertyName("data")] public List<OaModelEntry>? Data { get; set; }
   }

   private sealed class OaModelEntry
   {
      [JsonPropertyName("id")] public string? Id { get; set; }
   }

   // -- fields ---------------------------------------------------------------

   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      PropertyNameCaseInsensitive = true,
   };

   private readonly string      _baseUrl;
   private readonly string?     _apiKey;
   private readonly HttpClient  _http;
   private readonly bool        _ownsHttp;
}
