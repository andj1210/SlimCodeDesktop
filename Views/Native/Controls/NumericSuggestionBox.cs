// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native.Controls;

/// <summary>
/// Integer input combining a text field with a set of quick-pick suggestions.
/// Free typing + stepping + suggested values, all in one widget.
///
/// Two renderings:
/// <list type="bullet">
/// <item>tall (default) -- a NumericUpDown with the suggestions as a chip row
/// underneath. For vertical settings forms, where height is cheap.</item>
/// <item>compact -- a single-line TextBox; suggestions move into a flyout on a
/// caret inside the field, and Up/Down step by the increment. For table rows,
/// where height is the scarce axis.</item>
/// </list>
///
/// Compact deliberately does not use NumericUpDown: token counts want "k"
/// notation in both directions, and NumericUpDown's own text/value syncing
/// fights any attempt to impose it.
/// </summary>
public sealed class NumericSuggestionBox : StackPanel
{
   // -- constants ----------------------------------------------------------

   private const double COMPACT_WIDTH = 100;
   private const double TALL_WIDTH    = 160;

   // -- fields -------------------------------------------------------------

   private readonly NumericUpDown?     _nud;       // tall rendering
   private readonly TextBox?           _text;      // compact rendering
   private readonly StackPanel?        _chipRow;   // tall rendering
   private readonly IReadOnlyList<int> _suggestions;
   private readonly int                _increment;
   private readonly int?               _min;
   private readonly int?               _max;
   private readonly bool               _kilo;

   private int  _value;
   private bool _suppressEvent;

   // -- public surface -----------------------------------------------------

   /// <summary>Fires when the user changes the value (suggestion, step, or typing).</summary>
   public event Action<int>? ValueChanged;

   public int Value
   {
      get => _value;
      set
      {
         if (_value == value) return;
         _value = value;
         _Render();
         _RefreshChips();
      }
   }

   public NumericSuggestionBox(int initial, int increment, IReadOnlyList<int>? suggestions, int? min, int? max)
      : this(initial, increment, suggestions, min, max, false)
   {
   }

   public NumericSuggestionBox(int initial, int increment, IReadOnlyList<int>? suggestions,
                               int? min, int? max, bool compact)
   {
      _value       = initial;
      _suggestions = suggestions ?? Array.Empty<int>();
      _increment   = increment > 0 ? increment : 1;
      _min         = min;
      _max         = max;
      _kilo        = _UsesKiloNotation(_increment, _suggestions);

      Orientation = Orientation.Vertical;
      Spacing     = 4;

      if (compact)
      {
         _text = _BuildCompactField();
         Children.Add(_text);
         return;
      }

      _nud = new NumericUpDown
      {
         Value               = initial,
         Increment           = _increment,
         Minimum             = _min ?? 0,
         Maximum             = _max ?? 1_000_000,
         FormatString        = "0",
         Width               = TALL_WIDTH,
         HorizontalAlignment = HorizontalAlignment.Left,
         Foreground          = ThemeBrushes.TextPrimary,
         Background          = ThemeBrushes.PanelBackground,
      };
      _nud.ValueChanged += (_, _) =>
      {
         if (_suppressEvent) return;
         if (_nud.Value is decimal d)
            _Commit((int)d);
      };

      _chipRow = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         Spacing     = 4,
      };
      Children.Add(_nud);
      if (_suggestions.Count > 0)
         Children.Add(_chipRow);
      _RefreshChips();
   }

   // -- compact rendering --------------------------------------------------

   private TextBox _BuildCompactField()
   {
      var box = new TextBox
      {
         Text              = _Format(_value),
         Width             = COMPACT_WIDTH,
         FontSize          = 11,
         Padding           = new Thickness(4, 1),
         MinHeight         = 0,
         Foreground        = ThemeBrushes.TextPrimary,
         Background        = ThemeBrushes.PanelBackground,
         CaretBrush        = ThemeBrushes.TextPrimary,
         BorderBrush       = ThemeBrushes.BorderColor,
         VerticalAlignment = VerticalAlignment.Center,
      };
      if (_suggestions.Count > 0)
         box.InnerRightContent = _BuildPresetButton();

      // Commit on leaving the field or pressing Enter -- never per keystroke,
      // which would reinterpret half-typed numbers.
      box.LostFocus += (_, _) => _CommitText();
      box.KeyDown   += _CompactField_KeyDown;
      return box;
   }

   /// <summary>Caret inside the field opening the suggestion list (compact only).</summary>
   private Button _BuildPresetButton()
   {
      var menu = new MenuFlyout();
      foreach (var v in _suggestions)
      {
         int captured = v;
         var item = new MenuItem { Header = _Format(captured) };
         item.Click += (_, _) => _Commit(captured);
         menu.Items.Add(item);
      }

      if (_kilo)
      {
         // Explicit form of what typing a bare "256" (or "256k") already does,
         // for when the current value came from somewhere else.
         menu.Items.Add(new Separator());
         var multiply = new MenuItem { Header = "× 1024" };
         multiply.Click += (_, _) => _Commit(_Clamp((long)_value * 1024));
         menu.Items.Add(multiply);
      }

      var btn = new Button
      {
         Content           = "▾",
         FontSize          = 9,
         Padding           = new Thickness(4, 0),
         MinWidth          = 0,
         Focusable         = false,
         Background        = Brushes.Transparent,
         BorderThickness   = new Thickness(0),
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
         Flyout            = menu,
      };
      ToolTip.SetTip(btn, _kilo
         ? "Common values. Up/Down steps by " + _increment + "; typing \"256k\" means 256 × 1024."
         : "Common values. Up/Down steps by " + _increment + ".");
      return btn;
   }

   private void _CompactField_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key == Key.Up || e.Key == Key.Down)
      {
         int from = _Parse(_text?.Text);
         _Commit(_Clamp((long)from + (e.Key == Key.Up ? _increment : -_increment)));
         _Render();
         e.Handled = true;
      }
      else if (e.Key == Key.Enter)
      {
         _CommitText();
         e.Handled = true;
      }
   }

   private void _CommitText()
   {
      _Commit(_Parse(_text?.Text));
      _Render();      // normalise what was typed: "256" -> "256k"
   }

   // -- private methods ----------------------------------------------------

   /// <summary>
   /// True when this setting is a token count, and so should render and accept
   /// "k" notation. Deliberately conservative: a setting that is not measured
   /// in 1024s (a retry count, a port) keeps plain decimal formatting.
   /// </summary>
   private static bool _UsesKiloNotation(int increment, IReadOnlyList<int> suggestions)
   {
      if (increment > 0 && increment % 1024 == 0)
         return true;
      if (suggestions.Count == 0)
         return false;
      foreach (var v in suggestions)
      {
         if (v != 0 && v % 1024 != 0)
            return false;
      }
      return true;
   }

   /// <summary>
   /// Parses user input, falling back to the current value on anything
   /// unreadable. A trailing "k" multiplies by 1024.
   ///
   /// A bare number below the minimum is additionally read as a k-value when
   /// that lands it back inside the valid range: typing "256" into a context
   /// window whose minimum is 1024 can only have meant 256k, since 256 would be
   /// clamped away anyway. The rule therefore never reinterprets a number the
   /// user could validly have meant -- and a setting with a minimum of 0 (the
   /// thinking budget) keeps bare numbers literal, preserving 0 = "six times
   /// the threshold".
   /// </summary>
   private int _Parse(string? raw)
   {
      var text = raw?.Trim();
      if (string.IsNullOrEmpty(text))
         return _value;

      bool explicitKilo = text.EndsWith("k", StringComparison.OrdinalIgnoreCase);
      if (explicitKilo)
         text = text.Substring(0, text.Length - 1).TrimEnd();

      if (!int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands,
                        CultureInfo.CurrentCulture, out int n))
         return _value;

      long result = explicitKilo ? (long)n * 1024 : n;

      if (!explicitKilo && _kilo && _min is int min && result < min)
      {
         long asKilo = (long)n * 1024;
         if (asKilo >= min && (_max is not int max || asKilo <= max))
            result = asKilo;
      }
      return _Clamp(result);
   }

   private int _Clamp(long v)
   {
      if (v > int.MaxValue) v = int.MaxValue;
      if (_max is int max && v > max) v = max;
      if (_min is int min && v < min) v = min;
      if (v < 0) v = 0;
      return (int)v;
   }

   private void _Commit(int v)
   {
      if (_value == v) return;
      _value = v;
      _Render();
      _RefreshChips();
      ValueChanged?.Invoke(v);
   }

   /// <summary>Pushes <see cref="_value"/> back into whichever control is shown.</summary>
   private void _Render()
   {
      _suppressEvent = true;
      if (_text != null) _text.Text  = _Format(_value);
      if (_nud  != null) _nud.Value  = _value;
      _suppressEvent = false;
   }

   private void _RefreshChips()
   {
      if (_chipRow == null) return;      // compact rendering has no chips
      _chipRow.Children.Clear();
      foreach (var v in _suggestions)
         _chipRow.Children.Add(_BuildChip(v));
   }

   private Button _BuildChip(int v)
   {
      bool active = (v == _value);
      var btn = new Button
      {
         Content    = _Format(v),
         Padding    = new Thickness(8, 2),
         MinWidth   = 40,
         FontSize   = 11,
         Foreground = active ? ThemeBrushes.TextOnAccent : ThemeBrushes.TextPrimary,
         Background = active ? ThemeBrushes.AccentDark   : ThemeBrushes.NeutralButtonBackground,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1),
      };
      btn.Click += (_, _) => _Commit(v);
      return btn;
   }

   private string _Format(int v)
   {
      if (_kilo && v >= 1024 && v % 1024 == 0)
         return (v / 1024) + "k";
      return v.ToString();
   }
}
