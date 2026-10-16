// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Skills;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Loads the full instructions of one discovered skill on demand. The catalog
/// (names + one-line descriptions) lives in the system prompt; this tool pulls
/// a skill's Markdown body only when the model decides it's relevant — the
/// progressive-disclosure mechanism that keeps unused skills free of context.
///
/// A single tool for all skills (not one tool per skill) so the tool catalog
/// doesn't bloat as skills grow.
///
/// Categorized as <see cref="ToolCategory.Read"/>: it returns text only and has
/// no side effects, so the gate auto-allows it. A skill body may *instruct* the
/// model to write or build, but those still pass through the normal permission
/// gate — loading a skill adds no execution surface.
///
/// The skill set is resolved fresh on each call so externally-added skills are
/// usable without rebuilding the loop.
/// </summary>
public sealed class UseSkillTool : ITool
{
   private readonly Func<IReadOnlyList<SkillInfo>> _resolve;

   public UseSkillTool(Func<IReadOnlyList<SkillInfo>> resolve)
   {
      _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
   }

   public string Name => "use_skill";

   public string Description =>
      "Load the full instructions for one of the available skills by name. Call " +
      "this when a skill's description matches the task, or when the user names a " +
      "skill; the body is returned as the tool result. Skills are listed in the " +
      "system prompt unless this project suppressed the catalog — calling with an " +
      "unknown name returns the list of valid names.";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args) =>
      args.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
         ? (n.GetString() ?? "")
         : "(no name)";

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      string? name = args.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                     ? n.GetString()
                     : null;
      if (string.IsNullOrWhiteSpace(name))
         return Task.FromResult(ToolResult.Error("'name' is required"));

      var skills = _resolve();
      foreach (var s in skills)
      {
         if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Ok(s.ToModelText()));
      }

      return Task.FromResult(ToolResult.Error(
         "unknown skill: " + name + ". " + AvailableList(skills)));
   }

   private static string AvailableList(IReadOnlyList<SkillInfo> skills)
   {
      if (skills.Count == 0) return "No skills are available.";
      var sb = new StringBuilder("Available skills: ");
      for (int i = 0; i < skills.Count; i++)
      {
         if (i > 0) sb.Append(", ");
         sb.Append(skills[i].Name);
      }
      return sb.ToString();
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "name": { "type": "string", "description": "Exact name of the skill to load." }
      },
      "required": ["name"],
      "additionalProperties": false
   }
   """;
}
