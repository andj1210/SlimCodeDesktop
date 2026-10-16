// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>Which layer decided an effective permission.</summary>
public enum EffectiveSource
{
   App,       // global policy (incl. sticky AlwaysDeny / AlwaysAsk)
   Session,   // in-session grant
   Project,   // per-project policy
   Default,   // nothing matched → fall through to the live prompt
}

/// <summary>Computed effective permission + the layer that produced it.</summary>
public readonly record struct EffectiveResult(PermissionDecision Decision, EffectiveSource Source);

/// <summary>
/// Display-only resolver that mirrors the static layer order of
/// <see cref="PermissionGate.CheckAsync"/> for Implement mode. It deliberately
/// excludes the Plan-mode short-circuit (write/exec always ask in Plan,
/// regardless of layers) and the interactive prompt itself. Kept in lockstep
/// with the gate so the editor's "Effective" column can't silently drift from
/// what the gate actually does.
///
/// The returned <see cref="EffectiveResult.Decision"/> is normalized to
/// <see cref="PermissionDecision.Allow"/> / <see cref="PermissionDecision.Deny"/> /
/// <see cref="PermissionDecision.Prompt"/> ("ask").
/// </summary>
public static class PermissionResolver
{
   public static EffectiveResult Resolve(
      PermissionDecision  app,
      PermissionDecision? session,
      PermissionDecision  project)
   {
      // 1-2. Sticky App vetoes — no narrower layer can override these.
      if (app == PermissionDecision.AlwaysDeny)
         return new EffectiveResult(PermissionDecision.Deny, EffectiveSource.App);
      if (app == PermissionDecision.AlwaysAsk)
         return new EffectiveResult(PermissionDecision.Prompt, EffectiveSource.App);

      // 3. Session grant (only ever Allow / Deny).
      if (session is PermissionDecision sd &&
          (sd == PermissionDecision.Allow || sd == PermissionDecision.Deny))
         return new EffectiveResult(sd, EffectiveSource.Session);

      // 4. Project decision, when it has an opinion.
      if (project != PermissionDecision.Prompt)
         return new EffectiveResult(project, EffectiveSource.Project);

      // 5. App layered (regular Allow / Deny).
      if (app == PermissionDecision.Allow || app == PermissionDecision.Deny)
         return new EffectiveResult(app, EffectiveSource.App);

      // 6. Nothing matched → the gate would prompt the user.
      return new EffectiveResult(PermissionDecision.Prompt, EffectiveSource.Default);
   }
}
