// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;

namespace SlimCodeDesktop.Agents.Native.Config;

/// <summary>How to reach an MCP server.</summary>
public enum McpTransportKind
{
   /// <summary>Spawn it as a child process and talk over stdin/stdout.</summary>
   Stdio,

   /// <summary>POST each message to one HTTP endpoint (MCP "Streamable HTTP").</summary>
   StreamableHttp,
}

/// <summary>
/// One MCP server a project talks to. The whole record is user-authored and
/// stored verbatim, so a server set up on one OS won't necessarily launch on
/// another — same trade as <see cref="CommandStep"/>.
/// </summary>
public sealed class McpServerConfig
{
   /// <summary>
   /// Short label, unique within the project. Doubles as the prefix in the
   /// tool names the model sees ("<c>files__read_file</c>"), which is what
   /// keeps two servers offering a <c>search</c> tool apart.
   /// </summary>
   public string Name { get; set; } = "";

   public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;

   // -- stdio ----------------------------------------------------------------

   public string Command { get; set; } = "";
   public List<string> Args { get; set; } = new();
   public Dictionary<string, string> Env { get; set; } = new();

   /// <summary>Working directory for the child process. Empty = the project folder.</summary>
   public string? Cwd { get; set; }

   // -- http -----------------------------------------------------------------

   public string Url { get; set; } = "";

   /// <summary>
   /// Extra request headers, typically an Authorization bearer token.
   ///
   /// Stored in plaintext in the project's config file for v1 — the setup
   /// dialog says so out loud. An OS credential store is the obvious next
   /// step and deliberately not a v1 blocker: the alternative today is the
   /// user pasting the same token into a shell profile instead.
   /// </summary>
   public Dictionary<string, string> Headers { get; set; } = new();

   // -- common ---------------------------------------------------------------

   public bool Enabled { get; set; } = true;

   /// <summary>
   /// Allow-list of tool names to advertise. Null (the default) means "every
   /// tool the server offers". Written by the setup dialog's per-tool
   /// checkboxes, and the v1 answer to a catalog too large for a local
   /// model's prompt budget.
   /// </summary>
   public List<string>? EnabledTools { get; set; }

   /// <summary>Per-<c>tools/call</c> timeout, in seconds.</summary>
   public int TimeoutSec { get; set; } = 60;

   public McpServerConfig Clone()
   {
      var c = new McpServerConfig
      {
         Name       = Name,
         Transport  = Transport,
         Command    = Command,
         Cwd        = Cwd,
         Url        = Url,
         Enabled    = Enabled,
         TimeoutSec = TimeoutSec,
         Args       = new List<string>(Args),
      };
      foreach (var kv in Env)     c.Env[kv.Key]     = kv.Value;
      foreach (var kv in Headers) c.Headers[kv.Key] = kv.Value;
      if (EnabledTools != null) c.EnabledTools = new List<string>(EnabledTools);
      return c;
   }

   /// <summary>True when this entry names a reachable target for its transport.</summary>
   public bool IsRunnable =>
      Transport == McpTransportKind.Stdio
         ? !string.IsNullOrWhiteSpace(Command)
         : !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// One step of a project command (build / test). Steps run in order; the run
/// stops at the first non-zero exit. <see cref="LogOnSucceed"/> controls
/// whether the step's output is forwarded to the model when it succeeds —
/// failed steps always include their log.
/// </summary>
public sealed class CommandStep
{
   /// <summary>Friendly label shown to the model, e.g. "Setup", "Build".</summary>
   public string Name { get; set; } = "";

   /// <summary>
   /// Shell command line, run by the OS shell with cwd = project root.
   /// Authored by the user in their platform's syntax and stored verbatim, so
   /// a project set up on one OS won't necessarily run on another.
   /// </summary>
   public string Command { get; set; } = "";

   /// <summary>Attach this step's log to the model even when it succeeds.</summary>
   public bool LogOnSucceed { get; set; }

   public CommandStep Clone() => new()
   {
      Name         = Name,
      Command      = Command,
      LogOnSucceed = LogOnSucceed,
   };
}

/// <summary>
/// Per-project, user-authored configuration. Holds the (multi-step) build and
/// test commands the native agent may invoke via the <c>build</c> / <c>test</c>
/// tools. A tool is exposed to the model only when its step list has at least
/// one runnable (non-empty) command.
/// </summary>
public sealed class ProjectConfig
{
   /// <summary>
   /// What this project's sessions are for. <see cref="ProjectKind.Mcp"/>
   /// swaps the built-in coding tools for the ones
   /// <see cref="McpServers"/> advertise; everything else about a session
   /// (transcript, resume, permission gate, mode pills) is unchanged.
   ///
   /// Written once, when the project is added, and never edited afterwards:
   /// the kind also selects the project's storage slug, so it is recorded in
   /// the sidebar's projects.json (which is readable before any per-project
   /// file can be located) and mirrored here for the agent side, which reaches
   /// it from a project path alone.
   /// </summary>
   public ProjectKind Kind { get; set; } = ProjectKind.Code;

   /// <summary>MCP servers this project's sessions connect to. Only read when
   /// <see cref="Kind"/> is <see cref="ProjectKind.Mcp"/>.</summary>
   public List<McpServerConfig> McpServers { get; set; } = new();

   public List<CommandStep> BuildSteps { get; set; } = new();
   public List<CommandStep> TestSteps  { get; set; } = new();

   /// <summary>
   /// Names of discovered skills to auto-load at the start of every new
   /// session for this project (their full instructions are injected before
   /// the first user turn). Resumed sessions don't re-trigger this — the
   /// skill bodies are already in their persisted history.
   /// </summary>
   public List<string> AutoLoadSkills { get; set; } = new();

   /// <summary>
   /// Suppress the skills catalog block in the system prompt (names +
   /// one-line descriptions of every discovered skill). Leans the prompt for
   /// projects where the model should not go shopping for skills on its own.
   ///
   /// Explicit loading is unaffected: <see cref="AutoLoadSkills"/> still
   /// injects bodies at session start, the session skill picker still works,
   /// and the <c>use_skill</c> tool stays registered so the user can name a
   /// skill in chat. Only the unsolicited advertisement goes away.
   /// </summary>
   public bool SkipSkillAnnouncement { get; set; }

   /// <summary>
   /// Per-folder narrowing of the file tools (see <see cref="FolderAccessPolicy"/>).
   /// Build / test steps are not affected. Only read for a code project.
   /// </summary>
   public List<FolderRule> FolderRules { get; set; } = new();

   /// <summary>Per-step timeout (seconds) applied to each build step.</summary>
   public int BuildTimeoutSec { get; set; } = 120;

   /// <summary>Per-step timeout (seconds) applied to each test step.</summary>
   public int TestTimeoutSec { get; set; } = 120;

   public ProjectConfig Clone()
   {
      var c = new ProjectConfig
      {
         Kind                  = Kind,
         BuildTimeoutSec       = BuildTimeoutSec,
         TestTimeoutSec        = TestTimeoutSec,
         SkipSkillAnnouncement = SkipSkillAnnouncement,
      };
      foreach (var s in BuildSteps) c.BuildSteps.Add(s.Clone());
      foreach (var s in TestSteps)  c.TestSteps.Add(s.Clone());
      foreach (var m in McpServers) c.McpServers.Add(m.Clone());
      c.AutoLoadSkills = new List<string>(AutoLoadSkills);
      c.FolderRules    = new List<FolderRule>(FolderRules);
      return c;
   }

   /// <summary>Rules a newly added code project starts with. The user can edit or remove them.</summary>
   public static List<FolderRule> DefaultFolderRules() => new()
   {
      new FolderRule(".git", FolderAccess.NoAccess, AnyDepth: true),
   };

   /// <summary>
   /// The policy for this project's file tools. Rules that cannot be parsed
   /// (only possible by hand-editing project.json; the setup dialog refuses
   /// them) lock the whole project instead of being dropped: a dropped rule
   /// would leave its folder open while the user believes it is protected.
   /// </summary>
   public FolderAccessPolicy BuildFolderPolicy()
   {
      if (Kind != ProjectKind.Code || FolderRules.Count == 0)
         return FolderAccessPolicy.Empty;
      try
      {
         return new FolderAccessPolicy(FolderRules);
      }
      catch (ArgumentException)
      {
         return new FolderAccessPolicy(new[] { new FolderRule(".", FolderAccess.NoAccess) });
      }
   }

   /// <summary>True if any step carries a non-empty command.</summary>
   public static bool HasRunnableSteps(IReadOnlyList<CommandStep> steps)
   {
      foreach (var s in steps)
         if (!string.IsNullOrWhiteSpace(s.Command)) return true;
      return false;
   }
}
