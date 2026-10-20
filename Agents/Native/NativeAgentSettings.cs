// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Agents.Native;

/// <summary>
/// What a plain Enter does in the prompt box. Nothing else about the input
/// changes: the other combination always does the opposite.
/// </summary>
public enum PromptSubmitMode
{
   /// <summary>Enter = new line, Shift+Enter = send. Matches the CLI agents.</summary>
   [SettingChoice("Structured text (Enter = new line, Shift+Enter = send)")]
   StructuredText = 0,

   /// <summary>Enter = send, Shift+Enter = new line. Matches chat apps.</summary>
   [SettingChoice("Vibe coder (Enter = send, Shift+Enter = new line)")]
   VibeCoder = 1,
}

/// <summary>
/// Persisted configuration for the native coding agent. Bound by the
/// reflection-driven settings system; lives inside the shared
/// <c>AgentSettingsStore</c> JSON under the agent's id.
///
/// Endpoints are modeled as named <see cref="Providers"/>, each owning its
/// own model catalog. Provider + model management has a dedicated editor /
/// discovery UI, so neither <see cref="Providers"/> is exposed as a
/// [Setting]; they round-trip through JSON persistence like any other
/// property. The active model is the pair
/// (<see cref="SelectedProvider"/>, <see cref="SelectedModel"/>) — the same
/// model id can live on two providers, so both halves are needed to identify it.
/// </summary>
public sealed class NativeAgentSettings : ISettingsMigration
{
   /// <summary>Default provider name seeded for a fresh configuration.</summary>
   public const string DEFAULT_PROVIDER_NAME = "Local";

   /// <summary>
   /// Named OpenAI-compatible endpoints. Seeded with a single local llama.cpp /
   /// llama-swap provider; edited via the provider/model catalog window.
   /// </summary>
   public List<ProviderInfo> Providers { get; set; } = _NewDefaultProviders();

   [Setting("Selected Provider", Help = "Name of the provider hosting the active model. Usually picked via the chat-view dropdown; editable here for direct configuration.")]
   public string SelectedProvider { get; set; } = DEFAULT_PROVIDER_NAME;

   [Setting("Selected Model", Help = "Wire id of the model used for new conversations, on the selected provider. Usually picked via the chat-view dropdown; editable here for direct configuration.")]
   public string SelectedModel { get; set; } = "";

   [Setting("Override temperature",
            Help = "When off, the provider picks the temperature for each request. Turn on to send the value below explicitly (including 0.0 for fully deterministic).")]
   public bool OverrideTemperature { get; set; } = false;

   [Setting("Temperature",
            Help = "Only used when 'Override temperature' is on. 0.0 = deterministic, 1.0 = creative.")]
   public float Temperature { get; set; } = 0.0f;

   [Setting("Max Tokens", Help = "Per-turn output cap. 0 = provider default.")]
   public int MaxTokens { get; set; } = 0;

   [Setting("Subagent model",
            Help = "Wire id of the model used by start_read_subagent runs, looked up on the same provider as the session's model. Empty = use the session's model. Point this at a cheaper/faster model to cut subagent cost.")]
   public string SubagentModel { get; set; } = "";

   /// <summary>
   /// Instructions prepended to every conversation. Curated to match the
   /// current tool catalog and gating model. Not exposed as a <c>[Setting]</c>
   /// (no UI for tweaking) and intentionally NOT persisted to JSON — a
   /// stale string from an older build would otherwise survive across
   /// upgrades and override the new default. Each process starts with
   /// the constant below. Resumed sessions still use whatever prompt was
   /// frozen into their meta record, so historical conversations keep
   /// their original instructions.
   /// </summary>
   [JsonIgnore]
   public string SystemPrompt { get; set; } = DEFAULT_SYSTEM_PROMPT;

   [Setting("Prompt Enter key",
            Help = "How the prompt box treats Enter. 'Structured text' keeps Enter for new lines and sends with Shift+Enter — safer for multi-line prompts. 'Vibe coder' sends on Enter and inserts a new line with Shift+Enter, like most chat apps. Takes effect immediately, including in open sessions.")]
   public PromptSubmitMode PromptSubmit { get; set; } = PromptSubmitMode.StructuredText;

   [Setting("Preserve audit trail across session deletion",
            Help = "When off (default), deleting a session also removes its entries from the project's permission audit log, and orphaned entries from sessions deleted out-of-band are cleaned up when the audit log viewer opens. Turn on to keep every entry forever — useful if you treat the audit log as a permanent record.")]
   public bool PreserveAuditOnSessionDelete { get; set; } = false;

   /// <summary>
   /// Default context window in tokens for newly discovered or manually added
   /// models. Picked to comfortably accommodate modern long-context models —
   /// per-model overrides happen in the model catalog editor.
   /// </summary>
   public const int DEFAULT_NEW_MODEL_CONTEXT = 262144;

   /// <summary>
   /// Resolve the <see cref="ProviderInfo"/> named <see cref="SelectedProvider"/>,
   /// falling back to the first configured provider (or null if there are none).
   /// </summary>
   public ProviderInfo? ResolveSelectedProvider() => ResolveProvider(SelectedProvider);

   /// <summary>
   /// Resolve a provider by name (case-insensitive), falling back to the first
   /// configured provider so a stale/blank selection still yields something usable.
   /// </summary>
   public ProviderInfo? ResolveProvider(string? name)
   {
      foreach (var p in Providers)
      {
         if (string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase))
            return p;
      }
      return Providers.Count > 0 ? Providers[0] : null;
   }

   private static List<ProviderInfo> _NewDefaultProviders() => new()
   {
      new ProviderInfo
      {
         Name    = DEFAULT_PROVIDER_NAME,
         BaseUrl = "http://localhost:8080",
         ApiKey  = "",
         Models  = new(),
      },
   };

   /// <summary>
   /// Extra skills-root directories scanned in addition to the global
   /// (<c>%APPDATA%\SlimCode\&lt;profile&gt;\skills</c>) and project-local
   /// (<c>&lt;project&gt;\.ai\skills</c> by default) locations — e.g. skill sets
   /// shipped by other vendors. Each entry is a root holding
   /// <c>&lt;name&gt;\SKILL.md</c> subfolders. Configured via JSON; no
   /// dedicated picker (round-trips like <see cref="Models"/>).
   /// </summary>
   public List<string> SkillSearchPaths { get; set; } = new();

   /// <summary>
   /// One-time migration for settings files written before the
   /// <see cref="OverrideTemperature"/> flag existed. Legacy convention
   /// was "Temperature = 0 → use provider default; otherwise send literal".
   /// If the stored JSON carries a temperature but no overrideTemperature
   /// key, we infer the legacy intent: non-zero temp meant the user wanted
   /// the override on; zero meant default-on. After this runs once, the
   /// next save persists both keys and the migration is a no-op forever.
   /// </summary>
   void ISettingsMigration.Migrate(JsonElement raw)
   {
      if (raw.ValueKind != JsonValueKind.Object) return;
      if (raw.TryGetProperty("overrideTemperature", out _)) return; // already migrated
      if (!raw.TryGetProperty("temperature", out var tempEl)) return;
      if (tempEl.ValueKind != JsonValueKind.Number) return;
      // Treat any explicit non-zero stored value as "user wanted this".
      // Zero stays in the new world as "default" with the flag off.
      OverrideTemperature = tempEl.GetSingle() > 0f;
   }

   private const string DEFAULT_SYSTEM_PROMPT =
      "You are a coding assistant working inside the user's project directory. You have " +
      "tools to inspect and modify the project:\n" +
      "\n" +
      "  read_file, list_dir, glob, grep  - read-only inspection\n" +
      "  read_image                       - look at an image file (screenshot, diagram)\n" +
      "  write_file, edit_file            - create or modify files\n" +
      "  move_file, delete_file           - move/rename or delete files and directories\n" +
      "  copy_file                        - copy files and directories\n" +
      "  start_read_subagent              - delegate a self-contained read-only research task\n" +
      "  start_write_subagent             - delegate a self-contained task that may edit files\n" +
      "\n" +
      "There is NO general shell access. You cannot run arbitrary commands. If the " +
      "project has a build or test command configured, a 'build' and/or 'test' tool " +
      "is offered (see below); call it (no arguments) to verify your changes. If no " +
      "such tool is present, that command isn't configured — don't ask to run shell " +
      "commands, just make the edits and describe how the user can verify them.\n" +
      "\n" +
      "Every tool call is gated by an interactive permission prompt the first time it " +
      "runs in a new context; read tools are typically pre-approved, writes and build/" +
      "test runs are not. Prefer the narrowest tool for the job: read before you write, " +
      "glob/grep before you read whole files, and a focused edit_file over a full " +
      "write_file when only part of a file changes.\n" +
      "\n" +
      "When the user asks a question, gather enough context with the inspection tools " +
      "before answering - don't guess about file contents. When the user asks for a " +
      "change, state the plan briefly, make the edits, and report what changed. Stop " +
      "and ask if a requested change would touch large amounts of code or delete data.\n" +
      "\n" +
      "The session has two operating modes:\n" +
      "  - IMPLEMENT (default): you can read, write, and run the configured build/test " +
      "commands subject to the permission gate. Cached approvals carry across calls.\n" +
      "  - PLAN: you focus on understanding and planning. Read tools are unrestricted; " +
      "every write_file / edit_file / build / test call triggers a fresh user prompt " +
      "regardless of prior approvals. The expectation in PLAN mode is to draft at most a single " +
      "Markdown plan file and discuss the approach — defer real code changes until the " +
      "user switches back to IMPLEMENT.\n" +
      "The host announces every mode change with a system message; honor it from that " +
      "turn onward.\n" +
      "\n" +
      "For work that spans more than a couple of steps, maintain a checklist by calling " +
      "the 'set_tasks' tool. Re-send the FULL list each time progress changes (the call " +
      "replaces the previous list — items you omit are dropped). Statuses are 'pending', " +
      "'in_progress', 'completed', and 'cancelled'; mark exactly one task 'in_progress' " +
      "while you're actively working on it. The host pins the list above the chat so " +
      "the user always sees current progress.\n" +
      "\n" +
      "Skip the task list for trivial one-shot requests. Do NOT embed task lists in " +
      "regular Markdown — only the tool call is rendered in the pinned panel.\n" +
      "\n" +
      "For broad read-only research (sweeping many files for an answer you need in " +
      "summary form), prefer the 'start_read_subagent' tool over reading everything into " +
      "your own context: the subagent burns its own context window and hands back only " +
      "a report. Its task must be fully self-contained — it cannot see this conversation " +
      "or ask questions.\n" +
      "\n" +
      "'start_write_subagent' is the same deal but with the full file tools, for a " +
      "mechanical change you can specify completely up front (apply one refactor across " +
      "many files, write a module to a spec you just settled). Its writes raise the same " +
      "permission prompts your own would, and its result lists every file it touched — " +
      "re-read those before editing them yourself. Default to the read-only subagent: " +
      "reach for the write one only when the edits are numerous and mechanical enough " +
      "that describing them costs less than making them. Never use it for work you have " +
      "already started, and never for a change that needs the user's judgement mid-way.\n" +
      "\n" +
      "Write your replies — and your reasoning — as plain text or Markdown. The host " +
      "renders Markdown (headings, lists, fenced code blocks, tables) but does NOT render " +
      "HTML: never emit HTML tags such as <div>, <p>, <br>, <b>, <ul> or <table>, they " +
      "show up as literal source. Use the Markdown equivalent instead.";

   /// <summary>
   /// Instructions for a nested subagent loop. Deliberately terse: the
   /// subagent is disposable and its entire value is the final report.
   /// The write variant additionally has the full file tools and whatever
   /// build/test commands the project configures.
   /// </summary>
   public static string BuildSubagentSystemPrompt(
      string projectPath,
      SubagentKind kind = SubagentKind.ReadOnly)
   {
      var sb = new System.Text.StringBuilder();

      if (kind == SubagentKind.Write)
      {
         sb.Append("You are an implementation subagent working inside the user's project " +
                   "directory (");
         sb.Append(projectPath);
         sb.Append("). You have inspection and file-modification tools:\n" +
                   "\n" +
                   "  read_file, list_dir, glob, grep  - read-only inspection\n" +
                   "  read_image                       - look at an image file\n" +
                   "  write_file, edit_file            - create or modify files\n" +
                   "  move_file, delete_file           - move/rename or delete files\n" +
                   "  copy_file                        - copy files and directories\n" +
                   "\n" +
                   "There is NO general shell access. You cannot ask questions — work with " +
                   "exactly the task you were given; if it turns out to be underspecified, " +
                   "stop and say so in your report rather than guessing at the intent.\n" +
                   "\n" +
                   "Read before you write, and prefer a focused edit_file over rewriting a " +
                   "whole file. Every write is gated by a permission prompt shown to the " +
                   "user, who may deny it — a denial comes back as a tool error; do not " +
                   "retry the same call in a loop, adapt or report the blockage. Stay " +
                   "inside the task's scope: you are one step of a plan the delegating " +
                   "agent is still holding.\n");

         string commands = _SubagentCommandBlock(projectPath);
         if (commands.Length > 0)
            sb.Append(commands);

         sb.Append("\nWhen you are done, END with ONE final message containing your complete " +
                   "report: what you changed and why, every file you touched, anything you " +
                   "could NOT do, and what still needs checking. That final message is the " +
                   "ONLY thing returned to the agent that delegated the task — it must be " +
                   "self-contained. Do not narrate intermediate steps.\n");
      }
      else
      {
         sb.Append("You are a read-only research subagent working inside the user's project " +
                   "directory (");
         sb.Append(projectPath);
         sb.Append("). You have inspection tools only:\n" +
                   "\n" +
                   "  read_file, list_dir, glob, grep, read_image\n" +
                   "\n" +
                   "You cannot modify files, run commands, or ask questions — work with " +
                   "exactly the task you were given. Investigate autonomously and " +
                   "efficiently: glob/grep to locate, read only what matters.\n" +
                   "\n" +
                   "When you are done, END with ONE final message containing your complete " +
                   "report: findings, relevant file paths with line references, key code " +
                   "excerpts, and clear conclusions. That final message is the ONLY thing " +
                   "returned to the agent that delegated the task — it must be " +
                   "self-contained. Do not narrate intermediate steps.\n");
      }

      sb.Append("\nWrite your report — and your reasoning — as plain text or Markdown. The " +
                "host renders Markdown but does NOT render HTML: never emit HTML tags such " +
                "as <div>, <p>, <br> or <table>, they show up as literal source. Use the " +
                "Markdown equivalent instead.");

      return sb.ToString();
   }

   // Build/test advertisement for a write subagent — same wording as the
   // main prompt's block, so a delegated step can verify itself instead of
   // handing back changes nobody compiled.
   private static string _SubagentCommandBlock(string projectPath)
   {
      try
      {
         var cfg = Config.ProjectConfigStore.LoadOrCreate(projectPath).Config;
         bool hasBuild = Config.ProjectConfig.HasRunnableSteps(cfg.BuildSteps);
         bool hasTest  = Config.ProjectConfig.HasRunnableSteps(cfg.TestSteps);
         if (!hasBuild && !hasTest) return "";

         var sb = new System.Text.StringBuilder("\nProject commands available:");
         if (hasBuild)
            sb.Append("\n- 'build' tool (no arguments): runs " + _StepSummary(cfg.BuildSteps) +
                      ". Run it before reporting; a non-zero exit means fix and retry.");
         if (hasTest)
            sb.Append("\n- 'test' tool (no arguments): runs " + _StepSummary(cfg.TestSteps) +
                      ". Use it to confirm behavior after changes.");
         sb.Append('\n');
         return sb.ToString();
      }
      catch
      {
         return "";
      }
   }

   /// <summary>
   /// Compose the effective system prompt for a new session: the persisted
   /// <see cref="SystemPrompt"/> (or the supplied override) followed by a
   /// dynamic context block carrying the session start time and the project
   /// folder. Result is captured into the session's meta record so resumes
   /// replay the exact prompt that was originally used.
   /// </summary>
   public static string BuildEffectiveSystemPrompt(
      string basePrompt,
      string projectPath,
      System.DateTime startedLocal,
      AgentMode initialMode = AgentMode.Plan,
      System.Collections.Generic.IReadOnlyList<Skills.SkillInfo>? skills = null,
      System.Collections.Generic.IReadOnlyList<Mcp.McpServerHandle>? mcpServers = null)
   {
      var sb = new System.Text.StringBuilder();
      if (!string.IsNullOrEmpty(basePrompt))
      {
         sb.Append(basePrompt);
         sb.Append("\n\n");
      }
      sb.Append("This session started ");
      sb.Append(startedLocal.ToString("yyyy-MM-dd HH:mm",
                                      System.Globalization.CultureInfo.InvariantCulture));
      sb.Append(".\n");
      sb.Append("The local project folder is ");
      sb.Append(projectPath);
      sb.Append(".\n");
      sb.Append("Initial mode: ");
      sb.Append(initialMode.ToString().ToUpperInvariant());
      sb.Append(".");

      // Project config drives both dynamic blocks below. Loaded once; if it's
      // unavailable both blocks fall back to their "nothing configured" shape.
      Config.ProjectConfig? cfg = null;
      try { cfg = Config.ProjectConfigStore.LoadOrCreate(projectPath).Config; }
      catch { /* config unavailable — omit the dependent blocks */ }

      // An MCP project has no build/test tools and no skills registered — its
      // whole tool set comes from the servers — so those two blocks are
      // skipped and the MCP block takes their place.
      bool isMcp = cfg != null && cfg.Kind == Core.ProjectKind.Mcp;
      if (isMcp)
      {
         _AppendMcpBlock(sb, mcpServers);
         return sb.ToString();
      }

      // Dynamically advertise the project's configured commands. Only the
      // configured ones are mentioned (and only those tools are registered),
      // so an unconfigured command costs nothing here.
      if (cfg != null)
      {
         bool hasBuild = Config.ProjectConfig.HasRunnableSteps(cfg.BuildSteps);
         bool hasTest  = Config.ProjectConfig.HasRunnableSteps(cfg.TestSteps);
         if (hasBuild || hasTest)
         {
            sb.Append("\n\nProject commands available:");
            if (hasBuild)
               sb.Append("\n- 'build' tool (no arguments): runs " + _StepSummary(cfg.BuildSteps) +
                         ". Use it to confirm your changes build; a non-zero exit means fix and retry.");
            if (hasTest)
               sb.Append("\n- 'test' tool (no arguments): runs " + _StepSummary(cfg.TestSteps) +
                         ". Use it to confirm behavior after changes.");
         }
      }

      // Skills catalog: names + one-line descriptions only (the cheap
      // catalog). The model pulls a skill's full body on demand via the
      // 'use_skill' tool, so an unused skill costs only its description here.
      //
      // Suppressed entirely when the project sets SkipSkillAnnouncement. That
      // only removes the advertisement — auto-load, the session picker, and
      // the 'use_skill' tool (for a skill the user names in chat) all keep
      // working without it.
      bool announce = cfg == null || !cfg.SkipSkillAnnouncement;
      if (announce && skills != null && skills.Count > 0)
      {
         sb.Append("\n\nAvailable skills (call the 'use_skill' tool with the name to load full instructions):");
         foreach (var s in skills)
         {
            sb.Append("\n- ");
            sb.Append(s.Name);
            if (!string.IsNullOrWhiteSpace(s.Description))
            {
               sb.Append(": ");
               sb.Append(s.Description);
            }
         }
      }

      return sb.ToString();
   }

   /// <summary>
   /// The MCP half of an MCP project's frozen system prompt: a short statement
   /// of what the session is, plus an INFORMATIONAL listing of the tools each
   /// server advertised.
   ///
   /// The listing is redundant with the request's tools array and says so —
   /// the model is told not to treat it as the callable definition. It exists
   /// because the user's "sys" toggle is the only place they can see what a
   /// session was actually wired to, and a prompt that omitted it would make
   /// that unanswerable after the fact. It is frozen at first send like the
   /// rest of the prompt; later catalog changes arrive as history notes
   /// instead (see the mcpTools transcript records).
   /// </summary>
   private static void _AppendMcpBlock(
      System.Text.StringBuilder sb,
      System.Collections.Generic.IReadOnlyList<Mcp.McpServerHandle>? servers)
   {
      sb.Append("\n\nThis is an MCP project. Your tools come from external Model Context ");
      sb.Append("Protocol servers rather than from this application: there are no built-in ");
      sb.Append("file, build or test tools. Each tool is named '<server>__<tool>'. Call them ");
      sb.Append("exactly as the tool definitions in this request describe them, and treat a ");
      sb.Append("failed call as recoverable — the server, not the conversation, failed.");

      if (servers == null || servers.Count == 0)
      {
         sb.Append("\n\n(No MCP server was reachable when this session started.)");
         return;
      }

      sb.Append("\n\nConnected MCP servers and the tools they advertised at session start. ");
      sb.Append("This list is informational only — it is sent to you as a tools array, not ");
      sb.Append("as prompt text, and the array is authoritative:");

      foreach (var h in servers)
      {
         sb.Append("\n\n- ").Append(h.Name);
         if (h.Identity != null && !string.IsNullOrEmpty(h.Identity.ServerName))
            sb.Append(" (").Append(h.Identity.Describe).Append(')');

         if (h.State != Mcp.McpState.Connected)
         {
            sb.Append("\n    unavailable: ").Append(h.Error ?? "not connected");
            continue;
         }
         if (h.Tools.Count == 0)
         {
            sb.Append("\n    (advertises no tools)");
            continue;
         }

         foreach (var t in h.Tools)
         {
            sb.Append("\n    ").Append(h.QualifiedName(t.Name));
            var desc = _FirstLine(t.Description);
            if (!string.IsNullOrEmpty(desc))
               sb.Append(": ").Append(desc);
         }
      }
   }

   // One line, clipped: a tool description can be paragraphs long, and the
   // full text is already travelling in the tools array.
   private static string _FirstLine(string? text)
   {
      if (string.IsNullOrWhiteSpace(text)) return "";
      var s  = text!.Replace('\r', ' ').Replace('\n', ' ').Trim();
      while (s.Contains("  ")) s = s.Replace("  ", " ");
      const int max = 140;
      return s.Length <= max ? s : s.Substring(0, max) + "…";
   }

   // "Setup, Build" — comma-joined step names (falling back to the command),
   // skipping empty-command steps. Used in the dynamic system-prompt block.
   private static string _StepSummary(System.Collections.Generic.IReadOnlyList<Config.CommandStep> steps)
   {
      var sb = new System.Text.StringBuilder();
      foreach (var s in steps)
      {
         if (string.IsNullOrWhiteSpace(s.Command)) continue;
         if (sb.Length > 0) sb.Append(", ");
         sb.Append(string.IsNullOrWhiteSpace(s.Name) ? s.Command : s.Name);
      }
      return sb.Length > 0 ? sb.ToString() + " (in order, stops at first failure)" : "(no steps)";
   }
}
