// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text;

namespace SlimCodeDesktop.Core
{
   /// <summary>
   /// Centralized resolver for app-owned file locations under
   /// <c>%APPDATA%\SlimCode\&lt;profile&gt;\</c> -- the single place to look
   /// when something needs to move.
   ///
   /// Everything the app owns (settings, workspace, sessions, permission
   /// policies, skills, crash log) hangs off <see cref="AppDataRoot"/>, so
   /// selecting a profile isolates all of it in one step. The profile is
   /// chosen once at startup from the command line
   /// (<c>--profile &lt;name&gt;</c>) via <see cref="Initialize"/> and is
   /// immutable afterwards; call sites just read the properties below.
   /// </summary>
   public static class AppPaths
   {
      private const string AppFolder       = "SlimCode";
      private const string WorkspaceFolder = "workspace";

      /// <summary>Profile used when <c>--profile</c> is absent.</summary>
      public const string DefaultProfileName = "Default";

      /// <summary>Command-line switch that selects the profile.</summary>
      public const string ProfileSwitch = "--profile";

      // Folder names longer than this are almost certainly a mistake (a pasted
      // path, a quoting accident) and would push us against MAX_PATH.
      private const int MaxProfileLength = 48;

      private static string s_profile = DefaultProfileName;

      // -- startup --------------------------------------------------------------

      /// <summary>
      /// Pick the profile from the process command line. Call once, before
      /// anything touches a path -- i.e. first thing in <c>Main</c>.
      ///
      /// Accepts <c>--profile Name</c> and <c>--profile=Name</c>. An unknown
      /// or empty value silently falls back to <see cref="DefaultProfileName"/>:
      /// this runs before any UI exists, and refusing to start over a typo in
      /// a folder name would be worse than starting in the default profile.
      /// The name is sanitized because it becomes a directory name.
      /// </summary>
      public static void Initialize(string[]? args)
      {
         s_profile = _Sanitize(_ParseProfile(args));
      }

      // -- profile --------------------------------------------------------------

      /// <summary>Active profile name, e.g. "Default".</summary>
      public static string Profile => s_profile;

      /// <summary>True while running in the unnamed default profile.</summary>
      public static bool IsDefaultProfile =>
         string.Equals(s_profile, DefaultProfileName, StringComparison.OrdinalIgnoreCase);

      /// <summary>
      /// Window-title tail naming the profile, or "" in the default one --
      /// the default needs no badge, an alternate profile must be obvious at
      /// a glance so state doesn't get written into the wrong one.
      /// </summary>
      public static string TitleSuffix =>
         IsDefaultProfile ? "" : "  [" + s_profile + "]";

      // -- absolute roots -------------------------------------------------------

      /// <summary><c>%APPDATA%\SlimCode\&lt;profile&gt;</c>.</summary>
      public static string AppDataRoot =>
         Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolder,
            s_profile);

      /// <summary>
      /// <see cref="AppDataRoot"/>, or null when APPDATA can't be resolved.
      /// For callers that treat "no user profile" as "this feature is off"
      /// rather than writing into a relative path next to the executable.
      /// </summary>
      public static string? AppDataRootOrNull
      {
         get
         {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appdata))
               return null;
            return Path.Combine(appdata, AppFolder, s_profile);
         }
      }

      /// <summary><c>%APPDATA%\SlimCode\&lt;profile&gt;\workspace</c>.</summary>
      public static string WorkspaceDir =>
         Path.Combine(AppDataRoot, WorkspaceFolder);

      // -- single-file slots ----------------------------------------------------

      /// <summary><c>slimcode_settings.json</c> -- per-agent settings.</summary>
      public static string AgentSettingsFile =>
         Path.Combine(AppDataRoot, "slimcode_settings.json");

      /// <summary><c>slimcode_app.json</c> -- app-wide UI prefs.</summary>
      public static string AppSettingsFile =>
         Path.Combine(AppDataRoot, "slimcode_app.json");

      /// <summary><c>crash.log</c> -- appended by <c>CrashLog</c>.</summary>
      public static string CrashLogFile =>
         Path.Combine(AppDataRoot, "crash.log");

      /// <summary>
      /// <c>workspace\default.json</c> -- current workspace's project list.
      /// Placeholder for a future multi-workspace feature; for now there is
      /// exactly one and it's called "default".
      /// </summary>
      public static string DefaultWorkspaceFile =>
         Path.Combine(WorkspaceDir, "default.json");

      // -- private --------------------------------------------------------------

      private static string _ParseProfile(string[]? args)
      {
         if (args == null)
            return DefaultProfileName;

         for (int i = 0; i < args.Length; i++)
         {
            var a = args[i];
            if (a == null)
               continue;

            if (a.StartsWith(ProfileSwitch + "=", StringComparison.OrdinalIgnoreCase))
               return a.Substring(ProfileSwitch.Length + 1);

            if (string.Equals(a, ProfileSwitch, StringComparison.OrdinalIgnoreCase))
               return i + 1 < args.Length ? args[i + 1] : DefaultProfileName;
         }
         return DefaultProfileName;
      }

      // The result is used verbatim as a directory name, so anything that
      // could escape the SlimCode folder (separators, "..", drive colons) or
      // that the filesystem rejects is dropped rather than escaped -- a
      // profile name is an identifier, not arbitrary text.
      private static string _Sanitize(string? name)
      {
         if (string.IsNullOrWhiteSpace(name))
            return DefaultProfileName;

         var sb = new StringBuilder();
         foreach (var c in name.Trim())
         {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar ||
                c == Path.VolumeSeparatorChar || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
            {
               continue;
            }
            sb.Append(c);
            if (sb.Length >= MaxProfileLength)
               break;
         }

         // Trailing dots and spaces are legal to *write* on Windows but not to
         // open again, and a name of only dots would resolve to a parent dir.
         var clean = sb.ToString().TrimEnd('.', ' ');
         return clean.Length == 0 ? DefaultProfileName : clean;
      }
   }
}
