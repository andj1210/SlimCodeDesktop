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
/// Project selection and the full-page session picker: per-agent sections,
/// session cards, the shared rename/delete menu and on-demand naming.
/// </summary>
public partial class MainWindow
{
   // -- project selection + session picker ------------------------------------

   private void SelectProject(string path)
   {
      _selectedProjectPath = path;
      RebuildSidebar();
      UpdateHeader();

      // A project with an open native tab: selecting it in the sidebar
      // focuses its live workspace, same as clicking the tab.
      var tab = _FindTab(path);
      if (tab != null)
      {
         ActivateNativeTab(tab);
         return;
      }

      RebuildSessionsView();
      SetMainView(MainView.Sessions);
   }

   private void RebuildSessionsView()
   {
      SessionsPanel.Children.Clear();
      var path = _selectedProjectPath;
      if (path == null) return;

      // No header here — the project name + path is already shown in the
      // top bar (UpdateHeader) so we skip the redundant block.
      foreach (var agent in AgentRegistry.All)
         SessionsPanel.Children.Add(BuildAgentSection(agent, path));
   }

   // One per-agent block: header (name + count + refresh) over a card area.
   // Enumeration runs off the UI thread; the area shows "Loading…" until the
   // result arrives or returns the cached value synchronously.
   private Control BuildAgentSection(Agent agent, string path)
   {
      var container = new StackPanel { Margin = new Thickness(0, 4, 0, 18) };

      var nameLbl = new TextBlock
      {
         Text = agent.DisplayName,
         FontSize = 14,
         FontWeight = FontWeight.SemiBold,
         Foreground = ThemeBrushes.TextPrimary,
         VerticalAlignment = VerticalAlignment.Center,
      };
      var countLbl = new TextBlock
      {
         Text = "",
         FontSize = 11,
         Foreground = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
      };
      var refresh = new Button
      {
         Content = "↻",
         FontSize = 11,
         Padding = new Thickness(6, 1),
         VerticalAlignment = VerticalAlignment.Center,
      };
      ToolTip.SetTip(refresh, "Refresh");

      var header = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         Spacing = 8,
         Margin = new Thickness(0, 0, 0, 6),
      };
      header.Children.Add(nameLbl);
      header.Children.Add(countLbl);
      header.Children.Add(refresh);
      container.Children.Add(header);

      var body = new StackPanel();
      container.Children.Add(body);

      void Render(IReadOnlyList<AgentSession> sessions)
      {
         body.Children.Clear();
         countLbl.Text = "(" + sessions.Count + ")";
         if (sessions.Count == 0)
         {
            body.Children.Add(new TextBlock
            {
               Text = "no history",
               Foreground = ThemeBrushes.TextMuted,
               FontSize = 11,
               FontStyle = FontStyle.Italic,
            });
            return;
         }

         // Lineage order only — the cards wrap at a fixed width, so a fork is
         // marked on the card itself rather than indented the way the sidebar
         // rows are. Forks still land directly after the session they came
         // from, which is what makes the marker legible.
         var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
         foreach (var e in SessionLineage.Arrange(sessions))
            wrap.Children.Add(BuildSessionCard(agent, path, e.Session, e.Depth));
         body.Children.Add(wrap);
      }

      void Load(bool force)
      {
         var key = (agent.Id, path);
         // Native sessions grow append-only on disk for the lifetime of an
         // active chat, so a cached list goes stale the moment we open the
         // picker for a project that's mid-conversation. Cost of re-reading
         // is negligible (small JSONL files, first-record stop).
         bool useCache = !force && agent is not Agents.Native.NativeAgent;
         if (useCache && _sessionCache.TryGetValue(key, out var cached))
         {
            Render(cached);
            return;
         }

         body.Children.Clear();
         countLbl.Text = "…";
         body.Children.Add(new TextBlock
         {
            Text = "Loading…",
            Foreground = ThemeBrushes.TextMuted,
            FontSize = 11,
         });

         var capturedAgent = agent;
         var capturedPath = path;
         Task.Run(() =>
         {
            try { return capturedAgent.EnumerateSessions(capturedPath); }
            catch { return (IReadOnlyList<AgentSession>)Array.Empty<AgentSession>(); }
         }).ContinueWith(t =>
         {
            var result = t.IsCompletedSuccessfully && t.Result != null
               ? t.Result : Array.Empty<AgentSession>();
            Dispatcher.UIThread.Post(() =>
            {
               // Bail if user navigated away from this project between request
               // and response — stale rendering would write into a now-detached panel.
               if (!string.Equals(_selectedProjectPath, capturedPath, StringComparison.OrdinalIgnoreCase))
                  return;
               _sessionCache[key] = result;
               Render(result);
            });
         });
      }

      refresh.Click += (_, _) => Load(true);
      Load(false);
      return container;
   }

   private Control BuildSessionCard(Agent agent, string projectPath, AgentSession session, int depth = 0)
   {
      var card = new Border
      {
         Width = 220,
         Background = ThemeBrushes.PanelBackground,
         BorderBrush = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1),
         CornerRadius = new CornerRadius(4),
         Padding = new Thickness(10, 8),
         Margin = new Thickness(0, 0, 8, 8),
         Cursor = new Cursor(StandardCursorType.Hand),
      };

      var title = new TextBlock
      {
         Text = (depth > 0 ? "⑂ " : "")
                + (string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title),
         Foreground = ThemeBrushes.TextPrimary,
         FontSize = 12,
         FontWeight = FontWeight.SemiBold,
         // Unnamed sessions show a quote of the first prompt — italic marks
         // that as derived rather than a name the user (or the model) chose.
         FontStyle = session.Caption == null ? FontStyle.Italic : FontStyle.Normal,
         TextWrapping = TextWrapping.Wrap,
         TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
         MaxLines = 2,
      };
      var sub = new TextBlock
      {
         Text = ComposeSubtitle(session, depth),
         Foreground = ThemeBrushes.TextMuted,
         FontSize = 10,
         Margin = new Thickness(0, 4, 0, 0),
      };

      var stack = new StackPanel();
      stack.Children.Add(title);
      stack.Children.Add(sub);
      card.Child = stack;

      if (!string.IsNullOrWhiteSpace(session.PreviewText))
         ToolTip.SetTip(card, _BuildSessionPreviewTip(session.PreviewText));

      card.PointerEntered += (_, _) => card.Background = ThemeBrushes.HoverBackground;
      card.PointerExited  += (_, _) => card.Background = ThemeBrushes.PanelBackground;
      card.PointerPressed += (_, e) =>
      {
         if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
         LaunchAgent(projectPath, agent, session.SessionId);
      };

      var menu = BuildSessionContextMenu(agent, projectPath, session, _RefreshSessionPickers);
      if (menu != null) card.ContextMenu = menu;

      return card;
   }

   // Tooltip body shared by both session pickers: the first prompt, monospaced.
   private static Control _BuildSessionPreviewTip(string preview) => new Border
   {
      MaxWidth   = 780,
      Padding    = new Thickness(10, 8),
      Background = ThemeBrushes.TooltipBackground,
      Child      = new TextBlock
      {
         Text         = preview,
         FontFamily   = new FontFamily("Consolas, Cascadia Mono, Courier New"),
         FontSize     = 11,
         TextWrapping = TextWrapping.Wrap,
         Foreground   = ThemeBrushes.TooltipText,
      },
   };

   /// <summary>
   /// Rename / delete menu shared by the sidebar rows and the full-page cards.
   /// Entries appear only for the capabilities the agent declares — an agent
   /// that can't delete safely (e.g. nanocoder) gets no Delete entry rather
   /// than one that always fails after the click. Returns null when the agent
   /// supports neither, so the caller can skip attaching a menu at all.
   /// <paramref name="onChanged"/> re-renders the pickers once the store moved.
   /// </summary>
   private ContextMenu? BuildSessionContextMenu(Agent agent, string projectPath,
                                                AgentSession session, Action onChanged)
   {
      if (!agent.CanRenameSessions && !agent.CanDeleteSessions) return null;

      var menu = new ContextMenu();

      if (agent.CanRenameSessions)
      {
         var rename = new MenuItem
         {
            Header = session.Caption == null ? "Name session…" : "Rename…",
         };
         rename.Click += async (_, _) =>
         {
            var name = await ShowPrompt("Name session", "Session name:", session.Caption ?? "");
            if (name == null) return; // cancelled (ShowPrompt folds blank into null)
            if (!agent.RenameSession(projectPath, session.Id, name))
            {
               await ShowError("Could not write the session name.");
               return;
            }
            _sessionCache.Remove((agent.Id, projectPath));
            onChanged();
         };
         menu.Items.Add(rename);

         // Ask the model for one. Native only — it's the agent that has a
         // provider catalog and a transcript recording which model the
         // session ran on.
         if (agent is Agents.Native.NativeAgent titleAgent)
         {
            var suggest = new MenuItem { Header = "Suggest name…" };
            ToolTip.SetTip(suggest,
               "Have the session's own model propose a name from its first prompt. "
               + "You get to edit it before it's applied.");
            suggest.Click += async (_, _) =>
               await _SuggestSessionName(titleAgent, projectPath, session, onChanged);
            menu.Items.Add(suggest);
         }

         // ShowPrompt can't express "clear", so clearing gets its own entry.
         if (session.Caption != null)
         {
            var clear = new MenuItem { Header = "Clear name" };
            clear.Click += async (_, _) =>
            {
               if (!agent.RenameSession(projectPath, session.Id, ""))
               {
                  await ShowError("Could not clear the session name.");
                  return;
               }
               _sessionCache.Remove((agent.Id, projectPath));
               onChanged();
            };
            menu.Items.Add(clear);
         }
      }

      if (agent.CanDeleteSessions)
      {
         if (menu.Items.Count > 0) menu.Items.Add(new Separator());
         var delItem = new MenuItem { Header = "Delete session…" };
         delItem.Click += async (_, _) =>
         {
            var prompt = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;
            bool ok = await ShowConfirm(
               "Delete session",
               "Permanently delete this session?\n\n" + prompt,
               okLabel: "Delete");
            if (!ok) return;

            if (!agent.DeleteSession(projectPath, session.Id))
            {
               await ShowError("Could not delete the session file.");
               return;
            }

            _sessionCache.Remove((agent.Id, projectPath));
            onChanged();
         };
         menu.Items.Add(delItem);
      }

      return menu;
   }

   /// <summary>
   /// Name a session on demand: ask a model for a title derived from the
   /// session's first prompt, then hand it to the ordinary rename dialog so
   /// the user can accept or edit it. Works whether or not the provider has
   /// auto-titling armed — this one was asked for explicitly.
   ///
   /// Which model? The one the session itself ran on, read back from its
   /// transcript (the last "model" record, or the meta's). That is the model
   /// most likely to be resident — with llama-swap, picking a different one
   /// pays a full load, which dwarfs anything a smaller model could save on a
   /// call this size. Only when the recording no longer resolves against the
   /// catalog does this fall back to the app-wide selection.
   /// </summary>
   private async Task _SuggestSessionName(Agents.Native.NativeAgent agent, string projectPath,
                                          AgentSession session, Action onChanged)
   {
      var file = Agents.Native.Sessions.TranscriptStore.FileFor(projectPath, session.Id);
      if (file == null || !File.Exists(file))
      {
         await ShowError("Session file not found.");
         return;
      }

      string? prompt   = null;
      string? model    = null;
      string? provider = null;
      foreach (var r in Agents.Native.Sessions.TranscriptStore.Read(file))
      {
         if (r.Type == "meta")
         {
            model    = r.Model;
            provider = r.Provider;
         }
         else if (r.Type == "model" && !string.IsNullOrWhiteSpace(r.Model))
         {
            // A mid-session switch: the session would resume on this one.
            model    = r.Model;
            provider = r.Provider;
         }
         else if (r.Type == "msg" && r.Role == "user" && prompt == null)
         {
            prompt = r.Content;
         }
      }

      if (string.IsNullOrWhiteSpace(prompt))
      {
         await ShowError("This session has no text prompt to name it from.");
         return;
      }

      var s = agent.Settings;
      // Exact match only: ResolveProvider falls back to the first configured
      // provider, which would aim a recorded model id at an unrelated
      // endpoint. Better to fall back to the selected pair as a pair.
      Agents.Native.Llm.ProviderInfo? prov = null;
      foreach (var p in s.Providers)
         if (string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase)) { prov = p; break; }

      if (prov == null || string.IsNullOrWhiteSpace(model))
      {
         prov  = s.ResolveSelectedProvider();
         model = s.SelectedModel;
      }
      if (prov == null || string.IsNullOrWhiteSpace(prov.BaseUrl) || string.IsNullOrWhiteSpace(model))
      {
         await ShowError("No usable model for this session — configure a provider and model first.");
         return;
      }

      var titler = new Agents.Native.Sessions.SessionTitler(
         new Agents.Native.Llm.OpenAiCompatClient(
            prov.BaseUrl, string.IsNullOrWhiteSpace(prov.ApiKey) ? null : prov.ApiKey),
         model!,
         prov.FindModel(model)?.Reasoning.OffKwargs);

      // The wait is usually a second or two, but a swapping server may have to
      // load the model first — the cursor is the only feedback there is.
      string? proposed;
      var prevCursor = Cursor;
      Cursor = new Cursor(StandardCursorType.Wait);
      try
      {
         using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120));
         proposed = await titler.ProposeAsync(prompt!, cts.Token);
      }
      catch
      {
         proposed = null;
      }
      finally
      {
         Cursor = prevCursor;
      }

      if (proposed == null)
      {
         await ShowError("The model did not return a usable name.");
         return;
      }

      // Straight into the rename dialog rather than applying it: a generated
      // name is a suggestion, and the edit is free while it's in front of you.
      var name = await ShowPrompt("Name session", "Session name:", proposed);
      if (name == null) return;

      if (!agent.RenameSession(projectPath, session.Id, name))
      {
         await ShowError("Could not write the session name.");
         return;
      }
      _sessionCache.Remove((agent.Id, projectPath));
      onChanged();
   }

   private static string RelativeTime(DateTime t)
   {
      if (t == default) return "";
      var d = DateTime.Now - t;
      if (d.TotalSeconds < 60) return "just now";
      if (d.TotalMinutes < 60) return ((int)d.TotalMinutes) + "m ago";
      if (d.TotalHours   < 24) return ((int)d.TotalHours)   + "h ago";
      if (d.TotalDays    <  7) return ((int)d.TotalDays)    + "d ago";
      return t.ToString("MMM d");
   }

   private static string FormatTokens(int t)
   {
      if (t <= 0) return "";
      if (t < 1000) return t + " tok";
      if (t < 10_000) return (t / 1000.0).ToString("0.0",
         System.Globalization.CultureInfo.InvariantCulture) + "k tok";
      if (t < 1_000_000) return (t / 1000) + "k tok";
      return (t / 1_000_000.0).ToString("0.0",
         System.Globalization.CultureInfo.InvariantCulture) + "M tok";
   }

   /// <param name="depth">
   /// Fork depth. The "branched" tag is shown only for a fork whose parent is
   /// still on screen (depth > 0) — on an orphaned fork it would point at a
   /// session the user cannot see.
   /// </param>
   private static string ComposeSubtitle(AgentSession s, int depth = 0)
   {
      var rel = RelativeTime(s.LastUsed);
      var tok = FormatTokens(s.ContextTokens);

      var parts = new List<string>(3);
      if (rel.Length > 0) parts.Add(rel);
      if (tok.Length > 0) parts.Add(tok);
      if (depth > 0)
      {
         var how = SessionLineage.DescribeFork(s.ForkKind);
         parts.Add(how.Length > 0 ? how : "branched");
      }
      return string.Join(" · ", parts);
   }
}
