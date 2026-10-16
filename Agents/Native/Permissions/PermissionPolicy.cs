// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// One layer's allow/deny table, keyed by tool name. A missing key resolves
/// to <see cref="PermissionDecision.Prompt"/> ("this layer has no opinion").
/// Plain dictionary instead of strong-typed properties so adding a tool is
/// configuration-only.
///
/// Serialized as plain JSON:
/// <code>{ "tools": { "write_file": "Allow", "bash": "Prompt" } }</code>
/// </summary>
public sealed class PermissionPolicy
{
   /// <summary>
   /// The allow/deny table. Treated as immutable once published: every
   /// mutation builds a fresh dictionary and swaps the reference in. The
   /// stores are process-wide singletons, so a gate check running on a tool
   /// call's background thread can be reading this table while the permission
   /// editor rewrites it on the UI thread — copy-on-write makes that safe
   /// without a lock on the (very hot) read path.
   /// </summary>
   [JsonPropertyName("tools")]
   public Dictionary<string, PermissionDecision> Tools { get; set; } =
      new(StringComparer.OrdinalIgnoreCase);

   /// <summary>Lookup with safe fallback when the tool isn't listed.</summary>
   public PermissionDecision Get(string tool)
   {
      // Read the reference once — a concurrent writer may swap it mid-call,
      // and the instance we grabbed is never mutated after publication.
      var snapshot = Tools;
      return snapshot.TryGetValue(tool, out var d) ? d : PermissionDecision.Prompt;
   }

   /// <summary>Idempotent set; pass <see cref="PermissionDecision.Prompt"/> to clear.</summary>
   public void Set(string tool, PermissionDecision decision)
   {
      lock (_writeLock)
      {
         var next = new Dictionary<string, PermissionDecision>(Tools, StringComparer.OrdinalIgnoreCase);
         if (decision == PermissionDecision.Prompt)
            next.Remove(tool);
         else
            next[tool] = decision;
         Tools = next;
      }
   }

   /// <summary>
   /// Swap in a whole new table in one step — what the permission editor does
   /// on save. Clearing and refilling <see cref="Tools"/> in place would leave
   /// a live gate reading a half-written dictionary.
   /// </summary>
   public void Replace(IReadOnlyDictionary<string, PermissionDecision> tools)
   {
      lock (_writeLock)
      {
         Tools = new Dictionary<string, PermissionDecision>(tools, StringComparer.OrdinalIgnoreCase);
      }
   }

   private readonly object _writeLock = new();
}
