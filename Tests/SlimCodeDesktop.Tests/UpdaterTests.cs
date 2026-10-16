// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SlimCodeDesktop.Core;
using Xunit;

namespace SlimCodeDesktop.Tests;

public sealed class UpdateServiceTests
{
   [Theory]
   [InlineData("v1.2.3",       1, 2, 3)]
   [InlineData("V0.0.1",       0, 0, 1)]
   [InlineData("1.2",          1, 2, 0)]
   [InlineData("1.2.3-beta.1", 1, 2, 3)]
   [InlineData("1.2.3+abc",    1, 2, 3)]
   public void ParseVersion_Normalizes(string text, int major, int minor, int build)
   {
      Assert.Equal(new Version(major, minor, build, 0), UpdateService.ParseVersion(text));
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("latest")]
   [InlineData("v")]
   public void ParseVersion_RejectsGarbage(string? text)
   {
      Assert.Null(UpdateService.ParseVersion(text));
   }

   [Theory]
   [InlineData("v0.0.2", "0.0.1",  true)]
   [InlineData("v0.1",   "0.0.9",  true)]
   [InlineData("v0.0.1", "0.0.1",  false)]
   [InlineData("v0.0.1", "0.0.1.0", false)]
   [InlineData("v0.0.1", "0.0.2",  false)]
   public void IsNewer_ComparesNormalized(string remote, string local, bool expected)
   {
      Assert.Equal(expected, UpdateService.IsNewer(UpdateService.ParseVersion(remote), local));
   }

   [Fact]
   public void ParseChecksums_ReadsSha256sumOutput()
   {
      var a = new string('a', 64);
      var b = new string('B', 64);
      var text = a + "  slimcode_desktop_v1.0.0_win-x64.zip\n"
               + b + " *slimcode_desktop_v1.0.0_linux-x64.tar.gz\r\n"
               + "not a checksum line\n"
               + "\n";

      var map = UpdateService.ParseChecksums(text);

      Assert.Equal(2, map.Count);
      Assert.Equal(a, map["slimcode_desktop_v1.0.0_win-x64.zip"]);
      Assert.Equal(b.ToLowerInvariant(), map["slimcode_desktop_v1.0.0_linux-x64.tar.gz"]);
   }

   [Fact]
   public void ParseRelease_PicksThisPlatformsAssetAndChecksums()
   {
      var mine = UpdateService.ExpectedAssetName("1.4.0");
      var json = "{"
         + "\"tag_name\":\"v1.4.0\","
         + "\"html_url\":\"https://github.com/o/r/releases/tag/v1.4.0\","
         + "\"assets\":["
         + "  {\"name\":\"other_asset.zip\",\"url\":\"https://api/other\",\"size\":1},"
         + "  {\"name\":\"" + mine + "\",\"url\":\"https://api/mine\",\"size\":42},"
         + "  {\"name\":\"SHA256SUMS.txt\",\"url\":\"https://api/sums\",\"size\":2}"
         + "]}";

      var rel = UpdateService.ParseRelease(json);

      Assert.Equal("v1.4.0", rel.Tag);
      Assert.Equal(new Version(1, 4, 0, 0), rel.Version);
      Assert.Equal(mine, rel.AssetName);
      Assert.Equal("https://api/mine", rel.AssetApiUrl);
      Assert.Equal(42, rel.AssetSize);
      Assert.Equal("https://api/sums", rel.ChecksumsApiUrl);
   }

   [Fact]
   public void ParseRelease_WithoutMatchingAsset_HasNoAsset()
   {
      var rel = UpdateService.ParseRelease("{\"tag_name\":\"v2.0.0\",\"assets\":[{\"name\":\"x.zip\",\"url\":\"u\"}]}");

      Assert.Null(rel.AssetApiUrl);
      Assert.Null(rel.ChecksumsApiUrl);
   }

   [Fact]
   public void ExpectedAssetName_MatchesPublishScript()
   {
      var name = UpdateService.ExpectedAssetName("0.0.1");

      Assert.StartsWith("slimcode_desktop_v0.0.1_", name);
      Assert.EndsWith(OperatingSystem.IsWindows() ? "_win-x64.zip" : "_linux-x64.tar.gz", name);
   }
}

public sealed class UpdateInstallerTests : IDisposable
{
   public UpdateInstallerTests()
   {
      _base    = Path.Combine(Path.GetTempPath(), "slimcode-update-tests-" + Guid.NewGuid().ToString("N"));
      _install = Directory.CreateDirectory(Path.Combine(_base, "install")).FullName;
      _source  = Directory.CreateDirectory(Path.Combine(_base, "source")).FullName;
   }

   public void Dispose()
   {
      try { Directory.Delete(_base, recursive: true); } catch { }
   }

   [Fact]
   public void Stage_Zip_ExtractsAndFindsExe()
   {
      _Write(_source, "app.exe", "new");
      _Write(_source, "sub/lib.dll", "lib");
      var zip = Path.Combine(_base, "pkg.zip");
      ZipFile.CreateFromDirectory(_source, zip);

      var staging = UpdateInstaller.Stage(zip, _install, "app.exe");

      Assert.Equal(Path.Combine(_install, UpdateInstaller.StagingDirName), staging);
      Assert.Equal("new", File.ReadAllText(Path.Combine(staging, "app.exe")));
      Assert.Equal("lib", File.ReadAllText(Path.Combine(staging, "sub", "lib.dll")));
   }

   [Fact]
   public void Stage_TarGz_Extracts()
   {
      _Write(_source, "app", "new");
      var tgz = Path.Combine(_base, "pkg.tar.gz");
      using (var fs = File.Create(tgz))
      using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
         TarFile.CreateFromDirectory(_source, gz, includeBaseDirectory: false);

      var staging = UpdateInstaller.Stage(tgz, _install, "app");

      Assert.Equal("new", File.ReadAllText(Path.Combine(staging, "app")));
      if (!OperatingSystem.IsWindows())
         Assert.True(File.GetUnixFileMode(Path.Combine(staging, "app")).HasFlag(UnixFileMode.UserExecute));
   }

   [Fact]
   public void Stage_WithoutExe_Throws()
   {
      _Write(_source, "readme.txt", "x");
      var zip = Path.Combine(_base, "pkg.zip");
      ZipFile.CreateFromDirectory(_source, zip);

      Assert.Throws<InvalidOperationException>(() => UpdateInstaller.Stage(zip, _install, "app.exe"));
   }

   [Fact]
   public void Apply_ReplacesFiles_ParksOld_KeepsUnrelated()
   {
      _Write(_install, "app.exe", "old");
      _Write(_install, "sub/lib.dll", "old-lib");
      _Write(_install, "user-launcher.bat", "mine");
      var staging = Path.Combine(_install, UpdateInstaller.StagingDirName);
      _Write(staging, "app.exe", "new");
      _Write(staging, "sub/lib.dll", "new-lib");
      _Write(staging, "added/extra.dll", "extra");

      UpdateInstaller.Apply(_install, staging);

      Assert.Equal("new",     File.ReadAllText(Path.Combine(_install, "app.exe")));
      Assert.Equal("new-lib", File.ReadAllText(Path.Combine(_install, "sub", "lib.dll")));
      Assert.Equal("extra",   File.ReadAllText(Path.Combine(_install, "added", "extra.dll")));
      Assert.Equal("mine",    File.ReadAllText(Path.Combine(_install, "user-launcher.bat")));

      var backup = Path.Combine(_install, UpdateInstaller.BackupDirName);
      Assert.Equal("old",     File.ReadAllText(Path.Combine(backup, "app.exe")));
      Assert.Equal("old-lib", File.ReadAllText(Path.Combine(backup, "sub", "lib.dll")));
      Assert.Empty(Directory.GetFiles(staging, "*", SearchOption.AllDirectories));
   }

   [Fact]
   public void Apply_Failure_RollsBackEverything()
   {
      // Files are applied in ordinal order: a.dll, b.dll, then c.dll fails
      // because the install has a folder of that name.
      _Write(_install, "a.dll", "old-a");
      Directory.CreateDirectory(Path.Combine(_install, "c.dll"));
      var staging = Path.Combine(_install, UpdateInstaller.StagingDirName);
      _Write(staging, "a.dll", "new-a");
      _Write(staging, "b.dll", "new-b");
      _Write(staging, "c.dll", "new-c");

      var ex = Assert.Throws<IOException>(() => UpdateInstaller.Apply(_install, staging));

      Assert.Contains("rolled back", ex.Message);
      Assert.Equal("old-a", File.ReadAllText(Path.Combine(_install, "a.dll")));
      Assert.False(File.Exists(Path.Combine(_install, "b.dll")));
      Assert.True(Directory.Exists(Path.Combine(_install, "c.dll")));
      Assert.Equal("new-a", File.ReadAllText(Path.Combine(staging, "a.dll")));
      Assert.Equal("new-b", File.ReadAllText(Path.Combine(staging, "b.dll")));
   }

   [Fact]
   public void CleanupLeftovers_RemovesBackupAndStaging()
   {
      _Write(Path.Combine(_install, UpdateInstaller.BackupDirName), "app.exe", "old");
      _Write(Path.Combine(_install, UpdateInstaller.StagingDirName), "x", "y");
      _Write(_install, "app.exe", "new");

      Assert.True(UpdateInstaller.CleanupLeftovers(_install));

      Assert.False(Directory.Exists(Path.Combine(_install, UpdateInstaller.BackupDirName)));
      Assert.False(Directory.Exists(Path.Combine(_install, UpdateInstaller.StagingDirName)));
      Assert.True(File.Exists(Path.Combine(_install, "app.exe")));
   }

   // -- private --------------------------------------------------------------

   private static void _Write(string root, string rel, string content)
   {
      var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, content);
   }

   private readonly string _base;
   private readonly string _install;
   private readonly string _source;
}
