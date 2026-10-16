// Copyright 2025 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlimCodeDesktop.Core
{
   public enum EntryKind { Project, Bracket }

   /// <summary>
   /// What a project's sessions are for. <see cref="Code"/> is the original
   /// behavior: the built-in sandboxed file tools plus any configured
   /// build/test commands. <see cref="Mcp"/> replaces that tool set entirely
   /// with whatever the project's configured MCP servers advertise.
   ///
   /// Declared here (rather than next to the per-project config that stores
   /// it) because it classifies a project, and Core is what both the sidebar
   /// and the agent side already depend on. Absent from JSON = Code, so every
   /// project that predates this stays a coding project.
   /// </summary>
   public enum ProjectKind { Code, Mcp }

   /// <summary>
   /// One row in the sidebar. A flat project uses <see cref="Path"/>; a bracket
   /// uses <see cref="Name"/>, <see cref="Expanded"/> and <see cref="Paths"/>.
   /// Two shapes share one class so the on-disk JSON stays a single flat list.
   /// </summary>
   public class ProjectEntry
   {
      public EntryKind Kind { get; set; }
      public string Path { get; set; } = "";
      public string Name { get; set; } = "";
      public bool Expanded { get; set; } = true;
      public List<string> Paths { get; set; } = new();
   }

   /// <summary>Persistent list of pinned project directories and brackets, stored as JSON.</summary>
   public class ProjectList
   {
      // -- public ---------------------------------------------------------------

      public List<ProjectEntry> Entries { get; } = new();

      /// <summary>
      /// Optional per-project display aliases, keyed by project path. Empty/missing
      /// means "use the folder name". Stored separately from <see cref="Entries"/>
      /// so it covers both top-level projects and bracketed children (which are
      /// bare path strings, not entries). Path is a unique key — adding the same
      /// project twice is intentionally unsupported.
      /// </summary>
      public Dictionary<string, string> Aliases { get; } =
         new(StringComparer.OrdinalIgnoreCase);

      /// <summary>
      /// Per-project kind, keyed by project path. Missing = <see cref="ProjectKind.Code"/>,
      /// so every project written before this stays a coding project. Stored
      /// here rather than only in the project's own config file because the
      /// kind is part of a project's *identity*: it picks the metadata slug
      /// (see <see cref="Agents.Native.Sessions.TranscriptStore.ProjectSlug"/>),
      /// which has to be resolvable before any per-project file is opened.
      ///
      /// Same keying as <see cref="Aliases"/> — a bare path — so it covers
      /// bracketed children too.
      /// </summary>
      public Dictionary<string, ProjectKind> Kinds { get; } =
         new(StringComparer.OrdinalIgnoreCase);

      /// <summary>
      /// The live list, for code too far from MainWindow to be handed it —
      /// specifically the slug helpers, which are called from deep inside the
      /// agent with nothing but a path. Safe as a static because there is
      /// exactly one project list per process; published by <see cref="Load"/>
      /// before any project can be opened, and an empty one until then (which
      /// resolves every path to Code, i.e. today's behaviour).
      /// </summary>
      public static ProjectList Current { get; private set; } = new();

      private static readonly JsonSerializerOptions _jsonOpts = new()
      {
         WriteIndented = true,
         Converters = { new JsonStringEnumConverter() },
      };

      /// <summary>Load from file, or return an empty list if the file doesn't exist.
      /// Reads both the current object format and the legacy formats: a bare
      /// array of entries, and the even older flat-string array.</summary>
      public static ProjectList Load(string filePath)
      {
         var list = new ProjectList();
         Current = list;
         if (!File.Exists(filePath))
            return list;

         try
         {
            var json = File.ReadAllText(filePath);
            var node = JsonNode.Parse(json);

            if (node is JsonArray arr)
            {
               // Legacy: a bare array of entries (or older bare-string paths).
               ReadEntries(arr, list);
            }
            else if (node is JsonObject root)
            {
               if (root["entries"] is JsonArray entries)
                  ReadEntries(entries, list);
               if (root["aliases"] is JsonObject aliases)
               {
                  foreach (var kv in aliases)
                  {
                     if (kv.Value is JsonValue av && av.TryGetValue<string>(out var alias) &&
                         !string.IsNullOrWhiteSpace(alias))
                        list.Aliases[kv.Key] = alias;
                  }
               }
               if (root["kinds"] is JsonObject kinds)
               {
                  foreach (var kv in kinds)
                  {
                     if (kv.Value is JsonValue kvv && kvv.TryGetValue<string>(out var name) &&
                         Enum.TryParse<ProjectKind>(name, ignoreCase: true, out var kind))
                        list.Kinds[kv.Key] = kind;
                  }
               }
            }
         }
         catch { }

         return list;
      }

      private static void ReadEntries(JsonArray arr, ProjectList list)
      {
         foreach (var item in arr)
         {
            if (item is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
               // Oldest format: bare path string.
               list.Entries.Add(new ProjectEntry { Kind = EntryKind.Project, Path = s });
            }
            else if (item is JsonObject obj)
            {
               var entry = JsonSerializer.Deserialize<ProjectEntry>(obj.ToJsonString(), _jsonOpts);
               if (entry != null) list.Entries.Add(entry);
            }
         }
      }

      public void Save(string filePath)
      {
         try
         {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
               Directory.CreateDirectory(dir);

            var root = new JsonObject
            {
               ["entries"] = JsonSerializer.SerializeToNode(Entries, _jsonOpts),
               ["aliases"] = JsonSerializer.SerializeToNode(Aliases, _jsonOpts),
               ["kinds"]   = JsonSerializer.SerializeToNode(Kinds, _jsonOpts),
            };
            File.WriteAllText(filePath, root.ToJsonString(_jsonOpts));
         }
         catch { }
      }

      // -- queries --------------------------------------------------------------

      /// <summary>Every project path in the list, top level and bracketed.</summary>
      public IEnumerable<string> AllProjectPaths =>
         Entries.Where(e => e.Kind == EntryKind.Project).Select(e => e.Path)
                .Concat(Entries.Where(e => e.Kind == EntryKind.Bracket).SelectMany(e => e.Paths));

      public IEnumerable<string> BracketNames =>
         Entries.Where(e => e.Kind == EntryKind.Bracket).Select(e => e.Name);

      /// <summary>The user-set alias for <paramref name="path"/>, or null if none.</summary>
      public string? GetAlias(string path) =>
         Aliases.TryGetValue(path, out var a) && !string.IsNullOrWhiteSpace(a) ? a : null;

      /// <summary>Set or clear (empty/whitespace) the display alias for a project path.</summary>
      public void SetAlias(string path, string? alias)
      {
         if (string.IsNullOrWhiteSpace(alias))
            Aliases.Remove(path);
         else
            Aliases[path] = alias.Trim();
      }

      /// <summary>Sidebar label for a project: its alias if set, else the folder name.</summary>
      public string DisplayLabel(string path) =>
         GetAlias(path) ?? Path.GetFileName(path);

      /// <summary>
      /// What kind of project lives at <paramref name="path"/>. Unknown paths
      /// answer <see cref="ProjectKind.Code"/>, which is both the pre-existing
      /// behaviour and the right answer for a folder nobody has classified.
      /// </summary>
      public ProjectKind GetKind(string path) =>
         Kinds.TryGetValue(path, out var k) ? k : ProjectKind.Code;

      /// <summary>
      /// Record a project's kind. Only ever called when a project is created:
      /// the kind decides where the project's metadata lives, so flipping it
      /// afterwards would orphan the config and sessions written under the old
      /// slug rather than convert them.
      /// </summary>
      public void SetKind(string path, ProjectKind kind)
      {
         if (kind == ProjectKind.Code) Kinds.Remove(path);
         else                          Kinds[path] = kind;
      }

      private static bool Eq(string a, string b) =>
         string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

      /// <summary>True when the list already holds this project, at top level
      /// or inside any bracket.</summary>
      public bool Contains(string path) => PathExistsAnywhere(path);

      private bool PathExistsAnywhere(string path) =>
         Entries.Any(e =>
            (e.Kind == EntryKind.Project && Eq(e.Path, path)) ||
            (e.Kind == EntryKind.Bracket && e.Paths.Any(p => Eq(p, path))));

      // -- mutations ------------------------------------------------------------

      /// <summary>
      /// Add a project of the given kind. Returns false when the path is
      /// already in the list — the path is the key, so the same folder cannot
      /// be added twice, not even once as Code and once as MCP. Callers say so
      /// out loud rather than letting the click do nothing.
      /// </summary>
      public bool AddProject(string path, ProjectKind kind = ProjectKind.Code)
      {
         if (PathExistsAnywhere(path)) return false;
         Entries.Add(new ProjectEntry { Kind = EntryKind.Project, Path = path });
         SetKind(path, kind);
         return true;
      }

      /// <summary>Remove a project everywhere — top level and inside any bracket.</summary>
      public void RemoveProject(string path)
      {
         Entries.RemoveAll(e => e.Kind == EntryKind.Project && Eq(e.Path, path));
         foreach (var b in Entries.Where(e => e.Kind == EntryKind.Bracket))
            b.Paths.RemoveAll(p => Eq(p, path));
      }

      public void AddBracket(string name)
      {
         if (Entries.Any(e => e.Kind == EntryKind.Bracket && Eq(e.Name, name))) return;
         Entries.Add(new ProjectEntry { Kind = EntryKind.Bracket, Name = name, Expanded = true });
      }

      /// <summary>Delete a bracket; its children are promoted back to top-level.</summary>
      public void RemoveBracket(string name)
      {
         var bracket = Entries.FirstOrDefault(e => e.Kind == EntryKind.Bracket && Eq(e.Name, name));
         if (bracket == null) return;
         foreach (var p in bracket.Paths)
            if (!PathExistsAnywhereExcept(p, bracket))
               Entries.Add(new ProjectEntry { Kind = EntryKind.Project, Path = p });
         Entries.Remove(bracket);
      }

      private bool PathExistsAnywhereExcept(string path, ProjectEntry ignore) =>
         Entries.Any(e => e != ignore && (
            (e.Kind == EntryKind.Project && Eq(e.Path, path)) ||
            (e.Kind == EntryKind.Bracket && e.Paths.Any(p => Eq(p, path)))));

      public void RenameBracket(string oldName, string newName)
      {
         if (Eq(oldName, newName)) return;
         if (Entries.Any(e => e.Kind == EntryKind.Bracket && Eq(e.Name, newName))) return;
         var b = Entries.FirstOrDefault(e => e.Kind == EntryKind.Bracket && Eq(e.Name, oldName));
         if (b != null) b.Name = newName;
      }

      public void SetBracketExpanded(string name, bool expanded)
      {
         var b = Entries.FirstOrDefault(e => e.Kind == EntryKind.Bracket && Eq(e.Name, name));
         if (b != null) b.Expanded = expanded;
      }

      /// <summary>Move a project into the named bracket, or back to top level when
      /// <paramref name="bracketName"/> is null.</summary>
      public void MoveProject(string path, string? bracketName)
      {
         Entries.RemoveAll(e => e.Kind == EntryKind.Project && Eq(e.Path, path));
         foreach (var b in Entries.Where(e => e.Kind == EntryKind.Bracket))
            b.Paths.RemoveAll(p => Eq(p, path));

         if (bracketName == null)
         {
            Entries.Add(new ProjectEntry { Kind = EntryKind.Project, Path = path });
            return;
         }

         var dest = Entries.FirstOrDefault(e => e.Kind == EntryKind.Bracket && Eq(e.Name, bracketName));
         if (dest == null)
         {
            dest = new ProjectEntry { Kind = EntryKind.Bracket, Name = bracketName, Expanded = true };
            Entries.Add(dest);
         }
         dest.Paths.Add(path);
      }
   }
}
