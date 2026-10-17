// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

// Renders the app icon SVGs in assets/icon/ into the raster files the app and
// the build use. Run after changing an SVG, then commit the outputs:
//
//     dotnet run --file tools/make-icons.cs
//
// Outputs (all in assets/icon/):
//   slimcode.ico           Windows exe + window icon: 16/24 px from
//                          slimcode-small.svg, 32..256 px from slimcode.svg
//   slimcode-256.png       window icon on Linux, About page
//   slimcode-small-32.png  custom title bar (drawn at 16 px, crisp on HiDPI)

#:package Svg.Skia@5.2.3

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Svg.Skia;

var iconDir  = Path.GetFullPath(Path.Combine(_ScriptDir(), "..", "assets", "icon"));
var robotSvg = Path.Combine(iconDir, "slimcode.svg");
var smallSvg = Path.Combine(iconDir, "slimcode-small.svg");

// ICO frames: the robot is unreadable below 32 px, so the plain </> covers those.
var frames = new List<(int Size, byte[] Png)>();
foreach (var size in new[] { 16, 24 })
   frames.Add((size, Render(smallSvg, size)));
foreach (var size in new[] { 32, 48, 64, 128, 256 })
   frames.Add((size, Render(robotSvg, size)));

WriteIco(Path.Combine(iconDir, "slimcode.ico"), frames);
File.WriteAllBytes(Path.Combine(iconDir, "slimcode-256.png"), Render(robotSvg, 256));
File.WriteAllBytes(Path.Combine(iconDir, "slimcode-small-32.png"), Render(smallSvg, 32));

Console.WriteLine("Icons written to " + iconDir);

// -- helpers ------------------------------------------------------------------

// Rasterizes an SVG into a size x size PNG, scaling its viewBox to fill it.
static byte[] Render(string svgPath, int size)
{
   using var svg = new SKSvg();
   var picture = svg.Load(svgPath) ?? throw new InvalidOperationException("Cannot load " + svgPath);

   var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
   using var surface = SKSurface.Create(info);
   var canvas = surface.Canvas;
   canvas.Clear(SKColors.Transparent);

   var bounds = picture.CullRect;
   canvas.Scale(size / bounds.Width, size / bounds.Height);
   canvas.Translate(-bounds.Left, -bounds.Top);
   canvas.DrawPicture(picture);

   using var image = surface.Snapshot();
   using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
   return data.ToArray();
}

// ICO container with PNG-compressed frames (supported since Windows Vista).
static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
   using var fs = File.Create(path);
   using var w  = new BinaryWriter(fs);

   w.Write((ushort)0);              // reserved
   w.Write((ushort)1);              // type: icon
   w.Write((ushort)frames.Count);

   int offset = 6 + 16 * frames.Count;
   foreach (var (size, png) in frames)
   {
      w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
      w.Write((byte)(size >= 256 ? 0 : size));
      w.Write((byte)0);             // palette colors
      w.Write((byte)0);             // reserved
      w.Write((ushort)1);           // color planes
      w.Write((ushort)32);          // bits per pixel
      w.Write(png.Length);
      w.Write(offset);
      offset += png.Length;
   }
   foreach (var (_, png) in frames)
      w.Write(png);
}

static string _ScriptDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
   Path.GetDirectoryName(path)!;
