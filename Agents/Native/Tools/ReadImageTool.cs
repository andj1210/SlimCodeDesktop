// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SkiaSharp;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Look at an image file from the project. Two modes:
///   - "info": size and format from the file header, no pixels sent. Cheap,
///     and what the model needs to plan a crop.
///   - "view": the image itself, optionally cropped and with a chosen pixel
///     budget, delivered as an image part the vision encoder actually sees.
///
/// The pixels cannot ride in the tool result — a tool message is text-only on
/// the OpenAI schema — so they go back via <see cref="ToolResult.Images"/> and
/// the loop appends them as a user turn after the results. The text result
/// states the source size and the scale it was shown at, because the model
/// sees the shrunk image but crops in source pixels; without both numbers it
/// crops in the coordinates of what it saw and misses.
///
/// Same pipeline as a dropped or pasted image (<see cref="ImageIngest"/>):
/// crop first, then downscale, so a crop gets the whole pixel budget.
/// </summary>
public sealed class ReadImageTool : ITool
{
   public string Name => "read_image";

   public string Description =>
      "Look at an image file from the project (screenshot, diagram, UI mockup, photo). " +
      "mode='info' returns its size and format only — cheap, use it to plan a crop. " +
      "mode='view' (default) shows you the image, downscaled to max_pixels (default " +
      ImageIngest.MAX_PIXELS + "). To see detail, crop: crop_x/crop_y/crop_width/crop_height " +
      "are always in SOURCE pixels, and the cropped region gets the whole pixel budget. " +
      "Supported: " + string.Join(", ", ImageIngest.SUPPORTED_EXTENSIONS) + ".";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      var sb = new StringBuilder();
      sb.Append(_Str(args, "path") ?? "(no path)");
      var mode = _Str(args, "mode");
      if (!string.IsNullOrEmpty(mode) && mode != MODE_VIEW)
         sb.Append(", mode=").Append(mode);
      if (_TryCrop(args, out var c, out _) && c.HasValue)
         sb.Append(", crop=").Append(c.Value.Left).Append(',').Append(c.Value.Top)
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

      string mode = _Str(args, "mode") ?? MODE_VIEW;
      if (mode != MODE_VIEW && mode != MODE_INFO)
         return Task.FromResult(ToolResult.Error("'mode' must be 'info' or 'view'"));

      var res = ctx.Resolve(rel, PathAccess.Read);
      if (!res.IsAllowed)
         return Task.FromResult(ToolResult.Error("path " + res.Reason + ": " + rel));
      var abs = res.FullPath;
      if (!File.Exists(abs))
         return Task.FromResult(ToolResult.Error("file not found: " + rel));
      if (!ImageIngest.IsSupportedFile(abs))
         return Task.FromResult(ToolResult.Error(
            "not a supported image type (" + string.Join(", ", ImageIngest.SUPPORTED_EXTENSIONS) +
            "); use read_file for text"));

      try
      {
         var info = new FileInfo(abs);
         if (info.Length > ImageIngest.MAX_SOURCE_BYTES)
            return Task.FromResult(ToolResult.Error(
               "file is larger than " + (ImageIngest.MAX_SOURCE_BYTES / (1024 * 1024)) + " MB"));

         var bytes = File.ReadAllBytes(abs);
         if (!ImageIngest.TryProbe(bytes, out int srcW, out int srcH, out string format, out var probeErr))
            return Task.FromResult(ToolResult.Error(probeErr ?? "not a readable image"));

         var header = new StringBuilder();
         header.Append(rel).Append(": ").Append(srcW).Append('×').Append(srcH)
               .Append(" source pixels, ").Append(format)
               .Append(", ").Append(ToolPreview.FormatBytes(info.Length)).Append('.');

         if (mode == MODE_INFO)
            return Task.FromResult(ToolResult.Ok(header.ToString()));

         return Task.FromResult(_View(args, rel, abs, bytes, srcW, srcH, header));
      }
      catch (Exception ex)
      {
         return Task.FromResult(ToolResult.Error(ex.Message));
      }
   }

   // -- private --------------------------------------------------------------

   private static ToolResult _View(JsonElement args, string rel, string abs, byte[] bytes,
                                   int srcW, int srcH, StringBuilder text)
   {
      if (!_TryCrop(args, out var requested, out var cropErr))
         return ToolResult.Error(cropErr!);

      long maxPixels = _Int(args, "max_pixels") ?? ImageIngest.MAX_PIXELS;
      if (maxPixels < MIN_PIXELS) maxPixels = MIN_PIXELS;
      bool capped = maxPixels > MAX_PIXELS_CAP;
      if (capped) maxPixels = MAX_PIXELS_CAP;

      // Clamp here as well as in ImageIngest so the text can state the region
      // that was actually used, not the one that was asked for.
      SKRectI? crop = null;
      if (requested.HasValue)
      {
         var r = SKRectI.Intersect(requested.Value, new SKRectI(0, 0, srcW, srcH));
         if (r.Width <= 0 || r.Height <= 0)
            return ToolResult.Error("crop lies outside the image (" + srcW + "×" + srcH + " source pixels)");
         if (r.Width != srcW || r.Height != srcH)
            crop = r;
      }

      if (!ImageIngest.TryLoadBytes(bytes, Path.GetFileName(abs), ImageIngest.IsJpegPath(abs),
                                    crop, maxPixels, out var image, out var err) || image == null)
         return ToolResult.Error(err ?? "could not load the image");

      int regionW = crop?.Width  ?? srcW;
      int regionH = crop?.Height ?? srcH;
      double scale = regionW > 0 ? (double)image.Width / regionW : 1.0;

      if (crop.HasValue)
      {
         var c = crop.Value;
         text.Append(" Cropped to x=").Append(c.Left).Append(", y=").Append(c.Top)
             .Append(", width=").Append(c.Width).Append(", height=").Append(c.Height);
         if (requested.HasValue && requested.Value != c)
            text.Append(" (clamped to the image)");
         text.Append('.');
      }
      text.Append(" Shown at ").Append(image.Width).Append('×').Append(image.Height);
      text.Append(scale < 0.999
         ? " (scale " + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")."
         : " (full resolution).");
      if (capped)
         text.Append(" max_pixels was capped at ").Append(MAX_PIXELS_CAP).Append('.');
      text.Append(" Crop coordinates are always source pixels.");
      text.Append(" The image follows in the next message.");

      image.FileName = rel;
      return ToolResult.Ok(text.ToString(), new List<ImagePart> { image });
   }

   // A crop needs all four fields; any of them alone is a mistake worth naming
   // rather than silently ignoring. Returns true with a null rect when no crop
   // was asked for.
   private static bool _TryCrop(JsonElement args, out SKRectI? crop, out string? error)
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
      crop = SKRectI.Create((int)Math.Min(x.Value, int.MaxValue), (int)Math.Min(y.Value, int.MaxValue),
                            (int)Math.Min(w.Value, int.MaxValue), (int)Math.Min(h.Value, int.MaxValue));
      return true;
   }

   private static string? _Str(JsonElement args, string name) =>
      args.ValueKind == JsonValueKind.Object &&
      args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
         ? v.GetString()
         : null;

   // Accepts 12 and 12.0 alike — models are not consistent about it.
   private static long? _Int(JsonElement args, string name)
   {
      if (args.ValueKind != JsonValueKind.Object ||
          !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
         return null;
      if (v.TryGetInt64(out var l)) return l;
      return (long)Math.Round(v.GetDouble());
   }

   // -- constants ------------------------------------------------------------

   private const string MODE_VIEW = "view";
   private const string MODE_INFO = "info";

   /// <summary>Floor for max_pixels: below this nothing is legible anyway.</summary>
   private const long MIN_PIXELS = 16_384;

   /// <summary>
   /// Ceiling for max_pixels. Every image stays in history for the rest of the
   /// session, so this bounds what one call can add to the context; vision
   /// encoders rarely resolve more detail than this anyway.
   /// </summary>
   private const long MAX_PIXELS_CAP = 2_000_000;

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "path":        { "type": "string", "description": "Project-relative path to the image file." },
         "mode":        { "type": "string", "enum": ["view", "info"], "description": "'view' (default) shows the image; 'info' returns size and format only." },
         "crop_x":      { "type": "integer", "minimum": 0, "description": "Left edge of the crop, in source pixels." },
         "crop_y":      { "type": "integer", "minimum": 0, "description": "Top edge of the crop, in source pixels." },
         "crop_width":  { "type": "integer", "minimum": 1, "description": "Crop width, in source pixels." },
         "crop_height": { "type": "integer", "minimum": 1, "description": "Crop height, in source pixels." },
         "max_pixels":  { "type": "integer", "minimum": 16384, "maximum": 2000000, "description": "Pixel budget for the shown image (default 1000000, max 2000000). Aspect ratio is kept." }
      },
      "required": ["path"]
   }
   """;
}
