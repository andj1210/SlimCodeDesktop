// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Loads and saves per-agent SlimCode-side settings as a single JSON file:
///   { "agents": { "qwen": { "lineEnd": "Translated" }, … } }
///
/// Each agent's section deserializes directly into its <see cref="Agent.SettingsType"/>
/// POCO; missing properties keep their POCO defaults. Property names match
/// case-insensitively, so a key <c>"lineend"</c> from older files still binds
/// to <c>LineEnd</c>. Enums round-trip as strings ("Native"/"Translated").
///
/// Sections for unknown agent ids are preserved verbatim across save cycles.
/// </summary>
public sealed class AgentSettingsStore
{
   private readonly string _filePath;
   // Raw sections kept as JsonElement so we never lose data for agents that
   // aren't currently registered. Reserialized straight back on Save().
   private Dictionary<string, JsonElement> _byAgent = new();

   private static readonly JsonSerializerOptions _jsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = true,
      Converters = { new JsonStringEnumConverter() },
   };

   public AgentSettingsStore(string filePath) { _filePath = filePath; }

   public static AgentSettingsStore Load(string filePath)
   {
      var s = new AgentSettingsStore(filePath);
      if (!File.Exists(filePath)) return s;
      try
      {
         var json = File.ReadAllText(filePath);
         var doc = JsonSerializer.Deserialize<Root>(json, _jsonOpts);
         if (doc?.Agents != null) s._byAgent = doc.Agents;
      }
      catch { }
      return s;
   }

   public void Save()
   {
      try
      {
         var dir = Path.GetDirectoryName(_filePath);
         if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
         var doc = new Root { Agents = _byAgent };
         File.WriteAllText(_filePath, JsonSerializer.Serialize(doc, _jsonOpts));
      }
      catch { }
   }

   /// <summary>
   /// Materialize the agent's typed settings POCO from the persisted section
   /// (or a fresh default instance if nothing is on disk yet) and assign it
   /// to <see cref="Agent.SettingsObj"/>. Returns the same instance.
   /// </summary>
   public object Apply(Agent agent)
   {
      object instance;
      if (_byAgent.TryGetValue(agent.Id, out var elem) && elem.ValueKind == JsonValueKind.Object)
      {
         try
         {
            instance = elem.Deserialize(agent.SettingsType, _jsonOpts)
                       ?? Activator.CreateInstance(agent.SettingsType)!;
         }
         catch
         {
            instance = Activator.CreateInstance(agent.SettingsType)!;
         }
      }
      else
      {
         instance = Activator.CreateInstance(agent.SettingsType)!;
      }
      // Run any per-agent migration that needs visibility into the raw JSON
      // shape (e.g. distinguishing "key absent" from "key set to default").
      if (instance is ISettingsMigration mig)
      {
         try
         {
            mig.Migrate(elem);
         }
         catch
         {
            // A migration failure must not block startup; the POCO defaults
            // are still a workable state.
         }
      }

      agent.SettingsObj = instance;
      // Re-serialize so the on-disk shape matches the POCO (fills in defaults
      // and migrates legacy key casing). Caller is expected to Save() afterwards.
      _byAgent[agent.Id] = JsonSerializer.SerializeToElement(instance, agent.SettingsType, _jsonOpts);
      return instance;
   }

   /// <summary>Update one property of an agent's settings and persist.</summary>
   public void Set(Agent agent, SettingDescriptor desc, object? value)
   {
      desc.SetValue(agent.SettingsObj, value);
      _byAgent[agent.Id] = JsonSerializer.SerializeToElement(agent.SettingsObj, agent.SettingsType, _jsonOpts);
      Save();
   }

   /// <summary>
   /// Re-serialize the agent's entire settings POCO and persist. Use when
   /// you mutate <see cref="Agent.SettingsObj"/> directly (e.g. modifying
   /// a list or a property not exposed via <c>[Setting]</c>) — a plain
   /// <see cref="Save"/> would only write the cached pre-mutation snapshot.
   /// </summary>
   public void SaveAgent(Agent agent)
   {
      _byAgent[agent.Id] = JsonSerializer.SerializeToElement(agent.SettingsObj, agent.SettingsType, _jsonOpts);
      Save();
   }

   private sealed class Root
   {
      public Dictionary<string, JsonElement> Agents { get; set; } = new();
   }
}
