// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Permission prompt body — the tool name, arguments preview, reason field
/// and decision buttons. Hosted inside the per-tab pseudo-modal overlay
/// (<c>NativeAgentView</c>'s PermissionOverlay layer) instead of a modal
/// window, so a prompt only ever blocks its own tab and concurrent tabs
/// can each hold a pending prompt of their own.
///
/// One-shot per Configure: exactly one <see cref="Completed"/> fires per
/// request, from whichever button resolves it (or <see cref="ResolveDeny"/>
/// for Escape / host-forced teardown).
/// </summary>
public partial class PermissionPromptPanel : UserControl
{
   public PermissionPromptPanel()
   {
      InitializeComponent();
   }

   /// <summary>Fires exactly once per Configure with the user's decision.</summary>
   public event Action<PromptResponse>? Completed;

   /// <summary>Bind to a request. Resets all per-request state.</summary>
   public void Configure(PromptRequest req)
   {
      _completed = false;

      TxtHeadline.Text = req.ToolName + "  →  " + req.Category.ToString().ToLowerInvariant();
      TxtProject.Text  = req.ProjectPath;
      TxtArgs.Text     = string.IsNullOrEmpty(req.ArgsPreview) ? "(none)" : req.ArgsPreview;
      TxtReason.Text   = "";

      // Category badge color hints at risk level.
      (TxtCategory.Text, TxtCategory.Background, TxtCategory.Foreground) = req.Category switch
      {
         ToolCategory.Read    => ("READ",    ThemeBrushes.HoverBackground, ThemeBrushes.TextMutedStrong),
         ToolCategory.Write   => ("WRITE",   ThemeBrushes.AccentDark,      (IBrush)Brushes.White),
         ToolCategory.Execute => ("EXECUTE", (IBrush)Brushes.IndianRed,    (IBrush)Brushes.White),
         _                    => ("?",       ThemeBrushes.HoverBackground, ThemeBrushes.TextMutedStrong),
      };

      // OneShotOnly: hide upgrade buttons so the user can only pick
      // Allow once / Deny. Triggered by global AlwaysAsk policy or Plan mode.
      BtnAllowSession.IsVisible = !req.OneShotOnly;
      BtnAllowProject.IsVisible = !req.OneShotOnly;

      // Plan-mode escape hatch: only on Plan-mode-driven prompts (not the
      // AlwaysAsk variant — switching modes can't unstick global AlwaysAsk).
      BtnSwitchImplement.IsVisible = req.PlanModePrompt;

      // Who is asking. A subagent runs autonomously with a task the user
      // never saw, so approving its write is a different act from approving
      // the main agent's — say so, and scope the Abort button to the
      // subagent only (the parent turn survives and gets a failed tool
      // result it can recover from).
      bool fromSub = !string.IsNullOrWhiteSpace(req.SubagentLabel);
      OriginBadge.IsVisible = fromSub;
      TxtOrigin.Text        = fromSub ? "SUBAGENT: " + req.SubagentLabel : "";
      BtnAbort.Content      = fromSub ? "Abort subagent" : "Abort agent";
      ToolTip.SetTip(BtnAbort, fromSub
         ? "Deny this call and stop the subagent. The main agent keeps running and " +
           "receives the subagent's failure as a tool result."
         : "Deny this call and stop the agent loop. Use this when the model keeps " +
           "retrying after denials.");

      TxtRestriction.Text      = req.RestrictionNote ?? "";
      TxtRestriction.IsVisible = !string.IsNullOrEmpty(req.RestrictionNote);
   }

   /// <summary>Focus the safe default (Deny) so Enter can't accidentally approve.</summary>
   public void FocusDefault() => BtnDeny.Focus();

   /// <summary>
   /// Host-driven one-shot deny — Escape key or forced teardown (tab close
   /// with a prompt still pending). No-op if a button already resolved it.
   /// </summary>
   public void ResolveDeny(string reason, bool abort = false)
   {
      Finish(new PromptResponse
      {
         Decision = PermissionDecision.Deny,
         Scope    = PermissionScope.User,
         Reason   = reason,
         Abort    = abort,
      });
   }

   // -- private methods --------------------------------------------------------

   private string? _ReasonOrNull() =>
      string.IsNullOrWhiteSpace(TxtReason.Text) ? null : TxtReason.Text!.Trim();

   private void Finish(PromptResponse response)
   {
      if (_completed) return;
      _completed = true;
      Completed?.Invoke(response);
   }

   // -- private event handlers ---------------------------------------------------

   private void BtnDeny_Click(object? sender, RoutedEventArgs e) =>
      Finish(new PromptResponse
      {
         Decision = PermissionDecision.Deny,
         Scope    = PermissionScope.User,
         Reason   = _ReasonOrNull(),
      });

   private void BtnAbort_Click(object? sender, RoutedEventArgs e) =>
      // Abort = deny this call AND stop the agent loop. The host sees the
      // Abort flag and cancels the CancellationTokenSource that drives the
      // loop; the Deny here is what gets recorded in the audit log.
      Finish(new PromptResponse
      {
         Decision = PermissionDecision.Deny,
         Scope    = PermissionScope.User,
         Reason   = _ReasonOrNull() ?? "user aborted the agent",
         Abort    = true,
      });

   private void BtnAllowOnce_Click(object? sender, RoutedEventArgs e) =>
      Finish(new PromptResponse
      {
         Decision = PermissionDecision.Allow,
         Scope    = PermissionScope.User,
         Reason   = _ReasonOrNull(),
      });

   private void BtnAllowSession_Click(object? sender, RoutedEventArgs e) =>
      Finish(new PromptResponse
      {
         Decision = PermissionDecision.Allow,
         Scope    = PermissionScope.Session,
         Reason   = _ReasonOrNull(),
      });

   private void BtnAllowProject_Click(object? sender, RoutedEventArgs e) =>
      Finish(new PromptResponse
      {
         Decision = PermissionDecision.Allow,
         Scope    = PermissionScope.Project,
         Reason   = _ReasonOrNull(),
      });

   private void BtnSwitchImplement_Click(object? sender, RoutedEventArgs e) =>
      // Decision/Scope are ignored when SwitchToImplement is set — gate
      // flips mode and re-resolves the same call.
      Finish(new PromptResponse
      {
         Decision          = PermissionDecision.Deny,
         Scope             = PermissionScope.User,
         Reason            = _ReasonOrNull(),
         SwitchToImplement = true,
      });

   // -- fields -------------------------------------------------------------------

   private bool _completed;
}
