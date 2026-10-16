// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>One directory entry a read tool may see (see <see cref="ToolContext.ListVisible"/>).</summary>
public readonly record struct VisibleEntry(string FullPath, string Name, bool IsDirectory, bool IsLink);

/// <summary>
/// Per-call context handed to every tool. Carries the working-directory
/// root that bounds all file-system access, plus a cancellation token
/// the UI can fire to abort an in-progress tool call.
///
/// All path arguments from the model are resolved via
/// <see cref="Resolve"/>, which both expands relative paths against
/// <see cref="ProjectRoot"/> and rejects any path that would escape it
/// (e.g. <c>..\..\..\Windows\System32</c>). Tools that walk directories
/// use <see cref="ListVisible"/> / <see cref="EnumerateFilesRecursive"/>
/// so every entry below the starting point passes the same gate. This is
/// the only sandbox enforcement in exploration mode.
/// </summary>
public sealed class ToolContext
{
   /// <param name="folders">Per-folder narrowing inside the root; null means
   /// none. Copy it along whenever a context is re-created from another
   /// (see <see cref="Folders"/>).</param>
   public ToolContext(string projectRoot, CancellationToken cancel, FolderAccessPolicy? folders = null)
   {
      if (string.IsNullOrWhiteSpace(projectRoot))
         throw new ArgumentException("projectRoot required", nameof(projectRoot));

      // Drop a trailing separator ("F:\proj\" -> "F:\proj") so the root
      // compares equal to normalized paths like "F:\proj\.". Volume roots
      // ("F:\", "/") keep theirs.
      _root    = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
      _folders = folders ?? FolderAccessPolicy.Empty;
      _cmp     = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

      // Where the root really lives on disk. Null when it does not exist:
      // nothing below it can exist either, so the physical check is moot.
      if (Directory.Exists(_root))
      {
         var real  = Platform.RealPath(_root);
         _realRoot = real == null ? null : Path.TrimEndingDirectorySeparator(real);
      }

      Cancel = cancel;
   }

   /// <summary>Absolute, normalized project root. All tool I/O is scoped here.</summary>
   public string ProjectRoot => _root;

   /// <summary>Folder rules applied by <see cref="Resolve"/> after root containment.</summary>
   public FolderAccessPolicy Folders => _folders;

   public CancellationToken Cancel { get; }

   /// <summary>
   /// Resolve a model-supplied path against the project root. Empty / null
   /// input resolves to the project root itself. Two passes, both against
   /// root containment and <see cref="Folders"/> for the requested
   /// <paramref name="access"/>:
   /// <list type="number">
   ///   <item>lexical -- the normalized path as written;</item>
   ///   <item>physical -- where the existing part of the path really lands
   ///         on disk (junctions, symlinks, 8.3 names resolved), measured
   ///         against the physical root.</item>
   /// </list>
   /// The returned path is the lexical one; the physical pass only vetoes.
   /// </summary>
   public PathResolution Resolve(string? path, PathAccess access)
   {
      string full;
      if (string.IsNullOrWhiteSpace(path))
      {
         full = _root;
      }
      else
      {
         try
         {
            string combined = Path.IsPathRooted(path) ? path : Path.Combine(_root, path);
            full = Path.GetFullPath(combined);
         }
         catch
         {
            return PathResolution.Deny(PathDenial.Invalid);
         }
      }

      var denial = _Check(full, _root, access);
      if (denial != PathDenial.None)
         return PathResolution.Deny(denial);

      if (_realRoot != null)
      {
         var physical = _PhysicalPath(full);
         if (physical == null)
            return PathResolution.Deny(PathDenial.Unresolved);

         denial = _Check(physical, _realRoot, access);
         if (denial != PathDenial.None)
            return PathResolution.Deny(denial);
      }

      return PathResolution.Allow(full);
   }

   /// <summary>
   /// Entries directly inside <paramref name="absDir"/> that a read tool
   /// may see. <see cref="FolderAccess.NoAccess"/> entries are omitted, as
   /// are links whose target fails <see cref="Resolve"/>. The caller must
   /// have resolved <paramref name="absDir"/> itself.
   /// </summary>
   public IReadOnlyList<VisibleEntry> ListVisible(string absDir)
   {
      var rel = _RuleRelOf(absDir);
      if (rel == null)
         return Array.Empty<VisibleEntry>();

      var list = new List<VisibleEntry>();
      foreach (var (entry, _) in _Visible(absDir, rel))
         list.Add(entry);
      return list;
   }

   /// <summary>
   /// Files below <paramref name="absDir"/>, depth-first, filtered like
   /// <see cref="ListVisible"/>. Directories for which
   /// <paramref name="skipDirName"/> returns true are not entered. Linked
   /// directories are never entered: their content is reachable at its
   /// real location, and links can form cycles.
   /// </summary>
   public IEnumerable<string> EnumerateFilesRecursive(string absDir, Func<string, bool>? skipDirName = null)
   {
      var startRel = _RuleRelOf(absDir);
      if (startRel == null)
         yield break;

      var stack = new Stack<(string Dir, string Rel)>();
      stack.Push((absDir, startRel));
      while (stack.Count > 0)
      {
         Cancel.ThrowIfCancellationRequested();
         var (dir, rel) = stack.Pop();
         foreach (var (entry, entryRel) in _Visible(dir, rel))
         {
            if (!entry.IsDirectory)
            {
               yield return entry.FullPath;
               continue;
            }
            if (entry.IsLink || (skipDirName != null && skipDirName(entry.Name)))
               continue;
            stack.Push((entry.FullPath, entryRel));
         }
      }
   }

   /// <summary>Render an absolute path as a project-relative one, for UI / model output.</summary>
   public string ToRelative(string absolute)
   {
      var rel = Path.GetRelativePath(_root, absolute);
      return rel.Replace('\\', '/');
   }

   // -- internals ------------------------------------------------------------

   /// <summary>Containment in <paramref name="root"/>, then folder rules.</summary>
   private PathDenial _Check(string full, string root, PathAccess access)
   {
      var rel = _RelTo(full, root);
      if (rel == null)
         return PathDenial.Escapes;

      // Write implies read, so NoAccess blocks both.
      var level = _folders.AccessAt(rel);
      if (level == FolderAccess.NoAccess)
         return PathDenial.NoAccess;
      if (access == PathAccess.Write)
      {
         if (level == FolderAccess.ReadOnly)
            return PathDenial.ReadOnly;
         if (_folders.HasRestrictionBelow(rel))
            return PathDenial.ProtectedBelow;
      }
      return PathDenial.None;
   }

   /// <summary>Path relative to <paramref name="root"/>, "" for the root, null when outside.</summary>
   private string? _RelTo(string full, string root)
   {
      if (full.Equals(root, _cmp))
         return "";

      var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
                        ? root
                        : root + Path.DirectorySeparatorChar;
      return full.StartsWith(rootWithSep, _cmp) ? full.Substring(rootWithSep.Length) : null;
   }

   /// <summary>
   /// Physical form of <paramref name="full"/>: the deepest part that
   /// exists is resolved by the OS, the not-yet-existing tail is appended
   /// as written. Null when an existing part cannot be resolved (dangling
   /// link, unopenable entry) -- writing through a dangling link would
   /// create its target wherever it points.
   /// </summary>
   private static string? _PhysicalPath(string full)
   {
      var    probe = full;
      string? tail = null;
      while (true)
      {
         // GetAttributes does not follow links, so a dangling one still
         // counts as present here and then fails RealPath below.
         bool present;
         try
         {
            File.GetAttributes(probe);
            present = true;
         }
         catch
         {
            present = false;
         }

         if (present)
         {
            var real = Platform.RealPath(probe);
            if (real == null)
               return null;
            return tail == null ? real : Path.Combine(real, tail);
         }

         var parent = Path.GetDirectoryName(probe);
         if (parent == null)
            return null;
         var name = Path.GetFileName(probe);
         tail  = tail == null ? name : Path.Combine(name, tail);
         probe = parent;
      }
   }

   /// <summary>
   /// Rule-space relative path of a directory the caller already resolved:
   /// physical when the root exists, so rules hold for a directory reached
   /// through a link that points elsewhere inside the project.
   /// </summary>
   private string? _RuleRelOf(string absPath)
   {
      if (_realRoot == null)
         return _RelTo(absPath, _root);

      var physical = _PhysicalPath(absPath);
      return physical == null ? null : _RelTo(physical, _realRoot);
   }

   private List<(VisibleEntry Entry, string Rel)> _Visible(string dir, string dirRel)
   {
      var result = new List<(VisibleEntry, string)>();

      // Materialized under try: enumeration can fail midway (permissions,
      // concurrent deletes) and an iterator cannot yield from a catch scope.
      FileSystemInfo[] infos;
      try
      {
         infos = new DirectoryInfo(dir).GetFileSystemInfos();
      }
      catch
      {
         return result;
      }

      foreach (var info in infos)
      {
         FileAttributes attrs;
         try
         {
            attrs = info.Attributes;
         }
         catch
         {
            continue;
         }

         var  rel    = dirRel.Length == 0 ? info.Name : dirRel + Path.DirectorySeparatorChar + info.Name;
         bool isLink = (attrs & FileAttributes.ReparsePoint) != 0;
         if (isLink)
         {
            // Where a link lands decides, not where it sits.
            if (!Resolve(info.FullName, PathAccess.Read).IsAllowed)
               continue;
         }
         else if (_folders.AccessAt(rel) == FolderAccess.NoAccess)
         {
            continue;
         }

         bool isDir = (attrs & FileAttributes.Directory) != 0;
         result.Add((new VisibleEntry(info.FullName, info.Name, isDir, isLink), rel));
      }
      return result;
   }

   // -- fields ---------------------------------------------------------------

   private readonly string             _root;
   private readonly string?            _realRoot;
   private readonly FolderAccessPolicy _folders;
   private readonly StringComparison   _cmp;
}
