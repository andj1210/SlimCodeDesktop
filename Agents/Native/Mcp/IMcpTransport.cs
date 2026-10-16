// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>
/// The wire under an <see cref="McpClient"/>: it moves whole JSON-RPC messages
/// in both directions and knows nothing about what they mean.
///
/// Deliberately one-way-ish rather than request/response: stdio genuinely is a
/// full-duplex stream where a response, a notification and a server request can
/// arrive in any order, so correlation has to live one layer up. The HTTP
/// transport fakes the same shape — it posts a message and republishes whatever
/// the response body contained through <see cref="MessageReceived"/> — which is
/// what lets <see cref="McpClient"/> be transport-agnostic.
/// </summary>
public interface IMcpTransport : IDisposable
{
   /// <summary>
   /// Bring the wire up (spawn the child process / prepare the HTTP client).
   /// Throws on failure; the caller turns that into a connect error.
   /// </summary>
   Task StartAsync(CancellationToken cancel);

   /// <summary>Send one complete JSON-RPC message.</summary>
   Task SendAsync(string json, CancellationToken cancel);

   /// <summary>Fires for each inbound JSON-RPC message, on a background thread.</summary>
   event Action<string>? MessageReceived;

   /// <summary>Diagnostics (a stdio server's stderr, HTTP status oddities).</summary>
   event Action<string>? LogLine;

   /// <summary>Fires once when the wire dies, carrying a human-readable reason.</summary>
   event Action<string>? Closed;

   /// <summary>Short description for status tooltips, e.g. "stdio: npx …".</summary>
   string Describe { get; }
}
