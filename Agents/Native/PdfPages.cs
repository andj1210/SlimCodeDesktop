// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// Page selections as read_pdf takes them: "3", "3-7", "1,4,9-11", "5-".
/// Always 1-based physical pages, never printed labels: "page 12" must not mean
/// two different pages in a document whose numbering restarts.
/// </summary>
public static class PdfPageSpec
{
   /// <summary>
   /// Parse into distinct pages, in the order given. The end of a range is
   /// clamped to the document, so "3-" and "3-9999" read to the last page; a
   /// start beyond it is an error.
   /// </summary>
   public static bool TryParse(string? spec, int pageCount, out List<int> pages, out string? error)
   {
      pages = new List<int>();
      error = null;
      if (string.IsNullOrWhiteSpace(spec))
      {
         error = "'pages' is empty";
         return false;
      }

      var seen = new HashSet<int>();
      foreach (var rawPart in spec.Split(','))
      {
         var part = rawPart.Trim();
         int dash = part.IndexOf('-');
         var startText = dash < 0 ? part : part.Substring(0, dash).Trim();
         var endText   = dash < 0 ? part : part.Substring(dash + 1).Trim();

         if (!int.TryParse(startText, out int start) ||
             !(endText.Length == 0 || int.TryParse(endText, out _)))
         {
            error = "cannot read pages '" + part + "'; use forms like 3, 3-7, 1,4,9-11 or 5-";
            return false;
         }
         int end = endText.Length == 0 ? pageCount : int.Parse(endText);

         if (start < 1)
         {
            error = "page numbers start at 1";
            return false;
         }
         if (end < start)
         {
            error = "pages '" + part + "' run backwards";
            return false;
         }
         if (start > pageCount)
         {
            error = "page " + start + " does not exist; the document has " + pageCount + " page(s)";
            return false;
         }

         for (int p = start; p <= Math.Min(end, pageCount); p++)
         {
            if (seen.Add(p))
               pages.Add(p);
         }
      }
      return true;
   }

   /// <summary>Write pages back in the same syntax, joining runs: 1,2,3,7 -> "1-3,7".</summary>
   public static string Format(IReadOnlyList<int> pages)
   {
      var sb = new StringBuilder();
      for (int i = 0; i < pages.Count; )
      {
         int last = i;
         while (last + 1 < pages.Count && pages[last + 1] == pages[last] + 1)
            last++;

         if (sb.Length > 0) sb.Append(',');
         sb.Append(pages[i]);
         if (last > i) sb.Append('-').Append(pages[last]);
         i = last + 1;
      }
      return sb.ToString();
   }
}

/// <summary>
/// The printed page numbers of a PDF (its /PageLabels tree): front matter as
/// "iv", a chapter restarting at 1. Shown next to physical page numbers so a
/// reference like "see p. 47" in the text can be mapped to a page to open.
/// PdfPig hands out the raw tree only, so the decoding is here.
/// </summary>
public sealed class PdfPageLabels
{
   /// <summary>One run of labels. StartIndex is the 0-based first page; Style is D, R, r, A, a or empty.</summary>
   public readonly record struct Range(int StartIndex, string Style, string Prefix, int Start);

   public PdfPageLabels(IEnumerable<Range> ranges, int pageCount)
   {
      _ranges    = ranges.OrderBy(r => r.StartIndex).ToList();
      _pageCount = pageCount;
   }

   /// <summary>
   /// Read the labels of an open document. Null when it has none, or when the
   /// tree is damaged: labels are a convenience and never fail a read.
   /// </summary>
   public static PdfPageLabels? TryRead(PdfDocument doc)
   {
      try
      {
         if (!doc.Structure.Catalog.CatalogDictionary.TryGet(NameToken.Create("PageLabels"), out var root))
            return null;

         var ranges = new List<Range>();
         _CollectRanges(doc, root, ranges, 0);
         return ranges.Count == 0 ? null : new PdfPageLabels(ranges, doc.NumberOfPages);
      }
      catch
      {
         return null;
      }
   }

   /// <summary>The label of a 1-based physical page. The page number itself where no range covers it.</summary>
   public string Get(int page)
   {
      int index = page - 1;
      for (int i = _ranges.Count - 1; i >= 0; i--)
      {
         var r = _ranges[i];
         if (r.StartIndex <= index)
            return r.Prefix + _FormatNumber(r.Style, r.Start + index - r.StartIndex);
      }
      return page.ToString();
   }

   /// <summary>
   /// Compact map for the runs whose labels differ from the physical numbers,
   /// e.g. "1-12 = i-xii, 13-240 = 1-228". Empty when there are none.
   /// </summary>
   public string Describe()
   {
      var parts = new List<string>();
      int omitted = 0;
      for (int i = 0; i < _ranges.Count; i++)
      {
         int first = _ranges[i].StartIndex + 1;
         int last  = i + 1 < _ranges.Count ? _ranges[i + 1].StartIndex : _pageCount;
         if (first > last || first > _pageCount || _IsIdentity(first, last))
            continue;

         if (parts.Count == MAX_DESCRIBED_RANGES)
         {
            omitted++;
            continue;
         }
         parts.Add(first == last
            ? first + " = " + Get(first)
            : first + "-" + last + " = " + Get(first) + "-" + Get(last));
      }
      if (omitted > 0)
         parts.Add("and " + omitted + " more");
      return string.Join(", ", parts);
   }

   // -- private --------------------------------------------------------------

   // A range's labels run in step, so its two ends tell whether all of it matches.
   private bool _IsIdentity(int first, int last) =>
      Get(first) == first.ToString() && Get(last) == last.ToString();

   // A number tree: /Nums pairs on the leaves, /Kids on the nodes above them.
   private static void _CollectRanges(PdfDocument doc, IToken token, List<Range> ranges, int depth)
   {
      if (depth > MAX_TREE_DEPTH || _Resolve(doc, token) is not DictionaryToken node)
         return;

      if (node.TryGet(NameToken.Create("Nums"), out var numsToken) && _Resolve(doc, numsToken) is ArrayToken nums)
      {
         for (int i = 0; i + 1 < nums.Data.Count; i += 2)
         {
            if (_Resolve(doc, nums.Data[i]) is NumericToken index &&
                _Resolve(doc, nums.Data[i + 1]) is DictionaryToken range)
            {
               ranges.Add(new Range(index.Int, _Name(doc, range, "S"), _Text(doc, range, "P"), _Number(doc, range, "St", 1)));
            }
         }
      }

      if (node.TryGet(NameToken.Create("Kids"), out var kidsToken) && _Resolve(doc, kidsToken) is ArrayToken kids)
      {
         foreach (var kid in kids.Data)
            _CollectRanges(doc, kid, ranges, depth + 1);
      }
   }

   private static IToken _Resolve(PdfDocument doc, IToken token)
   {
      for (int hop = 0; token is IndirectReferenceToken reference && hop < MAX_TREE_DEPTH; hop++)
         token = doc.Structure.GetObject(reference.Data).Data;
      return token;
   }

   private static string _Name(PdfDocument doc, DictionaryToken dict, string key) =>
      dict.TryGet(NameToken.Create(key), out var t) && _Resolve(doc, t) is NameToken name ? name.Data : "";

   private static string _Text(PdfDocument doc, DictionaryToken dict, string key)
   {
      if (!dict.TryGet(NameToken.Create(key), out var t))
         return "";
      return _Resolve(doc, t) switch
      {
         StringToken s => s.Data,
         HexToken h    => h.Data,
         _             => "",
      };
   }

   private static int _Number(PdfDocument doc, DictionaryToken dict, string key, int fallback) =>
      dict.TryGet(NameToken.Create(key), out var t) && _Resolve(doc, t) is NumericToken n ? n.Int : fallback;

   private static string _FormatNumber(string style, int value) => style switch
   {
      "D" => value.ToString(),
      "R" => _Roman(value),
      "r" => _Roman(value).ToLowerInvariant(),
      "A" => _Letters(value),
      "a" => _Letters(value).ToLowerInvariant(),
      _   => "",                                     // no style: the prefix alone
   };

   private static string _Roman(int value)
   {
      if (value < 1 || value >= 4000)
         return value.ToString();

      var sb = new StringBuilder();
      for (int i = 0; i < s_romanValues.Length; i++)
      {
         while (value >= s_romanValues[i])
         {
            sb.Append(s_romanDigits[i]);
            value -= s_romanValues[i];
         }
      }
      return sb.ToString();
   }

   // The PDF scheme: A..Z, then AA, BB, .. ZZ, then AAA.
   private static string _Letters(int value)
   {
      if (value < 1)
         return value.ToString();
      return new string((char)('A' + (value - 1) % 26), (value - 1) / 26 + 1);
   }

   // -- constants ------------------------------------------------------------

   private const int MAX_DESCRIBED_RANGES = 20;
   private const int MAX_TREE_DEPTH       = 32;

   private static readonly int[]    s_romanValues = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
   private static readonly string[] s_romanDigits = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

   // -- fields ---------------------------------------------------------------

   private readonly List<Range> _ranges;
   private readonly int         _pageCount;
}
