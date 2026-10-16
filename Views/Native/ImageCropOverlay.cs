// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// The clipboard wizard's one and only tool: drag a rectangle over the image
/// to say which part of it becomes the attachment.
///
/// Deliberately not an image editor. A screenshot is pasted to ask a question
/// about one dialog, one stack trace, one region of a 4K desktop; everything
/// else in the frame is tokens spent on noise and detail lost to the megapixel
/// cap. Cropping is the only operation that pays for itself here, so it is the
/// only one offered.
///
/// The selection is tracked in SOURCE pixels, not screen pixels, so resizing
/// the window — or the zoom below changing scale underneath it — moves the
/// rubber band with the image instead of silently changing what was selected.
///
/// Settling a selection zooms the view onto it (plus a margin), which makes
/// the gesture iterative: rough drag at fit-scale, then adjust the corners
/// against a magnified image, repeating until 1:1. Right-click unwinds all of
/// it back to the whole picture.
/// </summary>
public sealed class ImageCropOverlay : Border
{
   private const double PAD             = 28;  // breathing room around the image
   private const double HANDLE          = 9;   // corner grab box, display px
   private const int    MIN_SELECTION   = 8;   // source px per side
   private const double MARGIN_FRACTION = 0.2; // context kept around a zoom

   // -- fields ---------------------------------------------------------------

   private readonly Grid      _stage  = new();
   private readonly Grid      _frame  = new();  // exactly the displayed image's box
   private readonly Image     _img    = new() { Stretch = Stretch.Fill };
   private readonly Canvas    _canvas = new() { Background = Brushes.Transparent };
   private readonly Rectangle _band   = new();
   private readonly Rectangle[] _shade   = new Rectangle[4];
   private readonly Rectangle[] _handles = new Rectangle[4];

   private readonly TextBlock _hint   = new();
   private readonly TextBlock _size   = new();
   private readonly Button    _crop   = new();
   private readonly Button    _whole  = new();

   private Bitmap? _bmp;          // owned: disposed on close, see Show()

   private int    _srcW  = 1;
   private int    _srcH  = 1;
   private double _scale = 1.0;   // display px per source px
   private Rect   _view;          // source region currently on screen
   private Rect?  _focus;         // region that must stay visible; null = all

   private Rect _sel;             // source coords
   private bool _hasSel;

   private Drag  _drag = Drag.None;
   private Point _anchor;         // source coords: the corner that stays put
   private Point _grabbedAt;      // source coords: where a move drag started
   private Rect  _selAtGrab;

   public ImageCropOverlay()
   {
      Background = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0));
      IsVisible  = false;
      Focusable  = true;

      _BuildSelectionVisuals();

      // The frame is the viewport: the image is laid out at full scale and
      // slid under it by a negative margin, so clipping is what turns a zoom
      // into a window onto part of the picture.
      _frame.HorizontalAlignment = HorizontalAlignment.Center;
      _frame.VerticalAlignment   = VerticalAlignment.Center;
      _frame.ClipToBounds        = true;
      _img.HorizontalAlignment   = HorizontalAlignment.Left;
      _img.VerticalAlignment     = VerticalAlignment.Top;
      _frame.Children.Add(_img);
      _frame.Children.Add(_canvas);

      _stage.Margin = new Thickness(PAD, 14, PAD, 0);
      _stage.Children.Add(_frame);
      _stage.SizeChanged += Stage_SizeChanged;

      _canvas.Cursor = new Cursor(StandardCursorType.Cross);
      _canvas.PointerPressed  += Canvas_PointerPressed;
      _canvas.PointerMoved    += Canvas_PointerMoved;
      _canvas.PointerReleased += Canvas_PointerReleased;

      var header  = _BuildHeader();
      var toolbar = _BuildToolbar();
      Grid.SetRow(header,  0);
      Grid.SetRow(_stage,  1);
      Grid.SetRow(toolbar, 2);

      var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
      root.Children.Add(header);
      root.Children.Add(_stage);
      root.Children.Add(toolbar);
      Child = root;

      AddHandler(KeyDownEvent, Overlay_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
   }

   /// <summary>Crop accepted, in source-image pixels.</summary>
   public event Action<PixelRect>? Accepted;

   public event Action? Cancelled;

   /// <summary>
   /// Raise the wizard over <paramref name="bmp"/>. The bitmap is the full
   /// clipboard image, at its own resolution — the crop is expressed against
   /// that, and the megapixel cap is applied afterwards by the caller.
   ///
   /// Ownership transfers: the overlay disposes the bitmap when it closes, so
   /// the caller must NOT wrap it in a using.
   /// </summary>
   public void Show(Bitmap bmp)
   {
      if (bmp == null) return;

      _DropBitmap();
      _bmp        = bmp;
      _img.Source = bmp;
      _srcW       = Math.Max(1, bmp.PixelSize.Width);
      _srcH       = Math.Max(1, bmp.PixelSize.Height);
      _hasSel     = false;
      _sel        = default;
      _focus      = null;
      _drag       = Drag.None;

      IsVisible = true;
      _Layout();
      Focus();
   }

   public void Hide()
   {
      if (!IsVisible) return;

      IsVisible = false;
      _DropBitmap();
   }

   // A full-resolution frame is easily 30 MB here (no megapixel cap has been
   // applied yet) — don't keep one alive behind an invisible overlay.
   private void _DropBitmap()
   {
      _img.Source = null;
      try { _bmp?.Dispose(); } catch { }
      _bmp = null;
   }

   // -- private --------------------------------------------------------------

   private enum Drag { None, Draw, Move }

   private Control _BuildToolbar()
   {
      _hint.Text       = "Drag to select the part you want to attach";
      _hint.Foreground = ThemeBrushes.TextMuted;
      _hint.FontSize   = 12;
      _hint.VerticalAlignment = VerticalAlignment.Center;

      _size.Foreground = ThemeBrushes.TextPrimary;
      _size.FontSize   = 12;
      _size.Margin     = new Thickness(12, 0, 0, 0);
      _size.VerticalAlignment = VerticalAlignment.Center;

      _crop.Content   = "Attach selection";
      _crop.Padding   = new Thickness(14, 4);
      _crop.Margin    = new Thickness(4, 0, 4, 0);
      _crop.IsEnabled = false;
      _crop.Click    += (_, _) => _Accept(cropped: true);

      _whole.Content = "Attach whole image";
      _whole.Padding = new Thickness(14, 4);
      _whole.Margin  = new Thickness(4, 0, 4, 0);
      _whole.Click  += (_, _) => _Accept(cropped: false);

      var cancel = new Button
      {
         Content = "Cancel",
         Padding = new Thickness(14, 4),
         Margin  = new Thickness(4, 0, 4, 0),
      };
      cancel.Click += (_, _) => _Cancel();

      // Status line above the buttons, both centered: the image is centered
      // in the stage, so anything anchored to a corner reads as belonging to
      // the window rather than to the picture being cropped.
      var info = new StackPanel
      {
         Orientation         = Orientation.Horizontal,
         HorizontalAlignment = HorizontalAlignment.Center,
      };
      info.Children.Add(_hint);
      info.Children.Add(_size);

      var buttons = new StackPanel
      {
         Orientation         = Orientation.Horizontal,
         HorizontalAlignment = HorizontalAlignment.Center,
         Margin              = new Thickness(0, 10, 0, 0),
      };
      buttons.Children.Add(_crop);
      buttons.Children.Add(_whole);
      buttons.Children.Add(cancel);

      var bar = new StackPanel { Orientation = Orientation.Vertical };
      bar.Children.Add(info);
      bar.Children.Add(buttons);

      return new Border
      {
         Padding = new Thickness(PAD, 12, PAD, 16),
         Child   = bar,
      };
   }

   // Names the mode. Without it the overlay is an unlabelled dark screen with
   // an image on it — fine when you clicked the scissors a second ago, less so
   // when a permission prompt interrupted and you come back to it.
   private static Control _BuildHeader()
   {
      var title = new TextBlock
      {
         Text                = "✂  Clipboard wizard",
         FontSize            = 15,
         FontWeight          = FontWeight.SemiBold,
         Foreground          = ThemeBrushes.TextPrimary,
         HorizontalAlignment = HorizontalAlignment.Center,
      };

      var sub = new TextBlock
      {
         Text                = "Choose the part of the clipboard image to attach",
         FontSize            = 11,
         Margin              = new Thickness(0, 2, 0, 0),
         Foreground          = ThemeBrushes.TextMuted,
         HorizontalAlignment = HorizontalAlignment.Center,
      };

      var stack = new StackPanel { Orientation = Orientation.Vertical };
      stack.Children.Add(title);
      stack.Children.Add(sub);

      return new Border
      {
         Padding = new Thickness(PAD, 16, PAD, 0),
         Child   = stack,
      };
   }

   private void _BuildSelectionVisuals()
   {
      // Four rectangles dimming everything outside the selection. Cheaper and
      // far more predictable than a geometry with a hole in it, and it is what
      // every screenshot tool does.
      var dim = new SolidColorBrush(Color.FromArgb(0xA8, 0, 0, 0));
      for (int i = 0; i < _shade.Length; i++)
      {
         _shade[i] = new Rectangle { Fill = dim, IsHitTestVisible = false, IsVisible = false };
         _canvas.Children.Add(_shade[i]);
      }

      _band.Stroke           = ThemeBrushes.Accent;
      _band.StrokeThickness  = 1;
      _band.StrokeDashArray  = new AvaloniaList<double> { 4, 3 };
      _band.IsHitTestVisible = false;
      _band.IsVisible        = false;
      _canvas.Children.Add(_band);

      for (int i = 0; i < _handles.Length; i++)
      {
         _handles[i] = new Rectangle
         {
            Width            = HANDLE,
            Height           = HANDLE,
            Fill             = ThemeBrushes.Accent,
            IsHitTestVisible = false,
            IsVisible        = false,
         };
         _canvas.Children.Add(_handles[i]);
      }
   }

   /// <summary>
   /// Resolve <see cref="_focus"/> (the region that must be on screen) into an
   /// actual viewport and scale, then place the image inside the clipped frame.
   ///
   /// Never enlarges past 1:1. Beyond that point a selection's real pixel
   /// bounds would be finer than the pixels the user is aiming at, which makes
   /// the crop feel imprecise in exactly the situation zooming was meant to
   /// fix.
   ///
   /// The viewport is grown from the focus rect to fill whatever the stage has
   /// left over at that scale, so zooming shows the surrounding context for
   /// free instead of letterboxing black bars around it.
   /// </summary>
   private void _Layout()
   {
      double availW = Math.Max(1, _stage.Bounds.Width);
      double availH = Math.Max(1, _stage.Bounds.Height);

      var want = _focus ?? new Rect(0, 0, _srcW, _srcH);

      _scale = Math.Min(1.0, Math.Min(availW / Math.Max(1, want.Width),
                                      availH / Math.Max(1, want.Height)));
      if (_scale <= 0 || double.IsNaN(_scale) || double.IsInfinity(_scale))
         _scale = 1.0;

      double viewW = Math.Min(_srcW, availW / _scale);
      double viewH = Math.Min(_srcH, availH / _scale);
      double viewX = Math.Clamp(want.Center.X - viewW / 2, 0, _srcW - viewW);
      double viewY = Math.Clamp(want.Center.Y - viewH / 2, 0, _srcH - viewH);
      _view = new Rect(viewX, viewY, viewW, viewH);

      _frame.Width  = viewW * _scale;
      _frame.Height = viewH * _scale;

      // The image keeps its full size and is slid under the clipped frame —
      // the negative offset is the viewport's origin.
      _img.Width  = _srcW * _scale;
      _img.Height = _srcH * _scale;
      _img.Margin = new Thickness(-viewX * _scale, -viewY * _scale, 0, 0);

      _Sync();
   }

   // The focus rect for the current selection: the selection plus a margin of
   // MARGIN_FRACTION of its own size on every side, so what was just selected
   // is never flush against the edge of the view and can still be nudged
   // outwards. Cleared (null = whole image) when there is no selection.
   private void _FocusOnSelection()
   {
      if (!_hasSel)
      {
         _focus = null;
         return;
      }

      double mx = _sel.Width  * MARGIN_FRACTION;
      double my = _sel.Height * MARGIN_FRACTION;
      _focus = new Rect(_sel.X - mx, _sel.Y - my,
                        _sel.Width + 2 * mx, _sel.Height + 2 * my);
   }

   private void _Sync()
   {
      double fw = _view.Width  * _scale;
      double fh = _view.Height * _scale;

      var px = _SelectionPixels();
      bool show = _hasSel && px.Width > 0 && px.Height > 0;

      _band.IsVisible = show;
      foreach (var s in _shade)   s.IsVisible = show;
      foreach (var h in _handles) h.IsVisible = show;

      if (show)
      {
         double x = (_sel.X - _view.X) * _scale, y = (_sel.Y - _view.Y) * _scale;
         double w = _sel.Width * _scale, h = _sel.Height * _scale;

         _Place(_band, x, y, w, h);
         _Place(_shade[0], 0, 0, fw, y);                 // above
         _Place(_shade[1], 0, y + h, fw, fh - (y + h));  // below
         _Place(_shade[2], 0, y, x, h);                  // left
         _Place(_shade[3], x + w, y, fw - (x + w), h);   // right

         _PlaceHandle(_handles[0], x,     y);
         _PlaceHandle(_handles[1], x + w, y);
         _PlaceHandle(_handles[2], x + w, y + h);
         _PlaceHandle(_handles[3], x,     y + h);

         _size.Text = px.Width + " x " + px.Height + " px  ·  " + _ZoomLabel();
         _hint.Text = "Drag a corner to adjust, drag inside to move, right-click to start over";
      }
      else
      {
         _size.Text = _srcW + " x " + _srcH + " px  ·  " + _ZoomLabel();
         _hint.Text = "Drag to select the part you want to attach";
      }

      _crop.IsEnabled = show && px.Width >= MIN_SELECTION && px.Height >= MIN_SELECTION;
   }

   // Says whether what is on screen can still be trusted for fine work: at
   // 1:1 a selection edge lands on the pixel it looks like it lands on.
   private string _ZoomLabel() =>
      _scale >= 0.999 ? "1:1" : (int)Math.Round(_scale * 100) + "%";

   private static void _Place(Rectangle r, double x, double y, double w, double h)
   {
      r.Width  = Math.Max(0, w);
      r.Height = Math.Max(0, h);
      Canvas.SetLeft(r, x);
      Canvas.SetTop(r, y);
   }

   private static void _PlaceHandle(Rectangle r, double cx, double cy)
   {
      Canvas.SetLeft(r, cx - HANDLE / 2);
      Canvas.SetTop(r, cy - HANDLE / 2);
   }

   // Rounded to whole source pixels — that is what actually gets cut, so it is
   // also what the readout must show.
   private PixelRect _SelectionPixels()
   {
      int x = (int)Math.Round(_sel.X);
      int y = (int)Math.Round(_sel.Y);
      int w = (int)Math.Round(_sel.Width);
      int h = (int)Math.Round(_sel.Height);

      x = Math.Clamp(x, 0, _srcW);
      y = Math.Clamp(y, 0, _srcH);
      w = Math.Clamp(w, 0, _srcW - x);
      h = Math.Clamp(h, 0, _srcH - y);
      return new PixelRect(x, y, w, h);
   }

   // Clamped to the viewport, not to the image: while zoomed in, a drag that
   // runs off the edge should stop at what can be seen. Extending the
   // selection beyond that would silently change a region the user cannot
   // check — right-click resets to the whole image for that.
   private Point _ToSource(Point display) =>
      new(Math.Clamp(_view.X + display.X / _scale, _view.X, _view.Right),
          Math.Clamp(_view.Y + display.Y / _scale, _view.Y, _view.Bottom));

   // Which corner (if any) the pointer grabbed. Hit-tested here rather than on
   // the handle rectangles themselves so the grab area can be generous without
   // the handles having to be drawn that large.
   private int _CornerAt(Point display)
   {
      if (!_hasSel) return -1;

      double x = (_sel.X - _view.X) * _scale, y = (_sel.Y - _view.Y) * _scale;
      double w = _sel.Width * _scale, h = _sel.Height * _scale;
      ReadOnlySpan<double> cx = [x, x + w, x + w, x];
      ReadOnlySpan<double> cy = [y, y, y + h, y + h];

      for (int i = 0; i < 4; i++)
      {
         if (Math.Abs(display.X - cx[i]) <= HANDLE && Math.Abs(display.Y - cy[i]) <= HANDLE)
            return i;
      }
      return -1;
   }

   private void _Accept(bool cropped)
   {
      var rect = cropped ? _SelectionPixels() : new PixelRect(0, 0, _srcW, _srcH);
      if (rect.Width <= 0 || rect.Height <= 0)
         return;

      Hide();
      Accepted?.Invoke(rect);
   }

   private void _Cancel()
   {
      Hide();
      Cancelled?.Invoke();
   }

   // -- private event handlers -----------------------------------------------

   private void Stage_SizeChanged(object? sender, SizeChangedEventArgs e)
   {
      if (IsVisible) _Layout();
   }

   private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
   {
      var pt  = e.GetPosition(_canvas);
      var src = _ToSource(pt);

      // Right-click: drop the selection and pull back to the whole image. The
      // way out of a zoom that went somewhere unhelpful, and the reason the
      // zoom can be aggressive in the first place.
      if (e.GetCurrentPoint(_canvas).Properties.IsRightButtonPressed)
      {
         _drag   = Drag.None;
         _hasSel = false;
         _focus  = null;
         _Layout();
         e.Handled = true;
         return;
      }

      int corner = _CornerAt(pt);
      if (corner >= 0)
      {
         // Resizing from a corner is just a fresh draw anchored at the
         // opposite one, so the two gestures share all the drag math below.
         _anchor = new Point(corner is 1 or 2 ? _sel.X : _sel.Right,
                             corner is 2 or 3 ? _sel.Y : _sel.Bottom);
         _drag   = Drag.Draw;
      }
      else if (_hasSel && _sel.Contains(src))
      {
         _grabbedAt = src;
         _selAtGrab = _sel;
         _drag      = Drag.Move;
      }
      else
      {
         _anchor = src;
         _sel    = new Rect(src, new Size(0, 0));
         _hasSel = true;
         _drag   = Drag.Draw;
      }

      e.Pointer.Capture(_canvas);
      e.Handled = true;
   }

   private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
   {
      if (_drag == Drag.None) return;

      var src = _ToSource(e.GetPosition(_canvas));

      if (_drag == Drag.Draw)
      {
         _sel = new Rect(Math.Min(_anchor.X, src.X), Math.Min(_anchor.Y, src.Y),
                         Math.Abs(src.X - _anchor.X), Math.Abs(src.Y - _anchor.Y));
      }
      else
      {
         // Clamped to the viewport so a move can never push the selection out
         // of sight — it slides along the edge instead of shrinking, vanishing
         // or wandering off into the part of the image that is scrolled away.
         double maxX = Math.Max(_view.X, _view.Right  - _selAtGrab.Width);
         double maxY = Math.Max(_view.Y, _view.Bottom - _selAtGrab.Height);
         double x = Math.Clamp(_selAtGrab.X + (src.X - _grabbedAt.X), _view.X, maxX);
         double y = Math.Clamp(_selAtGrab.Y + (src.Y - _grabbedAt.Y), _view.Y, maxY);
         _sel = new Rect(x, y, _selAtGrab.Width, _selAtGrab.Height);
      }

      _Sync();
      e.Handled = true;
   }

   private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
   {
      if (_drag == Drag.None) return;

      _drag = Drag.None;
      e.Pointer.Capture(null);

      // A plain click (no drag) clears the selection rather than leaving a
      // 0x0 one behind, so clicking off the box is an intuitive "start over".
      var px = _SelectionPixels();
      if (px.Width < MIN_SELECTION || px.Height < MIN_SELECTION)
         _hasSel = false;

      // Zoom on release, never during the drag: re-scaling under a pointer
      // that is mid-gesture moves the image out from under the cursor. Each
      // adjustment therefore zooms one step further in, which is the point —
      // a rough selection at fit-scale becomes a precise one at 1:1.
      _FocusOnSelection();
      _Layout();
      e.Handled = true;
   }

   private void Overlay_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key == Key.Escape)
      {
         _Cancel();
         e.Handled = true;
      }
      else if (e.Key is Key.Enter or Key.Return)
      {
         _Accept(cropped: _crop.IsEnabled);
         e.Handled = true;
      }
   }
}
