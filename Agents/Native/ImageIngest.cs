// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using SkiaSharp;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// Turns a dropped file (or raw bytes) into an <see cref="ImagePart"/> ready
/// for the wire: decoded, downscaled to <see cref="MAX_PIXELS"/>, re-encoded.
///
/// Downscaling is not an optimization, it is the feature: a 4K screenshot
/// costs several thousand prompt tokens and a vision encoder generally sees
/// no more detail than it does at a megapixel. The cap is a constant on
/// purpose — if a real case ever needs a different ceiling it can become a
/// setting, but guessing at one now would just be another knob nobody turns.
///
/// Re-encoding format follows the source: JPEG in stays JPEG (re-compressing
/// a photo as PNG multiplies its size), anything else comes out PNG so
/// screenshots and diagrams stay lossless and crisp at the text they contain.
/// </summary>
public static class ImageIngest
{
   /// <summary>Pixel ceiling after downscaling. Aspect ratio is preserved.</summary>
   public const long MAX_PIXELS = 1_000_000;

   /// <summary>Quality used when re-encoding a JPEG source.</summary>
   public const int JPEG_QUALITY = 88;

   /// <summary>
   /// Refuse absurd source files before decoding them — a decoded frame costs
   /// width*height*4 bytes of RAM regardless of how well it compressed.
   /// </summary>
   public const long MAX_SOURCE_BYTES = 32L * 1024 * 1024;

   /// <summary>
   /// Extensions accepted from a drop. Skia decodes all of these; the list
   /// exists so dropping a folder of mixed files silently picks the images
   /// rather than erroring on each .txt.
   /// </summary>
   public static readonly string[] SUPPORTED_EXTENSIONS =
      { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

   /// <summary>True when the path's extension is one we attempt to decode.</summary>
   public static bool IsSupportedFile(string? path)
   {
      if (string.IsNullOrWhiteSpace(path)) return false;
      var ext = Path.GetExtension(path);
      if (string.IsNullOrEmpty(ext)) return false;
      foreach (var candidate in SUPPORTED_EXTENSIONS)
      {
         if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
            return true;
      }
      return false;
   }

   /// <summary>
   /// Load and normalize an image file. Returns false with a short
   /// user-facing reason in <paramref name="error"/> on any failure —
   /// a bad drop must never take the chat down.
   /// </summary>
   public static bool TryLoadFile(string path, out ImagePart? image, out string? error)
   {
      image = null;
      error = null;
      try
      {
         var info = new FileInfo(path);
         if (!info.Exists)
         {
            error = "file not found";
            return false;
         }
         if (info.Length > MAX_SOURCE_BYTES)
         {
            error = "file is larger than " + (MAX_SOURCE_BYTES / (1024 * 1024)) + " MB";
            return false;
         }

         var bytes = File.ReadAllBytes(path);
         return TryLoadBytes(bytes, Path.GetFileName(path), _IsJpegPath(path), out image, out error);
      }
      catch (Exception ex)
      {
         error = ex.Message;
         return false;
      }
   }

   /// <summary>
   /// Normalize already-loaded bytes. <paramref name="preferJpeg"/> selects
   /// the output encoding (see the class remarks).
   /// </summary>
   public static bool TryLoadBytes(byte[] bytes, string? fileName, bool preferJpeg,
                                   out ImagePart? image, out string? error)
      => TryLoadBytes(bytes, fileName, preferJpeg, null, out image, out error);

   /// <summary>
   /// As above, but keeping only <paramref name="crop"/> (in source-image
   /// pixels; clamped to the image, ignored when it covers everything).
   ///
   /// Order matters: the crop runs BEFORE the megapixel cap, so the region the
   /// user kept gets the whole pixel budget to itself. Cropping afterwards
   /// would hand the model a magnified piece of an already-shrunk frame — the
   /// difference between reading the code in a 4K screenshot and not.
   /// </summary>
   public static bool TryLoadBytes(byte[] bytes, string? fileName, bool preferJpeg, SKRectI? crop,
                                   out ImagePart? image, out string? error)
      => TryLoadBytes(bytes, fileName, preferJpeg, crop, MAX_PIXELS, out image, out error);

   /// <summary>
   /// As above, with an explicit pixel budget instead of <see cref="MAX_PIXELS"/>.
   /// Exists for the read_image tool, where the model picks the trade-off
   /// between detail and context cost itself.
   /// </summary>
   public static bool TryLoadBytes(byte[] bytes, string? fileName, bool preferJpeg, SKRectI? crop,
                                   long maxPixels, out ImagePart? image, out string? error)
   {
      image = null;
      error = null;
      if (bytes == null || bytes.Length == 0)
      {
         error = "empty file";
         return false;
      }

      try
      {
         using var decoded = SKBitmap.Decode(bytes);
         if (decoded == null)
         {
            error = "not a readable image";
            return false;
         }

         using var cropped = _Crop(decoded, crop);
         var full = cropped ?? decoded;

         using var scaled = _Downscale(full, maxPixels);
         var source = scaled ?? full;

         var format  = preferJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
         int quality = preferJpeg ? JPEG_QUALITY : 100;

         using var skImage = SKImage.FromBitmap(source);
         using var data    = skImage?.Encode(format, quality);
         if (data == null)
         {
            error = "could not re-encode the image";
            return false;
         }

         image = new ImagePart
         {
            MediaType = preferJpeg ? "image/jpeg" : "image/png",
            Base64    = Convert.ToBase64String(data.ToArray()),
            Width     = source.Width,
            Height    = source.Height,
            FileName  = fileName,
         };
         return true;
      }
      catch (Exception ex)
      {
         error = ex.Message;
         return false;
      }
   }

   // -- private --------------------------------------------------------------

   // Null whenever the whole image is being kept -- including a degenerate or
   // off-image rectangle, which is treated as "no crop" rather than as an
   // error: the alternative is failing an attachment over a stray click.
   //
   // ExtractSubset shares pixel memory with the source, so the returned bitmap
   // is only valid while `src` is alive. Both live to the end of the caller's
   // try block, which is where the encode happens.
   private static SKBitmap? _Crop(SKBitmap src, SKRectI? crop)
   {
      if (crop == null) return null;

      var rect = SKRectI.Intersect(crop.Value, new SKRectI(0, 0, src.Width, src.Height));
      if (rect.Width <= 0 || rect.Height <= 0) return null;
      if (rect.Width == src.Width && rect.Height == src.Height) return null;

      var dst = new SKBitmap();
      if (src.ExtractSubset(dst, rect))
         return dst;

      dst.Dispose();
      return null;
   }

   // Null when the image is already within budget — the caller then encodes
   // the original bitmap rather than a needless copy.
   private static SKBitmap? _Downscale(SKBitmap src, long maxPixels)
   {
      long pixels = (long)src.Width * src.Height;
      if (pixels <= maxPixels || maxPixels <= 0 || src.Width <= 0 || src.Height <= 0)
         return null;

      double factor = Math.Sqrt((double)maxPixels / pixels);
      int w = Math.Max(1, (int)Math.Round(src.Width  * factor));
      int h = Math.Max(1, (int)Math.Round(src.Height * factor));

      var info     = new SKImageInfo(w, h, src.ColorType, src.AlphaType);
      var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
      return src.Resize(info, sampling);
   }

   /// <summary>
   /// Read size and encoding from the file header without decoding pixels.
   /// Returns false with a short reason when the bytes aren't an image Skia
   /// understands.
   /// </summary>
   public static bool TryProbe(byte[] bytes, out int width, out int height, out string format,
                               out string? error)
   {
      width  = 0;
      height = 0;
      format = "";
      error  = null;
      try
      {
         using var data  = SKData.CreateCopy(bytes);
         using var codec = SKCodec.Create(data);
         if (codec == null)
         {
            error = "not a readable image";
            return false;
         }
         width  = codec.Info.Width;
         height = codec.Info.Height;
         format = codec.EncodedFormat.ToString().ToUpperInvariant();
         return true;
      }
      catch (Exception ex)
      {
         error = ex.Message;
         return false;
      }
   }

   /// <summary>True for .jpg / .jpeg — the sources that stay JPEG when re-encoded.</summary>
   public static bool IsJpegPath(string path) => _IsJpegPath(path);

   private static bool _IsJpegPath(string path)
   {
      var ext = Path.GetExtension(path);
      return string.Equals(ext, ".jpg",  StringComparison.OrdinalIgnoreCase)
          || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);
   }
}
