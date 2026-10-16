// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;

namespace SlimCodeDesktop.Agents.Settings;

/// <summary>
/// Optional hook for settings POCOs that need to inspect their raw JSON
/// shape (e.g. to detect whether a key was absent vs. set to its default
/// value) and migrate themselves after deserialization. Invoked by
/// <c>AgentSettingsStore.Apply</c> once the instance has been deserialized,
/// before the in-memory snapshot is refreshed.
/// </summary>
public interface ISettingsMigration
{
   /// <summary>
   /// Run any one-time migrations against the raw stored shape. The
   /// <paramref name="raw"/> element is the on-disk JSON object for this
   /// agent (or <c>default</c> when this is a fresh install). Implementations
   /// should be idempotent — Apply may be called repeatedly across a session.
   /// </summary>
   void Migrate(JsonElement raw);
}
