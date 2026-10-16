// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Shared shell-execution core: run a command through the OS shell (resolved
/// by <see cref="Platform.WrapShell"/>) with cwd pinned to the project root.
/// Combined stdout+stderr (shell-side <c>2&gt;&amp;1</c>) is captured into a
/// single string so the model sees the same stream a human would in a terminal.
///
/// Caps:
///   - Capture is capped at 10 MB. That is a memory ceiling, NOT a context
///     budget: callers shrink the log for the model with <see cref="Elide"/>.
///     The stream is still drained to the end past the cap — a stalled reader
///     would deadlock the child on a full pipe buffer.
///   - Process tree is killed on timeout or external cancel; partial output
///     is still returned.
///
/// Used by the (dormant) <see cref="BashTool"/> and by the user-configured
/// project command tools (<c>build</c> / <c>test</c>).
/// </summary>
/// <summary>
/// Structured outcome of one shell run. <see cref="ExitCode"/> is null when
/// the process never produced an exit code (failed to start, timed out, or was
/// cancelled). <see cref="Output"/> is the combined stdout+stderr without any
/// status header — callers format that themselves.
/// </summary>
public readonly record struct ShellResult(
   int? ExitCode,
   bool TimedOut,
   bool Cancelled,
   bool Truncated,
   string Output)
{
   public bool Success => !TimedOut && !Cancelled && ExitCode == 0;
}

public static class ShellRunner
{
   public const int DEFAULT_TIMEOUT_SEC = 30;
   public const int MAX_TIMEOUT_SEC     = 600;

   // Memory ceiling for one run, not a context budget — a full build log is
   // worth keeping so the caller can pick which parts the model sees. 10 M
   // chars is ~20 MB in the StringBuilder, transient and freed on return.
   private const int MAX_OUTPUT_CHARS = 10_000_000;

   public static async Task<ShellResult> RunAsync(string command, ToolContext ctx, int timeoutSec)
   {
      if (string.IsNullOrWhiteSpace(command))
         return new ShellResult(null, false, false, false, "'command' is required");

      timeoutSec = Math.Clamp(timeoutSec, 1, MAX_TIMEOUT_SEC);

      if (!Directory.Exists(ctx.ProjectRoot))
         return new ShellResult(null, false, false, false,
                                "project root does not exist: " + ctx.ProjectRoot);

      // The wrapper folds stderr into stdout shell-side, so we only need one
      // reader. A duplicate 2>&1 (if the command already had one) is harmless.
      var shell = Platform.WrapShell(command);
      var psi = new ProcessStartInfo
      {
         WorkingDirectory       = ctx.ProjectRoot,
         RedirectStandardOutput = true,
         RedirectStandardError  = false,
         UseShellExecute        = false,
         CreateNoWindow         = true,
         StandardOutputEncoding = Encoding.UTF8,
      };
      shell.ApplyTo(psi);

      using var proc = new Process { StartInfo = psi };
      try { proc.Start(); }
      catch (Exception ex)
      {
         return new ShellResult(null, false, false, false,
                                "failed to start " + shell.Exe + ": " + ex.Message);
      }

      // Linked CTS: external cancel (UI) OR our timeout, whichever fires first.
      using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
      using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancel, timeoutCts.Token);

      var outputBuf = new StringBuilder();
      bool truncated = false;

      // Read stdout (which already includes stderr) until the process closes
      // it. Past the cap we keep reading and DISCARD: stopping the reader would
      // fill the OS pipe buffer (4 KB on Windows) and block the child forever
      // inside its next write, which looks exactly like a hung, idle build that
      // only ever ends at our timeout. Draining is what a terminal does.
      var readTask = Task.Run(async () =>
      {
         var buf = new char[4096];
         int total = 0;
         while (!linkedCts.IsCancellationRequested)
         {
            int n = await proc.StandardOutput.ReadAsync(buf.AsMemory(0, buf.Length), linkedCts.Token)
                                             .ConfigureAwait(false);
            if (n == 0) break;
            int copy = Math.Min(n, MAX_OUTPUT_CHARS - total);
            if (copy > 0)
            {
               outputBuf.Append(buf, 0, copy);
               total += copy;
            }
            if (copy < n) truncated = true;
         }
      }, linkedCts.Token);

      bool exitedCleanly;
      try
      {
         await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
         exitedCleanly = true;
      }
      catch (OperationCanceledException)
      {
         exitedCleanly = false;
         try { proc.Kill(entireProcessTree: true); } catch { }
      }

      // Give the reader a brief grace period to drain any final output.
      try { await Task.WhenAny(readTask, Task.Delay(250)).ConfigureAwait(false); } catch { }

      bool timedOut  = !exitedCleanly && timeoutCts.IsCancellationRequested;
      bool cancelled = !exitedCleanly && !timedOut;
      int? exitCode  = exitedCleanly ? proc.ExitCode : (int?)null;

      return new ShellResult(exitCode, timedOut, cancelled, truncated, outputBuf.ToString());
   }

   /// <summary>
   /// Shrink a captured log to a context budget by keeping its first
   /// <paramref name="headLines"/> and last <paramref name="tailLines"/> lines
   /// and eliding the middle. Keeping BOTH ends serves compilers whose root
   /// cause is at the top (C++) and tools that summarize at the bottom
   /// (dotnet/MSBuild, test runners).
   ///
   /// The char budgets are the real guard: line counting alone is defeated by
   /// output that never emits '\n' — progress bars, minified blobs — where a
   /// single "line" can be the whole log. Each end is clamped independently,
   /// and the tail keeps its END so the final summary survives.
   /// </summary>
   internal static string Elide(string text, int headLines, int tailLines, int headChars, int tailChars)
   {
      if (string.IsNullOrEmpty(text)) return text;

      // Bare '\r' is a line break for our purposes: progress-bar output rewrites
      // one terminal line with it, and treating that as a single giant line
      // would spend the whole head budget on redraws of the same text.
      var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

      bool fits = lines.Length <= headLines + tailLines + 1
               && text.Length <= headChars + tailChars;
      if (fits) return text;

      int head = Math.Min(headLines, lines.Length);
      int tail = Math.Min(tailLines, lines.Length - head);

      var headText = _Join(lines, 0, head);
      var tailText = _Join(lines, lines.Length - tail, tail);

      // Clamp from the outside in: the head loses its end, the tail its start.
      if (headText.Length > headChars) headText = headText.Substring(0, headChars);
      if (tailText.Length > tailChars) tailText = tailText.Substring(tailText.Length - tailChars);

      int omitted = lines.Length - head - tail;
      var note = omitted > 0
         ? "[… " + omitted + " lines omitted …]"
         : "[… middle of the output omitted …]";

      var sb = new StringBuilder(headText.Length + tailText.Length + note.Length + 4);
      sb.Append(headText).Append('\n').Append(note).Append('\n').Append(tailText);
      return sb.ToString();
   }

   private static string _Join(string[] lines, int start, int count)
   {
      var sb = new StringBuilder();
      for (int i = 0; i < count; i++)
      {
         if (i > 0) sb.Append('\n');
         sb.Append(lines[start + i]);
      }
      return sb.ToString();
   }
}
