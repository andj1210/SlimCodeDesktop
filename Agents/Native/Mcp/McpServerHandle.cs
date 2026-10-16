// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Config;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>
/// One configured MCP server, bound to one session. Owns the
/// <see cref="McpClient"/>, connects lazily on the session's first send, and
/// exposes enough state for a status dot in the header.
///
/// Failure is contained here on purpose: <see cref="ConnectAsync"/> never
/// throws. One unreachable server out of three must leave the other two
/// usable, so a failure becomes state plus a chat error rather than an
/// exception that would take the whole turn down.
/// </summary>
public sealed class McpServerHandle : IDisposable
{
   public McpServerHandle(McpServerConfig config, string projectPath)
   {
      Config       = config ?? throw new ArgumentNullException(nameof(config));
      _projectPath = projectPath ?? "";
      Name         = string.IsNullOrWhiteSpace(config.Name) ? "server" : config.Name.Trim();
      Prefix       = SanitizeSegment(Name);
   }

   public McpServerConfig Config { get; }

   /// <summary>The user's label for this server.</summary>
   public string Name { get; }

   /// <summary>Tool-name prefix derived from <see cref="Name"/>.</summary>
   public string Prefix { get; }

   public McpState State => _client?.State ?? _state;

   /// <summary>Failure reason when <see cref="State"/> is Failed, else null.</summary>
   public string? Error => _client?.FailureReason ?? _error;

   /// <summary>Identity reported at initialize, or null before connecting.</summary>
   public McpServerIdentity? Identity => _client?.Identity;

   /// <summary>
   /// Tools this handle currently advertises: what the server listed, filtered
   /// by <see cref="McpServerConfig.EnabledTools"/>. Empty before connecting.
   /// </summary>
   public IReadOnlyList<McpToolInfo> Tools => _tools;

   /// <summary>Every tool the server listed, allow-list ignored. For the setup dialog.</summary>
   public IReadOnlyList<McpToolInfo> AllTools => _allTools;

   /// <summary>Fires (background thread) when the server's catalog changed and was re-listed.</summary>
   public event Action<McpServerHandle>? ToolsChanged;

   /// <summary>Fires (background thread) on any state transition.</summary>
   public event Action<McpServerHandle>? StatusChanged;

   /// <summary>
   /// Connect, initialize and list tools. Returns true on success; on failure
   /// the reason is in <see cref="Error"/> and the handle stays usable as a
   /// "failed" status source.
   /// </summary>
   public async Task<bool> ConnectAsync(CancellationToken cancel)
   {
      if (State == McpState.Connected) return true;

      _state = McpState.Connecting;
      _error = null;
      StatusChanged?.Invoke(this);

      try
      {
         if (!Config.IsRunnable)
            throw new InvalidOperationException(
               Config.Transport == McpTransportKind.Stdio
                  ? "no command configured"
                  : "no URL configured");

         var transport = CreateTransport(Config, _projectPath);
         var client    = new McpClient(transport);
         client.Log += line => _log.Invoke(Name, line);
         client.ToolsListChanged += _OnServerListChanged;

         _client = client;

         await client.InitializeAsync(cancel).ConfigureAwait(false);
         await _RefreshToolsAsync(cancel).ConfigureAwait(false);

         _state = McpState.Connected;
         StatusChanged?.Invoke(this);
         return true;
      }
      catch (Exception ex)
      {
         _state = McpState.Failed;
         _error = ex is OperationCanceledException ? "cancelled" : ex.Message;
         // Leave nothing running behind a failed connect: a stdio server that
         // started but failed to negotiate is still a live child process.
         try { _client?.Dispose(); } catch { }
         _client = null;
         StatusChanged?.Invoke(this);
         return false;
      }
   }

   /// <summary>
   /// Re-run <c>tools/list</c> and publish the result. Used by the
   /// list_changed notification and by the setup dialog's Test connection.
   /// </summary>
   public async Task RefreshToolsAsync(CancellationToken cancel)
   {
      if (_client == null || _client.State != McpState.Connected) return;
      await _RefreshToolsAsync(cancel).ConfigureAwait(false);
      ToolsChanged?.Invoke(this);
   }

   /// <summary>
   /// Call one of this server's tools. <paramref name="toolName"/> is the
   /// server-side name, not the prefixed one the model sees.
   /// </summary>
   public async Task<McpCallResult> CallAsync(string toolName, string argumentsJson,
                                              CancellationToken cancel)
   {
      var client = _client;
      if (client == null || client.State != McpState.Connected)
         return McpCallResult.Failure(
            "MCP server '" + Name + "' is not connected" +
            (string.IsNullOrEmpty(Error) ? "" : ": " + Error));

      // Per-call budget on top of the caller's token: a server that accepts a
      // request and never answers would otherwise hold the turn open forever,
      // and the transport has no timeout of its own by design.
      int seconds = Config.TimeoutSec > 0 ? Config.TimeoutSec : 60;
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
      using var linked  = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);

      try
      {
         return await client.CallToolAsync(toolName, argumentsJson, linked.Token)
                            .ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancel.IsCancellationRequested)
      {
         return McpCallResult.Failure("tool call timed out after " + seconds + "s");
      }
   }

   /// <summary>Short line for a status tooltip.</summary>
   public string Describe()
   {
      var sb = new StringBuilder(Name).Append(" — ");
      switch (State)
      {
         case McpState.NotConnected: sb.Append("not connected (connects on first send)"); break;
         case McpState.Connecting:   sb.Append("connecting…"); break;
         case McpState.Connected:
            sb.Append("connected");
            if (Identity != null) sb.Append('\n').Append(Identity.Describe);
            sb.Append('\n').Append(Tools.Count).Append(" tool(s) advertised");
            break;
         case McpState.Failed:       sb.Append("failed: ").Append(Error ?? "unknown reason"); break;
      }
      sb.Append('\n').Append(Config.Transport == McpTransportKind.Stdio
                             ? "stdio: " + Config.Command
                             : "http: "  + Config.Url);
      return sb.ToString();
   }

   public void Dispose()
   {
      var client = _client;
      _client = null;
      try { client?.Dispose(); } catch { }
      _state = McpState.NotConnected;
   }

   // -- static helpers -------------------------------------------------------

   /// <summary>
   /// Build the transport one config calls for. A stdio server with no
   /// explicit cwd runs in the project folder, which is what a filesystem
   /// server almost always wants.
   /// </summary>
   public static IMcpTransport CreateTransport(McpServerConfig cfg, string projectPath)
   {
      if (cfg.Transport == McpTransportKind.StreamableHttp)
         return new McpHttpTransport(cfg.Url, cfg.Headers);

      var cwd = string.IsNullOrWhiteSpace(cfg.Cwd) ? projectPath : cfg.Cwd;
      return new McpStdioTransport(cfg.Command, cfg.Args, cfg.Env, cwd);
   }

   /// <summary>
   /// Reduce one name to what a provider will accept in a tool name. The
   /// OpenAI schema allows <c>[A-Za-z0-9_-]</c> only, and a server is free to
   /// name itself anything, so the mapping has to be lossy rather than
   /// optimistic — a rejected tool definition fails the whole request.
   /// </summary>
   public static string SanitizeSegment(string s)
   {
      var sb = new StringBuilder(s.Length);
      foreach (var ch in s)
      {
         bool keep = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                     (ch >= '0' && ch <= '9') || ch == '_' || ch == '-';
         sb.Append(keep ? ch : '_');
      }
      var result = sb.ToString().Trim('_');
      return result.Length > 0 ? result : "mcp";
   }

   /// <summary>The name the model sees for one of this server's tools.</summary>
   public string QualifiedName(string toolName) =>
      Prefix + NAME_SEPARATOR + SanitizeSegment(toolName);

   /// <summary>Separator between the server prefix and the server-side tool name.</summary>
   public const string NAME_SEPARATOR = "__";

   // -- private --------------------------------------------------------------

   private async Task _RefreshToolsAsync(CancellationToken cancel)
   {
      var client = _client;
      if (client == null) return;

      var listed = await client.ListToolsAsync(cancel).ConfigureAwait(false);
      _allTools = listed;

      var allow = Config.EnabledTools;
      if (allow == null)
      {
         _tools = listed;
         return;
      }

      var set  = new HashSet<string>(allow, StringComparer.Ordinal);
      var kept = new List<McpToolInfo>(listed.Count);
      foreach (var t in listed)
         if (set.Contains(t.Name)) kept.Add(t);
      _tools = kept;
   }

   // The server announced a catalog change. Re-list on a background task so
   // the transport's reader thread isn't blocked on a round trip it is itself
   // responsible for delivering.
   private void _OnServerListChanged()
   {
      _ = Task.Run(async () =>
      {
         try
         {
            await _RefreshToolsAsync(CancellationToken.None).ConfigureAwait(false);
            ToolsChanged?.Invoke(this);
         }
         catch { /* a failed re-list leaves the previous catalog in place */ }
      });
   }

   // Assigned by the host so a server's stderr can reach the transcript.
   private Action<string, string> _log = (_, _) => { };

   /// <summary>Route this server's diagnostics somewhere visible.</summary>
   public void SetLogSink(Action<string, string> sink) => _log = sink ?? ((_, _) => { });

   // -- fields ---------------------------------------------------------------

   private readonly string _projectPath;

   private McpClient? _client;
   private McpState   _state = McpState.NotConnected;
   private string?    _error;

   private IReadOnlyList<McpToolInfo> _tools    = Array.Empty<McpToolInfo>();
   private IReadOnlyList<McpToolInfo> _allTools = Array.Empty<McpToolInfo>();
}
