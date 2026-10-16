// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Llm;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>
/// MCP "Streamable HTTP": every client message is a POST to one endpoint, and
/// the reply is either a single JSON object or an SSE stream carrying one or
/// more messages. The server may mint a session id on initialize, which every
/// later request has to echo back.
///
/// Only the POST half is implemented. The spec also allows a standing GET/SSE
/// channel for unsolicited server messages; without it a
/// <c>notifications/tools/list_changed</c> only reaches us when the server
/// folds it into the response stream of some other call. That is a v1 gap, not
/// a correctness problem: the catalog is re-listed on every session's first
/// send anyway (see the resume diff in NativeAgentView).
///
/// The deprecated "HTTP+SSE" transport (separate GET endpoint + POST-back URL)
/// is not supported.
/// </summary>
public sealed class McpHttpTransport : IMcpTransport
{
   public McpHttpTransport(string url, IReadOnlyDictionary<string, string>? headers)
   {
      _url     = url ?? "";
      _headers = headers;
   }

   public event Action<string>? MessageReceived;
   public event Action<string>? LogLine;
   public event Action<string>? Closed;

   public string Describe => "http: " + _url;

   /// <summary>
   /// Negotiated protocol version, sent as <c>MCP-Protocol-Version</c> on every
   /// request after initialize. Set by <see cref="McpClient"/> once the server
   /// has answered; servers that predate the header ignore it.
   /// </summary>
   public string? ProtocolVersion { get; set; }

   /// <summary>Server-assigned session id, captured from the first response that carries one.</summary>
   public string? SessionId => _sessionId;

   public Task StartAsync(CancellationToken cancel)
   {
      if (string.IsNullOrWhiteSpace(_url))
         throw new InvalidOperationException("no URL configured");
      if (!Uri.TryCreate(_url, UriKind.Absolute, out var uri) ||
          (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
         throw new InvalidOperationException("not an http(s) URL: " + _url);

      // No overall timeout on the client: a tool call's budget is enforced by
      // the caller's CancellationToken, and one ceiling for both a 5 ms
      // tools/list and a 10 minute tool call would have to be wrong for one
      // of them.
      _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
      return Task.CompletedTask;
   }

   public async Task SendAsync(string json, CancellationToken cancel)
   {
      var http = _http;
      if (http == null || _disposed)
         throw new InvalidOperationException("http transport is not running");

      using var req = new HttpRequestMessage(HttpMethod.Post, _url)
      {
         Content = new StringContent(json, new UTF8Encoding(false), "application/json"),
      };
      req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
      if (!string.IsNullOrEmpty(_sessionId))
         req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
      if (!string.IsNullOrEmpty(ProtocolVersion))
         req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
      if (_headers != null)
      {
         foreach (var kv in _headers)
         {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value ?? "");
         }
      }

      // ResponseHeadersRead: an SSE body must be streamed, not buffered — the
      // whole point is that messages arrive before the stream ends.
      var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel)
                           .ConfigureAwait(false);

      if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids))
      {
         foreach (var id in ids)
         {
            if (!string.IsNullOrWhiteSpace(id)) { _sessionId = id; break; }
         }
      }

      if (!resp.IsSuccessStatusCode)
      {
         string body = "";
         try { body = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false); } catch { }
         resp.Dispose();
         throw new InvalidOperationException(
            "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase +
            (string.IsNullOrWhiteSpace(body) ? "" : ": " + _Clip(body)));
      }

      // 202 with no body: the server accepted a notification. Nothing to read.
      var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
      if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
      {
         // Pumped in the background so this call returns as soon as the
         // response has started. The caller is waiting on a correlated reply,
         // not on the stream, and a server that holds the stream open after
         // answering must not wedge the send path.
         _ = Task.Run(() => _PumpSseAsync(resp));
         return;
      }

      try
      {
         var body = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
         if (!string.IsNullOrWhiteSpace(body))
            MessageReceived?.Invoke(body);
      }
      finally
      {
         resp.Dispose();
      }
   }

   public void Dispose()
   {
      if (_disposed) return;
      _disposed = true;
      try { _pumpCts.Cancel(); } catch { }
      try { _http?.Dispose(); } catch { }
      _http = null;
      try { _pumpCts.Dispose(); } catch { }
   }

   // -- private --------------------------------------------------------------

   // Deliberately tied to the transport's own lifetime rather than to the
   // caller's token: the caller stops waiting as soon as its correlated reply
   // arrives, and its CancellationTokenSource is disposed right after — a
   // linked source registered on it would then be reading a disposed source.
   // The stream ends on its own when the server closes it, and transport
   // disposal is the backstop.
   private async Task _PumpSseAsync(HttpResponseMessage resp)
   {
      var token = _pumpCts.Token;
      try
      {
         using (resp)
         {
            var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await foreach (var payload in SseReader.ReadAsync(stream, token).ConfigureAwait(false))
            {
               if (string.IsNullOrWhiteSpace(payload)) continue;
               MessageReceived?.Invoke(payload);
            }
         }
      }
      catch (OperationCanceledException) { }
      catch (Exception ex)
      {
         if (!_disposed)
            LogLine?.Invoke("stream error: " + ex.Message);
      }
   }

   private static string _Clip(string s)
   {
      s = s.Replace('\n', ' ').Replace('\r', ' ');
      return s.Length <= 300 ? s : s.Substring(0, 300) + "…";
   }

   // -- fields ---------------------------------------------------------------

   private readonly string                              _url;
   private readonly IReadOnlyDictionary<string, string>? _headers;
   private readonly CancellationTokenSource             _pumpCts = new();

   private HttpClient? _http;
   private string?     _sessionId;

   private volatile bool _disposed;
}
