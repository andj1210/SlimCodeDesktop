// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

// Resolve ambiguity: Avalonia.Interactivity defines RoutingStrategies too,
// keeping it qualified makes the AddHandler call below readable.
using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Views.Native;
using SkiaSharp;

// `using Avalonia;` brings the Avalonia.Platform namespace into scope, which
// makes the bare name `Platform` ambiguous with our own static class.
using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views;

/// <summary>
/// Embedded view for <see cref="NativeAgent"/> — replaces the terminal pane
/// in the main window when a native agent is the active agent.
///
/// Stateful: holds one <see cref="AgentLoop"/> per project session so the
/// model has continuity across multiple Send clicks. <see cref="Configure"/>
/// (re)binds the view to a specific agent + project + settings store;
/// <see cref="Reset"/> drops the loop, e.g. when switching projects.
///
/// Round C: transcript is a stack of role-styled bubbles + collapsible
/// tool-call cards (<see cref="UserBubble"/>, <see cref="AssistantBubble"/>,
/// <see cref="ToolCallCard"/>, <see cref="ErrorLine"/>). Streaming text
/// accumulates inside the active <see cref="AssistantBubble"/>; a single
/// DispatcherTimer flushes the markdown re-parse at ~20 FPS so we don't
/// rebuild the tree per token.
/// </summary>
public partial class NativeAgentView : UserControl
{
   public NativeAgentView()
   {
      InitializeComponent();
      _BuildStatsBars();

      // TextBox.AcceptsReturn=True swallows Enter for newline insertion via
      // its class handler on the bubbling KeyDown event — by the time a
      // plain XAML KeyDown handler runs, the event is already marked
      // Handled and Shift+Enter never reaches us. Subscribing on the
      // tunneling phase with handledEventsToo=true lets us intercept
      // before TextBox's editing logic sees the keystroke.
      TxtInput.AddHandler(KeyDownEvent, TxtInput_KeyDown,
                          RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                          handledEventsToo: true);
      TxtInput.AddHandler(KeyUpEvent, TxtInput_KeyUp,
                          RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                          handledEventsToo: true);

      // Autoscroll-latch: by default we follow new content, but the moment
      // the user scrolls up we stop dragging them back down. Re-latching
      // happens automatically when they scroll all the way back to the
      // bottom.
      _scrollLatch = new ScrollLatch(LogScroll);
      _scrollLatch.LatchChanged += OnScrollLatchChanged;

      // Pseudo-modal permission overlay: panel buttons resolve the pending
      // gate await; Escape anywhere on the overlay is a one-shot Deny. The
      // overlay Border is focusable so keyboard input can't leak through
      // to the (visually blocked) chat below.
      PermPanel.Completed += OnPromptCompleted;
      PermissionOverlay.AddHandler(KeyDownEvent, PermissionOverlay_KeyDown,
                                   RoutingStrategies.Tunnel,
                                   handledEventsToo: true);

      // Image attachments arrive by dropping files onto the input area. The
      // handler goes on the whole input bar rather than the TextBox so the
      // target is the full-width strip the user aims at, and so a drop still
      // lands when the thumbnail row has pushed the box down.
      DragDrop.SetAllowDrop(InputBar, true);
      InputBar.AddHandler(DragDrop.DragOverEvent, InputBar_DragOver);
      InputBar.AddHandler(DragDrop.DropEvent,     InputBar_Drop);

      // ...and by pasting one. PastingFromClipboard rather than a Ctrl+V key
      // check so Shift+Insert and the context menu's Paste work too.
      TxtInput.PastingFromClipboard += TxtInput_PastingFromClipboard;

      // Clipboard wizard. The indicator button is raised by
      // RefreshClipboardOffer (window activation / tab switch); clicking it
      // opens the crop overlay, which reports back the kept rectangle.
      CropOverlay.Accepted  += _CropAccepted;
      CropOverlay.Cancelled += _CropCancelled;

      // Clicking any thumbnail — staged or already sent — opens it full size.
      Attachments.Preview += _ShowImagePreview;
      ImagePreviewOverlay.PointerPressed += ImagePreviewOverlay_PointerPressed;
      ImagePreviewOverlay.AddHandler(KeyDownEvent, ImagePreviewOverlay_KeyDown,
                                     RoutingStrategies.Tunnel,
                                     handledEventsToo: true);

      _RefreshModeUi();
   }

   // -- public API -----------------------------------------------------------

   /// <summary>
   /// Bind the view to a project session. If <paramref name="resumeSessionId"/>
   /// is non-empty, the corresponding JSONL transcript is loaded from disk:
   /// the visual transcript and the in-memory <see cref="AgentLoop"/> history
   /// are both rebuilt so the next Send continues the conversation seamlessly.
   /// </summary>
   public void Configure(NativeAgent agent, string projectPath, AgentSettingsStore store, string? resumeSessionId = null)
   {
      _agent       = agent ?? throw new ArgumentNullException(nameof(agent));
      _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
      _store       = store ?? throw new ArgumentNullException(nameof(store));

      // Seed this tab's model selection from the app-wide default. From here
      // on the selection is view-local: another tab (or a resume elsewhere)
      // changing the global default must never touch this session's model.
      _selProvider = agent.Settings.SelectedProvider;
      _selModel    = agent.Settings.SelectedModel;

      _RefreshPromptHints();

      Reset();

      // Build the permission gate for this project. Both the project policy
      // and the audit log live under the same per-project metadata directory.
      var globalStore  = GlobalPolicyStore.LoadOrCreate();
      var projectStore = ProjectPolicyStore.LoadOrCreate(projectPath);
      var metaDir      = ProjectPolicyStore.ProjectMetadataDir(projectPath);
      var auditLog     = new PermissionAuditLog(System.IO.Path.Combine(metaDir!, "permissions.log.jsonl"));
      _promptService  = new PermissionPromptService();
      _promptService.SetHandler(ShowPermissionPrompt);
      _gate = new PermissionGate(globalStore, projectStore, auditLog, _promptService, projectPath, sessionId: null);
      _WireGateEvents(_gate);

      RefreshModelCombo();
      UpdateGaugeWindow();
      RefreshStatus();

      // Build (but don't connect) the MCP handles so the header states the
      // project's servers and their "not connected" status right away.
      _EnsureMcpHandles();

      if (!string.IsNullOrWhiteSpace(resumeSessionId))
         ResumeSession(resumeSessionId!);
   }

   /// <summary>Project path the view is currently bound to, or null if not configured.</summary>
   public string? ProjectPath => _projectPath;

   /// <summary>
   /// True iff there is an active conversation (session file is open and the
   /// loop holds history). Used by the host to decide whether navigating to
   /// this project should drop the user into the chat or into the picker.
   /// </summary>
   public bool HasActiveSession => _sessionId != null;

   /// <summary>
   /// Id of the session this view currently holds, or null when idle. Lets the
   /// session picker mark the row that is already open in a tab.
   /// </summary>
   public string? CurrentSessionId => _sessionId;

   /// <summary>
   /// Fires when the user explicitly closes the chat via the Close button.
   /// The host clears any "active project" state so the sidebar reflects
   /// that no session is running. Internal Reset() calls (resume, fork,
   /// project switch) intentionally do NOT raise this — they aren't a
   /// session end from the user's perspective.
   /// </summary>
   public event Action? SessionClosedByUser;

   /// <summary>
   /// Fires (UI thread) when this session was named automatically, so the host
   /// can re-render the pickers that are showing the old label.
   /// </summary>
   public event Action? SessionTitled;

   /// <summary>
   /// Fires (UI thread) when a permission prompt overlay is raised/resolved.
   /// The tab host uses this for the attention badge on a background tab.
   /// </summary>
   public event Action<bool>? PromptPendingChanged;

   /// <summary>
   /// Fires (UI thread) when an agent turn starts/finishes. Drives the
   /// per-tab busy indicator in the tab strip.
   /// </summary>
   public event Action<bool>? RunningChanged;

   /// <summary>True while a permission prompt overlay is awaiting the user.</summary>
   public bool HasPendingPrompt => _pendingPrompt != null;

   /// <summary>True while an agent turn is in flight.</summary>
   public bool IsRunning => _running;

   /// <summary>
   /// True when the composer holds unsent work: typed text or staged images.
   /// The host treats that as "the user is busy with this session" and keeps
   /// a click in the session picker from rebinding the tab underneath them.
   /// </summary>
   public bool HasDraftInput => !string.IsNullOrWhiteSpace(TxtInput.Text) || Attachments.Count > 0;

   /// <summary>
   /// Host nudge for tab switching: the view refreshes model combo / status
   /// on its own IsVisible flip, but returning from the config overlay only
   /// toggles the tab *host*'s visibility — the child property never changes,
   /// so the host calls this explicitly.
   /// </summary>
   public void NotifyBecameVisible() => OnBecameVisible();

   public void Reset()
   {
      _AbandonPendingPrompt("session closed while prompt was pending");
      CancelIfRunning();
      // An auto-title still in flight belongs to the session being dropped.
      // Its file would be named correctly, but nobody is waiting for it —
      // and the request itself would compete with whatever comes next.
      // Cancelled, not disposed: the call may still be holding the token, and
      // disposing it under a live registration throws inside that task.
      try { _titleCts?.Cancel(); } catch { }
      _titleCts = null;
      StopFlushTimer();
      _loop = null;
      _currentAssistant = null;
      _toolCards.Clear();
      _streamingCards.Clear();
      _currentToolRun.Clear();
      _currentToolGroup = null;
      _userBubbles.Clear();
      _assistantBubbles.Clear();
      _committedAssistantTurns = 0;
      _systemBubbles.Clear();
      _loadedSkills.Clear();
      _pendingSkills.Clear();
      _skillCallArgNames.Clear();
      _sessionSystemPrompt = null;

      // MCP connections belong to the session, not the tab: dropping the
      // session kills the stdio children with it. The next session's first
      // send connects again from the (possibly edited) project config.
      _DisposeMcpHandles();
      _mcpReady          = false;
      _mcpConnecting     = false;
      _lastKnownMcpTools = null;
      _RefreshMcpStatus();

      _scrollLatch.Arm();
      TaskPanel?.Clear();
      Attachments.Clear();
      _HideImagePreview();
      _selectedMode = AgentMode.Plan;
      _appliedMode  = AgentMode.Plan;
      _RefreshModeUi();
      TranscriptPanel.Children.Clear();
      Gauge.UsedTokens = 0;
      _sumInput = _sumCached = _sumOutput = _sumReasoning = _sumCacheWrite = 0;
      Gauge.ClearUsage();
      _ResetStats();

      // Abandon (but do not delete) the in-progress session file. The user
      // can resume it later from the sidebar; we start a fresh file on the
      // next Send.
      _transcriptStore = null;
      _sessionId       = null;

      // The gate is per-project and reused across sessions; drop its session
      // grants so they don't leak into the next session loaded into this view.
      _gate?.ClearSessionCache();
   }

   // -- send / cancel --------------------------------------------------------

   private async void BtnSend_Click(object? sender, RoutedEventArgs e)
   {
      if (_running || _mcpConnecting || _agent == null || _store == null) return;

      var s = _agent.Settings;
      var prov = s.ResolveProvider(_selProvider);
      if (prov == null || string.IsNullOrWhiteSpace(prov.BaseUrl)) { AppendError("No provider configured — add one with an endpoint URL in the model catalog."); return; }
      if (string.IsNullOrWhiteSpace(_selModel)){ AppendError("No model selected — click Discover or set one in configuration."); return; }
      if (_projectPath == null || !Directory.Exists(_projectPath))
      {
         AppendError("Project folder is missing: " + _projectPath);
         return;
      }

      var query  = TxtInput.Text ?? "";
      var images = Attachments.Snapshot();
      // Images alone are a legitimate turn — dropping a screenshot and
      // hitting Send reads as "look at this", and the model gets a content
      // array with an image part and no text.
      if (string.IsNullOrWhiteSpace(query) && images.Count == 0)
         return;

      // The user pressed Send: they want to see what happens next. Re-arm
      // the latch regardless of where they were reading before.
      _scrollLatch.Arm();

      // MCP projects connect here, before anything else touches the session:
      // the frozen system prompt lists the advertised tools, so the servers
      // have to have answered tools/list before EnsureNewSession runs. No-op
      // for a coding project and for every send after the first.
      if (_IsMcpProject() && !_mcpReady)
      {
         await _ConnectMcpAsync();
         // Cancelled during the connect (or the tab was reset under us):
         // don't push a turn into a session that no longer wants one.
         if (!_mcpReady) return;

         // A RESUMED session already built its loop — at resume time, when no
         // server had answered yet and the registry was necessarily empty.
         // Hand it the real tool set now, before the turn goes out.
         if (_loop != null)
         {
            _viewToolRegistry = _BuildToolRegistry(withTaskCallback: true);
            _loop.UpdateTools(_viewToolRegistry);
         }
      }

      // Ensure the loop + transcript file exist before we touch either
      // (commit-mode-change writes into both, and the user bubble's
      // NoticeLine must sit *above* it in render order).
      if (_loop == null)
      {
         EnsureNewSession();
         // Name the session from this prompt. Fired here — before the turn's
         // request goes out — because the session has no warm prompt cache
         // yet, so this is the one moment where a side call has nothing to
         // evict. Never awaited: the answer is worth a few seconds, the
         // user's first reply is not.
         _StartAutoTitle(query, prov);
         _loop = BuildLoop(s);
         SubscribePersistence(_loop);
         // Tool definitions directly behind the system prompt bubble (added
         // in EnsureNewSession) and ahead of any skill injections below —
         // mirrors the order the model sees the rendered prompt in.
         _AppendToolSpecsBubble();
      }

      // Record the advertised MCP catalog (and, on a resume whose servers have
      // changed, tell the model what moved). Needs both the loop and the
      // transcript file, so it lands right after they exist.
      _SyncMcpCatalog();

      // Inject any user-selected / auto-load skills before the user turn so
      // their instructions are in context for this message.
      _DrainPendingSkills();

      // Order matches the user's mental model:
      //   1. change mode    (NoticeLine + system message + mode record)
      //   2. send prompt    (user bubble + run)
      _CommitPendingModeChange();
      AppendUserBubble(query.Trim(), _appliedMode, images);
      TxtInput.Text = "";
      Attachments.Clear();

      await _RunTurn(ct => _loop!.RunAsync(query, images, ct));
   }

   /// <summary>
   /// Run one drive of the loop with the view's busy state around it: send /
   /// cancel buttons, the working indicator, and the final flush that keeps a
   /// cancelled turn's partial output on screen.
   ///
   /// Shared by the send path and the branch re-runs, which differ only in
   /// which loop entry point they call.
   /// </summary>
   private async Task _RunTurn(Func<CancellationToken, Task> drive)
   {
      _running = true;
      // A turn that was cancelled offers a regenerate on its bubble, and that
      // offer is only valid while nothing is running — withdraw it now
      // rather than leaving a link that answers with an error.
      _RefreshBranchAffordances();
      _cts = new CancellationTokenSource();
      BtnSend.IsEnabled = false;
      BtnCancel.IsEnabled = true;
      if (StatusSplitter != null) StatusSplitter.IsWorking = true;
      RunningChanged?.Invoke(true);

      try
      {
         await drive(_cts.Token);
      }
      catch (Exception ex)
      {
         AppendError(ex.Message);
      }
      finally
      {
         // Final flush so any tail tokens land in the assistant bubble — and
         // so a call cancelled mid-arguments keeps the text it did receive
         // on screen rather than freezing one tick short of it.
         _currentAssistant?.Flush();
         _FlushStreamingCards();
         _streamingCards.Clear();
         StopFlushTimer();

         _running = false;
         _cts?.Dispose();
         _cts = null;
         BtnSend.IsEnabled = true;
         BtnCancel.IsEnabled = false;
         if (StatusSplitter != null) StatusSplitter.IsWorking = false;
         RunningChanged?.Invoke(false);

         // The turn just committed is now a fork point, and any turn whose
         // bubble was live is finished. Deliberately after _running is
         // cleared: an uncommitted tail bubble is only branchable while idle.
         _RefreshBranchAffordances();
      }
   }

   /// <summary>
   /// Compose the tool set for this project. Starts from the sandboxed
   /// defaults (no free-form shell) and exposes the user-authored build /
   /// test commands ONLY when configured — an unset command means the tool
   /// is never advertised to the model, costing nothing in prompt length.
   /// <paramref name="withTaskCallback"/> wires set_tasks to the live panel
   /// (loop use); the permission editor passes false (it only needs the tool
   /// list, and set_tasks isn't shown there).
   /// </summary>
   private ToolRegistry _BuildToolRegistry(bool withTaskCallback)
   {
      // An MCP project's tool set is whatever its servers advertise, and
      // nothing else: no file tools, no project commands, no subagents (a
      // nested loop would need its own connections and its own catalog, which
      // is not a v1 problem worth solving). Before the first send the handles
      // have no tools yet and this is legitimately empty.
      if (_IsMcpProject())
         return ToolRegistry.CreateMcp(_mcpHandles);

      var tools = ToolRegistry.CreateFullDefaults();

      if (withTaskCallback)
      {
         tools.Register(new SetTasksTool(items =>
            Dispatcher.UIThread.Post(() => TaskPanel.SetTasks(items))));
      }

      // Two tools, not one tool with a mode argument: ITool.Category is
      // static, so this is what lets the read-only variant stay honestly
      // categorized Read (auto-allowed, no prompts) while the write variant
      // is gated as Write. It also gives the user two independent policy
      // entries — "allow research, ask for writes" — which is why these are
      // registered even for the permission editor's list (withTaskCallback
      // false): the factory is never invoked from there.
      tools.Register(new SubagentTool(SubagentKind.ReadOnly, _StartSubagentRun));
      tools.Register(new SubagentTool(SubagentKind.Write,    _StartSubagentRun));

      _RegisterProjectCommands(tools);

      // Deny existence when no skills are configured for this project: the
      // use_skill tool — and the catalog block — only appear once at least
      // one skill is discovered. The resolver reads fresh per call so a
      // newly-added skill becomes loadable without rebuilding the loop.
      if (_DiscoverSkills().Count > 0)
         tools.Register(new UseSkillTool(_DiscoverSkills));

      return tools;
   }

   /// <summary>
   /// Advertise the user-authored build / test commands, if any are
   /// configured. Shared by the session registry and the write subagent's,
   /// so a delegated implementation step can verify itself instead of
   /// handing back changes nobody compiled.
   /// </summary>
   private void _RegisterProjectCommands(ToolRegistry tools)
   {
      if (string.IsNullOrEmpty(_projectPath)) return;

      var cfg = ProjectConfigStore.LoadOrCreate(_projectPath).Config;
      if (ProjectConfig.HasRunnableSteps(cfg.BuildSteps))
         tools.Register(new ProjectCommandTool(
            "build",
            "Compile/build the project using its configured steps to verify your " +
            "changes compile. Takes no arguments — the steps are set by the user.",
            c => (c.BuildSteps, c.BuildTimeoutSec)));
      if (ProjectConfig.HasRunnableSteps(cfg.TestSteps))
         tools.Register(new ProjectCommandTool(
            "test",
            "Run the project's configured test steps to verify behavior. Takes no " +
            "arguments — the steps are set by the user.",
            c => (c.TestSteps, c.TestTimeoutSec)));
   }

   private ITool? _LookupTool(string name)
   {
      if (_viewToolRegistry != null && _viewToolRegistry.TryGet(name, out var t))
         return t;
      return null;
   }

   private AgentLoop BuildLoop(NativeAgentSettings s, IReadOnlyList<ChatMessage>? history = null)
   {
      var prov   = s.ResolveProvider(_selProvider);
      var client = new OpenAiCompatClient(prov?.BaseUrl ?? "", NullIfBlank(prov?.ApiKey));
      var tools  = _BuildToolRegistry(withTaskCallback: true);
      var ctx    = new ToolContext(_projectPath!, CancellationToken.None);
      // Keep the loop's registry around for view concerns: tool-card header
      // previews (ITool.ArgsPreview) and the tools bubble.
      _viewToolRegistry = tools;

      // The effective prompt is set by EnsureNewSession (new chat) or by
      // ResumeSession (loaded from the JSONL meta record). It already
      // includes any dynamic trailing context such as session start time
      // and project folder.
      var effectivePrompt = _sessionSystemPrompt ?? s.SystemPrompt;

      var loop = new AgentLoop(client, tools, ctx,
                               _selModel,
                               effectivePrompt,
                               history,
                               gate: _gate);
      // Temperature is opt-in: only sent when the user has explicitly
      // enabled the override. Otherwise the provider's default is used,
      // which is what most cloud + local servers want anyway.
      loop.Temperature = s.OverrideTemperature ? s.Temperature : (float?)null;
      loop.MaxTokens   = s.MaxTokens   > 0 ? s.MaxTokens   : (int?)null;

      // One stable cache key per loop instance pins all turns of this
      // conversation to the same prompt-cache partition on routed providers.
      // Generated once here (not per turn) — a per-request key would scatter
      // the cache. A resumed session gets a fresh key, which is fine: the
      // remote cache is cold by then anyway.
      loop.CacheKey = Guid.NewGuid().ToString("N");

      _ConfigureReasoning(loop, prov, _selModel);

      _loopModelId  = _selModel;
      _loopProvider = _selProvider;
      WireEvents(loop);
      return loop;
   }

   private void BtnCancel_Click(object? sender, RoutedEventArgs e) => CancelIfRunning();

   // -- mode toggle ----------------------------------------------------------

   private void BtnModePlan_Click(object? sender, RoutedEventArgs e)
      => _SelectMode(AgentMode.Plan);

   private void BtnModeImplement_Click(object? sender, RoutedEventArgs e)
      => _SelectMode(AgentMode.Implement);

   private void _SelectMode(AgentMode mode)
   {
      _selectedMode = mode;
      _RefreshModeUi();
   }

   private void _RefreshModeUi()
   {
      // Active pill matches the user-bubble color for its mode so the
      // bottom indicator visually echoes the bubble's tint:
      //   Plan      → ochre (matches Plan bubble)
      //   Implement → accent dark (matches Implement bubble)
      // Inactive pills fall back to the neutral hover brush.
      var inactive = ThemeBrushes.HoverBackground;

      BtnModePlan.Background = _selectedMode == AgentMode.Plan
                                  ? UserBubble.PlanAccent
                                  : inactive;
      BtnModeImplement.Background = _selectedMode == AgentMode.Implement
                                  ? ThemeBrushes.AccentDark
                                  : inactive;

      TxtModePending.IsVisible = _selectedMode != _appliedMode;

      _ApplyModeTintToInput(_selectedMode);
   }

   /// <summary>
   /// Taint the chat input box in the color of the mode the *next* message
   /// will be sent under (_selectedMode), so an accidental Plan/Implement
   /// state is obvious before sending. Mutates the TextBox's local Fluent
   /// brush resources (the template resolves them dynamically): a colored
   /// border plus a barely-perceptible warm/cool background shift.
   ///   Plan      → amber border, warm background
   ///   Implement → blue  border, cool background
   /// </summary>
   private void _ApplyModeTintToInput(AgentMode mode)
   {
      if (TxtInput == null) return;

      // Background pair (normal / pointer-over; focused reuses normal) —
      // base is PanelBackground #1E1E1F. Left hue-fixed on purpose: the shift
      // is meant to be barely perceptible, which a saturated accent would not be.
      (Color bg, Color bgHover) = mode == AgentMode.Plan
         ? (Color.Parse("#24201A"), Color.Parse("#2C2620"))   // warm
         : (Color.Parse("#1C2028"), Color.Parse("#232833"));  // cool

      // Plan's amber is a *status* color and stays fixed — it has to remain
      // distinguishable whatever the accent is. Implement follows the accent,
      // which is where it came from (this used to hardcode AccentDark's hex).
      IBrush borderBrush = mode == AgentMode.Plan
         ? new SolidColorBrush(Color.Parse("#C8A050"))
         : ThemeBrushes.AccentDark;

      var bgBrush      = new SolidColorBrush(bg);
      var bgHoverBrush = new SolidColorBrush(bgHover);

      var r = TxtInput.Resources;
      r["TextControlBackground"]            = bgBrush;
      r["TextControlBackgroundPointerOver"] = bgHoverBrush;
      r["TextControlBackgroundFocused"]     = bgBrush;
      r["TextControlBorderBrush"]            = borderBrush;
      r["TextControlBorderBrushPointerOver"] = borderBrush;
      r["TextControlBorderBrushFocused"]     = borderBrush;

      // Selection highlight tracks the accent in both modes -- it marks text,
      // not mode, so tying it to Plan's amber would be misleading.
      r["TextControlSelectionHighlightColor"] = ThemeBrushes.AccentDark;
   }

   /// <summary>
   /// Apply any pending mode change before sending the next user turn:
   /// update the permission gate, append a system message announcing the
   /// switch, persist a mode record so resume picks it up, and drop a
   /// visual marker in the transcript. No-op when nothing changed.
   /// Called from <see cref="BtnSend_Click"/> right after building the
   /// loop but before <c>RunAsync</c>.
   /// </summary>
   private void _CommitPendingModeChange()
   {
      if (_selectedMode == _appliedMode) return;
      if (_loop == null) return;

      var prev = _appliedMode;
      _appliedMode = _selectedMode;
      if (_gate != null) _gate.Mode = _appliedMode;

      string announce = _appliedMode switch
      {
         AgentMode.Plan =>
            "[Mode switch] You are now in PLAN mode. Focus on reading the code and " +
            "drafting a plan; produce at most one Markdown plan file. The host will " +
            "prompt the user for approval on every write or build/test run, ignoring " +
            "prior grants.",
         AgentMode.Implement =>
            "[Mode switch] You are now in IMPLEMENT mode. You may write code and run " +
            "the configured build/test commands subject to the standard permission gate " +
            "(cached approvals carry across calls).",
         _ => "[Mode switch] mode=" + _appliedMode,
      };

      _loop.AppendSystemMessage(announce);
      _transcriptStore?.AppendModeChange(_appliedMode);
      AppendItem(new NoticeLine("mode switched: " + prev + " → " + _appliedMode));

      _RefreshModeUi();
   }

   private void CancelIfRunning()
   {
      try { _cts?.Cancel(); } catch { }
      // A first send that is still waiting on a slow stdio server has no
      // _cts yet — Cancel has to reach the connect, or the button lies.
      try { _mcpConnectCts?.Cancel(); } catch { }
   }

   // "Close" ends the active session: in-memory loop and visual transcript
   // are dropped, the JSONL file stays on disk and reappears in the project
   // picker as a resumable card.
   private void BtnClose_Click(object? sender, RoutedEventArgs e)
   {
      Reset();
      SessionClosedByUser?.Invoke();
   }

   private void BtnProjectSettings_Click(object? sender, RoutedEventArgs e)
   {
      if (string.IsNullOrEmpty(_projectPath)) return;

      var store = ProjectConfigStore.LoadOrCreate(_projectPath);
      var win   = new ProjectSettingsWindow();
      win.Configure(store.Config, _projectPath!, _DiscoverSkills(), updated =>
      {
         store.Config.BuildSteps      = updated.BuildSteps;
         store.Config.TestSteps       = updated.TestSteps;
         store.Config.BuildTimeoutSec = updated.BuildTimeoutSec;
         store.Config.TestTimeoutSec  = updated.TestTimeoutSec;
         store.Config.AutoLoadSkills  = updated.AutoLoadSkills;
         store.Config.SkipSkillAnnouncement = updated.SkipSkillAnnouncement;
         // Kind is fixed at project creation; the dialog only ever reads it.
         store.Config.McpServers      = updated.McpServers;
         store.Save();
         // Re-expose build/test on the live loop so freshly-configured
         // steps become callable without reopening the session.
         //
         // MCP servers deliberately do NOT hot-swap here: this session is
         // already connected to the servers it was built with, and repointing
         // a live conversation at a different set mid-turn would invalidate
         // every tool call still in its history. The edit lands on the next
         // session instead, which is also when the frozen system prompt can
         // describe it honestly.
         _loop?.UpdateTools(_BuildToolRegistry(withTaskCallback: true));
         if (_mcpHandles.Count == 0) { _EnsureMcpHandles(); _RefreshMcpStatus(); }
      });
      var owner = TopLevel.GetTopLevel(this) as Window;
      if (owner != null) win.ShowDialog(owner);
      else                win.Show();
   }

   private void TxtInput_KeyDown(object? sender, KeyEventArgs e)
   {
      // Which Enter sends is a setting (see PromptSubmitMode); the other
      // combination falls through to the TextBox, whose AcceptsReturn=True
      // logic inserts the newline regardless of Shift. The send-button
      // highlight previews which action a press of Enter will perform.
      if (e.Key == Key.Enter && _IsSendChord(e.KeyModifiers))
      {
         e.Handled = true;        // suppress newline insertion
         BtnSend_Click(sender, e);
         return;
      }
      UpdateSendPreview(e.KeyModifiers);
   }

   /// <summary>True when Enter with these modifiers means "send" right now.</summary>
   private bool _IsSendChord(KeyModifiers mods)
   {
      bool shift = (mods & KeyModifiers.Shift) != 0;
      return _PromptSubmitMode() == PromptSubmitMode.VibeCoder ? !shift : shift;
   }

   // Read live rather than caching: the config tab writes straight into the
   // agent's settings POCO, so an open session picks the change up on the
   // next keystroke.
   private PromptSubmitMode _PromptSubmitMode() =>
      _agent?.Settings.PromptSubmit ?? PromptSubmitMode.StructuredText;

   /// <summary>
   /// Re-state the Enter convention in the prompt placeholder and reset the
   /// send-button highlight to what an un-modified Enter would do. Called
   /// when the view is bound and whenever it becomes visible again, which
   /// covers the return from the configuration tab.
   /// </summary>
   private void _RefreshPromptHints()
   {
      TxtInput.PlaceholderText =
         _PromptSubmitMode() == PromptSubmitMode.VibeCoder
            ? "Ask about the project… (Enter = send, Shift+Enter = new line, drop or paste images to attach)"
            : "Ask about the project… (Enter = new line, Shift+Enter = send, drop or paste images to attach)";
      UpdateSendPreview(KeyModifiers.None);
   }

   private void TxtInput_KeyUp(object? sender, KeyEventArgs e)
   {
      // KeyUp on the Shift key itself reports KeyModifiers without Shift,
      // which is exactly the trigger we need to drop the preview highlight.
      UpdateSendPreview(e.KeyModifiers);
   }

   private void UpdateSendPreview(KeyModifiers mods)
   {
      bool sendMode = _IsSendChord(mods);
      BtnSend.FontWeight  = sendMode ? FontWeight.Bold : FontWeight.Normal;
      BtnSend.BorderBrush = sendMode ? ThemeBrushes.Accent : Brushes.Transparent;
   }

   // -- misc -----------------------------------------------------------------

   private void SetStatus(string s) => TxtStatus.Text = s;

   /// <summary>
   /// Refresh the header status text to reflect the currently configured
   /// model + endpoint. Called on view re-entry and after any settings
   /// edit that could have changed either.
   /// </summary>
   private void RefreshStatus()
   {
      if (_agent == null) return;
      // Model is shown in the header dropdown; status carries the active
      // provider + endpoint until a turn finishes, then per-turn token usage
      // takes over.
      var prov = _agent.Settings.ResolveProvider(_selProvider);
      SetStatus(prov != null ? prov.Name + " · " + prov.BaseUrl : "(no provider configured)");

      // Shares the model-change path: the level shown must follow the model
      // the header now names, since the two styles offer different lists.
      _RefreshReasoningPicker();
   }

   /// <summary>
   /// Called when the view becomes visible again (e.g. user navigated
   /// from the configuration tab back to the chat). If the selected
   /// model changed while we were hidden, the in-memory loop holds a
   /// stale model id baked into its constructor — drop it so the next
   /// Send rebuilds with the new model.
   /// </summary>
   private void OnBecameVisible()
   {
      if (_agent == null) return;

      // The model list might have grown via the setup tab while we were
      // hidden; refresh the dropdown. The selection itself is view-local —
      // there is deliberately NO realignment to the app-wide default here:
      // with multiple tabs, that default tracks whichever tab (or resume)
      // touched it last, and syncing to it silently swapped other sessions'
      // models on every tab switch.
      RefreshModelCombo();
      UpdateGaugeWindow();
      RefreshStatus();

      // Covers both a tab switch and a return from the config view — the
      // latter matters because that is where the wizard gets switched off.
      RefreshClipboardOffer();

      // Same story for the Enter convention: re-read it after a config visit.
      _RefreshPromptHints();
   }

   protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   {
      base.OnPropertyChanged(change);
      if (change.Property == IsVisibleProperty && change.NewValue is bool now && now)
         OnBecameVisible();
   }

   private static void UiPost(Action a)
   {
      if (Dispatcher.UIThread.CheckAccess())
         a();
      else
         Dispatcher.UIThread.Post(a);
   }

   private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

   // -- fields ---------------------------------------------------------------

   private NativeAgent?              _agent;
   private string?                   _projectPath;
   private AgentSettingsStore?       _store;
   private AgentLoop?                _loop;
   private CancellationTokenSource?  _cts;
   private bool                      _running;

   private AssistantBubble?                       _currentAssistant;
   private readonly Dictionary<string, ToolCallCard> _toolCards = new();
   private readonly List<ToolCallCard>            _streamingCards = new();
   private DispatcherTimer?                       _flushTimer;

   // Consecutive tool-call run tracking for retroactive grouping.
   private const int GROUP_THRESHOLD = 3;
   private readonly List<ToolCallCard> _currentToolRun = new();
   private ToolCallGroupCard?          _currentToolGroup;

   // Ordered list of user bubbles in the current transcript. The n-th
   // entry corresponds to the n-th user message in the persisted session;
   // used by the revert flow to pick a fork point without walking the DOM.
   private readonly List<UserBubble>   _userBubbles = new();

   // Every assistant bubble in the current transcript, live and replayed, so
   // the branch footers can be re-evaluated in one pass. Position in this list
   // is NOT the transcript ordinal — see AssistantBubble.RecordOrdinal.
   private readonly List<AssistantBubble> _assistantBubbles = new();

   // How many bubble-worthy assistant messages this session has committed.
   // Hands out RecordOrdinal; the live and replay paths share it so both
   // number the same turns the same way.
   private int _committedAssistantTurns;

   // Registry of the tools the current loop advertises. View-side uses only:
   // card header previews + the tools bubble. Set by BuildLoop and at replay
   // start; never executed from here.
   private ToolRegistry? _viewToolRegistry;

   // System-message bubbles + the header toggle state. Bubbles are always
   // appended; _showSystemMessages gates their visibility (persists across
   // sessions, since the ToggleButton's state is never reset).
   private readonly List<SystemBubble> _systemBubbles = new();
   private bool                        _showSystemMessages;

   // Operating mode state. _selectedMode is what the user has clicked
   // (drives the pill highlight); _appliedMode is what the model has
   // actually been told about (gate + system messages). They diverge
   // between the flip and the next Send — by design: mode changes are
   // announced together with the user's next message, never on the fly.
   private AgentMode                   _selectedMode = AgentMode.Plan;
   private AgentMode                   _appliedMode  = AgentMode.Plan;

   // Autoscroll latch: follow new content to the bottom until the user
   // scrolls up, then leave them alone. Released and re-taken by the user's
   // own scrolling; explicitly re-armed on Send / Reset.
   private readonly ScrollLatch        _scrollLatch;

   // Permission infrastructure — one gate per Configure call (one project).
   // Carries session cache across sessions in the same project; that's fine,
   // session-scope grants are intentionally per project, not per individual
   // conversation file.
   private PermissionGate?                        _gate;
   private PermissionPromptService?               _promptService;

   // Model id + provider name baked into the active loop. If either diverges
   // from settings (e.g. user changed it via the setup tab), we hot-swap —
   // a provider change additionally rebuilds the wire client.
   private string?                                _loopModelId;
   private string?                                _loopProvider;

   // This tab's model selection (drives the combo, gauge, new loops and the
   // session meta). Seeded from the app-wide default in Configure; explicit
   // combo picks write the default back for future sessions, but no code
   // path reads the default into a live tab afterwards.
   private string                                 _selProvider = "";
   private string                                 _selModel    = "";
}
