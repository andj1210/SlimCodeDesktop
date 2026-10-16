// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Agents;
using SlimCodeDesktop.Agents.Settings;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Themes;
using SlimCodeDesktop.Views.Native.Controls;

namespace SlimCodeDesktop;

/// <summary>
/// Small modal helpers owned by the main window: error, info, text prompt
/// and yes/no confirmation.
/// </summary>
public partial class MainWindow
{
   private async Task ShowError(string message)
   {
      var dlg = new Window
      {
         Title = "SlimCode Desktop",
         Width = 360,
         Height = 120,
         WindowStartupLocation = WindowStartupLocation.CenterOwner,
         CanResize = false,
         Background = ThemeBrushes.PanelBackground,
      };

      var tb = new TextBlock
      {
         Text = message,
         Foreground = ThemeBrushes.TextPrimary,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(16, 18, 16, 10),
      };

      var btn = new Button
      {
         Content = "OK",
         HorizontalAlignment = HorizontalAlignment.Center,
         Margin = new Thickness(0, 0, 0, 12),
      };

      dlg.Content = new StackPanel { Children = { tb, btn } };
      btn.Click += (_, _) => dlg.Close();
      await dlg.ShowDialog(this);
   }

   // Same shape as ShowError, but titled and height-fitted: used for the
   // longer "here's why that didn't happen" messages.
   private async Task ShowInfo(string title, string message)
   {
      var dlg = new Window
      {
         Title = title,
         Width = 440,
         SizeToContent = SizeToContent.Height,
         WindowStartupLocation = WindowStartupLocation.CenterOwner,
         CanResize = false,
         Background = ThemeBrushes.PanelBackground,
      };

      var tb = new TextBlock
      {
         Text = message,
         Foreground = ThemeBrushes.TextPrimary,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(16, 18, 16, 10),
      };

      var btn = new Button
      {
         Content = "OK",
         IsDefault = true,
         HorizontalAlignment = HorizontalAlignment.Center,
         Margin = new Thickness(0, 0, 0, 12),
      };

      dlg.Content = new StackPanel { Children = { tb, btn } };
      btn.Click += (_, _) => dlg.Close();
      await dlg.ShowDialog(this);
   }

   // Modal text-input prompt. Returns the entered string, or null if the user
   // cancelled or left it blank.
   private async Task<string?> ShowPrompt(string title, string label, string initial = "")
   {
      var dlg = new Window
      {
         Title = title,
         Width = 360,
         Height = 160,
         WindowStartupLocation = WindowStartupLocation.CenterOwner,
         CanResize = false,
         Background = ThemeBrushes.PanelBackground,
      };

      var lbl = new TextBlock
      {
         Text = label,
         Foreground = ThemeBrushes.TextPrimary,
         Margin = new Thickness(16, 18, 16, 4),
      };
      var input = new TextBox
      {
         Text = initial,
         Margin = new Thickness(16, 0, 16, 10),
      };
      var ok = new Button { Content = "OK", Width = 70, IsDefault = true };
      var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
      var buttons = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         HorizontalAlignment = HorizontalAlignment.Center,
         Spacing = 8,
         Margin = new Thickness(0, 0, 0, 12),
         Children = { ok, cancel },
      };
      dlg.Content = new StackPanel { Children = { lbl, input, buttons } };

      string? result = null;
      ok.Click += (_, _) => { result = input.Text; dlg.Close(); };
      cancel.Click += (_, _) => { result = null; dlg.Close(); };

      input.AttachedToVisualTree += (_, _) => input.Focus();
      await dlg.ShowDialog(this);
      return string.IsNullOrWhiteSpace(result) ? null : result;
   }

   // Yes/No confirmation dialog. Returns true when the user clicked the
   // affirmative button.
   private async Task<bool> ShowConfirm(string title, string message,
                                        string okLabel = "OK", string cancelLabel = "Cancel")
   {
      var dlg = new Window
      {
         Title = title,
         Width = 400,
         // Height-fitted so longer messages (e.g. the update warning) are not clipped.
         SizeToContent = SizeToContent.Height,
         WindowStartupLocation = WindowStartupLocation.CenterOwner,
         CanResize = false,
         Background = ThemeBrushes.PanelBackground,
      };

      var tb = new TextBlock
      {
         Text = message,
         Foreground = ThemeBrushes.TextPrimary,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(16, 18, 16, 12),
      };
      var ok = new Button { Content = okLabel, Width = 80, IsDefault = true };
      var cancel = new Button { Content = cancelLabel, Width = 80, IsCancel = true };
      var buttons = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         HorizontalAlignment = HorizontalAlignment.Center,
         Spacing = 8,
         Margin = new Thickness(0, 0, 0, 12),
         Children = { ok, cancel },
      };
      dlg.Content = new StackPanel { Children = { tb, buttons } };

      bool result = false;
      ok.Click += (_, _) => { result = true; dlg.Close(); };
      cancel.Click += (_, _) => { result = false; dlg.Close(); };

      await dlg.ShowDialog(this);
      return result;
   }
}
