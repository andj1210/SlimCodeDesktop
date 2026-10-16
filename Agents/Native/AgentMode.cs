// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// Operating mode for the native agent. Lives in session state (not in
/// the user's persisted settings) — it can flip mid-conversation and
/// each flip is announced to the model via an injected system message
/// just before the user's next turn.
/// </summary>
public enum AgentMode
{
   /// <summary>Default. Full tool access subject to the permission gate.</summary>
   Implement,

   /// <summary>
   /// Plan-and-discuss only. Write and Execute tools still callable, but
   /// the permission gate ignores cached Allow grants and re-prompts on
   /// every invocation so the user must explicitly approve each side
   /// effect. Typically used to ask the model to draft a single Markdown
   /// plan before switching back to Implement.
   /// </summary>
   Plan,
}
