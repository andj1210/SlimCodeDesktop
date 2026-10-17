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
/// Project sidebar in list mode: adding folders / MCP projects / brackets,
/// and the bracket and project rows with their context menus.
/// </summary>
public partial class MainWindow
{
   // -- project sidebar -------------------------------------------------------

   private async void BtnAddFolder_Click(object? sender, RoutedEventArgs e) =>
      await _AddProject(ProjectKind.Code);

   private async void BtnAddMcp_Click(object? sender, RoutedEventArgs e) =>
      await _AddProject(ProjectKind.Mcp);

   /// <summary>
   /// Pick a folder and add it as a project of the given kind. The kind is
   /// fixed here, at creation, and never changes afterwards: it selects the
   /// project's storage slug and its whole tool set, so a mid-life switch
   /// would be a migration, not a setting.
   ///
   /// An MCP project is still folder-backed — the folder is where its
   /// transcripts are keyed and the default working directory for stdio
   /// servers — it simply gets no file tools over that folder.
   /// </summary>
   private async Task _AddProject(ProjectKind kind)
   {
      var title = kind == ProjectKind.Mcp ? "Add MCP project folder" : "Add project folder";
      var opts = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
      var result = await StorageProvider.OpenFolderPickerAsync(opts);
      if (result.Count == 0) return;

      var path = result[0].TryGetLocalPath();
      if (string.IsNullOrEmpty(path)) return;

      if (!_projects.AddProject(path, kind))
      {
         await ShowInfo("Already added",
            "This folder is already in the project list:\n\n" + path +
            "\n\nA folder can only be added once — a project's path is its identity, " +
            "so the same folder cannot be both a code project and an MCP project.");
         return;
      }

      _projects.Save(_projectsFile);
      RebuildSidebar();

      // Stamp the kind into the project's own config so the agent side sees it
      // without consulting the sidebar, then go straight to setup: an MCP
      // project with no servers configured has no tools at all.
      _StampKind(path, kind);
      if (kind == ProjectKind.Mcp) OpenProjectSetup(path);
   }

   /// <summary>
   /// Same as <see cref="_AddProject"/>, but the picked folder lands inside a
   /// bracket. A folder already in the list is moved into the bracket (the
   /// long-standing behaviour of this entry) and keeps the kind it was created
   /// with — only a genuinely new project takes the kind from the menu entry.
   /// </summary>
   private async Task _AddProjectToBracket(string bracketName, ProjectKind kind)
   {
      var title = kind == ProjectKind.Mcp ? "Add MCP project folder" : "Add project folder";
      var opts = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
      var result = await StorageProvider.OpenFolderPickerAsync(opts);
      if (result.Count == 0) return;

      var path = result[0].TryGetLocalPath();
      if (string.IsNullOrEmpty(path)) return;

      bool isNew = !_projects.Contains(path);
      _projects.MoveProject(path, bracketName);
      if (isNew) _projects.SetKind(path, kind);
      _projects.Save(_projectsFile);
      RebuildSidebar();

      if (!isNew) return;
      _StampKind(path, kind);
      if (kind == ProjectKind.Mcp) OpenProjectSetup(path);
   }

   private static void _StampKind(string path, ProjectKind kind)
   {
      try
      {
         var store = Agents.Native.Config.ProjectConfigStore.LoadOrCreate(path);
         // A folder added before (and removed again) keeps its own rules.
         if (kind == ProjectKind.Code && !File.Exists(store.FilePath))
            store.Config.FolderRules = Agents.Native.Config.ProjectConfig.DefaultFolderRules();
         store.Config.Kind = kind;
         store.Save();
      }
      catch { /* the sidebar list stays authoritative either way */ }
   }

   private async void BtnAddBracket_Click(object? sender, RoutedEventArgs e)
   {
      var name = await ShowPrompt("Add bracket", "Bracket name:");
      if (string.IsNullOrWhiteSpace(name)) return;
      _projects.AddBracket(name.Trim());
      _projects.Save(_projectsFile);
      RebuildSidebar();
   }

   private void RebuildSidebar()
   {
      // Invalidates any in-flight session enumeration (MainWindow.Sidebar.cs).
      _sidebarGeneration++;
      SidebarPanel.Children.Clear();
      _ApplySidebarChrome();

      if (_sidebarMode == SidebarMode.Sessions && _sidebarProjectPath != null)
      {
         _RebuildSidebarSessions();
         return;
      }

      foreach (var entry in _projects.Entries)
      {
         if (entry.Kind == EntryKind.Project)
            SidebarPanel.Children.Add(BuildProjectItem(entry.Path, indent: 0));
         else
            SidebarPanel.Children.Add(BuildBracketItem(entry));
      }
   }

   private Control BuildBracketItem(ProjectEntry bracket)
   {
      var panel = new StackPanel();

      // -- header row (chevron + bracket name; click to toggle) --
      var header = new Border
      {
         Padding = new Avalonia.Thickness(6, 6, 8, 6),
         Background = Brushes.Transparent,
         Cursor = new Cursor(StandardCursorType.Hand),
      };

      var chevron = new TextBlock
      {
         Text = bracket.Expanded ? "▾" : "▸", // ▾ / ▸
         Foreground = ThemeBrushes.Accent,
         FontSize = 14,
         Width = 16,
         VerticalAlignment = VerticalAlignment.Center,
      };

      var name = new TextBlock
      {
         Text = bracket.Name.ToUpperInvariant(),
         Foreground = ThemeBrushes.TextMutedStrong,
         FontSize = 14,
         FontWeight = FontWeight.Bold,
         VerticalAlignment = VerticalAlignment.Center,
         TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
      };

      var count = new TextBlock
      {
         Text = "  (" + bracket.Paths.Count + ")",
         Foreground = ThemeBrushes.TextMuted,
         FontSize = 12,
         VerticalAlignment = VerticalAlignment.Center,
      };

      var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
      headerRow.Children.Add(chevron);
      headerRow.Children.Add(name);
      headerRow.Children.Add(count);
      header.Child = headerRow;

      header.PointerEntered += (_, _) => header.Background = ThemeBrushes.HoverBackground;
      header.PointerExited += (_, _) => header.Background = Brushes.Transparent;

      header.PointerPressed += (_, e) =>
      {
         if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
         {
            _projects.SetBracketExpanded(bracket.Name, !bracket.Expanded);
            _projects.Save(_projectsFile);
            RebuildSidebar();
         }
      };

      // -- header context menu --
      var menu = new ContextMenu();

      var itemAdd = new MenuItem { Header = "Add Project to Bracket…" };
      itemAdd.Click += async (_, _) => await _AddProjectToBracket(bracket.Name, ProjectKind.Code);
      menu.Items.Add(itemAdd);

      if (_appSettings.Values.EnableMcp)
      {
         var itemAddMcp = new MenuItem { Header = "Add MCP to Bracket…" };
         itemAddMcp.Click += async (_, _) => await _AddProjectToBracket(bracket.Name, ProjectKind.Mcp);
         menu.Items.Add(itemAddMcp);
      }

      var itemToggle = new MenuItem { Header = bracket.Expanded ? "Collapse" : "Expand" };
      itemToggle.Click += (_, _) =>
      {
         _projects.SetBracketExpanded(bracket.Name, !bracket.Expanded);
         _projects.Save(_projectsFile);
         RebuildSidebar();
      };
      menu.Items.Add(itemToggle);

      var itemRename = new MenuItem { Header = "Rename…" };
      itemRename.Click += async (_, _) =>
      {
         var newName = await ShowPrompt("Rename bracket", "New name:", bracket.Name);
         if (string.IsNullOrWhiteSpace(newName)) return;
         _projects.RenameBracket(bracket.Name, newName.Trim());
         _projects.Save(_projectsFile);
         RebuildSidebar();
      };
      menu.Items.Add(itemRename);

      menu.Items.Add(new Separator());

      var itemDelete = new MenuItem { Header = "Delete Bracket (keep projects)" };
      itemDelete.Click += (_, _) =>
      {
         _projects.RemoveBracket(bracket.Name);
         _projects.Save(_projectsFile);
         RebuildSidebar();
      };
      menu.Items.Add(itemDelete);

      header.ContextMenu = menu;
      panel.Children.Add(header);

      // -- children --
      if (bracket.Expanded)
      {
         foreach (var p in bracket.Paths)
            panel.Children.Add(BuildProjectItem(p, indent: 18));
      }

      return panel;
   }

   private Control BuildProjectItem(string path, int indent)
   {
      var border = new Border
      {
         Padding = new Avalonia.Thickness(8 + indent, 5, 8, 5),
         Background = Brushes.Transparent,
         Cursor = new Cursor(StandardCursorType.Hand),
      };

      ToolTip.SetTip(border, path);

      // Active = a live workspace exists for this project, i.e. it has an
      // open session tab.
      bool isActive   = _HasTabFor(path);
      bool isSelected = !isActive && string.Equals(_selectedProjectPath, path, StringComparison.OrdinalIgnoreCase);

      var tb = new TextBlock
      {
         Text = _projects.DisplayLabel(path),
         Foreground = isActive ? ThemeBrushes.Accent
                     : isSelected ? ThemeBrushes.AccentHover
                     : ThemeBrushes.TextPrimary,
         FontSize = 13,
         FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
         TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
         VerticalAlignment = VerticalAlignment.Center,
      };

      // Drill-down affordance: same idiom the brackets already use, so the
      // gesture is discoverable without relying on double-click alone.
      var chevron = new TextBlock
      {
         Text = "›",
         Foreground = ThemeBrushes.TextMuted,
         FontSize = 15,
         Width = 14,
         TextAlignment = TextAlignment.Center,
         VerticalAlignment = VerticalAlignment.Center,
      };
      // The glyph alone is a few pixels wide. The transparent hit area grows
      // it, and the negative margins push that area out over the row's own
      // padding to its top, bottom and right edges, so the glyph stays put.
      var chevronHit = new Border
      {
         Child      = chevron,
         Background = Brushes.Transparent,
         Padding    = new Thickness(10, 5, 8, 5),
         Margin     = new Thickness(-4, -5, -8, -5),
      };
      ToolTip.SetTip(chevronHit, "Show sessions");
      chevronHit.PointerEntered += (_, _) => chevron.Foreground = ThemeBrushes.Accent;
      chevronHit.PointerExited += (_, _) => chevron.Foreground = ThemeBrushes.TextMuted;
      chevronHit.PointerPressed += (_, e) =>
      {
         EnterSidebarSessions(path);
         e.Handled = true; // don't let the click also select the project
      };

      var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
      Grid.SetColumn(tb, 0);
      Grid.SetColumn(chevronHit, 2);
      row.Children.Add(tb);
      row.Children.Add(chevronHit);

      // The folder name says nothing about the kind, and the two behave
      // nothing alike once a session is open — so MCP projects are marked.
      if (_projects.GetKind(path) == ProjectKind.Mcp)
      {
         var badge = new TextBlock
         {
            Text              = "mcp",
            Foreground        = ThemeBrushes.TextMuted,
            FontSize          = 9,
            Margin            = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
         };
         ToolTip.SetTip(badge, "MCP project — tools come from its configured MCP servers.");
         Grid.SetColumn(badge, 1);
         row.Children.Add(badge);
      }

      border.Child = row;

      border.PointerEntered += (_, _) =>
         border.Background = ThemeBrushes.HoverBackground;
      border.PointerExited += (_, _) =>
         border.Background = Brushes.Transparent;

      border.PointerPressed += (_, e) =>
      {
         if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
            SelectProject(path);
      };

      // Accelerator for the chevron. The single-click select has already run
      // by the time this fires — selecting then drilling in is coherent, so
      // there's nothing to suppress.
      border.DoubleTapped += (_, e) =>
      {
         EnterSidebarSessions(path);
         e.Handled = true;
      };

      var menu = new ContextMenu();

      // -- SlimCode Agent (in-process native agent) --
      var nativeAgent = AgentRegistry.All.FirstOrDefault(a => a.IsNative);
      menu.Items.Add(MenuHeader("SlimCode Agent"));
      var itemSetup = new MenuItem { Header = "Project Setup…" };
      itemSetup.Click += (_, _) => OpenProjectSetup(path);
      menu.Items.Add(itemSetup);
      if (nativeAgent != null)
      {
         var itemLaunchNative = new MenuItem { Header = "Launch " + nativeAgent.DisplayName };
         itemLaunchNative.Click += (_, _) => LaunchAgent(path, nativeAgent);
         menu.Items.Add(itemLaunchNative);
      }

      // -- Tools (external; spawned, not attached) --
      menu.Items.Add(new Separator());
      menu.Items.Add(MenuHeader("Tools"));

      var itemFolder = new MenuItem { Header = "Open Folder" };
      itemFolder.Click += (_, _) => ExternalTools.OpenFolder(path);
      menu.Items.Add(itemFolder);

      var itemCmd = new MenuItem { Header = "Open Terminal (cmd)" };
      itemCmd.Click += (_, _) => ExternalTools.OpenCmd(path);
      menu.Items.Add(itemCmd);

      var itemPs = new MenuItem { Header = "Open Terminal (powershell)" };
      itemPs.Click += (_, _) => ExternalTools.OpenPowerShell(path);
      menu.Items.Add(itemPs);

      // -- Project management --
      menu.Items.Add(new Separator());

      var itemMove = new MenuItem { Header = "Move to bracket" };
      var bracketNames = _projects.BracketNames.ToList();

      var itemNone = new MenuItem { Header = "(no bracket)" };
      itemNone.Click += (_, _) =>
      {
         _projects.MoveProject(path, null);
         _projects.Save(_projectsFile);
         RebuildSidebar();
      };
      itemMove.Items.Add(itemNone);

      if (bracketNames.Count > 0)
         itemMove.Items.Add(new Separator());

      foreach (var bn in bracketNames)
      {
         var capName = bn;
         var bItem = new MenuItem { Header = capName };
         bItem.Click += (_, _) =>
         {
            _projects.MoveProject(path, capName);
            _projects.Save(_projectsFile);
            RebuildSidebar();
         };
         itemMove.Items.Add(bItem);
      }

      itemMove.Items.Add(new Separator());
      var itemNewBracket = new MenuItem { Header = "New bracket…" };
      itemNewBracket.Click += async (_, _) =>
      {
         var n = await ShowPrompt("New bracket", "Bracket name:");
         if (string.IsNullOrWhiteSpace(n)) return;
         _projects.MoveProject(path, n.Trim());
         _projects.Save(_projectsFile);
         RebuildSidebar();
      };
      itemMove.Items.Add(itemNewBracket);

      menu.Items.Add(itemMove);

      var itemRemove = new MenuItem { Header = "Remove" };
      itemRemove.Click += (_, _) =>
      {
         _projects.RemoveProject(path);
         _projects.Save(_projectsFile);
         RebuildSidebar();
      };
      menu.Items.Add(itemRemove);

      border.ContextMenu = menu;

      return border;
   }

   // Open the per-project build/test setup editor for a sidebar project.
   // Mirrors the chat view's "Project settings…" menu; persists to the same
   // project.json. No live loop to refresh from here.
   private void OpenProjectSetup(string path)
   {
      var store  = Agents.Native.Config.ProjectConfigStore.LoadOrCreate(path);
      var skills = Agents.Native.Skills.SkillStore.Discover(path);
      var win    = new Views.Native.ProjectSettingsWindow();
      win.Configure(store.Config, path, skills, updated =>
      {
         store.Config.BuildSteps      = updated.BuildSteps;
         store.Config.TestSteps       = updated.TestSteps;
         store.Config.BuildTimeoutSec = updated.BuildTimeoutSec;
         store.Config.TestTimeoutSec  = updated.TestTimeoutSec;
         store.Config.AutoLoadSkills  = updated.AutoLoadSkills;
         store.Config.SkipSkillAnnouncement = updated.SkipSkillAnnouncement;
         store.Config.FolderRules     = updated.FolderRules;
         // Kind is deliberately not copied back: it is fixed when the project
         // is added (see _AddProject) and the dialog cannot change it.
         store.Config.McpServers      = updated.McpServers;
         store.Save();
      },
      alias: _projects.GetAlias(path),
      onAliasSave: alias =>
      {
         _projects.SetAlias(path, alias);
         _projects.Save(_projectsFile);
         RebuildSidebar();
      });
      win.ShowDialog(this);
   }

   // Disabled MenuItem used as a section header. Greyed out, can't be clicked,
   // but visually anchors the items below it.
   private static MenuItem MenuHeader(string text) => new()
   {
      Header = text,
      IsEnabled = false,
      FontWeight = FontWeight.SemiBold,
   };
}
