// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;
using System.Runtime.InteropServices;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Application identity read from the assembly metadata. The single source of
/// truth is <c>&lt;Version&gt;</c> in the .csproj — it seeds the assembly's
/// informational version, which is read back here so the value never has to be
/// duplicated in code.
/// </summary>
public static class AppInfo
{
   /// <summary>Product name shown in the title bar / about surfaces.</summary>
   public const string Name = "SlimCode Desktop";

   /// <summary>Primary author / copyright holder.</summary>
   public const string Author = "Andreas Jung";

   /// <summary>SPDX license identifier (matches the per-file headers).</summary>
   public const string License = "GPL-3.0-only";

   /// <summary>Semantic version, e.g. "0.0.1". Falls back to "0.0.0" if unreadable.</summary>
   public static string Version => s_version;

   /// <summary>"SlimCode Desktop 0.0.1" — convenience for titles.</summary>
   public static string NameWithVersion => Name + " " + Version;

   /// <summary>Source commit hash baked into the build, or "" if none.</summary>
   public static string Commit => s_commit;

   /// <summary>Build configuration ("Debug" / "Release"), or "" if unknown.</summary>
   public static string Configuration => s_configuration;

   /// <summary>Runtime framework description, e.g. ".NET 10.0.0".</summary>
   public static string Framework => RuntimeInformation.FrameworkDescription;

   /// <summary>
   /// One-line build detail combining configuration, framework and (if present)
   /// the short commit — for the About surface, e.g. "Release · .NET 10.0.0 · 2a1b07a".
   /// </summary>
   public static string BuildInfo
   {
      get
      {
         var parts = new System.Collections.Generic.List<string>(3);
         if (!string.IsNullOrEmpty(s_configuration)) parts.Add(s_configuration);
         parts.Add(Framework);
         if (!string.IsNullOrEmpty(s_commit)) parts.Add(ShortCommit);
         return string.Join("  ·  ", parts);
      }
   }

   /// <summary>First 7 chars of <see cref="Commit"/>, or "".</summary>
   public static string ShortCommit =>
      s_commit.Length >= 7 ? s_commit.Substring(0, 7) : s_commit;

   // -- fields ---------------------------------------------------------------

   private static readonly string s_version;
   private static readonly string s_commit;
   private static readonly string s_configuration;

   static AppInfo()
   {
      var asm  = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
      var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

      // The informational version is "<semver>" or "<semver>+<commit>".
      int plus = info.IndexOf('+');
      if (plus >= 0)
      {
         s_version = info.Substring(0, plus);
         s_commit  = info.Substring(plus + 1);
      }
      else if (!string.IsNullOrEmpty(info))
      {
         s_version = info;
         s_commit  = "";
      }
      else
      {
         var v = asm.GetName().Version;
         s_version = v != null ? v.Major + "." + v.Minor + "." + v.Build : "0.0.0";
         s_commit  = "";
      }

      s_configuration = asm.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "";
   }
}
