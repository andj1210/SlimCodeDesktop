// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Outcome of a policy lookup at one resolution layer.
///
/// Layered values (project / session may override at narrower scope):
///   <see cref="Prompt"/>  - "this layer has no opinion".
///   <see cref="Allow"/>   - grant the call here.
///   <see cref="Deny"/>    - reject the call here.
///
/// Sticky values (meaningful only at the global layer; cannot be overridden):
///   <see cref="AlwaysDeny"/> - hard veto. The gate returns Deny without
///                              consulting any other layer and without
///                              prompting the user.
///   <see cref="AlwaysAsk"/>  - force a user prompt on every call. Session /
///                              project Allow grants are ignored. The prompt
///                              dialog hides scope upgrades so the user can
///                              only pick "Allow once" or "Deny".
///
/// Sticky values are stored only in the global policy file. Setting them on
/// a project file is meaningless (the gate doesn't read them at that layer).
/// </summary>
public enum PermissionDecision
{
   Prompt = 0,
   Allow,
   Deny,
   AlwaysDeny,
   AlwaysAsk,
}
