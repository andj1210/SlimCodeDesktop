// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Skills;

/// <summary>
/// Discovers skills from layered on-disk locations. One folder per skill, each
/// holding a <c>SKILL.md</c> (frontmatter + Markdown body). Layers, in
/// ascending precedence:
///
///   0. Intrinsic — built into the app (<see cref="IntrinsicSkills"/>), no folder
///   1. Global  — <c>%APPDATA%\SlimCode\&lt;profile&gt;\skills</c>
///   2. Extra    — built-in vendor roots (<c>%USERPROFILE%\.claude\skills</c>,
///                 <c>%USERPROFILE%\.qwen\skills</c>) plus user-configured roots
///   3. Project  — <c>&lt;project&gt;\&lt;folder&gt;\skills</c> for each folder in
///                 <see cref="AppSettings.ProjectSkillFolders"/> (default
///                 <c>.ai</c>; git-shareable). Earlier folders win among themselves.
///
/// On a name clash the higher layer wins, so a project-local skill shadows a
/// global one of the same name. Mirrors the global/project permission +
/// config layering already used elsewhere.
///
/// Discovery is cheap (a directory walk + a small header parse) and is read
/// fresh on demand, so externally-edited skills take effect without a restart.
/// </summary>
public static class SkillStore
{
   /// <summary>
   /// Scan all layers and return the effective skill set, project-wins,
   /// ordered by name. <paramref name="extraRoots"/> are additional
   /// skills-root directories (each containing <c>&lt;name&gt;\SKILL.md</c>
   /// subfolders); null / empty when none are configured.
   /// </summary>
   public static IReadOnlyList<SkillInfo> Discover(
      string projectPath,
      IReadOnlyList<string>? extraRoots = null)
   {
      // Keyed by name (case-insensitive); later scans overwrite earlier ones,
      // so we scan in ascending-precedence order to make Project win.
      var byName = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

      // Built-in skills first, so any on-disk skill of the same name shadows them.
      foreach (var sk in IntrinsicSkills.For(projectPath))
         byName[sk.Name] = sk;

      ScanRoot(GlobalSkillsDir(), SkillSource.Global, byName);

      // Built-in vendor roots (Claude Code, Qwen) so their skills work without
      // any configuration; missing folders are simply ignored by ScanRoot.
      foreach (var root in DefaultVendorRoots())
         ScanRoot(root, SkillSource.Extra, byName);

      if (extraRoots != null)
         foreach (var root in extraRoots)
            ScanRoot(root, SkillSource.Extra, byName);

      // Reverse order so the first configured folder is scanned last and wins.
      var projectDirs = ProjectSkillsDirs(projectPath);
      for (int i = projectDirs.Count - 1; i >= 0; i--)
         ScanRoot(projectDirs[i], SkillSource.Project, byName);

      var list = new List<SkillInfo>(byName.Values);
      list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
      return list;
   }

   /// <summary><c>%APPDATA%\SlimCode\&lt;profile&gt;\skills</c>, or null if APPDATA is unresolved.</summary>
   public static string? GlobalSkillsDir()
   {
      var root = AppPaths.AppDataRootOrNull;
      if (root == null) return null;
      return Path.Combine(root, "skills");
   }

   /// <summary>
   /// The primary project skills root — <c>&lt;project&gt;\&lt;first folder&gt;\skills</c> —
   /// used by the create / open button. Null if the path is empty or no
   /// folder is configured.
   /// </summary>
   public static string? ProjectSkillsDir(string projectPath)
   {
      var dirs = ProjectSkillsDirs(projectPath);
      return dirs.Count > 0 ? dirs[0] : null;
   }

   /// <summary>
   /// <c>&lt;project&gt;\&lt;folder&gt;\skills</c> for every entry of
   /// <see cref="AppSettings.ProjectSkillFolders"/>, in configured order.
   /// Blank, duplicate and absolute entries are dropped — this setting is
   /// strictly project-relative; absolute roots belong in SkillSearchPaths.
   /// </summary>
   public static IReadOnlyList<string> ProjectSkillsDirs(string projectPath)
   {
      if (string.IsNullOrWhiteSpace(projectPath)) return Array.Empty<string>();

      var raw  = AppSettingsStore.Current.Values.ProjectSkillFolders ?? "";
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var dirs = new List<string>();
      foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      {
         if (Path.IsPathRooted(part))
            continue;
         string dir;
         try { dir = Path.GetFullPath(Path.Combine(projectPath, part, "skills")); }
         catch { continue; } // invalid path chars in a hand-typed entry
         if (seen.Add(dir))
            dirs.Add(dir);
      }
      return dirs;
   }

   /// <summary>
   /// Built-in skills roots from other vendors, under the user's profile
   /// (<c>%USERPROFILE%</c>): Claude Code and Qwen. Always scanned; folders
   /// that don't exist are skipped. Empty if the profile can't be resolved.
   /// </summary>
   public static IReadOnlyList<string> DefaultVendorRoots()
   {
      var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      if (string.IsNullOrEmpty(home)) return Array.Empty<string>();
      return new[]
      {
         Path.Combine(home, ".claude", "skills"),
         Path.Combine(home, ".qwen",   "skills"),
      };
   }

   // -- scanning -------------------------------------------------------------

   private static void ScanRoot(string? root, SkillSource source,
                                Dictionary<string, SkillInfo> byName)
   {
      if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
         return;

      foreach (var dir in Directory.EnumerateDirectories(root))
      {
         var mdPath = Path.Combine(dir, "SKILL.md");
         if (!File.Exists(mdPath))
            continue;

         var skill = TryParse(mdPath, dir, source);
         if (skill != null)
            byName[skill.Name] = skill;
      }
   }

   /// <summary>
   /// Parse a SKILL.md. Returns null (and logs) when the frontmatter is
   /// missing or lacks <c>name</c> — a malformed folder is skipped rather
   /// than aborting the whole scan.
   /// </summary>
   internal static SkillInfo? TryParse(string mdPath, string dir, SkillSource source)
   {
      string text;
      try
      {
         text = File.ReadAllText(mdPath);
      }
      catch (Exception ex)
      {
         Debug.WriteLine("[SkillStore] cannot read " + mdPath + ": " + ex.Message);
         return null;
      }

      if (!TrySplitFrontmatter(text, out var fields, out var body))
      {
         Debug.WriteLine("[SkillStore] missing/invalid frontmatter, skipping " + mdPath);
         return null;
      }

      fields.TryGetValue("name", out var name);
      fields.TryGetValue("description", out var description);

      // Fall back to the folder name if the author omitted `name`, but a
      // total absence of identity (no name AND no folder) is unusable.
      if (string.IsNullOrWhiteSpace(name))
         name = new DirectoryInfo(dir).Name;
      if (string.IsNullOrWhiteSpace(name))
      {
         Debug.WriteLine("[SkillStore] no name, skipping " + mdPath);
         return null;
      }

      return new SkillInfo
      {
         Name        = name.Trim(),
         Description = (description ?? "").Trim(),
         Body        = body.Trim(),
         Dir         = dir,
         Source      = source,
      };
   }

   /// <summary>
   /// Split a leading <c>---</c> … <c>---</c> frontmatter block into
   /// <c>key: value</c> pairs and return the remaining text as the body.
   /// A tiny hand parser — no YAML dependency. Returns false when there is
   /// no opening / closing fence.
   /// </summary>
   internal static bool TrySplitFrontmatter(
      string text,
      out Dictionary<string, string> fields,
      out string body)
   {
      fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      body   = text ?? "";
      if (string.IsNullOrEmpty(text)) return false;

      // Normalize newlines for line-based parsing; the body keeps its own
      // line breaks via Substring on the original below.
      var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

      int i = 0;
      // Skip a possible BOM / leading blank lines before the opening fence.
      while (i < lines.Length && lines[i].Trim().Length == 0) i++;
      if (i >= lines.Length || lines[i].Trim() != "---")
         return false;
      i++; // past opening fence

      int closeLine = -1;
      for (int j = i; j < lines.Length; j++)
      {
         if (lines[j].Trim() == "---") { closeLine = j; break; }
      }
      if (closeLine < 0)
         return false;

      for (int j = i; j < closeLine; j++)
      {
         var line = lines[j];
         if (line.Trim().Length == 0) continue;
         int colon = line.IndexOf(':');
         if (colon <= 0) continue;
         var key = line.Substring(0, colon).Trim();
         var val = line.Substring(colon + 1).Trim();
         if (key.Length > 0) fields[key] = val;
      }

      var bodyLines = new List<string>(lines.Length - closeLine);
      for (int j = closeLine + 1; j < lines.Length; j++)
         bodyLines.Add(lines[j]);
      body = string.Join("\n", bodyLines);
      return true;
   }
}
