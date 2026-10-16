// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Views.Native;
using SkiaSharp;

// `using Avalonia;` brings the Avalonia.Platform namespace into scope, which
// makes the bare name `Platform` ambiguous with our own static class.
using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views;

/// <summary>
/// Image attachments: drag & drop, clipboard paste, the clipboard crop
/// wizard and the full-size image preview.
/// </summary>
public partial class NativeAgentView
{
   // -- image attachments ----------------------------------------------------

   private void InputBar_DragOver(object? sender, DragEventArgs e)
   {
      // Copy vs. None is the whole feedback mechanism here: the cursor tells
      // the user whether this drop will land before they release the button.
      // Contains() rather than actually fetching the bitmap: DragOver fires
      // continuously while the pointer moves.
      bool accepts = _ImagePathsFrom(e).Count > 0
                  || e.DataTransfer.Contains(DataFormat.Bitmap);

      e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
      e.Handled     = true;
   }

   private void InputBar_Drop(object? sender, DragEventArgs e)
   {
      e.Handled = true;

      var paths = _ImagePathsFrom(e);
      if (paths.Count > 0)
      {
         _AttachImageFiles(paths);
         return;
      }

      // An image dragged straight out of a browser has no file behind it —
      // it arrives as a bitmap. Without this branch such a drop is accepted
      // by the cursor and then silently does nothing.
      var bmp = e.DataTransfer.TryGetBitmap();
      if (bmp != null)
      {
         using (bmp) _AttachBitmap(bmp, "dropped.png");
      }
   }

   /// <summary>
   /// Paste (Ctrl+V, Shift+Insert, context menu) with an image on the
   /// clipboard stages it as an attachment.
   ///
   /// The paste is deliberately NOT cancelled: the text half stays native, so
   /// caret position, selection replacement and undo keep behaving exactly as
   /// they always did, and we only add the image half on top. Cancelling would
   /// mean reimplementing text insertion — and the decision has to be made
   /// synchronously, before the clipboard read that would inform it. In the
   /// case this exists for (a screenshot tool) the clipboard carries no text
   /// at all, so nothing lands in the box.
   /// </summary>
   private async void TxtInput_PastingFromClipboard(object? sender, RoutedEventArgs e)
   {
      var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
      if (clipboard == null)
         return;

      try
      {
         // Files first: copying a .png in Explorer should attach that file,
         // keeping its name and its own encoding, rather than a bitmap
         // rendered out of it.
         var paths = _ImagePaths(await clipboard.TryGetFilesAsync());
         if (paths.Count > 0)
         {
            _AttachImageFiles(paths);
            return;
         }

         using var bmp = await clipboard.TryGetBitmapAsync();
         if (bmp != null)
            _AttachBitmap(bmp, "clipboard.png");
      }
      catch (Exception ex)
      {
         AppendError("Could not read the clipboard: " + ex.Message);
      }
   }

   /// <summary>
   /// Open one attachment full size over the tab. What is shown is the
   /// normalized image — i.e. exactly what the model was sent, downscaling
   /// included — because the source file's bytes are deliberately not kept.
   /// That makes this the right place to check whether the text in a
   /// screenshot survived the megapixel cap legibly.
   /// </summary>
   private void _ShowImagePreview(ImagePart img)
   {
      var bmp = ImageThumb.Decode(img);
      if (bmp == null)
      {
         AppendError("This attachment can no longer be decoded.");
         return;
      }

      // Uniform stretch scales it down to the window; the max-size clamp at
      // the bitmap's own dimensions stops it being blown up past 1:1.
      ImagePreviewImage.Source      = bmp;
      ImagePreviewImage.MaxWidth    = bmp.Size.Width;
      ImagePreviewImage.MaxHeight   = bmp.Size.Height;
      ImagePreviewCaption.Text      = ImageThumb.Caption(img);
      ImagePreviewOverlay.IsVisible = true;
      ImagePreviewOverlay.Focus();
   }

   private void _HideImagePreview()
   {
      if (!ImagePreviewOverlay.IsVisible) return;

      ImagePreviewOverlay.IsVisible = false;
      // Drop the reference: a decoded megapixel frame is ~4 MB, and the next
      // preview re-decodes from the attachment's own bytes anyway.
      ImagePreviewImage.Source = null;
   }

   private void ImagePreviewOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
   {
      // Click anywhere, image included — standard lightbox dismissal.
      _HideImagePreview();
      e.Handled = true;
   }

   private void ImagePreviewOverlay_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key != Key.Escape) return;
      _HideImagePreview();
      e.Handled = true;
   }

   private void _AttachImageFiles(IReadOnlyList<string> paths)
   {
      foreach (var path in paths)
      {
         if (ImageIngest.TryLoadFile(path, out var image, out var error) && image != null)
            Attachments.Add(image);
         else
            AppendError("Could not attach " + Path.GetFileName(path) + ": " + (error ?? "unreadable"));
      }
   }

   // Avalonia hands clipboard / drag bitmaps over as a decoded Bitmap, so the
   // route back to bytes is a PNG round-trip. ImageIngest then applies the
   // same megapixel cap it applies to a dropped file — a 4K screenshot must
   // not cost more just because it came via the clipboard.
   private void _AttachBitmap(Avalonia.Media.Imaging.Bitmap bmp, string label)
   {
      try
      {
         using var ms = new MemoryStream();
         bmp.Save(ms, null);
         if (ImageIngest.TryLoadBytes(ms.ToArray(), label, preferJpeg: false, out var image, out var error)
             && image != null)
            Attachments.Add(image);
         else
            AppendError("Could not attach the pasted image: " + (error ?? "unreadable"));
      }
      catch (Exception ex)
      {
         AppendError("Could not attach the pasted image: " + ex.Message);
      }
   }

   // -- clipboard wizard ------------------------------------------------------

   private Window? _window;            // for the Activated subscription

   private ulong _clipConsumedToken;   // last clipboard identity acted on
   private bool  _clipTokenValid;      // ...and whether one was ever recorded

   private byte[]? _cropSource;        // full-res PNG behind the open wizard

   // Window activation is the whole trigger: "came back to the app" is the
   // moment a screenshot taken elsewhere becomes interesting. Subscribed from
   // the visual tree rather than the constructor because the view is built
   // before it is parented into a window.
   protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
   {
      base.OnAttachedToVisualTree(e);

      _window = TopLevel.GetTopLevel(this) as Window;
      if (_window != null)
         _window.Activated += Window_Activated;
   }

   protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   {
      if (_window != null)
      {
         _window.Activated -= Window_Activated;
         _window = null;
      }
      base.OnDetachedFromVisualTree(e);
   }

   private void Window_Activated(object? sender, EventArgs e) => RefreshClipboardOffer();

   /// <summary>
   /// Ask whether an image is sitting on the clipboard and raise (or drop) the
   /// indicator accordingly.
   ///
   /// Called on window activation and on tab switch — i.e. exactly when the
   /// user comes back from wherever they took the screenshot. Nothing polls,
   /// nothing pops up, and no timer decides that the offer has expired: the
   /// button is a single affordance the user can take or ignore, and it costs
   /// nothing to leave sitting there.
   /// </summary>
   public async void RefreshClipboardOffer()
   {
      // Only for a live session tab: the button belongs to an input box that
      // can actually send something, and background tabs must stay quiet.
      if (_agent == null || !IsEffectivelyVisible)
         return;

      // Read per call rather than cached, so switching the option off takes
      // effect the moment the user leaves the config view.
      if (!AppSettingsStore.Current.Values.ClipboardWizard)
      {
         _SetClipboardOffer(false);
         return;
      }

      try
      {
         bool offer;
         if (CorePlatform.TryPeekClipboardImage(out bool hasImage, out ulong token))
         {
            // Windows: exact, and free. Suppress the offer for content that
            // was already attached or already declined — otherwise the button
            // would reappear on every alt-tab for the rest of the session.
            offer = hasImage && !(_clipTokenValid && token == _clipConsumedToken);
         }
         else
         {
            // Elsewhere there is no identity to compare, so the question
            // shrinks to "is an image there?" and a coincidence just means an
            // extra button the user ignores. See Platform.Linux.cs.
            offer = await _ClipboardHasImageAsync();
         }

         _SetClipboardOffer(offer);
      }
      catch
      {
         // A clipboard owned by a wedged app must never break the input bar.
         _SetClipboardOffer(false);
      }
   }

   private async Task<bool> _ClipboardHasImageAsync()
   {
      var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
      if (clipboard == null) return false;

      // Formats only — the contents stay untouched until the user clicks.
      var formats = await clipboard.GetDataFormatsAsync();
      if (formats == null) return false;

      foreach (var fmt in formats)
      {
         if (fmt == DataFormat.Bitmap)
            return true;
      }
      return false;
   }

   // Collapsed rather than hidden: the button sits in the Send/Cancel row, so
   // an invisible-but-measured one would leave a gap next to the prompt.
   private void _SetClipboardOffer(bool show) => BtnClipWizard.IsVisible = show;

   // The offer has been answered — either way this clipboard content is done,
   // and the indicator must not come back for it on the next activation.
   //
   // Where the platform has no identity to record (X11/Wayland) it does come
   // back, because the alternative — suppressing until an activation happens
   // to observe a non-image clipboard — would swallow the *next* screenshot.
   // A button that reappears is ignorable; one that never appears is a bug.
   private void _ConsumeClipboardOffer()
   {
      if (CorePlatform.TryPeekClipboardImage(out _, out ulong token))
      {
         _clipConsumedToken = token;
         _clipTokenValid    = true;
      }
      _SetClipboardOffer(false);
   }

   /// <summary>
   /// Pull the clipboard image at full resolution and open the crop wizard on
   /// it. The bytes are kept alongside the displayed bitmap because the crop
   /// is cut from them, not from what is on screen: the megapixel cap is
   /// applied only afterwards, so the kept region gets the whole budget.
   /// </summary>
   private async void BtnClipWizard_Click(object? sender, RoutedEventArgs e)
   {
      _ConsumeClipboardOffer();

      var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
      if (clipboard == null) return;

      try
      {
         var bmp = await clipboard.TryGetBitmapAsync();
         if (bmp == null)
         {
            // Raced with another app taking the clipboard — say so rather
            // than opening an empty wizard.
            AppendError("The clipboard no longer holds an image.");
            return;
         }

         try
         {
            using var ms = new MemoryStream();
            bmp.Save(ms, null);
            _cropSource = ms.ToArray();
         }
         catch
         {
            bmp.Dispose();
            throw;
         }

         // Ownership of the bitmap passes to the overlay here; it disposes it
         // when the wizard closes.
         CropOverlay.Show(bmp);
      }
      catch (Exception ex)
      {
         _cropSource = null;
         AppendError("Could not read the clipboard: " + ex.Message);
      }
   }

   private void _CropAccepted(PixelRect rect)
   {
      var bytes = _cropSource;
      _cropSource = null;
      if (bytes == null) return;

      var crop = SKRectI.Create(rect.X, rect.Y, rect.Width, rect.Height);
      if (ImageIngest.TryLoadBytes(bytes, "clipboard.png", preferJpeg: false, crop,
                                   out var image, out var error) && image != null)
         Attachments.Add(image);
      else
         AppendError("Could not attach the cropped image: " + (error ?? "unreadable"));
   }

   private void _CropCancelled() => _cropSource = null;

   // -- end clipboard wizard --------------------------------------------------

   private static List<string> _ImagePathsFrom(DragEventArgs e)
   {
      try
      {
         // Avalonia 12 replaced IDataObject with IDataTransfer: the payload
         // is reached through e.DataTransfer, and TryGetFiles() returns null
         // when the drag carries no file format at all.
         return _ImagePaths(e.DataTransfer.TryGetFiles());
      }
      catch
      {
         return new List<string>();
      }
   }

   // Local image files among the given storage items, in order. Non-images and
   // remote items are skipped silently — a mixed drag of sources and a
   // screenshot should attach the screenshot, not raise four errors.
   private static List<string> _ImagePaths(IEnumerable<IStorageItem>? items)
   {
      var result = new List<string>();
      if (items == null) return result;

      foreach (var item in items)
      {
         var path = item.TryGetLocalPath();
         if (!string.IsNullOrEmpty(path) && ImageIngest.IsSupportedFile(path))
            result.Add(path!);
      }
      return result;
   }
}
