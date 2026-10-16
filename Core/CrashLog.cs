// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Core
{
   /// <summary>
   /// Last-resort crash recorder. The app is a WinExe, so an unhandled
   /// exception on the UI thread takes the process down with nothing printed
   /// anywhere -- no console is attached to write it to. This appends the
   /// exception and its stack to <see cref="AppPaths.CrashLogFile"/> so a
   /// user-reported "it just closes" can be diagnosed after the fact.
   ///
   /// Every write is best-effort: a logger that throws during a crash would
   /// replace the interesting exception with a boring one.
   /// </summary>
   public static class CrashLog
   {
      /// <summary>Hook the process-wide handlers. Call once, first thing in Main.</summary>
      public static void Install()
      {
         AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

         TaskScheduler.UnobservedTaskException += (_, e) =>
         {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
         };
      }

      /// <summary>Append one entry. Silently does nothing if it cannot write.</summary>
      public static void Write(string origin, Exception? ex)
      {
         try
         {
            var sb = new StringBuilder();
            sb.Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("  [").Append(origin).AppendLine("]");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();

            var path = AppPaths.CrashLogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, sb.ToString());
         }
         catch { }
      }
   }
}
