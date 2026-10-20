// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using SlimCodeDesktop.Agents.Native.Llm;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Registry of tools available to a native agent turn. Owns the mapping
/// from tool name to implementation, and produces the
/// <see cref="LlmToolSpec"/> list advertised to the provider.
/// </summary>
public sealed class ToolRegistry
{
   public ToolRegistry() { }

   public void Register(ITool tool)
   {
      _byName[tool.Name] = tool;
   }

   public bool TryGet(string name, out ITool tool)
   {
      return _byName.TryGetValue(name, out tool!);
   }

   /// <summary>Iterate every registered tool. Used by the permission editor
   /// to enumerate known tools regardless of whether the user has set a
   /// policy on them yet.</summary>
   public System.Collections.Generic.IEnumerable<ITool> All => _byName.Values;

   public IReadOnlyList<LlmToolSpec> BuildSpecs()
   {
      var specs = new List<LlmToolSpec>(_byName.Count);
      foreach (var t in _byName.Values)
      {
         specs.Add(new LlmToolSpec
         {
            Name                 = t.Name,
            Description          = t.Description,
            ParametersJsonSchema = t.ParametersJsonSchema,
         });
      }
      return specs;
   }

   /// <summary>Default exploration-mode set: read-only file inspection.</summary>
   public static ToolRegistry CreateExplorationDefaults()
   {
      var r = new ToolRegistry();
      r.Register(new ReadFileTool());
      r.Register(new ReadImageTool());
      r.Register(new ListDirTool());
      r.Register(new GlobTool());
      r.Register(new GrepTool());
      return r;
   }

   /// <summary>
   /// Full default set: exploration tools plus write + edit. No free-form
   /// shell access — the model can't run arbitrary commands. User-authored
   /// project commands (build / test) are added per-session by the host only
   /// when configured (see NativeAgentView), so they cost nothing in the tool
   /// catalog when unused.
   /// </summary>
   public static ToolRegistry CreateFullDefaults()
   {
      var r = CreateExplorationDefaults();
      r.Register(new WriteFileTool());
      r.Register(new EditFileTool());
      r.Register(new MoveFileTool());
      r.Register(new CopyFileTool());
      r.Register(new DeleteFileTool());
      return r;
   }

   /// <summary>
   /// Tool set for an MCP project: every tool the given (already connected)
   /// handles advertise, and nothing else — no file tools, no subagents, no
   /// project commands. A handle that failed to connect contributes nothing
   /// and does not fail the registry, so one dead server still leaves the
   /// others callable.
   ///
   /// Name collisions across servers cannot happen: each tool is registered
   /// under "&lt;serverPrefix&gt;__&lt;tool&gt;".
   /// </summary>
   public static ToolRegistry CreateMcp(IEnumerable<Mcp.McpServerHandle> handles)
   {
      var r = new ToolRegistry();
      if (handles == null) return r;
      foreach (var h in handles)
      {
         foreach (var info in h.Tools)
            r.Register(new McpTool(h, info));
      }
      return r;
   }

   // -- fields ---------------------------------------------------------------

   private readonly Dictionary<string, ITool> _byName = new();
}
