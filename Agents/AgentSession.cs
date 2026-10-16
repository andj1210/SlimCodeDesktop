// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace SlimCodeDesktop.Agents;

/// <summary>
/// One resumable session record discovered for a project + agent.
///
/// Produced by <see cref="Agent.EnumerateSessions"/>. Carries everything the
/// session-picker UI needs to render a card and to resume the session — no
/// further file IO required at click time.
/// </summary>
/// <param name="Id">
/// Stable identifier as the agent itself uses it (typically the session
/// filename without extension, often a UUID). Round-trips back into
/// <paramref name="SessionId"/> and may be used as a dictionary key for
/// caching / deletion.
/// </param>
/// <param name="Title">
/// Single-line label for the session card. Convention: the first ~60-80
/// characters of the user's first prompt, with newlines collapsed; fall back
/// to the session id when no prompt is available.
/// </param>
/// <param name="PreviewText">
/// Multi-line snippet shown in the hover tooltip — typically the full text of
/// the first user prompt, capped at a few KB by the producer. May be empty.
/// </param>
/// <param name="LastUsed">
/// Most recent modification time of the session file, surfaced for sorting
/// and a relative-time label on the card ("2h ago", "yesterday", …).
/// </param>
/// <param name="SessionId">
/// The id to hand back to the agent in order to resume this session. Normally
/// identical to <paramref name="Id"/>; it stays a separate field so an agent
/// whose resume handle differs from its display/delete key doesn't have to
/// overload one of them.
/// </param>
/// <param name="ContextTokens">
/// Most recent prompt-context size in tokens (i.e. how full the model's
/// context window was on the last turn). Surfaced on the card so the user
/// can see at a glance how close a session is to its limit before resuming.
/// <c>0</c> = unknown / not surfaced by this agent.
/// </param>
/// <param name="Caption">
/// User-set (later: model-generated) session name, when one exists. Null =
/// none, in which case <paramref name="Title"/> holds the derived first-prompt
/// label. Producers that support captions must fold the caption into
/// <paramref name="Title"/> as well, so display code can just render Title;
/// this field exists so the UI can tell a real name from a derived one and
/// style them differently.
/// </param>
/// <param name="ParentId">
/// Session this one was forked from, or null. Display only: a fork carries a
/// full copy of what it inherited, so the parent's existence is never
/// required to open, resume or delete this session. An id whose session is
/// gone is not an error — see <see cref="SessionLineage"/>, which renders
/// such an orphan as a root.
/// </param>
/// <param name="ForkKind">
/// Short tag for what produced the fork ("revert", "regenerate"),
/// or null when this is not a fork. Display only.
/// </param>
public readonly record struct AgentSession(
   string Id,
   string Title,
   string PreviewText,
   DateTime LastUsed,
   string SessionId,
   int ContextTokens = 0,
   string? Caption = null,
   string? ParentId = null,
   string? ForkKind = null);
