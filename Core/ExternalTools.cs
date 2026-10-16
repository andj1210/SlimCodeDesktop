// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Fire-and-forget launchers for external Windows tools (Explorer, cmd,
/// PowerShell) anchored at a project folder. The spawned processes are NOT
/// attached to SlimCode -- they get their own console window and outlive us.
///
/// All methods swallow exceptions: the user invoked from a context menu, so
/// failure is best surfaced via the absence of a new window rather than a
/// dialog. (If this proves annoying, route errors through MainWindow.ShowError.)
/// </summary>
public static class ExternalTools
{
   /// <summary>Open the folder in Windows Explorer.</summary>
   public static void OpenFolder(string path)
   {
      try
      {
         Process.Start(new ProcessStartInfo
         {
            FileName = "explorer.exe",
            Arguments = "\"" + path + "\"",
            UseShellExecute = true,
         });
      }
      catch { }
   }

   /// <summary>
   /// Open Explorer on the folder holding <paramref name="path"/>, with the
   /// file itself selected. Falls back to just opening the folder when the
   /// file does not exist yet: a store file only appears once something has
   /// been saved, and a button that does nothing on first run is worse than
   /// one that shows the (empty) folder it will land in.
   /// </summary>
   public static void RevealFile(string path)
   {
      try
      {
         if (File.Exists(path))
         {
            Process.Start(new ProcessStartInfo
            {
               FileName = "explorer.exe",
               // Explorer's own syntax: the comma is part of the switch and
               // sits outside the quoting around the path.
               Arguments = "/select,\"" + path + "\"",
               UseShellExecute = true,
            });
            return;
         }

         var dir = Path.GetDirectoryName(path);
         if (string.IsNullOrEmpty(dir)) return;
         // No-op when it already exists. Creating it here is harmless — it is
         // SlimCode's own data folder, which the next save would create anyway.
         Directory.CreateDirectory(dir);
         OpenFolder(dir);
      }
      catch { }
   }

   /// <summary>Open a new cmd.exe console window with the project as CWD.</summary>
   public static void OpenCmd(string path) => OpenShell("cmd.exe", path);

   /// <summary>
   /// Open a new cmd.exe in the project folder, run <paramref name="command"/>
   /// once, and keep the window open (/k) so the user can read the result.
   /// Lets the user sanity-check their configured build/test commands.
   /// </summary>
   public static void RunInCmd(string path, string command)
   {
      if (string.IsNullOrWhiteSpace(command)) return;
      try
      {
         Process.Start(new ProcessStartInfo
         {
            FileName         = "cmd.exe",
            Arguments        = "/k " + command,
            WorkingDirectory = path,
            UseShellExecute  = true,
         });
      }
      catch { }
   }

   /// <summary>Open a new Windows PowerShell console window with the project as CWD.</summary>
   public static void OpenPowerShell(string path) => OpenShell("powershell.exe", path);

   // UseShellExecute=true plus a GUI parent causes the console host to spawn
   // its own window -- exactly what we want for "open a fresh terminal here".
   private static void OpenShell(string exe, string path)
   {
      try
      {
         Process.Start(new ProcessStartInfo
         {
            FileName = exe,
            WorkingDirectory = path,
            UseShellExecute = true,
         });
      }
      catch { }
   }
}
