// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Modal editor for a <see cref="PermissionPolicy"/>. One window reused
/// for both the global policy (with the sticky <c>AlwaysDeny</c> /
/// <c>AlwaysAsk</c> options) and per-project policies (regular Allow /
/// Deny / Prompt only).
///
/// Pre-populated with one row per registered tool so the user can
/// pre-configure decisions for tools they haven't seen the agent invoke
/// yet. Saving in-place commits to a working copy and invokes
/// <see cref="_onSave"/> with the finalized policy.
/// </summary>
public partial class PermissionEditorWindow : Window
{
   public enum Mode { Global, Project }

   public PermissionEditorWindow()
   {
      InitializeComponent();
   }

   /// <summary>
   /// Bind to a live policy + a save callback. The dialog mutates a local
   /// copy; only Save commits and invokes the callback. Cancel discards.
   /// </summary>
   public void Configure(
      Mode             mode,
      PermissionPolicy policy,
      ToolRegistry     tools,
      Action<PermissionPolicy> onSave,
      string?          contextHint = null,
      PermissionPolicy? appPolicy = null,
      IReadOnlyDictionary<string, PermissionDecision>? sessionGrants = null)
   {
      _mode          = mode;
      _onSave        = onSave;
      _working       = ClonePolicy(policy);
      _tools         = tools;
      _appPolicy     = appPolicy;
      _sessionGrants = sessionGrants;

      // Decisions list differs by mode. Sticky values are global-only;
      // showing them on a project policy would let the user save settings
      // that the gate silently ignores.
      _decisions = mode == Mode.Global
         ? new[]
            {
               PermissionDecision.Prompt,
               PermissionDecision.Allow,
               PermissionDecision.Deny,
               PermissionDecision.AlwaysDeny,
               PermissionDecision.AlwaysAsk,
            }
         : new[]
            {
               PermissionDecision.Prompt,
               PermissionDecision.Allow,
               PermissionDecision.Deny,
            };

      TxtTitle.Text    = mode == Mode.Global ? "Global permissions" : "Project permissions";
      TxtSubtitle.Text = contextHint ?? "";

      TxtLegend.Text = mode == Mode.Global
         ? "Layered Allow/Deny can be overridden by project policies. " +
           "AlwaysDeny and AlwaysAsk are sticky — narrower scopes cannot override them. " +
           "Use AlwaysAsk to require explicit user approval on every call (e.g. for network tools)."
         : "Project policies override the corresponding global Allow/Deny. " +
           "Sticky global decisions (AlwaysDeny / AlwaysAsk) still take precedence over anything set here. " +
           "Each row shows the App + Session layers and the resulting Effective permission " +
           "(Implement mode; in Plan mode every write/exec call is asked regardless).";

      RebuildRows();
   }

   // -- row UI ---------------------------------------------------------------

   private void RebuildRows()
   {
      RowsPanel.Children.Clear();
      if (_tools == null) return;

      foreach (var t in _tools.All)
         RowsPanel.Children.Add(BuildRow(t));
   }

   private Control BuildRow(ITool tool)
   {
      // Category badge ------------------------------------------------------
      var (catText, catBg, catFg) = tool.Category switch
      {
         ToolCategory.Read    => ("READ",    ThemeBrushes.HoverBackground,    ThemeBrushes.TextMutedStrong),
         ToolCategory.Write   => ("WRITE",   ThemeBrushes.AccentDark,         (IBrush)Brushes.White),
         ToolCategory.Execute => ("EXECUTE", (IBrush)Brushes.IndianRed,       (IBrush)Brushes.White),
         _                    => ("?",       ThemeBrushes.HoverBackground,    ThemeBrushes.TextMutedStrong),
      };
      var catLbl = new Border
      {
         Background          = catBg,
         CornerRadius        = new CornerRadius(2),
         Padding             = new Thickness(6, 1),
         VerticalAlignment   = VerticalAlignment.Center,
         HorizontalAlignment = HorizontalAlignment.Right,
         Child = new TextBlock
         {
            Text       = catText,
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = catFg,
         },
      };

      // Name ----------------------------------------------------------------
      var nameTb = new TextBlock
      {
         Text              = tool.Name,
         FontFamily        = MONO,
         FontSize          = 12,
         Foreground        = ThemeBrushes.TextPrimary,
         VerticalAlignment = VerticalAlignment.Center,
      };

      // Description ---------------------------------------------------------
      var descTb = new TextBlock
      {
         Text              = tool.Description,
         FontSize          = 10,
         Foreground        = ThemeBrushes.TextMuted,
         TextTrimming      = TextTrimming.CharacterEllipsis,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(0, 2, 8, 0),
      };

      // Decision combo ------------------------------------------------------
      var current = _working.Get(tool.Name);
      var labels = new string[_decisions.Length];
      int currentIdx = -1;
      for (int i = 0; i < _decisions.Length; i++)
      {
         labels[i] = LabelFor(_decisions[i]);
         if (_decisions[i] == current) currentIdx = i;
      }
      // Sticky value persisted at project layer (shouldn't happen normally but
      // could via manual JSON edit) — display the closest renderable choice.
      if (currentIdx < 0) currentIdx = 0;

      var combo = new ComboBox
      {
         ItemsSource         = labels,
         SelectedIndex       = currentIdx,
         Width               = 160,
         HorizontalAlignment = HorizontalAlignment.Right,
         Foreground          = ThemeBrushes.TextPrimary,
         Background          = ThemeBrushes.PanelBackground,
      };
      // Layout --------------------------------------------------------------
      var grid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("180,*,80,Auto"),
         Margin            = new Thickness(8, 6),
      };
      Grid.SetColumn(nameTb, 0);
      Grid.SetColumn(descTb, 1);
      Grid.SetColumn(catLbl, 2);
      Grid.SetColumn(combo,  3);
      grid.Children.Add(nameTb);
      grid.Children.Add(descTb);
      grid.Children.Add(catLbl);
      grid.Children.Add(combo);

      // Second line: the App + Session "reasoning" on the left, and the
      // resulting Effective permission directly under the combo on the right.
      // Project editor only — for the Global editor this row *is* the App
      // layer, so a comparison would be redundant.
      bool showLayers = _mode == Mode.Project && _appPolicy != null;
      Control body = grid;
      if (showLayers)
      {
         var reasoningTb = new TextBlock
         {
            FontSize            = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            TextWrapping        = TextWrapping.NoWrap,
            Margin              = new Thickness(0, 0, 12, 0),
         };
         var effectiveTb = new TextBlock
         {
            FontSize            = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            TextWrapping        = TextWrapping.NoWrap,
         };

         // Mirror the main grid's columns so the effective text lines up
         // beneath the combo (col 3) while the reasoning sits to the left.
         var layerGrid = new Grid
         {
            ColumnDefinitions = new ColumnDefinitions("180,*,80,Auto"),
            Margin            = new Thickness(8, 0, 8, 6),
         };
         Grid.SetColumn(reasoningTb, 0);
         Grid.SetColumnSpan(reasoningTb, 3);
         Grid.SetColumn(effectiveTb, 3);
         layerGrid.Children.Add(reasoningTb);
         layerGrid.Children.Add(effectiveTb);

         _RefreshLayerLine(reasoningTb, effectiveTb, tool.Name);

         combo.SelectionChanged += (_, _) =>
         {
            int i = combo.SelectedIndex;
            if (i >= 0 && i < _decisions.Length)
               _working.Set(tool.Name, _decisions[i]);
            _RefreshLayerLine(reasoningTb, effectiveTb, tool.Name);
         };

         var stack = new StackPanel { Orientation = Orientation.Vertical };
         stack.Children.Add(grid);
         stack.Children.Add(layerGrid);
         body = stack;
      }
      else
      {
         combo.SelectionChanged += (_, _) =>
         {
            int i = combo.SelectedIndex;
            if (i >= 0 && i < _decisions.Length)
               _working.Set(tool.Name, _decisions[i]);
         };
      }

      return new Border
      {
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 0, 0, 1),
         Child           = body,
      };
   }

   // Rebuild the reasoning ("App: … · Session: …") and the colored Effective
   // ("Effective: X (from Y)") for one tool, reflecting the current working
   // (Project) decision. Effective color: Allow green, Deny red, Ask amber.
   private void _RefreshLayerLine(TextBlock reasoning, TextBlock effective, string toolName)
   {
      var app     = _appPolicy?.Get(toolName) ?? PermissionDecision.Prompt;
      var project = _working.Get(toolName);
      PermissionDecision? session = null;
      if (_sessionGrants != null && _sessionGrants.TryGetValue(toolName, out var sv))
         session = sv;

      var eff = PermissionResolver.Resolve(app, session, project);

      reasoning.Inlines!.Clear();
      reasoning.Inlines.Add(new Run { Text = "App: ", Foreground = ThemeBrushes.TextMuted });
      reasoning.Inlines.Add(new Run { Text = ShortLabel(app), Foreground = ThemeBrushes.TextMutedStrong });
      reasoning.Inlines.Add(new Run { Text = "   ·   Session: ", Foreground = ThemeBrushes.TextMuted });
      reasoning.Inlines.Add(new Run
      {
         Text       = session is PermissionDecision sd ? ShortLabel(sd) : "—",
         Foreground = ThemeBrushes.TextMutedStrong,
      });

      effective.Inlines!.Clear();
      effective.Inlines.Add(new Run { Text = "Effective: ", Foreground = ThemeBrushes.TextMuted });
      effective.Inlines.Add(new Run
      {
         Text       = ShortLabel(eff.Decision),
         FontWeight = FontWeight.SemiBold,
         Foreground = EffectiveBrush(eff.Decision),
      });
      effective.Inlines.Add(new Run
      {
         Text       = "  (from " + eff.Source + ")",
         Foreground = ThemeBrushes.TextMuted,
      });
   }

   private static IBrush EffectiveBrush(PermissionDecision d) => d switch
   {
      PermissionDecision.Allow => new SolidColorBrush(Color.Parse("#4EC9A0")),  // green
      PermissionDecision.Deny  => (IBrush)Brushes.IndianRed,
      _                        => new SolidColorBrush(Color.Parse("#C8A050")),  // amber = ask
   };

   private static string ShortLabel(PermissionDecision d) => d switch
   {
      PermissionDecision.Prompt     => "Ask",
      PermissionDecision.Allow      => "Allow",
      PermissionDecision.Deny       => "Deny",
      PermissionDecision.AlwaysAsk  => "Always Ask",
      PermissionDecision.AlwaysDeny => "Always Deny",
      _ => d.ToString(),
   };

   private static string LabelFor(PermissionDecision d) => d switch
   {
      PermissionDecision.Prompt     => "Prompt (ask)",
      PermissionDecision.Allow      => "Allow",
      PermissionDecision.Deny       => "Deny",
      PermissionDecision.AlwaysAsk  => "Always Ask (sticky)",
      PermissionDecision.AlwaysDeny => "Always Deny (sticky)",
      _ => d.ToString(),
   };

   // -- footer ---------------------------------------------------------------

   private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();

   private void BtnSave_Click(object? sender, RoutedEventArgs e)
   {
      try { _onSave?.Invoke(_working); } catch { }
      Close();
   }

   // -- helpers --------------------------------------------------------------

   private static PermissionPolicy ClonePolicy(PermissionPolicy source)
   {
      var p = new PermissionPolicy();
      foreach (var kv in source.Tools)
         p.Tools[kv.Key] = kv.Value;
      return p;
   }

   // -- fields ---------------------------------------------------------------

   private static readonly FontFamily MONO =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private Mode                  _mode;
   private PermissionPolicy      _working = new();
   private ToolRegistry?         _tools;
   private PermissionDecision[]  _decisions = Array.Empty<PermissionDecision>();
   private Action<PermissionPolicy>? _onSave;

   // Read-only context for the App + Session + Effective line (Project mode).
   private PermissionPolicy?     _appPolicy;
   private IReadOnlyDictionary<string, PermissionDecision>? _sessionGrants;
}
