// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Config;

/// <summary>
/// Per-project configuration storage. One JSON file per project at
/// <c>%APPDATA%\SlimCode\&lt;profile&gt;\projects\&lt;slug&gt;\project.json</c>, alongside the
/// permission policy. Missing file = empty config (no build/test commands).
/// </summary>
public sealed class ProjectConfigStore
{
   private ProjectConfigStore(string filePath, ProjectConfig config)
   {
      _filePath = filePath;
      Config     = config;
   }

   public ProjectConfig Config { get; }
   public string FilePath => _filePath;

   public static ProjectConfigStore LoadOrCreate(string projectPath)
   {
      var dir = ProjectPolicyStore.ProjectMetadataDir(projectPath)
                ?? throw new InvalidOperationException("cannot resolve project metadata dir");
      var path = Path.Combine(dir, "project.json");

      ProjectConfig config = new();
      if (File.Exists(path))
      {
         try
         {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<ProjectConfig>(json, s_jsonOpts) ?? new();
         }
         catch
         {
            // Corrupt / partial file → start clean rather than locking the user out.
         }
      }
      return new ProjectConfigStore(path, config);
   }

   public void Save()
   {
      try
      {
         var dir = Path.GetDirectoryName(_filePath);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
         var json = JsonSerializer.Serialize(Config, s_jsonOpts);
         File.WriteAllText(_filePath, json);
      }
      catch { /* a transient write failure must not break the agent */ }
   }

   // -- fields ---------------------------------------------------------------

   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
      WriteIndented               = true,
      // Project kind and MCP transport read as names rather than ordinals —
      // this file is routinely inspected (and occasionally hand-edited), and
      // "Stdio" survives a reordering of the enum where 0 would not. Reading
      // still accepts a bare number, so files written before this stay valid.
      Converters                  = { new JsonStringEnumConverter() },
   };

   private readonly string _filePath;
}
