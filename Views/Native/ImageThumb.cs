// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using Avalonia.Media.Imaging;
using SlimCodeDesktop.Agents.Native;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Decoding helpers shared by the pending-attachment strip and the user
/// bubble. Both render the same <see cref="ImagePart"/> bytes, and both must
/// survive a corrupt payload without taking the chat down.
/// </summary>
internal static class ImageThumb
{
   /// <summary>
   /// Decode an attachment for display, or null if the payload is unusable.
   /// Decoded at full size — <see cref="ImageIngest.MAX_PIXELS"/> already
   /// caps that at a megapixel, so a thumbnail-specific decode would save
   /// little and cost an API the two call sites don't otherwise need.
   /// </summary>
   public static Bitmap? Decode(ImagePart? img)
   {
      if (img == null || string.IsNullOrEmpty(img.Base64)) return null;
      try
      {
         var bytes = Convert.FromBase64String(img.Base64);
         using var ms = new MemoryStream(bytes);
         return new Bitmap(ms);
      }
      catch
      {
         return null;
      }
   }

   /// <summary>Short "name — 1280x720" label for a tooltip / caption.</summary>
   public static string Caption(ImagePart img)
   {
      var size = img.Width + "x" + img.Height;
      return string.IsNullOrEmpty(img.FileName) ? size : img.FileName + " — " + size;
   }
}
