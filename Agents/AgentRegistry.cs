// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using SlimCodeDesktop.Agents.Native;

namespace SlimCodeDesktop.Agents;

/// <summary>
/// Static catalog of all known agents. SlimCode Agent is the only one today;
/// the registry stays because every consumer (launch menus, the sessions
/// pickers, the config tabs) is written against the list rather than against
/// one agent, so adding a second in-process agent needs no UI changes.
/// </summary>
public static class AgentRegistry
{
   private static readonly Agent[] _all =
   {
      new NativeAgent(),
   };

   public static IReadOnlyList<Agent> All => _all;

   public static Agent? FindById(string id) =>
      _all.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
