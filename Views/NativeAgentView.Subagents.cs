// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Views.Native;
using SkiaSharp;

// `using Avalonia;` brings the Avalonia.Platform namespace into scope, which
// makes the bare name `Platform` ambiguous with our own static class.
using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views;

/// <summary>
/// Subagent runs: the factory handed to the subagent tools and the events
/// the host uses to open viewer tabs.
/// </summary>
public partial class NativeAgentView
{
   /// <summary>
   /// Fires (parent loop's background thread) when a subagent call has
   /// built its nested run and is about to drive it. The host opens the
   /// optional grouped viewer tab from this.
   /// </summary>
   public event Action<SubagentRun>? SubagentStarted;

   /// <summary>
   /// Fires (UI thread) when the user clicks a subagent tool card's "view"
   /// button. The host focuses the run's viewer tab, reopening it if hidden.
   /// </summary>
   public event Action<SubagentRun>? SubagentViewerRequested;

   /// <summary>
   /// Fires (UI thread) when the user clicks "view" on a *replayed*
   /// subagent card — no live run exists, but its stored transcript
   /// does. Carries the task text, the sub-transcript file path and whether
   /// it was a write subagent; the host opens a replay viewer tab.
   /// </summary>
   public event Action<string, string, bool>? SubagentReplayRequested;

   /// <summary>
   /// Factory handed to <see cref="SubagentTool"/>: build the nested loop
   /// from this session's provider settings, at the capability level the
   /// parent model asked for. Runs on the parent loop's background thread;
   /// a failure reason comes back as a recoverable tool error.
   /// </summary>
   private SubagentStart _StartSubagentRun(SubagentKind kind, string task, string? label)
   {
      if (_agent == null || _projectPath == null)
         return SubagentStart.Fail("no project session is active");
      var s    = _agent.Settings;
      var prov = s.ResolveProvider(_selProvider);
      if (prov == null || string.IsNullOrWhiteSpace(prov.BaseUrl))
         return SubagentStart.Fail("no provider configured");

      // Plan mode refuses write delegation outright rather than raising a
      // prompt per nested write: the user asked to review changes one by
      // one, and a subagent's writes are exactly the ones they haven't seen
      // coming. The read-only variant is unaffected.
      if (kind == SubagentKind.Write && _gate != null && _gate.Mode == AgentMode.Plan)
         return SubagentStart.Fail(
            "the session is in PLAN mode, which does not allow delegating write work. " +
            "Use start_read_subagent for read-only research, or ask the user to switch to " +
            "IMPLEMENT mode first.");

      // A cheap SubagentModel is a good trade for research and a bad one for
      // edits, so it applies to read-only runs only; a write subagent uses
      // the session's own model.
      var model = kind == SubagentKind.Write || string.IsNullOrWhiteSpace(s.SubagentModel)
                  ? _selModel
                  : s.SubagentModel;
      if (string.IsNullOrWhiteSpace(model))
         return SubagentStart.Fail("no model configured");

      var client = new OpenAiCompatClient(prov.BaseUrl, NullIfBlank(prov.ApiKey));
      var tools  = kind == SubagentKind.Write
                   ? ToolRegistry.CreateFullDefaults()
                   : ToolRegistry.CreateExplorationDefaults();
      if (kind == SubagentKind.Write)
         _RegisterProjectCommands(tools);
      var ctx       = new ToolContext(_projectPath!, CancellationToken.None);
      var subPrompt = NativeAgentSettings.BuildSubagentSystemPrompt(_projectPath!, kind);

      // The gate is shared with the parent session, deliberately: a grant
      // the user gives is about this project and this session, not about
      // which loop happened to ask. Read tools normally auto-allow; writes
      // prompt in this tab's overlay, tagged with the subagent's label.
      var loop = new AgentLoop(client, tools, ctx, model, subPrompt, gate: _gate);
      loop.Temperature   = s.OverrideTemperature ? s.Temperature : (float?)null;
      loop.MaxTokens     = s.MaxTokens > 0 ? s.MaxTokens : (int?)null;
      // Edit -> build -> fix cycles need more headroom than a read sweep.
      // Both are backstops against a runaway nested loop, nothing more - a
      // sweep over a large repo burns one iteration per grep/read pair and
      // must not die halfway with its report unwritten.
      loop.MaxIterations = kind == SubagentKind.Write ? 400 : 250;
      loop.SubagentLabel = string.IsNullOrWhiteSpace(label) ? "subagent" : label!.Trim();
      // Own cache partition: the subagent's growing transcript must not
      // share (or scatter) the parent conversation's prompt-cache prefix.
      loop.CacheKey = Guid.NewGuid().ToString("N");

      // Resolved against the SUBAGENT's model, which may differ from the
      // session's (settings.SubagentModel).
      _ConfigureReasoning(loop, prov, model);

      var run = new SubagentRun(loop, task, kind, label);

      // Abort on a subagent's permission prompt stops the subagent only, so
      // the overlay needs to know which run is live. Synchronous nesting
      // means there is at most one at a time. Published from THIS (loop)
      // thread rather than via UiPost: the run can raise a prompt as soon
      // as it starts, and the field must already be set by then.
      _activeSubagent = run;
      run.Completed += () => UiPost(() =>
      {
         if (ReferenceEquals(_activeSubagent, run)) _activeSubagent = null;
      });

      // Live progress on the parent transcript's tool card. The card id was
      // captured on the loop thread in WireEvents just before execution
      // started, so it's stable by the time we get here.
      var cardId = _lastSubagentCallId;

      // Persist the subagent's own transcript alongside the parent session:
      // <sessionId>.sub.<callId>.jsonl. Deterministic naming is the resume
      // correlation — a replayed subagent card re-derives this path
      // from (session id, call id) and offers a stored-transcript viewer.
      // The system prompt is written explicitly (the loop ctor adds it to
      // history without firing MessageAdded, same as the parent path).
      if (_sessionId != null && cardId != null)
      {
         var subPath = TranscriptStore.SubFileFor(_projectPath!, _sessionId!, cardId);
         if (subPath != null)
         {
            var subStore = new TranscriptStore(subPath);
            subStore.AppendMeta(_sessionId + TranscriptStore.SUB_FILE_MARK + cardId,
                                model, prov.Name, subPrompt, _projectPath!,
                                kind: TranscriptStore.SUBAGENT_KIND);
            subStore.AppendMessage(new ChatMessage { Role = ChatRole.System, Content = subPrompt });
            loop.MessageAdded += msg => subStore.AppendMessage(msg);
         }
      }
      run.Progress += () => UiPost(() =>
      {
         if (cardId == null || !_toolCards.TryGetValue(cardId, out var card)) return;
         var text = run.ToolCallCount + " tool calls · out=" + run.TotalOutputTokens + " tok";
         // Surface writes on the parent card: the viewer tab is optional and
         // hideable, so it must not be the only place changes are visible.
         if (run.Kind == SubagentKind.Write && run.Changes.Count > 0)
            text += " · " + run.Changes.Count + " file(s) changed";
         card.SetProgress(text);
      });

      // "view" button on the card: jump to (or reopen) this run's viewer tab.
      UiPost(() =>
      {
         if (cardId != null && _toolCards.TryGetValue(cardId, out var card))
            card.AddHeaderAction("view", "Open the subagent viewer tab",
                                 () => SubagentViewerRequested?.Invoke(run));
      });

      // Subagent tokens count toward the session's cost totals but NOT the
      // context gauge fill — they never occupy the parent's context window.
      run.UsageReported += usage => UiPost(() =>
      {
         _sumInput      += usage.PromptTokens;
         _sumCached     += usage.CachedReadTokens;
         _sumOutput     += usage.CompletionTokens;
         _sumReasoning  += usage.ReasoningTokens ?? 0;
         _sumCacheWrite += usage.CacheWriteTokens;
         Gauge.SetSessionTotals(_sumInput, _sumCached, _sumOutput, _sumReasoning, _sumCacheWrite);
      });

      SubagentStarted?.Invoke(run);
      return SubagentStart.Ok(run);
   }

   // True for either subagent tool. Both produce a nested run with a stored
   // sub-transcript, so card wiring and replay treat them identically —
   // only the capability level of the run itself differs. "start_subagent" is
   // the read tool's former name, kept here so sessions recorded before the
   // rename still replay with their viewer wiring intact.
   private static bool _IsSubagentTool(string? name) =>
      string.Equals(name, "start_read_subagent",  StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name, "start_write_subagent", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name, "start_subagent",       StringComparison.OrdinalIgnoreCase);

   // Extract the 'task' string from a subagent call's arguments, for the
   // replay viewer's header. Falls back to a placeholder on parse issues.
   private static string _ParseSubagentTask(string argsJson)
   {
      try
      {
         using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
         if (doc.RootElement.TryGetProperty("task", out var t) &&
             t.ValueKind == System.Text.Json.JsonValueKind.String)
            return t.GetString() ?? "(task unavailable)";
      }
      catch { }
      return "(task unavailable)";
   }

   // Call id of the most recent subagent call. Written and read on the
   // parent loop's thread only (ToolCallReady handler → factory during
   // ExecuteOne); links the run's progress updates to its tool card.
   private string?                                _lastSubagentCallId;

   // The subagent currently being driven inside a parent tool call, if any.
   // Synchronous nesting means there is at most one; it exists so an Abort
   // on its permission prompt can stop it without killing the parent turn.
   private SubagentRun?                           _activeSubagent;
}
