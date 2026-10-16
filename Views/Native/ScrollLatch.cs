// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// "Stick to the bottom unless the user scrolled away" behaviour for a
/// transcript ScrollViewer. Shared by the main chat view and the subagent
/// viewer so both follow streaming content by the same rules.
///
/// The latch is released only by the user scrolling *up*, and taken again as
/// soon as the view sits at the bottom. Everything else that moves the bottom
/// - content growing, a tool card expanding or collapsing, the splitter
/// shrinking the transcript pane - re-anchors instead of releasing, because
/// the user did not ask for any of it.
/// </summary>
public sealed class ScrollLatch
{
   public ScrollLatch(ScrollViewer scroll)
   {
      _scroll = scroll ?? throw new ArgumentNullException(nameof(scroll));
      _scroll.ScrollChanged += OnScrollChanged;
   }

   /// <summary>True while the view is following the bottom of the content.</summary>
   public bool IsLatched => _latched;

   /// <summary>Fires on the UI thread whenever the latch flips.</summary>
   public event Action<bool>? LatchChanged;

   /// <summary>
   /// Re-arm and jump to the bottom regardless of where the user was reading.
   /// For deliberate "show me what happens next" moments only - sending a
   /// prompt, resetting the session.
   /// </summary>
   public void Arm()
   {
      _Set(true);
      ScrollToEnd();
   }

   /// <summary>Follow the content to the bottom. No-op while unlatched.</summary>
   public void ScrollToEnd()
   {
      if (!_latched)
         return;
      Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
   }

   // -- private methods ---------------------------------------------------------

   /// <summary>
   /// Post-layout upkeep for changes the user did not make. Latched: follow
   /// the bottom wherever it moved to. Unlatched: take the latch again only
   /// once the view actually sits at the bottom.
   /// </summary>
   private void _ReanchorOrLatch()
   {
      bool atBottom = _IsAtBottom();

      if (_latched)
      {
         if (!atBottom)
            ScrollToEnd();
         return;
      }

      if (atBottom)
         _Set(true);
   }

   /// <summary>
   /// True when a bubbled ScrollChanged originated in the transcript's own
   /// ScrollViewer rather than one nested inside it. Matched by nearest
   /// ScrollViewer ancestor rather than by identity on the source, because the
   /// source is the scroller itself for wheel and scrollbar input but can be an
   /// inner presenter for programmatic offset changes.
   /// </summary>
   private bool _IsOwnScroll(object? source)
   {
      for (var v = source as Visual; v != null; v = v.GetVisualParent())
      {
         if (v is ScrollViewer sv)
            return ReferenceEquals(sv, _scroll);
      }
      // Origin outside any scroller: not a nested one, so it is ours.
      return true;
   }

   private bool _IsAtBottom()
   {
      // A few pixels of tolerance: layout + scrollbar arithmetic can leave a
      // sub-pixel gap even when the user is visually at the bottom.
      const double TOLERANCE = 4.0;
      return _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - TOLERANCE;
   }

   private void _Set(bool latched)
   {
      if (latched == _latched)
         return;
      _latched = latched;
      LatchChanged?.Invoke(latched);
   }

   // -- private event handlers --------------------------------------------------

   private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
   {
      // ScrollChanged bubbles, and a transcript is full of nested
      // ScrollViewers - every tool call's argument and result body sits in
      // one, and those scroll themselves to the end while their text streams
      // in. Treating that as transcript movement unlatched us on every
      // streamed chunk.
      if (!_IsOwnScroll(e.Source))
         return;

      // Nothing but a real offset change can be the user scrolling. Extent
      // changes (content grew) and viewport changes (the splitter drag that
      // grows the prompt pane and shrinks ours) move the bottom out from under
      // a stationary offset - they must never unlatch.
      if (e.OffsetDelta.Y == 0)
      {
         // Decide after layout has settled: mid-layout the extent and viewport
         // read back inconsistently and _IsAtBottom lies.
         Dispatcher.UIThread.Post(_ReanchorOrLatch, DispatcherPriority.Background);
         return;
      }

      // Direction is what separates us from the user: every scroll we issue is
      // a ScrollToEnd (downward), so only an upward move can be someone
      // leaving the bottom to read history. A downward move that lands at the
      // bottom re-latches, whoever caused it. Hence no "is this scroll mine?"
      // flag - one that has to be armed before the scroll and consumed by its
      // event leaks whenever the scroll produces no event, and then swallows
      // the user's next one.
      if (e.OffsetDelta.Y < 0 && !_IsAtBottom())
      {
         _Set(false);
         return;
      }

      _ReanchorOrLatch();
   }

   // -- fields ---------------------------------------------------------------------

   private readonly ScrollViewer _scroll;

   private bool _latched = true;
}
