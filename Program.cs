using Avalonia;
using SlimCodeDesktop.Core;
using System;

namespace SlimCodeDesktop;

class Program
{
   [STAThread]
   public static void Main(string[] args)
   {
      // First: every path in the app -- including the crash log the handler
      // below writes to -- hangs off the profile selected here.
      AppPaths.Initialize(args);
      CrashLog.Install();
      try
      {
         BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
      }
      catch (Exception ex)
      {
         // An exception escaping the dispatcher loop is the usual way this app
         // dies. Catch it here as well as in the AppDomain handler -- this is
         // the one place the full stack is guaranteed to be intact.
         CrashLog.Write("Main", ex);
         throw;
      }
   }

   public static AppBuilder BuildAvaloniaApp()
       => AppBuilder.Configure<App>()
           .UsePlatformDetect()
           .With(new Win32PlatformOptions
           {
              RenderingMode = [Win32RenderingMode.Software]
           })
#if DEBUG
           .WithDeveloperTools()
#endif
           .WithInterFont()
           .LogToTrace();
}
