// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Row of thumbnails for the images staged on the next Send. Sits directly
/// above the input box so what will be attached is visible while the prompt
/// is being written — the attachments are part of the message, and a dropped
/// screenshot with no visible confirmation is indistinguishable from a drop
/// that silently failed.
///
/// Hides itself while empty so the input area keeps its full height in the
/// overwhelmingly common text-only case.
/// </summary>
public sealed class AttachmentStrip : Border
{
   private const double THUMB_HEIGHT = 54;

   private readonly WrapPanel        _panel = new() { Orientation = Orientation.Horizontal };
   private readonly List<ImagePart>  _items = new();

   public AttachmentStrip()
   {
      Padding   = new Thickness(0, 0, 0, 6);
      Child     = _panel;
      IsVisible = false;
   }

   /// <summary>Fires whenever the staged set changes (add, remove, clear).</summary>
   public event Action? Changed;

   /// <summary>Fires when a thumbnail is clicked, asking the host to preview it.</summary>
   public event Action<ImagePart>? Preview;

   public int Count => _items.Count;

   /// <summary>Staged images in insertion order. Live list — copy before storing.</summary>
   public IReadOnlyList<ImagePart> Items => _items;

   public void Add(ImagePart image)
   {
      if (image == null) return;
      _items.Add(image);
      _Rebuild();
   }

   /// <summary>Replace the staged set wholesale (used by the revert flow).</summary>
   public void Reset(IEnumerable<ImagePart>? images)
   {
      _items.Clear();
      if (images != null)
      {
         foreach (var img in images)
         {
            if (img != null) _items.Add(img);
         }
      }
      _Rebuild();
   }

   public void Clear() => Reset(null);

   /// <summary>Detached copy of the staged set, for handing to the loop.</summary>
   public List<ImagePart> Snapshot() => new(_items);

   // -- private --------------------------------------------------------------

   private void _Rebuild()
   {
      _panel.Children.Clear();
      foreach (var img in _items)
         _panel.Children.Add(_BuildChip(img));

      IsVisible = _items.Count > 0;
      Changed?.Invoke();
   }

   private Control _BuildChip(ImagePart img)
   {
      var bmp = ImageThumb.Decode(img);

      Control preview;
      if (bmp != null)
      {
         var thumb = new Image
         {
            Source  = bmp,
            Height  = THUMB_HEIGHT,
            Stretch = Stretch.Uniform,
         };
         // Click to view it full size. The remove button sits above this in
         // the same cell, so a click on ✕ never reaches here.
         thumb.Cursor = new Cursor(StandardCursorType.Hand);
         thumb.PointerPressed += (_, e) =>
         {
            Preview?.Invoke(img);
            e.Handled = true;
         };
         preview = thumb;
      }
      else
      {
         // Undecodable payload: still show a chip so the user can remove it,
         // rather than an attachment that exists on the wire but not on screen.
         preview = new TextBlock
         {
            Text              = "?",
            Height            = THUMB_HEIGHT,
            Width             = THUMB_HEIGHT,
            FontSize          = 20,
            Foreground        = ThemeBrushes.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextAlignment       = TextAlignment.Center,
         };
      }

      var remove = new Button
      {
         Content             = "✕",
         FontSize            = 10,
         Width               = 16,
         Height              = 16,
         Padding             = new Thickness(0),
         Background          = ThemeBrushes.WindowBackground,
         BorderBrush         = ThemeBrushes.BorderColor,
         BorderThickness     = new Thickness(1),
         Foreground          = ThemeBrushes.TextPrimary,
         CornerRadius        = new CornerRadius(8),
         HorizontalAlignment = HorizontalAlignment.Right,
         VerticalAlignment   = VerticalAlignment.Top,
         Margin              = new Thickness(0, -4, -4, 0),
         Cursor              = new Cursor(StandardCursorType.Hand),
      };
      ToolTip.SetTip(remove, "Remove this attachment");
      remove.Click += (_, _) =>
      {
         _items.Remove(img);
         _Rebuild();
      };

      var overlay = new Grid();
      overlay.Children.Add(preview);
      overlay.Children.Add(remove);

      var chip = new Border
      {
         Background      = ThemeBrushes.PanelBackground,
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1),
         CornerRadius    = new CornerRadius(4),
         Padding         = new Thickness(4),
         Margin          = new Thickness(0, 0, 6, 0),
         Child           = overlay,
      };
      ToolTip.SetTip(chip, ImageThumb.Caption(img));
      return chip;
   }
}
