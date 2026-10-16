// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>What a tool intends to do at a path. Write implies read.</summary>
public enum PathAccess
{
   Read,
   Write,
}

/// <summary>Why <see cref="ToolContext.Resolve"/> refused a path.</summary>
public enum PathDenial
{
   None,
   /// <summary>Resolves outside the project root.</summary>
   Escapes,
   /// <summary>Not a well-formed path (e.g. embedded NUL).</summary>
   Invalid,
   /// <summary>Inside a <see cref="FolderAccess.NoAccess"/> folder.</summary>
   NoAccess,
   /// <summary>Write into a <see cref="FolderAccess.ReadOnly"/> folder.</summary>
   ReadOnly,
   /// <summary>Write to a folder that contains a restricted folder.</summary>
   ProtectedBelow,
   /// <summary>An existing part of the path cannot be resolved on disk (e.g. dangling link).</summary>
   Unresolved,
}

/// <summary>
/// Outcome of <see cref="ToolContext.Resolve"/>: the absolute path when
/// allowed, otherwise the reason it was denied.
/// </summary>
public readonly struct PathResolution
{
   private PathResolution(string? fullPath, PathDenial denial)
   {
      FullPath = fullPath;
      Denial   = denial;
   }

   public static PathResolution Allow(string fullPath) => new(fullPath, PathDenial.None);
   public static PathResolution Deny(PathDenial denial) => new(null, denial);

   /// <summary>Absolute, normalized path; null when denied.</summary>
   public string? FullPath { get; }

   public PathDenial Denial { get; }

   [MemberNotNullWhen(true, nameof(FullPath))]
   public bool IsAllowed => Denial == PathDenial.None;

   /// <summary>
   /// Predicate phrase for error messages, e.g. <c>"path " + Reason + ": " + rel</c>.
   /// </summary>
   public string Reason => Denial switch
   {
      PathDenial.None           => "is allowed",
      PathDenial.Escapes        => "escapes project root",
      PathDenial.Invalid        => "is not a valid path",
      PathDenial.NoAccess       => "is in a no-access folder",
      PathDenial.ReadOnly       => "is in a read-only folder",
      PathDenial.ProtectedBelow => "contains a read-only or no-access folder",
      PathDenial.Unresolved     => "cannot be resolved on disk (broken link?)",
      _                         => "is denied",
   };
}
