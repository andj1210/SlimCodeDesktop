// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SlimCodeDesktop.Tests;

/// <summary>
/// Hand-assembled PDFs, so a test can state exactly what is in the file: page
/// rotation, labels, outline, a page with no text layer. No PDF writer library
/// would produce all of those on request.
/// </summary>
internal static class TestPdf
{
   /// <summary>
   /// Four A4 pages. Printed labels i, 1, 2, 3 and an outline pointing at pages
   /// 1 and 2. Pages 2-4 carry a red square at <see cref="RED_X"/>,
   /// <see cref="RED_Y"/>, side <see cref="RED_SIDE"/>, measured from the
   /// top-left of the unrotated page.
   /// <code>
   ///   1  "Cover page"
   ///   2  "Pinout LQFP64", then "split over" / "two lines" on separate lines
   ///   3  "Rotated page", /Rotate 90
   ///   4  no text at all
   /// </code>
   /// </summary>
   public static byte[] Sample() => _Build(new List<string>
   {
      "<< /Type /Catalog /Pages 3 0 R /Outlines 13 0 R /PageLabels << /Nums [0 << /S /r >> 1 << /S /D >>] >> >>",
      "<< /Title (Combo Test) /Author (Scratch) >>",
      "<< /Type /Pages /Count 4 /Kids [4 0 R 5 0 R 6 0 R 7 0 R] >>",
      _Page(8, 12), _Page(9, 12), _Page(10, 12, " /Rotate 90"), _Page(11, 12),
      _Stream(_Text("Cover page")),
      _Stream(_Text("Pinout LQFP64") + "\n" + _Text("split over", 740) + "\n" + _Text("two lines", 720) + "\n" + RED),
      _Stream(_Text("Rotated page") + "\n" + RED),
      _Stream(RED),
      FONT,
      "<< /Type /Outlines /First 14 0 R /Last 15 0 R /Count 2 >>",
      "<< /Title (Overview) /Parent 13 0 R /Next 15 0 R /Dest [4 0 R /Fit] >>",
      "<< /Title (Pinout) /Parent 13 0 R /Prev 14 0 R /Dest [5 0 R /Fit] >>",
   });

   /// <summary>The given pages of text, one string per page. No labels, no outline.</summary>
   public static byte[] Pages(params string[] pageTexts) =>
      _Plain(pageTexts.Select(t => _Stream(_Text(t))).ToArray());

   /// <summary>One page with two columns of lines side by side.</summary>
   public static byte[] TwoColumns(string[] left, string[] right)
   {
      var content = new StringBuilder();
      for (int i = 0; i < left.Length; i++)
         content.Append(_Text(left[i], 780 - i * 16, 50)).Append('\n').Append(_Text(right[i], 780 - i * 16, 320)).Append('\n');
      return _Plain(_Stream(content.ToString()));
   }

   /// <summary>One page whose labels sit behind an indirect reference and a /Kids level.</summary>
   public static byte[] IndirectLabels() => _Build(new List<string>
   {
      "<< /Type /Catalog /Pages 3 0 R /PageLabels 7 0 R >>",
      "<< /Title (Labels) >>",
      "<< /Type /Pages /Count 1 /Kids [4 0 R] >>",
      _Page(5, 6),
      _Stream(_Text("Only page")),
      FONT,
      "<< /Kids [8 0 R] >>",
      "<< /Nums [0 9 0 R] >>",
      "<< /S /A /P (App-) /St 3 >>",
   });

   public const float RED_X    = 100;
   public const float RED_Y    = 100;
   public const float RED_SIDE = 100;

   // -- private --------------------------------------------------------------

   private static byte[] _Plain(params string[] streams)
   {
      int fontObj = 4 + 2 * streams.Length;
      var objs = new List<string> { "<< /Type /Catalog /Pages 3 0 R >>", "<< /Title (Plain) >>" };
      var kids = string.Join(" ", streams.Select((_, i) => (4 + 2 * i) + " 0 R"));
      objs.Add("<< /Type /Pages /Count " + streams.Length + " /Kids [" + kids + "] >>");
      for (int i = 0; i < streams.Length; i++)
      {
         objs.Add(_Page(5 + 2 * i, fontObj));
         objs.Add(streams[i]);
      }
      objs.Add(FONT);
      return _Build(objs);
   }

   private static byte[] _Build(List<string> objs)
   {
      var sb = new StringBuilder("%PDF-1.7\n");
      var offsets = new List<int>();
      for (int i = 0; i < objs.Count; i++)
      {
         offsets.Add(sb.Length);
         sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
      }
      int xref = sb.Length;
      sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
      foreach (var o in offsets)
         sb.Append(o.ToString("D10")).Append(" 00000 n \n");
      sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R /Info 2 0 R >>\nstartxref\n")
        .Append(xref).Append("\n%%EOF\n");
      return Encoding.Latin1.GetBytes(sb.ToString());
   }

   private static string _Page(int contentObj, int fontObj, string extra = "") =>
      "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 595 842]" + extra + " /Contents " + contentObj +
      " 0 R /Resources << /Font << /F1 " + fontObj + " 0 R >> >> >>";

   private static string _Stream(string content) =>
      "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";

   private static string _Text(string text, int y = 780, int x = 50) =>
      "BT /F1 14 Tf " + x + " " + y + " Td (" + text + ") Tj ET";

   private const string FONT = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
   private const string RED  = "1 0 0 rg 100 642 100 100 re f";
}
