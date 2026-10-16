// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// In-process coding agent that talks directly to an OpenAI-compatible
/// chat-completions endpoint (llama.cpp, llama-swap, OpenAI, OpenRouter).
/// The host renders it through <c>NativeAgentView</c>, one per session tab.
///
/// Read-only "exploration" tool set today: read_file, list_dir, glob, grep.
/// </summary>
public sealed partial class NativeAgent : Agent
{
   // Id stays "native" — it's the persistence key for settings,
   // permissions, sessions, and audit logs. Changing it would orphan
   // every existing on-disk record.
   public override string Id            => "native";
   public override string DisplayName   => "SlimCode Agent";

   public override bool   IsNative      => true;

   public override Type   SettingsType  => typeof(NativeAgentSettings);

   /// <summary>Strongly-typed view of the persisted settings POCO.</summary>
   public NativeAgentSettings Settings => (NativeAgentSettings)SettingsObj;
}
