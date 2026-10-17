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
using Avalonia.Styling;
using Avalonia.Threading;
using SlimCodeDesktop.Agents;
using SlimCodeDesktop.Agents.Settings;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Themes;
using SlimCodeDesktop.Views.Native.Controls;

namespace SlimCodeDesktop;

public partial class MainWindow : Window
{
   private ProjectList _projects = new();
   private static readonly string _projectsFile = AppPaths.DefaultWorkspaceFile;

   private AgentSettingsStore _settingsStore = new(AppPaths.AgentSettingsFile);

   // Loaded by App before this window is constructed, so the accent could be
   // applied to the resources first -- take that instance rather than re-reading.
   private AppSettingsStore _appSettings = AppSettingsStore.Current;

   private string? _selectedProjectPath;

   // Sessions doubles as the empty state: with no project selected it renders
   // blank, which is what the right pane shows before anything is picked.
   private enum MainView { Config, Sessions, Native }
   private MainView _view = MainView.Sessions;

   // Cached per (agentId, projectPath). Dropped for a path when its session
   // tab closes so a freshly written session appears on the next visit.
   private readonly Dictionary<(string agentId, string path), IReadOnlyList<Agents.AgentSession>> _sessionCache = new();

   public MainWindow()
   {
      InitializeComponent();
      // The profile badge is empty in the default profile, so the usual title
      // is unchanged; anything else says so up front (see AppPaths.TitleSuffix).
      _baseTitle = AppInfo.NameWithVersion + AppPaths.TitleSuffix;
      Title = _baseTitle;
      _ApplyTitleBar();
      Opened += OnOpened;
      Closing += OnClosing;

      // F11 toggles true full-screen (Avalonia's WindowState.FullScreen
      // spans the taskbar and hides the title bar — the equivalent of
      // WPF's WindowStyle=None + WindowState=Maximized recipe).
      // Tunnel-only routing + handledEventsToo so focused TextBoxes
      // can't swallow the key. Subscribing to BOTH tunnel and bubble
      // would fire the handler twice per press and toggle right back.
      AddHandler(KeyDownEvent, OnMainWindowKeyDown,
                 RoutingStrategies.Tunnel,
                 handledEventsToo: true);

      // Sessions live in tabs (MainWindow.Tabs.cs); their close / badge
      // events are wired per tab in OpenNativeTab.
   }

   // Height of the custom title strip; also handed to the platform as the
   // non-client drag height so the caption buttons and the strip line up.
   private const double TITLE_BAR_HEIGHT = 32;

   /// <summary>
   /// Replace the native (light, on Win10) title bar with a theme-colored
   /// strip. Avalonia draws the caption buttons and the window title over
   /// it, so the strip itself is only a background — and it must stay free
   /// of interactive content, since the whole height is a drag region.
   /// </summary>
   private void _ApplyTitleBar()
   {
      ExtendClientAreaTitleBarHeightHint = TITLE_BAR_HEIGHT;
      _customTitleBar = Core.Platform.InstallCustomTitleBar(this);
      if (!_customTitleBar) return;

      TitleBarStrip.Height = TITLE_BAR_HEIGHT;
      TitleBarIcon.Source  = AppIcons.Small;

      // Avalonia's drawn title text sits at the left edge of the decorations;
      // push it right past the icon. The decorations are not part of this
      // window's logical tree, so the style has to live at application level.
      Application.Current?.Styles.Add(new Style(x => x.Name("PART_TitleTextPanel"))
      {
         Setters = { new Setter(MarginProperty, new Thickness(TITLE_TEXT_INDENT, 0, 0, 0)) },
      });

      _UpdateChrome();
   }

   // Left margin for Avalonia's title text, which adds ~12 px padding of its
   // own: lands the text ~8 px right of the icon (10 px margin + 16 px icon).
   private const double TITLE_TEXT_INDENT = 22;

   private bool _customTitleBar;

   /// <summary>
   /// Strip and 1 px outline follow the window state: full-screen hides the
   /// drawn decorations, so the strip goes with them; maximized and
   /// full-screen have no visible window edge, so the outline does too.
   /// </summary>
   private void _UpdateChrome()
   {
      TitleBarStrip.IsVisible = WindowState != WindowState.FullScreen;
      WindowOutline.BorderThickness = WindowState == WindowState.Normal
         ? new Thickness(1)
         : new Thickness(0);
   }

   protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   {
      base.OnPropertyChanged(change);

      if (change.Property == WindowStateProperty && _customTitleBar)
         _UpdateChrome();
   }

   private WindowState _preFullScreenState = WindowState.Normal;

   private void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key != Key.F11) return;
      // Ignore modifier-laden variants so Ctrl/Alt/Shift+F11 stay free
      // for future shortcuts.
      if (e.KeyModifiers != KeyModifiers.None) return;

      if (WindowState == WindowState.FullScreen)
      {
         WindowState = _preFullScreenState;
      }
      else
      {
         _preFullScreenState = WindowState;
         WindowState = WindowState.FullScreen;
      }
      e.Handled = true;
   }

   private void OnOpened(object? sender, EventArgs e)
   {
      _projects = ProjectList.Load(_projectsFile);
      // Recovers the kind of projects created before it was part of the list,
      // and moves an MCP project's storage onto the -mcp slug. No-op after the
      // first run (see ProjectKindMigration).
      if (Agents.Native.Config.ProjectKindMigration.Run(_projects))
         _projects.Save(_projectsFile);
      _settingsStore = AgentSettingsStore.Load(AppPaths.AgentSettingsFile);
      _appSettings   = AppSettingsStore.Current;
      // Pre-apply settings to all known agents so option defaults exist on
      // first run even before the user opens the config view.
      foreach (var a in AgentRegistry.All) _settingsStore.Apply(a);
      _settingsStore.Save();
      _appSettings.Save();
      RebuildSidebar();
   }

   private void UpdateHeader()
   {
      // The project the right pane is about: the active tab's in the session
      // view, the selected one in History (and Config, which keeps it).
      string? path = _view == MainView.Native ? _activeTab?.ProjectPath : _selectedProjectPath;
      string? name = path != null ? _projects.DisplayLabel(path) : null;

      // Name + path go into the window title (drawn on the title strip, and
      // what the taskbar shows) instead of a header row of their own.
      Title = path != null ? $"{_baseTitle} — {name} — {path}" : _baseTitle;

      // Context label in the tab bar, only where no tab is active to name
      // the place.
      (HeaderText.Text, HeaderDetail.Text) = _view switch
      {
         MainView.Config                     => ("Configuration", ""),
         MainView.Sessions when name != null => (name, "History"),
         _                                   => ("", ""),
      };
      HeaderPanel.IsVisible = HeaderText.Text.Length > 0;

      BtnHistory.IsEnabled = (_activeTab?.ProjectPath ?? _selectedProjectPath) != null;
   }

   // Window title without the project part; UpdateHeader prefixes it.
   private string _baseTitle = "";

   // Exclusive view switch in the right pane. Native means "the tab host is
   // visible, showing the active tab's view".
   private void SetMainView(MainView v)
   {
      _view = v;
      ConfigView.IsVisible   = v == MainView.Config;
      SessionsView.IsVisible = v == MainView.Sessions;
      NativeHost.IsVisible   = v == MainView.Native;

      // Strip and bar-button highlights follow the view.
      BtnToggleConfig.Classes.Set("active", v == MainView.Config);
      BtnHistory.Classes.Set("active", v == MainView.Sessions);
      RebuildTabStrip();
      UpdateHeader();

      // The tab host's visibility flip doesn't propagate an IsVisible change
      // to the child view, so returning from Config/Sessions needs an
      // explicit nudge to refresh the model combo / status line.
      if (v == MainView.Native)
         _activeTab?.Session?.NotifyBecameVisible();
   }

   // What the right pane should show when we're not in Config:
   //  - active session tab -> Native
   //  - otherwise          -> Sessions (blank when no project is selected)
   private MainView DefaultMainView() =>
      _activeTab != null ? MainView.Native : MainView.Sessions;

   private void BtnToggleConfig_Click(object? sender, RoutedEventArgs e)
   {
      if (_view == MainView.Config)
      {
         SetMainView(DefaultMainView());
      }
      else
      {
         RebuildConfigView();
         SetMainView(MainView.Config);
      }
   }

   /// <summary>
   /// Stored sessions of the project in focus — the active tab's, else the
   /// selected one. A second click returns, like the configuration toggle.
   /// </summary>
   private void BtnHistory_Click(object? sender, RoutedEventArgs e)
   {
      if (_view == MainView.Sessions)
      {
         SetMainView(DefaultMainView());
         return;
      }

      var path = _view == MainView.Native ? _activeTab?.ProjectPath : _selectedProjectPath;
      path ??= _activeTab?.ProjectPath;
      if (path == null) return;

      _selectedProjectPath = path;
      RebuildSessionsView();
      SetMainView(MainView.Sessions);
   }

   /// <summary>
   /// Open a session tab for <paramref name="agent"/> in <paramref name="dir"/>.
   /// With a <paramref name="resumeSessionId"/> the tab picks up that stored
   /// session instead of starting a fresh one.
   /// </summary>
   private async void LaunchAgent(string dir, Agent agent, string? resumeSessionId = null)
   {
      if (!Directory.Exists(dir))
      {
         await ShowError("Project folder no longer exists:\n" + dir);
         return;
      }

      _settingsStore.Apply(agent);
      if (agent is Agents.Native.NativeAgent na)
         OpenNativeTab(dir, na, resumeSessionId);
   }

   private void OnClosing(object? sender, WindowClosingEventArgs e)
   {
      // Stop every session tab's loop (cancels in-flight turns — including
      // nested subagent runs, whose cancellation chains off the parent's —
      // and resolves pending permission prompts) so no background task
      // outlives the window. Viewer tabs are passive; nothing to stop.
      foreach (var tab in _nativeTabs)
         tab.Session?.Reset();
   }
}
