// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using SlimCodeDesktop.Agents.Settings;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Core;

/// <summary>
/// Application-wide SlimCode preferences (i.e. settings that aren't tied to
/// any particular agent). Persisted to <c>slimcode_app.json</c> via
/// <see cref="AppSettingsStore"/>.
///
/// Add new knobs here as <c>[Setting]</c>-attributed properties; the config
/// UI picks them up automatically via reflection, no UI code changes needed.
///
/// Settings files written by older builds carry keys that no longer exist;
/// the deserializer ignores unknown properties, and the next save drops them.
/// </summary>
public sealed class AppSettings
{
   /// <summary>
   /// On by default: the indicator only appears when there is genuinely an
   /// image waiting, and ignoring it costs nothing. The switch exists for
   /// people who paste images constantly and want the button to stop
   /// appearing, and as the off ramp if some clipboard owner ever makes the
   /// probe misbehave.
   /// </summary>
   [Setting("Clipboard wizard",
      Help = "Offer a crop-and-attach button next to the prompt when you return "
           + "to SlimCode with an image on the clipboard.")]
   public bool ClipboardWizard { get; set; } = true;

   /// <summary>
   /// Lives here rather than in a shared store on purpose: this file is
   /// per-profile, so each profile carries its own accent and the default
   /// (Auto) already derives a distinct one from the profile name.
   /// </summary>
   [Setting("Accent color",
      Help = "Drives buttons, selected tabs and highlights. Auto derives a color from "
           + "the profile name, so a --profile session is recognizable at a glance. "
           + "Buttons and tabs re-color immediately; elements already drawn in the "
           + "chat keep the old color until restart.")]
   public AccentPreset Accent { get; set; } = AccentPreset.Auto;

   /// <summary>
   /// Off by default: MCP projects are a distinct project kind with their own
   /// setup, and most users never want one. The switch only controls whether
   /// new MCP projects can be created — projects that already exist keep
   /// working and stay editable after it is turned back off.
   /// </summary>
   [Setting("Enable MCP support (experimental)",
      Help = "Adds an 'Add MCP…' button to the project list. An MCP project gets "
           + "its tools from Model Context Protocol servers instead of the built-in "
           + "file / build / test tools. Existing MCP projects keep working when "
           + "this is off; only creating new ones is hidden.")]
   public bool EnableMcp { get; set; } = false;

   [Setting("Accent color override",
      Help = "Exact color as hex (e.g. #3DA8E5), overriding the preset above. "
           + "Leave empty to use the preset. Very dark or very light values are "
           + "nudged into a readable range; unparseable text is ignored.")]
   public string AccentColorHex { get; set; } = "";

   /// <summary>
   /// Raw semicolon-separated list; parsed by
   /// <see cref="SlimCodeDesktop.Agents.Native.Skills.SkillStore.ProjectSkillsDirs"/>.
   /// App-wide rather than per project so one convention covers every repo.
   /// </summary>
   [Setting("Project skill folders",
      Help = "Semicolon-separated folders, relative to the project root, that hold a "
           + "'skills' subfolder (e.g. .ai -> <project>\\.ai\\skills\\<name>\\SKILL.md). "
           + "On a name clash the earlier folder wins. The first folder is the one "
           + "'Create project skills folder' creates. Absolute paths are ignored.")]
   public string ProjectSkillFolders { get; set; } = ".ai";
}
