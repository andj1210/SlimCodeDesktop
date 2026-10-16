// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// Disambiguate the two Inline hierarchies: Markdig's AST node vs Avalonia's
// rendered inline run. Aliases keep the rest of the file readable.
using AvInline = Avalonia.Controls.Documents.Inline;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace SlimCodeDesktop.Markdown;

/// <summary>
/// Renders a Markdig <see cref="MarkdownDocument"/> to an Avalonia control
/// tree. Output is a <see cref="StackPanel"/> of per-block controls; text
/// uses <see cref="SelectableTextBlock"/> so the user can copy.
///
/// Supported: headings, paragraphs, fenced + indented code, quotes,
/// ordered/unordered/nested lists, GFM pipe tables, thematic breaks,
/// inline bold/italic/code/links/line breaks.
///
/// Not supported (rare in LLM output): footnotes, definition lists,
/// math, custom containers. These render as their literal text.
///
/// Theming reads <c>{DynamicResource ...}</c> brushes at render time —
/// switching theme requires re-rendering.
/// </summary>
public sealed class AvaloniaMarkdownRenderer
{
   public AvaloniaMarkdownRenderer()
   {
      _pipeline = new MarkdownPipelineBuilder()
         .UsePipeTables()
         .UseEmphasisExtras()
         .UseAutoLinks()
         .UseTaskLists()
         // Recognize $...$ inline math and $$...$$ block math so the
         // wrapper dollar-signs disappear and we can substitute the
         // common LaTeX commands (\to, \le, \alpha, …) with their
         // Unicode equivalents in RenderInline.
         .UseMathematics()
         .Build();
   }

   /// <summary>
   /// Optional override for the foreground brush applied to body text
   /// (paragraphs, headings, list items). Setting this beats Avalonia
   /// Fluent's implicit TextBlock style, which would otherwise paint
   /// over plain property inheritance from the host. Code blocks and
   /// quotes keep their own theme brushes for readability.
   /// </summary>
   public IBrush? TextForeground { get; set; }

   /// <summary>Parse and render in one step. Returns an empty panel for null/empty.</summary>
   public Control Render(string? markdown)
   {
      if (string.IsNullOrEmpty(markdown))
         return new StackPanel();
      var doc = Markdig.Markdown.Parse(markdown, _pipeline);
      return Render(doc);
   }

   public Control Render(MarkdownDocument doc)
   {
      var panel = new StackPanel { Spacing = 6 };
      foreach (var block in doc)
      {
         var c = RenderBlock(block);
         if (c != null) panel.Children.Add(c);
      }
      return panel;
   }

   // -- block dispatch -------------------------------------------------------

   private Control? RenderBlock(Block block) => block switch
   {
      HeadingBlock h        => RenderHeading(h),
      ParagraphBlock p      => RenderParagraph(p),
      // MathBlock inherits from FencedCodeBlock in Markdig, so it must
      // be matched first or the code-block arm swallows it.
      MathBlock mb          => RenderMathBlock(mb),
      FencedCodeBlock fc    => RenderCode(fc, fc.Info),
      CodeBlock cb          => RenderCode(cb, null),
      QuoteBlock q          => RenderQuote(q),
      ListBlock l           => RenderList(l),
      ThematicBreakBlock _  => RenderThematicBreak(),
      Table t               => RenderTable(t),
      // A line that opens with an unknown tag (<tool_call>, <arg key="…">)
      // parses as an HTML block. We don't render HTML — but dropping it loses
      // whole stretches of a model's output, which is exactly what a
      // degenerate turn emitting tool syntax as prose looks like. Show the
      // source.
      HtmlBlock hb          => RenderLiteralLines(hb),
      _                     => RenderFallback(block),
   };

   private Control RenderMathBlock(MathBlock mb)
   {
      // Block-level math ($$...$$). Render the resolved LaTeX as a single
      // italic, centered paragraph — we don't typeset, but at least the
      // wrapper dollars are gone and the common commands are readable.
      var sb = new System.Text.StringBuilder();
      var lines = mb.Lines.Lines;
      for (int i = 0; i < mb.Lines.Count; i++)
      {
         if (i > 0) sb.Append('\n');
         sb.Append(lines[i].Slice.ToString());
      }
      var tb = new SelectableTextBlock
      {
         Text                = _ResolveLatex(sb.ToString()),
         FontStyle           = FontStyle.Italic,
         FontSize            = 13,
         TextWrapping        = TextWrapping.Wrap,
         HorizontalAlignment = HorizontalAlignment.Center,
         Margin              = new Thickness(0, 4, 0, 4),
      };
      if (TextForeground != null) tb.Foreground = TextForeground;
      return tb;
   }

   private Control RenderHeading(HeadingBlock h)
   {
      double size = h.Level switch
      {
         1 => 22, 2 => 19, 3 => 16, 4 => 14, 5 => 13, _ => 12
      };
      var tb = new SelectableTextBlock
      {
         FontSize   = size,
         FontWeight = FontWeight.Bold,
         Margin     = new Thickness(0, h.Level == 1 ? 8 : 4, 0, 2),
         TextWrapping = TextWrapping.Wrap,
      };
      if (TextForeground != null) tb.Foreground = TextForeground;
      ApplyInlines(tb, h.Inline);
      return tb;
   }

   private Control RenderParagraph(ParagraphBlock p)
   {
      var tb = new SelectableTextBlock
      {
         FontSize     = 13,
         TextWrapping = TextWrapping.Wrap,
      };
      if (TextForeground != null) tb.Foreground = TextForeground;
      ApplyInlines(tb, p.Inline);
      return tb;
   }

   private Control RenderCode(LeafBlock cb, string? language)
   {
      // Code block content lives in the block's Lines collection as raw text.
      var sb = new System.Text.StringBuilder();
      var lines = cb.Lines.Lines;
      for (int i = 0; i < cb.Lines.Count; i++)
      {
         if (i > 0) sb.Append('\n');
         sb.Append(lines[i].Slice.ToString());
      }

      var text = new SelectableTextBlock
      {
         Text         = sb.ToString(),
         FontFamily   = MONO_FONT,
         FontSize     = 12,
         TextWrapping = TextWrapping.NoWrap,
         Foreground   = ThemeBrush("TextLogBrush"),
      };

      var content = new StackPanel { Spacing = 2 };
      if (!string.IsNullOrWhiteSpace(language))
      {
         content.Children.Add(new TextBlock
         {
            Text       = language!,
            FontSize   = 10,
            Foreground = ThemeBrush("TextMutedBrush"),
            Margin     = new Thickness(0, 0, 0, 2),
         });
      }
      content.Children.Add(new ScrollViewer
      {
         HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
         VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
         Content                       = text,
      });

      // Copy affordance overlaid top-right, shown on hover only so it doesn't
      // cost a header line on every block.
      var copy = _NewCopyLink(text.Text ?? "");
      var overlay = new Grid();
      overlay.Children.Add(content);
      overlay.Children.Add(copy);

      var border = new Border
      {
         Background      = ThemeBrush("PanelBackgroundBrush"),
         BorderBrush     = ThemeBrush("BorderColorBrush"),
         BorderThickness = new Thickness(1),
         CornerRadius    = new CornerRadius(3),
         Padding         = new Thickness(8, 6),
         Margin          = new Thickness(0, 2, 0, 2),
         Child           = overlay,
      };
      border.PointerEntered += (_, _) => copy.IsVisible = true;
      border.PointerExited  += (_, _) => copy.IsVisible = false;
      return border;
   }

   private static TextBlock _NewCopyLink(string payload)
   {
      var link = new TextBlock
      {
         Text                = "⧉ copy",
         FontSize            = 10,
         Padding             = new Thickness(4, 0),
         IsVisible           = false,
         HorizontalAlignment = HorizontalAlignment.Right,
         VerticalAlignment   = VerticalAlignment.Top,
         Background          = ThemeBrush("PanelBackgroundBrush"),
         Foreground          = ThemeBrush("TextMutedBrush"),
         Cursor              = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
      };
      link.PointerEntered += (_, _) => link.Foreground = ThemeBrush("AccentBrush");
      link.PointerExited  += (_, _) => link.Foreground = ThemeBrush("TextMutedBrush");
      link.PointerPressed += async (_, e) =>
      {
         e.Handled = true;
         var clipboard = TopLevel.GetTopLevel(link)?.Clipboard;
         if (clipboard == null) return;
         try
         {
            await clipboard.SetTextAsync(payload);
            link.Text = "✓ copied";
            DispatcherTimer.RunOnce(() => link.Text = "⧉ copy", TimeSpan.FromSeconds(1.5));
         }
         catch { }
      };
      return link;
   }

   private Control RenderQuote(QuoteBlock q)
   {
      var inner = new StackPanel { Spacing = 4 };
      foreach (var child in q)
      {
         var c = RenderBlock(child);
         if (c != null) inner.Children.Add(c);
      }
      return new Border
      {
         BorderBrush     = ThemeBrush("AccentBrush"),
         BorderThickness = new Thickness(3, 0, 0, 0),
         Padding         = new Thickness(10, 2, 0, 2),
         Margin          = new Thickness(0, 2, 0, 2),
         Child           = inner,
      };
   }

   private Control RenderList(ListBlock list)
   {
      var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
      int n = 1;
      foreach (var item in list)
      {
         if (item is not ListItemBlock li) continue;
         var marker = list.IsOrdered ? (n + ".") : "•";
         panel.Children.Add(RenderListItem(li, marker));
         n++;
      }
      return panel;
   }

   private Control RenderListItem(ListItemBlock li, string marker)
   {
      var inner = new StackPanel { Spacing = 2 };
      foreach (var child in li)
      {
         var c = RenderBlock(child);
         if (c != null) inner.Children.Add(c);
      }

      var grid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("20,*"),
         Margin            = new Thickness(8, 0, 0, 0),
      };
      var markerTb = new TextBlock
      {
         Text              = marker,
         VerticalAlignment = VerticalAlignment.Top,
         FontWeight        = FontWeight.Bold,
      };
      if (TextForeground != null) markerTb.Foreground = TextForeground;
      Grid.SetColumn(markerTb, 0);
      Grid.SetColumn(inner,    1);
      grid.Children.Add(markerTb);
      grid.Children.Add(inner);
      return grid;
   }

   private Control RenderThematicBreak()
   {
      return new Border
      {
         Height          = 1,
         Background      = ThemeBrush("BorderColorBrush"),
         Margin          = new Thickness(0, 8, 0, 8),
         HorizontalAlignment = HorizontalAlignment.Stretch,
      };
   }

   private Control RenderTable(Table table)
   {
      var grid = new Grid
      {
         Margin = new Thickness(0, 4, 0, 4),
      };

      // Column count from the widest row.
      int cols = 0;
      foreach (var r in table)
      {
         if (r is TableRow row && row.Count > cols) cols = row.Count;
      }
      for (int c = 0; c < cols; c++)
         grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

      int rowIdx = 0;
      foreach (var r in table)
      {
         if (r is not TableRow row) continue;
         grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
         for (int c = 0; c < row.Count; c++)
         {
            if (row[c] is not TableCell cell) continue;
            var cellContent = new StackPanel();
            foreach (var b in cell)
            {
               var ctrl = RenderBlock(b);
               if (ctrl != null) cellContent.Children.Add(ctrl);
            }
            var border = new Border
            {
               BorderBrush     = ThemeBrush("BorderColorBrush"),
               BorderThickness = new Thickness(0, 0, 1, 1),
               Padding         = new Thickness(8, 4),
               Background      = row.IsHeader ? ThemeBrush("PanelBackgroundBrush") : null,
               Child           = cellContent,
            };
            Grid.SetRow(border, rowIdx);
            Grid.SetColumn(border, c);
            grid.Children.Add(border);
         }
         rowIdx++;
      }

      return new Border
      {
         BorderBrush     = ThemeBrush("BorderColorBrush"),
         BorderThickness = new Thickness(1, 1, 0, 0),
         Child           = grid,
      };
   }

   /// <summary>
   /// Render a leaf block as its own source text, verbatim. The reader is
   /// better served by the characters the model actually produced than by a
   /// note saying we didn't recognize them.
   /// </summary>
   private Control RenderLiteralLines(LeafBlock block)
   {
      var sb = new System.Text.StringBuilder();
      var lines = block.Lines.Lines;
      for (int i = 0; i < block.Lines.Count; i++)
      {
         if (i > 0) sb.Append('\n');
         sb.Append(lines[i].Slice.ToString());
      }

      var tb = new SelectableTextBlock
      {
         Text         = sb.ToString(),
         TextWrapping = TextWrapping.Wrap,
      };
      if (TextForeground != null) tb.Foreground = TextForeground;
      return tb;
   }

   private Control RenderFallback(Block block)
   {
      // Anything with source lines can still be shown as itself; only a
      // container we have no arm for falls through to the placeholder.
      if (block is LeafBlock leaf && leaf.Lines.Count > 0)
         return RenderLiteralLines(leaf);

      return new SelectableTextBlock
      {
         Text       = "[unsupported block: " + block.GetType().Name + "]",
         FontStyle  = FontStyle.Italic,
         Foreground = ThemeBrush("TextMutedBrush"),
      };
   }

   // -- inline rendering -----------------------------------------------------

   private void ApplyInlines(SelectableTextBlock tb, ContainerInline? container)
   {
      if (container == null) return;
      foreach (var inline in container)
      {
         var produced = RenderInline(inline);
         foreach (var it in produced)
            tb.Inlines!.Add(it);
      }
   }

   private IEnumerable<AvInline> RenderInline(MdInline inline)
   {
      switch (inline)
      {
         case LiteralInline lit:
            yield return new Run(lit.Content.ToString());
            break;

         case EmphasisInline em:
         {
            // delimiter '*' or '_' single = italic, double = bold, triple = both.
            var span = new Span();
            foreach (var child in em)
               foreach (var inner in RenderInline(child))
                  span.Inlines.Add(inner);

            if (em.DelimiterCount >= 2)
               span.FontWeight = FontWeight.Bold;
            if (em.DelimiterCount == 1 || em.DelimiterCount == 3)
               span.FontStyle = FontStyle.Italic;

            yield return span;
            break;
         }

         case CodeInline code:
         {
            yield return new Run(code.Content)
            {
               FontFamily = MONO_FONT,
               Background = ThemeBrush("PanelBackgroundBrush"),
               Foreground = ThemeBrush("TextLogBrush"),
            };
            break;
         }

         case LinkInline link:
         {
            // Render as styled, underlined accent-colored run with the URL
            // shown on hover. Click navigation is intentionally not wired
            // here — the host UI decides whether to open in browser.
            var span = new Span
            {
               TextDecorations = TextDecorations.Underline,
               Foreground      = ThemeBrush("AccentBrush"),
            };
            foreach (var child in link)
               foreach (var inner in RenderInline(child))
                  span.Inlines.Add(inner);

            // ToolTip.SetTip requires a Control; Span isn't one. URLs are
            // visible in the parsed source; click navigation + URL preview
            // can come later via InlineUIContainer if needed.
            yield return span;
            break;
         }

         case LineBreakInline:
            yield return new LineBreak();
            break;

         case MathInline math:
         {
            // Resolve common LaTeX commands to Unicode and emit as a
            // styled italic run. We don't actually render TeX — anything
            // unknown is passed through verbatim so the user can still
            // read the raw command.
            var resolved = _ResolveLatex(math.Content.ToString());
            yield return new Run(resolved)
            {
               FontStyle = FontStyle.Italic,
            };
            break;
         }

         case TaskList task:
         {
            // GFM checkbox at the start of a list item: "- [ ]" / "- [x]".
            // Read-only — the model's text isn't a live document we edit.
            // Trailing space separates the glyph from the label text that
            // follows it as the next inline.
            yield return new Run(task.Checked ? "☑ " : "☐ ")
            {
               Foreground = task.Checked
                  ? ThemeBrush("AccentBrush")
                  : ThemeBrush("TextMutedBrush"),
            };
            break;
         }

         case HtmlInline html:
            // A bare tag inside a paragraph: <tool_call>, <arg key="path">,
            // <parameter=old_string>. We render no HTML, so the tag IS the
            // text — and a model that emits tool syntax as prose is precisely
            // when the reader needs to see it. Markdig's inline nodes do not
            // override ToString(), so the old default arm printed
            // "Markdig.Syntax.Inlines.HtmlInline" here instead of the tag.
            yield return new Run(html.Tag ?? "");
            break;

         case HtmlEntityInline entity:
            yield return new Run(entity.Transcoded.ToString());
            break;

         case AutolinkInline auto:
            yield return new Run(auto.Url ?? "")
            {
               TextDecorations = TextDecorations.Underline,
               Foreground      = ThemeBrush("AccentBrush"),
            };
            break;

         case ContainerInline container:
         {
            foreach (var child in container)
               foreach (var inner in RenderInline(child))
                  yield return inner;
            break;
         }

         default:
            // Unknown leaf inline. Recover its source span rather than its
            // type name — ToString() on a Markdig node is the class name, so
            // anything landing here would otherwise be unreadable.
            yield return new Run(_SourceTextOf(inline));
            break;
      }
   }

   // Best-effort recovery of the literal characters an inline came from.
   // Empty when the node carries no usable span, which beats a type name.
   private static string _SourceTextOf(MdInline inline)
   {
      if (inline is LeafInline leaf)
      {
         var s = leaf.ToString();
         // Guard against Markdig's default Object.ToString(): a class name is
         // never what the model wrote.
         if (!string.IsNullOrEmpty(s) && !s.StartsWith("Markdig.", StringComparison.Ordinal))
            return s;
      }
      return "";
   }

   // -- helpers --------------------------------------------------------------

   // -- LaTeX → Unicode substitution -----------------------------------------

   /// <summary>
   /// Resolve a curated set of common LaTeX commands ($\to$, $\alpha$, …)
   /// to their Unicode equivalents. Unknown commands are left as-is so
   /// the user still sees the raw token; whole-word matching avoids
   /// mangling identifiers like <c>\toolname</c>. Pure string transform,
   /// no rendering of fractions / scripts / matrices.
   /// </summary>
   private static string _ResolveLatex(string src)
   {
      if (string.IsNullOrEmpty(src)) return src ?? "";
      return s_latexRe.Replace(src, m =>
      {
         var name = m.Value.Substring(1); // strip the backslash
         return s_latexMap.TryGetValue(name, out var u) ? u : m.Value;
      });
   }

   // Matches a backslash followed by one or more ASCII letters. Greedy on
   // letters so "\rightarrow" matches as a whole, not as "\r" + "ightarrow".
   private static readonly System.Text.RegularExpressions.Regex s_latexRe =
      new(@"\\[A-Za-z]+", System.Text.RegularExpressions.RegexOptions.Compiled);

   private static readonly Dictionary<string, string> s_latexMap = new(StringComparer.Ordinal)
   {
      // arrows
      ["to"]              = "→",
      ["rightarrow"]      = "→",
      ["Rightarrow"]      = "⇒",
      ["leftarrow"]       = "←",
      ["Leftarrow"]       = "⇐",
      ["leftrightarrow"]  = "↔",
      ["Leftrightarrow"]  = "⇔",
      ["mapsto"]          = "↦",
      ["uparrow"]         = "↑",
      ["downarrow"]       = "↓",

      // operators / relations
      ["cdot"]   = "·", ["cdots"] = "⋯", ["ldots"] = "…",
      ["times"]  = "×", ["div"]   = "÷",
      ["pm"]     = "±", ["mp"]    = "∓",
      ["le"]     = "≤", ["leq"]   = "≤",
      ["ge"]     = "≥", ["geq"]   = "≥",
      ["ne"]     = "≠", ["neq"]   = "≠",
      ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼", ["propto"] = "∝",
      ["in"]     = "∈", ["notin"] = "∉", ["ni"] = "∋",
      ["subset"] = "⊂", ["supset"] = "⊃", ["subseteq"] = "⊆", ["supseteq"] = "⊇",
      ["cup"]    = "∪", ["cap"]   = "∩", ["setminus"] = "∖",
      ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄",
      ["land"]   = "∧", ["lor"]   = "∨", ["lnot"] = "¬", ["neg"] = "¬",
      ["sum"]    = "∑", ["prod"]  = "∏", ["int"] = "∫",
      ["infty"]  = "∞", ["partial"] = "∂", ["nabla"] = "∇",
      ["sqrt"]   = "√", ["angle"]   = "∠", ["degree"] = "°",

      // greek lowercase
      ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
      ["epsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ",
      ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
      ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ",
      ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ",
      ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",

      // greek uppercase
      ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
      ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ",
      ["Psi"] = "Ψ", ["Omega"] = "Ω",
   };

   private static IBrush? ThemeBrush(string key)
   {
      if (Application.Current != null &&
          Application.Current.TryFindResource(key, out var v) &&
          v is IBrush b)
         return b;
      return null;
   }

   // -- fields ---------------------------------------------------------------

   private static readonly FontFamily MONO_FONT =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private readonly MarkdownPipeline _pipeline;
}
