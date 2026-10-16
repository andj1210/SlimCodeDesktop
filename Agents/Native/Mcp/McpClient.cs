// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>Connection state of one <see cref="McpClient"/>, for the UI's status dot.</summary>
public enum McpState
{
   NotConnected,
   Connecting,
   Connected,
   Failed,
}

/// <summary>
/// The client half of MCP, hand-rolled over <see cref="IMcpTransport"/>:
/// lifecycle (initialize → notifications/initialized), <c>tools/list</c> and
/// <c>tools/call</c>. Resources, prompts, sampling and elicitation are out of
/// scope; server-initiated requests are answered with method-not-found so a
/// server that asks anyway gets a refusal instead of silence.
///
/// Hand-rolled rather than taking the pre-1.0 <c>ModelContextProtocol</c>
/// package: the client role is this file, and a breaking dependency on a
/// moving target would cost more than it saves. Revisit if sampling is ever
/// needed — that is where the real protocol surface starts.
///
/// Thread-safe for concurrent calls: requests are correlated by id through
/// <see cref="_pending"/>, and the transport serializes writes.
/// </summary>
public sealed class McpClient : IDisposable
{
   public McpClient(IMcpTransport transport)
   {
      _transport = transport ?? throw new ArgumentNullException(nameof(transport));
      _transport.MessageReceived += _OnMessage;
      _transport.LogLine         += line => Log?.Invoke(line);
      _transport.Closed          += _OnClosed;
   }

   // -- public surface -------------------------------------------------------

   public McpState State { get; private set; } = McpState.NotConnected;

   /// <summary>Why the connection failed, when <see cref="State"/> is Failed.</summary>
   public string? FailureReason { get; private set; }

   /// <summary>What the server reported during initialize. Null before connecting.</summary>
   public McpServerIdentity? Identity { get; private set; }

   /// <summary>Fires when the server announces that its tool catalog changed.</summary>
   public event Action? ToolsListChanged;

   /// <summary>Diagnostics: the server's stderr and transport-level oddities.</summary>
   public event Action<string>? Log;

   /// <summary>
   /// Start the transport, negotiate, and announce readiness. Sets
   /// <see cref="State"/> either way; throws on failure so the caller can put
   /// the reason on screen.
   /// </summary>
   public async Task<McpServerIdentity> InitializeAsync(CancellationToken cancel)
   {
      State         = McpState.Connecting;
      FailureReason = null;

      try
      {
         await _transport.StartAsync(cancel).ConfigureAwait(false);

         var initParams = new StringBuilder();
         initParams.Append("{\"protocolVersion\":").Append(McpJsonRpc.Quote(PROTOCOL_VERSION));
         // An empty capabilities object is the honest declaration: we support
         // no roots, no sampling, no elicitation. Servers gate optional
         // behavior on this, so overstating it would invite requests we would
         // then have to refuse.
         initParams.Append(",\"capabilities\":{}");
         initParams.Append(",\"clientInfo\":{\"name\":").Append(McpJsonRpc.Quote(AppInfo.Name))
                   .Append(",\"version\":").Append(McpJsonRpc.Quote(AppInfo.Version)).Append("}}");

         var result = await _RequestAsync("initialize", initParams.ToString(), cancel)
                            .ConfigureAwait(false);

         var identity = new McpServerIdentity
         {
            ProtocolVersion  = _Str(result, "protocolVersion") ?? "",
            ServerName       = _Nested(result, "serverInfo", "name")    ?? "",
            ServerVersion    = _Nested(result, "serverInfo", "version") ?? "",
            ToolsListChanged = _ToolsListChangedDeclared(result),
         };
         Identity = identity;

         // Echo the version the server settled on, not the one we asked for:
         // it is allowed to answer with an older revision it actually speaks.
         if (_transport is McpHttpTransport http && !string.IsNullOrEmpty(identity.ProtocolVersion))
            http.ProtocolVersion = identity.ProtocolVersion;

         await _transport.SendAsync(McpJsonRpc.Notification("notifications/initialized", null), cancel)
                         .ConfigureAwait(false);

         State = McpState.Connected;
         return identity;
      }
      catch (Exception ex)
      {
         State         = McpState.Failed;
         FailureReason = ex is OperationCanceledException ? "connect cancelled" : ex.Message;
         throw;
      }
   }

   /// <summary>
   /// Every tool the server advertises, following <c>nextCursor</c> to the end.
   /// </summary>
   public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancel)
   {
      var tools  = new List<McpToolInfo>();
      string? cursor = null;

      // Bounded: a server that keeps handing back a cursor would otherwise
      // spin here forever, and no real catalog is 100 pages long.
      for (int page = 0; page < 100; page++)
      {
         var args = cursor == null ? "{}" : "{\"cursor\":" + McpJsonRpc.Quote(cursor) + "}";
         var result = await _RequestAsync("tools/list", args, cancel).ConfigureAwait(false);

         if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
         {
            foreach (var el in arr.EnumerateArray())
            {
               var info = McpToolInfo.FromJson(el);
               if (info != null) tools.Add(info);
            }
         }

         cursor = _Str(result, "nextCursor");
         if (string.IsNullOrEmpty(cursor)) break;
      }

      return tools;
   }

   /// <summary>
   /// Invoke one tool. <paramref name="argumentsJson"/> is the model's raw
   /// arguments object, forwarded verbatim. A protocol-level failure comes
   /// back as <see cref="McpCallResult.IsError"/> rather than an exception —
   /// the model can recover from a failed tool, but not from an aborted turn.
   /// </summary>
   public async Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson,
                                                  CancellationToken cancel)
   {
      var args = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
      var sb = new StringBuilder();
      sb.Append("{\"name\":").Append(McpJsonRpc.Quote(toolName))
        .Append(",\"arguments\":").Append(args).Append('}');

      try
      {
         var result = await _RequestAsync("tools/call", sb.ToString(), cancel).ConfigureAwait(false);
         return McpCallResult.FromResult(result);
      }
      catch (OperationCanceledException)
      {
         throw;
      }
      catch (Exception ex)
      {
         return McpCallResult.Failure(ex.Message);
      }
   }

   public void Dispose()
   {
      if (_disposed) return;
      _disposed = true;

      _FailAllPending("client disposed");
      try { _transport.Dispose(); } catch { }
      if (State != McpState.Failed) State = McpState.NotConnected;
   }

   // -- request plumbing -----------------------------------------------------

   /// <summary>
   /// Send a request and await its response. Returns the <c>result</c> object;
   /// a JSON-RPC error is raised as an exception carrying the server's message.
   /// </summary>
   private async Task<JsonElement> _RequestAsync(string method, string? paramsJson,
                                                 CancellationToken cancel)
   {
      if (_disposed) throw new ObjectDisposedException(nameof(McpClient));

      var id  = McpJsonRpc.NextId();
      // RunContinuationsAsynchronously: the completion runs on the transport's
      // reader thread, and letting an awaiting caller's continuation execute
      // inline there would stall the reader for the rest of the turn.
      var tcs = new TaskCompletionSource<McpMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_gate) _pending[id] = tcs;

      try
      {
         await _transport.SendAsync(McpJsonRpc.Request(id, method, paramsJson), cancel)
                         .ConfigureAwait(false);

         using var reg = cancel.Register(() => tcs.TrySetCanceled(cancel));
         var msg = await tcs.Task.ConfigureAwait(false);

         if (msg.HasError)
            throw new InvalidOperationException(msg.ErrorMessage ?? "server returned an error");
         if (!msg.HasResult)
            throw new InvalidOperationException("server response carried no result");
         return msg.Result;
      }
      finally
      {
         lock (_gate) _pending.Remove(id);
      }
   }

   private void _OnMessage(string json)
   {
      var msg = McpMessage.TryParse(json);
      if (msg == null)
      {
         Log?.Invoke("dropped a non-JSON line from the server");
         return;
      }

      if (msg.IsResponse)
      {
         TaskCompletionSource<McpMessage>? tcs;
         lock (_gate) _pending.TryGetValue(msg.Id!, out tcs);
         // An id we never issued (or already gave up on) is not an error worth
         // surfacing — a late response to a cancelled call looks exactly so.
         tcs?.TrySetResult(msg);
         return;
      }

      if (msg.IsNotification)
      {
         if (msg.Method == "notifications/tools/list_changed")
            ToolsListChanged?.Invoke();
         return;
      }

      if (msg.IsRequest)
      {
         // Sampling, elicitation, roots/list: all refused in v1. Answering is
         // mandatory — a server that blocks on the reply would hang otherwise.
         try
         {
            _ = _transport.SendAsync(
                   McpJsonRpc.MethodNotFound(msg.Id!, msg.Method ?? ""),
                   CancellationToken.None);
         }
         catch { }
      }
   }

   private void _OnClosed(string reason)
   {
      if (_disposed) return;
      State         = McpState.Failed;
      FailureReason = reason;
      _FailAllPending(reason);
   }

   private void _FailAllPending(string reason)
   {
      List<TaskCompletionSource<McpMessage>> waiting;
      lock (_gate)
      {
         waiting = new List<TaskCompletionSource<McpMessage>>(_pending.Values);
         _pending.Clear();
      }
      foreach (var tcs in waiting)
         tcs.TrySetException(new InvalidOperationException("MCP connection lost: " + reason));
   }

   // -- json helpers ---------------------------------------------------------

   private static string? _Str(JsonElement obj, string name) =>
      obj.ValueKind == JsonValueKind.Object &&
      obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
         ? v.GetString() : null;

   private static string? _Nested(JsonElement obj, string outer, string inner) =>
      obj.ValueKind == JsonValueKind.Object &&
      obj.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object
         ? _Str(o, inner) : null;

   private static bool _ToolsListChangedDeclared(JsonElement result) =>
      result.ValueKind == JsonValueKind.Object &&
      result.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object &&
      caps.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Object &&
      tools.TryGetProperty("listChanged", out var lc) && lc.ValueKind == JsonValueKind.True;

   // -- constants ------------------------------------------------------------

   /// <summary>
   /// Newest revision we know how to speak. Servers answer with the version
   /// they picked, which may be older (2024-11-05 and 2025-03-26 are both
   /// still in the wild); nothing here depends on a feature newer than the
   /// oldest of them, so an older answer is accepted as-is.
   /// </summary>
   public const string PROTOCOL_VERSION = "2025-06-18";

   // -- fields ---------------------------------------------------------------

   private readonly IMcpTransport _transport;
   private readonly object        _gate = new();

   private readonly Dictionary<string, TaskCompletionSource<McpMessage>> _pending =
      new(StringComparer.Ordinal);

   private volatile bool _disposed;
}
