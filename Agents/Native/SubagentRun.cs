// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Tools;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// One in-flight subagent: a nested <see cref="AgentLoop"/> driven
/// synchronously from inside the parent's subagent tool call. The
/// subagent's transcript never enters the parent's history (and thus never
/// disturbs the parent's prompt cache) — only the final report comes back,
/// as the tool result.
///
/// A <see cref="SubagentKind.Write"/> run may mutate the project, so the
/// result always carries a change manifest built from the mutating calls
/// that actually succeeded — not from what the subagent claims it did. A
/// run that fails mid-task is exactly the one least likely to report
/// accurately, and the parent needs to know its picture of those files is
/// stale either way.
///
/// Built per call by the host view (which owns provider settings and the
/// optional viewer tab); <see cref="RunAsync"/> is awaited on the parent
/// loop's background context. Events fire on that context — UI consumers
/// must marshal.
/// </summary>
public sealed class SubagentRun
{
   /// <summary>Hard cap on the report returned to the parent model.</summary>
   private const int MAX_REPORT_CHARS = 20_000;

   public SubagentRun(AgentLoop loop, string task, SubagentKind kind, string? label)
   {
      Loop     = loop ?? throw new ArgumentNullException(nameof(loop));
      TaskText = task ?? "";
      Kind     = kind;
      Label    = string.IsNullOrWhiteSpace(label) ? "subagent" : label!.Trim();

      Loop.ToolCallReady += call =>
      {
         ToolCallCount++;
         if (kind == SubagentKind.Write && !string.IsNullOrEmpty(call.Id))
            _pendingCalls[call.Id] = call;
         Progress?.Invoke();
      };
      Loop.ToolCallCompleted += (callId, result) =>
      {
         if (kind != SubagentKind.Write || string.IsNullOrEmpty(callId)) return;
         if (!_pendingCalls.TryGetValue(callId, out var call)) return;
         _pendingCalls.Remove(callId);
         if (!result.IsError)
            _RecordMutation(call);
      };
      Loop.UsageReported += usage =>
      {
         TotalOutputTokens += usage.CompletionTokens;
         LastPromptTokens   = usage.PromptTokens;
         UsageReported?.Invoke(usage);
         Progress?.Invoke();
      };
      Loop.Error += msg => _lastError = msg;
   }

   /// <summary>The nested loop — exposed so a viewer can render its transcript live.</summary>
   public AgentLoop Loop { get; }

   /// <summary>The task prompt the parent model delegated. (Named TaskText to
   /// keep the System.Threading.Tasks.Task type unambiguous in this file.)</summary>
   public string TaskText { get; }

   /// <summary>Capability level this run was started at.</summary>
   public SubagentKind Kind { get; }

   /// <summary>Short label from the delegating call — UI and permission prompts.</summary>
   public string Label { get; }

   public int  ToolCallCount     { get; private set; }
   public int  LastPromptTokens  { get; private set; }
   public long TotalOutputTokens { get; private set; }
   public bool IsCompleted       { get; private set; }

   /// <summary>Mutations this run has completed, in order. Always empty for
   /// a <see cref="SubagentKind.ReadOnly"/> run.</summary>
   public IReadOnlyList<string> Changes => _changes;

   /// <summary>Fires whenever the progress counters advance (loop thread).</summary>
   public event Action? Progress;

   /// <summary>Per-iteration token usage passthrough, for host cost accounting.</summary>
   public event Action<LlmUsage>? UsageReported;

   /// <summary>Fires once when <see cref="RunAsync"/> finishes (loop thread).</summary>
   public event Action? Completed;

   /// <summary>
   /// Stop just this subagent, leaving the parent turn alive. Used by the
   /// permission overlay's Abort button while a subagent prompt is up: the
   /// parent receives the abort as an ordinary failed tool result and can
   /// recover, instead of losing the whole conversation turn.
   /// </summary>
   public void Cancel()
   {
      try { _ownCts?.Cancel(); } catch { }
   }

   /// <summary>
   /// Drive the nested loop to completion and distill the tool result: the
   /// subagent's final assistant message on success, an error (with partial
   /// report when one exists) on failure / abort / iteration cap. A write
   /// run appends its change manifest on every path.
   /// </summary>
   public async Task<ToolResult> RunAsync(CancellationToken cancel)
   {
      // Own token, linked to the parent's: the host can stop this run alone
      // without touching the parent, while a parent cancel still reaches
      // every nested tool call.
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
      _ownCts = linked;

      bool abortedByUser = false;
      try
      {
         // No images: a subagent is started from a text task description.
         await Loop.RunAsync(TaskText, null, linked.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
      {
         // Ours, not the parent's -> recoverable for the parent. A parent
         // cancel is left to propagate and end the parent turn as before.
         abortedByUser = true;
      }
      finally
      {
         _ownCts     = null;
         IsCompleted = true;
         Completed?.Invoke();
      }

      // AgentLoop reports cancellation through Error and returns normally
      // rather than rethrowing, so the token — not the catch above — is what
      // actually distinguishes "the user stopped this subagent" from "it
      // failed". The catch stays as a guard in case that ever changes.
      if (linked.IsCancellationRequested && !cancel.IsCancellationRequested)
         abortedByUser = true;

      var report = _FinalAssistantText();

      if (abortedByUser)
         return ToolResult.Error(_WithChanges(
            "subagent was aborted by the user before it finished" + _PartialSuffix(report)));

      if (_lastError != null)
         return ToolResult.Error(_WithChanges(
            "subagent failed: " + _lastError + _PartialSuffix(report)));

      if (string.IsNullOrWhiteSpace(report))
         return ToolResult.Error(_WithChanges("subagent finished without producing a report"));

      return ToolResult.Ok(_WithChanges(_Truncate(report!)));
   }

   // -- private methods --------------------------------------------------------

   private static string _PartialSuffix(string? report) =>
      string.IsNullOrWhiteSpace(report)
         ? ""
         : "\n\nPartial report before it stopped:\n\n" + _Truncate(report!);

   // Appended AFTER truncation so a chatty subagent can't push the file
   // list out of the result.
   private string _WithChanges(string body)
   {
      if (Kind != SubagentKind.Write) return body;

      var sb = new StringBuilder(body);
      sb.Append("\n\n---\nFiles the subagent changed");
      if (_changes.Count == 0)
      {
         sb.Append(": none.");
      }
      else
      {
         sb.Append(" (");
         sb.Append(_changes.Count);
         sb.Append("). Your view of these is now stale — re-read before editing them:\n");
         foreach (var c in _changes)
         {
            sb.Append("  ");
            sb.Append(c);
            sb.Append('\n');
         }
      }
      return sb.ToString();
   }

   // Derive "what changed" from the mutating calls that actually succeeded.
   // Path argument names track the write tools' schemas (path; source /
   // destination for move_file / copy_file).
   private void _RecordMutation(ToolCall call)
   {
      string verb = call.Name switch
      {
         "write_file"  => "wrote",
         "edit_file"   => "edited",
         "move_file"   => "moved",
         "copy_file"   => "copied",
         "delete_file" => "deleted",
         _             => "",
      };
      if (verb.Length == 0) return;

      try
      {
         using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
         var root = doc.RootElement;

         if (call.Name == "move_file" || call.Name == "copy_file")
         {
            string src = _Str(root, "source");
            string dst = _Str(root, "destination");
            if (src.Length > 0 || dst.Length > 0)
               _changes.Add(verb + " " + src + " -> " + dst);
            return;
         }

         string path = _Str(root, "path");
         if (path.Length > 0)
            _changes.Add(verb + " " + path);
      }
      catch (JsonException)
      {
         // Malformed args can't have produced a successful call; ignore.
      }
   }

   private static string _Str(JsonElement obj, string name) =>
      obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
         ? v.GetString() ?? ""
         : "";

   // Last assistant message with actual text. The loop appends messages from
   // its own thread, but by the time RunAsync returns the loop is idle, so
   // reading History here is safe.
   private string? _FinalAssistantText()
   {
      var history = Loop.History;
      for (int i = history.Count - 1; i >= 0; i--)
      {
         var m = history[i];
         if (m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))
            return m.Content;
      }
      return null;
   }

   private static string _Truncate(string s)
   {
      if (s.Length <= MAX_REPORT_CHARS) return s;
      return s.Substring(0, MAX_REPORT_CHARS)
             + "\n\n[report truncated at " + MAX_REPORT_CHARS + " characters]";
   }

   // -- fields -------------------------------------------------------------------

   private string? _lastError;

   // Written and read on the nested loop's thread only (ToolCallReady and
   // ToolCallCompleted both fire there, in order).
   private readonly Dictionary<string, ToolCall> _pendingCalls = new();
   private readonly List<string>                 _changes      = new();

   private CancellationTokenSource? _ownCts;
}
