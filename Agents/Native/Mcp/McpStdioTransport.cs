// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>
/// Spawns an MCP server as a child process and speaks newline-delimited JSON
/// over its stdin/stdout. stderr is diagnostics only — servers use it for
/// startup chatter, and a line there is never a protocol message.
///
/// Cold starts are the defining property of this transport: <c>npx -y …</c>
/// may download a package before it says anything at all, which is why the
/// session connects lazily and shows a phase for it rather than pretending
/// the wait doesn't exist.
/// </summary>
public sealed class McpStdioTransport : IMcpTransport
{
   public McpStdioTransport(string command, IReadOnlyList<string> args,
                            IReadOnlyDictionary<string, string>? env, string? cwd)
   {
      _command = command ?? "";
      _args    = args ?? Array.Empty<string>();
      _env     = env;
      _cwd     = cwd;
   }

   public event Action<string>? MessageReceived;
   public event Action<string>? LogLine;
   public event Action<string>? Closed;

   public string Describe
   {
      get
      {
         var sb = new StringBuilder("stdio: ").Append(_command);
         foreach (var a in _args) sb.Append(' ').Append(a);
         return sb.ToString();
      }
   }

   public Task StartAsync(CancellationToken cancel)
   {
      var psi = new ProcessStartInfo
      {
         UseShellExecute        = false,
         RedirectStandardInput  = true,
         RedirectStandardOutput = true,
         RedirectStandardError  = true,
         CreateNoWindow         = true,
         // UTF-8 both ways: the protocol is JSON, and a server that emits
         // non-ASCII in a tool description would otherwise arrive mangled.
         StandardOutputEncoding = new UTF8Encoding(false),
         StandardErrorEncoding  = new UTF8Encoding(false),
      };

      // Argv launch rather than a shell: an intermediate shell would own the
      // pipes, so the child's death would not close our end of them and the
      // reader would hang instead of reporting the failure.
      Platform.ResolveProcess(_command, _args).ApplyTo(psi);

      if (!string.IsNullOrWhiteSpace(_cwd) && Directory.Exists(_cwd))
         psi.WorkingDirectory = _cwd;

      if (_env != null)
      {
         foreach (var kv in _env)
         {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            psi.Environment[kv.Key] = kv.Value ?? "";
         }
      }

      var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
      if (!proc.Start())
         throw new InvalidOperationException("could not start '" + _command + "'");

      _proc = proc;
      // Writer is serialized through _sendLock; the Process's own StreamWriter
      // is not thread-safe and the client can send from several tasks.
      _stdin = proc.StandardInput;

      // Fire-and-forget readers: both end when their stream closes, which is
      // exactly when the child dies, so there is nothing to await or join.
      _ = Task.Run(() => _ReadLoop(proc.StandardOutput, isStdErr: false));
      _ = Task.Run(() => _ReadLoop(proc.StandardError,  isStdErr: true));

      proc.Exited += (_, _) =>
      {
         int code = -1;
         try { code = proc.ExitCode; } catch { }
         _RaiseClosed("server process exited (code " + code + ")");
      };

      return Task.CompletedTask;
   }

   public async Task SendAsync(string json, CancellationToken cancel)
   {
      var w = _stdin;
      if (w == null || _disposed)
         throw new InvalidOperationException("stdio transport is not running");

      await _sendLock.WaitAsync(cancel).ConfigureAwait(false);
      try
      {
         // '\n' explicitly, not WriteLine: on Windows that would emit CRLF and
         // a server splitting strictly on '\n' would see a trailing '\r' inside
         // the message.
         await w.WriteAsync(json.AsMemory(), cancel).ConfigureAwait(false);
         await w.WriteAsync("\n".AsMemory(), cancel).ConfigureAwait(false);
         await w.FlushAsync(cancel).ConfigureAwait(false);
      }
      finally
      {
         _sendLock.Release();
      }
   }

   public void Dispose()
   {
      if (_disposed) return;
      _disposed = true;

      var proc = _proc;
      _proc = null;

      try { _stdin?.Close(); } catch { }
      _stdin = null;

      if (proc != null)
      {
         // Closing stdin is the polite shutdown every MCP stdio server honors;
         // give it a moment before killing the tree.
         try { if (!proc.WaitForExit(500)) proc.Kill(entireProcessTree: true); } catch { }
         try { proc.Dispose(); } catch { }
      }

      try { _sendLock.Dispose(); } catch { }
   }

   // -- private --------------------------------------------------------------

   private void _ReadLoop(StreamReader reader, bool isStdErr)
   {
      try
      {
         string? line;
         while ((line = reader.ReadLine()) != null)
         {
            if (line.Length == 0) continue;
            if (isStdErr)
               LogLine?.Invoke(line);
            else
               MessageReceived?.Invoke(line);
         }
      }
      catch (Exception ex)
      {
         if (!_disposed && !isStdErr)
            LogLine?.Invoke("read error: " + ex.Message);
      }

      if (!isStdErr)
         _RaiseClosed("server closed its output stream");
   }

   // Both the reader loop and the Exited handler can get here; the flag keeps
   // the client from seeing two closes for one death.
   private void _RaiseClosed(string reason)
   {
      if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
      if (_disposed) return;
      Closed?.Invoke(reason);
   }

   // -- fields ---------------------------------------------------------------

   private readonly string                             _command;
   private readonly IReadOnlyList<string>              _args;
   private readonly IReadOnlyDictionary<string,string>? _env;
   private readonly string?                            _cwd;

   private readonly SemaphoreSlim _sendLock = new(1, 1);

   private Process?      _proc;
   private StreamWriter? _stdin;

   private volatile bool _disposed;
   private int           _closedRaised;
}
