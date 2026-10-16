// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Loads and saves application-wide <see cref="AppSettings"/> as a single
/// JSON file. Mirrors the per-agent <see cref="AgentSettingsStore"/> machinery
/// but without the per-agent dimension — there is exactly one POCO instance.
/// </summary>
public sealed class AppSettingsStore
{
   private readonly string _filePath;
   public AppSettings Values { get; private set; } = new();

   private static readonly JsonSerializerOptions _jsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = true,
      Converters = { new JsonStringEnumConverter() },
   };

   /// <summary>
   /// The live store, for code too far from MainWindow to be handed it (the
   /// session view's clipboard wizard). Safe as a static because there is
   /// exactly one of these by design — see the class remarks — and it is
   /// published by <see cref="Load"/> before any view exists. Until then it
   /// holds a defaults-only instance, so a read can never be null.
   /// </summary>
   public static AppSettingsStore Current { get; private set; } = new(AppPaths.AppSettingsFile);

   public AppSettingsStore(string filePath) { _filePath = filePath; }

   public static AppSettingsStore Load(string filePath)
   {
      var s = new AppSettingsStore(filePath);
      Current = s;
      if (!File.Exists(filePath)) return s;
      try
      {
         var json = File.ReadAllText(filePath);
         s.Values = JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts) ?? new AppSettings();
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
         File.WriteAllText(_filePath, JsonSerializer.Serialize(Values, _jsonOpts));
      }
      catch { }
   }

   /// <summary>Update one [Setting] property and persist.</summary>
   public void Set(SettingDescriptor desc, object? value)
   {
      desc.SetValue(Values, value);
      Save();
   }

   /// <summary>Reflected schema of <see cref="AppSettings"/>.</summary>
   public System.Collections.Generic.IReadOnlyList<SettingDescriptor> Options =>
      SettingsReflector.For(typeof(AppSettings));
}
