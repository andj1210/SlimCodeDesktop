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
/// Permission plumbing: the pseudo-modal prompt overlay the gate awaits,
/// gate event wiring, and the project permission / audit log dialogs.
/// </summary>
public partial class NativeAgentView
{
   /// <summary>
   /// Marshal to the UI thread, raise the in-tab pseudo-modal overlay, and
   /// await the user's response. The gate awaits this from a non-UI thread
   /// (the streaming tool-execution path) so the thread switch is mandatory.
   /// Blocks only this view: other tabs stay interactive, and a background
   /// tab's prompt surfaces via <see cref="PromptPendingChanged"/> (badge)
   /// instead of stealing focus with a window-modal dialog.
   /// </summary>
   private async Task<PromptResponse> ShowPermissionPrompt(PromptRequest req, CancellationToken ct)
   {
      // RunContinuationsAsynchronously: the gate awaits this task from the
      // loop's background context — don't run its continuation inline on
      // the UI thread that resolves the TCS.
      var tcs = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
      Dispatcher.UIThread.Post(() =>
      {
         _pendingPrompt      = tcs;
         _promptFromSubagent = !string.IsNullOrWhiteSpace(req.SubagentLabel);
         PermPanel.Configure(req);
         PermissionOverlay.IsVisible = true;
         PermPanel.FocusDefault();
         PromptPendingChanged?.Invoke(true);
      });
      // Loop cancelled (user hit Cancel / closed the tab) while the prompt
      // is pending: tear the overlay down and let the prompt service map
      // the cancellation to a Deny. The identity check keeps a late-posted
      // cancellation from hiding a newer prompt's overlay.
      using var reg = ct.Register(() => UiPost(() =>
      {
         if (!ReferenceEquals(_pendingPrompt, tcs)) return;
         _HidePromptOverlay();
         tcs.TrySetCanceled();
      }));
      return await tcs.Task.ConfigureAwait(false);
   }

   /// <summary>
   /// PermPanel.Completed handler (UI thread). Hides the overlay and
   /// resolves the gate's await. On Abort the loop is cancelled *before*
   /// the response is returned so the gate logs a clean denial for the
   /// in-flight tool call and the next loop iteration sees cancellation
   /// instead of trying yet another tool.
   ///
   /// When the prompt came from a nested subagent, Abort stops that
   /// subagent only: the parent turn survives and receives the abort as a
   /// failed tool result it can recover from. Stopping the whole
   /// conversation because one delegated step went wrong is too blunt —
   /// the tab's Cancel button is still there for that.
   /// </summary>
   private void OnPromptCompleted(PromptResponse response)
   {
      var tcs = _pendingPrompt;
      var sub = _promptFromSubagent ? _activeSubagent : null;
      _HidePromptOverlay();

      if (response.Abort)
      {
         if (sub != null && !sub.IsCompleted)
            sub.Cancel();
         else
            CancelIfRunning();
      }

      tcs?.TrySetResult(response);
      TxtInput.Focus();
   }

   /// <summary>
   /// Resolve a pending prompt as Deny before teardown (Reset / tab close).
   /// Without this the gate's await — and the loop task behind it — would
   /// stay blocked forever on an overlay that no longer exists.
   /// </summary>
   private void _AbandonPendingPrompt(string reason)
   {
      var tcs = _pendingPrompt;
      if (tcs == null) return;
      _HidePromptOverlay();
      tcs.TrySetResult(new PromptResponse
      {
         Decision = PermissionDecision.Deny,
         Scope    = PermissionScope.User,
         Reason   = reason,
         Abort    = true,
      });
   }

   private void _HidePromptOverlay()
   {
      _pendingPrompt      = null;
      _promptFromSubagent = false;
      if (PermissionOverlay.IsVisible)
      {
         PermissionOverlay.IsVisible = false;
         PromptPendingChanged?.Invoke(false);
      }
   }

   private void BtnProjectPerms_Click(object? sender, RoutedEventArgs e)
   {
      if (string.IsNullOrEmpty(_projectPath)) return;

      var store       = ProjectPolicyStore.LoadOrCreate(_projectPath);
      var globalStore = GlobalPolicyStore.LoadOrCreate();
      // Include configured build/test commands so their permission can be set
      // here (false = no set_tasks; the editor only needs the tool list).
      var tools       = _BuildToolRegistry(withTaskCallback: false);
      var win         = new PermissionEditorWindow();
      win.Configure(
         PermissionEditorWindow.Mode.Project,
         store.Policy,
         tools,
         onSave: updated =>
         {
            // One swap, not clear-then-refill: the live gate may be reading
            // this table from a tool call's background thread. The store is a
            // process-wide singleton, so the gate holds this very instance and
            // sees the change immediately — no gate rebuild needed (which
            // would drop the session's mode and its session-scope grants).
            store.Policy.Replace(updated.Tools);
            store.Save();
         },
         contextHint:   store.FilePath,
         appPolicy:     globalStore.Policy,
         sessionGrants: _gate?.SnapshotSessionGrants());
      var owner = TopLevel.GetTopLevel(this) as Window;
      if (owner != null) win.ShowDialog(owner);
      else                win.Show();
   }

   private void BtnAuditLog_Click(object? sender, RoutedEventArgs e)
   {
      if (string.IsNullOrEmpty(_projectPath)) return;
      var metaDir = ProjectPolicyStore.ProjectMetadataDir(_projectPath);
      if (metaDir == null) return;

      var logPath = System.IO.Path.Combine(metaDir, "permissions.log.jsonl");

      // Build live-session set only when the user wants orphan cleanup; if
      // they've opted into permanent audit history we skip the GC pass.
      List<string>? liveIds = null;
      if (_agent != null && !_agent.Settings.PreserveAuditOnSessionDelete)
      {
         liveIds = new List<string>();
         foreach (var s in _agent.EnumerateSessions(_projectPath))
            liveIds.Add(s.Id);
      }

      var win = new AuditLogWindow();
      win.Configure(logPath, _sessionId, liveIds);
      var owner = TopLevel.GetTopLevel(this) as Window;
      if (owner != null) win.ShowDialog(owner);
      else                win.Show();
   }

   // A RebuildGate() used to live here, called after the permission editor
   // saved, because the gate held a different ProjectPolicyStore instance than
   // the editor did. The stores are process-wide singletons now (see
   // ProjectPolicyStore.LoadOrCreate), so an edit lands on the live gate
   // directly — and rebuilding would only have lost the session's mode and its
   // session-scope grants.

   /// <summary>
   /// Wire gate-level events that need UI-thread mirroring. Currently:
   /// Plan→Implement mode flip requested from a permission prompt — mirrors
   /// the pills, persists the mode record, and drops a NoticeLine so resume
   /// + the running transcript stay in sync.
   /// </summary>
   private void _WireGateEvents(PermissionGate gate)
   {
      // Persist newly-granted session-scope permissions into the active
      // transcript so resume can restore them. Fires on the gate's
      // (background) thread during a tool call; the file append is itself
      // thread-safe and _transcriptStore is stable for the call's duration.
      gate.SessionGrantPersist += (tool, decision) =>
         _transcriptStore?.AppendSessionGrant(tool, decision);

      gate.ModeSwitchedFromPrompt += () =>
      {
         Avalonia.Threading.Dispatcher.UIThread.Post(() =>
         {
            var prev = _appliedMode;
            _selectedMode = AgentMode.Implement;
            _appliedMode  = AgentMode.Implement;
            _transcriptStore?.AppendModeChange(AgentMode.Implement);
            _loop?.AppendSystemMessage(
               "[Mode switch] Switched to IMPLEMENT mode mid-call from the permission prompt. " +
               "Subsequent write/build/test calls follow standard gate rules (cached approvals carry).");
            AppendItem(new NoticeLine("mode switched: " + prev + " → Implement"));
            _RefreshModeUi();
         });
      };
   }

   private void PermissionOverlay_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key != Key.Escape) return;
      PermPanel.ResolveDeny("denied via Escape");
      e.Handled = true;
   }

   // Pending pseudo-modal permission prompt. Non-null exactly while the
   // overlay is up; resolved by a panel button, Escape, loop cancellation,
   // or forced teardown on Reset.
   private TaskCompletionSource<PromptResponse>?  _pendingPrompt;

   // True while the pending prompt was raised by a nested subagent rather
   // than the main loop. Decides what Abort means (subagent only vs. whole
   // turn); UI thread only, alongside _pendingPrompt.
   private bool                                   _promptFromSubagent;
}
