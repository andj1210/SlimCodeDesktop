// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Result of a gate decision. Carries enough provenance to drive the audit
/// log and to display "why was this allowed" in the UI later.
/// </summary>
public readonly record struct PermissionGrant(
   PermissionDecision Decision,
   PermissionScope    Scope,
   string             Reason,
   DateTime           TimestampUtc);
