// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Per-project permission storage. One JSON file per project lives at
/// <c>%APPDATA%\SlimCode\&lt;profile&gt;\projects\&lt;slug&gt;\permissions.json</c>.
/// Missing file = empty policy (every tool resolves to Prompt at this layer).
/// The slug is the same one used by <see cref="TranscriptStore.ProjectSlug"/>.
/// </summary>
public sealed class ProjectPolicyStore
{
   private ProjectPolicyStore(string filePath, PermissionPolicy policy)
   {
      _filePath = filePath;
      Policy     = policy;
   }

   public PermissionPolicy Policy { get; }
   public string FilePath => _filePath;

   /// <summary>
   /// The store for a project, shared process-wide. One instance per policy
   /// file: <see cref="Save"/> rewrites the file whole, so a second instance
   /// would silently drop grants made through the first (the permission
   /// editor and a running session's gate both hold one).
   /// </summary>
   public static ProjectPolicyStore LoadOrCreate(string projectPath)
   {
      var dir = ProjectMetadataDir(projectPath)
                ?? throw new InvalidOperationException("cannot resolve project metadata dir");
      var path = Path.Combine(dir, "permissions.json");

      lock (s_instanceLock)
      {
         if (s_instances.TryGetValue(path, out var existing)) return existing;
         var created = _Load(path);
         s_instances[path] = created;
         return created;
      }
   }

   private static ProjectPolicyStore _Load(string path)
   {
      PermissionPolicy policy = new();
      if (File.Exists(path))
      {
         try
         {
            var json = File.ReadAllText(path);
            policy = JsonSerializer.Deserialize<PermissionPolicy>(json, s_jsonOpts) ?? new();
         }
         catch
         {
            // Corrupt / partial file → start clean. We don't want a single
            // bad character to lock the agent out.
         }
      }
      return new ProjectPolicyStore(path, policy);
   }

   public void Save()
   {
      try
      {
         var dir = Path.GetDirectoryName(_filePath);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
         var json = JsonSerializer.Serialize(Policy, s_jsonOpts);
         File.WriteAllText(_filePath, json);
      }
      catch { /* a transient write failure must not break the agent */ }
   }

   // -- path helpers ---------------------------------------------------------

   /// <summary>
   /// <c>%APPDATA%\SlimCode\&lt;profile&gt;\projects\&lt;slug&gt;</c>. Returns null if APPDATA
   /// can't be resolved or the project path is empty.
   /// </summary>
   public static string? ProjectMetadataDir(string projectPath)
   {
      var root = AppPaths.AppDataRootOrNull;
      if (root == null) return null;
      var slug = TranscriptStore.ProjectSlug(projectPath);
      if (slug.Length == 0) return null;
      return Path.Combine(root, "projects", slug);
   }

   // -- fields ---------------------------------------------------------------

   // Keyed by policy-file path, not project path, so two spellings of the
   // same folder resolve to one store (the slug already normalises them).
   private static readonly Dictionary<string, ProjectPolicyStore> s_instances =
      new(StringComparer.OrdinalIgnoreCase);

   private static readonly object s_instanceLock = new();

   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
      WriteIndented               = true,
      Converters                  = { new JsonStringEnumConverter() },
   };

   private readonly string _filePath;
}
