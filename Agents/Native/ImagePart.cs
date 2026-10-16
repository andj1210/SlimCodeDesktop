// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// One image attached to a chat message. Held decoded-and-normalized: the
/// bytes here are what actually goes on the wire and into the transcript,
/// already downscaled and re-encoded by <see cref="ImageIngest"/> — never
/// the untouched file the user dropped.
///
/// Base64 rather than a byte[] because that is the form both consumers need
/// (the data: URL in the request, and the JSONL record), and re-encoding a
/// multi-megabyte buffer on every turn would be pure waste.
/// </summary>
public sealed class ImagePart
{
   /// <summary>MIME type of <see cref="Base64"/>: "image/png" or "image/jpeg".</summary>
   public string MediaType { get; set; } = "image/png";

   /// <summary>Base64 of the encoded image, without any data-URL prefix.</summary>
   public string Base64 { get; set; } = "";

   /// <summary>Pixel size after downscaling. Display only.</summary>
   public int Width { get; set; }
   public int Height { get; set; }

   /// <summary>Source file name, when it came from one. Display only.</summary>
   public string? FileName { get; set; }

   /// <summary>
   /// The <c>data:</c> URL form expected by the OpenAI <c>image_url</c>
   /// content part. Local servers (llama.cpp with an <c>--mmproj</c>) and
   /// hosted providers both accept it, which is why nothing here ever
   /// uploads to a separate media endpoint.
   /// </summary>
   public string ToDataUrl() => "data:" + MediaType + ";base64," + Base64;
}
