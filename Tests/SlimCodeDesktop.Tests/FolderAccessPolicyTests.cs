// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using SlimCodeDesktop.Agents.Native.Tools;
using Xunit;

namespace SlimCodeDesktop.Tests;

/// <summary>
/// Tests for <see cref="FolderAccessPolicy"/>. Pure lookup over
/// project-relative paths; the semantics flag is passed explicitly, so the
/// Windows and Linux cases both run on any host.
/// </summary>
public sealed class FolderAccessPolicyTests
{
   // -- empty policy ---------------------------------------------------------

   [Theory]
   [InlineData("")]
   [InlineData("a.cs")]
   [InlineData("src/deep/a.cs")]
   public void Empty_EverythingIsFull(string path)
   {
      Assert.True(FolderAccessPolicy.Empty.IsEmpty);
      Assert.Equal(FolderAccess.Full, FolderAccessPolicy.Empty.AccessAt(path));
      Assert.False(FolderAccessPolicy.Empty.HasRestrictionBelow(path));
   }

   // -- segment matching (both semantics) ------------------------------------

   [Theory]
   [InlineData("secret",             FolderAccess.NoAccess)]
   [InlineData("secret/",            FolderAccess.NoAccess)]
   [InlineData("secret/key.txt",     FolderAccess.NoAccess)]
   [InlineData("secret/a/b/key.txt", FolderAccess.NoAccess)]
   [InlineData("./secret/key.txt",   FolderAccess.NoAccess)]
   [InlineData("secrets/x",          FolderAccess.Full)]      // shares the prefix only
   [InlineData("secret2",            FolderAccess.Full)]
   [InlineData("secre",              FolderAccess.Full)]
   [InlineData("src/secret/x",       FolderAccess.Full)]      // rules are anchored at the root
   [InlineData("",                   FolderAccess.Full)]
   public void SingleRule_MatchesWholeSegments(string path, FolderAccess expected)
   {
      foreach (var windows in s_bothSemantics)
      {
         var policy = _Policy(windows, ("secret", FolderAccess.NoAccess));

         Assert.Equal(expected, policy.AccessAt(path));
      }
   }

   [Theory]
   [InlineData("docs/a.md",           FolderAccess.ReadOnly)]
   [InlineData("docs/private/x.md",   FolderAccess.NoAccess)]   // deeper rule tightens
   [InlineData("docs/privatex/x.md",  FolderAccess.ReadOnly)]
   [InlineData("src/a.cs",            FolderAccess.Full)]
   public void NestedRules_DeeperTightens(string path, FolderAccess expected)
   {
      var policy = _Policy(false,
         ("docs",         FolderAccess.ReadOnly),
         ("docs/private", FolderAccess.NoAccess));

      Assert.Equal(expected, policy.AccessAt(path));
   }

   [Fact]
   public void NestedRules_DeeperCannotLoosen()
   {
      var policy = _Policy(false,
         ("vault",        FolderAccess.NoAccess),
         ("vault/public", FolderAccess.ReadOnly),
         ("vault/open",   FolderAccess.Full));

      Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("vault/public/x"));
      Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("vault/open/x"));
   }

   [Fact]
   public void DuplicateRules_MostRestrictiveWins()
   {
      var policy = _Policy(false,
         ("docs", FolderAccess.NoAccess),
         ("docs", FolderAccess.ReadOnly));

      Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("docs/x"));
   }

   [Fact]
   public void FullRule_IsNoOp()
   {
      var policy = _Policy(false, ("src", FolderAccess.Full));

      Assert.True(policy.IsEmpty);
   }

   [Theory]
   [InlineData("")]
   [InlineData(".")]
   [InlineData("./")]
   public void RootRule_AppliesEverywhere(string rootSpelling)
   {
      var policy = _Policy(false, (rootSpelling, FolderAccess.ReadOnly));

      Assert.Equal(FolderAccess.ReadOnly, policy.AccessAt(""));
      Assert.Equal(FolderAccess.ReadOnly, policy.AccessAt("src/a.cs"));
   }

   // -- rule normalization ---------------------------------------------------

   [Theory]
   [InlineData("docs/sub")]
   [InlineData("docs\\sub")]       // either separator in rules, on every OS
   [InlineData("./docs/sub/")]
   [InlineData("docs//sub")]
   [InlineData("docs/./sub")]
   [InlineData("  docs/sub  ")]
   public void RuleSpellings_AreEquivalent(string rule)
   {
      foreach (var windows in s_bothSemantics)
      {
         var policy = _Policy(windows, (rule, FolderAccess.NoAccess));

         Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("docs/sub/x"));
         Assert.Equal(FolderAccess.Full,     policy.AccessAt("docs/x"));
      }
   }

   [Theory]
   [InlineData("/abs")]
   [InlineData("\\abs")]
   [InlineData("..")]
   [InlineData("../x")]
   [InlineData("docs/../x")]
   public void InvalidRule_Throws(string rule)
   {
      foreach (var windows in s_bothSemantics)
         Assert.Throws<ArgumentException>(() => _Policy(windows, (rule, FolderAccess.NoAccess)));
   }

   [Fact]
   public void Windows_DriveRootedRule_Throws()
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Path.IsPathRooted follows the host OS");

      Assert.Throws<ArgumentException>(() => _Policy(true, (@"C:\x", FolderAccess.NoAccess)));
   }

   [Fact]
   public void NullRuleFolder_Throws()
   {
      Assert.Throws<ArgumentException>(() => _Policy(false, (null!, FolderAccess.NoAccess)));
   }

   [Fact]
   public void UnnormalizedPath_Throws()
   {
      var policy = _Policy(false, ("secret", FolderAccess.NoAccess));

      Assert.Throws<ArgumentException>(() => policy.AccessAt("x/../secret"));
   }

   // -- Windows vs. Linux semantics ------------------------------------------

   [Theory]
   [InlineData("SECRET/x",                           FolderAccess.NoAccess, FolderAccess.Full)]     // case
   [InlineData(@"secret\x",                          FolderAccess.NoAccess, FolderAccess.Full)]     // '\' is a name char on Linux
   [InlineData("secret::$INDEX_ALLOCATION/x",        FolderAccess.NoAccess, FolderAccess.Full)]     // NTFS directory stream
   [InlineData("secret:stream",                      FolderAccess.NoAccess, FolderAccess.Full)]     // stream on the folder itself
   [InlineData("secret./x",                          FolderAccess.NoAccess, FolderAccess.Full)]     // trailing dot dropped by Win32
   [InlineData("secret /x",                          FolderAccess.NoAccess, FolderAccess.Full)]     // trailing space dropped by Win32
   [InlineData("secrets/x",                          FolderAccess.Full,     FolderAccess.Full)]
   public void Semantics_Differ(string path, FolderAccess windows, FolderAccess linux)
   {
      Assert.Equal(windows, _Policy(true,  ("secret", FolderAccess.NoAccess)).AccessAt(path));
      Assert.Equal(linux,   _Policy(false, ("secret", FolderAccess.NoAccess)).AccessAt(path));
   }

   [Fact]
   public void Windows_RuleCaseDoesNotMatter()
   {
      var policy = _Policy(true, ("Secret", FolderAccess.NoAccess));

      Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("sEcReT/x"));
   }

   // -- restrictions below a path --------------------------------------------

   [Theory]
   [InlineData("",              true)]
   [InlineData("docs",          true)]
   [InlineData("docs/",         true)]
   [InlineData("docs/private",  false)]   // the rule itself, not below: AccessAt covers it
   [InlineData("docs/private/x",false)]
   [InlineData("doc",           false)]
   [InlineData("docsx",         false)]
   [InlineData("src",           false)]
   public void HasRestrictionBelow(string path, bool expected)
   {
      var policy = _Policy(false, ("docs/private", FolderAccess.ReadOnly));

      Assert.Equal(expected, policy.HasRestrictionBelow(path));
   }

   [Fact]
   public void HasRestrictionBelow_WindowsIgnoresCase()
   {
      Assert.True (_Policy(true,  ("docs/private", FolderAccess.NoAccess)).HasRestrictionBelow("DOCS"));
      Assert.False(_Policy(false, ("docs/private", FolderAccess.NoAccess)).HasRestrictionBelow("DOCS"));
   }

   // -- any-depth rules ------------------------------------------------------

   [Theory]
   [InlineData(".git",                 FolderAccess.NoAccess)]
   [InlineData(".git/config",          FolderAccess.NoAccess)]
   [InlineData("sub/module/.git",      FolderAccess.NoAccess)]   // gitfile of a submodule
   [InlineData("sub/module/.git/HEAD", FolderAccess.NoAccess)]
   [InlineData(".gitignore",           FolderAccess.Full)]
   [InlineData("src/.github/x",        FolderAccess.Full)]
   [InlineData("src/a.cs",             FolderAccess.Full)]
   [InlineData("",                     FolderAccess.Full)]
   public void AnyDepthRule_MatchesNameAnywhere(string path, FolderAccess expected)
   {
      foreach (var windows in s_bothSemantics)
      {
         var policy = new FolderAccessPolicy(new[] { new FolderRule(".git", FolderAccess.NoAccess, AnyDepth: true) }, windows);
         Assert.Equal(expected, policy.AccessAt(path));
      }
   }

   [Fact]
   public void AnyDepthRule_StrictestWinsAgainstAnchored()
   {
      var policy = new FolderAccessPolicy(new[]
      {
         new FolderRule("vendor", FolderAccess.ReadOnly),
         new FolderRule(".git",   FolderAccess.NoAccess, AnyDepth: true),
      }, false);

      Assert.Equal(FolderAccess.NoAccess, policy.AccessAt("vendor/lib/.git/x"));
      Assert.Equal(FolderAccess.ReadOnly, policy.AccessAt("vendor/lib/a.c"));
   }

   [Theory]
   [InlineData("a/.git")]
   [InlineData(".")]
   [InlineData("")]
   [InlineData("../.git")]
   public void AnyDepthRule_NotASingleName_Throws(string rule)
   {
      foreach (var windows in s_bothSemantics)
         Assert.Throws<ArgumentException>(() =>
            new FolderAccessPolicy(new[] { new FolderRule(rule, FolderAccess.NoAccess, AnyDepth: true) }, windows));
   }

   [Fact]
   public void AnyDepthRule_MatchesAnyDepthAndFlags()
   {
      var policy = new FolderAccessPolicy(new[] { new FolderRule(".git", FolderAccess.NoAccess, AnyDepth: true) }, true);

      Assert.False(policy.IsEmpty);
      Assert.True (policy.HasAnyDepthRules);
      Assert.True (policy.MatchesAnyDepth(".GIT"));
      Assert.True (policy.MatchesAnyDepth(".git."));       // trailing dot dropped by Win32
      Assert.False(policy.MatchesAnyDepth(".github"));
      Assert.False(policy.HasRestrictionBelow(""));        // disk-dependent, see ToolContext
      Assert.False(FolderAccessPolicy.Empty.HasAnyDepthRules);
   }

   [Fact]
   public void AnyDepthRule_FullIsNoOp()
   {
      var policy = new FolderAccessPolicy(new[] { new FolderRule(".git", FolderAccess.Full, AnyDepth: true) }, false);

      Assert.True (policy.IsEmpty);
      Assert.False(policy.MatchesAnyDepth(".git"));
   }

   // -- helpers --------------------------------------------------------------

   private static FolderAccessPolicy _Policy(bool windows, params (string Folder, FolderAccess Access)[] rules)
   {
      var list = new FolderRule[rules.Length];
      for (int i = 0; i < rules.Length; i++)
         list[i] = new FolderRule(rules[i].Folder, rules[i].Access);
      return new FolderAccessPolicy(list, windows);
   }

   // -- fields ---------------------------------------------------------------

   private static readonly bool[] s_bothSemantics = { true, false };
}
