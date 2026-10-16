// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Coarse classification used to seed default permissions. Each
/// <see cref="SlimCodeDesktop.Agents.Native.Tools.ITool"/> declares its
/// category; tools without a strong opinion should pick the most permissive
/// of <see cref="Read"/>, <see cref="Write"/>, <see cref="Execute"/>
/// they actually need.
/// </summary>
public enum ToolCategory
{
   Read    = 0,   // file inspection: read_file, list_dir, glob, grep
   Write   = 1,   // file mutation: write_file, edit_file, move_file, copy_file, delete_file
   Execute = 2,   // shell command execution: bash
}
