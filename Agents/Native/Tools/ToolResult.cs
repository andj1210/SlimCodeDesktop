// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Outcome of a single tool invocation. <see cref="Content"/> is the payload
/// sent back to the model as the <c>tool</c> message content. Errors are
/// also returned as content (with <see cref="IsError"/> set) so the model
/// can recover — throwing would abort the whole turn.
/// </summary>
public sealed class ToolResult
{
   public bool   IsError { get; init; }
   public string Content { get; init; } = "";

   public static ToolResult Ok(string content) =>
      new() { IsError = false, Content = content };

   public static ToolResult Error(string message) =>
      new() { IsError = true, Content = "ERROR: " + message };
}
