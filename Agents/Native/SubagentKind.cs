// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// Capability level a delegated subagent runs at. Chosen by the parent
/// model at call time by picking one of the two subagent tools, so a plain
/// research delegation stays prompt-free while anything that can touch the
/// tree is gated as a write.
/// </summary>
public enum SubagentKind
{
   /// <summary>
   /// Inspection tools only (read_file, list_dir, glob, grep). Cannot
   /// mutate anything by construction, which is what lets the delegating
   /// tool be categorized <c>Read</c> honestly.
   /// </summary>
   ReadOnly,

   /// <summary>
   /// Full file tools plus the project's configured build/test commands.
   /// Every call still passes the session's permission gate, and grants are
   /// shared with the main loop — a "session" allow given here also covers
   /// the parent, by design.
   /// </summary>
   Write,
}
