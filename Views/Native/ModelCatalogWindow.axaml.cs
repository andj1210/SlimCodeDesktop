// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Settings;
using SlimCodeDesktop.Themes;
using SlimCodeDesktop.Views.Native.Controls;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Modal editor for the per-agent provider catalog. Each provider card lets
/// the user edit its name / endpoint / API key, discover its models, and
/// per-model: adjust the context window, hide it from the chat picker, or
/// remove it. Providers themselves can be added and removed.
///
/// Edits happen on a local copy; <see cref="BtnSave_Click"/> commits back to
/// <see cref="NativeAgentSettings.Providers"/> and persists. Cancel /
/// close-via-X discards changes silently.
/// </summary>
public partial class ModelCatalogWindow : Window
{
   public ModelCatalogWindow()
   {
      InitializeComponent();
   }

   /// <summary>Bind to a specific agent's settings + persistence store before showing.</summary>
   public void Configure(NativeAgentSettings settings, Action onSaved)
   {
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _onSaved  = onSaved;

      // Work on a deep copy — Cancel must leave the original untouched.
      _working = new List<ProviderInfo>();
      foreach (var p in settings.Providers)
         _working.Add(_CloneProvider(p));
      RebuildRows();
   }

   private static ProviderInfo _CloneProvider(ProviderInfo p)
   {
      var clone = new ProviderInfo
      {
         Name    = p.Name,
         BaseUrl = p.BaseUrl,
         ApiKey  = p.ApiKey,
         Models  = new List<ModelInfo>(),
         AutoTitleSessions = p.AutoTitleSessions,
      };
      foreach (var m in p.Models)
      {
         clone.Models.Add(new ModelInfo
         {
            Id            = m.Id,
            ContextWindow = m.ContextWindow,
            AddedManually = m.AddedManually,
            DisplayName   = m.DisplayName,
            Hidden        = m.Hidden,
            ReasoningStyle   = m.ReasoningStyle,
            DefaultReasoning = m.DefaultReasoning,
         });
      }
      return clone;
   }

   // -- provider cards -------------------------------------------------------

   private void RebuildRows()
   {
      RowsPanel.Children.Clear();
      if (_working.Count == 0)
      {
         RowsPanel.Children.Add(new TextBlock
         {
            Text       = "(no providers — click \"+ Add provider\" to configure one)",
            Foreground = ThemeBrushes.TextMuted,
            FontStyle  = FontStyle.Italic,
            Margin     = new Thickness(8, 8),
         });
         return;
      }

      foreach (var provider in _working)
         RowsPanel.Children.Add(BuildProviderCard(provider));
   }

   private Control BuildProviderCard(ProviderInfo provider)
   {
      // -- header: name / endpoint / key + actions --------------------------
      var nameBox = _Field(provider.Name, 180, "Provider name");
      nameBox.LostFocus += (_, _) => provider.Name = nameBox.Text ?? "";

      var urlBox = _Field(provider.BaseUrl, 0, "http://localhost:8080");
      urlBox.LostFocus += (_, _) => provider.BaseUrl = urlBox.Text ?? "";

      var keyBox = _Field(provider.ApiKey, 220, "API key (optional)");
      keyBox.LostFocus += (_, _) => provider.ApiKey = keyBox.Text ?? "";

      var status = new TextBlock
      {
         FontSize          = 11,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(0, 0, 8, 0),
         IsVisible         = false,
      };

      var btnDiscover = new Button
      {
         Content    = "Discover",
         Padding    = new Thickness(10, 4),
         Foreground = ThemeBrushes.TextPrimary,
         Background = ThemeBrushes.NeutralButtonBackground,
      };
      ToolTip.SetTip(btnDiscover, "Query this provider's /v1/models list, then pick which of them to add to its catalog.");
      btnDiscover.Click += async (_, _) =>
      {
         // Pick up any in-flight edits to the endpoint/key before discovering.
         provider.Name    = nameBox.Text ?? "";
         provider.BaseUrl = urlBox.Text  ?? "";
         provider.ApiKey  = keyBox.Text  ?? "";
         await _DiscoverProvider(provider, btnDiscover, status);
      };

      var btnRemove = new Button
      {
         Content    = "Remove",
         Padding    = new Thickness(10, 4),
         Foreground = ThemeBrushes.TextPrimary,
         Background = ThemeBrushes.NeutralButtonBackground,
      };
      ToolTip.SetTip(btnRemove, "Delete this provider and all its models.");
      btnRemove.Click += (_, _) =>
      {
         _working.Remove(provider);
         RebuildRows();
      };

      var actions = new StackPanel
      {
         Orientation       = Orientation.Horizontal,
         Spacing           = 6,
         VerticalAlignment = VerticalAlignment.Center,
      };
      actions.Children.Add(status);
      actions.Children.Add(btnDiscover);
      actions.Children.Add(btnRemove);

      var headerRow = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
      };
      var nameCell = _Labeled("Name", nameBox);
      var urlCell  = _Labeled("Endpoint", urlBox);
      var keyCell  = _Labeled("API key", keyBox);
      Grid.SetColumn(nameCell, 0);
      Grid.SetColumn(urlCell,  1);
      Grid.SetColumn(keyCell,  2);
      Grid.SetColumn(actions,  3);
      headerRow.Children.Add(nameCell);
      headerRow.Children.Add(urlCell);
      headerRow.Children.Add(keyCell);
      headerRow.Children.Add(actions);

      // -- models sub-list --------------------------------------------------
      var modelsPanel = new StackPanel { Spacing = 0, Margin = new Thickness(12, 6, 0, 0) };
      if (provider.Models.Count == 0)
      {
         modelsPanel.Children.Add(new TextBlock
         {
            Text       = "(no models — Discover, or add one manually)",
            FontSize   = 11,
            Foreground = ThemeBrushes.TextMuted,
            FontStyle  = FontStyle.Italic,
            Margin     = new Thickness(4, 2),
         });
      }
      else
      {
         // One header per card, so the model rows carry no repeated labels.
         modelsPanel.Children.Add(_BuildModelHeader());
         // Visible models first, hidden ones sorted to the back.
         foreach (var model in provider.Models)
            if (!model.Hidden) modelsPanel.Children.Add(BuildModelRow(provider, model));
         foreach (var model in provider.Models)
            if (model.Hidden) modelsPanel.Children.Add(BuildModelRow(provider, model));
      }

      var btnAddModel = new Button
      {
         Content    = "+ Add model",
         Padding    = new Thickness(8, 2),
         Margin     = new Thickness(4, 4, 0, 0),
         FontSize   = 11,
         Foreground = ThemeBrushes.TextPrimary,
         Background = ThemeBrushes.NeutralButtonBackground,
         HorizontalAlignment = HorizontalAlignment.Left,
      };
      btnAddModel.Click += (_, _) =>
      {
         provider.Models.Add(new ModelInfo
         {
            Id            = "model-id",
            ContextWindow = NativeAgentSettings.DEFAULT_NEW_MODEL_CONTEXT,
            AddedManually = true,
         });
         RebuildRows();
      };
      modelsPanel.Children.Add(btnAddModel);

      var body = new StackPanel { Spacing = 4 };
      body.Children.Add(headerRow);
      body.Children.Add(_BuildAutoTitleRow(provider));
      body.Children.Add(modelsPanel);

      return new Border
      {
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1),
         CornerRadius    = new CornerRadius(4),
         Padding         = new Thickness(8),
         Margin          = new Thickness(0, 0, 0, 8),
         Child           = body,
      };
   }

   // -- auto session titles --------------------------------------------------

   /// <summary>
   /// Provider-level opt-in for naming new sessions from their first prompt.
   /// No model opt-in and no probe: it's an ordinary short completion, so the
   /// only question is whether the user wants to spend one on each session —
   /// which is a per-endpoint question (free locally, billed on a hosted one).
   /// </summary>
   private Control _BuildAutoTitleRow(ProviderInfo provider)
   {
      var chk = new CheckBox
      {
         Content    = "Auto-name new sessions",
         IsChecked  = provider.AutoTitleSessions,
         Foreground = ThemeBrushes.TextPrimary,
         FontSize   = 11,
         VerticalAlignment = VerticalAlignment.Center,
      };
      ToolTip.SetTip(chk,
         "When a session's first prompt is sent, spend one short call on the session's own "
         + "model to name it, instead of listing it by a quote of that prompt. The name is "
         + "editable afterwards, and any session can be named on demand from its right-click "
         + "menu whether or not this is on.");
      chk.IsCheckedChanged += (_, _) => provider.AutoTitleSessions = chk.IsChecked == true;

      return new StackPanel
      {
         Orientation = Orientation.Horizontal,
         Margin      = new Thickness(12, 2, 0, 0),
         Children    = { chk },
      };
   }

   // -- discovery ------------------------------------------------------------

   private async Task _DiscoverProvider(ProviderInfo provider, Button btn, TextBlock status)
   {
      if (string.IsNullOrWhiteSpace(provider.BaseUrl))
      {
         status.Foreground = ThemeBrushes.Accent;
         status.Text       = "set an endpoint first";
         status.IsVisible  = true;
         return;
      }

      btn.IsEnabled    = false;
      status.Foreground = ThemeBrushes.TextMuted;
      status.Text       = "discovering…";
      status.IsVisible  = true;
      try
      {
         using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
         var found = await OpenAiCompatClient.ListModelsAsync(
            provider.BaseUrl,
            string.IsNullOrWhiteSpace(provider.ApiKey) ? null : provider.ApiKey,
            cts.Token);

         if (found.Count == 0)
         {
            status.Text = "no models returned";
            return;
         }

         // Never merge blind: an API provider hosts hundreds of models, of
         // which the user wants a handful. The picker decides what enters the
         // catalog, whether it arrives hidden, and what happens to entries the
         // server stopped offering.
         var picker = new ModelDiscoveryWindow();
         picker.Configure(provider.Name, found, provider.Models);
         await picker.ShowDialog(this);
         if (picker.Result == null)
         {
            status.Text = "cancelled";
            return;
         }

         provider.Models = ModelCatalog.ApplyStale(
            ModelCatalog.Merge(
               provider.Models, picker.Result, NativeAgentSettings.DEFAULT_NEW_MODEL_CONTEXT),
            picker.StaleResult);
         // RebuildRows replaces the controls (including this status label),
         // so the newly picked models simply appear in the refreshed card.
         RebuildRows();
      }
      catch (Exception ex)
      {
         status.Foreground = ThemeBrushes.Accent;
         status.Text       = "failed";
         status.IsVisible  = true;
         ToolTip.SetTip(status, ex.Message);
      }
      finally
      {
         btn.IsEnabled = true;
      }
   }

   // -- model rows -----------------------------------------------------------

   /// <summary>Column header for one provider's model table.</summary>
   private static Control _BuildModelHeader()
   {
      var grid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions(MODEL_COLUMNS),
         // Model rows sit inside a Border with 4px padding; match it so the
         // captions land on the same x as the controls they describe.
         Margin            = new Thickness(8, 0, 8, 3),
      };
      _PlaceHeader(grid, 1, "model");
      _PlaceHeader(grid, 2, "context");
      _PlaceHeader(grid, 3, "reasoning");
      _PlaceHeader(grid, 4, "level");
      return grid;
   }

   private static void _PlaceHeader(Grid grid, int column, string caption, bool center = false)
   {
      var tb = new TextBlock
      {
         Text       = caption,
         FontSize   = 10,
         Foreground = ThemeBrushes.TextMuted,
         Margin     = center ? default : new Thickness(CELL_GAP, 0, 0, 0),
         HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
      };
      Grid.SetColumn(tb, column);
      grid.Children.Add(tb);
   }

   private Control BuildModelRow(ProviderInfo provider, ModelInfo model)
   {
      var tagTb = new TextBlock
      {
         Text              = model.AddedManually ? "manual" : "discovered",
         FontSize          = 10,
         Foreground        = model.AddedManually ? ThemeBrushes.Accent : ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
         Width             = 70,
      };

      var idCtrl = (Control)(model.AddedManually
         ? (Control)new TextBox
         {
            Text               = model.Id,
            FontFamily         = MONO,
            FontSize           = 11,
            Padding            = new Thickness(4, 1),
            MinHeight          = 0,
            Margin             = new Thickness(CELL_GAP, 0, 0, 0),
            Foreground         = ThemeBrushes.TextPrimary,
            Background         = ThemeBrushes.PanelBackground,
            CaretBrush         = ThemeBrushes.TextPrimary,
            BorderBrush        = ThemeBrushes.BorderColor,
            VerticalAlignment  = VerticalAlignment.Center,
            MinWidth           = 240,
         }
         : new TextBlock
         {
            Text              = model.Id,
            FontFamily        = MONO,
            FontSize          = 12,
            Margin            = new Thickness(CELL_GAP, 0, 0, 0),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = ThemeBrushes.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
         });

      if (idCtrl is TextBox tb)
      {
         tb.LostFocus += (_, _) => model.Id = tb.Text ?? "";
      }

      var ctxBox = _BuildContextBox(model);

      // Hide/Show toggle: hidden models stay in the catalog (and survive
      // re-discovery) but are excluded from the chat model picker.
      var hideBtn = _RowButton(model.Hidden ? "Show" : "Hide", 52);
      ToolTip.SetTip(hideBtn,
         "Exclude this model from the chat model picker (it stays in the catalog and survives re-discovery).");
      hideBtn.Click += (_, _) =>
      {
         model.Hidden = !model.Hidden;
         RebuildRows();
      };

      var delBtn = _RowButton("✕", 0);
      ToolTip.SetTip(delBtn, "Remove this model from the catalog.");
      delBtn.Click += (_, _) =>
      {
         provider.Models.Remove(model);
         RebuildRows();
      };

      // Grid (not StackPanel) so the id column stretches and everything else
      // stays anchored to the right edge regardless of name length.
      var row = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions(MODEL_COLUMNS),
         Margin            = new Thickness(4, 1),
      };
      _Place(row, tagTb,  0);
      _Place(row, idCtrl, 1);
      _Place(row, ctxBox, 2);
      _Place(row, hideBtn, 5);
      _Place(row, delBtn,  6);

      // Skipped for hidden models, which never reach the chat picker.
      if (!model.Hidden)
      {
         var (styleBox, levelBox) = _BuildReasoningCells(model);
         _Place(row, styleBox, 3);
         _Place(row, levelBox, 4);
      }

      // Dim the informational controls when hidden; the toggle stays at full
      // opacity so it's clearly actionable.
      double dim = model.Hidden ? 0.45 : 1.0;
      tagTb.Opacity  = dim;
      idCtrl.Opacity = dim;
      ctxBox.Opacity = dim;

      return new Border
      {
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 0, 0, 1),
         Padding         = new Thickness(4, 1),
         Child           = row,
      };
   }

   private static void _Place(Grid grid, Control child, int column)
   {
      Grid.SetColumn(child, column);
      grid.Children.Add(child);
   }

   private static Button _RowButton(string content, double minWidth) => new()
   {
      Content    = content,
      FontSize   = 11,
      Padding    = new Thickness(8, 1),
      MinHeight  = 0,
      MinWidth   = minWidth,
      Margin     = new Thickness(CELL_GAP, 0, 0, 0),
      Foreground = ThemeBrushes.TextPrimary,
      Background = ThemeBrushes.NeutralButtonBackground,
      VerticalAlignment = VerticalAlignment.Center,
   };

   // Declared reasoning capability plus the level new sessions start at, as two
   // cells of the model row. The style cannot be detected — the only
   // machine-readable source is the chat template, which requires the model to
   // be loaded first — so it is the user's statement, and a wrong one fails
   // requests outright rather than degrading.
   //
   // The level list is rebuilt from the chosen style rather than showing the
   // full union, so the editor can only produce a combination that exists.
   private static (Control Style, Control Level) _BuildReasoningCells(ModelInfo model)
   {
      var styleBox = _ChoiceBox(s_reasoningStyleDescriptor,
         "Which reasoning controls this model's chat template understands.");
      var levelBox = _ChoiceBox(s_reasoningLevelDescriptor,
         "Level a new session starts at.");

      // Guards the item rebuild below: repopulating raises SelectionChanged
      // with no item, which would otherwise write a garbage level back.
      bool loading = false;

      void SyncLevels()
      {
         loading = true;

         var spec  = ReasoningSpec.For(model.ReasoningStyle);
         var items = new List<ComboBoxItem>();
         foreach (var c in s_reasoningLevelDescriptor?.Choices ?? _NoChoices)
         {
            if (c.Value is not ReasoningLevel lvl)
               continue;
            // "Model default" is always offered; the rest only where the
            // declared style actually implements them.
            if (lvl != ReasoningLevel.Default && !_Lists(spec, lvl))
               continue;
            items.Add(new ComboBoxItem { Content = c.Label, Tag = lvl, FontSize = 11 });
         }

         levelBox.ItemsSource = items;
         levelBox.IsEnabled   = spec.HasControl;
         levelBox.Opacity     = spec.HasControl ? 1.0 : 0.45;

         // A style change can strip the level that was selected; fold it onto
         // one this style has rather than leave a stale value persisted.
         int sel = -1;
         for (int i = 0; i < items.Count; i++)
         {
            if (Equals(items[i].Tag, model.DefaultReasoning)) { sel = i; break; }
         }
         if (sel < 0)
         {
            model.DefaultReasoning = ReasoningLevel.Default;
            sel = items.Count > 0 ? 0 : -1;
         }
         levelBox.SelectedIndex = sel;

         loading = false;
      }

      var styleItems = new List<ComboBoxItem>();
      foreach (var c in s_reasoningStyleDescriptor?.Choices ?? _NoChoices)
         styleItems.Add(new ComboBoxItem { Content = c.Label, Tag = c.Value, FontSize = 11 });
      styleBox.ItemsSource = styleItems;
      for (int i = 0; i < styleItems.Count; i++)
      {
         if (Equals(styleItems[i].Tag, model.ReasoningStyle)) { styleBox.SelectedIndex = i; break; }
      }
      SyncLevels();

      styleBox.SelectionChanged += (_, _) =>
      {
         if (styleBox.SelectedItem is ComboBoxItem it && it.Tag is ReasoningStyle st)
         {
            model.ReasoningStyle = st;
            SyncLevels();
         }
      };
      levelBox.SelectionChanged += (_, _) =>
      {
         if (loading) return;
         if (levelBox.SelectedItem is ComboBoxItem it && it.Tag is ReasoningLevel lvl)
            model.DefaultReasoning = lvl;
      };

      return (styleBox, levelBox);
   }

   private static readonly IReadOnlyList<SettingChoice> _NoChoices = Array.Empty<SettingChoice>();

   private static bool _Lists(ReasoningSpec spec, ReasoningLevel level)
   {
      foreach (var c in spec.Choices)
      {
         if (c == level)
            return true;
      }
      return false;
   }

   private static ComboBox _ChoiceBox(SettingDescriptor? desc, string fallbackHelp)
   {
      var box = new ComboBox
      {
         FontSize            = 11,
         MinHeight           = 0,
         Padding             = new Thickness(6, 1),
         Margin              = new Thickness(CELL_GAP, 0, 0, 0),
         HorizontalAlignment = HorizontalAlignment.Stretch,
         VerticalAlignment   = VerticalAlignment.Center,
      };
      ToolTip.SetTip(box, desc?.Help ?? fallbackHelp);
      return box;
   }

   /// <summary>
   /// Context-window picker, driven by the [Setting] metadata on
   /// <see cref="ModelInfo.ContextWindow"/> so the catalog and the generic
   /// settings panel stay in sync.
   /// </summary>
   private static Control _BuildContextBox(ModelInfo model)
   {
      var desc = s_contextDescriptor;
      return _CompactBox(model.ContextWindow, desc, 1024, v => model.ContextWindow = v);
   }

   /// <summary>One compact numeric cell built from its [Setting] descriptor.</summary>
   private static NumericSuggestionBox _CompactBox(
      int current, SettingDescriptor? desc, int fallbackIncrement, Action<int> write)
   {
      int increment = (desc != null && desc.Increment > 0) ? desc.Increment : fallbackIncrement;
      var box = new NumericSuggestionBox(
         current,
         increment,
         desc?.Suggestions,
         desc?.Min,
         desc?.Max,
         compact: true)
      {
         Margin            = new Thickness(CELL_GAP, 0, 0, 0),
         VerticalAlignment = VerticalAlignment.Center,
      };
      box.ValueChanged += v => write(v);
      if (desc?.Help != null) ToolTip.SetTip(box, desc.Help);
      return box;
   }

   // -- small layout helpers -------------------------------------------------

   private static TextBox _Field(string text, double minWidth, string watermark)
   {
      var box = new TextBox
      {
         Text              = text,
         PlaceholderText   = watermark,
         Foreground        = ThemeBrushes.TextPrimary,
         Background        = ThemeBrushes.PanelBackground,
         CaretBrush        = ThemeBrushes.TextPrimary,
         BorderBrush       = ThemeBrushes.BorderColor,
         VerticalAlignment = VerticalAlignment.Center,
      };
      if (minWidth > 0) box.Width = minWidth;
      return box;
   }

   private static TextBlock _Caption(string text) => new()
   {
      Text       = text,
      FontSize   = 10,
      Foreground = ThemeBrushes.TextMuted,
   };

   private static Control _Labeled(string caption, Control field)
   {
      var panel = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 8, 0) };
      panel.Children.Add(_Caption(caption));
      panel.Children.Add(field);
      return panel;
   }

   // -- footer actions -------------------------------------------------------

   private void BtnAdd_Click(object? sender, RoutedEventArgs e)
   {
      _working.Add(new ProviderInfo
      {
         Name    = "provider-" + (_working.Count + 1),
         BaseUrl = "http://localhost:8080",
         ApiKey  = "",
         Models  = new List<ModelInfo>(),
      });
      RebuildRows();
   }

   private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();

   private void BtnSave_Click(object? sender, RoutedEventArgs e)
   {
      if (_settings == null) { Close(); return; }

      // Drop providers with a blank name and models with a blank id (added
      // then left unfilled).
      var clean = new List<ProviderInfo>();
      foreach (var p in _working)
      {
         if (string.IsNullOrWhiteSpace(p.Name)) continue;
         p.Name = p.Name.Trim();
         var models = new List<ModelInfo>();
         foreach (var m in p.Models)
         {
            if (!string.IsNullOrWhiteSpace(m.Id))
               models.Add(m);
         }
         p.Models = models;
         clean.Add(p);
      }
      _settings.Providers = clean;

      try { _onSaved?.Invoke(); } catch { }
      Close();
   }

   // -- fields ---------------------------------------------------------------

   // 0 tag | 1 id | 2 context | 3 reasoning | 4 level | 5 hide | 6 delete.
   //
   // Every row is its own Grid, so Auto columns would size per-row and the
   // header — whose cells hold short captions rather than controls — would
   // drift out of line with the rows beneath it. Fixed widths keep the whole
   // table on the same grid.
   private const string MODEL_COLUMNS = "70,*,106,132,96,60,36";

   /// <summary>Left gap on every cell past the tag column.</summary>
   private const double CELL_GAP = 6;

   private static readonly FontFamily MONO =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private static readonly SettingDescriptor? s_contextDescriptor =
      _FindDescriptor(nameof(ModelInfo.ContextWindow));

   private static readonly SettingDescriptor? s_reasoningStyleDescriptor =
      _FindDescriptor(nameof(ModelInfo.ReasoningStyle));

   private static readonly SettingDescriptor? s_reasoningLevelDescriptor =
      _FindDescriptor(nameof(ModelInfo.DefaultReasoning));

   private static SettingDescriptor? _FindDescriptor(string propertyName)
   {
      foreach (var d in SettingsReflector.For(typeof(ModelInfo)))
      {
         if (d.Property.Name == propertyName)
            return d;
      }
      return null;
   }

   private NativeAgentSettings? _settings;
   private List<ProviderInfo>   _working = new();
   private Action?              _onSaved;
}
