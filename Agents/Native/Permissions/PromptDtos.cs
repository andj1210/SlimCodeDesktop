// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// One pending user-prompt request. Built by <see cref="PermissionGate"/>
/// when no cached layer has an opinion and the user must decide. The UI
/// (see <c>PermissionPromptPanel</c>) renders one of these and emits a
/// <see cref="PromptResponse"/>.
/// </summary>
public sealed class PromptRequest
{
   public string       ToolName    { get; init; } = "";
   public ToolCategory Category    { get; init; }
   public string       ArgsPreview { get; init; } = "";
   public string       ProjectPath { get; init; } = "";
   public string?      SessionId   { get; init; }

   /// <summary>
   /// When true, the prompt dialog must restrict the user to one-shot
   /// decisions (Allow once / Deny). Triggered by a global
   /// <see cref="PermissionDecision.AlwaysAsk"/> entry — wider-scope grants
   /// would be silently ignored anyway, so we don't offer them.
   /// </summary>
   public bool OneShotOnly { get; init; }

   /// <summary>
   /// True when this prompt was raised by the Plan-mode short-circuit
   /// (i.e. the user is in Plan, called a write/exec tool). Dialog uses
   /// this to surface the "Switch to Implement mode" escape hatch — relevant
   /// only here, not for the AlwaysAsk-driven OneShotOnly path.
   /// </summary>
   public bool PlanModePrompt { get; init; }

   /// <summary>
   /// Optional explanatory line shown above the dialog buttons. Currently
   /// used to surface why scope upgrades are disabled (e.g. "global policy
   /// requires a prompt for every call of this tool").
   /// </summary>
   public string? RestrictionNote { get; init; }

   /// <summary>
   /// Non-null when the call comes from a nested subagent loop rather than
   /// the session's main agent; carries the subagent's short label. Purely
   /// informational — subagents deliberately share the session's gate and
   /// its grants, so this changes what the dialog *says*, never how the
   /// decision resolves or how wide a granted scope reaches.
   /// </summary>
   public string? SubagentLabel { get; init; }
}

/// <summary>
/// User's answer to a <see cref="PromptRequest"/>. The optional
/// <see cref="Reason"/> field lets the user annotate the audit log
/// (e.g. "trusted because I just reviewed the diff").
/// </summary>
public sealed class PromptResponse
{
   public PermissionDecision Decision { get; init; } = PermissionDecision.Deny;
   public PermissionScope    Scope    { get; init; } = PermissionScope.User;
   public string?            Reason   { get; init; }

   /// <summary>
   /// User wants the entire agent loop stopped, not just this one tool call
   /// denied. Set by the dialog's "Abort agent" button. The host (the view
   /// that owns the agent loop's CancellationTokenSource) cancels the loop
   /// when it sees this flag; the response itself still propagates a Deny
   /// so the audit log records the denial reason cleanly.
   /// </summary>
   public bool Abort { get; init; }

   /// <summary>
   /// User clicked "Switch to Implement mode" instead of deciding this call.
   /// The gate flips its mode, lets the host mirror the UI change, then
   /// re-runs <see cref="PermissionGate.CheckAsync"/> for the same tool call
   /// — cached/persisted Allow grants may now resolve it without another
   /// prompt. Decision/Scope are ignored when this flag is set.
   /// </summary>
   public bool SwitchToImplement { get; init; }
}
