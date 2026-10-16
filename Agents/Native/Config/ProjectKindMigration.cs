// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Config;

/// <summary>
/// One-shot upgrade for projects created while the project kind still lived
/// only in <c>project.json</c> and could be flipped in the setup dialog.
///
/// Two things changed under those projects: the kind became part of the
/// sidebar list (it decides the storage slug, so it has to be readable before
/// any per-project file is opened), and an MCP project's storage moved to a
/// <c>-mcp</c> slug. This walks the list once, recovers the kind from the old
/// location, and moves the storage to where the new slug expects it.
///
/// Idempotent: a project whose kind is already recorded is skipped, and a move
/// whose destination exists is left alone rather than merged.
/// </summary>
public static class ProjectKindMigration
{
   /// <summary>Returns true when anything changed, i.e. the list needs saving.</summary>
   public static bool Run(ProjectList list)
   {
      bool dirty = false;
      var root = AppPaths.AppDataRootOrNull;
      if (root == null) return false;

      foreach (var path in list.AllProjectPaths.ToList())
      {
         if (list.Kinds.ContainsKey(path)) continue;

         var legacySlug = TranscriptStore.ProjectSlug(path, ProjectKind.Code);
         if (legacySlug.Length == 0) continue;
         if (_LegacyKind(root, legacySlug) != ProjectKind.Mcp) continue;

         list.SetKind(path, ProjectKind.Mcp);
         dirty = true;

         var newSlug = legacySlug + TranscriptStore.MCP_SLUG_SUFFIX;
         _MoveDir(Path.Combine(root, "projects", legacySlug),
                  Path.Combine(root, "projects", newSlug));
         _MoveDir(Path.Combine(root, "sessions", "native", legacySlug),
                  Path.Combine(root, "sessions", "native", newSlug));
      }

      return dirty;
   }

   // -- private ---------------------------------------------------------------

   // Read the kind straight out of the JSON rather than through
   // ProjectConfigStore: the store resolves its own path from the (new) slug,
   // which is precisely what hasn't happened yet here.
   private static ProjectKind _LegacyKind(string root, string slug)
   {
      var file = Path.Combine(root, "projects", slug, "project.json");
      if (!File.Exists(file)) return ProjectKind.Code;
      try
      {
         var node = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
         var kind = node?["kind"] ?? node?["Kind"];
         if (kind is not JsonValue v) return ProjectKind.Code;

         // Written either as a name ("Mcp") or, by the builds before the enum
         // converter landed, as the bare ordinal.
         if (v.TryGetValue<string>(out var s) &&
             Enum.TryParse<ProjectKind>(s, ignoreCase: true, out var parsed))
            return parsed;
         if (v.TryGetValue<int>(out var i) && Enum.IsDefined(typeof(ProjectKind), i))
            return (ProjectKind)i;
      }
      catch { }
      return ProjectKind.Code;
   }

   private static void _MoveDir(string from, string to)
   {
      try
      {
         if (!Directory.Exists(from) || Directory.Exists(to)) return;
         var parent = Path.GetDirectoryName(to);
         if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
         Directory.Move(from, to);
      }
      catch { /* leave the old folder in place; the project just starts empty */ }
   }
}
