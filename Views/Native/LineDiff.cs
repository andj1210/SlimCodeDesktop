// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Minimal line-based LCS diff, used by <see cref="ToolCallCard"/> to render
/// edit_file's old_string -> new_string as removed/added lines.
/// </summary>
internal static class LineDiff
{
   internal enum Op { Context, Removed, Added }

   /// <summary>
   /// Diff two texts line-wise. Inputs too large for the quadratic LCS table
   /// degrade to "all removed, then all added" instead of stalling the UI.
   /// </summary>
   internal static List<(Op Op, string Line)> Compute(string oldText, string newText)
   {
      var a = SplitLines(oldText);
      var b = SplitLines(newText);
      var result = new List<(Op, string)>(a.Length + b.Length);

      if ((long)a.Length * b.Length > 1_000_000)
      {
         foreach (var l in a) result.Add((Op.Removed, l));
         foreach (var l in b) result.Add((Op.Added, l));
         return result;
      }

      var lcs = new int[a.Length + 1, b.Length + 1];
      for (int i = a.Length - 1; i >= 0; i--)
      {
         for (int j = b.Length - 1; j >= 0; j--)
         {
            lcs[i, j] = a[i] == b[j]
               ? lcs[i + 1, j + 1] + 1
               : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
         }
      }

      int x = 0, y = 0;
      while (x < a.Length && y < b.Length)
      {
         if (a[x] == b[y])
         {
            result.Add((Op.Context, a[x]));
            x++; y++;
         }
         else if (lcs[x + 1, y] >= lcs[x, y + 1])
         {
            result.Add((Op.Removed, a[x]));
            x++;
         }
         else
         {
            result.Add((Op.Added, b[y]));
            y++;
         }
      }
      while (x < a.Length) { result.Add((Op.Removed, a[x])); x++; }
      while (y < b.Length) { result.Add((Op.Added,   b[y])); y++; }
      return result;
   }

   private static string[] SplitLines(string s)
   {
      if (string.IsNullOrEmpty(s))
         return Array.Empty<string>();
      return s.Replace("\r\n", "\n").Split('\n');
   }
}
