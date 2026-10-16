// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Filesystem half of the self-updater. Swaps a downloaded release into the
/// install folder while the app is still running, without a helper script:
///
///   1. <see cref="Stage"/> extracts the archive into <c>&lt;install&gt;/.update/</c>.
///   2. <see cref="Apply"/> moves every installed file that the release
///      replaces into <c>&lt;install&gt;/.old/</c>, then moves the staged file
///      into its place. Windows refuses to overwrite a running exe or loaded
///      DLL but does allow renaming it; Linux allows both. Any failure moves
///      everything back, so the install is never left half old, half new.
///   3. <see cref="ScheduleRelaunch"/> + <see cref="RelaunchIfPending"/> start
///      the new exe once Main has fully shut the old one down.
///   4. On the next start <see cref="CleanupLeftoversInBackground"/> deletes
///      <c>.old/</c> and <c>.update/</c>.
///
/// Only files are moved, never directories: Windows cannot rename a folder
/// that holds an open file. Files in the install folder that the release does
/// not contain are left alone. User data lives under %APPDATA% (see
/// <see cref="AppPaths"/>), so nothing in the install folder needs preserving.
/// </summary>
public static class UpdateInstaller
{
   public const string StagingDirName = ".update";
   public const string BackupDirName  = ".old";

   private static ProcessStartInfo? s_relaunch;

   /// <summary>Name of the packaged executable in a release archive.</summary>
   public static string AppExeName => OperatingSystem.IsWindows() ? "SlimCodeDesktop.exe" : "SlimCodeDesktop";

   /// <summary>Folder the running app was started from.</summary>
   public static string InstallDir =>
      Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

   /// <summary>
   /// Why this process cannot update itself in place, or null when it can.
   /// The UI falls back to opening the release page when non-null.
   /// </summary>
   public static string? GetInstallBlocker()
   {
      // A Debug build runs from bin\Debug -- replacing the dev output with a
      // release package would be a nasty surprise.
      if (string.Equals(AppInfo.Configuration, "Debug", StringComparison.OrdinalIgnoreCase))
         return "This is a Debug build; in-place updates are only done for packaged release builds.";

      // Started as `dotnet SlimCodeDesktop.dll` or similar: the process image
      // is not ours to replace.
      var exe = Environment.ProcessPath;
      if (string.IsNullOrEmpty(exe) ||
          !string.Equals(Path.GetFileName(exe), AppExeName, StringComparison.OrdinalIgnoreCase) ||
          !File.Exists(Path.Combine(InstallDir, AppExeName)))
      {
         return "SlimCode was not started from a packaged install folder.";
      }

      if (!IsWritable(InstallDir))
         return "The install folder is not writable (" + InstallDir + ").";

      return null;
   }

   /// <summary>
   /// Other SlimCode processes started from <paramref name="installDir"/>
   /// (typically other profiles). They keep running on the old files after an
   /// update, and keep <c>.old/</c> locked until they exit. Processes whose
   /// image path cannot be read (other users) are not counted.
   /// </summary>
   public static int CountOtherInstances(string installDir)
   {
      var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)) + Path.DirectorySeparatorChar;
      var cmp  = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
      int self = Environment.ProcessId;
      int count = 0;

      foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
      {
         using (p)
         {
            try
            {
               if (p.Id == self)
                  continue;
               // Under the install root also covers .old/: a process whose
               // files were already parked there by an earlier update.
               var path = p.MainModule?.FileName;
               if (path != null && path.StartsWith(root, cmp))
                  count++;
            }
            catch { }
         }
      }
      return count;
   }

   public static bool IsWritable(string dir)
   {
      try
      {
         var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));
         File.WriteAllBytes(probe, Array.Empty<byte>());
         File.Delete(probe);
         return true;
      }
      catch
      {
         return false;
      }
   }

   // -- stage ----------------------------------------------------------------

   /// <summary>
   /// Extracts <paramref name="archivePath"/> (.zip or .tar.gz) into a fresh
   /// <c>.update/</c> under <paramref name="installDir"/> and checks it holds
   /// <paramref name="exeName"/> at its root. Returns the staging folder.
   /// </summary>
   public static string Stage(string archivePath, string installDir, string exeName)
   {
      var staging = Path.Combine(installDir, StagingDirName);
      if (Directory.Exists(staging))
         Directory.Delete(staging, recursive: true);
      Directory.CreateDirectory(staging);

      // Both extractors reject entries that would land outside `staging`.
      if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
      {
         ZipFile.ExtractToDirectory(archivePath, staging);
      }
      else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
      {
         using var fs = File.OpenRead(archivePath);
         using var gz = new GZipStream(fs, CompressionMode.Decompress);
         TarFile.ExtractToDirectory(gz, staging, overwriteFiles: false);
      }
      else
      {
         throw new InvalidOperationException("Unknown archive type: " + Path.GetFileName(archivePath));
      }

      var exe = Path.Combine(staging, exeName);
      if (!File.Exists(exe))
         throw new InvalidOperationException("The downloaded package does not contain " + exeName + " -- not installing it.");

      // The release tarball carries the exec bit; set it anyway in case the
      // archive came from a build that lost it.
      if (!OperatingSystem.IsWindows())
         File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

      return staging;
   }

   // -- apply ----------------------------------------------------------------

   /// <summary>
   /// Moves every file from <paramref name="stagingDir"/> into
   /// <paramref name="installDir"/>, parking replaced files in <c>.old/</c>.
   /// On failure everything is moved back and the exception rethrown.
   /// </summary>
   public static void Apply(string installDir, string stagingDir)
   {
      var backupDir = Path.Combine(installDir, BackupDirName);

      // Left over from an update whose cleanup has not run yet. Normally the
      // startup cleanup removed it; if a file there is still locked -- another
      // SlimCode window still running the previous version -- stop before
      // touching the live install.
      if (Directory.Exists(backupDir))
      {
         try
         {
            Directory.Delete(backupDir, recursive: true);
         }
         catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
         {
            throw new IOException(
               "Files from the previous update are still in use by another SlimCode window. "
               + "Close all SlimCode windows, start SlimCode again and retry the update.", ex);
         }
      }
      Directory.CreateDirectory(backupDir);

      var files = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
      Array.Sort(files, StringComparer.Ordinal);

      var backedUp  = new List<string>();
      var installed = new List<string>();
      try
      {
         foreach (var staged in files)
         {
            var rel    = Path.GetRelativePath(stagingDir, staged);
            var target = Path.Combine(installDir, rel);

            if (Directory.Exists(target))
               throw new IOException("Cannot replace folder '" + rel + "' with a file.");

            if (File.Exists(target))
            {
               var parked = Path.Combine(backupDir, rel);
               Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
               File.Move(target, parked);
               backedUp.Add(rel);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(staged, target);
            installed.Add(rel);
         }
      }
      catch (Exception ex)
      {
         var problems = _Rollback(installDir, stagingDir, backupDir, installed, backedUp);
         if (problems.Count == 0)
            throw new IOException("Update failed and was rolled back: " + ex.Message, ex);

         throw new IOException(
            "Update failed (" + ex.Message + ") and the rollback was incomplete. The previous files are in "
            + backupDir + ". Problems: " + string.Join("; ", problems), ex);
      }
   }

   // -- relaunch -------------------------------------------------------------

   /// <summary>
   /// Starts <paramref name="exePath"/> with <paramref name="args"/> once Main
   /// returns. Call BEFORE <see cref="Apply"/>: building the start info here
   /// loads System.Diagnostics.Process from the old install, so the dying
   /// process never pulls that assembly from the freshly swapped-in files.
   /// </summary>
   public static void ScheduleRelaunch(string exePath, string[] args)
   {
      var psi = new ProcessStartInfo(exePath)
      {
         UseShellExecute  = false,
         WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
      };
      foreach (var a in args)
         psi.ArgumentList.Add(a);
      s_relaunch = psi;
   }

   public static void CancelRelaunch()
   {
      s_relaunch = null;
   }

   /// <summary>Call at the very end of Main, after the UI has shut down.</summary>
   public static void RelaunchIfPending()
   {
      if (s_relaunch == null)
         return;

      try
      {
         Process.Start(s_relaunch);
      }
      catch (Exception ex)
      {
         // The update is in place either way; the user just starts it by hand.
         CrashLog.Write("UpdateInstaller.Relaunch", ex);
      }
   }

   // -- cleanup --------------------------------------------------------------

   /// <summary>
   /// Deletes <c>.old/</c> and <c>.update/</c> left by a previous update. The
   /// old process may still be exiting when the new one starts, so this
   /// retries for a few seconds on a background thread. Best effort.
   /// </summary>
   public static void CleanupLeftoversInBackground(string installDir)
   {
      var backup  = Path.Combine(installDir, BackupDirName);
      var staging = Path.Combine(installDir, StagingDirName);
      if (!Directory.Exists(backup) && !Directory.Exists(staging))
         return;

      var t = new Thread(() => CleanupLeftovers(installDir, attempts: 10, delayMs: 500));
      t.IsBackground = true;
      t.Start();
   }

   /// <summary>Synchronous form of <see cref="CleanupLeftoversInBackground"/>. Returns true when nothing is left.</summary>
   public static bool CleanupLeftovers(string installDir, int attempts = 1, int delayMs = 0)
   {
      var dirs = new[]
      {
         Path.Combine(installDir, BackupDirName),
         Path.Combine(installDir, StagingDirName),
      };

      for (int i = 0; i < attempts; i++)
      {
         bool clean = true;
         foreach (var d in dirs)
         {
            if (!Directory.Exists(d))
               continue;
            try { Directory.Delete(d, recursive: true); }
            catch { clean = false; }
         }
         if (clean)
            return true;
         if (i + 1 < attempts)
            Thread.Sleep(delayMs);
      }
      return false;
   }

   // -- private --------------------------------------------------------------

   // Undo in reverse order: new files back to staging, then the parked
   // originals back into place. Returns what could not be restored.
   private static List<string> _Rollback(string installDir, string stagingDir, string backupDir,
                                         List<string> installed, List<string> backedUp)
   {
      var problems = new List<string>();

      for (int i = installed.Count - 1; i >= 0; i--)
      {
         var rel = installed[i];
         try { File.Move(Path.Combine(installDir, rel), Path.Combine(stagingDir, rel)); }
         catch (Exception ex) { problems.Add(rel + ": " + ex.Message); }
      }

      for (int i = backedUp.Count - 1; i >= 0; i--)
      {
         var rel = backedUp[i];
         try { File.Move(Path.Combine(backupDir, rel), Path.Combine(installDir, rel)); }
         catch (Exception ex) { problems.Add(rel + ": " + ex.Message); }
      }

      return problems;
   }
}
