// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop;

/// <summary>
/// "Check for updates" on the About page. Network access happens only when the
/// user clicks the button; see <see cref="UpdateService"/> and
/// <see cref="UpdateInstaller"/> for the mechanics.
/// </summary>
public partial class MainWindow
{
   private bool _updateBusy;

   private Control BuildUpdateSection()
   {
      var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0), Spacing = 6 };

      // Globe glyph: says "this goes online" before the user clicks. Drawn
      // as a path (outline, meridian, three parallels) rather than an emoji,
      // which the UI font may not carry.
      var globe = new Shapes.Path
      {
         Data            = Geometry.Parse(
            "M 8,0.75 A 7.25,7.25 0 1 0 8,15.25 A 7.25,7.25 0 1 0 8,0.75 Z "
          + "M 8,0.75 A 3.25,7.25 0 1 0 8,15.25 A 3.25,7.25 0 1 0 8,0.75 Z "
          + "M 0.75,8 H 15.25 M 2.1,4.3 H 13.9 M 2.1,11.7 H 13.9"),
         StrokeThickness = 1.2,
         Width           = 16,
         Height          = 16,
         Stretch         = Stretch.None,
         VerticalAlignment = VerticalAlignment.Center,
      };

      var button = new Button
      {
         Content = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Children    = { globe, new TextBlock { Text = "Check for updates", VerticalAlignment = VerticalAlignment.Center } },
         },
         FontSize            = 11,
         Padding             = new Thickness(10, 5),
         HorizontalAlignment = HorizontalAlignment.Left,
      };
      // Follow the button's text color through hover / disabled states.
      globe.Bind(Shapes.Shape.StrokeProperty, button.GetObservable(TemplatedControl.ForegroundProperty));
      ToolTip.SetTip(button, "Ask GitHub for the latest release of " + AppInfo.Name + ".");

      var status = new SelectableTextBlock
      {
         FontSize     = 11,
         Foreground   = ThemeBrushes.TextMuted,
         TextWrapping = TextWrapping.Wrap,
         IsVisible    = false,
      };

      button.Click += async (_, _) => await _CheckForUpdatesAsync(button, status);

      panel.Children.Add(button);
      panel.Children.Add(status);
      return panel;
   }

   private async Task _CheckForUpdatesAsync(Button button, SelectableTextBlock status)
   {
      if (_updateBusy)
         return;

      if (AppInfo.IsDevBuild)
      {
         status.Text      = "Dev build - no update check.";
         status.IsVisible = true;
         return;
      }

      _updateBusy = true;
      button.IsEnabled = false;

      void Status(string text)
      {
         status.Text      = text;
         status.IsVisible = true;
      }

      string? tempDir = null;
      try
      {
         Status("Checking…");
         var rel = await UpdateService.CheckLatestAsync();

         if (!UpdateService.IsNewer(rel.Version, AppInfo.Version))
         {
            Status("You are up to date (" + AppInfo.Version + "; latest release " + rel.Tag + ").");
            return;
         }

         var blocker = rel.AssetApiUrl == null
            ? "The release has no package for " + UpdateService.RuntimeId + "."
            : UpdateInstaller.GetInstallBlocker();

         if (blocker != null)
         {
            Status(rel.Tag + " is available. " + blocker);
            if (await ShowConfirm("Update available",
                                  rel.Tag + " is available (installed " + AppInfo.Version + ").\n" + blocker,
                                  "Open release page", "Close"))
            {
               _OpenUrl(rel.HtmlUrl);
            }
            return;
         }

         Status(rel.Tag + " is available.");
         var message = rel.Tag + " is available (installed " + AppInfo.Version + ").\n"
                     + "Install it now? SlimCode restarts and running sessions are stopped.";

         // Other profiles running from this folder are not restarted and stay
         // on the old version; say so before anything changes.
         int others = UpdateInstaller.CountOtherInstances(UpdateInstaller.InstallDir);
         if (others > 0)
         {
            message += "\n\n" + others + " other SlimCode window" + (others == 1 ? " is" : "s are")
                     + " running from this folder (other profiles). Close "
                     + (others == 1 ? "it" : "them") + " before updating, or restart "
                     + (others == 1 ? "it" : "them") + " right after -- until then "
                     + (others == 1 ? "it keeps" : "they keep") + " running the old version.";
         }

         if (!await ShowConfirm("Update available", message, "Update", "Later"))
         {
            return;
         }

         tempDir = Path.Combine(Path.GetTempPath(), "slimcode-update-" + Guid.NewGuid().ToString("N"));
         var progress = new Progress<double>(p => Status("Downloading " + rel.Tag + "… " + (int)(p * 100) + " %"));
         var archive  = await UpdateService.DownloadAsync(rel, tempDir, progress);

         Status("Installing " + rel.Tag + "…");
         var installDir = UpdateInstaller.InstallDir;

         // Restart with the same command line, so --profile survives.
         // Scheduled before the swap on purpose (see ScheduleRelaunch).
         UpdateInstaller.ScheduleRelaunch(
            Path.Combine(installDir, UpdateInstaller.AppExeName),
            Environment.GetCommandLineArgs().Skip(1).ToArray());
         try
         {
            await Task.Run(() =>
            {
               var staging = UpdateInstaller.Stage(archive, installDir, UpdateInstaller.AppExeName);
               UpdateInstaller.Apply(installDir, staging);
            });
         }
         catch
         {
            UpdateInstaller.CancelRelaunch();
            throw;
         }

         Status("Restarting…");
         if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
         else
            Close();
      }
      catch (Exception ex)
      {
         Status("Update failed: " + ex.Message);
      }
      finally
      {
         if (tempDir != null)
         {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
         }
         _updateBusy = false;
         button.IsEnabled = true;
      }
   }

   private static void _OpenUrl(string url)
   {
      if (string.IsNullOrEmpty(url))
         return;
      try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
      catch { }
   }
}
