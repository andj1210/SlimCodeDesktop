// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Tools;
using UglyToad.PdfPig;
using Xunit;

namespace SlimCodeDesktop.Tests;

/// <summary>
/// read_pdf end to end against real files and the real libraries. Path rules
/// live in <see cref="FileToolSandboxTests"/>.
/// </summary>
public sealed class ReadPdfToolTests : IDisposable
{
   public ReadPdfToolTests()
   {
      _tree = new TempTree();
      _ctx  = new ToolContext(_tree.Project, CancellationToken.None);
      File.WriteAllBytes(_tree.InProject("sample.pdf"), TestPdf.Sample());
   }

   public void Dispose() => _tree.Dispose();

   // -- page selection -------------------------------------------------------

   [Theory]
   [InlineData("3",          10, "3")]
   [InlineData("3-5",        10, "3,4,5")]
   [InlineData(" 1, 4 ,9-10", 10, "1,4,9,10")]
   [InlineData("8-",         10, "8,9,10")]
   [InlineData("8-999",      10, "8,9,10")]
   [InlineData("2,1,2",      10, "2,1")]
   public void PageSpec_Parses(string spec, int count, string expected)
   {
      Assert.True(PdfPageSpec.TryParse(spec, count, out var pages, out var error), error);
      Assert.Equal(expected, string.Join(",", pages));
   }

   [Theory]
   [InlineData("0")]
   [InlineData("11")]
   [InlineData("5-3")]
   [InlineData("abc")]
   [InlineData("-3")]
   [InlineData("1,,2")]
   [InlineData("")]
   [InlineData("99999999999")]
   public void PageSpec_Rejects(string spec)
   {
      Assert.False(PdfPageSpec.TryParse(spec, 10, out _, out var error));
      Assert.False(string.IsNullOrEmpty(error));
   }

   [Fact]
   public void PageSpec_FormatJoinsRuns()
   {
      Assert.Equal("1-3,7,9-10", PdfPageSpec.Format(new[] { 1, 2, 3, 7, 9, 10 }));
   }

   // -- page labels ----------------------------------------------------------

   [Theory]
   [InlineData(1,  "i")]
   [InlineData(4,  "iv")]
   [InlineData(5,  "1")]
   [InlineData(8,  "4")]
   [InlineData(9,  "A-27")]
   [InlineData(10, "A-28")]
   [InlineData(11, "AA")]
   [InlineData(12, "BB")]
   [InlineData(13, "Cover")]
   [InlineData(14, "Cover")]
   public void Labels_FollowEachStyle(int page, string expected)
   {
      var labels = new PdfPageLabels(new[]
      {
         new PdfPageLabels.Range(0,  "r", "",       1),
         new PdfPageLabels.Range(4,  "D", "",       1),
         new PdfPageLabels.Range(8,  "D", "A-",     27),
         new PdfPageLabels.Range(10, "A", "",       27),
         new PdfPageLabels.Range(12, "",  "Cover",  1),
      }, 14);

      Assert.Equal(expected, labels.Get(page));
   }

   [Fact]
   public void Labels_DescribeSkipsRunsMatchingThePageNumber()
   {
      var labels = new PdfPageLabels(new[]
      {
         new PdfPageLabels.Range(0, "D", "", 1),
         new PdfPageLabels.Range(3, "r", "", 1),
      }, 5);

      Assert.Equal("4-5 = i-ii", labels.Describe());
   }

   [Fact]
   public void Labels_ReadThroughIndirectReferencesAndKids()
   {
      using var doc = PdfDocument.Open(TestPdf.IndirectLabels());

      var labels = PdfPageLabels.TryRead(doc);

      Assert.NotNull(labels);
      Assert.Equal("App-C", labels!.Get(1));
   }

   // -- info -----------------------------------------------------------------

   [Fact]
   public async Task Info_IsTheDefaultAndMapsTheDocument()
   {
      var res = await _Run(new { path = "sample.pdf" });

      Assert.False(res.IsError, res.Content);
      Assert.Null(res.Images);
      Assert.Contains("4 pages", res.Content);
      Assert.Contains("Title: Combo Test", res.Content);
      Assert.Contains("Author: Scratch", res.Content);
      Assert.Contains("1-2 595×842, 3 842×595, 4 595×842", res.Content);   // rotation applied
      Assert.Contains("Printed labels: 1 = i, 2-4 = 1-3", res.Content);
      Assert.Contains("Text layer: on 3 of 4 pages", res.Content);
      Assert.Contains("Overview -> p.1", res.Content);
      Assert.Contains("Pinout -> p.2", res.Content);
   }

   [Fact]
   public async Task Info_SaysWhenThereIsNoOutlineOrLabels()
   {
      File.WriteAllBytes(_tree.InProject("plain.pdf"), TestPdf.Pages("Only page"));

      var res = await _Run(new { path = "plain.pdf", mode = "info" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("1 page,", res.Content);
      Assert.Contains("Outline: none", res.Content);
      Assert.DoesNotContain("Printed labels", res.Content);
   }

   // -- search ---------------------------------------------------------------

   [Fact]
   public async Task Search_NamesPageAndLabelCaseInsensitively()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "search", query = "lqfp64" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("1 match on 1 of 4 searched pages", res.Content);
      Assert.Contains("page 2 (label 1): 1 match", res.Content);
      Assert.Contains("Pinout LQFP64", res.Content);
   }

   [Fact]
   public async Task Search_MatchesAcrossLineBreaks()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "search", query = "over   two" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("page 2", res.Content);
   }

   [Fact]
   public async Task Search_FlagsPagesItCannotSee()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "search", query = "nothing like this" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("0 matches on 0 of 4", res.Content);
      Assert.Contains("1 searched page(s) have no text layer", res.Content);
   }

   [Fact]
   public async Task Search_RespectsPages()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "search", query = "page", pages = "3" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("of 1 searched pages", res.Content);
      Assert.DoesNotContain("Cover", res.Content);
   }

   [Fact]
   public async Task Search_NeedsQuery()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "search" });

      Assert.True(res.IsError);
      Assert.Contains("query", res.Content);
   }

   // -- text -----------------------------------------------------------------

   [Fact]
   public async Task Text_HeadsEachPageWithItsNumberAndLabel()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "text", pages = "1-2" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("--- page 1 (label i) ---", res.Content);
      Assert.Contains("Cover page", res.Content);
      Assert.Contains("--- page 2 (label 1) ---", res.Content);
      Assert.DoesNotContain("Rotated", res.Content);
   }

   [Fact]
   public async Task Text_TakesPagesAsANumber()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "text", pages = 3 });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("Rotated page", res.Content);
      Assert.DoesNotContain("Cover", res.Content);
   }

   [Fact]
   public async Task Text_SendsAScannedPageToView()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "text", pages = "4" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("no text layer", res.Content);
      Assert.Contains("view", res.Content);
   }

   [Fact]
   public async Task Text_StopsOnAPageBoundaryAndSaysWhereToResume()
   {
      var filler = new string('x', 12_000);
      File.WriteAllBytes(_tree.InProject("long.pdf"), TestPdf.Pages(filler, filler, filler, filler, filler));

      var res = await _Run(new { path = "long.pdf", mode = "text" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("--- page 2 ---", res.Content);
      Assert.DoesNotContain("--- page 3 ---", res.Content);
      Assert.Contains("Continue with pages=\"3-5\"", res.Content);
   }

   [Fact]
   public async Task Text_CutsAPageTooLongForTheWholeBudget()
   {
      File.WriteAllBytes(_tree.InProject("huge.pdf"), TestPdf.Pages(new string('x', 40_000)));

      var res = await _Run(new { path = "huge.pdf", mode = "text" });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("page cut at", res.Content);
      Assert.True(res.Content.Length < 31_000, res.Content.Length.ToString());
   }

   [Fact]
   public async Task Text_ColumnsLayoutKeepsAColumnTogether()
   {
      File.WriteAllBytes(_tree.InProject("cols.pdf"), TestPdf.TwoColumns(
         new[] { "Left first line", "Left second line", "Left third line" },
         new[] { "Right first line", "Right second line", "Right third line" }));

      var lines   = await _Run(new { path = "cols.pdf", mode = "text" });
      var columns = await _Run(new { path = "cols.pdf", mode = "text", layout = "columns" });

      Assert.False(lines.IsError, lines.Content);
      Assert.False(columns.IsError, columns.Content);
      // Rows read across by default, so a table row stays one line...
      Assert.True(lines.Content.IndexOf("Right first") < lines.Content.IndexOf("Left second"), lines.Content);
      // ...and the columns layout finishes one column before the next.
      Assert.True(columns.Content.IndexOf("Left third") < columns.Content.IndexOf("Right first"), columns.Content);
   }

   [Fact]
   public async Task Text_RejectsUnknownLayout()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "text", layout = "fancy" });

      Assert.True(res.IsError);
   }

   // -- view -----------------------------------------------------------------

   [Fact]
   public async Task View_DefaultsToPageOneWithinTheBudget()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "view" });

      Assert.False(res.IsError, res.Content);
      var image = Assert.Single(res.Images!);
      Assert.Equal("image/png", image.MediaType);
      Assert.True((long)image.Width * image.Height <= ImageIngest.MAX_PIXELS);
      Assert.True((long)image.Width * image.Height > ImageIngest.MAX_PIXELS * 9 / 10);
      Assert.Equal("sample.pdf p.1", image.FileName);
      Assert.Contains("4 pages", res.Content);
      Assert.Contains("page 1 (label i): 595×842 points", res.Content);
   }

   [Fact]
   public async Task View_ReturnsPagesInTheAskedOrder()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "view", pages = "3,1" });

      Assert.False(res.IsError, res.Content);
      Assert.Equal(new[] { "sample.pdf p.3", "sample.pdf p.1" }, res.Images!.Select(i => i.FileName));
      Assert.True(res.Content.IndexOf("page 3") < res.Content.IndexOf("page 1 "), res.Content);
   }

   [Fact]
   public async Task View_RefusesTooManyPages()
   {
      File.WriteAllBytes(_tree.InProject("five.pdf"), TestPdf.Pages("a", "b", "c", "d", "e"));

      var res = await _Run(new { path = "five.pdf", mode = "view", pages = "1-5" });

      Assert.True(res.IsError);
      Assert.Null(res.Images);
   }

   [Fact]
   public async Task View_HonoursMaxPixels()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "view", max_pixels = 40_000 });

      Assert.False(res.IsError, res.Content);
      var image = Assert.Single(res.Images!);
      Assert.True((long)image.Width * image.Height <= 40_000, image.Width + "x" + image.Height);
   }

   [Fact]
   public async Task View_CropIsInPointsAndKeepsItsShape()
   {
      // A 2:1 region around the red square, leaving a 10 point margin left and top.
      var res = await _Run(new
      {
         path = "sample.pdf", mode = "view", pages = 2,
         crop_x = 90, crop_y = 90, crop_width = 240, crop_height = 120, max_pixels = 500_000,
      });

      Assert.False(res.IsError, res.Content);
      var image = Assert.Single(res.Images!);
      Assert.InRange((double)image.Width / image.Height, 1.98, 2.02);
      Assert.True(image.Width > 240 * 2, "a crop gets the budget, not the page's scale");

      using var bitmap = _Decode(image);
      double scale = image.Width / 240.0;
      Assert.True(_IsRed(bitmap, (int)(60 * scale), (int)(60 * scale)), "inside the square");
      Assert.False(_IsRed(bitmap, (int)(5 * scale), (int)(60 * scale)), "left of the square");
      Assert.False(_IsRed(bitmap, (int)(115 * scale), (int)(60 * scale)), "right of the square");
      Assert.False(_IsRed(bitmap, (int)(60 * scale), (int)(115 * scale)), "below the square");
   }

   [Fact]
   public async Task View_CropFollowsPageRotation()
   {
      // Page 3 is /Rotate 90: displayed 842×595, its square at x 642-742, y 100-200.
      var res = await _Run(new
      {
         path = "sample.pdf", mode = "view", pages = 3,
         crop_x = 630, crop_y = 90, crop_width = 120, crop_height = 120, max_pixels = 90_000,
      });

      Assert.False(res.IsError, res.Content);
      var image = Assert.Single(res.Images!);
      using var bitmap = _Decode(image);
      double scale = image.Width / 120.0;
      Assert.True(_IsRed(bitmap, (int)(60 * scale), (int)(60 * scale)), "inside the square");
      Assert.False(_IsRed(bitmap, (int)(5 * scale), (int)(60 * scale)), "left of the square");
      Assert.False(_IsRed(bitmap, (int)(60 * scale), (int)(5 * scale)), "above the square");
   }

   [Fact]
   public async Task View_CapsTheZoomOfATinyCrop()
   {
      var res = await _Run(new
      {
         path = "sample.pdf", mode = "view", pages = 2,
         crop_x = 100, crop_y = 100, crop_width = 10, crop_height = 10,
      });

      Assert.False(res.IsError, res.Content);
      var image = Assert.Single(res.Images!);
      Assert.InRange(image.Width, 40, 42);     // 300 dpi: 10 points are about 41 pixels
   }

   [Fact]
   public async Task View_ClampsACropToThePageAndSaysSo()
   {
      var res = await _Run(new
      {
         path = "sample.pdf", mode = "view", pages = 1,
         crop_x = 500, crop_y = 0, crop_width = 500, crop_height = 100,
      });

      Assert.False(res.IsError, res.Content);
      Assert.Contains("width=95", res.Content);
      Assert.Contains("clamped", res.Content);
   }

   [Theory]
   [InlineData(1000, 0, 10, 10)]     // outside the page
   [InlineData(-1,   0, 10, 10)]
   [InlineData(0,    0, 0,  10)]
   public async Task View_RejectsBadCrops(int x, int y, int w, int h)
   {
      var res = await _Run(new { path = "sample.pdf", mode = "view", crop_x = x, crop_y = y, crop_width = w, crop_height = h });

      Assert.True(res.IsError);
      Assert.Null(res.Images);
   }

   [Fact]
   public async Task View_RejectsAPartialCrop()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "view", crop_x = 10 });

      Assert.True(res.IsError);
      Assert.Contains("all of", res.Content);
   }

   [Fact]
   public async Task View_RejectsACropAcrossPages()
   {
      var res = await _Run(new
      {
         path = "sample.pdf", mode = "view", pages = "1-2",
         crop_x = 0, crop_y = 0, crop_width = 10, crop_height = 10,
      });

      Assert.True(res.IsError);
      Assert.Contains("one page", res.Content);
   }

   // -- bad input ------------------------------------------------------------

   [Theory]
   [InlineData("text")]
   [InlineData("search")]
   [InlineData("view")]
   public async Task PageBeyondTheDocument_IsAnError(string mode)
   {
      var res = await _Run(new { path = "sample.pdf", mode, pages = "9", query = "x" });

      Assert.True(res.IsError);
      Assert.Contains("4 page", res.Content);
   }

   [Theory]
   [InlineData("info")]
   [InlineData("text")]
   [InlineData("search")]
   [InlineData("view")]
   public async Task CorruptFile_IsAnErrorNotACrash(string mode)
   {
      File.WriteAllBytes(_tree.InProject("bad.pdf"), Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a pdf at all"));

      var res = await _Run(new { path = "bad.pdf", mode, query = "x" });

      Assert.True(res.IsError);
   }

   [Fact]
   public async Task TruncatedFile_IsAnErrorNotACrash()
   {
      var bytes = TestPdf.Sample();
      File.WriteAllBytes(_tree.InProject("cut.pdf"), bytes.Take(bytes.Length / 2).ToArray());

      var res = await _Run(new { path = "cut.pdf", mode = "view" });

      Assert.True(res.IsError);
   }

   [Fact]
   public async Task RejectsOtherFileTypes()
   {
      _tree.WriteProjectFile("notes.txt", "hello");

      var res = await _Run(new { path = "notes.txt" });

      Assert.True(res.IsError);
      Assert.Contains(".pdf", res.Content);
   }

   [Fact]
   public async Task RejectsAnUnknownMode()
   {
      var res = await _Run(new { path = "sample.pdf", mode = "summarize" });

      Assert.True(res.IsError);
   }

   [Fact]
   public async Task CancelledSearch_StopsWithAnError()
   {
      using var cts = new CancellationTokenSource();
      cts.Cancel();
      var ctx  = new ToolContext(_tree.Project, cts.Token);
      var args = JsonSerializer.SerializeToElement(new { path = "sample.pdf", mode = "search", query = "page" });

      var res = await new ReadPdfTool().ExecuteAsync(args, ctx);

      Assert.True(res.IsError);
      Assert.Contains("cancelled", res.Content);
   }

   // -- neighbours -----------------------------------------------------------

   [Fact]
   public async Task OtherReadTools_PointAPdfHere()
   {
      foreach (ITool tool in new ITool[] { new ReadImageTool(), new ReadFileTool() })
      {
         var args = JsonSerializer.SerializeToElement(new { path = "sample.pdf" });

         var res = await tool.ExecuteAsync(args, _ctx);

         Assert.True(res.IsError, tool.Name);
         Assert.Contains("read_pdf", res.Content);
      }
   }

   [Fact]
   public void ExplorationDefaults_IncludeIt()
   {
      Assert.True(ToolRegistry.CreateExplorationDefaults().TryGet("read_pdf", out _));
   }

   [Fact]
   public void ArgsPreview_ShowsWhatTheCallAsksFor()
   {
      var args = JsonSerializer.SerializeToElement(new
      {
         path = "a.pdf", mode = "view", pages = "3", crop_x = 1, crop_y = 2, crop_width = 3, crop_height = 4,
      });

      var preview = new ReadPdfTool().ArgsPreview(args);

      Assert.Contains("a.pdf", preview);
      Assert.Contains("mode=view", preview);
      Assert.Contains("pages=3", preview);
      Assert.Contains("crop=1,2 3×4", preview);
   }

   // -- helpers --------------------------------------------------------------

   private Task<ToolResult> _Run(object args) =>
      new ReadPdfTool().ExecuteAsync(JsonSerializer.SerializeToElement(args), _ctx);

   private static SKBitmap _Decode(ImagePart image) =>
      SKBitmap.Decode(Convert.FromBase64String(image.Base64));

   private static bool _IsRed(SKBitmap bitmap, int x, int y)
   {
      var c = bitmap.GetPixel(x, y);
      return c.Red > 200 && c.Green < 60 && c.Blue < 60;
   }

   // -- fields ---------------------------------------------------------------

   private readonly TempTree    _tree;
   private readonly ToolContext _ctx;
}
