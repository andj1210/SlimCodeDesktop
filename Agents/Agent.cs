// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Agents;

/// <summary>
/// Base class for a supported coding agent.
///
/// Concrete agents live under <c>Agents/{Name}/</c> and override only what is
/// agent-specific; default implementations return safe no-ops. Today
/// <c>NativeAgent</c> is the only one — see <see cref="AgentRegistry"/> for
/// why the abstraction stays.
///
/// Two feature areas are exposed:
///   1. SlimCode-side settings — <see cref="SettingsType"/> / <see cref="Options"/>
///   2. History / resumable sessions — <see cref="EnumerateSessions"/> and friends
/// </summary>
public abstract class Agent
{
   // -- identity -------------------------------------------------------------

   /// <summary>Stable id used in serialized state (e.g. "native").</summary>
   public abstract string Id { get; }

   /// <summary>Human-readable name shown in menus.</summary>
   public abstract string DisplayName { get; }

   /// <summary>
   /// True if this agent runs in-process. Every agent does today; the flag
   /// survives as the seam an out-of-process agent would have to declare
   /// itself through.
   /// </summary>
   public virtual bool IsNative => false;

   // -- settings -------------------------------------------------------------

   /// <summary>
   /// CLR type of this agent's SlimCode-side settings POCO. Properties marked
   /// with <see cref="SettingAttribute"/> are surfaced in the UI and persisted.
   /// Default: <see cref="EmptySettings"/> (no knobs).
   /// </summary>
   public virtual Type SettingsType => typeof(EmptySettings);

   /// <summary>
   /// Live settings instance. Owned by us; populated by <c>AgentSettingsStore</c>
   /// from JSON, with property defaults filling any gaps. Subclasses expose a
   /// strongly-typed view (e.g. <c>NativeAgent.Settings</c>) backed by this slot.
   /// </summary>
   public object SettingsObj { get; internal set; } = new EmptySettings();

   /// <summary>
   /// Reflected schema of <see cref="SettingsType"/>. Drives the auto-generated
   /// configuration UI. Cached per-Type; reading is cheap.
   /// </summary>
   public IReadOnlyList<SettingDescriptor> Options => SettingsReflector.For(SettingsType);

   // -- history / resumable sessions -----------------------------------------

   /// <summary>
   /// Enumerate resumable sessions the agent has stored on disk for the given
   /// project directory. Drives both session pickers (the sidebar rows and the
   /// full-page cards).
   ///
   /// Implementations:
   ///   - Map <paramref name="projectPath"/> to the agent's per-project history
   ///     location,
   ///   - Extract the first user prompt for the <see cref="AgentSession.Title"/>
   ///     and <see cref="AgentSession.PreviewText"/>,
   ///   - Set <see cref="AgentSession.SessionId"/> to the id the agent needs in
   ///     order to resume.
   ///
   /// Should be safe to call on every project selection: cap any per-file
   /// parse to the first N bytes / lines, return quickly if the history
   /// directory is missing. The UI may call this off the UI thread but does
   /// not require thread-safety beyond "no shared mutable state".
   ///
   /// Default: none.
   /// </summary>
   public virtual IReadOnlyList<AgentSession> EnumerateSessions(string projectPath)
      => System.Array.Empty<AgentSession>();

   /// <summary>
   /// Delete the on-disk record for one session belonging to this agent.
   /// Returns true on success. Default: false (not supported / no-op) — the
   /// UI uses the return value to decide whether to surface a "Delete" entry.
   ///
   /// Implementations resolve <paramref name="sessionId"/> through the same
   /// path mapping used by <see cref="EnumerateSessions"/> and remove the
   /// underlying file(s). Failures (file already gone, permission denied,
   /// unknown id) should return false rather than throw.
   /// </summary>
   public virtual bool DeleteSession(string projectPath, string sessionId) => false;

   /// <summary>
   /// Whether this agent supports deleting on-disk sessions. Drives whether the
   /// session-picker surfaces a "Delete" entry at all. Default: false — an agent
   /// opts in by overriding this together with <see cref="DeleteSession"/>.
   /// Kept separate from <see cref="DeleteSession"/> so the UI can probe the
   /// capability without performing (and thus needing to undo) a deletion.
   /// </summary>
   public virtual bool CanDeleteSessions => false;

   /// <summary>
   /// Set (or, with a blank caption, clear) the display name of one stored
   /// session. Returns true on success. Default: false (not supported).
   /// Guarded by <see cref="CanRenameSessions"/> the same way deletion is.
   /// </summary>
   public virtual bool RenameSession(string projectPath, string sessionId, string caption) => false;

   /// <summary>
   /// Whether this agent supports naming its stored sessions. Drives whether
   /// the session picker offers a "Rename" entry.
   /// </summary>
   public virtual bool CanRenameSessions => false;
}
