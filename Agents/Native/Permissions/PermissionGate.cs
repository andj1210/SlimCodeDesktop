// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Resolves tool-call permission requests by walking, in order:
/// <list type="number">
///   <item>session cache (in-memory, per gate instance);</item>
///   <item>project policy (persistent, per-project file);</item>
///   <item>global policy (persistent, app-wide file);</item>
///   <item>user prompt via <see cref="PermissionPromptService"/>.</item>
/// </list>
///
/// Every decision — whether served from cache or freshly asked — flows
/// through <see cref="PermissionAuditLog"/> so the project's gate history
/// is complete.
///
/// One instance per active conversation. Constructed by
/// <c>NativeAgentView</c> after picking the project; the gate captures the
/// project policy + audit log at construction time and writes through to
/// them as scopes are granted.
/// </summary>
public sealed class PermissionGate
{
   public PermissionGate(
      GlobalPolicyStore       globalStore,
      ProjectPolicyStore      projectStore,
      PermissionAuditLog      auditLog,
      PermissionPromptService promptService,
      string                  projectPath,
      string?                 sessionId)
   {
      _globalStore   = globalStore   ?? throw new ArgumentNullException(nameof(globalStore));
      _projectStore  = projectStore  ?? throw new ArgumentNullException(nameof(projectStore));
      _auditLog      = auditLog      ?? throw new ArgumentNullException(nameof(auditLog));
      _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
      _projectPath   = projectPath ?? "";
      _sessionId     = sessionId;
   }

   /// <summary>Update the session id (e.g. when a new session is started mid-gate).</summary>
   public void SetSessionId(string? sessionId) => _sessionId = sessionId;

   /// <summary>
   /// Raised when a brand-new <see cref="PermissionScope.Session"/> grant is
   /// applied from a user prompt. The host persists it into the active
   /// session's transcript so a later resume can restore it via
   /// <see cref="SeedSessionGrant"/>. Not fired for seeded (replayed) grants.
   /// </summary>
   public event Action<string, PermissionDecision>? SessionGrantPersist;

   /// <summary>
   /// Drop all in-memory session grants. The gate instance is reused across
   /// sessions (it's per-project), so this MUST be called on every session
   /// boundary — Reset / new session / resume — or grants leak between
   /// sibling sessions.
   /// </summary>
   public void ClearSessionCache()
   {
      lock (_sessionLock) _sessionCache.Clear();
   }

   /// <summary>
   /// Restore a persisted session grant on resume. Does not re-fire
   /// <see cref="SessionGrantPersist"/> (the record already exists on disk).
   /// </summary>
   public void SeedSessionGrant(string toolName, PermissionDecision decision)
   {
      if (string.IsNullOrEmpty(toolName)) return;
      lock (_sessionLock) _sessionCache[toolName] = decision;
   }

   /// <summary>
   /// Point-in-time copy of the current session grants (tool → Allow/Deny).
   /// Used by the permission editor to display the live Session layer.
   /// </summary>
   public IReadOnlyDictionary<string, PermissionDecision> SnapshotSessionGrants()
   {
      lock (_sessionLock)
         return new Dictionary<string, PermissionDecision>(_sessionCache, StringComparer.OrdinalIgnoreCase);
   }

   /// <summary>
   /// Current operating mode of the session. <see cref="AgentMode.Plan"/>
   /// forces a fresh prompt on every Write / Execute call regardless of
   /// cached or persisted Allow grants — Read tools are unaffected.
   /// </summary>
   public AgentMode Mode { get; set; } = AgentMode.Implement;

   /// <summary>
   /// Raised when the user clicks "Switch to Implement mode" on a Plan-mode
   /// permission prompt. The gate has already flipped <see cref="Mode"/> by
   /// the time this fires; the host is expected to mirror the UI (pills,
   /// NoticeLine, persisted mode record) synchronously so the visual state
   /// is consistent before the retry prompt (if any) appears.
   /// </summary>
   public event Action? ModeSwitchedFromPrompt;

   /// <summary>
   /// Resolve the gate for one tool call. Resolution order:
   /// <list type="number">
   ///   <item><b>Global sticky</b> — <see cref="PermissionDecision.AlwaysDeny"/>
   ///         short-circuits to Deny; <see cref="PermissionDecision.AlwaysAsk"/>
   ///         jumps straight to a one-shot-only user prompt. Neither can be
   ///         overridden by narrower layers.</item>
   ///   <item>Session cache.</item>
   ///   <item>Project policy.</item>
   ///   <item>Global layered (regular Allow / Deny).</item>
   ///   <item>User prompt with full scope choices.</item>
   /// </list>
   /// Always writes one audit record regardless of which layer answered.
   /// </summary>
   /// <param name="subagentLabel">Short label of the nested subagent making
   /// the call, or null for the session's main agent. Only travels into the
   /// prompt dialog so the user knows who is asking — resolution, caching
   /// and grant scope are deliberately identical either way.</param>
   public async Task<PermissionGrant> CheckAsync(
      string         toolName,
      ToolCategory   category,
      string         argsPreview,
      CancellationToken cancel,
      string?        subagentLabel = null)
   {
      var globalDecision = _globalStore.Policy.Get(toolName);

      // 0. Plan mode short-circuit. Write and Execute always re-prompt;
      //    cached Allow grants of any scope are ignored. AlwaysDeny still
      //    wins (safety first) so a project / global hard ban still holds.
      if (Mode == AgentMode.Plan && category != ToolCategory.Read &&
          globalDecision != PermissionDecision.AlwaysDeny)
      {
         return await PromptUser(
            toolName, category, argsPreview,
            oneShotOnly:    true,
            planModePrompt: true,
            restrictionNote: "Plan mode is on — every write/exec call is reviewed individually.",
            subagentLabel, cancel).ConfigureAwait(false);
      }

      // 1. Sticky global vetoes — no override possible from any other layer.
      if (globalDecision == PermissionDecision.AlwaysDeny)
      {
         var g = MakeGrant(PermissionDecision.Deny, PermissionScope.Global,
                           "global AlwaysDeny — sticky, cannot be overridden");
         Log(toolName, g, argsPreview);
         return g;
      }

      if (globalDecision == PermissionDecision.AlwaysAsk)
      {
         // Skip all cached layers — every call must ask. Restrict the
         // dialog so the user can't grant a wider scope that would be
         // silently ignored on the next call.
         return await PromptUser(
            toolName, category, argsPreview,
            oneShotOnly:    true,
            planModePrompt: false,
            restrictionNote: "Global policy requires asking on every call (AlwaysAsk).",
            subagentLabel, cancel).ConfigureAwait(false);
      }

      // 2. Session cache.
      bool hasSession;
      PermissionDecision sessionDecision;
      lock (_sessionLock) hasSession = _sessionCache.TryGetValue(toolName, out sessionDecision);
      if (hasSession)
      {
         var g = MakeGrant(sessionDecision, PermissionScope.Session,
                           "session grant: " + sessionDecision);
         Log(toolName, g, argsPreview);
         return g;
      }

      // 3. Project policy.
      var projectDecision = _projectStore.Policy.Get(toolName);
      if (projectDecision != PermissionDecision.Prompt)
      {
         var g = MakeGrant(projectDecision, PermissionScope.Project,
                           "project policy: " + projectDecision);
         Log(toolName, g, argsPreview);
         return g;
      }

      // 4. Global layered (regular Allow / Deny — Always* already handled).
      if (globalDecision == PermissionDecision.Allow ||
          globalDecision == PermissionDecision.Deny)
      {
         var scope = globalDecision == PermissionDecision.Allow && category == ToolCategory.Read
            ? PermissionScope.Default   // tag baked-in read defaults distinctly
            : PermissionScope.Global;
         var g = MakeGrant(globalDecision, scope, scope + " policy: " + globalDecision);
         Log(toolName, g, argsPreview);
         return g;
      }

      // 5. Ask the user, full scope choices.
      return await PromptUser(toolName, category, argsPreview,
                              oneShotOnly: false, planModePrompt: false,
                              restrictionNote: null,
                              subagentLabel, cancel).ConfigureAwait(false);
   }

   private async Task<PermissionGrant> PromptUser(
      string         toolName,
      ToolCategory   category,
      string         argsPreview,
      bool           oneShotOnly,
      bool           planModePrompt,
      string?        restrictionNote,
      string?        subagentLabel,
      CancellationToken cancel)
   {
      var request = new PromptRequest
      {
         ToolName        = toolName,
         Category        = category,
         ArgsPreview     = argsPreview,
         ProjectPath     = _projectPath,
         SessionId       = _sessionId,
         OneShotOnly     = oneShotOnly,
         PlanModePrompt  = planModePrompt,
         RestrictionNote = restrictionNote,
         SubagentLabel   = subagentLabel,
      };
      var response = await _promptService.AskAsync(request, cancel).ConfigureAwait(false);

      // User wants out of Plan mode — flip the gate, let the host mirror UI,
      // then re-resolve this same call. Cached/persisted Allow grants now
      // apply; if none match, the user gets the standard (non-restricted)
      // prompt next.
      if (response.SwitchToImplement && planModePrompt)
      {
         Mode = AgentMode.Implement;
         try { ModeSwitchedFromPrompt?.Invoke(); } catch { }
         return await CheckAsync(toolName, category, argsPreview, cancel, subagentLabel)
                        .ConfigureAwait(false);
      }

      // Even under OneShotOnly the user might (in theory) submit a wider
      // scope via API; clamp to User in that case so the audit log doesn't
      // record a misleading scope.
      if (oneShotOnly && response.Scope != PermissionScope.User)
      {
         response = new PromptResponse
         {
            Decision = response.Decision,
            Scope    = PermissionScope.User,
            Reason   = response.Reason,
         };
      }

      ApplyScope(toolName, response);

      var reasonSuffix = string.IsNullOrWhiteSpace(response.Reason)
         ? ""
         : " — " + response.Reason;
      var userGrant = MakeGrant(response.Decision, response.Scope,
                                "user " + response.Decision.ToString().ToLowerInvariant() +
                                " (" + response.Scope + ")" + reasonSuffix);
      Log(toolName, userGrant, argsPreview);
      return userGrant;
   }

   // -- internals ------------------------------------------------------------

   private void ApplyScope(string toolName, PromptResponse response)
   {
      switch (response.Scope)
      {
         case PermissionScope.User:
            // One-shot — no caching.
            break;
         case PermissionScope.Session:
            lock (_sessionLock) _sessionCache[toolName] = response.Decision;
            // Persist into the active session transcript so resume restores it.
            try { SessionGrantPersist?.Invoke(toolName, response.Decision); } catch { }
            break;
         case PermissionScope.Project:
            _projectStore.Policy.Set(toolName, response.Decision);
            _projectStore.Save();
            break;
         case PermissionScope.Global:
            _globalStore.Policy.Set(toolName, response.Decision);
            _globalStore.Save();
            break;
      }
   }

   private static PermissionGrant MakeGrant(PermissionDecision d, PermissionScope s, string reason) =>
      new(d, s, reason, DateTime.UtcNow);

   private void Log(string tool, PermissionGrant grant, string argsPreview) =>
      _auditLog.Append(tool, grant, argsPreview, _sessionId);

   // -- fields ---------------------------------------------------------------

   private readonly GlobalPolicyStore       _globalStore;
   private readonly ProjectPolicyStore      _projectStore;
   private readonly PermissionAuditLog      _auditLog;
   private readonly PermissionPromptService _promptService;
   private readonly string                  _projectPath;
   private string?                          _sessionId;

   // Tool -> Allow/Deny for the active session. Persisted into the session
   // transcript (perm records) and reseeded on resume; cleared on every
   // session boundary by the host. Guarded because CheckAsync runs on a
   // background thread while Clear/Seed are driven from the UI thread.
   private readonly object _sessionLock = new();
   private readonly Dictionary<string, PermissionDecision> _sessionCache =
      new(StringComparer.OrdinalIgnoreCase);
}
