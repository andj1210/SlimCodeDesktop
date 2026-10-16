// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Post-discovery picker: shows what a provider's /v1/models list returned and
/// lets the user reconcile it with the catalog, in both directions.
///
/// Merging everything is only reasonable for a local provider whose model list
/// the user curated themselves; an API provider hosts hundreds of models, of
/// which two or three are wanted. Each new row is a three-way choice — skip,
/// add, or add hidden (in the catalog, out of the chat picker) — defaulting to
/// skip.
///
/// The mirror case is a catalog entry the server no longer offers: those get
/// their own table with keep / hide / delete, defaulting to keep, so a
/// temporarily unloaded model is never lost by accident.
///
/// <see cref="Result"/> and <see cref="StaleResult"/> carry the picks; Result
/// is null when the dialog was cancelled. Nothing is written to the provider
/// from here.
/// </summary>
public partial class ModelDiscoveryWindow : Window
{
   public ModelDiscoveryWindow()
   {
      InitializeComponent();

      // The build funcs take the item as a hint only -- both templates drive
      // their controls off DataContext, so the panel is free to call them with
      // a null item or to re-point an element at a different row.
      ListNew.ItemTemplate =
         new FuncDataTemplate<Row>((row, _) => _BuildRow(row), supportsRecycling: false);
      ListStale.ItemTemplate =
         new FuncDataTemplate<Row>((row, _) => _BuildRow(row), supportsRecycling: false);
      ListKnown.ItemTemplate =
         new FuncDataTemplate<string>((id, _) => _BuildKnownRow(id), supportsRecycling: false);

      HeaderHost.Child = _BuildHeader(
         ("skip",   "Do not add this model."),
         ("add",    "Add to the catalog and show it in the chat model picker."),
         ("hidden", "Add to the catalog but keep it out of the chat model picker."));
      StaleHeaderHost.Child = _BuildHeader(
         ("keep",   "Leave this entry alone — the model may just be unloaded right now."),
         ("hide",   "Keep it in the catalog but take it out of the chat model picker."),
         ("delete", "Remove this entry from the catalog."));

      TxtFilter.TextChanged += (_, _) => _ApplyFilter();
      BtnAll.Click  += (_, _) => _SetFiltered(PickAction.Add);
      BtnNone.Click += (_, _) => _SetFiltered(PickAction.Skip);
      ToolTip.SetTip(BtnAll,  "Set every new model matching the current filter to \"add\".");
      ToolTip.SetTip(BtnNone, "Set every new model matching the current filter back to \"skip\".");

      Opened += (_, _) => TxtFilter.Focus();
   }

   /// <summary>Models the user approved, or null when the dialog was cancelled.</summary>
   public List<ModelPick>? Result { get; private set; }

   /// <summary>
   /// Verdicts on catalog entries the server no longer offers. Only entries the
   /// user moved off "keep" are listed; empty when the dialog was cancelled.
   /// </summary>
   public List<StaleModelPick> StaleResult { get; } = new();

   /// <summary>
   /// Populate from one discovery result. Ids already present in
   /// <paramref name="existing"/> are listed separately and cannot be picked —
   /// re-adding them would clobber the context window the user set. Existing
   /// ids that discovery did *not* return land in the "gone from the server"
   /// table instead.
   /// </summary>
   public void Configure(
      string providerName,
      IReadOnlyList<string> discovered,
      IReadOnlyList<ModelInfo>? existing)
   {
      var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (existing != null)
      {
         foreach (var m in existing)
         {
            if (!string.IsNullOrWhiteSpace(m.Id)) known.Add(m.Id);
         }
      }

      _rows.Clear();
      _stale.Clear();
      _known.Clear();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var id in discovered)
      {
         if (string.IsNullOrWhiteSpace(id)) continue;
         if (!seen.Add(id)) continue;
         if (known.Contains(id))
            _known.Add(id);
         else
            _rows.Add(new Row { Id = id });
      }

      if (existing != null)
      {
         foreach (var m in existing)
         {
            if (string.IsNullOrWhiteSpace(m.Id)) continue;
            if (seen.Contains(m.Id)) continue;
            _stale.Add(new Row
            {
               Id = m.Id,
               // A hand-added entry was never expected to show up in discovery,
               // so say so rather than let it look like the server dropped it.
               Note = m.AddedManually ? "   (added manually)"
                    : m.Hidden        ? "   (hidden)"
                    : "",
            });
         }
      }

      _rows.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
      _stale.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
      _known.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

      string name = string.IsNullOrWhiteSpace(providerName) ? "This provider" : "\"" + providerName + "\"";
      TxtIntro.Text =
         name + " returned " + seen.Count + " model" + (seen.Count == 1 ? "" : "s") + ". "
         + "Pick what goes into the catalog: \"add\" makes a model available in the chat model picker, "
         + "\"hidden\" keeps it in the catalog but out of the picker. Anything left on \"skip\" is not added."
         + (_stale.Count > 0
               ? " Entries the server did not report are listed below — they stay as they are unless you say otherwise."
               : "");

      _ApplyFilter();
   }

   // -- filtering ------------------------------------------------------------

   private void _ApplyFilter()
   {
      var terms = _Terms(TxtFilter.Text);

      // A fresh list every time: assigning the same instance back would not
      // re-run the item template, so radio state edited by Select all / Clear
      // would not show up.
      var shown = new List<Row>();
      foreach (var r in _rows)
      {
         if (_Matches(r.Id, terms)) shown.Add(r);
      }
      // Clear first: swapping straight to a shorter list leaves the panel
      // holding realized indices the new list does not have.
      ListNew.ItemsSource = null;
      ListNew.ItemsSource = shown;

      TxtEmpty.IsVisible = shown.Count == 0;
      TxtEmpty.Text = _rows.Count == 0
         ? "(nothing new — every model this provider offers is already in the catalog)"
         : "(no model matches the filter)";

      var staleShown = new List<Row>();
      foreach (var r in _stale)
      {
         if (_Matches(r.Id, terms)) staleShown.Add(r);
      }
      ListStale.ItemsSource  = null;
      ListStale.ItemsSource  = staleShown;
      StaleSection.IsVisible = _stale.Count > 0;
      TxtStale.Text = terms.Length == 0
         ? "Gone from the server (" + _stale.Count + ") — in the catalog, but not in this discovery"
         : "Gone from the server (" + staleShown.Count + " of " + _stale.Count + " match)";

      var knownShown = new List<string>();
      foreach (var k in _known)
      {
         if (_Matches(k, terms)) knownShown.Add(k);
      }
      ListKnown.ItemsSource   = null;
      ListKnown.ItemsSource   = knownShown;
      KnownSection.IsVisible  = _known.Count > 0;
      TxtKnown.Text = terms.Length == 0
         ? "Already in the catalog (" + _known.Count + ") — edit these in the provider card"
         : "Already in the catalog (" + knownShown.Count + " of " + _known.Count + " match)";

      _UpdateCounts();
   }

   /// <summary>Apply one choice to every new model the filter currently shows.</summary>
   private void _SetFiltered(PickAction action)
   {
      var terms = _Terms(TxtFilter.Text);
      foreach (var r in _rows)
      {
         if (_Matches(r.Id, terms)) r.Choice = (int)action;
      }
      _ApplyFilter();
   }

   private static string[] _Terms(string? filter) =>
      string.IsNullOrWhiteSpace(filter)
         ? Array.Empty<string>()
         : filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

   /// <summary>All terms must appear somewhere in the id (case-insensitive).</summary>
   private static bool _Matches(string id, string[] terms)
   {
      foreach (var t in terms)
      {
         if (id.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
      }
      return true;
   }

   private void _UpdateCounts()
   {
      int add = 0, hidden = 0;
      foreach (var r in _rows)
      {
         if (r.Choice == (int)PickAction.Add)            add++;
         else if (r.Choice == (int)PickAction.AddHidden) hidden++;
      }

      int stale = 0;
      foreach (var r in _stale)
      {
         if (r.Choice != (int)StaleAction.Keep) stale++;
      }

      string text = _rows.Count + " new · " + add + " to add · " + hidden + " hidden";
      if (_stale.Count > 0) text += "   |   " + _stale.Count + " gone · " + stale + " to change";
      TxtCounts.Text = text;

      BtnApply.IsEnabled = add + hidden + stale > 0;
   }

   // -- rows -----------------------------------------------------------------

   /// <summary>
   /// Column captions for the pick radios. The radios themselves carry no
   /// label — with a few hundred rows the repeated text is pure noise — so the
   /// header is what names them, and fixed column widths keep it aligned.
   ///
   /// The header also carries every tooltip in the table. It is built once and
   /// lives outside the virtualized list: a tip attached to a row would be
   /// registered and torn down on each scroll, and a pending tip on a row that
   /// gets recycled out from under the pointer takes the app down.
   /// </summary>
   private static Control _BuildHeader(
      (string Caption, string Tip) first,
      (string Caption, string Tip) second,
      (string Caption, string Tip) third)
   {
      var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(COLUMNS) };
      _Place(grid, new TextBlock
      {
         Text       = "model",
         FontSize   = 10,
         Foreground = ThemeBrushes.TextMuted,
         Margin     = new Thickness(CELL_GAP, 0, 0, 0),
      }, 0);
      _Place(grid, _HeaderCaption(first.Caption,  first.Tip),  1);
      _Place(grid, _HeaderCaption(second.Caption, second.Tip), 2);
      _Place(grid, _HeaderCaption(third.Caption,  third.Tip),  3);
      return grid;
   }

   private static TextBlock _HeaderCaption(string text, string tip)
   {
      var tb = new TextBlock
      {
         Text                = text,
         FontSize            = 10,
         Foreground          = ThemeBrushes.TextMuted,
         HorizontalAlignment = HorizontalAlignment.Center,
      };
      ToolTip.SetTip(tb, tip);
      return tb;
   }

   /// <summary>
   /// One picker row, used by both tables — the three radios are positional, so
   /// the same builder serves skip/add/hidden and keep/hide/delete alike (see
   /// <see cref="PickAction"/> / <see cref="StaleAction"/>).
   ///
   /// The virtualizing panel calls the item template with a null item (and may
   /// later point the element at a different row), so the controls must not
   /// capture the Row handed in at build time -- they read it from DataContext,
   /// which the item container sets and updates. <paramref name="initial"/> is
   /// only a fallback for the case where DataContext never arrives.
   /// </summary>
   private Control _BuildRow(Row? initial)
   {
      var idTb = new TextBlock
      {
         FontFamily        = MONO,
         FontSize          = 12,
         Margin            = new Thickness(CELL_GAP, 0, CELL_GAP, 0),
         TextTrimming      = TextTrimming.CharacterEllipsis,
         Foreground        = ThemeBrushes.TextPrimary,
         VerticalAlignment = VerticalAlignment.Center,
      };

      // Nothing in a row carries a tooltip -- see _BuildHeader. No GroupName
      // either: an un-named radio groups by visual parent, and all three share
      // this row's grid.
      var rbFirst  = _Radio();
      var rbSecond = _Radio();
      var rbThird  = _Radio();

      var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(COLUMNS) };
      _Place(grid, idTb,     0);
      _Place(grid, rbFirst,  1);
      _Place(grid, rbSecond, 2);
      _Place(grid, rbThird,  3);

      var border = new Border
      {
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(0, 0, 0, 1),
         Padding         = new Thickness(4, 2),
         Child           = grid,
      };

      Row? bound   = null;
      bool binding = false;

      // Pull the row's state into the controls. Guarded, because checking one
      // radio makes the group uncheck the others, which re-enters the handlers.
      void Show()
      {
         binding = true;
         bound   = border.DataContext as Row ?? initial;
         idTb.Text         = bound == null ? "" : bound.Id + bound.Note;
         rbFirst.IsChecked  = bound == null || bound.Choice == 0;
         rbSecond.IsChecked = bound != null && bound.Choice == 1;
         rbThird.IsChecked  = bound != null && bound.Choice == 2;
         binding = false;
      }

      void Pick(int choice)
      {
         if (binding || bound == null) return;
         bound.Choice = choice;
         _UpdateCounts();
      }

      rbFirst.IsCheckedChanged  += (_, _) => { if (rbFirst.IsChecked  == true) Pick(0); };
      rbSecond.IsCheckedChanged += (_, _) => { if (rbSecond.IsChecked == true) Pick(1); };
      rbThird.IsCheckedChanged  += (_, _) => { if (rbThird.IsChecked  == true) Pick(2); };

      border.DataContextChanged += (_, _) => Show();
      Show();

      return border;
   }

   private static RadioButton _Radio() => new()
   {
      MinWidth            = 0,
      Padding             = default,
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment   = VerticalAlignment.Center,
   };

   /// <summary>Read-only row for the "already in the catalog" list. Same null
   /// caveat as <see cref="_BuildRow"/> -- the id comes from DataContext.</summary>
   private static Control _BuildKnownRow(string? initial)
   {
      var tb = new TextBlock
      {
         FontFamily   = MONO,
         FontSize     = 11,
         Opacity      = 0.55,
         Margin       = new Thickness(4 + CELL_GAP, 1, CELL_GAP, 1),
         TextTrimming = TextTrimming.CharacterEllipsis,
         Foreground   = ThemeBrushes.TextPrimary,
      };
      tb.DataContextChanged += (_, _) => tb.Text = tb.DataContext as string ?? initial ?? "";
      tb.Text = initial ?? "";
      return tb;
   }

   private static void _Place(Grid grid, Control child, int column)
   {
      Grid.SetColumn(child, column);
      grid.Children.Add(child);
   }

   // -- footer actions -------------------------------------------------------

   private void BtnApply_Click(object? sender, RoutedEventArgs e)
   {
      var picks = new List<ModelPick>();
      foreach (var r in _rows)
      {
         if (r.Choice == (int)PickAction.Skip) continue;
         picks.Add(new ModelPick { Id = r.Id, Hidden = r.Choice == (int)PickAction.AddHidden });
      }
      Result = picks;

      StaleResult.Clear();
      foreach (var r in _stale)
      {
         if (r.Choice == (int)StaleAction.Keep) continue;
         StaleResult.Add(new StaleModelPick
         {
            Id     = r.Id,
            Action = r.Choice == (int)StaleAction.Delete
                        ? StaleModelAction.Delete
                        : StaleModelAction.Hide,
         });
      }
      Close();
   }

   private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();

   protected override void OnKeyDown(KeyEventArgs e)
   {
      if (e.Key == Key.Escape)
      {
         Close();
         e.Handled = true;
         return;
      }
      if (e.Key == Key.Enter && BtnApply.IsEnabled)
      {
         BtnApply_Click(this, new RoutedEventArgs());
         e.Handled = true;
         return;
      }
      base.OnKeyDown(e);
   }

   // -- fields ---------------------------------------------------------------

   // Both enums are column indices into a row's three radios, which is what
   // Row.Choice stores -- one row builder serves both tables that way.
   private enum PickAction  { Skip = 0, Add  = 1, AddHidden = 2 }
   private enum StaleAction { Keep = 0, Hide = 1, Delete    = 2 }

   private sealed class Row
   {
      public string Id     = "";
      public string Note   = "";
      public int    Choice;
   }

   // model | radio | radio | radio. Fixed widths for the three choice columns
   // so the per-row grids line up with the caption header above them.
   private const string COLUMNS  = "*,64,64,74";
   private const double CELL_GAP = 6;

   private static readonly FontFamily MONO =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private readonly List<Row>    _rows  = new();
   private readonly List<Row>    _stale = new();
   private readonly List<string> _known = new();
}
