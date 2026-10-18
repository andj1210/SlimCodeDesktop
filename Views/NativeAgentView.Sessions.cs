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
/// Session persistence: starting a new session, auto-titling, and resuming
/// a stored transcript (visual replay + loop history).
/// </summary>
public partial class NativeAgentView
{
   // -- session persistence --------------------------------------------------

   /// <summary>
   /// On first user send of a fresh conversation, create a new JSONL file
   /// and write its meta header + the system prompt as the first message
   /// (the message is what gets replayed; meta carries discovery metadata).
   /// </summary>
   private void EnsureNewSession()
   {
      if (_transcriptStore != null || _agent == null || _projectPath == null) return;

      var sid  = TranscriptStore.NewSessionId();
      var path = TranscriptStore.FileFor(_projectPath, sid);
      if (path == null) return;

      var store = new TranscriptStore(path);
      var s = _agent.Settings;

      // Freeze the prompt for this session: base text + dynamic trailing
      // block (start time, project folder, skills catalog). Captured into
      // both meta and the system message so a future resume replays the
      // exact prompt the model originally saw.
      // For an MCP project the handles have already connected (BtnSend_Click
      // does that first), so the prompt can name the tools the session was
      // actually wired to. The skills list is passed regardless and simply
      // ignored for MCP projects, which register no use_skill tool.
      var skills = _DiscoverSkills();
      var effectivePrompt = NativeAgentSettings.BuildEffectiveSystemPrompt(
         s.SystemPrompt, _projectPath, DateTime.Now, _selectedMode, skills,
         _IsMcpProject() ? _mcpHandles : null);
      _sessionSystemPrompt = effectivePrompt;

      // Show the frozen system prompt as the first (toggleable) system bubble
      // so the "sys" toggle reveals exactly what the model was instructed with.
      // It's hidden by default like every system bubble, so its size is a
      // non-issue until the user opts in.
      if (!string.IsNullOrEmpty(effectivePrompt))
         AppendSystemBubble(effectivePrompt);

      // Queue the project's auto-load skills for injection on this first
      // turn (drained in BtnSend_Click once the loop exists). Only names
      // that actually resolve to a discovered skill are queued.
      var autoLoad = ProjectConfigStore.LoadOrCreate(_projectPath).Config.AutoLoadSkills;
      foreach (var name in autoLoad)
      {
         foreach (var sk in skills)
         {
            if (string.Equals(sk.Name, name, StringComparison.OrdinalIgnoreCase))
            {
               _pendingSkills.Add(sk.Name);
               break;
            }
         }
      }

      store.AppendMeta(sid, _selModel, _selProvider, effectivePrompt, _projectPath);
      // Record the starting mode explicitly so resume of this session
      // restores it correctly. Without this, resume defaults to Implement
      // for sessions that never flipped mode mid-flight.
      store.AppendModeChange(_selectedMode);
      // Same for a reasoning level picked before the first send.
      if (_selReasoning != ReasoningLevel.Default)
         store.AppendReasoningChange(_selReasoning);
      if (!string.IsNullOrEmpty(effectivePrompt))
      {
         store.AppendMessage(new ChatMessage
         {
            Role    = ChatRole.System,
            Content = effectivePrompt,
         });
      }

      _transcriptStore = store;
      _sessionId       = sid;
      _gate?.SetSessionId(sid);
      // Fresh session — no inherited session grants.
      _gate?.ClearSessionCache();
      // Gate also needs to know the starting mode from the get-go —
      // permission decisions on this session's first turn depend on it.
      if (_gate != null) _gate.Mode = _selectedMode;
      _appliedMode = _selectedMode;
      _RefreshModeUi();
   }

   private void SubscribePersistence(AgentLoop loop)
   {
      loop.MessageAdded += msg => _transcriptStore?.AppendMessage(msg);
   }

   /// <summary>
   /// Name the just-created session from its first prompt, if the hosting
   /// provider opted in. Fire-and-forget by design: the call is deliberately
   /// outside <see cref="_RunTurn"/> so it never touches the busy state, the
   /// send button, or the context gauge — its tokens are not session context
   /// and would corrupt the fill meter.
   ///
   /// Only ever runs on a session <see cref="EnsureNewSession"/> just minted,
   /// so there is no caption to overwrite. Forks inherit their parent's name
   /// instead (see <see cref="_ForkTitle"/>) — they mint a session per revert /
   /// regenerate click, and titling each one would spend a call per click to
   /// re-derive the same name.
   /// </summary>
   private void _StartAutoTitle(string firstPrompt, ProviderInfo? prov)
   {
      if (prov == null || !prov.AutoTitleSessions) return;
      if (string.IsNullOrWhiteSpace(firstPrompt)) return;      // image-only turn
      if (string.IsNullOrWhiteSpace(prov.BaseUrl) || string.IsNullOrWhiteSpace(_selModel)) return;

      var store = _transcriptStore;
      if (store == null || _sessionId == null) return;         // session file couldn't be created

      // The session's OWN model: a cheaper one would make llama-swap evict the
      // resident model and reload it around a call that saves cents.
      var mi     = prov.FindModel(_selModel);
      var titler = new SessionTitler(
         new OpenAiCompatClient(prov.BaseUrl, NullIfBlank(prov.ApiKey)),
         _selModel,
         mi?.Reasoning.OffKwargs);

      try { _titleCts?.Cancel(); } catch { }
      var cts   = new CancellationTokenSource();
      _titleCts = cts;

      _ = Task.Run(async () =>
      {
         string? title;
         try { title = await titler.ProposeAsync(firstPrompt, cts.Token).ConfigureAwait(false); }
         catch (OperationCanceledException) { return; }
         catch { return; }                                     // never surfaces; the quote-fallback stands
         if (title == null || cts.IsCancellationRequested) return;

         // Written through the captured store, so a session switch that raced
         // us lands the name on the session it was generated for.
         store.AppendTitle(title);

         UiPost(() =>
         {
            // Stale by the time we got here (tab closed, session switched):
            // the file is named correctly either way, but only the live
            // session's host has pickers worth re-rendering.
            if (!ReferenceEquals(_titleCts, cts)) return;
            SessionTitled?.Invoke();
         });
      });
   }

   /// <summary>
   /// Load a JSONL transcript from disk: rebuild the visual transcript,
   /// reconstruct <see cref="AgentLoop"/> history, and reopen the file for
   /// append so the next Send continues writing into the same session.
   /// </summary>
   private void ResumeSession(string sessionId)
   {
      if (_agent == null || _projectPath == null) return;

      var path = TranscriptStore.FileFor(_projectPath, sessionId);
      if (path == null || !File.Exists(path))
      {
         AppendError("Session file not found: " + sessionId);
         return;
      }

      var records = TranscriptStore.Read(path);
      if (records.Count == 0)
      {
         AppendError("Session file is empty: " + sessionId);
         return;
      }

      var history       = new List<ChatMessage>();
      var localCards    = new Dictionary<string, ToolCallCard>();
      string? metaModel    = null;
      string? metaPrompt   = null;
      string?   effectiveModel    = null;
      string?   effectiveProvider = null;
      string?   previousModel     = null;
      string?   previousProvider  = null;
      AgentMode effectiveMode     = AgentMode.Implement;
      // No record = never picked, so the model's own default applies (and a
      // level left over from this tab's previous session does not leak in).
      ReasoningLevel effectiveReasoning = ReasoningLevel.Default;

      // Last-writer-wins map of persisted session-scope grants, replayed
      // into the gate after the walk so "Allow session" survives resume.
      var sessionGrants = new Dictionary<string, PermissionDecision>(StringComparer.OrdinalIgnoreCase);

      // Tool specs aren't persisted (see _AppendToolSpecsBubble) — render the
      // live set once, right after the first system record (the base prompt)
      // to match the template's prompt order. Note this shows the tool set
      // the *resumed* session will send, which may differ from the original
      // run (e.g. build/test steps configured since).
      bool toolsBubbleShown = false;

      // Replay creates tool cards before BuildLoop runs, so the registry the
      // card previews resolve against must exist up front. BuildLoop replaces
      // it with the loop's own (equivalent) instance afterwards.
      _viewToolRegistry = _BuildToolRegistry(withTaskCallback: true);

      foreach (var r in records)
      {
         if (r.Type == "meta")
         {
            metaModel         = r.Model;
            metaPrompt        = r.SystemPrompt;
            effectiveModel    = r.Model;
            effectiveProvider = r.Provider;
            continue;
         }
         if (r.Type == "model")
         {
            // Mid-session swap marker. Replay the same visual notice the
            // live path produces and track the latest provider + model so we
            // resume talking to whichever one was active at the end.
            previousModel     = effectiveModel;
            previousProvider  = effectiveProvider;
            effectiveModel    = r.Model;
            effectiveProvider = r.Provider;
            AppendItem(new NoticeLine(
               _ModelSwapNote(previousProvider, previousModel, r.Model ?? "", r.Provider ?? "")));
            continue;
         }
         if (r.Type == "mode")
         {
            // Mode switch marker. Track the latest declared mode so we
            // resume in whichever mode the session ended on; replay the
            // visual notice for context. The system-message announce
            // (Type="msg", Role="system") was written separately at the
            // same time as the original flip and replays via the normal
            // msg path below.
            if (System.Enum.TryParse<AgentMode>(r.Mode, out var parsedMode))
            {
               var prevMode = effectiveMode;
               effectiveMode = parsedMode;
               AppendItem(new NoticeLine("mode switched: " + prevMode + " → " + parsedMode));
            }
            continue;
         }
         if (r.Type == "reasoning")
         {
            if (System.Enum.TryParse<ReasoningLevel>(r.Reasoning, out var parsedLevel))
               effectiveReasoning = parsedLevel;
            continue;
         }
         if (r.Type == "mcpTools")
         {
            // The MCP catalog this conversation was last told about. The last
            // such record wins; the first send after the resume diffs the live
            // catalog against it and announces any difference.
            _lastKnownMcpTools = r.McpTools != null
                                 ? new List<string>(r.McpTools)
                                 : new List<string>();
            _lastKnownMcpTools.Sort(StringComparer.Ordinal);
            continue;
         }
         if (r.Type == "perm")
         {
            // Session-scope grant marker. Record (don't apply yet) — the
            // last decision per tool wins, mirroring the live cache.
            if (!string.IsNullOrEmpty(r.PermTool) &&
                System.Enum.TryParse<PermissionDecision>(r.PermDecision, out var permDecision))
            {
               sessionGrants[r.PermTool!] = permDecision;
            }
            continue;
         }
         if (r.Type != "msg") continue;

         var role = TranscriptStore.ParseRole(r.Role);

         // Reconstruct the ChatMessage exactly as it left the wire.
         var msg = new ChatMessage
         {
            Role             = role,
            Content          = r.Content ?? "",
            ToolCallId       = r.ToolCallId,
            ReasoningContent = r.ReasoningContent,
            ReasoningTokens  = r.ReasoningTokens,
            PromptTokens     = r.PromptTokens,
            CompletionTokens = r.CompletionTokens,
            CachedReadTokens = r.CachedReadTokens,
            CacheWriteTokens = r.CacheWriteTokens,
            IsError          = r.IsError == true,
            Images           = TranscriptStore.ImagesFrom(r),
            Timings          = TranscriptStore.TimingsFrom(r),
            Reads            = TranscriptStore.ReadsFrom(r),
         };
         if (r.ToolCalls != null && r.ToolCalls.Count > 0)
         {
            msg.ToolCalls = new List<ToolCall>(r.ToolCalls.Count);
            foreach (var t in r.ToolCalls)
            {
               msg.ToolCalls.Add(new ToolCall
               {
                  Id            = t.Id,
                  Name          = t.Name,
                  ArgumentsJson = t.ArgumentsJson,
               });
            }
         }
         history.Add(msg);

         // Visual replay
         switch (role)
         {
            case ChatRole.User:
               // Stamp the bubble with whatever mode was effective at the
               // time this user message was sent. The walk above tracks
               // mode-change records so this is "current" relative to
               // the position in the JSONL stream, not whatever mode
               // the session happens to end on.
               AppendUserBubble(r.Content ?? "", effectiveMode, msg.Images);
               break;

            case ChatRole.Assistant:
               if (_RendersAsBubble(r))
               {
                  var bubble = _NewAssistantBubble();
                  // Stamped from the same counter the live path uses, so a
                  // branch taken after a resume forks at the same record as
                  // one taken during the original run.
                  bubble.RecordOrdinal = _committedAssistantTurns++;
                  AppendItem(bubble);
                  if (!string.IsNullOrEmpty(r.ReasoningContent))
                  {
                     bubble.SetReasoning(r.ReasoningContent!);
                     // No loop to push a reading, so compute it once here.
                     bubble.SetReasoningStats(ReasoningWatch.Evaluate(r.ReasoningContent!));
                     bubble.FinalizeReasoning(r.ReasoningTokens);
                  }
                  if (!string.IsNullOrEmpty(r.Content))
                  {
                     bubble.Append(r.Content!);
                     bubble.Flush();
                  }
                  bubble.SetTimings(msg.Timings, msg.Reads,
                                    msg.PromptTokens, msg.CachedReadTokens,
                                    msg.CompletionTokens, msg.ReasoningTokens, null);
               }
               if (msg.ToolCalls != null)
               {
                  foreach (var tc in msg.ToolCalls)
                  {
                     var card = new ToolCallCard(tc.Id, tc.Name, _LookupTool(tc.Name));
                     card.SetArgs(tc.ArgumentsJson);
                     localCards[tc.Id] = card;
                     AppendToolCard(card);

                     // A subagent call whose stored transcript survives gets
                     // its "view" button back — replay-mode viewer this time.
                     if (_IsSubagentTool(tc.Name))
                     {
                        var subPath = TranscriptStore.SubFileFor(_projectPath!, sessionId, tc.Id);
                        if (subPath != null && File.Exists(subPath))
                        {
                           var capturedTask  = _ParseSubagentTask(tc.ArgumentsJson);
                           var capturedPath  = subPath;
                           var capturedWrite = string.Equals(tc.Name, "start_write_subagent",
                                                             StringComparison.OrdinalIgnoreCase);
                           card.AddHeaderAction("view", "Open the stored subagent transcript",
                              () => SubagentReplayRequested?.Invoke(capturedTask, capturedPath, capturedWrite));
                        }
                     }
                  }
               }
               break;

            case ChatRole.Tool:
               if (!string.IsNullOrEmpty(r.ToolCallId) &&
                   localCards.TryGetValue(r.ToolCallId!, out var matchCard))
               {
                  // Persisted IsError drives the ✓/✗ status. Legacy
                  // records that pre-date the field default to success,
                  // matching the old replay behavior but new sessions
                  // restore the correct status.
                  var content = r.Content ?? "";
                  matchCard.SetResult(r.IsError == true
                                      ? ToolResult.Error(content)
                                      : ToolResult.Ok(content));
                  matchCard.SetTiming(msg.Timings);
               }
               break;

            case ChatRole.System:
               // Every system turn renders as a toggleable bubble, including
               // the base prompt (the first system record) so the "sys" toggle
               // reveals it on resume too — mirrors the new-session path.
               AppendSystemBubble(r.Content ?? "");
               if (!toolsBubbleShown)
               {
                  _AppendToolSpecsBubble();
                  toolsBubbleShown = true;
               }
               break;
         }
      }

      // Sessions persisted without a system record (empty prompt) still get
      // the tools block, just at the end of the replay.
      if (!toolsBubbleShown)
         _AppendToolSpecsBubble();

      // Realign THIS TAB's provider + model to whatever the session was
      // *last* using — honors any mid-session switches recorded above. The
      // selection is view-local: a resume must not rewrite the app-wide
      // default (other tabs' sessions keep their own models). The provider
      // is applied first so BuildLoop below connects to the right endpoint.
      bool realigned = false;
      if (!string.IsNullOrEmpty(effectiveProvider) &&
          !string.Equals(_selProvider, effectiveProvider, StringComparison.OrdinalIgnoreCase))
      {
         _selProvider = effectiveProvider!;
         realigned = true;
      }
      if (!string.IsNullOrEmpty(effectiveModel) && _selModel != effectiveModel)
      {
         _selModel = effectiveModel!;
         realigned = true;
      }
      if (realigned)
         RefreshModelCombo();

      // Use the META's frozen system prompt rather than the live setting:
      // a resumed session must talk to the model with the exact instructions
      // it originally had, otherwise behavior drifts.
      _sessionSystemPrompt = metaPrompt;
      // Before BuildLoop, which configures the loop's reasoning from it.
      _selReasoning = effectiveReasoning;
      _loop = BuildLoop(_agent.Settings, history);
      SubscribePersistence(_loop);

      // Restore which skills are already in this session's context so the
      // picker shows them as loaded.
      _RebuildLoadedSkillsFromHistory(history);

      // Reopen the transcript file for continued append.
      _transcriptStore = new TranscriptStore(path);
      _sessionId       = sessionId;
      _gate?.SetSessionId(sessionId);
      // Restore this session's "Allow session" grants. Clear first so we
      // don't inherit grants from whatever session this view held before.
      if (_gate != null)
      {
         _gate.ClearSessionCache();
         foreach (var kv in sessionGrants)
            _gate.SeedSessionGrant(kv.Key, kv.Value);
      }

      UpdateGaugeWindow();

      // Prefill the gauge from the most recent assistant turn that carries
      // usage tokens — otherwise the user sees an empty bar after resume
      // until the next turn completes, which misleads about how much room
      // is left in the context window.
      for (int i = history.Count - 1; i >= 0; i--)
      {
         var m = history[i];
         if (m.Role != ChatRole.Assistant) continue;
         if (m.PromptTokens.HasValue || m.CompletionTokens.HasValue)
         {
            Gauge.UsedTokens = (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0);
            Gauge.SetUsageBreakdown(
               m.PromptTokens ?? 0,
               m.CachedReadTokens ?? 0,
               m.CompletionTokens ?? 0,
               m.ReasoningTokens ?? 0,
               m.CacheWriteTokens ?? 0);
            SetStatus("resumed — last usage prompt=" + (m.PromptTokens ?? 0)
                      + " completion=" + (m.CompletionTokens ?? 0));
            break;
         }
      }

      // Seed the cumulative session rows from the whole resumed history.
      _SeedSessionTotalsFromHistory();
      _ResetStats();
      _SeedStatsFromHistory(history);

      // Restore mode state: pill highlight + gate enforcement match
      // whatever the session was last using. The system messages that
      // were sent at each flip are already in history via the normal
      // msg-replay path above, so the model sees them on the next turn.
      _selectedMode = effectiveMode;
      _appliedMode  = effectiveMode;
      if (_gate != null) _gate.Mode = effectiveMode;
      _RefreshModeUi();

      // Repopulate the pinned task panel from the most recent assistant
      // message that invoked the set_tasks tool. Replays the call by
      // parsing its arguments JSON the same way the live tool body does.
      for (int i = history.Count - 1; i >= 0; i--)
      {
         var m = history[i];
         if (m.Role != ChatRole.Assistant || m.ToolCalls == null) continue;
         ToolCall? hit = null;
         foreach (var tc in m.ToolCalls)
         {
            if (string.Equals(tc.Name, "set_tasks", System.StringComparison.OrdinalIgnoreCase))
               hit = tc; // last wins within one assistant turn too
         }
         if (hit == null) continue;
         _ReplaySetTasksArgs(hit.ArgumentsJson);
         break;
      }

      // Every replayed turn is committed, so all of them are branch points.
      _RefreshBranchAffordances();

      RefreshStatus();
   }

   /// <summary>
   /// Parse a stored <c>set_tasks</c> tool-call's arguments JSON and push
   /// the resulting items into the pinned panel. Used during session
   /// resume — the tool body isn't re-executed (it has no idempotency
   /// guarantees beyond the UI update anyway).
   /// </summary>
   private void _ReplaySetTasksArgs(string argsJson)
   {
      if (string.IsNullOrWhiteSpace(argsJson)) return;
      try
      {
         using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
         if (!doc.RootElement.TryGetProperty("tasks", out var arr) ||
             arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return;

         var items = new List<TaskListPanel.TaskItem>(arr.GetArrayLength());
         foreach (var el in arr.EnumerateArray())
         {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            string text = el.TryGetProperty("text", out var t) &&
                          t.ValueKind == System.Text.Json.JsonValueKind.String
                          ? (t.GetString() ?? "")
                          : "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            var status = TaskListPanel.Status.Pending;
            if (el.TryGetProperty("status", out var s) &&
                s.ValueKind == System.Text.Json.JsonValueKind.String)
            {
               status = (s.GetString() ?? "").ToLowerInvariant() switch
               {
                  "completed"   => TaskListPanel.Status.Completed,
                  "in_progress" => TaskListPanel.Status.InProgress,
                  "cancelled"   => TaskListPanel.Status.Cancelled,
                  _             => TaskListPanel.Status.Pending,
               };
            }
            items.Add(new TaskListPanel.TaskItem(status, text));
         }
         if (items.Count > 0) TaskPanel.SetTasks(items);
      }
      catch
      {
         // Malformed args — leave the panel hidden, audit log still has
         // the raw call for forensic inspection.
      }
   }

   // Frozen system prompt for the active session: set by EnsureNewSession
   // (new chat, includes dynamic trailing context) or ResumeSession (from
   // the meta record). Null between sessions.
   private string?                     _sessionSystemPrompt;

   // JSONL transcript for the currently active conversation. Null between
   // sessions (after Reset / before first Send / after switching project).
   private TranscriptStore?                       _transcriptStore;
   private string?                                _sessionId;

   // Auto-titling runs beside the first turn, not inside it, so it needs its
   // own cancellation: closing the tab or switching sessions must abandon a
   // name still in flight rather than let it land on a session the user left.
   private CancellationTokenSource?               _titleCts;
}
