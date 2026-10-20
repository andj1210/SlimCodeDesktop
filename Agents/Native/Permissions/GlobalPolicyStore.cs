// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// App-wide permission policy. Single JSON file at
/// <c>%APPDATA%\SlimCode\&lt;profile&gt;\global-permissions.json</c>. Intentionally decoupled
/// from <c>AppSettings</c> so the policy shape can evolve independently of
/// the reflection-driven settings UI.
///
/// Seeded with safe defaults when missing — see <see cref="BuildDefault"/>.
/// </summary>
public sealed class GlobalPolicyStore
{
   private GlobalPolicyStore(string filePath, PermissionPolicy policy)
   {
      _filePath = filePath;
      Policy     = policy;
   }

   public PermissionPolicy Policy { get; }
   public string FilePath => _filePath;

   /// <summary>
   /// The process-wide store. Deliberately a singleton: every caller mutates
   /// <see cref="Policy"/> then calls <see cref="Save"/>, and Save rewrites
   /// the whole file — so two instances alive at once (one per open session
   /// tab, plus the settings editor) meant the last writer silently dropped
   /// the other's grants. One instance also means a grant made in one tab is
   /// immediately visible to every other.
   /// </summary>
   public static GlobalPolicyStore LoadOrCreate()
   {
      lock (s_instanceLock)
      {
         return s_instance ??= _Load();
      }
   }

   private static GlobalPolicyStore _Load()
   {
      var path = DefaultPath();
      PermissionPolicy policy;

      if (File.Exists(path))
      {
         try
         {
            var json = File.ReadAllText(path);
            policy = JsonSerializer.Deserialize<PermissionPolicy>(json, s_jsonOpts) ?? BuildDefault();
         }
         catch
         {
            policy = BuildDefault();
         }
      }
      else
      {
         policy = BuildDefault();
      }
      return new GlobalPolicyStore(path, policy);
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
      catch { }
   }

   // -- defaults -------------------------------------------------------------

   /// <summary>
   /// Default global policy: read tools auto-allow, write + execute tools
   /// fall through to the user prompt. The policy is keyed by tool name —
   /// any tool the user hasn't seen yet inherits Prompt from
   /// <see cref="PermissionPolicy.Get"/>'s missing-key fallback.
   /// </summary>
   private static PermissionPolicy BuildDefault()
   {
      var p = new PermissionPolicy();
      // Known read tools — explicit Allow keeps the audit log readable
      // ("global policy: Allow" reads better than "default: Allow").
      p.Set("read_file",  PermissionDecision.Allow);
      p.Set("read_image", PermissionDecision.Allow);
      p.Set("list_dir",   PermissionDecision.Allow);
      p.Set("glob",       PermissionDecision.Allow);
      p.Set("grep",       PermissionDecision.Allow);
      // use_skill returns instruction text only (Read); a skill body can only
      // *instruct* the model — any write/build it suggests still hits the gate.
      p.Set("use_skill", PermissionDecision.Allow);
      return p;
   }

   public static string DefaultPath()
   {
      return Path.Combine(AppPaths.AppDataRoot, "global-permissions.json");
   }

   // -- fields ---------------------------------------------------------------

   private static GlobalPolicyStore? s_instance;
   private static readonly object    s_instanceLock = new();

   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
      WriteIndented               = true,
      Converters                  = { new JsonStringEnumConverter() },
   };

   private readonly string _filePath;
}
