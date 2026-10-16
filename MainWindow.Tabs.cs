// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Themes;
using SlimCodeDesktop.Views;
using SlimCodeDesktop.Views.Native;

namespace SlimCodeDesktop;

/// <summary>
/// Native session tab management: one <see cref="NativeAgentView"/> per open
/// SlimCode Agent session, hosted in <c>NativeHost</c>, with a strip of tab
/// headers above the content area.
///
/// One tab per project: opening a project that already has a tab focuses it
/// instead of creating a second one — this sidesteps concurrent writers on
/// the per-project policy files entirely.
/// </summary>
public partial class MainWindow
{
   // -- tab model --------------------------------------------------------------

   /// <summary>
   /// One strip tab: either a native session (Session non-null) or a
   /// subagent viewer grouped under its parent session tab (Viewer + Run +
   /// Parent non-null). Content is whichever control fills the host.
   /// </summary>
   private sealed class NativeTab
   {
      public string           ProjectPath = "";
      public Control          Content     = null!;
      public NativeAgentView? Session;
      public SubagentViewer?  Viewer;
      public Agents.Native.SubagentRun? Run;
      public NativeTab?       Parent;

      /// <summary>
      /// True for any tab grouped under a session (currently: a subagent
      /// viewer) rather than being one. Child tabs have no
      /// <see cref="Session"/>, so every path that reaches for it must check
      /// this first — spelling the test out per-call is what put a null deref
      /// in both the badge and the close handler.
      /// </summary>
      public bool IsChild => Viewer != null;

      /// <summary>Viewer tabs: the task text (live tabs read Run.TaskText;
      /// replay tabs have no run) and, for replay tabs, the transcript file
      /// they render — the dedup key when "view" is clicked again.</summary>
      public string?          ViewerTask;
      public string?          ViewerSource;

      public Border           Header      = null!;
      public TextBlock        Label       = null!;
      public TextBlock        Badge       = null!;

      /// <summary>Session tabs: viewer tabs the user closed. They stay alive
      /// (subscribed, transcript intact) and can be reopened from the session
      /// tab's context menu; destroyed only when the session closes.</summary>
      public List<NativeTab>  HiddenViewers = new();
   }

   private readonly List<NativeTab> _nativeTabs = new();
   private NativeTab?               _activeTab;

   private NativeTab? _FindTab(string projectPath)
   {
      foreach (var t in _nativeTabs)
         if (t.Session != null &&
             string.Equals(t.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            return t;
      return null;
   }

   /// <summary>True when any native tab is open for the path (sidebar highlight).</summary>
   private bool _HasTabFor(string projectPath) => _FindTab(projectPath) != null;

   // -- open / activate / close -------------------------------------------------

   /// <summary>
   /// Open (or focus) the native session tab for a project. With a
   /// <paramref name="resumeSessionId"/>, an existing tab is rebound to that
   /// session as long as nothing would be lost by it; a busy tab, or one with
   /// a half-written prompt over a live session, refuses and says why — one
   /// session per project at a time either way.
   /// </summary>
   private async void OpenNativeTab(string dir, Agents.Native.NativeAgent agent, string? resumeSessionId)
   {
      var existing = _FindTab(dir);
      if (existing != null)
      {
         if (!string.IsNullOrWhiteSpace(resumeSessionId) &&
             !string.Equals(existing.Session!.CurrentSessionId, resumeSessionId, StringComparison.Ordinal))
         {
            // A turn in flight (or a prompt waiting on the user) always wins.
            // A half-written prompt only blocks a *live* session — an idle tab
            // loses nothing by being rebound, and the draft text survives it.
            var cur = existing.Session;
            if (cur.IsRunning || cur.HasPendingPrompt ||
                (cur.HasActiveSession && cur.HasDraftInput))
            {
               ActivateNativeTab(existing);
               await ShowError(_SwitchBlockedReason(cur));
               return;
            }
            // Swap in place. The outgoing session's grouped viewers belong to
            // a loop that Configure→Reset is about to drop, so they go first
            // — same teardown a tab close does.
            _DestroyChildTabs(existing);
            cur.Configure(agent, dir, _settingsStore, resumeSessionId);
            // The session we just left may be new to the picker's list; the
            // ActivateNativeTab below re-renders sidebar and strip.
            _InvalidateSessionCache(dir);
         }
         ActivateNativeTab(existing);
         return;
      }

      var view = new NativeAgentView { IsVisible = false };
      var tab  = new NativeTab { ProjectPath = dir, Content = view, Session = view };

      view.SessionClosedByUser  += () => CloseNativeTab(tab);
      // Auto-named session: the pickers are showing a quote of the first
      // prompt and need to re-read the file to pick the caption up.
      view.SessionTitled        += () =>
      {
         _InvalidateSessionCache(tab.ProjectPath);
         _RefreshSessionPickers();
      };
      view.RunningChanged       += _  => _UpdateTabBadge(tab);
      view.PromptPendingChanged += _  => _UpdateTabBadge(tab);
      // Subagent runs get an optional grouped viewer tab. The event fires on
      // the parent loop's background thread — marshal before touching UI.
      view.SubagentStarted += run =>
         Dispatcher.UIThread.Post(() => OpenSubagentTab(tab, run));
      // Tool card "view" button (already UI thread): focus the run's viewer.
      view.SubagentViewerRequested += run => _FocusSubagentTab(tab, run);
      // Replayed card's "view" button: open the stored transcript.
      view.SubagentReplayRequested += (task, path, isWrite) => _OpenSubagentReplayTab(tab, task, path, isWrite);

      NativeHost.Children.Add(view);
      _nativeTabs.Add(tab);

      view.Configure(agent, dir, _settingsStore, resumeSessionId);
      // ActivateNativeTab → SetMainView → RebuildTabStrip creates the header.
      ActivateNativeTab(tab);
   }

   /// <summary>
   /// Open the grouped, closable viewer tab for a freshly started subagent
   /// run. Deliberately NOT focused — the user stays in the parent session
   /// watching the tool card; the tab is there to peek into.
   /// </summary>
   private void OpenSubagentTab(NativeTab parent, Agents.Native.SubagentRun run)
   {
      if (!_nativeTabs.Contains(parent)) return; // parent closed in the interim

      var viewer = new SubagentViewer(run) { IsVisible = false };
      var tab = new NativeTab
      {
         ProjectPath = parent.ProjectPath,
         Content     = viewer,
         Viewer      = viewer,
         Run         = run,
         Parent      = parent,
      };
      viewer.RunCompleted += () => _UpdateTabBadge(tab);

      // Insert directly behind the parent's existing viewer children so the
      // group stays visually contiguous in the strip.
      int idx = _nativeTabs.IndexOf(parent) + 1;
      while (idx < _nativeTabs.Count && ReferenceEquals(_nativeTabs[idx].Parent, parent))
         idx++;
      _nativeTabs.Insert(idx, tab);
      NativeHost.Children.Add(viewer);

      RebuildTabStrip();
   }

   /// <summary>
   /// Open (or refocus) a replay viewer for a stored subagent transcript —
   /// the "view" button on a resumed session's subagent card. Grouped
   /// under the parent like a live viewer, and focused immediately (the
   /// user asked for it explicitly).
   /// </summary>
   private void _OpenSubagentReplayTab(NativeTab parent, string task, string subFilePath, bool isWrite)
   {
      if (!_nativeTabs.Contains(parent)) return;

      // Already open (visible or hidden)? Focus instead of duplicating.
      foreach (var t in _nativeTabs)
         if (string.Equals(t.ViewerSource, subFilePath, StringComparison.OrdinalIgnoreCase))
         {
            ActivateNativeTab(t);
            return;
         }
      foreach (var hv in parent.HiddenViewers)
         if (string.Equals(hv.ViewerSource, subFilePath, StringComparison.OrdinalIgnoreCase))
         {
            _ReopenViewerTab(parent, hv);
            return;
         }

      var records = TranscriptStore.Read(subFilePath);
      var viewer  = new SubagentViewer(task, records, isWrite) { IsVisible = false };
      var tab = new NativeTab
      {
         ProjectPath  = parent.ProjectPath,
         Content      = viewer,
         Viewer       = viewer,
         Parent       = parent,
         ViewerTask   = task,
         ViewerSource = subFilePath,
      };

      int idx = _nativeTabs.IndexOf(parent) + 1;
      while (idx < _nativeTabs.Count && ReferenceEquals(_nativeTabs[idx].Parent, parent))
         idx++;
      _nativeTabs.Insert(idx, tab);
      NativeHost.Children.Add(viewer);

      ActivateNativeTab(tab);
   }

   private void ActivateNativeTab(NativeTab tab)
   {
      _activeTab = tab;
      foreach (var t in _nativeTabs)
         t.Content.IsVisible = ReferenceEquals(t, tab);

      _selectedProjectPath = tab.ProjectPath;
      SetMainView(MainView.Native);
      RebuildSidebar();
      UpdateHeader();
   }

   /// <summary>
   /// Tear down a tab: end the session (loop cancelled, pending permission
   /// prompt resolved as deny — both via <see cref="NativeAgentView.Reset"/>),
   /// detach the view, and move focus to a sensible neighbor. The session
   /// JSONL stays on disk, resumable from the project picker.
   /// </summary>
   private void CloseNativeTab(NativeTab tab)
   {
      if (!_nativeTabs.Contains(tab)) return; // already closed (Close button + reentry)

      // Child tabs hide rather than close — their work belongs to the parent
      // session and keeps running. Everything below assumes a session tab.
      if (tab.IsChild)
      {
         _HideViewerTab(tab);
         return;
      }

      // If the user was sitting on this session or one of its viewers, the
      // fallback below must run even though the viewer is gone by then.
      bool wasActive = ReferenceEquals(_activeTab, tab) ||
                       (_activeTab != null && ReferenceEquals(_activeTab.Parent, tab));

      _DestroyChildTabs(tab);

      // Reset is idempotent; when the close came from the view's own Close
      // button it already ran. For strip-"×" closes it does the actual work.
      tab.Session!.Reset();

      int idx = _nativeTabs.IndexOf(tab);
      _nativeTabs.Remove(tab);
      NativeHost.Children.Remove(tab.Content);
      TabStripPanel.Children.Remove(tab.Header);

      // Freshly-closed session must show up in the picker on next render.
      _InvalidateSessionCache(tab.ProjectPath);

      if (wasActive)
      {
         _activeTab = null;
         if (_nativeTabs.Count > 0)
         {
            ActivateNativeTab(_nativeTabs[Math.Min(idx, _nativeTabs.Count - 1)]);
            return;
         }
         // Last tab gone: fall back to the picker for its project (matches
         // the pre-tab close behaviour).
         _selectedProjectPath = tab.ProjectPath;
         RebuildSessionsView();
         SetMainView(MainView.Sessions);
      }

      RebuildTabStrip();
      RebuildSidebar();
      UpdateHeader();
   }

   /// <summary>
   /// Take a session tab's grouped viewers down — visible and hidden alike.
   /// Their runs die with the session's loop anyway (Reset cancels it), and
   /// a rebind to another session leaves them pointing at nothing.
   /// </summary>
   private void _DestroyChildTabs(NativeTab tab)
   {
      for (int i = _nativeTabs.Count - 1; i >= 0; i--)
         if (ReferenceEquals(_nativeTabs[i].Parent, tab))
            _DestroyViewerTab(_nativeTabs[i]);
      foreach (var hv in new List<NativeTab>(tab.HiddenViewers))
         _DestroyViewerTab(hv);
   }

   /// <summary>Drop every cached session list for a project so the pickers re-read from disk.</summary>
   private void _InvalidateSessionCache(string projectPath)
   {
      var stale = new List<(string agentId, string path)>();
      foreach (var k in _sessionCache.Keys)
         if (string.Equals(k.path, projectPath, StringComparison.OrdinalIgnoreCase))
            stale.Add(k);
      foreach (var k in stale) _sessionCache.Remove(k);
   }

   /// <summary>Why a session swap was refused, phrased as the way out of it.</summary>
   private static string _SwitchBlockedReason(NativeAgentView view)
   {
      if (view.HasPendingPrompt)
         return "This tab is waiting on a permission prompt.\n" +
                "Answer it before switching to another session.";
      if (view.IsRunning)
         return "This tab has a turn in flight.\n" +
                "Wait for it to finish (or cancel it) before switching to another session.";
      return "This tab has an unsent prompt in the input box.\n" +
             "Send or clear it before switching to another session.";
   }

   /// <summary>
   /// Hide a subagent viewer tab: drop it from the strip but keep the viewer
   /// alive and subscribed, so its transcript keeps accumulating and the tab
   /// can be reopened losslessly from the parent session tab's context menu.
   /// The run itself is untouched either way — it belongs to the parent
   /// session's tool call and keeps executing.
   /// </summary>
   private void _HideViewerTab(NativeTab tab)
   {
      if (!_nativeTabs.Contains(tab)) return;

      _nativeTabs.Remove(tab);
      TabStripPanel.Children.Remove(tab.Header);
      tab.Content.IsVisible = false;
      tab.Parent?.HiddenViewers.Add(tab);

      if (ReferenceEquals(_activeTab, tab))
      {
         _activeTab = null;
         // The parent session tab always outlives its viewers (session close
         // destroys children first), so fall back to it.
         if (tab.Parent != null && _nativeTabs.Contains(tab.Parent))
         {
            ActivateNativeTab(tab.Parent);
            return;
         }
         SetMainView(MainView.Sessions);
      }
      RebuildTabStrip();
      UpdateHeader();
   }

   /// <summary>
   /// Bring a run's viewer tab to front: activate it when visible, reopen it
   /// when hidden. No-op if the viewer no longer exists (can only happen
   /// after its session closed — and the card's button dies with it).
   /// </summary>
   private void _FocusSubagentTab(NativeTab parent, Agents.Native.SubagentRun run)
   {
      foreach (var t in _nativeTabs)
         if (ReferenceEquals(t.Run, run))
         {
            ActivateNativeTab(t);
            return;
         }
      foreach (var hv in parent.HiddenViewers)
         if (ReferenceEquals(hv.Run, run))
         {
            _ReopenViewerTab(parent, hv);
            return;
         }
   }

   /// <summary>Reinsert a hidden viewer tab behind its parent's group and focus it.</summary>
   private void _ReopenViewerTab(NativeTab parent, NativeTab tab)
   {
      if (!parent.HiddenViewers.Remove(tab)) return;

      int idx = _nativeTabs.IndexOf(parent) + 1;
      while (idx < _nativeTabs.Count && ReferenceEquals(_nativeTabs[idx].Parent, parent))
         idx++;
      _nativeTabs.Insert(idx, tab);
      ActivateNativeTab(tab);
   }

   /// <summary>
   /// Really tear a viewer down (session close only): unsubscribe from the
   /// run and detach the control, whether the tab was visible or hidden.
   /// </summary>
   private void _DestroyViewerTab(NativeTab tab)
   {
      tab.Viewer?.Detach();
      _nativeTabs.Remove(tab);
      tab.Parent?.HiddenViewers.Remove(tab);
      NativeHost.Children.Remove(tab.Content);
      TabStripPanel.Children.Remove(tab.Header);
   }

   // -- strip rendering ----------------------------------------------------------

   /// <summary>
   /// Rebuild the whole strip: one header per tab, in open order. Cheap
   /// enough to run on any tab/view change; per-tab badge updates go through
   /// <see cref="_UpdateTabBadge"/> without a rebuild.
   /// </summary>
   private void RebuildTabStrip()
   {
      TabStripPanel.Children.Clear();

      foreach (var tab in _nativeTabs)
      {
         _BuildTabHeader(tab);
         TabStripPanel.Children.Add(tab.Header);
      }
   }

   private void _BuildTabHeader(NativeTab tab)
   {
      bool isActive = _view == MainView.Native && ReferenceEquals(tab, _activeTab);

      var badge = new TextBlock
      {
         Text              = "",
         FontSize          = 10,
         Width             = 12,
         VerticalAlignment = VerticalAlignment.Center,
      };

      bool isViewer = tab.IsChild;
      var label = new TextBlock
      {
         Text              = isViewer ? "⤷ subagent"
                                    : _projects.DisplayLabel(tab.ProjectPath),
         FontSize          = isViewer ? 11 : 12,
         FontStyle         = isViewer ? FontStyle.Italic : FontStyle.Normal,
         FontWeight        = isActive ? FontWeight.SemiBold : FontWeight.Normal,
         Foreground        = isActive ? ThemeBrushes.Accent : ThemeBrushes.TextPrimary,
         VerticalAlignment = VerticalAlignment.Center,
         MaxWidth          = 160,
         TextTrimming      = TextTrimming.CharacterEllipsis,
      };

      var close = new TextBlock
      {
         Text              = "×",
         FontSize          = 13,
         Foreground        = ThemeBrushes.TextMuted,
         Margin            = new Avalonia.Thickness(8, 0, 0, 0),
         VerticalAlignment = VerticalAlignment.Center,
      };
      ToolTip.SetTip(close, isViewer
         ? "Hide viewer (the subagent keeps running — reopen via right-click on the session tab)"
         : "Close tab (session stays resumable)");
      close.PointerEntered += (_, _) => close.Foreground = ThemeBrushes.TextPrimary;
      close.PointerExited  += (_, _) => close.Foreground = ThemeBrushes.TextMuted;
      close.PointerPressed += (_, e) =>
      {
         CloseNativeTab(tab);
         e.Handled = true; // don't let the click also activate the tab
      };

      var row = new StackPanel { Orientation = Orientation.Horizontal };
      row.Children.Add(badge);
      row.Children.Add(label);
      row.Children.Add(close);

      var header = new Border
      {
         // Sits on the bar's bottom edge; 6 px vertical padding fills most of
         // the 36 px bar, leaving a small gap above.
         Padding         = new Avalonia.Thickness(10, 6, 8, 6),
         CornerRadius    = new CornerRadius(4, 4, 0, 0),
         Background      = isActive ? ThemeBrushes.HoverBackground : Brushes.Transparent,
         BorderBrush     = isActive ? ThemeBrushes.Accent : ThemeBrushes.BorderColor,
         BorderThickness = new Avalonia.Thickness(1, 1, 1, 0),
         Cursor          = new Cursor(StandardCursorType.Hand),
         Child           = row,
      };
      ToolTip.SetTip(header, isViewer
         ? _OneLine(tab.Run?.TaskText ?? tab.ViewerTask ?? "subagent", 200)
         : tab.ProjectPath + " — SlimCode Agent");
      if (!isActive)
      {
         header.PointerEntered += (_, _) => header.Background = ThemeBrushes.HoverBackground;
         header.PointerExited  += (_, _) => header.Background = Brushes.Transparent;
      }
      header.PointerPressed += (_, e) =>
      {
         if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            ActivateNativeTab(tab);
      };

      // Session tabs with hidden subagent viewers get a right-click menu to
      // reopen them. Rebuilt with the strip, so the list stays current.
      if (!isViewer && tab.HiddenViewers.Count > 0)
      {
         var menu = new ContextMenu();
         menu.Items.Add(MenuHeader("Reopen viewer"));
         foreach (var hv in tab.HiddenViewers)
         {
            var captured = hv;
            var state = captured.Run != null && !captured.Run.IsCompleted ? "  (running)" : "";
            var item = new MenuItem
            {
               Header = "⤷ " + _OneLine(captured.Run?.TaskText ?? captured.ViewerTask ?? "subagent", 60) + state,
            };
            item.Click += (_, _) => _ReopenViewerTab(tab, captured);
            menu.Items.Add(item);
         }
         header.ContextMenu = menu;
      }

      tab.Header = header;
      tab.Label  = label;
      tab.Badge  = badge;
      _UpdateTabBadge(tab);
   }

   /// <summary>
   /// Per-tab status glyph: pending permission prompt (needs the user) wins
   /// over busy. Updated in place — no strip rebuild.
   /// </summary>
   private void _UpdateTabBadge(NativeTab tab)
   {
      if (tab.Badge == null) return;

      if (tab.Viewer != null)
      {
         if (tab.Run != null && !tab.Run.IsCompleted)
         {
            tab.Badge.Text       = "●";
            tab.Badge.Foreground = ThemeBrushes.Accent;
            ToolTip.SetTip(tab.Badge, "Subagent working");
         }
         else
         {
            tab.Badge.Text = "";
         }
         return;
      }

      // Everything past here is a session tab. Child tabs MUST return above —
      // Session is null on them, and falling through here is a null deref.
      if (tab.Session == null) return;

      if (tab.Session.HasPendingPrompt)
      {
         tab.Badge.Text       = "⚠";
         tab.Badge.Foreground = Brushes.Gold;
         ToolTip.SetTip(tab.Badge, "Waiting for permission");
      }
      else if (tab.Session.IsRunning)
      {
         tab.Badge.Text       = "●";
         tab.Badge.Foreground = ThemeBrushes.Accent;
         ToolTip.SetTip(tab.Badge, "Agent working");
      }
      else
      {
         tab.Badge.Text = "";
      }
   }

   private static string _OneLine(string s, int max)
   {
      var flat = s.Replace('\n', ' ').Replace('\r', ' ');
      return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
   }
}
