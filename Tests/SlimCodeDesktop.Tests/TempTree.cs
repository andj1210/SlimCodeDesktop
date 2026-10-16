// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;

namespace SlimCodeDesktop.Tests;

/// <summary>
/// Throw-away tree under %TEMP%: <c>project\</c> is the sandbox root,
/// <c>outside\</c> is a sibling that must stay unreachable.
/// </summary>
internal sealed class TempTree : IDisposable
{
   public TempTree()
   {
      _base   = Path.Combine(Path.GetTempPath(), "slimcode-tests-" + Guid.NewGuid().ToString("N"));
      Project = Directory.CreateDirectory(Path.Combine(_base, "project")).FullName;
      Outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
   }

   public string Project { get; }
   public string Outside { get; }

   /// <summary>Absolute path of a project-relative ('/'-separated) path.</summary>
   public string InProject(string rel) =>
      Path.Combine(Project, rel.Replace('/', Path.DirectorySeparatorChar));

   /// <summary>Create a file (and its folders) below the project root.</summary>
   public string WriteProjectFile(string rel, string content)
   {
      var abs = InProject(rel);
      Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
      File.WriteAllText(abs, content);
      return abs;
   }

   /// <summary>
   /// Directory link: junction on Windows (no admin / developer mode
   /// needed, unlike a symlink), symlink elsewhere. The target need not
   /// exist -- that is how dangling links are made.
   /// </summary>
   public void LinkDirectory(string link, string target)
   {
      if (!OperatingSystem.IsWindows())
      {
         Directory.CreateSymbolicLink(link, target);
         return;
      }

      var psi = new ProcessStartInfo("cmd.exe")
      {
         UseShellExecute        = false,
         CreateNoWindow         = true,
         RedirectStandardOutput = true,
         RedirectStandardError  = true,
      };
      psi.ArgumentList.Add("/c");
      psi.ArgumentList.Add("mklink");
      psi.ArgumentList.Add("/J");
      psi.ArgumentList.Add(link);
      psi.ArgumentList.Add(target);

      using var proc = Process.Start(psi)!;
      var err = proc.StandardError.ReadToEnd();
      proc.WaitForExit();
      if (proc.ExitCode != 0)
         throw new InvalidOperationException("mklink /J failed: " + err);
   }

   public void Dispose()
   {
      // Recursive delete removes links without following them.
      try { Directory.Delete(_base, recursive: true); } catch { }
   }

   private readonly string _base;
}
