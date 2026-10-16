// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SlimCodeDesktop.Agents;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop;

/// <summary>
/// Sidebar drill-down. The panel has two modes: the pinned project list, and
/// the session list of one project (entered by double-clicking a project row
/// or its chevron, left via the header's back arrow).
///
/// The session mode is the primary picker; the full-page
/// <c>SessionsView</c> still exists and renders the same
/// <see cref="AgentSession"/> data as cards — both go through
/// <see cref="Agent.EnumerateSessions"/>, so there is one source of truth
/// even though there are two renderers.
/// </summary>
public partial class MainWindow
{
   private enum SidebarMode { Projects, Sessions }

   private SidebarMode _sidebarMode = SidebarMode.Projects;
   private string?     _sidebarProjectPath;

   // Bumped on every sidebar rebuild. An async session-enumeration result
   // whose generation no longer matches is stale and gets dropped — checking
   // only the project path would let a leave-and-re-enter of the SAME project
   // append its rows twice.
   private int _sidebarGeneration;

   // Layout width vs. hover-expanded width. The expanded state overlays the
   // content pane (the Border carries ZIndex) rather than resizing the grid
   // column, so widening never triggers a relayout of the chat transcript.
   private const double SIDEBAR_WIDTH          = 220;
   private const double SIDEBAR_EXPANDED_WIDTH = 420;

   // -- mode switching ----------------------------------------------------------

   private void EnterSidebarSessions(string projectPath)
   {
      _sidebarMode        = SidebarMode.Sessions;
      _sidebarProjectPath = projectPath;
      RebuildSidebar();
   }

   private void LeaveSidebarSessions()
   {
      _sidebarMode        = SidebarMode.Projects;
      _sidebarProjectPath = null;
      _CollapseSidebar();
      RebuildSidebar();
   }

   private void BtnSidebarBack_Click(object? sender, RoutedEventArgs e) => LeaveSidebarSessions();

   /// <summary>
   /// Header + bottom-button chrome for the current mode. Called from
   /// <c>RebuildSidebar</c> before it fills the panel.
   /// </summary>
   private void _ApplySidebarChrome()
   {
      bool sessions = _sidebarMode == SidebarMode.Sessions && _sidebarProjectPath != null;

      BtnSidebarBack.IsVisible = sessions;
      SidebarTitle.Text = sessions
         ? _projects.DisplayLabel(_sidebarProjectPath!).ToUpperInvariant()
         : "PROJECTS";
      ToolTip.SetTip(SidebarTitle, sessions ? _sidebarProjectPath : null);

      // Project management belongs to the project list; hide it while drilled in.
      BtnAddFolder.IsVisible  = !sessions;
      BtnAddBracket.IsVisible = !sessions;
      // Creating MCP projects is behind the app-level switch; ones that
      // already exist stay in the list and stay editable regardless.
      BtnAddMcp.IsVisible     = !sessions && _appSettings.Values.EnableMcp;

      if (!sessions) _CollapseSidebar();
   }

   // -- hover expand ------------------------------------------------------------

   private void Sidebar_PointerEntered(object? sender, PointerEventArgs e)
   {
      // Only in session mode: the project list is short labels that already
      // fit, and a widening project list would just be noise on every pass of
      // the mouse toward the content pane.
      if (_sidebarMode != SidebarMode.Sessions) return;
      Sidebar.Width = SIDEBAR_EXPANDED_WIDTH;
   }

   private void Sidebar_PointerExited(object? sender, PointerEventArgs e) => _CollapseSidebar();

   private void _CollapseSidebar()
   {
      if (Sidebar != null) Sidebar.Width = SIDEBAR_WIDTH;
   }

   // A tooltip is its own popup window: one opened at the pointer can land
   // under it, which the sidebar sees as PointerExited and collapses. Session
   // rows span the sidebar, so placing their tips to the right of the row
   // puts them past the expanded edge, clear of the pointer.
   private static void _PlaceTipOutsideSidebar(Control row)
   {
      ToolTip.SetPlacement(row, PlacementMode.Right);
      ToolTip.SetHorizontalOffset(row, 4);
   }

   // -- session list ------------------------------------------------------------

   /// <summary>
   /// Fill the sidebar with the selected project's sessions. Enumeration runs
   /// off the UI thread (it stats and, on change, parses every transcript);
   /// the rows land when it returns.
   /// </summary>
   private void _RebuildSidebarSessions()
   {
      var nativeAgent = AgentRegistry.All.FirstOrDefault(a => a.IsNative);
      if (_sidebarProjectPath == null || nativeAgent == null)
      {
         LeaveSidebarSessions();
         return;
      }
      string path  = _sidebarProjectPath;
      Agent  agent = nativeAgent;

      SidebarPanel.Children.Add(_BuildSidebarNewSessionRow(path, agent));
      SidebarPanel.Children.Add(new Border
      {
         Height     = 1,
         Margin     = new Thickness(8, 4, 8, 4),
         Background = ThemeBrushes.BorderColor,
      });

      var loading = new TextBlock
      {
         Text       = "Loading…",
         Foreground = ThemeBrushes.TextMuted,
         FontSize   = 11,
         Margin     = new Thickness(10, 4, 8, 4),
      };
      SidebarPanel.Children.Add(loading);

      int generation = _sidebarGeneration;
      Task.Run(() =>
      {
         try { return agent.EnumerateSessions(path); }
         catch { return (IReadOnlyList<AgentSession>)Array.Empty<AgentSession>(); }
      }).ContinueWith(t =>
      {
         var result = t.IsCompletedSuccessfully && t.Result != null
            ? t.Result : Array.Empty<AgentSession>();
         Dispatcher.UIThread.Post(() =>
         {
            // Anything that rebuilt the sidebar since we asked wins.
            if (generation != _sidebarGeneration) return;

            SidebarPanel.Children.Remove(loading);
            if (result.Count == 0)
            {
               SidebarPanel.Children.Add(new TextBlock
               {
                  Text       = "no sessions yet",
                  Foreground = ThemeBrushes.TextMuted,
                  FontSize   = 11,
                  FontStyle  = FontStyle.Italic,
                  Margin     = new Thickness(10, 4, 8, 4),
               });
               return;
            }
            foreach (var e in SessionLineage.Arrange(result))
               SidebarPanel.Children.Add(_BuildSidebarSessionRow(agent, path, e.Session, e.Depth));
         });
      });
   }

   private Control _BuildSidebarNewSessionRow(string projectPath, Agent agent)
   {
      var row = new Border
      {
         Padding    = new Thickness(10, 6, 8, 6),
         Background = Brushes.Transparent,
         Cursor     = new Cursor(StandardCursorType.Hand),
      };
      row.Child = new TextBlock
      {
         Text       = "+  New session",
         Foreground = ThemeBrushes.Accent,
         FontSize   = 12,
         FontWeight = FontWeight.SemiBold,
      };
      ToolTip.SetTip(row, "Start a fresh " + agent.DisplayName + " session in this project");
      _PlaceTipOutsideSidebar(row);

      row.PointerEntered += (_, _) => row.Background = ThemeBrushes.HoverBackground;
      row.PointerExited  += (_, _) => row.Background = Brushes.Transparent;
      row.PointerPressed += (_, e) =>
      {
         if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
         _selectedProjectPath = projectPath;
         LaunchAgent(projectPath, agent);
      };
      return row;
   }

   /// <param name="depth">
   /// Fork depth from <see cref="SessionLineage"/>: indents the row under the
   /// session it was branched from. A fork whose parent is gone arrives here
   /// as depth 0 and reads as an ordinary session.
   /// </param>
   private Control _BuildSidebarSessionRow(Agent agent, string projectPath, AgentSession session, int depth = 0)
   {
      // The row for the session currently open in a tab reads as active, the
      // same way the project list marks a project with a live workspace.
      bool isOpen = string.Equals(_FindTab(projectPath)?.Session?.CurrentSessionId,
                                  session.Id, StringComparison.Ordinal);

      var row = new Border
      {
         Padding    = new Thickness(10 + depth * 12, 5, 8, 5),
         Background = Brushes.Transparent,
         Cursor     = new Cursor(StandardCursorType.Hand),
      };

      var title = new TextBlock
      {
         Text         = (depth > 0 ? "⑂ " : "")
                        + (string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title),
         Foreground   = isOpen ? ThemeBrushes.Accent : ThemeBrushes.TextPrimary,
         FontSize     = 12,
         FontWeight   = isOpen ? FontWeight.Bold : FontWeight.Normal,
         // No caption yet -> the label is a quote of the first prompt, not a
         // name. Italic keeps that visually distinct from a named session.
         FontStyle    = session.Caption == null ? FontStyle.Italic : FontStyle.Normal,
         TextTrimming = TextTrimming.CharacterEllipsis,
      };

      var sub = new TextBlock
      {
         Text       = ComposeSubtitle(session, depth),
         Foreground = ThemeBrushes.TextMuted,
         FontSize   = 10,
      };

      var stack = new StackPanel();
      stack.Children.Add(title);
      stack.Children.Add(sub);
      row.Child = stack;

      if (!string.IsNullOrWhiteSpace(session.PreviewText))
      {
         ToolTip.SetTip(row, _BuildSessionPreviewTip(session.PreviewText));
         _PlaceTipOutsideSidebar(row);
      }

      row.PointerEntered += (_, _) => row.Background = ThemeBrushes.HoverBackground;
      row.PointerExited  += (_, _) => row.Background = Brushes.Transparent;
      row.PointerPressed += (_, e) =>
      {
         if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
         _selectedProjectPath = projectPath;
         LaunchAgent(projectPath, agent, session.SessionId);
      };

      var menu = BuildSessionContextMenu(agent, projectPath, session, _RefreshSessionPickers);
      if (menu != null) row.ContextMenu = menu;

      return row;
   }

   /// <summary>
   /// Re-render whichever session pickers are on screen after a rename or a
   /// delete. Cheap: the parse cache makes a re-enumeration mostly stats.
   /// </summary>
   private void _RefreshSessionPickers()
   {
      if (_sidebarMode == SidebarMode.Sessions) RebuildSidebar();
      if (_view == MainView.Sessions) RebuildSessionsView();
   }
}
