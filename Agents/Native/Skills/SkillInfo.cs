// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Skills;

/// <summary>
/// Where a skill was discovered. Drives the project-wins layering in
/// <see cref="SkillStore"/> and the source label shown in the discovery UI.
/// </summary>
public enum SkillSource
{
   Global  = 0,   // %APPDATA%\SlimCode\<profile>\skills
   Extra   = 1,   // user-configured extra search roots
   Project = 2,   // <project>\<folder>\skills, per AppSettings.ProjectSkillFolders (wins on a name clash)
   Intrinsic = 3, // built into the app (IntrinsicSkills); lowest precedence, any on-disk skill shadows it
}

/// <summary>
/// One discovered skill: a reusable instruction module loaded on demand by
/// the model via the <c>use_skill</c> tool. Parsed from a
/// <c>&lt;root&gt;\&lt;name&gt;\SKILL.md</c> file — a small frontmatter block
/// (<c>name</c>, <c>description</c>) plus a Markdown <see cref="Body"/>.
///
/// Only <see cref="Name"/> and <see cref="Description"/> are advertised to the
/// model up front (the cheap catalog); <see cref="Body"/> costs context only
/// when the skill is actually loaded — that's the progressive-disclosure point.
/// </summary>
public sealed class SkillInfo
{
   /// <summary>Stable skill id from frontmatter; matches the folder name by convention.</summary>
   public string Name { get; init; } = "";

   /// <summary>One-line summary shown in the catalog so the model can decide relevance.</summary>
   public string Description { get; init; } = "";

   /// <summary>Full Markdown instructions, delivered only when the skill is loaded.</summary>
   public string Body { get; init; } = "";

   /// <summary>Absolute path to the skill's folder (holds SKILL.md + any resources).</summary>
   public string Dir { get; init; } = "";

   public SkillSource Source { get; init; }

   /// <summary>Human-readable source label for the discovery UI.</summary>
   public string SourceLabel => Source switch
   {
      SkillSource.Project   => "project",
      SkillSource.Extra     => "extra",
      SkillSource.Intrinsic => "intrinsic",
      _                   => "global",
   };

   /// <summary>
   /// Render the skill for delivery to the model: a stable header line plus the
   /// body. Shared by the <c>use_skill</c> tool result and the chatbox preload
   /// path so both look identical to the model.
   /// </summary>
   public string ToModelText() => "Skill: " + Name + "\n\n" + Body;
}
