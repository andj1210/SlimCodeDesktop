# SlimCode Desktop — Session Summary

A desktop GUI host for **SlimCode Agent**, an in-process LLM coding agent that talks directly to any OpenAI-compatible chat-completions endpoint. Avalonia 12 (software-rendered Skia) on .NET 10.

---

## Top-level layout

```
┌──────────────────────── MainWindow ────────────────────────┐
│ Sidebar  │ Tab strip (one tab per open session)            │
│ (folders,│ NativeAgentView (in-process "SlimCode Agent")   │
│ sessions)│ SessionsView (resume picker / empty state)      │
└──────────┴─────────────────────────────────────────────────┘
```

| File | Role |
|---|---|
| `Program.cs` | Avalonia bootstrap; forces software rendering (zero-VRAM) |
| `App.axaml` | Global Button style; `ScrollViewer.AllowAutoHide=False` (no scrollbar auto-hide anywhere) |
| `MainWindow.axaml(.cs)` | Sidebar + header + view switcher. **F11 toggles `WindowState.FullScreen`** |
| `MainWindow.Tabs.cs` | Session / subagent tab management |
| `MainWindow.Sidebar.cs` | Sidebar drill-down into one project's sessions |
| `Core/AgentSettingsStore.cs` | Per-agent settings JSON; calls `ISettingsMigration.Migrate` on POCOs that implement it |
| `Platform/Platform.*.cs` | Per-OS surface (`static class Platform`, namespace `SlimCodeDesktop.Core`). `$(TargetOS)` picks exactly one `Platform.<OS>.cs`; `Platform.Shared.cs` always compiles and holds the contract. Inactive file is unchecked by the compiler — `dotnet build -p:TargetOS=Linux` |
| `Markdown/AvaloniaMarkdownRenderer.cs` | Markdig → Avalonia controls. Supports task lists, math (`$...$` → Unicode), `TextForeground` knob to beat Fluent's implicit TextBlock style. One `SelectableTextBlock` per block, so selection can't span blocks — code blocks get a hover "copy" overlay; whole-answer copy / raw-source view live in `AssistantBubble` |
| `Markdown/MarkdownView.axaml(.cs)` | UserControl wrapper. `Markdown` + `TextForeground` styled properties; re-renders on either |

---

## Agent layer (`Agents/`)

`Agent.cs` base, `AgentRegistry.All = [NativeAgent]`. One agent today; the abstraction stays because the launch menus, both session pickers and the config tabs are all written against the registry list, so a second in-process agent would need no UI changes. `Agent` carries identity, the settings-POCO type, and the session enumerate/delete/rename capability trio — nothing else.

---

## SlimCode Agent — the in-process agent

`MainWindow` opens one `NativeAgentView` per session, in its own tab. Settings, sessions, permissions all live under `%APPDATA%\SlimCode\`.

### Layout

```
┌──── NativeAgentView ──────────────────────────────────────┐
│ Header: [Model ▾]  status  Gauge  [≡]  [Close]            │
│ TaskListPanel  (pinned, hidden until set_tasks called)    │
│ Transcript:                                               │
│   UserBubble       (mode-tinted bubble + mode badge       │
│                     + revert ↶ on left)                   │
│   AssistantBubble  (Thinking section + Markdown body,     │
│                     full-width minus 30px right gutter)   │
│   ToolCallCard     (collapsible; coloured per tool)       │
│   ToolCallGroupCard(retroactive wrap for 3+ consecutive)  │
│   NoticeLine       (model/mode switch markers)            │
│   ErrorLine                                               │
│ ── AnimatedSplitter ── (status line: green/gray + comet)  │
│ [Plan][Implement]                                         │
│ [TextBox]                                  [Send][Cancel] │
└───────────────────────────────────────────────────────────┘
```

### File map

**`Agents/Native/`** core
| File | Role |
|---|---|
| `NativeAgent.cs` / `.Sessions.cs` | Subclass of `Agent`. `Id="native"`, `DisplayName="SlimCode Agent"`, `IsNative=true`. `EnumerateSessions` + `DeleteSession`. Delete prunes audit-log entries for the session unless `PreserveAuditOnSessionDelete` is on. |
| `NativeAgentSettings.cs` | Settings POCO. Implements `ISettingsMigration` (temperature override migration). Curated `SystemPrompt` is **`[JsonIgnore]`** — every process starts with the in-code default; legacy stored values are ignored. `BuildEffectiveSystemPrompt(basePrompt, projectPath, startedLocal, initialMode)` returns prompt + dynamic block `"This session started …\nThe local project folder is …\nInitial mode: …"`. |
| `AgentMode.cs` | `{ Implement, Plan }`. Plan is the default for new sessions. |
| `AgentLoop.cs` | Turn loop. `Model` is **mutable** (hot-swap). `AppendSystemMessage(content)` injects mid-conversation system messages (used for mode-switch announces). Captures `PromptTokens`/`CompletionTokens`/`ReasoningTokens` per turn into the assistant message. **`MaxIterations = 150`** (raised from 20). Events: TextDelta, **ReasoningDelta**, ToolCallStarted/Ready/Completed, AssistantMessageCompleted, MessageAdded, UsageReported, Error, TurnCompleted |
| `ChatMessage.cs` | Adds `ReasoningContent`, `ReasoningTokens`, `PromptTokens`, `CompletionTokens`, `IsError`. None of these are sent on the wire (no field on `OaMessage`); they travel in-memory + JSONL only. |

**`Agents/Native/Llm/`**
| File | Role |
|---|---|
| `ILlmClient.cs` / `LlmDelta.cs` | `LlmDelta.DeltaKind` now includes `ReasoningText`. Usage delta carries optional `ReasoningTokens`. |
| `OpenAiCompatClient.cs` | Only impl. Emits ReasoningText before Text per chunk. `HttpClient.Timeout = Infinite`. Static `ListModelsAsync` for Discover. |
| `OpenAiSchemas.cs` | `OaStreamDelta.reasoning_content`. `OaUsage.completion_tokens_details.reasoning_tokens`. |

**`Agents/Native/Tools/`**
| File | Role |
|---|---|
| `ITool.cs` / `ToolContext.cs` / `ToolResult.cs` / `ToolRegistry.cs` | Same shape. `ToolContext.Resolve(path, PathAccess)` sandbox enforces project-root containment for all file tools; the required Read/Write intent is checked against the context's `FolderAccessPolicy` (per-folder `ReadOnly` / `NoAccess`, whole-segment match, most restrictive wins, writes to an ancestor of a restricted folder denied). Rules are anchored at the root, or `AnyDepth` (a single name matched at every depth, e.g. the `.git` preset new code projects get); for those, writing a directory scans it on disk for a match, since no lexical check can see one. Rules live in `ProjectConfig.FolderRules`; `ProjectConfig.BuildFolderPolicy()` turns them into the policy (an unparseable rule locks the whole project rather than being dropped). `BuildLoop` reads it once per loop, and subagents reuse the parent's. Returns a `PathResolution` (full path or `PathDenial` reason). Two passes: lexical, then physical via `Platform.RealPath` (junctions / symlinks / 8.3 names resolved, dangling links denied). Directory-walking tools must use `ListVisible` / `EnumerateFilesRecursive` (filtered by the same gate, never enter linked dirs) instead of `Directory.Enumerate*`. Re-created contexts must carry `Folders` along (see `AgentLoop`). `FileToolSandboxTests` fails for any new tool with a path argument until it is covered. |
| `ReadFileTool`, `ListDirTool`, `GlobTool`, `GrepTool` | Read tools. **`GrepTool.path` accepts file OR directory**; single-file mode skips the ext filter. Error message hints "omit 'path' to search the whole project" when target is neither. |
| `WriteFileTool` / `EditFileTool` | Atomic write. |
| `BashTool` | `cmd.exe /c …`. **Not** path-sandboxed (gated only by permission category). |
| `SetTasksTool.cs` | **New**. Replaces the markdown-fenced ```tasks``` convention. Full-replace semantics: model sends the complete list each call; arguments are `[{text, status}]` with status enum `pending|in_progress|completed|cancelled`. Category=Read so the gate auto-allows. Constructor takes an `Action<IReadOnlyList<TaskItem>>` callback closing over `TaskListPanel.SetTasks`. Magenta in `ToolCallCard.ToolNameBrush`. |

**`Agents/Native/Mcp/`** — MCP client (hand-rolled; no NuGet dependency)
| File | Role |
|---|---|
| `McpJsonRpc.cs` | JSON-RPC 2.0 envelopes + `McpMessage` (response / request / notification, told apart by which fields are present). Shared request-id counter. |
| `IMcpTransport.cs` | `StartAsync` / `SendAsync` + `MessageReceived` / `LogLine` / `Closed`. Correlation lives one layer up so both transports look the same. |
| `McpStdioTransport.cs` | Child process, newline-delimited JSON on stdin/stdout, stderr → LogLine. Launch goes through `Platform.ResolveProcess` (PATH lookup + `.cmd` shims via cmd.exe on Windows). |
| `McpHttpTransport.cs` | Streamable HTTP. POST per message; response is JSON or SSE (reuses `Llm.SseReader`). Carries `Mcp-Session-Id` + `MCP-Protocol-Version`. **No standing GET channel** — unsolicited `list_changed` only arrives inside another call's response stream. |
| `McpClient.cs` | `InitializeAsync` / `ListToolsAsync` (cursor paging) / `CallToolAsync`. Answers every server→client request with method-not-found (no sampling / elicitation / roots in v1). Protocol `2025-06-18`, accepts whatever the server answers with. |
| `McpToolInfo.cs` | Tool metadata (schema kept as raw JSON), `McpServerIdentity`, `McpCallResult` (flattens the content array to text; images/audio become "[… omitted]"). |
| `McpServerHandle.cs` | One configured server per session. Lazy connect, per-call timeout, `EnabledTools` allow-list, status for the header. `ConnectAsync` never throws — one dead server must not kill the others. Tool names: `<serverPrefix>__<tool>`, both segments sanitized to `[A-Za-z0-9_-]`. |
| `Tools/McpTool.cs` | `ITool` adapter. `Category = readOnlyHint ? Read : Execute` (absent hint ⇒ Execute). Schema forwarded verbatim. |

**`Agents/Native/Permissions/`**
| File | Role |
|---|---|
| `PermissionGate.cs` | Resolution order: **(0) Plan-mode short-circuit** (Write/Execute in Plan → forced one-shot prompt unless globally AlwaysDeny); (1) global sticky; (2) session cache; (3) project policy; (4) global layered; (5) prompt user. `Mode` property settable. |
| `PermissionAuditLog.cs` | JSONL writer. **`Prune(filePath, keep)`** rewrites the file atomically (temp + Move) keeping only entries `keep` returns true for. Used for delete-with-session + orphan GC on viewer open. |
| `GlobalPolicyStore`, `ProjectPolicyStore`, `PermissionPolicy`, `PermissionGrant`, `PromptDtos`, `PermissionPromptService` | Unchanged from before. Audit log records carry `SessionId`. |

**`Agents/Native/Sessions/`**
| File | Role |
|---|---|
| `TranscriptRecord.cs` | Record types: `"meta"`, `"msg"`, `"model"`, `"mode"`, `"perm"`, `"title"`, **`"mcpTools"`**. Msg adds: `ReasoningContent`, `ReasoningTokens`, `PromptTokens`, `CompletionTokens`, `IsError`. |
| `TranscriptStore.cs` | `AppendMeta`, `AppendMessage`, `AppendToolResult`, **`AppendModelChange`**, **`AppendModeChange`**, **`AppendMcpTools`**. |

**`Agents/Settings/`**
| File | Role |
|---|---|
| `SettingAttribute.cs` / `SettingDescriptor.cs` | `[Setting(...)]` reflection-driven UI. |
| `ISettingsMigration.cs` | **New**. Optional hook on the POCO. `Migrate(JsonElement raw)` lets it inspect raw JSON to distinguish "key absent" from "key default", invoked from `AgentSettingsStore.Apply`. |

**`Views/`** UI
| File | Role |
|---|---|
| `NativeAgentView.axaml(.cs)` | The chat pane. State: _loop, _gate, _transcriptStore, _sessionId, _appliedMode/_selectedMode, _autoScrollLatched, _sessionSystemPrompt, _userBubbles. Exposes `SessionClosedByUser` event (raised from BtnClose_Click only, not from internal Reset()) so MainWindow can clear `_activeProjectPath`. |
| `Native/AssistantBubble.cs` | Full-width minus 30px right gutter (no MaxWidth). Hosts collapsible "Thinking (Nk…)" section above MarkdownView body. Reasoning rendered as Markdown with `TextForeground = TextMuted`. `FinalizeReasoning(int? tokens)` locks the label (tokens if reported, char count otherwise). Double-click reasoning body → collapse. Footer bar: "copy" (raw Markdown to clipboard), "source" (swaps the rendered view for one `SelectableTextBlock` of raw Markdown — fully cross-line selectable), "regenerate". |
| `Native/UserBubble.cs` | `(text, mode, onRevert)` ctor. Mode-tinted background: Implement = AccentDark blue, Plan = ochre `#5C4A2E`. Always shows a corner badge: `PLAN` (amber `#C8A050` = `PlanAccent`) or `IMPLEMENT` (Accent blue). Revert `↶` button at left edge. `PlanBubbleBackground` / `PlanAccent` are `internal static` so the mode pills can reuse them. |
| `Native/ToolCallCard.cs` | Collapsible card. Per-tool color via `internal static ToolNameBrush`: read=green, glob/grep=cyan, write/edit=yellow, bash=red, set_tasks=magenta. Body has 20px right+bottom inset padding inside the ScrollViewer so scrollbars don't clip text. Double-click body → collapse. |
| `Native/ToolCallGroupCard.cs` | Created retroactively when 3+ consecutive tool calls have accumulated without a non-tool item breaking the run. Reparents earlier cards into its body. Header shows `N tool calls` + colored per-name summary (`3x read_file, 2x edit_file`); label tint = highest-risk tool color. Collapsed by default. Errors break the run; mode/notice lines too. |
| `Native/TaskListPanel.cs` | Pinned panel docked above the transcript ScrollViewer. `TaskItem(Status, Text)`. Glyph per status: ☑ accent (completed, strikethrough), ◐ accent semibold (in_progress, active highlight), ☐ muted (pending), ⊘ muted strikethrough (cancelled). Header summary: `(2/5) · 1 active`. Populated only via `SetTasksTool` callback — no markdown parsing. |
| `Native/ContextFillGauge.cs` | 36×36 donut. **Prefilled on resume** from the most recent assistant turn's `PromptTokens + CompletionTokens` if persisted. |
| `Native/ModelCatalogWindow`, `Native/PermissionPromptDialog`, `Native/PermissionEditorWindow`, `Native/AuditLogWindow` | Same surfaces as before. Audit log: bigger badges, body has 40px right gutter for the scrollbar, optional `"Only this session"` filter, tool name colored via `ToolCallCard.ToolNameBrush`. |
| `Native/AnimatedSplitter.cs` | **GridSplitter subclass** that paints a single status line: 6 px green when latched-to-bottom, 3 px gray when scrolled into history. While `IsWorking` is true, paints two converging streaks (knight-rider pattern, scaled to the line). |
| `Native/NoticeLine.cs` | Italic muted centered marker. Used for model-switch and mode-switch announcements. |
| `Native/ErrorLine.cs` | Red-accented inline for stream/loop errors. |
| `Native/TranscriptItem.cs` | Base. `View` is the root visual. |

---

## Key concepts

### Operating modes (Plan / Implement)

- `AgentMode` enum. **Plan is the default for new sessions.** Implement is full tool access with cached approvals. Plan re-prompts on every Write/Execute tool call regardless of cached/persisted Allow grants (Read tools unaffected). `AlwaysDeny` still wins for safety.
- Mode flips queue: clicking a pill updates `_selectedMode` and shows "(applies on send)" hint, but doesn't touch the gate or write any record yet. The actual commit happens at the start of the next `BtnSend_Click`, *before* the user bubble is appended — visual ordering reads:
  1. Mode-switch `NoticeLine` ("mode switched: Implement → Plan")
  2. System message announce (persisted, sent to model)
  3. User bubble (with the matching mode tint + badge)
- `EnsureNewSession` writes the initial mode as a `mode` record so resume restores it.
- Pill colors: active Plan = `PlanAccent` amber, active Implement = `AccentDark` blue, inactive = `HoverBackground` gray.
- System prompt's dynamic trailing block includes `Initial mode: PLAN.` so the model knows from turn 1.
- **Revert/fork preserves `mode` records** in the rewritten file — without this, reverting a Plan-mode turn would drop the fork into Implement.

### Permission gate

- 4-layer (global sticky → session cache → project → global layered → prompt) plus Plan-mode short-circuit at step 0.
- Audit log is project-wide (`%APPDATA%\SlimCode\projects\<slug>\permissions.log.jsonl`), every entry tagged with `SessionId`.
- Audit log cleanup (only if `PreserveAuditOnSessionDelete=false`):
  - Delete-with-session: `NativeAgent.DeleteSession` prunes entries for the gone session.
  - Orphan GC on audit log viewer open: `AuditLogWindow.Configure` accepts `liveSessionIds`; entries with sessionIds not in the set (and not null) get pruned.

### Reasoning ("Thinking")

- Capture: `reasoning_content` channel from OpenAI-compatible streams → `LlmDelta.Reasoning` → bubble's collapsible section. Models that emit `<think>` tags inside `content` aren't intercepted (they render inline).
- Display: live counter "Thinking (0.3k…)" using whitespace-split word count. On `AssistantMessageCompleted`, **`FinalizeReasoning(int? tokens)`** locks the label to `Thinking (N tokens)` if the provider reported `reasoning_tokens` else `Thinking (N chars)` (≥1000 → `X.Xk`).
- Body rendered through `MarkdownView` with `TextForeground = TextMuted`. Code blocks keep their own brush.
- Persisted via `TranscriptRecord.ReasoningContent` + `ReasoningTokens`; replay populates the section.
- Tool calls inside reasoning content are by design **not** executed (only the `tool_calls` channel is). Documented as model-quality issue.

### Task list (`set_tasks` tool + `TaskListPanel`)

- Full-replace semantics. Tool body parses args, invokes a per-session callback that calls `TaskPanel.SetTasks(items)`. ToolCallCard for the call still appears in the transcript (auditable, consistent with other tools).
- Replay walks history backwards for the last `set_tasks` call, parses its `ArgumentsJson`, repopulates the panel (the tool body isn't re-invoked).
- Panel is a `DockPanel.Dock="Top"` sibling of the transcript ScrollViewer — pinned, scrolls independently from chat. Easy to relocate later.

### Autoscroll latch + status splitter

- `_autoScrollLatched` (default true). `ScrollToEnd` no-ops when false. User scrolling (`OffsetDelta != 0`) re-evaluates. Extent-only changes (e.g. expand/collapse, streaming) defer to next dispatcher tick to avoid racing the streaming flush timer's own ScrollToEnd.
- Re-armed on Send and Reset.
- Visually surfaced by `AnimatedSplitter`: green when latched, gray when not, knight-rider streak while `IsWorking`.

### Model switching mid-session

- `CmbModel_SelectionChanged` and `OnBecameVisible` no longer call `Reset()`. `ApplyModelSwap(previous, current)`: updates `_loop.Model`, appends `NoticeLine`, writes a `model` record. First request after a swap may surface a provider error if tool-call structures aren't accepted — bubbles up through `loop.Error`.
- Resume realigns `SelectedModel` to the *last* declared model in the records, not the start model.

### Revert / fork

- Every UserBubble has a small `↶` button at the left edge. Click → fork:
  1. Read records from current session file.
  2. Find the targeted user message's record index.
  3. Mint a new session id; new meta carries forward model + system prompt + project path.
  4. Re-emit non-meta records up to (not including) the target user message. **Includes `mode`, `model`, and full msg records (with reasoning/tokens/isError).**
  5. `Reset()` + `ResumeSession(newId)`.
  6. Drop the reverted text into TxtInput.
- Refuses if `_running == true`.

### Persistence layering (NativeAgentSettings)

- `[Setting]`-decorated scalar fields flow through `AgentSettingsStore.Set` (per-property writes).
- `Models: List<ModelInfo>` is mutated directly, then persisted via `_settingsStore.SaveAgent(agent)`.
- `SystemPrompt` is `[JsonIgnore]` — process-default always wins; legacy stored values disappear from the JSON on next save.
- `OverrideTemperature` (bool, default false) + `Temperature` (float). The runtime sends `Temperature` only when `OverrideTemperature` is on. Migration: `ISettingsMigration.Migrate` checks the raw JSON; if `temperature` is non-zero and `overrideTemperature` key absent, infers the legacy intent (set Override=true).
- `PreserveAuditOnSessionDelete` (bool, default false): controls audit-log cleanup behavior.

### LaTeX math + GFM features in markdown

- Pipeline: PipeTables, EmphasisExtras, AutoLinks, **TaskLists**, **Mathematics**.
- Inline `$...$` and block `$$...$$` math → italic `Run` / centered italic block via `_ResolveLatex` — curated Unicode substitution table for arrows, operators/relations, Greek, common symbols. Unknown commands pass through verbatim.
- GFM task list inlines (`- [x]`, `- [ ]`) render as ☑/☐ glyphs in accent/muted colors.
- `MathBlock` arm placed before `FencedCodeBlock` in the pattern switch (MathBlock inherits from FencedCodeBlock; otherwise the more-general arm would shadow it).

---

## Disk layout

```
%APPDATA%\SlimCode\
├── global-permissions.json
├── projects\
│   └── <projectSlug>\
│       ├── permissions.json
│       └── permissions.log.jsonl
└── sessions\
    └── native\
        └── <projectSlug>\
            └── <sessionId>.jsonl   (yyyyMMdd-HHmmss-fff)
```

JSONL records:
- `meta` — sessionId, startedAt, model, projectPath, systemPrompt.
- `mode` — Plan or Implement. Written in `EnsureNewSession` and on every flip.
- `model` — mid-session model change marker.
- `title` — session caption. Appended (never patched into `meta`, which would
  need a file rewrite); the **last** such record wins, blank clears it.
  Written by `TranscriptStore.AppendTitle` / `NativeAgent.RenameSession`.
- `msg` — role, content, toolCalls, toolCallId, reasoningContent, reasoningTokens, promptTokens, completionTokens, isError.

---

## Open items / future queue

1. **Live gauge refresh while streaming** — `chars/4` (or word-based) estimate per delta, snap to real on `UsageReported`. ~1 hour.
2. **Query catalogue** — multiple-choice picker. When the model offers options ("1. Refactor X; 2. Add Y; 3. …"), surface them as clickable buttons under the latest assistant bubble; click fills the input. Convention-based detection (fenced marker, similar shape to set_tasks).
3. **Context window compaction** — discussed: programmatic threshold trigger + LLM-driven summarization round, swap history with `[system, summary, last K turns]`. Persist a `compact` record; UI shows a notice at the swap point. 4–6 hours.
4. **Prefill progress indicator** — model-switch causes long prefill with no protocol-level progress. Three layers proposed: (a) state-distinct animation + elapsed timer, (b) empirical learned-rate estimate per model, (c) llama.cpp `/slots` polling for native progress.
5. **Per-path / per-command permission policies** — bash is currently one big switch. "Allow `git status`, not `rm`" deliberately deferred.
6. **Click-through on markdown links** — currently Span-rendered without `InlineUIContainer + HyperlinkButton`.
7. **Syntax highlighting in fenced code blocks** — currently monospace plain text.
8. **System-prompt builder enhancement** — auto-prepend project root + brief tree summary measurably improves tool-call accuracy on smaller models.

---

## Conventions worth knowing

- WPF-style F11 → `WindowState.FullScreen` registered via `Tunnel` routing in `MainWindow` ctor (Tunnel-only — both tunnel and bubble would fire twice and toggle back).
- `ScrollViewer.AllowAutoHide=False` globally; reserve right/bottom padding (20px / 40px) inside scrolled containers to prevent always-visible scrollbars from clipping content.
- `AvaloniaMarkdownRenderer.TextForeground` is the standard way to colour rendered text — Avalonia Fluent's implicit `TextBlock` style sets a hard Foreground that beats property inheritance.
- Per-tool color scheme (defined once on `ToolCallCard.ToolNameBrush`, reused by `AuditLogWindow` and `ToolCallGroupCard`): green=read/list, cyan=glob/grep, yellow=write/edit, red=bash, magenta=set_tasks.
- Plan-mode visual signal palette: bubble ochre `#5C4A2E` (`PlanBubbleBackground`), accent amber `#C8A050` (`PlanAccent`). Both `internal static` on `UserBubble`.
- Sessions are project-slugged via `TranscriptStore.ProjectSlug` (lowercase, non-alphanumeric → `-`, plus a `-mcp` trailer for an MCP project). Per-project metadata uses `ProjectPolicyStore.ProjectMetadataDir(projectPath)`. The kind behind the trailer comes from `ProjectList.Current.GetKind(path)` (projects.json `kinds`), because it has to resolve before any per-project file is opened.
- Mid-session ChatMessage flow: `AgentLoop.AppendSystemMessage(content)` appends to history *and* raises `MessageAdded`, so persistence picks it up. Used for mode-switch announces.

---

## Quick reference — "I want to…"

| You want to … | Look at |
|---|---|
| Add a new native tool | New `*Tool.cs` under `Agents/Native/Tools/`, implement `ITool`, register in `ToolRegistry.CreateFullDefaults` (or per-session in `BuildLoop` if it needs a UI callback). Custom `ArgsPreview` if args are large |
| Make a project use MCP tools instead of the built-in ones | Project setup → Project kind = "MCP project", add servers, "Test connection" to pick tools. Stored in `ProjectConfig.Kind` + `ProjectConfig.McpServers` |
| Debug an MCP server that won't start | "Test connection" in project setup prints the server's stderr. Launch resolution is `Platform.ResolveProcess` |
| Change when MCP servers connect | `NativeAgentView._ConnectMcpAsync`, called from `BtnSend_Click` before `EnsureNewSession` (the frozen prompt lists the tools, so connect must come first) |
| Inspect what color a tool renders | `ToolCallCard.ToolNameBrush(name)` |
| Tune autoscroll behavior | `NativeAgentView.OnLogScrollChanged` + `_ReevaluateLatch` + `ScrollToEnd` |
| Change status splitter look | `Views/Native/AnimatedSplitter.cs` — thickness, palette, streak geometry |
| Change a mode's color | `UserBubble.PlanBubbleBackground` / `PlanAccent`, or `ThemeBrushes.AccentDark` for Implement |
| Add a context-window preset (16k/512k/…) | `ModelCatalogWindow.PRESET_VALUES` + `PRESET_LABELS` arrays |
| Surface a new wire-level provider | New `*Client.cs : ILlmClient` under `Agents/Native/Llm/`. `BuildLoop` chooses the client; add a setting if you need to pick |
| Persist a new field on `NativeAgentSettings` | Add `[Setting("Label", Help="…", Multiline=?)]` property — auto-renders + auto-serializes |
| Mutate a non-`[Setting]` field on settings | Mutate directly, then `_settingsStore.SaveAgent(_agent)` (NOT `Save()`) |
| Hide a `NativeAgentSettings` field from UI but keep it persistent | Drop the `[Setting]` attribute (round-trips like `Models`) |
| Hide a `NativeAgentSettings` field from UI **and** from JSON | Drop `[Setting]` and add `[System.Text.Json.Serialization.JsonIgnore]` (default-on-every-load — what `SystemPrompt` does) |
| Find why a tool was allowed/denied | `%APPDATA%\SlimCode\projects\<slug>\permissions.log.jsonl` — or chat ≡ menu → View audit log |
| Wipe permissions | Delete the JSON file (regenerates on next gate check) |
| Delete a session | Session row/card → right-click → Delete. Audit entries for that session are pruned unless `PreserveAuditOnSessionDelete` is on |
| Name a session | Session row/card → right-click → Name/Rename. Appends a `title` record; `AgentSession.Caption` is null when unnamed (picker then shows the first prompt, in italic) |
| Browse a project's sessions in the sidebar | Chevron `›` on a project row, or double-click it. `MainWindow.Sidebar.cs`; back arrow in the sidebar header returns to the project list |
| Fork the conversation at a past turn | Click the `↶` on any user bubble (rewrites a new session file, drops the message back in the input) |
| Tweak Plan-mode gate behavior | `PermissionGate.CheckAsync` step 0 |
| Tune streaming-flush rate | `NativeAgentView.EnsureFlushTimer` — `TimeSpan.FromMilliseconds(50)` |
| Resolve more LaTeX commands | `AvaloniaMarkdownRenderer.s_latexMap` |

---

## Moved from README (to be merged)

> Developer-facing material taken out of README.md during the user-focused
> rewrite. Parked here verbatim-ish until this file gets its own rework;
> some of it overlaps the sections above.

### Top-level code map

| Path                                | Role |
|-------------------------------------|------|
| `Program.cs`                        | Avalonia bootstrap; forces software rendering |
| `MainWindow.axaml(.cs)`             | Sidebar, header, view switcher; session-picker UI |
| `MainWindow.Tabs.cs`                | Session / subagent tab management |
| `MainWindow.Sidebar.cs`             | Sidebar drill-down into one project's sessions |
| `Core/AgentSettingsStore.cs`        | Per-agent settings JSON; runs `ISettingsMigration` |
| `Core/ExternalTools.cs`             | Fire-and-forget launchers for Explorer / cmd / PowerShell (still Windows-only) |
| `Platform/Platform.Shared.cs`       | `ShellCommand` + the contract every OS variant must satisfy |
| `Platform/Platform.{Windows,Linux}.cs` | Per-OS `static class Platform`; exactly one compiles, chosen by `$(TargetOS)` |
| `Agents/Agent.cs`                   | Base class + capability flags |
| `Agents/AgentRegistry.cs`           | Hard-coded list of known agents |
| `Agents/AgentSession.cs`            | Session-card DTO |
| `Agents/Native/`                    | The in-process native agent and everything it owns |
| `Markdown/`                         | Markdig → Avalonia renderer with task-list and LaTeX support |
| `Views/Native/`                     | Native-agent chat UI (bubbles, tool cards, task panel, ...) |

### Conventions (from README)

- **C# style:** Allman braces, 3-space indent, `_camelCase` private fields,
  `s_camelCase` static fields, `PascalCase` for everything public.
- **Avalonia gotchas:** `ScrollViewer.AllowAutoHide=False` is set globally;
  reserve right/bottom padding (20px / 40px) inside scrolled containers so
  always-visible scrollbars don't clip content.
- **Per-tool color palette** (defined on `ToolCallCard.ToolNameBrush`, reused
  by the audit log and the group card): green=read/list, cyan=glob/grep,
  yellow=write/edit, red=bash, magenta=set_tasks.
- **`AppPaths` owns every app-data path.** Nothing else may call
  `Environment.GetFolderPath(ApplicationData)` or spell `"SlimCode"` as a path
  segment -- that would silently escape the active profile. Build new locations
  on `AppPaths.AppDataRoot` (or `AppDataRootOrNull` when "no APPDATA" should
  disable the feature instead of writing a relative path).
- **Project slugs** for native-agent storage: lowercase, non-alphanumeric
  collapses to `-` (`TranscriptStore.ProjectSlug`).
- **Agent capability flags** are the right place to add new behavior toggles
  (e.g. `CanDeleteSessions`) -- prefer them to type-checking the concrete agent
  class in the UI.

### Images and the clipboard wizard

Images are downscaled to ~1 MP, sent as OpenAI `image_url` content parts
(data URLs) and stored base64-inline in the transcript. There is no vision
capability flag. The clipboard-wizard crop is cut *before* the downscale, so
the kept region gets the whole pixel budget. Selecting a region zooms the view
to it (plus ~20% context, never past 1:1); right-click drops the selection.
With the wizard switched off nothing probes the clipboard at all.

Detecting "a new image is on the clipboard" is per-OS and lives behind
`Platform.TryPeekClipboardImage`. Windows answers it exactly and for free
(clipboard sequence number + format availability, no content read). X11 and
Wayland offer no such signal, so there the button is raised whenever an image
is on the clipboard at activation time, whether or not it is a new one.

### Profiles: naming and accent palette

`--profile=<name>` works too. The name becomes a directory name, so it is
sanitized (separators and invalid filename characters dropped, 48 chars max);
an empty or unusable value falls back to `Default`. Nothing is migrated
between profiles.

Accent `Auto` hashes the profile name to one of seven presets (`Default` is
pinned to the classic blue); `Accent color override` takes a raw hex. Only the
base color is stored; `Themes/AccentPalette.cs` derives the four
`Accent*Brush` slots from it at fixed lightness targets, plus black-or-white
for `TextOnAccentBrush`. Changing it re-colors `{DynamicResource}` consumers
live, but controls built in code-behind hold the `IBrush` they were
constructed with -- those follow on restart. Amber and mint are deliberately
absent from the presets: they carry meaning elsewhere (Plan mode, permission
allow/ask).

### Misc

- The session-picker cache is keyed by `(agentId, path)` and dropped when a
  session tab closes.
- Linux `Platform` specifics: `/bin/sh -c`, non-login shell
  (`Platform/Platform.Linux.cs`).
