// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SlimCodeDesktop.Agents.Native.Permissions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Outline;

// PDFtoImage declares its platforms; this app ships on two of them.
#pragma warning disable CA1416

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Read a PDF from the project. Four modes, cheapest first, so a model can find
/// its way around a long document before paying for pixels:
///   - "info":   page count, metadata, page sizes, printed labels, outline.
///   - "search": which pages contain a phrase, with snippets.
///   - "text":   the text layer of chosen pages.
///   - "view":   chosen pages as images, optionally cropped.
///
/// Two libraries, split on purpose. Everything textual runs on PdfPig, which is
/// managed code, so a damaged file surfaces as an exception. Only "view" (and
/// page sizes) reaches native PDFium, through PDFtoImage. They parse the file
/// independently, so one can fail where the other copes; each mode reports its
/// own failure rather than assuming the document is unreadable altogether.
///
/// Crops are in page points from the top-left corner, as the page is displayed
/// (rotation applied). Unlike image pixels those stay valid at any zoom, and a
/// crop is rendered anew rather than cut from a shrunken page, so zooming into
/// a vector drawing gains real detail.
/// </summary>
public sealed class ReadPdfTool : ITool
{
   public string Name => "read_pdf";

   public string Description =>
      "Read a PDF file from the project. Pages are always 1-based physical pages. " +
      "Work cheapest-first: mode='info' gives page count, metadata, page sizes, printed page labels " +
      "and the outline; mode='search' with 'query' finds the pages containing a phrase; " +
      "mode='text' returns the text of 'pages' (use layout='columns' if two-column prose reads " +
      "scrambled; the default keeps table rows together); mode='view' shows 'pages' as images, at most " +
      MAX_VIEW_PAGES + " per call, for drawings, scans, or tables text mode garbles. " +
      "To see detail in one page, crop: crop_x/crop_y/crop_width/crop_height are in page points " +
      "from the top-left, and the crop is re-rendered with the whole pixel budget.";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      var sb = new StringBuilder();
      sb.Append(_Str(args, "path") ?? "(no path)");
      var mode = _Str(args, "mode");
      if (!string.IsNullOrEmpty(mode))
         sb.Append(", mode=").Append(mode);
      var pages = _Pages(args);
      if (!string.IsNullOrEmpty(pages))
         sb.Append(", pages=").Append(pages);
      var query = _Str(args, "query");
      if (!string.IsNullOrEmpty(query))
         sb.Append(", query=").Append(query);
      if (_TryCrop(args, out var c, out _) && c.HasValue)
         sb.Append(", crop=").Append(c.Value.X).Append(',').Append(c.Value.Y)
           .Append(' ').Append(c.Value.Width).Append('×').Append(c.Value.Height);
      if (_Int(args, "max_pixels") is long mp)
         sb.Append(", max_pixels=").Append(mp);
      return sb.ToString();
   }

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? rel = _Str(args, "path");
      if (string.IsNullOrWhiteSpace(rel))
         return Task.FromResult(ToolResult.Error("'path' is required"));

      string mode = _Str(args, "mode") ?? MODE_INFO;
      if (mode != MODE_INFO && mode != MODE_SEARCH && mode != MODE_TEXT && mode != MODE_VIEW)
         return Task.FromResult(ToolResult.Error("'mode' must be 'info', 'search', 'text' or 'view'"));

      var res = ctx.Resolve(rel, PathAccess.Read);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + rel));
      var abs = res.FullPath;
      if (!File.Exists(abs))
         return Task.FromResult(ToolResult.Error("file not found: " + rel));
      if (!string.Equals(Path.GetExtension(abs), ".pdf", StringComparison.OrdinalIgnoreCase))
         return Task.FromResult(ToolResult.Error("not a .pdf file; use read_file for text or read_image for images"));

      // Parsing and rendering a big document takes real time: keep it off
      // whatever thread the loop continues on.
      return Task.Run(() => _Run(args, rel, abs, mode, ctx.Cancel));
   }

   // -- private --------------------------------------------------------------

   private static ToolResult _Run(JsonElement args, string rel, string abs, string mode, CancellationToken cancel)
   {
      try
      {
         var info = new FileInfo(abs);
         if (info.Length > MAX_SOURCE_BYTES)
            return ToolResult.Error("file is larger than " + (MAX_SOURCE_BYTES / (1024 * 1024)) + " MB");

         var bytes = File.ReadAllBytes(abs);
         return mode switch
         {
            MODE_SEARCH => _Search(args, rel, bytes, cancel),
            MODE_TEXT   => _Text(args, rel, bytes, cancel),
            MODE_VIEW   => _View(args, rel, bytes, cancel),
            _           => _Info(rel, bytes, info.Length),
         };
      }
      catch (OperationCanceledException)
      {
         return ToolResult.Error("cancelled");
      }
      catch (Exception ex) when (ex is PdfDocumentEncryptedException or PdfPasswordProtectedException)
      {
         return ToolResult.Error(PASSWORD_MESSAGE);
      }
      catch (Exception ex)
      {
         return ToolResult.Error("could not read the PDF: " + ex.Message);
      }
   }

   // -- info -----------------------------------------------------------------

   private static ToolResult _Info(string rel, byte[] bytes, long fileLength)
   {
      var sb = new StringBuilder();
      sb.Append(rel).Append(": ");

      // Sizes come from PDFium because it applies page rotation without having
      // to parse each page. Either library alone still makes a useful answer.
      IList<SizeF>? sizes = null;
      string? sizesError  = null;
      try
      {
         lock (s_pdfiumLock)
            sizes = Conversion.GetPageSizes(bytes);
      }
      catch (PdfPasswordProtectedException) { throw; }
      catch (Exception ex) { sizesError = ex.Message; }

      PdfDocument? doc = null;
      string? docError = null;
      try
      {
         doc = _Open(bytes);
      }
      catch (PdfDocumentEncryptedException) { throw; }
      catch (Exception ex) { docError = ex.Message; }

      using (doc)
      {
         if (doc == null && sizes == null)
            return ToolResult.Error("could not read the PDF: " + (docError ?? sizesError));

         int pageCount = doc?.NumberOfPages ?? sizes!.Count;
         sb.Append(pageCount).Append(pageCount == 1 ? " page, " : " pages, ")
           .Append(ToolPreview.FormatBytes(fileLength)).Append(".\n");

         if (doc != null)
         {
            if (doc.IsEncrypted)
               sb.Append("Encrypted, readable without a password.\n");
            _AppendMetadata(sb, doc.Information);
         }

         if (sizes != null)
            sb.Append("Page size: ").Append(_DescribeSizes(sizes)).Append(" (points).\n");
         else
            sb.Append("Page sizes unavailable, 'view' may not work: ").Append(sizesError).Append('\n');

         if (doc == null)
         {
            sb.Append("Text, labels and outline unavailable, 'text' and 'search' may not work: ")
              .Append(docError).Append('\n');
            return ToolResult.Ok(sb.ToString().TrimEnd());
         }

         var labels = PdfPageLabels.TryRead(doc)?.Describe();
         if (!string.IsNullOrEmpty(labels))
            sb.Append("Printed labels: ").Append(labels).Append(".\n");

         sb.Append(_DescribeTextLayer(doc)).Append('\n');
         _AppendOutline(sb, doc);
      }
      return ToolResult.Ok(sb.ToString().TrimEnd());
   }

   private static void _AppendMetadata(StringBuilder sb, DocumentInformation meta)
   {
      void Field(string name, string? value)
      {
         if (string.IsNullOrWhiteSpace(value)) return;
         value = _Collapse(value);
         if (value.Length > MAX_METADATA_CHARS)
            value = value.Substring(0, MAX_METADATA_CHARS) + "...";
         sb.Append(name).Append(": ").Append(value).Append('\n');
      }

      Field("Title",    meta.Title);
      Field("Author",   meta.Author);
      Field("Subject",  meta.Subject);
      Field("Keywords", meta.Keywords);
      Field("Creator",  meta.Creator);
      Field("Producer", meta.Producer);
      Field("Created",  meta.GetCreatedDateTimeOffset()?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
      Field("Modified", meta.GetModifiedDateTimeOffset()?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
   }

   // "1-238 595×842, 239-240 842×595". Whole points: nothing downstream needs
   // finer, and A4 is 595.28 only on paper.
   private static string _DescribeSizes(IList<SizeF> sizes)
   {
      var parts = new List<string>();
      int omitted = 0;
      for (int i = 0; i < sizes.Count; )
      {
         string size = _Size(sizes[i]);
         int last = i;
         while (last + 1 < sizes.Count && _Size(sizes[last + 1]) == size)
            last++;

         if (parts.Count == MAX_SIZE_RUNS)
            omitted++;
         else
            parts.Add((last > i ? (i + 1) + "-" + (last + 1) : (i + 1).ToString()) + " " + size);
         i = last + 1;
      }
      if (omitted > 0)
         parts.Add("and " + omitted + " more runs");
      return string.Join(", ", parts);
   }

   private static string _Size(SizeF size) =>
      (int)Math.Round(size.Width) + "×" + (int)Math.Round(size.Height);

   // Sampled, evenly spread: parsing every page of a long document is the cost
   // this mode exists to avoid. Tells the model whether 'text' can work at all.
   private static string _DescribeTextLayer(PdfDocument doc)
   {
      int total   = doc.NumberOfPages;
      int sampled = Math.Min(total, TEXT_LAYER_SAMPLE);
      int withText = 0;
      for (int i = 0; i < sampled; i++)
      {
         int page = 1 + (int)((long)i * total / sampled);
         try
         {
            if (doc.GetPage(page).Letters.Count > 0)
               withText++;
         }
         catch { }
      }

      string scope = sampled == total ? sampled + " pages" : sampled + " sampled pages";
      if (withText == 0)
         return "Text layer: none on " + scope + " (scanned?); use mode='view'.";
      return "Text layer: on " + withText + " of " + scope + ".";
   }

   // A long outline keeps only its upper levels: the map matters more than
   // every sub-sub-section, and those pages are reachable through search.
   private static void _AppendOutline(StringBuilder sb, PdfDocument doc)
   {
      var entries = new List<(int Level, string Title, int? Page)>();
      try
      {
         if (doc.TryGetBookmarks(out var bookmarks, false))
         {
            foreach (var root in bookmarks.Roots)
               _CollectOutline(root, 0, entries);
         }
      }
      catch
      {
         sb.Append("Outline: could not be read.\n");
         return;
      }
      if (entries.Count == 0)
      {
         sb.Append("Outline: none.\n");
         return;
      }

      int maxLevel = entries.Max(e => e.Level);
      while (maxLevel > 0 && entries.Count(e => e.Level <= maxLevel) > MAX_OUTLINE_ENTRIES)
         maxLevel--;
      var shown = entries.Where(e => e.Level <= maxLevel).Take(MAX_OUTLINE_ENTRIES).ToList();

      sb.Append("Outline");
      if (shown.Count < entries.Count)
         sb.Append(" (").Append(shown.Count).Append(" of ").Append(entries.Count).Append(" entries)");
      sb.Append(":\n");
      foreach (var e in shown)
      {
         sb.Append(' ', 2 + e.Level * 2).Append(e.Title);
         if (e.Page.HasValue)
            sb.Append(" -> p.").Append(e.Page.Value);
         sb.Append('\n');
      }
   }

   private static void _CollectOutline(BookmarkNode node, int level, List<(int, string, int?)> entries)
   {
      if (level > MAX_OUTLINE_DEPTH) return;
      entries.Add((level, _Collapse(node.Title ?? ""), (node as DocumentBookmarkNode)?.PageNumber));
      foreach (var child in node.Children)
         _CollectOutline(child, level + 1, entries);
   }

   // -- search ---------------------------------------------------------------

   private static ToolResult _Search(JsonElement args, string rel, byte[] bytes, CancellationToken cancel)
   {
      string? query = _Str(args, "query");
      if (string.IsNullOrWhiteSpace(query))
         return ToolResult.Error("mode='search' needs 'query'");
      query = _Collapse(query);

      using var doc = _Open(bytes);
      if (!_TrySelectPages(args, doc.NumberOfPages, out var pages, out var pageError))
         return ToolResult.Error(pageError!);

      var labels = PdfPageLabels.TryRead(doc);
      var listing = new StringBuilder();
      int totalMatches = 0, pagesWithMatch = 0, pagesListed = 0, pagesWithoutText = 0, pagesFailed = 0, lastListed = 0;

      foreach (int page in pages)
      {
         cancel.ThrowIfCancellationRequested();

         string text;
         try
         {
            var p = doc.GetPage(page);
            if (p.Letters.Count == 0)
            {
               pagesWithoutText++;
               continue;
            }
            // Collapsed so a phrase still matches across a line break.
            text = _Collapse(_PageText(p, LAYOUT_LINES));
         }
         catch
         {
            pagesFailed++;
            continue;
         }

         var positions = _FindAll(text, query);
         if (positions.Count == 0)
            continue;
         totalMatches += positions.Count;
         pagesWithMatch++;
         if (pagesListed == MAX_SEARCH_PAGES)
            continue;

         pagesListed++;
         lastListed = page;
         listing.Append(_PageName(page, labels)).Append(": ").Append(positions.Count)
                .Append(positions.Count == 1 ? " match\n" : " matches\n");
         foreach (int pos in positions.Take(MAX_SNIPPETS_PER_PAGE))
            listing.Append("  ").Append(_Snippet(text, pos, query.Length)).Append('\n');
      }

      var sb = new StringBuilder();
      sb.Append(rel).Append(": \"").Append(query).Append("\": ").Append(totalMatches)
        .Append(totalMatches == 1 ? " match on " : " matches on ").Append(pagesWithMatch)
        .Append(" of ").Append(pages.Count).Append(" searched pages.\n");
      sb.Append(listing);
      if (pagesWithMatch > pagesListed)
         sb.Append("Only the first ").Append(pagesListed).Append(" matching pages are listed. Search on with pages=\"")
           .Append(lastListed + 1).Append("-\".\n");
      if (pagesWithoutText > 0)
         sb.Append(pagesWithoutText).Append(" searched page(s) have no text layer, so a match there cannot be found; use mode='view'.\n");
      if (pagesFailed > 0)
         sb.Append(pagesFailed).Append(" searched page(s) could not be parsed.\n");
      return ToolResult.Ok(sb.ToString().TrimEnd());
   }

   private static List<int> _FindAll(string text, string query)
   {
      var found = new List<int>();
      int at = 0;
      while ((at = text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase)) >= 0)
      {
         found.Add(at);
         at += query.Length;
      }
      return found;
   }

   private static string _Snippet(string text, int pos, int length)
   {
      int from = Math.Max(0, pos - SNIPPET_CONTEXT);
      int to   = Math.Min(text.Length, pos + length + SNIPPET_CONTEXT);
      return (from > 0 ? "..." : "") + text.Substring(from, to - from) + (to < text.Length ? "..." : "");
   }

   // -- text -----------------------------------------------------------------

   private static ToolResult _Text(JsonElement args, string rel, byte[] bytes, CancellationToken cancel)
   {
      string layout = _Str(args, "layout") ?? LAYOUT_LINES;
      if (layout != LAYOUT_LINES && layout != LAYOUT_COLUMNS)
         return ToolResult.Error("'layout' must be 'lines' or 'columns'");

      using var doc = _Open(bytes);
      if (!_TrySelectPages(args, doc.NumberOfPages, out var pages, out var pageError))
         return ToolResult.Error(pageError!);

      var labels = PdfPageLabels.TryRead(doc);
      var sb = new StringBuilder();
      sb.Append(rel).Append(": ").Append(doc.NumberOfPages).Append(" pages.\n");
      int budget = MAX_TEXT_CHARS;

      for (int i = 0; i < pages.Count; i++)
      {
         cancel.ThrowIfCancellationRequested();

         int page = pages[i];
         string body;
         try
         {
            var p = doc.GetPage(page);
            body = p.Letters.Count == 0
               ? "(no text layer on this page; use mode='view')"
               : _PageText(p, layout).Trim();
         }
         catch (Exception ex)
         {
            body = "(could not parse this page: " + ex.Message + ")";
         }

         // Pages stay whole where they can: a resume point on a page boundary
         // is one the model can name. Only a lone oversized page gets cut.
         if (body.Length > budget)
         {
            if (i > 0)
            {
               sb.Append("\nStopped before page ").Append(page).Append(" to stay within ").Append(MAX_TEXT_CHARS)
                 .Append(" characters. Continue with pages=\"").Append(PdfPageSpec.Format(pages.Skip(i).ToList())).Append("\".");
               return ToolResult.Ok(sb.ToString());
            }
            body = body.Substring(0, budget) + "\n(page cut at " + budget + " characters; use mode='search' or 'view' for the rest)";
         }

         sb.Append("\n--- ").Append(_PageName(page, labels)).Append(" ---\n").Append(body).Append('\n');
         budget -= body.Length;
      }
      return ToolResult.Ok(sb.ToString().TrimEnd());
   }

   private static string _PageText(Page page, string layout)
   {
      if (layout != LAYOUT_COLUMNS)
         return ContentOrderTextExtractor.GetText(page);

      var words   = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
      var blocks  = RecursiveXYCut.Instance.GetBlocks(words);
      var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
      return string.Join("\n\n", ordered.Select(b => b.Text));
   }

   // -- view -----------------------------------------------------------------

   private static ToolResult _View(JsonElement args, string rel, byte[] bytes, CancellationToken cancel)
   {
      if (!_TryCrop(args, out var requested, out var cropError))
         return ToolResult.Error(cropError!);

      int pageCount;
      lock (s_pdfiumLock)
         pageCount = Conversion.GetPageCount(bytes);

      // No 'pages' means page 1, and the text says how many there are: a viewer
      // never silently pays for a whole document.
      var pages = new List<int> { 1 };
      string? spec = _Pages(args);
      if (!string.IsNullOrEmpty(spec) && !PdfPageSpec.TryParse(spec, pageCount, out pages, out var pageError))
         return ToolResult.Error(pageError!);
      if (pages.Count > MAX_VIEW_PAGES)
         return ToolResult.Error("at most " + MAX_VIEW_PAGES + " pages per 'view', " + pages.Count + " were asked for: " +
                                 "every image stays in the conversation. Narrow it down with mode='search'.");
      if (requested.HasValue && pages.Count > 1)
         return ToolResult.Error("a crop applies to one page; " + pages.Count + " were asked for");

      long maxPixels = _Int(args, "max_pixels") ?? ImageIngest.MAX_PIXELS;
      if (maxPixels < ReadImageTool.MIN_PIXELS) maxPixels = ReadImageTool.MIN_PIXELS;
      bool capped = maxPixels > ReadImageTool.MAX_PIXELS_CAP;
      if (capped) maxPixels = ReadImageTool.MAX_PIXELS_CAP;

      var labels = _TryReadLabels(bytes);
      var images = new List<ImagePart>();
      var text   = new StringBuilder();
      text.Append(rel).Append(": ").Append(pageCount).Append(pageCount == 1 ? " page.\n" : " pages.\n");

      foreach (int page in pages)
      {
         cancel.ThrowIfCancellationRequested();

         SizeF size;
         lock (s_pdfiumLock)
            size = Conversion.GetPageSize(bytes, page - 1);

         RectangleF? crop = null;
         if (requested.HasValue)
         {
            var r = RectangleF.Intersect(requested.Value, new RectangleF(0, 0, size.Width, size.Height));
            if (r.Width <= 0 || r.Height <= 0)
               return ToolResult.Error("crop lies outside page " + page + " (" + _Size(size) + " points)");
            crop = r;
         }

         float regionW = crop?.Width  ?? size.Width;
         float regionH = crop?.Height ?? size.Height;
         int   dpi     = _DpiForBudget(regionW, regionH, maxPixels);

         // DpiRelativeToBounds is what makes a crop a crop: without it the region
         // is stretched over a full-page bitmap, distorted to the page's shape.
         var options = new RenderOptions
         {
            Dpi                 = dpi,
            WithAnnotations     = true,
            WithFormFill        = true,
            Bounds              = crop,
            DpiRelativeToBounds = crop.HasValue,
         };

         SkiaSharp.SKBitmap bitmap;
         lock (s_pdfiumLock)
            bitmap = Conversion.ToImage(bytes, page - 1, options: options);

         using (bitmap)
         {
            if (!_TryEncodePage(bitmap, rel + " p." + page, out var image, out var encodeError))
               return ToolResult.Error(encodeError ?? "could not encode page " + page);
            images.Add(image!);

            text.Append(_PageName(page, labels)).Append(": ").Append(_Size(size)).Append(" points.");
            if (crop.HasValue)
            {
               var c = crop.Value;
               text.Append(" Cropped to x=").Append(_Num(c.X)).Append(", y=").Append(_Num(c.Y))
                   .Append(", width=").Append(_Num(c.Width)).Append(", height=").Append(_Num(c.Height));
               if (requested!.Value != c)
                  text.Append(" (clamped to the page)");
               text.Append('.');
            }
            text.Append(" Shown at ").Append(bitmap.Width).Append('×').Append(bitmap.Height)
                .Append(" (scale ").Append(((double)dpi / POINTS_PER_INCH).ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" pixels per point).\n");
         }
      }

      if (capped)
         text.Append("max_pixels was capped at ").Append(ReadImageTool.MAX_PIXELS_CAP).Append(".\n");
      text.Append("Crop coordinates are points from the page's top-left, at any zoom: point = crop origin + pixel / scale.\n");
      text.Append(images.Count == 1 ? "The image follows in the next message." : "The images follow in the next message, in this order.");
      return ToolResult.Ok(text.ToString(), images);
   }

   // Lossless where it stays small. A scanned or photographic page does not, and
   // every image lives in the transcript and the prompt for the whole session.
   private static bool _TryEncodePage(SkiaSharp.SKBitmap bitmap, string name, out ImagePart? image, out string? error)
   {
      if (!ImageIngest.TryEncode(bitmap, name, preferJpeg: false, out image, out error))
         return false;
      if (image!.Base64.Length <= MAX_PNG_BASE64)
         return true;
      return ImageIngest.TryEncode(bitmap, name, preferJpeg: true, out image, out error);
   }

   // Largest whole DPI that keeps the region within the pixel budget, capped
   // where zooming stops resolving anything new.
   private static int _DpiForBudget(float widthPoints, float heightPoints, long maxPixels)
   {
      double area  = Math.Max(1.0, (double)widthPoints * heightPoints);
      double scale = Math.Sqrt(maxPixels / area);
      return Math.Clamp((int)Math.Floor(scale * POINTS_PER_INCH), 1, MAX_RENDER_DPI);
   }

   // View works from PDFium alone; labels are only a nicety on top of it.
   private static PdfPageLabels? _TryReadLabels(byte[] bytes)
   {
      try
      {
         using var doc = _Open(bytes);
         return PdfPageLabels.TryRead(doc);
      }
      catch
      {
         return null;
      }
   }

   // -- shared ---------------------------------------------------------------

   private static PdfDocument _Open(byte[] bytes) =>
      PdfDocument.Open(bytes, new ParsingOptions { SkipMissingFonts = true });

   // No 'pages' means the whole document.
   private static bool _TrySelectPages(JsonElement args, int pageCount, out List<int> pages, out string? error)
   {
      string? spec = _Pages(args);
      if (!string.IsNullOrEmpty(spec))
         return PdfPageSpec.TryParse(spec, pageCount, out pages, out error);

      pages = Enumerable.Range(1, pageCount).ToList();
      error = null;
      return true;
   }

   private static string _PageName(int page, PdfPageLabels? labels)
   {
      string? label = labels?.Get(page);
      return string.IsNullOrEmpty(label) || label == page.ToString()
         ? "page " + page
         : "page " + page + " (label " + label + ")";
   }

   private static string _Collapse(string text) =>
      string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

   private static string _Num(float value) =>
      value.ToString("0.##", CultureInfo.InvariantCulture);

   // Same rules as read_image: a crop needs all four fields.
   private static bool _TryCrop(JsonElement args, out RectangleF? crop, out string? error)
   {
      crop  = null;
      error = null;
      long? x = _Int(args, "crop_x"), y = _Int(args, "crop_y");
      long? w = _Int(args, "crop_width"), h = _Int(args, "crop_height");
      if (x == null && y == null && w == null && h == null)
         return true;
      if (x == null || y == null || w == null || h == null)
      {
         error = "a crop needs all of crop_x, crop_y, crop_width and crop_height";
         return false;
      }
      if (x < 0 || y < 0 || w <= 0 || h <= 0)
      {
         error = "crop_x/crop_y must be >= 0 and crop_width/crop_height > 0";
         return false;
      }
      crop = new RectangleF(x.Value, y.Value, w.Value, h.Value);
      return true;
   }

   // Models send 12 as often as "12".
   private static string? _Pages(JsonElement args)
   {
      if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("pages", out var v) && v.ValueKind == JsonValueKind.Number)
         return _Int(args, "pages")?.ToString();
      return _Str(args, "pages");
   }

   private static string? _Str(JsonElement args, string name) =>
      args.ValueKind == JsonValueKind.Object &&
      args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
         ? v.GetString()
         : null;

   private static long? _Int(JsonElement args, string name)
   {
      if (args.ValueKind != JsonValueKind.Object ||
          !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
         return null;
      if (v.TryGetInt64(out var l)) return l;
      return (long)Math.Round(v.GetDouble());
   }

   // -- constants ------------------------------------------------------------

   private const string MODE_INFO   = "info";
   private const string MODE_SEARCH = "search";
   private const string MODE_TEXT   = "text";
   private const string MODE_VIEW   = "view";

   private const string LAYOUT_LINES   = "lines";
   private const string LAYOUT_COLUMNS = "columns";

   private const string PASSWORD_MESSAGE =
      "this PDF is password-protected; opening it with a password is not supported";

   /// <summary>
   /// Larger than for images: a scanned PDF is routinely this big, and only the
   /// asked-for pages are ever decoded.
   /// </summary>
   private const long MAX_SOURCE_BYTES = 256L * 1024 * 1024;

   private const int MAX_TEXT_CHARS        = 30_000;
   private const int MAX_VIEW_PAGES        = 4;
   private const int MAX_SEARCH_PAGES      = 30;
   private const int MAX_SNIPPETS_PER_PAGE = 3;
   private const int SNIPPET_CONTEXT       = 60;
   private const int MAX_OUTLINE_ENTRIES   = 200;
   private const int MAX_OUTLINE_DEPTH     = 16;
   private const int MAX_METADATA_CHARS    = 200;
   private const int MAX_SIZE_RUNS         = 20;
   private const int TEXT_LAYER_SAMPLE     = 20;

   private const int POINTS_PER_INCH = 72;
   private const int MAX_RENDER_DPI  = 300;

   /// <summary>A page encoding past this (about 1.5 MB of PNG) goes out as JPEG instead.</summary>
   private const int MAX_PNG_BASE64 = 2_000_000;

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":        { "type": "string", "description": "Project-relative path to the PDF file." },
         "mode":        { "type": "string", "enum": ["info", "search", "text", "view"], "description": "'info' (default): page count, metadata, sizes, labels, outline. 'search': pages containing 'query'. 'text': text of 'pages'. 'view': 'pages' as images." },
         "pages":       { "type": "string", "description": "1-based physical pages: 3, 3-7, 1,4,9-11 or 5-. Default for 'search' and 'text': all pages. Default for 'view': page 1." },
         "query":       { "type": "string", "description": "mode='search': phrase to find, case-insensitive; line breaks in the page are ignored." },
         "layout":      { "type": "string", "enum": ["lines", "columns"], "description": "mode='text': 'lines' (default) reads across the page and keeps table rows together; 'columns' follows columns, for two-column prose." },
         "crop_x":      { "type": "integer", "minimum": 0, "description": "mode='view': left edge of the crop, in points from the page's left." },
         "crop_y":      { "type": "integer", "minimum": 0, "description": "mode='view': top edge of the crop, in points from the page's top." },
         "crop_width":  { "type": "integer", "minimum": 1, "description": "mode='view': crop width, in points." },
         "crop_height": { "type": "integer", "minimum": 1, "description": "mode='view': crop height, in points." },
         "max_pixels":  { "type": "integer", "minimum": 16384, "maximum": 2000000, "description": "mode='view': pixel budget per page or crop (default 1000000, max 2000000)." }
      },
      "required": ["path"]
   }
   """;

   // PDFium is not safe to enter from two threads, and a subagent can be reading
   // at the same time as the main loop.
   private static readonly object s_pdfiumLock = new();
}
