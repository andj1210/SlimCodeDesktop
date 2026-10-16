// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// Minimal Server-Sent Events line reader. Yields each <c>data:</c> payload
/// as it arrives, stripping the prefix and the optional leading space.
/// Comment lines (starting with ':') and other SSE fields are ignored —
/// chat-completions endpoints only use <c>data:</c>.
///
/// The terminator <c>[DONE]</c> (OpenAI convention) is yielded as-is so the
/// caller can detect end-of-stream without coupling to the transport layer.
/// </summary>
public static class SseReader
{
   public static async IAsyncEnumerable<string> ReadAsync(
      Stream stream,
      [EnumeratorCancellation] CancellationToken cancel)
   {
      using var reader = new StreamReader(stream);
      while (!cancel.IsCancellationRequested)
      {
         var line = await reader.ReadLineAsync(cancel).ConfigureAwait(false);
         if (line == null)
            yield break;
         if (line.Length == 0)
            continue;
         if (line[0] == ':')
            continue;
         if (!line.StartsWith("data:"))
            continue;

         var payload = line.Substring(5);
         if (payload.Length > 0 && payload[0] == ' ')
            payload = payload.Substring(1);

         yield return payload;
      }
   }
}
