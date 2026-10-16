// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Tools;
using Xunit;

namespace SlimCodeDesktop.Tests;

/// <summary>
/// Contract tests: every file tool goes through the gate. Each test runs a
/// real tool against a temp project with folder rules and an outside link,
/// and checks both the tool's answer and that the disk is untouched.
///
/// Layout (rules in brackets):
/// <code>
///   project/src/a.cs
///   project/docs/a.md            [docs: ReadOnly]
///   project/secret/key.txt       [secret: NoAccess]
///   project/vault/inner/v.txt    [vault/inner: ReadOnly]
///   project/link  -> outside/    (junction / symlink)
///   outside/leak.txt
/// </code>
/// Every file contains <see cref="MARK"/> so grep can show what it reached.
/// </summary>
public sealed class FileToolSandboxTests : IDisposable
{
   public FileToolSandboxTests()
   {
      _tree = new TempTree();
      _tree.WriteProjectFile("src/a.cs",          MARK + " src");
      _tree.WriteProjectFile("docs/a.md",         MARK + " docs");
      _tree.WriteProjectFile("secret/key.txt",    MARK + " secret");
      _tree.WriteProjectFile("vault/inner/v.txt", MARK + " vault");
      File.WriteAllText(Path.Combine(_tree.Outside, "leak.txt"), MARK + " outside");
      _tree.LinkDirectory(_tree.InProject("link"), _tree.Outside);

      _ctx = new ToolContext(_tree.Project, CancellationToken.None, new FolderAccessPolicy(new[]
      {
         new FolderRule("secret",      FolderAccess.NoAccess),
         new FolderRule("docs",        FolderAccess.ReadOnly),
         new FolderRule("vault/inner", FolderAccess.ReadOnly),
      }));
   }

   public void Dispose() => _tree.Dispose();

   // -- guard: no file tool slips past these tests ---------------------------

   [Fact]
   public void EveryToolTakingAPath_IsCoveredHere()
   {
      var uncovered = new List<string>();
      foreach (var type in typeof(ITool).Assembly.GetTypes())
      {
         if (type.IsAbstract || type.IsInterface || !typeof(ITool).IsAssignableFrom(type))
            continue;
         if (s_covered.Contains(type))
            continue;
         if (_TakesPath(type))
            uncovered.Add(type.Name);
      }

      Assert.True(uncovered.Count == 0,
         "Tools with path arguments but no sandbox tests: " + string.Join(", ", uncovered));
   }

   // -- read_file ------------------------------------------------------------

   [Theory]
   [InlineData("secret/key.txt")]
   [InlineData("link/leak.txt")]
   [InlineData("../outside/leak.txt")]
   public async Task ReadFile_Denied(string path)
   {
      var res = await _Run(new ReadFileTool(), new { path });

      _AssertDenied(res);
   }

   [Theory]
   [InlineData("docs/a.md")]          // ReadOnly still reads
   [InlineData("vault/inner/v.txt")]
   [InlineData("src/a.cs")]
   public async Task ReadFile_Allowed(string path)
   {
      var res = await _Run(new ReadFileTool(), new { path });

      Assert.False(res.IsError, res.Content);
      Assert.Contains(MARK, res.Content);
   }

   // -- write_file -----------------------------------------------------------

   [Theory]
   [InlineData("docs/new.md")]
   [InlineData("docs/a.md")]
   [InlineData("secret/new.txt")]
   [InlineData("vault/inner/new.txt")]
   [InlineData("link/new.txt")]
   public async Task WriteFile_Denied_DiskUntouched(string path)
   {
      var before = _Snapshot();

      var res = await _Run(new WriteFileTool(), new { path, content = "x" });

      _AssertDenied(res);
      Assert.Equal(before, _Snapshot());
   }

   [Fact]
   public async Task WriteFile_Allowed()
   {
      var res = await _Run(new WriteFileTool(), new { path = "src/new.cs", content = "x" });

      Assert.False(res.IsError, res.Content);
      Assert.True(File.Exists(_tree.InProject("src/new.cs")));
   }

   // -- edit_file ------------------------------------------------------------

   [Theory]
   [InlineData("docs/a.md")]
   [InlineData("secret/key.txt")]
   [InlineData("link/leak.txt")]
   public async Task EditFile_Denied_DiskUntouched(string path)
   {
      var before = _Snapshot();

      var res = await _Run(new EditFileTool(), new { path, old_string = MARK, new_string = "EDITED" });

      _AssertDenied(res);
      Assert.Equal(before, _Snapshot());
   }

   // -- delete_file ----------------------------------------------------------

   [Theory]
   [InlineData("docs/a.md")]
   [InlineData("docs")]
   [InlineData("secret")]
   [InlineData("vault")]              // contains a ReadOnly folder
   [InlineData("link")]               // lands outside
   [InlineData("link/leak.txt")]
   public async Task DeleteFile_Denied_DiskUntouched(string path)
   {
      var before = _Snapshot();

      var res = await _Run(new DeleteFileTool(), new { path, recursive = true });

      _AssertDenied(res);
      Assert.Equal(before, _Snapshot());
   }

   // -- move_file ------------------------------------------------------------

   [Theory]
   [InlineData("src/a.cs",      "docs/a.cs")]        // into ReadOnly
   [InlineData("docs/a.md",     "src/a.md")]         // out of ReadOnly
   [InlineData("src/a.cs",      "secret/a.cs")]      // into NoAccess
   [InlineData("src/a.cs",      "link/a.cs")]        // out through the link
   [InlineData("link/leak.txt", "src/leak.txt")]     // in through the link
   [InlineData("vault",         "vault2")]           // carries a ReadOnly folder along
   public async Task MoveFile_Denied_DiskUntouched(string source, string destination)
   {
      var before = _Snapshot();

      var res = await _Run(new MoveFileTool(), new { source, destination });

      _AssertDenied(res);
      Assert.Equal(before, _Snapshot());
   }

   // -- copy_file ------------------------------------------------------------

   [Theory]
   [InlineData("src/a.cs",            "docs/a.cs")]          // into ReadOnly
   [InlineData("src/a.cs",            "vault/inner/a.cs")]   // into nested ReadOnly
   [InlineData("src/a.cs",            "secret/a.cs")]        // into NoAccess
   [InlineData("secret/key.txt",      "src/key.txt")]        // out of NoAccess
   [InlineData("secret",              "src/secret")]         // NoAccess folder as a whole
   [InlineData("src/a.cs",            "link/a.cs")]          // out through the link
   [InlineData("link/leak.txt",       "src/leak.txt")]       // in through the link
   [InlineData("link",                "src/leak")]           // the link itself
   [InlineData("../outside/leak.txt", "src/leak.txt")]
   [InlineData("src",                 "src/sub")]            // into itself
   public async Task CopyFile_Denied_DiskUntouched(string source, string destination)
   {
      var before = _Snapshot();

      var res = await _Run(new CopyFileTool(), new { source, destination });

      _AssertDenied(res);
      Assert.Equal(before, _Snapshot());
   }

   [Fact]
   public async Task CopyFile_OutOfReadOnly_Allowed()
   {
      var res = await _Run(new CopyFileTool(), new { source = "docs/a.md", destination = "src/a.md" });

      Assert.False(res.IsError, res.Content);
      Assert.Equal(MARK + " docs", File.ReadAllText(_tree.InProject("src/a.md")));
      Assert.True(File.Exists(_tree.InProject("docs/a.md")));
   }

   [Fact]
   public async Task CopyFile_Directory_CarriesReadOnlyTreeAlong()
   {
      // Unlike move, the source is left in place, so its rules stay intact.
      var res = await _Run(new CopyFileTool(), new { source = "vault", destination = "vault2" });

      Assert.False(res.IsError, res.Content);
      Assert.Equal(MARK + " vault", File.ReadAllText(_tree.InProject("vault2/inner/v.txt")));
      Assert.True(File.Exists(_tree.InProject("vault/inner/v.txt")));
   }

   [Fact]
   public async Task CopyFile_ExistingDestination_NeedsOverwrite()
   {
      _tree.WriteProjectFile("src/b.cs", "old");

      var refused = await _Run(new CopyFileTool(), new { source = "src/a.cs", destination = "src/b.cs" });
      Assert.True(refused.IsError, refused.Content);
      Assert.Equal("old", File.ReadAllText(_tree.InProject("src/b.cs")));

      var res = await _Run(new CopyFileTool(), new { source = "src/a.cs", destination = "src/b.cs", overwrite = true });
      Assert.False(res.IsError, res.Content);
      Assert.Equal(MARK + " src", File.ReadAllText(_tree.InProject("src/b.cs")));
   }

   [Fact]
   public async Task CopyFile_Directory_LeavesNoAccessAndLinksBehind_KeepsEmptyDirs()
   {
      _tree.WriteProjectFile("src/private/p.txt", MARK + " private");
      Directory.CreateDirectory(_tree.InProject("src/empty"));
      _tree.LinkDirectory(_tree.InProject("src/out"), _tree.Outside);
      var ctx = new ToolContext(_tree.Project, CancellationToken.None, new FolderAccessPolicy(new[]
      {
         new FolderRule("src/private", FolderAccess.NoAccess),
      }));

      var res = await new CopyFileTool().ExecuteAsync(
         JsonSerializer.SerializeToElement(new { source = "src", destination = "src_copy" }), ctx);

      Assert.False(res.IsError, res.Content);
      Assert.True(File.Exists(_tree.InProject("src_copy/a.cs")));
      Assert.True(Directory.Exists(_tree.InProject("src_copy/empty")));
      Assert.False(Directory.Exists(_tree.InProject("src_copy/private")));
      Assert.False(Directory.Exists(_tree.InProject("src_copy/out")));
      Assert.Contains("skipped 2", res.Content);
   }

   [Fact]
   public async Task CopyFile_Directory_OverCap_DiskUntouched()
   {
      var big = Directory.CreateDirectory(_tree.InProject("big")).FullName;
      for (int i = 0; i <= CopyFileTool.MAX_ENTRIES; i++)
         File.WriteAllBytes(Path.Combine(big, i + ".txt"), Array.Empty<byte>());

      var res = await _Run(new CopyFileTool(), new { source = "big", destination = "big2" });

      _AssertDenied(res);
      Assert.Contains("too large", res.Content);
      Assert.False(Directory.Exists(_tree.InProject("big2")));
   }

   // -- list_dir -------------------------------------------------------------

   [Fact]
   public async Task ListDir_Root_HidesNoAccessAndOutsideLink()
   {
      var res = await _Run(new ListDirTool(), new { show_hidden = true });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("docs/", res.Content);
      Assert.Contains("src/",  res.Content);
      Assert.DoesNotContain("secret", res.Content);
      Assert.DoesNotContain("link",   res.Content);
   }

   [Theory]
   [InlineData("secret")]
   [InlineData("link")]
   public async Task ListDir_Denied(string path)
   {
      var res = await _Run(new ListDirTool(), new { path });

      _AssertDenied(res);
   }

   // -- glob -----------------------------------------------------------------

   [Fact]
   public async Task Glob_All_ReachesNeitherNoAccessNorOutside()
   {
      var res = await _Run(new GlobTool(), new { pattern = "**/*" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("docs/a.md",         res.Content);
      Assert.Contains("vault/inner/v.txt", res.Content);
      Assert.DoesNotContain("secret",   res.Content);
      Assert.DoesNotContain("leak.txt", res.Content);
   }

   [Theory]
   [InlineData("secret")]
   [InlineData("link")]
   public async Task Glob_DeniedStart(string path)
   {
      var res = await _Run(new GlobTool(), new { pattern = "**/*", path });

      _AssertDenied(res);
   }

   // -- grep -----------------------------------------------------------------

   [Fact]
   public async Task Grep_All_ReachesNeitherNoAccessNorOutside()
   {
      var res = await _Run(new GrepTool(), new { pattern = MARK });

      Assert.False(res.IsError, res.Content);
      Assert.Contains(MARK + " src",   res.Content);
      Assert.Contains(MARK + " docs",  res.Content);
      Assert.Contains(MARK + " vault", res.Content);
      Assert.DoesNotContain(MARK + " secret",  res.Content);
      Assert.DoesNotContain(MARK + " outside", res.Content);
   }

   [Theory]
   [InlineData("secret")]
   [InlineData("secret/key.txt")]     // single-file mode
   [InlineData("link")]
   [InlineData("link/leak.txt")]
   public async Task Grep_DeniedStart(string path)
   {
      var res = await _Run(new GrepTool(), new { pattern = MARK, path });

      _AssertDenied(res);
   }

   // -- helpers --------------------------------------------------------------

   private Task<ToolResult> _Run(ITool tool, object args)
   {
      var json = JsonSerializer.SerializeToElement(args);
      return tool.ExecuteAsync(json, _ctx);
   }

   private static void _AssertDenied(ToolResult res)
   {
      Assert.True(res.IsError, "expected a denial, got: " + res.Content);
   }

   /// <summary>Every file below the temp base (project + outside) with its content.</summary>
   private SortedDictionary<string, string> _Snapshot()
   {
      var snap = new SortedDictionary<string, string>(StringComparer.Ordinal);
      _Collect(_tree.Project, "project", snap);
      _Collect(_tree.Outside, "outside", snap);
      return snap;
   }

   private static void _Collect(string dir, string label, SortedDictionary<string, string> snap)
   {
      foreach (var info in new DirectoryInfo(dir).GetFileSystemInfos())
      {
         var key = label + "/" + info.Name;
         if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
         {
            snap[key] = "<link>";                   // its target is snapshotted on its own
         }
         else if ((info.Attributes & FileAttributes.Directory) != 0)
         {
            snap[key] = "<dir>";
            _Collect(info.FullName, key, snap);
         }
         else
         {
            snap[key] = File.ReadAllText(info.FullName);
         }
      }
   }

   /// <summary>
   /// Whether a tool's JSON schema declares a path-like argument. Tools
   /// without a parameterless constructor are inspected uninitialized;
   /// a schema that cannot be read that way (built from ctor state, e.g.
   /// MCP tools) is out of the sandbox's scope.
   /// </summary>
   private static bool _TakesPath(Type type)
   {
      string? schema;
      try
      {
         var tool = type.GetConstructor(Type.EmptyTypes) != null
                    ? (ITool)Activator.CreateInstance(type)!
                    : (ITool)RuntimeHelpers.GetUninitializedObject(type);
         schema = tool.ParametersJsonSchema;
      }
      catch
      {
         return false;
      }
      if (string.IsNullOrEmpty(schema))
         return false;

      using var doc = JsonDocument.Parse(schema);
      if (!doc.RootElement.TryGetProperty("properties", out var props))
         return false;
      return props.EnumerateObject().Any(p => s_pathArgs.Contains(p.Name));
   }

   // -- constants ------------------------------------------------------------

   private const string MARK = "SANDBOX_MARK";

   private static readonly HashSet<Type> s_covered = new()
   {
      typeof(ReadFileTool), typeof(WriteFileTool), typeof(EditFileTool), typeof(DeleteFileTool),
      typeof(MoveFileTool), typeof(ListDirTool),   typeof(GlobTool),     typeof(GrepTool),
      typeof(CopyFileTool),
   };

   private static readonly HashSet<string> s_pathArgs = new(StringComparer.OrdinalIgnoreCase)
   {
      "path", "file", "file_path", "dir", "directory", "source", "destination", "cwd", "working_dir",
   };

   // -- fields ---------------------------------------------------------------

   private readonly TempTree    _tree;
   private readonly ToolContext _ctx;
}
