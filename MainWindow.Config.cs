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
/// Configuration view: the Application / About pages and one tab per agent,
/// with settings rows generated from [Setting] properties.
/// </summary>
public partial class MainWindow
{
   // Build a tabbed configuration view: one "Application" tab plus one tab
   // per registered agent. Tabs run down the left edge so the list scales as
   // we add agents. Re-run on every open so the active agent's tab is preselected.
   private void RebuildConfigView()
   {
      ConfigPanel.Children.Clear();

      ConfigPanel.Children.Add(new TextBlock
      {
         Text = "Configuration",
         Foreground = ThemeBrushes.Accent,
         FontSize = 18,
         FontWeight = FontWeight.Bold,
         Margin = new Thickness(0, 0, 0, 12),
      });

      var tabs = new TabControl
      {
         TabStripPlacement = Dock.Left,
         Padding = new Thickness(0),
      };

      // Application tab
      tabs.Items.Add(new TabItem
      {
         Header = "Application",
         Content = BuildSettingsPage(
            "These settings apply to SlimCode itself, regardless of which agent is running.",
            _appSettings.Options,
            _appSettings.Values,
            // The sidebar rebuild is for the MCP switch, whose "Add MCP…"
            // button should appear the moment it is flipped rather than at
            // the next restart.
            (d, v) => { _appSettings.Set(d, v); ApplyAccent(); RebuildSidebar(); },
            BuildStorageSection()),
      });

      // One tab per registered agent, with the agent of the active session
      // tab preselected.
      var current = _activeTab != null
         ? AgentRegistry.All.FirstOrDefault(a => a.IsNative)
         : null;
      TabItem? currentTab = null;
      foreach (var agent in AgentRegistry.All)
      {
         var captured = agent;
         Control body;
         if (agent is Agents.Native.NativeAgent na)
         {
            body = BuildNativeAgentPage(na);
         }
         else
         {
            body = BuildSettingsPage(
               "These settings apply only to " + agent.DisplayName +
               " inside SlimCode. They do not modify the agent's own configuration.",
               agent.Options,
               agent.SettingsObj,
               (d, v) => _settingsStore.Set(captured, d, v));
         }
         var tab = new TabItem
         {
            Header = agent.DisplayName,
            Content = body,
         };
         tabs.Items.Add(tab);
         if (current != null && current.Id == agent.Id) currentTab = tab;
      }

      // About tab — always last.
      tabs.Items.Add(new TabItem
      {
         Header  = "About",
         Content = BuildAboutPage(),
      });

      if (currentTab != null) tabs.SelectedItem = currentTab;
      ConfigPanel.Children.Add(tabs);
   }

   // Static product / build / license info. Read from assembly metadata via
   // AppInfo so the version stays in lockstep with the .csproj.
   private Control BuildAboutPage()
   {
      var panel = new StackPanel { Margin = new Thickness(16, 8, 8, 8), Spacing = 2 };

      panel.Children.Add(new Image
      {
         Source              = AppIcons.Large,
         Width               = 96,
         Height              = 96,
         HorizontalAlignment = HorizontalAlignment.Left,
         Margin              = new Thickness(0, 0, 0, 10),
      });

      panel.Children.Add(new TextBlock
      {
         Text       = AppInfo.Name,
         Foreground = ThemeBrushes.Accent,
         FontSize   = 20,
         FontWeight = FontWeight.Bold,
      });
      panel.Children.Add(new TextBlock
      {
         Text       = "Version " + AppInfo.Version,
         Foreground = ThemeBrushes.TextPrimary,
         FontSize   = 13,
         Margin     = new Thickness(0, 0, 0, 14),
      });

      panel.Children.Add(AboutRow("Build",     AppInfo.BuildInfo));
      if (!string.IsNullOrEmpty(AppInfo.Commit))
         panel.Children.Add(AboutRow("Commit",  AppInfo.Commit));
      panel.Children.Add(AboutRow("License",   AppInfo.License));
      panel.Children.Add(AboutRow("Author",    AppInfo.Author));

      panel.Children.Add(BuildUpdateSection());

      panel.Children.Add(new TextBlock
      {
         Text       = "Copyright © 2026 " + AppInfo.Author,
         Foreground = ThemeBrushes.TextMuted,
         FontSize   = 11,
         Margin     = new Thickness(0, 16, 0, 0),
      });

      return new ScrollViewer { Content = panel };
   }

   // One "Label: value" row for the About page. Label is fixed-width so the
   // values line up; value is selectable so the user can copy the commit hash.
   private static Control AboutRow(string label, string value)
   {
      var grid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("90,*"),
         Margin            = new Thickness(0, 2),
      };
      var labelTb = new TextBlock
      {
         Text       = label,
         Foreground = ThemeBrushes.TextMuted,
         FontSize   = 12,
      };
      var valueTb = new SelectableTextBlock
      {
         Text         = value,
         Foreground   = ThemeBrushes.TextPrimary,
         FontSize     = 12,
         TextWrapping = TextWrapping.Wrap,
      };
      Grid.SetColumn(labelTb, 0);
      Grid.SetColumn(valueTb, 1);
      grid.Children.Add(labelTb);
      grid.Children.Add(valueTb);
      return grid;
   }

   // One tab's body: a scrollable column with a description blurb and one row
   // per [Setting] property on <paramref name="instance"/>. <paramref name="writer"/>
   // is invoked with (descriptor, newValue) whenever the user edits a control.
   private Control BuildSettingsPage(
      string blurb,
      IReadOnlyList<SettingDescriptor> opts,
      object instance,
      Action<SettingDescriptor, object?> writer,
      Control? extraSection = null)
   {
      var panel = new StackPanel { Margin = new Thickness(16, 8, 8, 8) };

      panel.Children.Add(new TextBlock
      {
         Text = blurb,
         Foreground = ThemeBrushes.TextMuted,
         FontSize = 11,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 0, 0, 16),
      });

      if (opts.Count == 0)
      {
         panel.Children.Add(new TextBlock
         {
            Text = "(no configurable options)",
            Foreground = ThemeBrushes.TextMuted,
            FontSize = 12,
         });
      }
      else
      {
         foreach (var desc in opts)
            panel.Children.Add(BuildOptionRow(instance, desc, v => writer(desc, v)));
      }

      if (extraSection != null)
         panel.Children.Add(extraSection);

      return new ScrollViewer { Content = panel };
   }

   // Re-derive the accent brushes after any Application setting changes. Cheap
   // enough (five brush allocations) not to bother filtering for the two that
   // actually matter, and filtering would silently break if a third is added.
   //
   // Buttons and tab strips follow immediately via {DynamicResource}; chat
   // bubbles, tool cards and gauges already on screen hold the brush they were
   // built with, so they stay on the old color until restart. Both settings say
   // so in their help text.
   private void ApplyAccent()
   {
      var app = Application.Current;
      if (app == null) return;

      var v = _appSettings.Values;
      AccentPalette.Apply(app, AccentPalette.Resolve(v.Accent, v.AccentColorHex, AppPaths.Profile));
   }

   // Custom section on the Application settings page: where SlimCode keeps its
   // own files. Worth surfacing because the location is otherwise pure folklore
   // -- nothing in the UI hints at %APPDATA%, and it is the folder you want
   // when backing up sessions or clearing state.
   private static Control BuildStorageSection()
   {
      var panel = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };

      panel.Children.Add(new Border
      {
         Height              = 1,
         Background          = ThemeBrushes.BorderColor,
         Margin              = new Thickness(0, 0, 0, 16),
         HorizontalAlignment = HorizontalAlignment.Stretch,
      });

      panel.Children.Add(new TextBlock
      {
         Text       = "Storage location",
         FontSize   = 13,
         FontWeight = FontWeight.SemiBold,
         Foreground = ThemeBrushes.TextPrimary,
      });

      panel.Children.Add(new TextBlock
      {
         Text         = "These settings live in the file below. Its folder also holds the per-agent settings, session transcripts, permission policies and the crash log — all of it scoped to the active profile (" + AppPaths.Profile + "). Start SlimCode with --profile <name> to keep a separate set.",
         FontSize     = 11,
         Foreground   = ThemeBrushes.TextMuted,
         TextWrapping = TextWrapping.Wrap,
         Margin       = new Thickness(0, 2, 0, 8),
      });

      var button = new Button
      {
         Content           = "Show in Explorer",
         FontSize          = 11,
         Padding           = new Thickness(10, 3),
         Margin            = new Thickness(0, 0, 10, 0),
         VerticalAlignment = VerticalAlignment.Top,
      };
      ToolTip.SetTip(button, "Open the containing folder in Explorer with this file selected.");
      button.Click += (_, _) => ExternalTools.RevealFile(AppPaths.AppSettingsFile);

      // Selectable so the path can be copied into a shell or a bug report.
      var pathText = new SelectableTextBlock
      {
         Text              = AppPaths.AppSettingsFile,
         FontSize          = 12,
         Foreground        = ThemeBrushes.TextPrimary,
         TextWrapping      = TextWrapping.Wrap,
         VerticalAlignment = VerticalAlignment.Center,
      };

      var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
      Grid.SetColumn(button, 0);
      Grid.SetColumn(pathText, 1);
      row.Children.Add(button);
      row.Children.Add(pathText);
      panel.Children.Add(row);

      return panel;
   }

   // Native-agent tab body: a dedicated model-management section above the
   // generic reflection-driven settings. Splitting the build path (instead of
   // extending the reflection generator) keeps native-only concerns out of
   // the shared SettingDescriptor pipeline.
   private Control BuildNativeAgentPage(Agents.Native.NativeAgent na)
   {
      var panel = new StackPanel { Margin = new Thickness(16, 8, 8, 8) };

      panel.Children.Add(new TextBlock
      {
         Text = "These settings apply only to " + na.DisplayName + " inside SlimCode.",
         Foreground = ThemeBrushes.TextMuted,
         FontSize = 11,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 0, 0, 16),
      });

      panel.Children.Add(BuildNativeAgentModelSection(na));

      panel.Children.Add(new Border
      {
         Height = 1,
         Background = ThemeBrushes.BorderColor,
         Margin = new Thickness(0, 16, 0, 16),
         HorizontalAlignment = HorizontalAlignment.Stretch,
      });

      panel.Children.Add(BuildNativeAgentPermissionsSection());

      panel.Children.Add(new Border
      {
         Height = 1,
         Background = ThemeBrushes.BorderColor,
         Margin = new Thickness(0, 16, 0, 16),
         HorizontalAlignment = HorizontalAlignment.Stretch,
      });

      foreach (var desc in na.Options)
         panel.Children.Add(BuildOptionRow(na.SettingsObj, desc, v => _settingsStore.Set(na, desc, v)));

      return new ScrollViewer { Content = panel };
   }

   // Global permission editor entry point. Project-level permissions and
   // the audit log are reached from the chat view (they need the project
   // path, which isn't necessarily set when the user is in app settings).
   private Control BuildNativeAgentPermissionsSection()
   {
      var section = new StackPanel { Spacing = 8 };

      section.Children.Add(new TextBlock
      {
         Text       = "Permissions",
         FontSize   = 12,
         FontWeight = FontWeight.SemiBold,
         Foreground = ThemeBrushes.TextPrimary,
      });

      section.Children.Add(new TextBlock
      {
         Text         = "Set the app-wide policy for each tool. Layered Allow/Deny can be overridden by per-project settings; AlwaysAsk and AlwaysDeny are sticky and cannot be overridden — use them for tools that must always be approved or never run.",
         FontSize     = 11,
         Foreground   = ThemeBrushes.TextMuted,
         TextWrapping = TextWrapping.Wrap,
      });

      var btnGlobal = new Button { Content = "Edit global permissions…", Padding = new Thickness(12, 4) };
      btnGlobal.Click += (_, _) =>
      {
         var store = Agents.Native.Permissions.GlobalPolicyStore.LoadOrCreate();
         var tools = Agents.Native.Tools.ToolRegistry.CreateFullDefaults();
         var win   = new Views.Native.PermissionEditorWindow();
         win.Configure(
            Views.Native.PermissionEditorWindow.Mode.Global,
            store.Policy,
            tools,
            onSave: updated =>
            {
               store.Policy.Replace(updated.Tools);
               store.Save();
            },
            contextHint: store.FilePath);
         win.ShowDialog(this);
      };

      var row = new StackPanel
      {
         Orientation = Avalonia.Layout.Orientation.Horizontal,
         Spacing     = 6,
      };
      row.Children.Add(btnGlobal);
      section.Children.Add(row);

      return section;
   }

   // Native-agent-only block: open the provider/model catalog. Selection
   // itself lives in the chat view (the user may want to switch providers or
   // models mid-conversation).
   //
   // The catalog window mutates `na.Settings.Providers` directly; it persists
   // via SaveAgent (re-serializes the whole POCO) — a plain Save would write
   // the cached pre-mutation snapshot in AgentSettingsStore._byAgent and the
   // change would be silently lost on disk.
   private Control BuildNativeAgentModelSection(Agents.Native.NativeAgent na)
   {
      var section = new StackPanel { Spacing = 8 };

      section.Children.Add(new TextBlock
      {
         Text       = "Providers & models",
         FontSize   = 12,
         FontWeight = FontWeight.SemiBold,
         Foreground = ThemeBrushes.TextPrimary,
      });

      section.Children.Add(new TextBlock
      {
         Text         = "Configure one or more OpenAI-compatible providers (name + endpoint + key), each with its own models. Discover queries a provider's /v1/models list and merges new ids into its catalog; you can also adjust per-model context windows or add manual entries. Pick the active model — shown with its provider — from the dropdown in the chat view.",
         FontSize     = 11,
         Foreground   = ThemeBrushes.TextMuted,
         TextWrapping = TextWrapping.Wrap,
      });

      var btnEdit = new Button { Content = "Edit providers & models…", Padding = new Thickness(12, 4) };
      btnEdit.Click += (_, _) =>
      {
         var win = new Views.Native.ModelCatalogWindow();
         win.Configure(na.Settings, onSaved: () => _settingsStore.SaveAgent(na));
         win.ShowDialog(this);
      };
      section.Children.Add(btnEdit);

      return section;
   }

   // Auto-render one row from a SettingDescriptor. Switches on the property's
   // CLR type so adding new control kinds (int, string, …) is additive. The
   // <paramref name="writer"/> callback is invoked with the new value whenever
   // the user edits the control — the caller decides where to persist.
   private Control BuildOptionRow(object instance, SettingDescriptor desc, Action<object?> writer)
   {
      var label = new TextBlock
      {
         Text = desc.Label,
         Foreground = ThemeBrushes.TextPrimary,
         FontSize = 12,
         FontWeight = FontWeight.SemiBold,
      };

      var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
      panel.Children.Add(label);

      Control input;
      var current = desc.GetValue(instance);

      if (desc.Kind == typeof(bool))
      {
         var cb = new CheckBox
         {
            IsChecked = current is bool b && b,
            Margin = new Thickness(0, 4, 0, 0),
            Content = "Enabled",
         };
         cb.IsCheckedChanged += (_, _) => writer(cb.IsChecked == true);
         input = cb;
      }
      else if (desc.Kind.IsEnum && desc.Choices != null)
      {
         var choices = desc.Choices;
         int curIdx = 0;
         for (int i = 0; i < choices.Count; i++)
            if (Equals(choices[i].Value, current)) { curIdx = i; break; }

         var labels = new string[choices.Count];
         for (int i = 0; i < choices.Count; i++) labels[i] = choices[i].Label;

         var combo = new ComboBox
         {
            ItemsSource = labels,
            SelectedIndex = curIdx,
            MinWidth = 200,
            Margin = new Thickness(0, 4, 0, 0),
         };
         combo.SelectionChanged += (_, _) =>
         {
            int idx = combo.SelectedIndex;
            if (idx >= 0 && idx < choices.Count) writer(choices[idx].Value);
         };
         input = combo;
      }
      else if (desc.Kind == typeof(string))
      {
         var tb = new TextBox
         {
            Text          = current as string ?? "",
            Margin        = new Thickness(0, 4, 0, 0),
            Foreground    = ThemeBrushes.TextPrimary,
            Background    = ThemeBrushes.PanelBackground,
            CaretBrush    = ThemeBrushes.TextPrimary,
            BorderBrush   = ThemeBrushes.BorderColor,
            AcceptsReturn = desc.Multiline,
            TextWrapping  = desc.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight     = desc.Multiline ? 80  : 0,
            MaxHeight     = desc.Multiline ? 240 : double.PositiveInfinity,
         };
         // LostFocus (not TextChanged) avoids one disk-write per keystroke.
         tb.LostFocus += (_, _) => writer(tb.Text ?? "");
         input = tb;
      }
      else if (desc.Kind == typeof(int))
      {
         int initial = current is int ci ? ci : 0;
         if (desc.Increment > 0 || desc.Suggestions != null)
         {
            var box = new NumericSuggestionBox(
               initial,
               desc.Increment > 0 ? desc.Increment : 1,
               desc.Suggestions,
               desc.Min,
               desc.Max)
            {
               Margin = new Thickness(0, 4, 0, 0),
            };
            box.ValueChanged += v => writer(v);
            input = box;
         }
         else
         {
            var nud = new NumericUpDown
            {
               Value        = initial,
               Minimum      = desc.Min ?? 0,
               Maximum      = desc.Max ?? 1_000_000,
               Increment    = 1,
               FormatString = "0",
               Width        = 160,
               HorizontalAlignment = HorizontalAlignment.Left,
               Margin       = new Thickness(0, 4, 0, 0),
               Foreground   = ThemeBrushes.TextPrimary,
               Background   = ThemeBrushes.PanelBackground,
            };
            nud.ValueChanged += (_, _) =>
            {
               if (nud.Value is decimal d) writer((int)d);
            };
            input = nud;
         }
      }
      else if (desc.Kind == typeof(float) || desc.Kind == typeof(double))
      {
         var nud = new NumericUpDown
         {
            Value        = current switch
            {
               float f  => (decimal)f,
               double d => (decimal)d,
               _        => 0m,
            },
            Minimum      = 0m,
            Maximum      = 2m,
            Increment    = 0.1m,
            FormatString = "0.0",
            Width        = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin       = new Thickness(0, 4, 0, 0),
            Foreground   = ThemeBrushes.TextPrimary,
            Background   = ThemeBrushes.PanelBackground,
         };
         nud.ValueChanged += (_, _) =>
         {
            if (nud.Value is decimal d)
            {
               if (desc.Kind == typeof(float))
                  writer((float)d);
               else
                  writer((double)d);
            }
         };
         input = nud;
      }
      else
      {
         input = new TextBlock { Text = "(unsupported option type: " + desc.Kind.Name + ")" };
      }
      panel.Children.Add(input);

      if (!string.IsNullOrEmpty(desc.Help))
      {
         panel.Children.Add(new TextBlock
         {
            Text = desc.Help,
            Foreground = ThemeBrushes.TextMuted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
         });
      }

      return panel;
   }
}
