// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SlimCodeDesktop.Agents.Native.Skills;

/// <summary>
/// Skills that ship with the app rather than living in a folder. Built per
/// project because their text names that project's configured skills folders
/// (<see cref="SkillStore.ProjectSkillsDirs"/>), which the model cannot know
/// otherwise. They sit below every on-disk layer, so a user skill of the same
/// name replaces one.
/// </summary>
public static class IntrinsicSkills
{
   public const string SKILLWRITER = "skillwriter";

   // Shown where an on-disk skill shows its folder.
   private const string BUILT_IN_DIR = "(built into SlimCode)";

   public static IReadOnlyList<SkillInfo> For(string projectPath) => new[]
   {
      new SkillInfo
      {
         Name        = SKILLWRITER,
         Description = "How to write a new skill for this project: where its folder goes and the shape of SKILL.md.",
         Body        = _SkillwriterBody(projectPath),
         Dir         = BUILT_IN_DIR,
         Source      = SkillSource.Intrinsic,
      },
   };

   private static string _SkillwriterBody(string projectPath)
   {
      var projectDirs = SkillStore.ProjectSkillsDirs(projectPath);
      var globalDir   = SkillStore.GlobalSkillsDir();

      var sb = new StringBuilder();
      sb.Append("# Writing a skill\n\n");
      sb.Append("A skill is a reusable instruction module. Only its name and description are listed ");
      sb.Append("to you up front; the body is delivered when the skill is loaded (by the user's picker, ");
      sb.Append("project auto-load, or your `use_skill` call). Write the body for a model reading it cold.\n\n");

      sb.Append("## Where it goes\n\n");
      sb.Append("One folder per skill, holding a file named exactly `SKILL.md`:\n\n");
      if (projectDirs.Count > 0)
      {
         var primary = _Relative(projectPath, projectDirs[0]);
         sb.Append("    ").Append(primary).Append("/<skill-name>/SKILL.md\n\n");
         sb.Append("That path is relative to the project root. Put new project skills there");
         if (projectDirs.Count > 1)
         {
            sb.Append(". Also scanned, with lower precedence: ");
            for (int i = 1; i < projectDirs.Count; i++)
            {
               if (i > 1) sb.Append(", ");
               sb.Append('`').Append(_Relative(projectPath, projectDirs[i])).Append('`');
            }
         }
         sb.Append(". Project skills can be committed with the repository.\n\n");
      }
      else
      {
         sb.Append("No project skills folder is configured for this app, so a project skill cannot be ");
         sb.Append("placed. Tell the user; they can set one in the app settings (Project skill folders).\n\n");
      }
      if (globalDir != null)
      {
         sb.Append("A skill meant for every project goes in the user's global folder instead: `")
           .Append(globalDir.Replace('\\', '/')).Append("/<skill-name>/SKILL.md`. ");
         sb.Append("Only write there when the user asks for a global skill.\n\n");
      }
      sb.Append("On a name clash the project skill wins over the global one. New or edited skills ");
      sb.Append("are picked up on the next lookup; no restart is needed.\n\n");

      sb.Append("## Shape of SKILL.md\n\n");
      sb.Append("```markdown\n");
      sb.Append("---\n");
      sb.Append("name: <skill-name>\n");
      sb.Append("description: <one line: what the skill does and when to load it>\n");
      sb.Append("---\n\n");
      sb.Append("# <Title>\n\n");
      sb.Append("<Markdown instructions>\n");
      sb.Append("```\n\n");
      sb.Append("Rules for the frontmatter (it is read by a minimal parser, not full YAML):\n\n");
      sb.Append("- It must be the first thing in the file, between two `---` lines.\n");
      sb.Append("- One `key: value` per line. No multi-line values, no lists, no quotes ");
      sb.Append("(quotes would become part of the value).\n");
      sb.Append("- `name`: lowercase, digits and `-`, matching the folder name.\n");
      sb.Append("- `description`: a single line. It is all a model sees before loading, so say ");
      sb.Append("what the skill covers and the situations it applies to.\n\n");

      sb.Append("## Writing the body\n\n");
      sb.Append("- Lead with what to do, then the details; imperative steps beat prose.\n");
      sb.Append("- Be specific to this project: real paths, commands, naming conventions.\n");
      sb.Append("- Keep it short; every loaded line costs context for the rest of the session.\n");
      sb.Append("- Supporting files (templates, examples) may sit in the same folder; ");
      sb.Append("reference them by their path from the project root so they can be read with the file tools.\n\n");

      sb.Append("## Before writing\n\n");
      sb.Append("Check the target folder for an existing skill of that name and update it rather than ");
      sb.Append("creating a duplicate. Tell the user the path of the file you wrote.\n");
      return sb.ToString();
   }

   private static string _Relative(string projectPath, string dir)
   {
      try { return Path.GetRelativePath(projectPath, dir).Replace('\\', '/'); }
      catch { return dir.Replace('\\', '/'); }
   }
}
