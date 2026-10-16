// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace SlimCodeDesktop.Markdown;

/// <summary>
/// Thin UserControl wrapper around <see cref="AvaloniaMarkdownRenderer"/>.
/// Bind or set <see cref="Markdown"/>; the visual tree refreshes on change.
///
/// Re-parse on every property change is fine for one-shot render. For
/// streaming, the host should debounce the update at the source (e.g. by
/// only assigning the property at paragraph boundaries) rather than
/// per-token, since a full reparse + control tree rebuild on every
/// character would be wasteful even for small documents.
/// </summary>
public partial class MarkdownView : UserControl
{
   public static readonly StyledProperty<string?> MarkdownProperty =
      AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

   public string? Markdown
   {
      get => GetValue(MarkdownProperty);
      set => SetValue(MarkdownProperty, value);
   }

   public static readonly StyledProperty<Avalonia.Media.IBrush?> TextForegroundProperty =
      AvaloniaProperty.Register<MarkdownView, Avalonia.Media.IBrush?>(nameof(TextForeground));

   /// <summary>
   /// Foreground brush applied to body text rendered by the inner
   /// <see cref="AvaloniaMarkdownRenderer"/>. Needed because Avalonia
   /// Fluent's implicit TextBlock style sets an explicit Foreground that
   /// beats normal property inheritance.
   /// </summary>
   public Avalonia.Media.IBrush? TextForeground
   {
      get => GetValue(TextForegroundProperty);
      set => SetValue(TextForegroundProperty, value);
   }

   private readonly AvaloniaMarkdownRenderer _renderer = new();
   private ContentPresenter? _host;

   public MarkdownView()
   {
      InitializeComponent();
      _host = this.FindControl<ContentPresenter>("Host");
   }

   protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   {
      base.OnPropertyChanged(change);
      if (change.Property == MarkdownProperty || change.Property == TextForegroundProperty)
         Refresh();
   }

   private void Refresh()
   {
      if (_host == null) return;
      _renderer.TextForeground = TextForeground;
      _host.Content = _renderer.Render(Markdown);
   }
}
