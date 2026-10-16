// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Views.Native;
using SkiaSharp;

// `using Avalonia;` brings the Avalonia.Platform namespace into scope, which
// makes the bare name `Platform` ambiguous with our own static class.
using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views;

/// <summary>
/// MCP projects: server handles, the first-send connect, and keeping the
/// model informed when the live tool catalog changes.
/// </summary>
public partial class NativeAgentView
{
   // -- MCP projects ----------------------------------------------------------

   /// <summary>
   /// True when this project's sessions get their tools from MCP servers
   /// instead of the built-in coding tools. Read from the project config each
   /// time rather than cached, so flipping the kind in the setup dialog takes
   /// effect on the next session without reopening the tab.
   /// </summary>
   private bool _IsMcpProject()
   {
      if (string.IsNullOrEmpty(_projectPath)) return false;
      try { return ProjectConfigStore.LoadOrCreate(_projectPath).Config.Kind == ProjectKind.Mcp; }
      catch { return false; }
   }

   /// <summary>
   /// Create one handle per enabled server, without connecting any of them.
   /// Called when the tab binds a project so the header's pills can state
   /// "not connected" from the start — the alternative, showing nothing until
   /// the first send, reads as "this project has no servers".
   ///
   /// Idempotent: handles already built are kept, which is what makes it safe
   /// for <see cref="_ConnectMcpAsync"/> to call.
   /// </summary>
   private void _EnsureMcpHandles()
   {
      if (_mcpHandles.Count > 0 || string.IsNullOrEmpty(_projectPath)) return;
      if (!_IsMcpProject()) return;

      ProjectConfig cfg;
      try { cfg = ProjectConfigStore.LoadOrCreate(_projectPath).Config; }
      catch { return; }

      foreach (var s in cfg.McpServers)
      {
         if (s == null || !s.Enabled) continue;
         // A clone: the session must keep talking to the server it was built
         // with even if the user edits the config mid-conversation.
         var handle = new McpServerHandle(s.Clone(), _projectPath!);
         handle.SetLogSink((name, line) => UiPost(() => SetStatus("mcp " + name + ": " + line)));
         handle.StatusChanged += _ => UiPost(_RefreshMcpStatus);
         handle.ToolsChanged  += h => UiPost(() => _OnMcpToolsChanged(h));
         _mcpHandles.Add(handle);
      }

      _RefreshMcpStatus();
   }

   /// <summary>
   /// Bring this session's MCP servers up. Called on the first send only —
   /// nothing connects at project open or session creation, so a tab costs
   /// nothing until it is actually used, and a stdio server's cold start is
   /// paid once by the user who asked for it.
   ///
   /// Per-server failure is contained: an unreachable server reports itself as
   /// a chat error and a red pill, and the session continues with whatever the
   /// others offered. Connecting is attempted exactly once per session even
   /// when every server failed — retrying on each send would re-pay an npx
   /// cold start per message.
   /// </summary>
   private async Task _ConnectMcpAsync()
   {
      if (_mcpReady || _mcpConnecting || string.IsNullOrEmpty(_projectPath)) return;

      _EnsureMcpHandles();

      if (_mcpHandles.Count == 0)
      {
         AppendError("This is an MCP project, but no enabled MCP server is configured. "
                   + "Add one under the menu's \"Project settings…\" — the model has no tools until you do.");
         _mcpReady = true;
         return;
      }

      _mcpConnecting = true;
      BtnSend.IsEnabled   = false;
      BtnCancel.IsEnabled = true;
      SetStatus("connecting to " + _mcpHandles.Count + " MCP server(s)…");

      // The connect is its own cancellable unit: it happens before the loop
      // (and before _cts) exists, and a stdio server that never answers would
      // otherwise leave the user with no way out but closing the tab.
      var cts = new CancellationTokenSource(TimeSpan.FromSeconds(MCP_CONNECT_TIMEOUT_SEC));
      _mcpConnectCts = cts;

      _livePhase = TurnPhase.McpConnect;
      _RefreshStatsBars();
      var clock = System.Diagnostics.Stopwatch.StartNew();

      bool cancelled;
      try
      {
         var connects = new List<Task<bool>>(_mcpHandles.Count);
         foreach (var h in _mcpHandles)
            connects.Add(h.ConnectAsync(cts.Token));
         // Parallel: three servers that each take two seconds should cost two
         // seconds, not six. ConnectAsync never throws, so WhenAll can't fault.
         await Task.WhenAll(connects).ConfigureAwait(true);
      }
      finally
      {
         cancelled = cts.IsCancellationRequested;
         clock.Stop();
         _livePhase = TurnPhase.Idle;
         _sessionTimings.Add(TurnPhase.McpConnect, clock.Elapsed);
         _RefreshStatsBars();

         _mcpConnectCts = null;
         try { cts.Dispose(); } catch { }
         _mcpConnecting      = false;
         BtnSend.IsEnabled   = true;
         BtnCancel.IsEnabled = false;
      }

      // Cancelled by the user, or the whole attempt hit the ceiling. Leave the
      // session un-connected so the next Send tries again rather than running
      // a turn with no tools — the user pressed Cancel to stop waiting, not to
      // start an empty conversation.
      if (cancelled)
      {
         _DisposeMcpHandles();
         _RefreshMcpStatus();
         AppendError("MCP connect was cancelled after "
                   + TimingViz.FormatDuration(clock.Elapsed) + ". Send again to retry.");
         SetStatus("mcp connect cancelled");
         return;
      }

      int live = 0;
      foreach (var h in _mcpHandles)
      {
         if (h.State == McpState.Connected) { live++; continue; }
         AppendError("MCP server '" + h.Name + "' is unavailable: " + (h.Error ?? "unknown reason"));
      }

      _mcpReady = true;
      _RefreshMcpStatus();
      SetStatus(live + "/" + _mcpHandles.Count + " MCP server(s) connected in "
                + TimingViz.FormatDuration(clock.Elapsed));
   }

   /// <summary>
   /// Qualified names of every MCP tool currently advertised, sorted so two
   /// snapshots can be compared without caring about server order.
   /// </summary>
   private List<string> _McpToolNames()
   {
      var names = new List<string>();
      foreach (var h in _mcpHandles)
         foreach (var t in h.Tools)
            names.Add(h.QualifiedName(t.Name));
      names.Sort(StringComparer.Ordinal);
      return names;
   }

   /// <summary>
   /// Reconcile what this conversation has been told about the MCP catalog
   /// with what the servers offer now.
   ///
   /// The tool definitions themselves are never persisted — they travel in
   /// every request's tools array — so the transcript carries only the names,
   /// and this is where a change becomes visible to the model. A resumed
   /// session whose server has gained or lost tools gets a short system note
   /// rather than silently different capabilities; a brand-new session just
   /// records its starting set.
   ///
   /// Old tool calls in history stay valid either way: a call naming a tool
   /// that has since vanished simply gets the loop's unknown-tool error.
   /// Expect the note to cost a prompt-cache miss — that is the price of the
   /// model knowing, and it is cheaper than it not knowing.
   /// </summary>
   private void _SyncMcpCatalog()
   {
      if (!_IsMcpProject()) return;

      var current = _McpToolNames();
      if (_lastKnownMcpTools != null && _SameNames(_lastKnownMcpTools, current))
         return;

      if (_lastKnownMcpTools != null)
      {
         var note = _McpDiffNote(_lastKnownMcpTools, current);
         if (note != null)
         {
            _loop?.AppendSystemMessage("[MCP catalog] " + note);
            AppendItem(new NoticeLine(note));
         }
      }

      _lastKnownMcpTools = current;
      _transcriptStore?.AppendMcpTools(current);
   }

   private static bool _SameNames(List<string> a, List<string> b)
   {
      if (a.Count != b.Count) return false;
      for (int i = 0; i < a.Count; i++)
         if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
      return true;
   }

   // "MCP tools added: a, b — removed: c". Null when nothing actually moved.
   private static string? _McpDiffNote(List<string> before, List<string> after)
   {
      var had = new HashSet<string>(before, StringComparer.Ordinal);
      var has = new HashSet<string>(after,  StringComparer.Ordinal);

      var added   = new List<string>();
      var removed = new List<string>();
      foreach (var n in after)  if (!had.Contains(n)) added.Add(n);
      foreach (var n in before) if (!has.Contains(n)) removed.Add(n);
      if (added.Count == 0 && removed.Count == 0) return null;

      var sb = new System.Text.StringBuilder("MCP tools ");
      if (added.Count > 0)
         sb.Append("added: ").Append(string.Join(", ", added));
      if (removed.Count > 0)
      {
         if (added.Count > 0) sb.Append(" — ");
         sb.Append("removed: ").Append(string.Join(", ", removed));
      }
      return sb.ToString();
   }

   /// <summary>
   /// A server announced (and we re-listed) a different catalog mid-session.
   /// Swap the live loop's tool set so the next turn advertises the new one,
   /// then let <see cref="_SyncMcpCatalog"/> tell the model what moved.
   /// </summary>
   private void _OnMcpToolsChanged(McpServerHandle handle)
   {
      _RefreshMcpStatus();
      if (!_mcpReady) return;

      _viewToolRegistry = _BuildToolRegistry(withTaskCallback: true);
      _loop?.UpdateTools(_viewToolRegistry);
      _SyncMcpCatalog();
   }

   /// <summary>
   /// One pill per configured server, colored by connection state. Hidden
   /// entirely for a coding project, and visible from session start for an MCP
   /// one — where it reads "not connected" until the first send, which is the
   /// truth rather than an omission.
   /// </summary>
   private void _RefreshMcpStatus()
   {
      if (PnlMcp == null) return;

      PnlMcp.Children.Clear();
      if (_mcpHandles.Count == 0)
      {
         PnlMcp.IsVisible = false;
         return;
      }

      foreach (var h in _mcpHandles)
      {
         var brush = h.State switch
         {
            McpState.Connected  => (IBrush)new SolidColorBrush(Color.Parse("#6AC96A")),
            McpState.Connecting => new SolidColorBrush(Color.Parse("#C8A050")),
            McpState.Failed     => new SolidColorBrush(Color.Parse("#D86A6A")),
            _                   => ThemeBrushes.TextMuted,
         };

         var dot = new Border
         {
            Width             = 7,
            Height            = 7,
            CornerRadius      = new CornerRadius(4),
            Background        = brush,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 4, 0),
         };
         var label = new TextBlock
         {
            Text              = h.Name,
            FontSize          = 10,
            Foreground        = ThemeBrushes.TextMutedStrong,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
         };

         var pill = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
         pill.Children.Add(dot);
         pill.Children.Add(label);
         ToolTip.SetTip(pill, h.Describe());
         PnlMcp.Children.Add(pill);
      }
      PnlMcp.IsVisible = true;
   }

   /// <summary>
   /// Drop every MCP connection this session held. Kills stdio children —
   /// without this, closing a tab would leave an npx process per session
   /// running until the app exits.
   /// </summary>
   private void _DisposeMcpHandles()
   {
      foreach (var h in _mcpHandles)
      {
         try { h.Dispose(); } catch { }
      }
      _mcpHandles.Clear();
   }

   // -- MCP projects ----------------------------------------------------------

   // This session's MCP servers, one handle per enabled config entry. Empty
   // for a coding project and until the first send of an MCP session.
   private readonly List<McpServerHandle> _mcpHandles = new();

   // True once the first send has finished connecting (successfully or not),
   // so later sends skip straight to the turn. Reset with the session.
   private bool _mcpReady;

   // True while the connect is in flight: blocks a second Send from starting
   // a turn on half-built handles.
   private bool _mcpConnecting;

   // Cancels an in-flight connect. Lives here rather than in _cts because the
   // connect happens before the loop (and before _cts) exists.
   private CancellationTokenSource? _mcpConnectCts;

   // Sorted tool names this conversation was last told about — seeded from the
   // last "mcpTools" transcript record on resume, null for a fresh session.
   // Compared against the live catalog to decide whether the model needs to be
   // told that something changed.
   private List<string>? _lastKnownMcpTools;

   // Ceiling on the whole first-send connect. Generous because an `npx -y`
   // cold start may download a package; it exists so a server that accepts a
   // connection and then says nothing cannot wedge the session forever.
   private const int MCP_CONNECT_TIMEOUT_SEC = 120;
}
