// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;

namespace SlimCodeDesktop.Core;

/// <summary>
/// One shell invocation, resolved for the target OS. Produced by
/// <c>Platform.WrapShell</c>; apply it to a <see cref="ProcessStartInfo"/>
/// with <see cref="ApplyTo"/> rather than reading the fields directly, so
/// callers don't have to care which of the two argument styles is in play.
///
/// Two styles exist because the platforms disagree about quoting:
///   - <see cref="Arguments"/> (raw command line) is what <c>cmd.exe</c>
///     needs — it does its own idiosyncratic parsing of everything after
///     <c>/c</c>, and .NET must not pre-tokenize it.
///   - <see cref="ArgumentList"/> (pre-split argv) is what POSIX shells
///     need — the command being wrapped routinely contains quotes, and
///     round-tripping it through a single command-line string would require
///     escaping that .NET would then undo incorrectly.
/// </summary>
public readonly record struct ShellCommand(
   string Exe,
   string? Arguments = null,
   IReadOnlyList<string>? ArgumentList = null)
{
   /// <summary>Set FileName plus whichever argument style this command uses.</summary>
   public void ApplyTo(ProcessStartInfo psi)
   {
      psi.FileName = Exe;
      if (ArgumentList != null)
      {
         psi.ArgumentList.Clear();
         foreach (var a in ArgumentList)
            psi.ArgumentList.Add(a);
      }
      else
      {
         psi.Arguments = Arguments ?? "";
      }
   }
}

// ---------------------------------------------------------------------------
// Contract for the per-OS Platform class
// ---------------------------------------------------------------------------
//
// Exactly one of Platform/Platform.Windows.cs and Platform/Platform.Linux.cs
// is compiled, selected by the $(TargetOS) property in the .csproj (defaults
// to the build host; override with `dotnet build -p:TargetOS=Linux`). Both
// declare `static class Platform` in this namespace, and both must provide
// every member listed below.
//
// Because only one file compiles, the compiler CANNOT tell you that the other
// one has drifted. Build the inactive side regularly -- it is a compile-only
// check and takes seconds:
//
//     dotnet build -p:TargetOS=Linux
//
// Required members:
//
//   string ShellDescription
//       How the shell is invoked, phrased for a *model-facing* tool
//       description -- e.g. "cmd.exe /c" or "/bin/sh -c". This string ends up
//       in the tool definitions sent to the LLM, so it must name the syntax
//       the model is expected to write, not just an executable.
//
//   ShellCommand WrapShell(string command)
//       Wrap a user/model-authored command line so the OS shell runs it with
//       stderr folded into stdout. Callers redirect stdout only.
//
//   ShellCommand ResolveProcess(string command, IReadOnlyList<string> args)
//       Turn a bare program name plus argv into something the OS can actually
//       start, WITHOUT going through a shell: PATH lookup where the platform
//       doesn't do it for an argv launch, and any wrapping a non-image
//       launcher needs. Used for long-lived child processes (MCP stdio
//       servers) where WrapShell would be wrong -- a shell in between would
//       own the pipes and swallow the signal when the child dies.
//
//   bool TryPeekClipboardImage(out bool hasImage, out ulong token)
//       Answer "is there an image on the clipboard, and is it a different one
//       than last time?" WITHOUT reading the clipboard's contents. Returns
//       false when the OS offers no such cheap signal, in which case the two
//       out values are meaningless and the caller must fall back to Avalonia's
//       IClipboard (which costs a real read). `token` is an opaque identity:
//       compare it for equality, never order it.
//
//   string? RealPath(string existingPath)
//       The physical, fully resolved absolute path of an EXISTING file or
//       directory: every symlink / junction followed, short (8.3) names
//       expanded, in the same plain form Path.GetFullPath produces (no
//       \\?\ prefix). Null when the path cannot be opened or resolved --
//       e.g. a dangling link. The sandbox (ToolContext.Resolve) relies on
//       this to see where a path really lands.
//
//   bool InstallCustomTitleBar(Avalonia.Controls.Window window)
//       Extend the window's client area into the title bar and fix up
//       whatever native frame the OS still draws around it, so the app owns
//       the whole chrome (Avalonia draws caption buttons and title on top of
//       the app's theme-colored strip). Returns false, changing nothing, where
//       the OS / window manager's native decorations are kept. Call once,
//       right after construction.
//
// Deliberate namespace choice: these files live in Platform/ but declare
// namespace SlimCodeDesktop.Core. A `SlimCodeDesktop.Platform` namespace would
// make the unqualified name `Platform` ambiguous with its own namespace, and
// `using Avalonia;` already brings an `Avalonia.Platform` namespace into
// scope. If a future call site imports both and hits CS0104, alias it:
//
//     using Platform = SlimCodeDesktop.Core.Platform;
