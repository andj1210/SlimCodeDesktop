// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Base class for one entry in the chat transcript. Subclasses build their
/// own visual tree in the constructor and expose it via <see cref="View"/>;
/// the host (NativeAgentView) just appends View to its transcript panel.
///
/// Items may continue to mutate after being added (assistant bubbles grow
/// as text streams in; tool cards transition through statuses) — the
/// underlying controls are kept alive and updated in place.
/// </summary>
public abstract class TranscriptItem
{
   /// <summary>Root visual for this transcript entry. Set by subclass ctor.</summary>
   public Control View { get; protected set; } = new ContentControl();
}
