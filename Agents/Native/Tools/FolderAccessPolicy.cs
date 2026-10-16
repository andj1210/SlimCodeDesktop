// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>Access level of a folder. Ordered from least to most restrictive.</summary>
public enum FolderAccess
{
   Full,
   ReadOnly,
   NoAccess,
}

/// <summary>
/// One folder rule. <see cref="Folder"/> is project-relative; "" or "."
/// addresses the project root itself.
/// </summary>
public sealed record FolderRule(string Folder, FolderAccess Access);

/// <summary>
/// Per-folder narrowing inside the project root. Pure lookup over
/// project-relative paths; containment in the root is
/// <see cref="ToolContext.Resolve"/>'s job and happens first.
///
/// Semantics:
/// <list type="bullet">
///   <item>Rules match whole path segments, anchored at the project root
///         ("secret" covers "secret/x", not "secrets" or "src/secret").</item>
///   <item>A rule covers its folder and everything below it.</item>
///   <item>The most restrictive matching rule wins: a deeper rule can
///         tighten, never loosen. <see cref="FolderAccess.Full"/> rules are
///         therefore no-ops.</item>
///   <item>Windows semantics: case-insensitive, both separators, and each
///         segment is compared without an NTFS stream suffix (":...") or
///         trailing dots / spaces -- all of which still open the same
///         folder ("secret::$INDEX_ALLOCATION\x" is "secret\x").</item>
/// </list>
///
/// Lexical only: junctions / symlinks and 8.3 short names are not resolved.
/// </summary>
public sealed class FolderAccessPolicy
{
   /// <summary>Policy without rules: every path inside the root is <see cref="FolderAccess.Full"/>.</summary>
   public static readonly FolderAccessPolicy Empty = new(Array.Empty<FolderRule>());

   /// <summary>Rules with the host OS's path semantics.</summary>
   public FolderAccessPolicy(IEnumerable<FolderRule> rules)
      : this(rules, OperatingSystem.IsWindows())
   {
   }

   /// <summary>
   /// Rules with explicit path semantics (tests exercise both on any host).
   /// Throws <see cref="ArgumentException"/> for a rooted folder or one
   /// containing "..": a rule that silently matches nothing would leave the
   /// user believing a folder is protected.
   /// </summary>
   public FolderAccessPolicy(IEnumerable<FolderRule> rules, bool windowsSemantics)
   {
      ArgumentNullException.ThrowIfNull(rules);

      _windows = windowsSemantics;
      _cmp     = windowsSemantics ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
      _rules   = new Dictionary<string, FolderAccess>(
                    windowsSemantics ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

      foreach (var rule in rules)
      {
         ArgumentNullException.ThrowIfNull(rule);
         var key = _NormalizeRule(rule.Folder);
         if (rule.Access == FolderAccess.Full)
            continue;
         if (!_rules.TryGetValue(key, out var existing) || rule.Access > existing)
            _rules[key] = rule.Access;
      }
   }

   public bool IsEmpty => _rules.Count == 0;

   /// <summary>
   /// Effective access at a project-relative path ("" = the root). The path
   /// must already be normalized: ".." segments throw.
   /// </summary>
   public FolderAccess AccessAt(string relativePath)
   {
      var level = _rules.TryGetValue("", out var rootLevel) ? rootLevel : FolderAccess.Full;
      var key   = "";
      foreach (var seg in _SplitPath(relativePath))
      {
         key = key.Length == 0 ? seg : key + "/" + seg;
         if (_rules.TryGetValue(key, out var l) && l > level)
            level = l;
      }
      return level;
   }

   /// <summary>
   /// True when a restricting rule sits strictly below
   /// <paramref name="relativePath"/>. Writing the path itself (delete /
   /// move of a folder) would then take the protected folder with it.
   /// </summary>
   public bool HasRestrictionBelow(string relativePath)
   {
      var prefix = string.Join('/', _SplitPath(relativePath));
      foreach (var key in _rules.Keys)
      {
         if (prefix.Length == 0)
         {
            if (key.Length > 0)
               return true;
         }
         else if (key.Length > prefix.Length + 1 &&
                  key[prefix.Length] == '/' &&
                  key.StartsWith(prefix, _cmp))
         {
            return true;
         }
      }
      return false;
   }

   // -- internals ------------------------------------------------------------

   private string _NormalizeRule(string? folder)
   {
      if (folder == null)
         throw new ArgumentException("folder rule without a folder");

      var trimmed = folder.Trim();
      if (trimmed.StartsWith('/') || trimmed.StartsWith('\\') || Path.IsPathRooted(trimmed))
         throw new ArgumentException("folder rule must be project-relative: " + folder);

      // Rules are user-authored config: accept either separator on every OS.
      var segs = new List<string>();
      foreach (var raw in trimmed.Split(s_bothSeps, StringSplitOptions.RemoveEmptyEntries))
      {
         if (raw == ".")
            continue;
         if (raw == "..")
            throw new ArgumentException("folder rule must not contain '..': " + folder);
         var seg = _Segment(raw);
         if (seg.Length > 0)
            segs.Add(seg);
      }
      return string.Join('/', segs);
   }

   private IEnumerable<string> _SplitPath(string relativePath)
   {
      ArgumentNullException.ThrowIfNull(relativePath);

      // Paths follow the OS: on Linux '\' is an ordinary file-name char.
      var seps = _windows ? s_bothSeps : s_slashOnly;
      foreach (var raw in relativePath.Split(seps, StringSplitOptions.RemoveEmptyEntries))
      {
         if (raw == ".")
            continue;
         if (raw == "..")
            throw new ArgumentException("path must be normalized: " + relativePath);
         var seg = _Segment(raw);
         if (seg.Length > 0)
            yield return seg;
      }
   }

   /// <summary>Comparison form of one segment (see class remarks for Windows).</summary>
   private string _Segment(string raw)
   {
      if (!_windows)
         return raw;

      var colon = raw.IndexOf(':');
      var name  = colon >= 0 ? raw.Substring(0, colon) : raw;
      return name.TrimEnd('.', ' ');
   }

   // -- fields ---------------------------------------------------------------

   private static readonly char[] s_bothSeps  = { '/', '\\' };
   private static readonly char[] s_slashOnly = { '/' };

   private readonly bool                             _windows;
   private readonly StringComparison                 _cmp;
   private readonly Dictionary<string, FolderAccess> _rules;
}
