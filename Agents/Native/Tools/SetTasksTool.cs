// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Views.Native;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Replace the session's pinned task list. Full-replace semantics: the
/// model is expected to send the complete list on every invocation, not
/// diffs. Idempotent — a second identical call is a no-op visually.
///
/// Categorized as <see cref="ToolCategory.Read"/> because the tool has
/// no side effects on disk or processes; the gate auto-allows it so the
/// model isn't pestered for permission every time progress moves.
///
/// One <see cref="SetTasksTool"/> instance per active session, constructed
/// by <c>NativeAgentView.BuildLoop</c> with a callback that pushes the
/// parsed items onto the pinned <c>TaskListPanel</c>.
/// </summary>
public sealed class SetTasksTool : ITool
{
   private readonly Action<IReadOnlyList<TaskListPanel.TaskItem>> _onUpdate;

   public SetTasksTool(Action<IReadOnlyList<TaskListPanel.TaskItem>> onUpdate)
   {
      _onUpdate = onUpdate ?? throw new ArgumentNullException(nameof(onUpdate));
   }

   public string Name => "set_tasks";

   public string Description =>
      "Replace the user-visible task list with the supplied items. Use this " +
      "for any work that spans more than a couple of steps. Send the FULL " +
      "list every call — items not included are dropped. Statuses: " +
      "'pending', 'in_progress', 'completed', 'cancelled'.";

   public ToolCategory Category => ToolCategory.Read;

   public string ParametersJsonSchema => SCHEMA;

   public string ArgsPreview(JsonElement args)
   {
      // Compact: "5 tasks (2 done, 1 in progress)" — useful in the audit
      // log and tool-call card header without dumping the full payload.
      if (!args.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array)
         return "(no tasks)";
      int total = 0, done = 0, prog = 0, cancel = 0;
      foreach (var t in arr.EnumerateArray())
      {
         total++;
         if (!t.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String)
            continue;
         switch ((s.GetString() ?? "").ToLowerInvariant())
         {
            case "completed":   done++;   break;
            case "in_progress": prog++;   break;
            case "cancelled":   cancel++; break;
         }
      }
      var parts = new List<string>(3);
      if (done   > 0) parts.Add(done   + " done");
      if (prog   > 0) parts.Add(prog   + " in progress");
      if (cancel > 0) parts.Add(cancel + " cancelled");
      var detail = parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
      return total + " task" + (total == 1 ? "" : "s") + detail;
   }

   public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      if (!args.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array)
         return Task.FromResult(ToolResult.Error("'tasks' array is required"));

      var items = new List<TaskListPanel.TaskItem>(arr.GetArrayLength());
      foreach (var el in arr.EnumerateArray())
      {
         if (el.ValueKind != JsonValueKind.Object) continue;
         string text = el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                          ? (t.GetString() ?? "")
                          : "";
         if (string.IsNullOrWhiteSpace(text)) continue;

         var status = TaskListPanel.Status.Pending;
         if (el.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
         {
            status = (s.GetString() ?? "").ToLowerInvariant() switch
            {
               "completed"   => TaskListPanel.Status.Completed,
               "in_progress" => TaskListPanel.Status.InProgress,
               "cancelled"   => TaskListPanel.Status.Cancelled,
               _             => TaskListPanel.Status.Pending,
            };
         }
         items.Add(new TaskListPanel.TaskItem(status, text));
      }

      try
      {
         _onUpdate(items);
      }
      catch
      {
         // The callback marshals to the UI thread; a failure shouldn't
         // abort the turn. Worst case the panel is stale until the next
         // invocation.
      }

      return Task.FromResult(ToolResult.Ok(
         "Task list updated: " + items.Count + " item" + (items.Count == 1 ? "" : "s") + "."));
   }

   private const string SCHEMA = """
   {
      "type": "object",
      "properties": {
         "tasks": {
            "type": "array",
            "description": "Full ordered list of tasks. Re-send every item on each call; omit nothing.",
            "items": {
               "type": "object",
               "properties": {
                  "text":   { "type": "string", "description": "One-line task description." },
                  "status": {
                     "type": "string",
                     "enum": ["pending", "in_progress", "completed", "cancelled"],
                     "description": "Current state. 'in_progress' marks the active task; only one should be in_progress at a time."
                  }
               },
               "required": ["text", "status"]
            }
         }
      },
      "required": ["tasks"]
   }
   """;
}
