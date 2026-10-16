// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text.Json;
using System.Threading;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>
/// One parsed JSON-RPC 2.0 message. MCP multiplexes four shapes over a single
/// channel and they are told apart only by which fields are present:
///   - response:     has "id" plus "result" or "error"
///   - request:      has "id" plus "method"   (server -&gt; client)
///   - notification: has "method", no "id"
/// Anything else is malformed and is dropped by the client's reader.
///
/// <see cref="Id"/> is normalized to a string because the spec allows both
/// numbers and strings and we only ever compare it for equality against ids
/// we minted ourselves.
/// </summary>
public sealed class McpMessage
{
   private McpMessage(JsonDocument doc)
   {
      _doc = doc;
      var root = doc.RootElement;

      if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
         Id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();

      if (root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String)
         Method = mEl.GetString();

      if (root.TryGetProperty("result", out var rEl))
      {
         Result    = rEl;
         HasResult = true;
      }

      if (root.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.Object)
      {
         HasError = true;
         var msg  = eEl.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String
                    ? mm.GetString() : null;
         var code = eEl.TryGetProperty("code", out var cc) && cc.ValueKind == JsonValueKind.Number
                    ? cc.GetInt32().ToString()
                    : null;
         ErrorMessage = string.IsNullOrEmpty(msg)
                        ? ("JSON-RPC error " + (code ?? "?"))
                        : (code != null ? msg + " (code " + code + ")" : msg!);
      }
   }

   public string? Id { get; }
   public string? Method { get; }

   /// <summary>The <c>result</c> object of a response. Only valid when <see cref="HasResult"/>.</summary>
   public JsonElement Result { get; }
   public bool HasResult { get; }

   public bool HasError { get; }
   public string? ErrorMessage { get; }

   /// <summary>The <c>params</c> object, for a server-initiated request or notification.</summary>
   public JsonElement Params =>
      _doc.RootElement.TryGetProperty("params", out var p) ? p : default;

   public bool IsResponse     => Id != null && (HasResult || HasError);
   public bool IsRequest      => Id != null && Method != null;
   public bool IsNotification => Id == null && Method != null;

   /// <summary>Parse one message. Returns null for anything that isn't valid JSON.</summary>
   public static McpMessage? TryParse(string json)
   {
      try
      {
         var doc = JsonDocument.Parse(json);
         if (doc.RootElement.ValueKind != JsonValueKind.Object)
         {
            doc.Dispose();
            return null;
         }
         return new McpMessage(doc);
      }
      catch
      {
         return null;
      }
   }

   // The document backs every JsonElement handed out above, so it is kept
   // alive for the message's lifetime rather than disposed after parsing.
   private readonly JsonDocument _doc;
}

/// <summary>
/// Builders for the outbound half of JSON-RPC 2.0, plus the shared request-id
/// counter. Everything is emitted as a compact single-line string: the stdio
/// framing is newline-delimited, so an embedded newline would split a message.
/// </summary>
public static class McpJsonRpc
{
   /// <summary>Next request id, unique across every client in the process.</summary>
   public static string NextId() => Interlocked.Increment(ref s_nextId).ToString();

   /// <summary>
   /// <c>{"jsonrpc":"2.0","id":..,"method":..,"params":..}</c>.
   /// <paramref name="paramsJson"/> is inserted verbatim and must already be a
   /// JSON object (or null to omit the member).
   /// </summary>
   public static string Request(string id, string method, string? paramsJson)
   {
      var sb = new System.Text.StringBuilder(128);
      sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
      WriteString(sb, id);
      sb.Append(",\"method\":");
      WriteString(sb, method);
      if (!string.IsNullOrEmpty(paramsJson))
      {
         sb.Append(",\"params\":");
         sb.Append(paramsJson);
      }
      sb.Append('}');
      return sb.ToString();
   }

   /// <summary>A notification: same as a request, minus the id.</summary>
   public static string Notification(string method, string? paramsJson)
   {
      var sb = new System.Text.StringBuilder(96);
      sb.Append("{\"jsonrpc\":\"2.0\",\"method\":");
      WriteString(sb, method);
      if (!string.IsNullOrEmpty(paramsJson))
      {
         sb.Append(",\"params\":");
         sb.Append(paramsJson);
      }
      sb.Append('}');
      return sb.ToString();
   }

   /// <summary>
   /// An error response to a server-initiated request. v1 answers every one of
   /// them (sampling, elicitation, roots/list) with method-not-found: we
   /// advertise no such capability, so a conforming server never asks, and a
   /// non-conforming one gets a clean refusal instead of a hang.
   /// </summary>
   public static string MethodNotFound(string id, string method)
   {
      var sb = new System.Text.StringBuilder(128);
      sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
      WriteString(sb, id);
      sb.Append(",\"error\":{\"code\":-32601,\"message\":");
      WriteString(sb, "method not supported by this client: " + method);
      sb.Append("}}");
      return sb.ToString();
   }

   /// <summary>JSON-escape <paramref name="value"/> and append it with quotes.</summary>
   public static void WriteString(System.Text.StringBuilder sb, string value)
   {
      sb.Append(JsonSerializer.Serialize(value));
   }

   /// <summary>JSON-escaped string literal, quotes included.</summary>
   public static string Quote(string value) => JsonSerializer.Serialize(value);

   // -- fields ---------------------------------------------------------------

   private static int s_nextId;
}
