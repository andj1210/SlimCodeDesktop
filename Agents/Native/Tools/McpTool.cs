// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Adapts one MCP-advertised tool to <see cref="ITool"/>, so an MCP project's
/// session uses the same agent loop, permission gate, tool cards and audit log
/// as a coding session — only the registry behind them differs.
///
/// The schema travels verbatim: it is already JSON Schema, and rewriting it
/// could only introduce a mismatch between what the model is told and what the
/// server validates.
/// </summary>
public sealed class McpTool : ITool
{
   public McpTool(McpServerHandle handle, McpToolInfo info)
   {
      _handle = handle;
      _info   = info;
      Name    = handle.QualifiedName(info.Name);
   }

   /// <summary>The server-side name, without the server prefix.</summary>
   public string RemoteName => _info.Name;

   /// <summary>The server this tool belongs to.</summary>
   public McpServerHandle Handle => _handle;

   public string Name { get; }

   public string Description
   {
      get
      {
         // Name the server in the description: two servers can offer tools
         // that differ only in which system they act on, and the prefix alone
         // doesn't say what "files" or "prod" mean.
         var body = string.IsNullOrWhiteSpace(_info.Description)
                    ? "(the server supplied no description)"
                    : _info.Description;
         return "[" + _handle.Name + "] " + body;
      }
   }

   /// <summary>
   /// Read only when the server says so. An absent <c>readOnlyHint</c> is
   /// treated as Execute rather than Read: this decides whether the gate
   /// auto-allows the call, and a server that declined to annotate has not
   /// earned that.
   /// </summary>
   public ToolCategory Category =>
      _info.ReadOnlyHint == true ? ToolCategory.Read : ToolCategory.Execute;

   public string ParametersJsonSchema => _info.InputSchemaJson;

   public string ArgsPreview(JsonElement args) => ToolPreview.DefaultPreview(args);

   public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
   {
      // Raw text, not a re-serialization: the model's arguments already
      // satisfy (or fail) the server's schema, and normalizing them here
      // would hide which of the two disagreed.
      var argsJson = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();

      var result = await _handle.CallAsync(RemoteName, argsJson, ctx.Cancel).ConfigureAwait(false);
      return result.IsError
         ? new ToolResult { IsError = true, Content = result.Text }
         : ToolResult.Ok(result.Text);
   }

   // -- fields ---------------------------------------------------------------

   private readonly McpServerHandle _handle;
   private readonly McpToolInfo     _info;
}
