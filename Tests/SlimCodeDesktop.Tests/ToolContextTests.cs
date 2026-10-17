// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SlimCodeDesktop.Agents.Native.Tools;
using Xunit;

namespace SlimCodeDesktop.Tests;

/// <summary>
/// Tests for <see cref="ToolContext.Resolve"/> -- the folder access gate
/// that keeps every file tool inside the project root.
///
/// Most cases are lexical: <see cref="Path.GetFullPath(string)"/> never
/// touches the disk, and the physical pass is skipped for a root that does
/// not exist, so a fake root (<c>F:\proj</c> / <c>/proj</c>) is enough.
/// The "real file system" sections build a temp tree with links.
/// </summary>
public sealed class ToolContextTests
{
   // -- construction ---------------------------------------------------------

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("   ")]
   public void Ctor_BlankRoot_Throws(string? root)
   {
      Assert.Throws<ArgumentException>(() => new ToolContext(root!, CancellationToken.None));
   }

   [Fact]
   public void Ctor_NormalizesRoot()
   {
      var ctx = new ToolContext(Path.Combine(s_root, "sub", ".."), CancellationToken.None);

      Assert.Equal(s_root, ctx.ProjectRoot);
   }

   [Fact]
   public void Ctor_TrailingSeparator_IsDropped()
   {
      var ctx = new ToolContext(s_root + Path.DirectorySeparatorChar, CancellationToken.None);

      Assert.Equal(s_root, ctx.ProjectRoot);
   }

   // -- inside the root (all platforms) --------------------------------------

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("   ")]
   public void BlankPath_ResolvesToRoot(string? path)
   {
      _AssertAllowed(s_root, _Resolve(path));
   }

   [Theory]
   [InlineData(".",                   "")]
   [InlineData("src/..",              "")]
   [InlineData("a.cs",                "a.cs")]
   [InlineData("src/a.cs",            "src/a.cs")]
   [InlineData("./src/../a.cs",       "a.cs")]
   [InlineData("src/sub/../../a.cs",  "a.cs")]
   [InlineData("does/not/exist.txt",  "does/not/exist.txt")]   // write tools need non-existent targets
   [InlineData("..x",                 "..x")]                  // a file name, not a traversal
   [InlineData("../proj/a.cs",        "a.cs")]                 // leaves and re-enters: judged after normalization
   public void RelativePath_Inside_Resolves(string path, string expectedRel)
   {
      _AssertAllowed(_Inside(expectedRel), _Resolve(path));
   }

   [Fact]
   public void AbsolutePath_Inside_Resolves()
   {
      var abs = Path.Combine(s_root, "src", "a.cs");

      _AssertAllowed(abs, _Resolve(abs));
   }

   // -- escaping the root (all platforms) ------------------------------------

   [Theory]
   [InlineData("..")]
   [InlineData("../")]
   [InlineData("../x")]
   [InlineData("./../x")]
   [InlineData("src/../../x")]
   [InlineData("a/b/../../../x")]
   [InlineData("../proj2/x")]          // sibling sharing the root's name as prefix
   [InlineData("../proj/../proj2/x")]
   public void RelativePath_Escaping_IsDenied(string path)
   {
      _AssertDenied(PathDenial.Escapes, _Resolve(path));
   }

   [Fact]
   public void AbsolutePath_SiblingWithSharedPrefix_IsDenied()
   {
      var sibling = Path.Combine(Path.GetDirectoryName(s_root)!, "proj2", "x");

      _AssertDenied(PathDenial.Escapes, _Resolve(sibling));
   }

   [Fact]
   public void AbsolutePath_FileSystemRoot_IsDenied()
   {
      _AssertDenied(PathDenial.Escapes, _Resolve(Path.GetPathRoot(s_root)));
   }

   [Fact]
   public void PathWithNullChar_IsInvalid()
   {
      // GetFullPath throws on '\0'; the gate must turn that into a denial.
      _AssertDenied(PathDenial.Invalid, _Resolve("a\0b"));
   }

   // -- root given with a trailing separator ---------------------------------

   [Fact]
   public void TrailingSeparatorRoot_ChildResolves()
   {
      var ctx = new ToolContext(s_root + Path.DirectorySeparatorChar, CancellationToken.None);

      _AssertAllowed(_Inside("a.cs"), ctx.Resolve("a.cs", PathAccess.Read));
   }

   [Fact]
   public void TrailingSeparatorRoot_DotResolvesToRoot()
   {
      // "<root>\." normalizes to "<root>" without the separator; the root
      // must be stored the same way for the two to compare equal.
      var ctx = new ToolContext(s_root + Path.DirectorySeparatorChar, CancellationToken.None);

      _AssertAllowed(s_root, ctx.Resolve(".", PathAccess.Read));
   }

   // -- access intent --------------------------------------------------------

   [Theory]
   [InlineData("src/a.cs", true)]
   [InlineData("",         true)]
   [InlineData("../x",     false)]
   public void WriteAccess_SameOutcomeAsRead_WithoutFolderRules(string path, bool allowed)
   {
      var ctx = _Ctx();

      var read  = ctx.Resolve(path, PathAccess.Read);
      var write = ctx.Resolve(path, PathAccess.Write);

      Assert.Equal(allowed,       read.IsAllowed);
      Assert.Equal(read.Denial,   write.Denial);
      Assert.Equal(read.FullPath, write.FullPath);
   }

   // -- folder rules ---------------------------------------------------------

   [Theory]
   [InlineData("secret/key.txt", PathAccess.Read,  PathDenial.NoAccess)]
   [InlineData("secret/key.txt", PathAccess.Write, PathDenial.NoAccess)]
   [InlineData("secret",         PathAccess.Read,  PathDenial.NoAccess)]
   [InlineData("docs/a.md",      PathAccess.Read,  PathDenial.None)]
   [InlineData("docs/a.md",      PathAccess.Write, PathDenial.ReadOnly)]
   [InlineData("docs",           PathAccess.Write, PathDenial.ReadOnly)]
   [InlineData("src/a.cs",       PathAccess.Write, PathDenial.None)]
   [InlineData("secrets/a.cs",   PathAccess.Write, PathDenial.None)]           // prefix only
   [InlineData("src/../secret",  PathAccess.Read,  PathDenial.NoAccess)]       // judged after normalization
   [InlineData("../proj/secret", PathAccess.Read,  PathDenial.NoAccess)]
   [InlineData("../x",           PathAccess.Read,  PathDenial.Escapes)]        // root containment comes first
   public void FolderRules_MatrixOfAccessAndLevel(string path, PathAccess access, PathDenial expected)
   {
      var res = _CtxWithRules().Resolve(path, access);

      Assert.Equal(expected, res.Denial);
   }

   [Fact]
   public void FolderRules_AbsolutePathInsideRestrictedFolder_IsDenied()
   {
      var abs = Path.Combine(s_root, "secret", "key.txt");

      _AssertDenied(PathDenial.NoAccess, _CtxWithRules().Resolve(abs, PathAccess.Read));
   }

   [Theory]
   [InlineData("")]                 // the root
   [InlineData("vault")]            // parent of a restricted folder
   public void FolderRules_WriteToAncestor_IsProtectedBelow(string path)
   {
      var ctx = _CtxWithRules();

      _AssertDenied(PathDenial.ProtectedBelow, ctx.Resolve(path, PathAccess.Write));
      Assert.True(ctx.Resolve(path, PathAccess.Read).IsAllowed);   // reading the ancestor stays fine
   }

   [Fact]
   public void FolderRules_RootRule_LocksEverything()
   {
      var ctx = new ToolContext(s_root, CancellationToken.None,
                                new FolderAccessPolicy(new[] { new FolderRule(".", FolderAccess.NoAccess) }));

      _AssertDenied(PathDenial.NoAccess, ctx.Resolve(null,       PathAccess.Read));
      _AssertDenied(PathDenial.NoAccess, ctx.Resolve("src/a.cs", PathAccess.Read));
   }

   [Fact]
   public void FolderRules_DefaultIsEmpty()
   {
      Assert.Same(FolderAccessPolicy.Empty, _Ctx().Folders);
   }

   [Fact]
   public void Windows_FolderRules_CaseAndSeparatorInsensitive()
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics");

      var ctx = _CtxWithRules();

      _AssertDenied(PathDenial.NoAccess, ctx.Resolve(@"SECRET\key.txt",                  PathAccess.Read));
      _AssertDenied(PathDenial.NoAccess, ctx.Resolve(@"F:\PROJ\Secret\key.txt",          PathAccess.Read));
      _AssertDenied(PathDenial.NoAccess, ctx.Resolve(@"secret::$INDEX_ALLOCATION\key",   PathAccess.Read));
   }

   // -- Windows semantics ----------------------------------------------------

   [Theory]
   [InlineData(@"src\a.cs",         @"F:\proj\src\a.cs")]
   [InlineData(@".\src\..\a.cs",    @"F:\proj\a.cs")]
   [InlineData(@"F:\PROJ\a.cs",     @"F:\proj\a.cs")]        // case-insensitive file system
   [InlineData(@"f:/proj/src/a.cs", @"F:\proj\src\a.cs")]    // forward slashes, lower-case drive
   [InlineData(@"a.cs:stream",      @"F:\proj\a.cs:stream")] // alternate data stream stays in the file
   public void Windows_Inside_Resolves(string path, string expected)
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics");

      var res = _Resolve(path);

      Assert.True(res.IsAllowed, "denied: " + res.Denial);
      Assert.Equal(expected, res.FullPath, ignoreCase: true);
   }

   [Theory]
   [InlineData(@"..\x")]
   [InlineData(@"src\..\..\x")]
   [InlineData(@"F:\proj2\x")]
   [InlineData(@"C:\Windows\System32")]
   [InlineData(@"\Windows")]                        // rooted on the current drive
   [InlineData(@"C:foo")]                           // drive-relative: C:'s current directory
   [InlineData(@"\\server\share\x")]                // UNC
   [InlineData(@"//server/share/x")]
   [InlineData(@"\\?\F:\proj\..\..\Windows")]       // extended-length: GetFullPath does not normalize
   [InlineData(@"\\?\F:\proj\a.cs")]                // ... so even inside targets are rejected
   [InlineData(@"\\.\F:\proj\a.cs")]                // device namespace
   public void Windows_Escaping_IsDenied(string path)
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics");

      // Escapes or Invalid -- what matters is that nothing gets through.
      Assert.False(_Resolve(path).IsAllowed);
   }

   [Fact]
   public void Windows_DriveRootProject_ParentClampsToRoot()
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics");

      var ctx = new ToolContext(@"F:\", CancellationToken.None);

      Assert.Equal(@"F:\", ctx.ProjectRoot);   // volume root keeps its separator
      _AssertAllowed(@"F:\",    ctx.Resolve("..",    PathAccess.Read));
      _AssertAllowed(@"F:\a\b", ctx.Resolve(@"a\b",  PathAccess.Read));
      _AssertDenied(PathDenial.Escapes, ctx.Resolve(@"C:\x", PathAccess.Read));
   }

   // -- Linux semantics ------------------------------------------------------

   [Theory]
   [InlineData(@"..\x",   @"/proj/..\x")]   // backslash is an ordinary file-name char
   [InlineData(@"C:\x",   @"/proj/C:\x")]   // not rooted on Linux
   public void Linux_Inside_Resolves(string path, string expected)
   {
      Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux path semantics");

      _AssertAllowed(expected, _Resolve(path));
   }

   [Theory]
   [InlineData("/PROJ/a.cs")]                // case-sensitive file system
   [InlineData("/etc/passwd")]
   [InlineData("//proj2/x")]
   public void Linux_Escaping_IsDenied(string path)
   {
      Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux path semantics");

      _AssertDenied(PathDenial.Escapes, _Resolve(path));
   }

   // -- ToRelative -----------------------------------------------------------

   [Fact]
   public void ToRelative_UsesForwardSlashes()
   {
      var abs = Path.Combine(s_root, "src", "a.cs");

      Assert.Equal("src/a.cs", _Ctx().ToRelative(abs));
   }

   [Fact]
   public void ToRelative_Root_IsDot()
   {
      Assert.Equal(".", _Ctx().ToRelative(s_root));
   }

   // -- real file system: physical pass --------------------------------------

   [Fact]
   public void RealTree_ExistingFile_Resolves()
   {
      using var tree = new TempTree();
      var file = tree.WriteProjectFile("a.cs", "");

      var ctx = new ToolContext(tree.Project, CancellationToken.None);

      _AssertAllowed(file, ctx.Resolve("a.cs", PathAccess.Read));
   }

   [Fact]
   public void RealTree_NotYetExistingPath_Resolves()
   {
      using var tree = new TempTree();
      var ctx = new ToolContext(tree.Project, CancellationToken.None);

      _AssertAllowed(tree.InProject("new/dir/a.cs"), ctx.Resolve("new/dir/a.cs", PathAccess.Write));
   }

   [Theory]
   [InlineData("link")]
   [InlineData("link/secret.txt")]
   [InlineData("link/does-not-exist.txt")]   // a write target behind the link
   public void RealTree_LinkPointingOutside_Escapes(string path)
   {
      using var tree = new TempTree();
      File.WriteAllText(Path.Combine(tree.Outside, "secret.txt"), "secret");
      tree.LinkDirectory(tree.InProject("link"), tree.Outside);

      var ctx = new ToolContext(tree.Project, CancellationToken.None);

      _AssertDenied(PathDenial.Escapes, ctx.Resolve(path, PathAccess.Read));
      _AssertDenied(PathDenial.Escapes, ctx.Resolve(path, PathAccess.Write));
   }

   [Fact]
   public void RealTree_LinkIntoRestrictedFolder_FollowsTheTargetsRules()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("secret/key.txt", "k");
      tree.WriteProjectFile("docs/a.md",      "d");
      tree.LinkDirectory(tree.InProject("to-secret"), tree.InProject("secret"));
      tree.LinkDirectory(tree.InProject("to-docs"),   tree.InProject("docs"));

      var ctx = _RealCtxWithRules(tree);

      _AssertDenied(PathDenial.NoAccess, ctx.Resolve("to-secret/key.txt", PathAccess.Read));
      Assert.True(ctx.Resolve("to-docs/a.md", PathAccess.Read).IsAllowed);
      _AssertDenied(PathDenial.ReadOnly, ctx.Resolve("to-docs/a.md",  PathAccess.Write));
      _AssertDenied(PathDenial.ReadOnly, ctx.Resolve("to-docs/new.md", PathAccess.Write));
   }

   [Fact]
   public void RealTree_DanglingLink_IsUnresolved()
   {
      // Writing through a dangling link would create its target wherever
      // it points -- possibly outside the project.
      using var tree = new TempTree();
      tree.LinkDirectory(tree.InProject("dangling"), Path.Combine(tree.Outside, "missing"));

      var ctx = new ToolContext(tree.Project, CancellationToken.None);

      _AssertDenied(PathDenial.Unresolved, ctx.Resolve("dangling/x.txt", PathAccess.Write));
   }

   [Fact]
   public void Windows_RealTree_ShortName_FollowsLongNameRules()
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names are a Windows thing");

      using var tree = new TempTree();
      tree.WriteProjectFile("secretfolder-long-name/key.txt", "k");
      var shortAbs = _ShortPath(tree.InProject("secretfolder-long-name"));
      var shortName = Path.GetFileName(shortAbs);
      Assert.SkipWhen(shortName.Equals("secretfolder-long-name", StringComparison.OrdinalIgnoreCase),
                      "8.3 name generation is disabled on the temp volume");

      var ctx = new ToolContext(tree.Project, CancellationToken.None,
                                new FolderAccessPolicy(new[] { new FolderRule("secretfolder-long-name", FolderAccess.NoAccess) }));

      _AssertDenied(PathDenial.NoAccess, ctx.Resolve(shortName + @"\key.txt", PathAccess.Read));
   }

   // -- real file system: walker ---------------------------------------------

   [Fact]
   public void ListVisible_HidesNoAccessAndOutsideLinks()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("src/a.cs",       "");
      tree.WriteProjectFile("docs/a.md",      "");
      tree.WriteProjectFile("secret/key.txt", "");
      tree.WriteProjectFile("top.txt",        "");
      tree.LinkDirectory(tree.InProject("out-link"), tree.Outside);
      tree.LinkDirectory(tree.InProject("in-link"),  tree.InProject("src"));

      var names = _Names(_RealCtxWithRules(tree).ListVisible(tree.Project));

      Assert.Equal(new[] { "docs", "in-link", "src", "top.txt" }, names);
   }

   [Fact]
   public void ListVisible_ReportsDirectoriesAndLinks()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("src/a.cs", "");
      tree.LinkDirectory(tree.InProject("in-link"), tree.InProject("src"));

      var entries = new ToolContext(tree.Project, CancellationToken.None).ListVisible(tree.Project);

      var src  = Assert.Single(entries, e => e.Name == "src");
      var link = Assert.Single(entries, e => e.Name == "in-link");
      Assert.True (src.IsDirectory);
      Assert.False(src.IsLink);
      Assert.True (link.IsDirectory);
      Assert.True (link.IsLink);
   }

   [Fact]
   public void EnumerateFilesRecursive_SkipsNoAccessLinksAndFilteredDirs()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("src/a.cs",            "");
      tree.WriteProjectFile("src/deep/b.cs",       "");
      tree.WriteProjectFile("docs/a.md",           "");
      tree.WriteProjectFile("secret/key.txt",      "");
      tree.WriteProjectFile("docs/private/p.md",   "");
      tree.WriteProjectFile("bin/out.dll",         "");
      File.WriteAllText(Path.Combine(tree.Outside, "leak.txt"), "");
      tree.LinkDirectory(tree.InProject("out-link"), tree.Outside);
      tree.LinkDirectory(tree.InProject("in-link"),  tree.InProject("src"));

      var ctx = new ToolContext(tree.Project, CancellationToken.None, new FolderAccessPolicy(new[]
      {
         new FolderRule("secret",       FolderAccess.NoAccess),
         new FolderRule("docs/private", FolderAccess.NoAccess),
      }));

      var files = new List<string>();
      foreach (var f in ctx.EnumerateFilesRecursive(tree.Project, name => name == "bin"))
         files.Add(ctx.ToRelative(f));
      files.Sort(StringComparer.Ordinal);

      Assert.Equal(new[] { "docs/a.md", "src/a.cs", "src/deep/b.cs" }, files);
   }

   [Fact]
   public void EnumerateFilesRecursive_StartInsideLinkToRestricted_YieldsNothing()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("secret/sub/key.txt", "");
      tree.LinkDirectory(tree.InProject("to-secret"), tree.InProject("secret"));

      var ctx = _RealCtxWithRules(tree);

      Assert.Empty(ctx.EnumerateFilesRecursive(tree.InProject("to-secret")));
   }

   // -- any-depth rules on disk ----------------------------------------------

   [Fact]
   public void AnyDepth_WriteToFolderContainingMatch_IsProtectedBelow()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("src/a.cs",              "");
      tree.WriteProjectFile("lib/mod/.git",          "gitdir: x");   // submodule gitfile
      tree.WriteProjectFile("other/.git/HEAD",       "");
      tree.WriteProjectFile("plain/b.cs",            "");
      var ctx = _AnyDepthCtx(tree);

      _AssertDenied(PathDenial.ProtectedBelow, ctx.Resolve("lib",       PathAccess.Write));
      _AssertDenied(PathDenial.ProtectedBelow, ctx.Resolve("lib/mod",   PathAccess.Write));
      _AssertDenied(PathDenial.ProtectedBelow, ctx.Resolve("other",     PathAccess.Write));
      _AssertDenied(PathDenial.ProtectedBelow, ctx.Resolve("",          PathAccess.Write));
      _AssertDenied(PathDenial.NoAccess,       ctx.Resolve("lib/mod/.git", PathAccess.Read));
      Assert.True(ctx.Resolve("plain",     PathAccess.Write).IsAllowed);
      Assert.True(ctx.Resolve("src/a.cs",  PathAccess.Write).IsAllowed);
      Assert.True(ctx.Resolve("new/dir",   PathAccess.Write).IsAllowed);
      Assert.True(ctx.Resolve("lib",       PathAccess.Read).IsAllowed);
   }

   [Fact]
   public void AnyDepth_LinkedFolderIsNotScanned()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("repo/.git/HEAD", "");
      tree.LinkDirectory(tree.InProject("to-repo"), tree.InProject("repo"));
      var ctx = _AnyDepthCtx(tree);

      // Writing the link (delete / move) only touches the link itself.
      Assert.True(ctx.Resolve("to-repo", PathAccess.Write).IsAllowed);
   }

   [Fact]
   public void AnyDepth_ListingHidesMatches()
   {
      using var tree = new TempTree();
      tree.WriteProjectFile("a/.git/HEAD", "");
      tree.WriteProjectFile("a/b.cs",      "");
      var ctx = _AnyDepthCtx(tree);

      Assert.Equal(new[] { "b.cs" }, _Names(ctx.ListVisible(tree.InProject("a"))));
   }

   // -- helpers --------------------------------------------------------------

   private static ToolContext _AnyDepthCtx(TempTree tree) =>
      new(tree.Project, CancellationToken.None, new FolderAccessPolicy(new[]
      {
         new FolderRule(".git", FolderAccess.NoAccess, AnyDepth: true),
      }));

   private static ToolContext _Ctx() => new(s_root, CancellationToken.None);

   private static PathResolution _Resolve(string? path) => _Ctx().Resolve(path, PathAccess.Read);

   /// <summary>secret = NoAccess, docs = ReadOnly, vault/inner = ReadOnly.</summary>
   private static ToolContext _CtxWithRules() =>
      new(s_root, CancellationToken.None, new FolderAccessPolicy(new[]
      {
         new FolderRule("secret",      FolderAccess.NoAccess),
         new FolderRule("docs",        FolderAccess.ReadOnly),
         new FolderRule("vault/inner", FolderAccess.ReadOnly),
      }));

   private static string _Inside(string rel) =>
      rel.Length == 0
         ? s_root
         : Path.Combine(s_root, rel.Replace('/', Path.DirectorySeparatorChar));

   private static void _AssertAllowed(string expectedFullPath, PathResolution res)
   {
      Assert.True(res.IsAllowed, "denied: " + res.Denial);
      Assert.Equal(expectedFullPath, res.FullPath);
   }

   private static void _AssertDenied(PathDenial expected, PathResolution res)
   {
      Assert.Equal(expected, res.Denial);
      Assert.Null(res.FullPath);
   }

   /// <summary>Same rules as <see cref="_CtxWithRules"/>, rooted in a real temp tree.</summary>
   private static ToolContext _RealCtxWithRules(TempTree tree) =>
      new(tree.Project, CancellationToken.None, new FolderAccessPolicy(new[]
      {
         new FolderRule("secret",      FolderAccess.NoAccess),
         new FolderRule("docs",        FolderAccess.ReadOnly),
         new FolderRule("vault/inner", FolderAccess.ReadOnly),
      }));

   private static string[] _Names(IReadOnlyList<VisibleEntry> entries)
   {
      var names = new string[entries.Count];
      for (int i = 0; i < names.Length; i++)
         names[i] = entries[i].Name;
      Array.Sort(names, StringComparer.Ordinal);
      return names;
   }

   private static string _ShortPath(string longPath)
   {
      var buf = new StringBuilder(1024);
      var len = GetShortPathNameW(longPath, buf, (uint)buf.Capacity);
      return len == 0 ? longPath : buf.ToString();
   }

   [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
   private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

   // -- fields ---------------------------------------------------------------

   private static readonly string s_root = OperatingSystem.IsWindows() ? @"F:\proj" : "/proj";
}
