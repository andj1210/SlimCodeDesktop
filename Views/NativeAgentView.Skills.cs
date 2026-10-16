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
/// Skills: the picker, pending-skill injection on Send, and rebuilding the
/// loaded set from a resumed history.
/// </summary>
public partial class NativeAgentView
{
   /// <summary>
   /// Effective skill set for the current project: global + extra roots +
   /// project-local, project-wins on a name clash. Read fresh (cheap dir
   /// walk) so externally-edited SKILL.md files take effect immediately.
   /// </summary>
   private IReadOnlyList<SkillInfo> _DiscoverSkills()
   {
      if (string.IsNullOrEmpty(_projectPath))
         return Array.Empty<SkillInfo>();
      return SkillStore.Discover(_projectPath, _agent?.Settings.SkillSearchPaths);
   }

   // -- skills ----------------------------------------------------------------

   /// <summary>
   /// Inject every queued skill's full instructions as a system message ahead
   /// of the next user turn, mark it loaded, and drop a NoticeLine. The marker
   /// prefix lets <see cref="ResumeSession"/> recognize these on reload. Skills
   /// already loaded or no longer discoverable are skipped.
   /// </summary>
   private void _DrainPendingSkills()
   {
      if (_pendingSkills.Count == 0 || _loop == null) return;

      var skills = _DiscoverSkills();
      foreach (var name in new List<string>(_pendingSkills))
      {
         if (_loadedSkills.Contains(name)) continue;
         SkillInfo? hit = null;
         foreach (var sk in skills)
            if (string.Equals(sk.Name, name, StringComparison.OrdinalIgnoreCase)) { hit = sk; break; }
         if (hit == null) continue;

         _loop.AppendSystemMessage(SKILL_LOADED_MARK + hit.Name + "\n\n" + hit.Body);
         _loadedSkills.Add(hit.Name);
         AppendItem(new NoticeLine("loaded skill: " + hit.Name));
      }
      _pendingSkills.Clear();
   }

   /// <summary>
   /// Open the skill picker. Loaded skills are checked + disabled (they can't
   /// be unloaded mid-session); pending selections are checked and tinted;
   /// everything else is an unchecked toggle that adds/removes from the
   /// pending set for the next Send.
   /// </summary>
   private void BtnSkills_Click(object? sender, RoutedEventArgs e)
   {
      var skills = _DiscoverSkills();

      var panel = new StackPanel { Spacing = 2, MinWidth = 280, Margin = new Thickness(4) };
      panel.Children.Add(new TextBlock
      {
         Text       = "Skills to load into this session",
         FontWeight = FontWeight.Bold,
         FontSize   = 12,
         Margin     = new Thickness(2, 0, 0, 6),
      });

      if (skills.Count == 0)
      {
         panel.Children.Add(new TextBlock
         {
            Text       = "(no skills found — add SKILL.md folders under the global or project skills folder)",
            Foreground = ThemeBrushes.TextMuted,
            FontStyle  = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth   = 320,
         });
      }

      foreach (var sk in skills)
      {
         bool loaded  = _loadedSkills.Contains(sk.Name);
         bool pending = _pendingSkills.Contains(sk.Name);

         var status = loaded ? "  (loaded)" : pending ? "  (pending)" : "";
         var chk = new CheckBox
         {
            Content   = sk.Name + " [" + sk.SourceLabel + "]" + status,
            IsChecked = loaded || pending,
            IsEnabled = !loaded,
            FontSize  = 12,
            Foreground = pending && !loaded ? UserBubble.PlanAccent : ThemeBrushes.TextPrimary,
         };
         if (!string.IsNullOrWhiteSpace(sk.Description))
            ToolTip.SetTip(chk, sk.Description);

         var captured = sk.Name;
         chk.IsCheckedChanged += (_, _) =>
         {
            if (_loadedSkills.Contains(captured)) return; // can't toggle loaded
            if (chk.IsChecked == true) _pendingSkills.Add(captured);
            else                       _pendingSkills.Remove(captured);
         };
         panel.Children.Add(chk);
      }

      var flyout = new Flyout
      {
         Content = new ScrollViewer
         {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
         },
      };
      flyout.ShowAt(BtnSkills);
   }

   /// <summary>
   /// Walk a resumed history and rebuild <see cref="_loadedSkills"/>: from the
   /// injected skill system messages (marker prefix) and from successful
   /// <c>use_skill</c> tool calls the model made. Keeps the picker's
   /// loaded/checked state correct after a reload.
   /// </summary>
   private void _RebuildLoadedSkillsFromHistory(IReadOnlyList<ChatMessage> history)
   {
      // Map tool-call id -> whether its result errored, so a use_skill call
      // only counts as loaded when the body was actually delivered.
      var errorById = new Dictionary<string, bool>(StringComparer.Ordinal);
      foreach (var m in history)
         if (m.Role == ChatRole.Tool && !string.IsNullOrEmpty(m.ToolCallId))
            errorById[m.ToolCallId!] = m.IsError;

      foreach (var m in history)
      {
         if (m.Role == ChatRole.System &&
             m.Content != null && m.Content.StartsWith(SKILL_LOADED_MARK, StringComparison.Ordinal))
         {
            var name = _ParseLoadedSkillName(m.Content);
            if (!string.IsNullOrEmpty(name)) _loadedSkills.Add(name!);
         }

         if (m.Role == ChatRole.Assistant && m.ToolCalls != null)
         {
            foreach (var tc in m.ToolCalls)
            {
               if (!string.Equals(tc.Name, "use_skill", StringComparison.OrdinalIgnoreCase)) continue;
               if (errorById.TryGetValue(tc.Id, out var errored) && errored) continue;
               var name = _ParseUseSkillArgName(tc.ArgumentsJson);
               if (!string.IsNullOrEmpty(name)) _loadedSkills.Add(name!);
            }
         }
      }
   }

   // Extract "<name>" from a "[Loaded skill] <name>\n\n..." system message.
   private static string? _ParseLoadedSkillName(string content)
   {
      var rest = content.Substring(SKILL_LOADED_MARK.Length);
      int nl = rest.IndexOf('\n');
      var line = nl < 0 ? rest : rest.Substring(0, nl);
      return line.Trim();
   }

   private static string? _ParseUseSkillArgName(string argsJson)
   {
      if (string.IsNullOrWhiteSpace(argsJson)) return null;
      try
      {
         using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
         if (doc.RootElement.TryGetProperty("name", out var n) &&
             n.ValueKind == System.Text.Json.JsonValueKind.String)
            return n.GetString();
      }
      catch { }
      return null;
   }

   private const string SKILL_LOADED_MARK = "[Loaded skill] ";

   // Skill state for the current session. _loadedSkills = skills whose full
   // instructions are already in context (via the picker, auto-load, or a
   // model use_skill call); _pendingSkills = user-selected to inject on the
   // next Send. _skillCallArgNames maps an in-flight use_skill call id to the
   // skill name so a successful completion can mark it loaded.
   private readonly HashSet<string>    _loadedSkills     = new(StringComparer.OrdinalIgnoreCase);
   private readonly HashSet<string>    _pendingSkills    = new(StringComparer.OrdinalIgnoreCase);
   private readonly Dictionary<string, string> _skillCallArgNames = new(StringComparer.Ordinal);
}
