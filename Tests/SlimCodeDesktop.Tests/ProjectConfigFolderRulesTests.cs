// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using Xunit;

namespace SlimCodeDesktop.Tests;

/// <summary>Tests for <see cref="ProjectConfig.FolderRules"/> and how a policy is built from them.</summary>
public sealed class ProjectConfigFolderRulesTests
{
   [Fact]
   public void Json_RoundTrip_KeepsRules()
   {
      var cfg = new ProjectConfig { FolderRules = ProjectConfig.DefaultFolderRules() };
      cfg.FolderRules.Add(new FolderRule("doc/private", FolderAccess.ReadOnly));

      var json = JsonSerializer.Serialize(cfg, s_jsonOpts);
      var back = JsonSerializer.Deserialize<ProjectConfig>(json, s_jsonOpts)!;

      Assert.Contains("\"NoAccess\"", json);
      Assert.Equal(cfg.FolderRules, back.FolderRules);
   }

   [Fact]
   public void Json_MissingAnyDepth_DefaultsToAnchored()
   {
      var json = """{ "FolderRules": [ { "Folder": "secret", "Access": "NoAccess" } ] }""";

      var cfg = JsonSerializer.Deserialize<ProjectConfig>(json, s_jsonOpts)!;

      Assert.Equal(new FolderRule("secret", FolderAccess.NoAccess), Assert.Single(cfg.FolderRules));
   }

   [Fact]
   public void Clone_CopiesRules()
   {
      var cfg   = new ProjectConfig { FolderRules = ProjectConfig.DefaultFolderRules() };
      var clone = cfg.Clone();

      clone.FolderRules.Clear();

      Assert.Single(cfg.FolderRules);
   }

   [Fact]
   public void DefaultRules_HideGitAtAnyDepth()
   {
      var policy = new ProjectConfig { FolderRules = ProjectConfig.DefaultFolderRules() }.BuildFolderPolicy();

      Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("sub/.git/HEAD"));
      Assert.Equal(FolderAccess.Full,     policy.AccessAt(".gitignore"));
   }

   [Fact]
   public void InvalidRule_LocksWholeProject()
   {
      var cfg = new ProjectConfig
      {
         FolderRules = new List<FolderRule> { new("../outside", FolderAccess.NoAccess) },
      };

      Assert.Equal(FolderAccess.NoAccess, cfg.BuildFolderPolicy().AccessAt("src/a.cs"));
   }

   [Fact]
   public void McpProject_IgnoresRules()
   {
      var cfg = new ProjectConfig
      {
         Kind        = ProjectKind.Mcp,
         FolderRules = ProjectConfig.DefaultFolderRules(),
      };

      Assert.Same(FolderAccessPolicy.Empty, cfg.BuildFolderPolicy());
   }

   // -- fields ---------------------------------------------------------------

   // Same options as ProjectConfigStore.
   private static readonly JsonSerializerOptions s_jsonOpts = new()
   {
      PropertyNameCaseInsensitive = true,
      WriteIndented               = true,
      Converters                  = { new JsonStringEnumConverter() },
   };
}
