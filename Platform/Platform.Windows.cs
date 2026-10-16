// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Windows implementation of the per-OS platform surface. Compiled when
/// <c>$(TargetOS) == Windows</c>. See Platform.Shared.cs for the contract
/// every OS variant must satisfy.
/// </summary>
public static class Platform
{
   /// <summary>Shell syntax the model is expected to write.</summary>
   public static string ShellDescription => "cmd.exe /c";

   /// <summary>
   /// The native Win10 title bar is light regardless of the app theme; the
   /// extended client area with Avalonia-drawn caption buttons matches it.
   ///
   /// On Windows 10 Avalonia's extended mode is incomplete: it keeps the
   /// left / right / bottom sizing frame as real non-client area, painted
   /// in the light classic style, and lets DefWindowProc repaint the old
   /// caption on every activation change. <see cref="_Win10FrameHook"/>
   /// fixes both. Windows 11 renders that frame itself (invisible, themed),
   /// so Avalonia's handling is complete there and the hook stays out.
   /// </summary>
   public static bool InstallCustomTitleBar(Window window)
   {
      window.ExtendClientAreaToDecorationsHint = true;
      if (System.Environment.OSVersion.Version.Build < 22000)
      {
         Win32Properties.AddWndProcHookCallback(window,
            (System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam, ref bool handled) =>
               _Win10FrameHook(window, hWnd, msg, wParam, lParam, ref handled));
      }
      return true;
   }

   /// <summary>
   /// <c>cmd.exe /c &lt;command&gt; 2&gt;&amp;1</c>.
   ///
   /// Raw-argument style: cmd.exe parses everything after /c itself, so the
   /// command must NOT be pre-tokenized. A duplicate 2&gt;&amp;1 (if the
   /// command already had one) is harmless here.
   ///
   /// No profile / autorun is loaded, matching the POSIX side's non-login
   /// shell -- output stays clean of banner text.
   /// </summary>
   public static ShellCommand WrapShell(string command) =>
      new("cmd.exe", "/c " + command + " 2>&1");

   /// <summary>
   /// Resolve a bare program name + argv into something CreateProcess can
   /// actually start. Two Windows-only problems are handled here:
   ///
   ///   - PATH lookup is not automatic for an argv-style launch, and the
   ///     interesting MCP launchers (npx, uvx, uv) live on PATH.
   ///   - npx and friends ship as <c>.cmd</c> shims, which are scripts, not
   ///     images. CreateProcess refuses them outright, so they have to be
   ///     routed through cmd.exe.
   ///
   /// <c>/s /c</c> with the whole command line wrapped in one pair of quotes is
   /// the deterministic form: /s tells cmd to strip exactly the first and last
   /// quote and take the rest verbatim, which removes its usual guesswork about
   /// how many quotes to eat.
   ///
   /// A command that resolves to nothing is returned unchanged so the caller
   /// surfaces the OS's own "not found" error rather than a synthetic one.
   /// </summary>
   public static ShellCommand ResolveProcess(string command, IReadOnlyList<string> args)
   {
      var resolved = _FindOnPath(command);
      if (resolved == null)
         return new ShellCommand(command, null, args);

      var ext = System.IO.Path.GetExtension(resolved);
      bool isScript = ext.Equals(".cmd", System.StringComparison.OrdinalIgnoreCase) ||
                      ext.Equals(".bat", System.StringComparison.OrdinalIgnoreCase);
      if (!isScript)
         return new ShellCommand(resolved, null, args);

      var sb = new System.Text.StringBuilder();
      sb.Append("/s /c \"").Append(_QuoteArg(resolved));
      foreach (var a in args)
         sb.Append(' ').Append(_QuoteArg(a));
      sb.Append('"');
      return new ShellCommand("cmd.exe", sb.ToString());
   }

   // Full path of `command` using PATH + PATHEXT, or null when nothing matches.
   // An already-rooted or extension-carrying name is only probed as given.
   private static string? _FindOnPath(string command)
   {
      if (string.IsNullOrWhiteSpace(command)) return null;

      var exts = new System.Collections.Generic.List<string> { "" };
      if (string.IsNullOrEmpty(System.IO.Path.GetExtension(command)))
      {
         var pathext = System.Environment.GetEnvironmentVariable("PATHEXT")
                       ?? ".COM;.EXE;.BAT;.CMD";
         foreach (var e in pathext.Split(';'))
            if (!string.IsNullOrWhiteSpace(e)) exts.Add(e.Trim());
      }

      if (System.IO.Path.IsPathRooted(command) || command.Contains('\\') || command.Contains('/'))
      {
         foreach (var e in exts)
         {
            var probe = command + e;
            if (System.IO.File.Exists(probe)) return System.IO.Path.GetFullPath(probe);
         }
         return null;
      }

      var dirs = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
      foreach (var dir in dirs)
      {
         if (string.IsNullOrWhiteSpace(dir)) continue;
         foreach (var e in exts)
         {
            string probe;
            try { probe = System.IO.Path.Combine(dir.Trim(), command + e); }
            catch { continue; }
            if (System.IO.File.Exists(probe)) return probe;
         }
      }
      return null;
   }

   // Quote one argument for the cmd.exe command line: wrap in quotes and
   // double any embedded quote. Backslashes need no treatment here because
   // the argument is consumed by cmd's parser, not by CommandLineToArgvW.
   private static string _QuoteArg(string arg) =>
      "\"" + (arg ?? "").Replace("\"", "\"\"") + "\"";

   /// <summary>
   /// Windows answers this properly. Both calls are single user32 round trips
   /// that neither open the clipboard nor read its contents -- so this can run
   /// on every window activation without stealing the clipboard from another
   /// app, and without tripping the Windows 11 "an app accessed your
   /// clipboard" indicator. Content is only ever read once the user clicks.
   /// </summary>
   public static bool TryPeekClipboardImage(out bool hasImage, out ulong token)
   {
      hasImage = _HasImageFormat();
      token    = GetClipboardSequenceNumber();
      return true;
   }

   /// <summary>
   /// Final on-disk path via <c>GetFinalPathNameByHandleW</c>: one call that
   /// resolves junctions, symlinks, 8.3 short names, subst drives and the
   /// real casing. Opening with zero access rights works even on files we
   /// may not read. The <c>\\?\</c> (<c>\\?\UNC\</c>) prefix is stripped so
   /// the result compares against <see cref="System.IO.Path.GetFullPath(string)"/> output.
   /// </summary>
   public static string? RealPath(string existingPath)
   {
      var open = existingPath.Length >= 260 && !existingPath.StartsWith(@"\\", System.StringComparison.Ordinal)
                 ? @"\\?\" + existingPath
                 : existingPath;

      using var handle = CreateFileW(open, 0, FILE_SHARE_ALL, System.IntPtr.Zero,
                                     OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, System.IntPtr.Zero);
      if (handle.IsInvalid)
         return null;

      var  buf = new char[512];
      uint len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
      if (len >= buf.Length)
      {
         // Too small: len is the required size including the terminator.
         buf = new char[len];
         len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
      }
      if (len == 0 || len >= buf.Length)
         return null;

      var path = new string(buf, 0, (int)len);
      if (path.StartsWith(@"\\?\UNC\", System.StringComparison.Ordinal))
         return @"\\" + path.Substring(8);
      if (path.StartsWith(@"\\?\", System.StringComparison.Ordinal))
         return path.Substring(4);
      return path;
   }

   // -- private --------------------------------------------------------------

   private const uint FILE_SHARE_ALL             = 0x1 | 0x2 | 0x4;   // read | write | delete
   private const uint OPEN_EXISTING              = 3;
   private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;        // required to open directories

   [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
   private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
      string name, uint access, uint share, System.IntPtr security,
      uint disposition, uint flags, System.IntPtr template);

   [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
   private static extern uint GetFinalPathNameByHandleW(
      Microsoft.Win32.SafeHandles.SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

   private const uint CF_BITMAP = 2;
   private const uint CF_DIB    = 8;

   // Chromium and Firefox publish screenshots and dragged images under a
   // registered "PNG" format (alpha survives there, in CF_DIB it does not).
   // The id is stable for the session, so it is resolved once.
   private static uint s_pngFormat = 0;

   private static bool _HasImageFormat()
   {
      if (s_pngFormat == 0)
         s_pngFormat = RegisterClipboardFormatW("PNG");

      return IsClipboardFormatAvailable(CF_DIB)
          || IsClipboardFormatAvailable(CF_BITMAP)
          || (s_pngFormat != 0 && IsClipboardFormatAvailable(s_pngFormat));
   }

   [DllImport("user32.dll")]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool IsClipboardFormatAvailable(uint format);

   // Increments on every clipboard write by any process. Used only as an
   // identity: a value different from the last one we consumed means the
   // content is new, and that is the whole question being asked.
   [DllImport("user32.dll")]
   private static extern uint GetClipboardSequenceNumber();

   [DllImport("user32.dll", CharSet = CharSet.Unicode)]
   private static extern uint RegisterClipboardFormatW(string format);

   // -- Win10 custom frame ---------------------------------------------------

   private const uint WM_SETTEXT          = 0x000C;
   private const uint WM_SETICON          = 0x0080;
   private const uint WM_NCCALCSIZE       = 0x0083;
   private const uint WM_NCHITTEST        = 0x0084;
   private const uint WM_NCPAINT          = 0x0085;
   private const uint WM_NCACTIVATE       = 0x0086;
   private const uint WM_NCUAHDRAWCAPTION = 0x00AE;   // undocumented, sent by uxtheme
   private const uint WM_NCUAHDRAWFRAME   = 0x00AF;   // undocumented, sent by uxtheme

   private const int  GWL_STYLE  = -16;
   private const long WS_VISIBLE = 0x10000000;

   private const int HTLEFT        = 10;
   private const int HTRIGHT       = 11;
   private const int HTTOP         = 12;
   private const int HTTOPLEFT     = 13;
   private const int HTTOPRIGHT    = 14;
   private const int HTBOTTOM      = 15;
   private const int HTBOTTOMLEFT  = 16;
   private const int HTBOTTOMRIGHT = 17;

   private const int SM_CXSIZEFRAME = 32;

   /// <summary>
   /// Restored state only — maximized windows overhang the monitor by the
   /// frame width, and Avalonia's own NCCALCSIZE trims exactly that, so it
   /// keeps those; full-screen is likewise Avalonia's.
   ///   - WM_NCCALCSIZE: leave the proposed rect untouched, so the client
   ///     area covers the whole window and no light frame remains.
   ///   - WM_NCHITTEST: with the frame gone, the resize zones have to live
   ///     inside the client area — a thin band along each edge, wider at the
   ///     corners. Everything else falls through to Avalonia (caption drag,
   ///     caption buttons, client).
   ///
   /// On Win10 Avalonia sets DWMWA_NCRENDERING_POLICY to DISABLED for an
   /// extended window, so DWM no longer draws the frame: DefWindowProc paints
   /// the "Basic" caption and frame with GDI through the window DC — straight
   /// over our client pixels, where they stay until Avalonia's next frame
   /// covers them. Every path into that painter is cut off, in all window
   /// states (when maximized the frame is off-screen anyway):
   ///   - WM_NCPAINT: sent on focus and z-order changes.
   ///   - WM_NCUAHDRAWCAPTION / WM_NCUAHDRAWFRAME: uxtheme's own redraw
   ///     requests for caption and frame.
   ///   - WM_NCACTIVATE / WM_SETTEXT / WM_SETICON: DefWindowProc must still
   ///     run (activation state, stored title / icon) but repaints the frame
   ///     inline. WS_VISIBLE is dropped for the call so the state change
   ///     happens and the paint does not (the trick Chromium uses). The
   ///     documented lParam = -1 for WM_NCACTIVATE is no help: it is ignored
   ///     while a visual style is active, and that repaint on every focus
   ///     change was the frame that stuck after clicking out of the window.
   /// </summary>
   private static System.IntPtr _Win10FrameHook(Window window, System.IntPtr hWnd, uint msg,
                                                System.IntPtr wParam, System.IntPtr lParam, ref bool handled)
   {
      switch (msg)
      {
         case WM_NCCALCSIZE when wParam != System.IntPtr.Zero && _IsRestored(window, hWnd):
            handled = true;
            return System.IntPtr.Zero;

         case WM_NCHITTEST when _IsRestored(window, hWnd):
            int hit = _ResizeHitTest(hWnd, lParam);
            if (hit != 0)
            {
               handled = true;
               return new System.IntPtr(hit);
            }
            break;

         case WM_NCPAINT:
         case WM_NCUAHDRAWCAPTION:
         case WM_NCUAHDRAWFRAME:
            handled = true;
            return System.IntPtr.Zero;

         case WM_NCACTIVATE:
         case WM_SETTEXT:
         case WM_SETICON:
            handled = true;
            return _DefWindowProcWithoutPaint(hWnd, msg, wParam, lParam);
      }
      return System.IntPtr.Zero;
   }

   private static System.IntPtr _DefWindowProcWithoutPaint(System.IntPtr hWnd, uint msg,
                                                           System.IntPtr wParam, System.IntPtr lParam)
   {
      long style = (long)GetWindowLongPtrW(hWnd, GWL_STYLE);
      if ((style & WS_VISIBLE) == 0)
         return DefWindowProcW(hWnd, msg, wParam, lParam);

      SetWindowLongPtrW(hWnd, GWL_STYLE, new System.IntPtr(style & ~WS_VISIBLE));
      try     { return DefWindowProcW(hWnd, msg, wParam, lParam); }
      finally { SetWindowLongPtrW(hWnd, GWL_STYLE, new System.IntPtr(style)); }
   }

   private static bool _IsRestored(Window window, System.IntPtr hWnd) =>
      window.WindowState != WindowState.FullScreen && !IsZoomed(hWnd) && !IsIconic(hWnd);

   /// <summary>Resize hit code for a screen point near the window edge, or 0.</summary>
   private static int _ResizeHitTest(System.IntPtr hWnd, System.IntPtr lParam)
   {
      if (!GetWindowRect(hWnd, out var rc))
         return 0;

      // Screen coordinates, signed 16-bit each (negative on left/top monitors).
      int x = (short)((long)lParam & 0xFFFF);
      int y = (short)(((long)lParam >> 16) & 0xFFFF);

      // SM_CXSIZEFRAME alone (≈4 px at 100 %): thin enough not to eat into the
      // scrollbars that sit flush against the right edge. Corners get double
      // that along each edge so diagonal resizing stays easy to hit.
      int edge   = GetSystemMetricsForDpi(SM_CXSIZEFRAME, GetDpiForWindow(hWnd));
      int corner = edge * 2;

      bool left   = x <  rc.Left   + edge;
      bool right  = x >= rc.Right  - edge;
      bool top    = y <  rc.Top    + edge;
      bool bottom = y >= rc.Bottom - edge;

      bool nearLeft   = x <  rc.Left   + corner;
      bool nearRight  = x >= rc.Right  - corner;
      bool nearTop    = y <  rc.Top    + corner;
      bool nearBottom = y >= rc.Bottom - corner;

      if ((top && nearLeft)     || (left && nearTop))     return HTTOPLEFT;
      if ((top && nearRight)    || (right && nearTop))    return HTTOPRIGHT;
      if ((bottom && nearLeft)  || (left && nearBottom))  return HTBOTTOMLEFT;
      if ((bottom && nearRight) || (right && nearBottom)) return HTBOTTOMRIGHT;
      if (left)   return HTLEFT;
      if (right)  return HTRIGHT;
      if (top)    return HTTOP;
      if (bottom) return HTBOTTOM;
      return 0;
   }

   [StructLayout(LayoutKind.Sequential)]
   private struct RECT
   {
      public int Left, Top, Right, Bottom;
   }

   [DllImport("user32.dll")]
   private static extern System.IntPtr DefWindowProcW(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);

   [DllImport("user32.dll")]
   private static extern System.IntPtr GetWindowLongPtrW(System.IntPtr hWnd, int index);

   [DllImport("user32.dll")]
   private static extern System.IntPtr SetWindowLongPtrW(System.IntPtr hWnd, int index, System.IntPtr value);

   [DllImport("user32.dll")]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);

   [DllImport("user32.dll")]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool IsZoomed(System.IntPtr hWnd);

   [DllImport("user32.dll")]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool IsIconic(System.IntPtr hWnd);

   [DllImport("user32.dll")]
   private static extern uint GetDpiForWindow(System.IntPtr hWnd);

   [DllImport("user32.dll")]
   private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
