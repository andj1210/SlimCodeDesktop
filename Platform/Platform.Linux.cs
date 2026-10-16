// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Linux implementation of the per-OS platform surface. Compiled when
/// <c>$(TargetOS) == Linux</c>. See Platform.Shared.cs for the contract
/// every OS variant must satisfy.
/// </summary>
public static class Platform
{
   /// <summary>Shell syntax the model is expected to write.</summary>
   public static string ShellDescription => "/bin/sh -c";

   /// <summary>
   /// Native decorations: the window manager owns the frame, and drawn
   /// decorations on X11 also need app-side resize grips and shadow.
   /// </summary>
   public static bool InstallCustomTitleBar(Avalonia.Controls.Window window) => false;

   /// <summary>
   /// <c>/bin/sh -c '&lt;command&gt; 2&gt;&amp;1'</c>.
   ///
   /// argv style: the command routinely contains quotes, and folding it into
   /// a single command-line string would need escaping that .NET's Unix
   /// argument parser would then undo incorrectly. Passing -c and the command
   /// as separate argv entries sidesteps quoting entirely.
   ///
   /// /bin/sh rather than $SHELL: only POSIX sh is guaranteed present, and
   /// -c is the one flag whose meaning every shell agrees on (fish and others
   /// differ once you combine short options).
   ///
   /// NON-login shell, deliberately. A login shell (-l) would source the
   /// user's profile and pick up PATH additions from nvm/pyenv/cargo, which
   /// is tempting -- but it also emits MOTD and profile chatter straight into
   /// the stdout we capture and hand to the model. Since the process already
   /// inherits the desktop session's environment, the PATH is usually right
   /// anyway. Revisit if tools turn out to be missing from PATH in practice.
   /// </summary>
   public static ShellCommand WrapShell(string command) =>
      new("/bin/sh", null, new[] { "-c", command + " 2>&1" });

   /// <summary>
   /// Pass-through. execvp already searches PATH, and there is no script-vs-
   /// image distinction to work around: a shebang makes a script directly
   /// executable. The Windows side of this method exists purely to undo two
   /// Windows-specific obstacles, both of which are absent here.
   /// </summary>
   public static ShellCommand ResolveProcess(string command, IReadOnlyList<string> args) =>
      new(command, null, args);

   /// <summary>
   /// No cheap answer exists here, so this always declines and the caller
   /// falls back to asking Avalonia.
   ///
   /// X11 keeps the clipboard in the owning application: asking what is on it
   /// is a selection transfer, not a lookup, and there is no change signal
   /// without an XFIXES subscription on an X connection Avalonia does not
   /// expose. Wayland is stricter still -- a client only learns about the
   /// clipboard while it holds focus. The fallback lands close enough: the
   /// question is asked exactly when the window is activated, which is when
   /// Wayland would hand it over anyway.
   /// </summary>
   public static bool TryPeekClipboardImage(out bool hasImage, out ulong token)
   {
      hasImage = false;
      token    = 0;
      return false;
   }

   /// <summary>
   /// libc <c>realpath(3)</c> with a NULL buffer: resolves every symlink in
   /// the path; the returned buffer is malloc'd and must be freed here.
   /// </summary>
   public static string? RealPath(string existingPath)
   {
      var p = realpath(existingPath, System.IntPtr.Zero);
      if (p == System.IntPtr.Zero)
         return null;
      try
      {
         return Marshal.PtrToStringUTF8(p);
      }
      finally
      {
         free(p);
      }
   }

   // -- private --------------------------------------------------------------

   [DllImport("libc", SetLastError = true)]
   private static extern System.IntPtr realpath(
      [MarshalAs(UnmanagedType.LPUTF8Str)] string path, System.IntPtr resolved);

   [DllImport("libc")]
   private static extern void free(System.IntPtr ptr);
}
