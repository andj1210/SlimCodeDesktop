# Deferred features

Ideas intentionally parked for later. Not rushed — revisit when there's time to
think them through.

---

## Build/test output: better error extraction

**Status:** deferred (current head+tail elision is good enough for now)
**Logged:** 2026-05-22 · **Reworded:** 2026-10-20 (matched to current code)

### Problem

`ProjectCommandTool` attaches a step's log when the step fails (or opts in via
`LogOnSucceed`) and shrinks it with `ShellRunner.Elide`: first 120 + last 40
lines, each end additionally clamped to 30 000 / 10 000 chars. This assumes
errors live at the ends. A multi-translation-unit compile breaks that: lots of
"compiling X… ok" noise at the start and a summary at the end, with the actual
error (`error C2065`, etc.) buried in the **middle** — exactly the part elision
drops. Head+tail can silently lose the one line that matters.

Kept as-is for now because: elision only triggers on genuinely long output
(>~160 lines), and the failure mode is "model sees fewer errors," not "model is
misled." (The capture itself is bounded separately at 10 M chars in
`ShellRunner`; that limit is about memory, not context.)

### Idea A — two-pass build-on-error (relies on incremental builds)

- First run is **internal**: capture only the exit code, discard the output.
- On failure, **rerun** the same command and surface only the second run's output
  to the LLM.
- Rationale: with incremental build caching, the previously-successful translation
  units are already built and don't recompile on the second pass — so their
  "…ok" noise is naturally absent and the second run's output is error-focused.
- Caveats / fragility:
  - Depends entirely on incremental/caching behavior. A clean-build command
    (`dotnet build` after `clean`, full rebuild, no cache) reproduces identical
    output → zero benefit, just double wall-clock.
  - Non-deterministic builds may differ between passes.
  - Doubles build time on the failure path.
  - The set of "recompiled" units on pass 2 isn't guaranteed to equal "the failed
    units" for every toolchain.

### Idea B — error-focused output mode (single pass, no rerun)

- Operate on the output we **already capture** (no rerun needed).
- One pass: keep lines matching broad error markers (case-insensitive
  `error:` / `: error` / `error C####` / `fatal` / `FAILED`, covering MSVC,
  gcc/clang, MSBuild/dotnet, most test runners) plus a few lines of context per
  hit; collapse the "ok" runs between hits.
- **Fallback:** if zero markers match (unknown toolchain / odd formatting), fall
  back to head+tail so we never return empty error context.
- There is no output-mode switch today (the elision is unconditional). Likely
  shipped as a per-project or per-step option (head+tail vs. error-focused), so
  the two can be compared on real output.
- Still a heuristic, but cheap and degrades gracefully — much lighter than a
  per-compiler parser, which would be over-engineering.

### Note

Idea B is the cleaner default direction (no rerun, works on existing capture);
Idea A is worth it only where incremental builds make pass 2 strictly cleaner.
They're not mutually exclusive. Revisit when middle-truncation actually bites in
practice.

---

## System-prompt refresh on revert/fork

**Status:** deferred (needs more design thought)
**Logged:** 2026-05-22 · **Reworded:** 2026-10-20 (forks now also come from branch/regenerate)

### Goal

When forking a session — the `↶` revert on a user bubble, or branch/regenerate
on an assistant bubble — optionally refresh the frozen system prompt instead of
carrying the original one forward verbatim. Recompute a
candidate prompt from current settings; if it differs *materially* from the
frozen one, inject the refreshed prompt at the start of the forked session.

**Key constraint:** a changed date/time alone must NOT count as a difference.
The session start timestamp is embedded in the prompt's dynamic block; if a new
timestamp triggered a refresh, every fork would invalidate the LLM prompt cache
for no benefit. Refresh only when the *meaningful* content changed (base prompt
text after an app upgrade, project folder, configured build/test commands, etc.).

### Background / current behavior

- The prompt is built by `NativeAgentSettings.BuildEffectiveSystemPrompt(...)`:
  `<base prompt>` + a volatile tail (`This session started …`, project folder,
  `Initial mode: …`).
- It is frozen at session creation into BOTH the `meta` record and a
  `Type="msg" / Role="system"` record, and replayed verbatim on resume.
- Both fork paths, `OnRevertRequested` and `OnBranchRequested`
  (`NativeAgentView.Branching.cs`), go through `_ForkAtRecord`, which writes a
  fresh meta record with the original frozen prompt and re-emits the history
  (including the system msg) unchanged. That shared function is the one place a
  refresh would go, covering both.

### Why it's fiddly (the honest assessment)

1. **"Ignore date/time" comparison** has two implementations:
   - *String-mask the timestamp line before comparing* — hacky. Creates a silent
     coupling between the prompt's text format and a comparison routine. If
     someone rewords the line later, refresh silently fires every time (kills the
     cache) or never fires (feature dead) — no compile/test failure to catch it.
   - *Separate the volatile block structurally* (expose stable vs. volatile parts
     of the prompt, compare only the stable part) — clean, but a small refactor.
     This is the only version worth shipping.
2. **The prompt lives in two places** (meta record + system msg in history). A
   refresh must update both coherently or they desync (model sees one prompt,
   meta claims another). `_ForkAtRecord` re-emits both today.
3. **Regenerate re-runs immediately.** A branch fork calls `ContinueAsync` right
   after the resume, so any refresh must be in place before that first request —
   and it changes what "regenerate against the identical context" means, since
   the context then isn't identical any more.

### Cleaner alternative (front-runner)

Most of the value is "a forked/long-lived session should pick up newly-configured
build/test commands." That needs no base-prompt surgery:

- **On fork, if the project's command set changed, inject a small
  `[Project commands] …` system message** via the existing `AppendSystemMessage`
  mechanism (same pattern as mode-switch announcements). Probably only for
  revert, where the user writes the next turn anyway; regenerate is meant to
  re-run against the same context.
- No string masking, no prompt-format coupling, no meta/history desync (additive
  record), and date/time never enters the picture so the cache concern is moot.
- Trade-off: does NOT refresh the base prompt text across app upgrades — which is
  arguably correct (an old session stays on the instructions it was created with).

### Decision pending

- **Option A — delta system message:** cheap, clean, fits existing patterns;
  base prompt stays frozen. Covers project-command drift + mode.
- **Option B — full compare-and-replace:** more reach (also refreshes base prompt
  text), but only acceptable if paired with the volatile-block refactor first.

Revisit and pick a direction before implementing.

---

## Same project added multiple times (distinguished by alias)

**Status:** deferred (out of scope for V0.0.1 — effort too high)
**Logged:** 2026-06-02

### Goal

Let the same folder be pinned to the sidebar more than once, each instance with
its own alias, so a user can keep e.g. a "Debug build" and a "Release build" row
for one repo. The per-project **display alias** that this would build on top of
*is* shipped (sidebar label + Project Setup field, keyed by path in `ProjectList`).

### Why it's deferred — project identity is the path, everywhere

Project identity is the path throughout the app. The sidebar passes bare path
strings (`BuildProjectItem(path)`), selection is `_selectedProjectPath`, and
**every** per-project store is keyed by `slug(path)`:

| State | Location | Owner |
|---|---|---|
| Native sessions | `…\sessions\native\<slug>\` | us |
| Native build/test config (`project.json`), permissions, audit log | `…\projects\<slug>\` | us |
| Qwen sessions | `~\.qwen\projects\<slug>\` | **qwen (external)** |
| Nanocoder sessions | filtered by `workingDirectory == path` | **nanocoder (external)** |

So two rows for the same path would **share** sessions, build config, permissions
and skills — which defeats the motivating use case (different build types need
*different* `project.json` build steps, the path-keyed native config).

### What a real implementation would require

- Give `ProjectEntry` a stable **id** (GUID).
- Re-key native per-project storage from `slug(path)` to a per-entry identity —
  cleanest via an optional `instanceId` that suffixes the slug (`null` = today's
  behaviour, backward-compatible). Touches `ProjectMetadataDir`, `TranscriptStore`,
  `ProjectConfigStore`, `ProjectPolicyStore`, the audit-log path.
- Thread the entry id (not just the path) through selection plumbing and the
  `Agent.EnumerateSessions(path)` / `DeleteSession(path, …)` base APIs.
- **Hard limit:** CLI-agent sessions (qwen, nanocoder) *cannot* be split per
  alias — their on-disk layout is path/working-directory based and owned by the
  external tools. Only native state can be made per-entry.

Rough estimate: ~1–2 days, medium-high risk (identity is load-bearing).

### Cleaner alternative to weigh first

The "different build types" need may be better served **without** duplicate paths:
support **multiple named build/test profiles inside one project's config**, picked
at launch. One sidebar entry, one session history, no identity refactor
(~half a day, low risk). Decide between this and true duplicate-paths before
implementing either.

---

## Spell check in the chat input (red squiggles + right-click suggestions)

**Status:** deferred (high effort, weakest value for a coding tool)
**Logged:** 2026-06-12

### Findings

- Avalonia has **no built-in spell check** (unlike WPF's `SpellCheck.IsEnabled`);
  everything is hand-rolled.
- **Engine:** `WeCantSpell.Hunspell` (pure .NET, no native deps) is the obvious
  choice; needs `.dic`/`.aff` dictionary files shipped per language (en/de).
- **Red squiggles are the hard part:** `TextBox` exposes no inline decorations,
  so it takes a custom adorner computing word rectangles via
  `TextLayout.HitTestTextRange`, kept in sync with scrolling, wrapping, caret
  edits, resize, IME. Days of edge cases.
- **Right-click suggestions** are comparatively easy: extend the input box's
  `ContextFlyout` with Hunspell suggestions for the word under the caret.

### Why deferred

Half of chat input in a coding tool is identifiers that would light up as
misspelled (mitigable with a camelCase/snake_case ignore rule, but still).
Worst value-per-effort of the 2026-06-12 feature batch. If ever picked up,
consider suggestions-only (no squiggles) as a cheap first stage.

---

## Syntax highlighting for fenced code blocks

**Status:** deferred (medium effort; clean insertion point already identified)
**Logged:** 2026-06-12

### Findings

- The hook already exists: `AvaloniaMarkdownRenderer.RenderCode` isolates fenced
  blocks **with the language tag in hand** and renders into a
  `SelectableTextBlock` — which supports `Inlines`, so highlighting is "emit
  colored `Run`s instead of one string". No structural change needed.
- **TextMateSharp / AvaloniaEdit route:** full grammar fidelity, but AvaloniaEdit
  targets Avalonia 11 and we are on **Avalonia 12.0.2** — likely no compatible
  release yet. Re-check compatibility before considering this route.
- **Hand-rolled tokenizer route (preferred):** keywords / strings / comments /
  numbers for the languages that matter here (C#, C/C++, JSON, XML/AXAML,
  Python, shell), falling back to plain mono for unknown tags. ~80% of the
  visual benefit, zero new dependencies.

### Note

The same tokenizer would be reusable for coloring the `edit_file` diff view in
`ToolCallCard` (shipped 2026-06; currently plain red/green line tinting).

---

## On-demand tool schemas (keep rarely used tools out of the catalog)

**Status:** deferred (opt in once the tool catalog grows unreasonably large)
**Logged:** 2026-10-20

### Problem

SlimCode is committed to a non-shell harness: capabilities come as dedicated
tools rather than "run this command". Every tool's schema rides along in every
request, so the catalog grows with each feature. The concern is **context fill**
(and tool-choice quality on small local models), not prompt processing time —
llama.cpp caches the static prefix anyway.

### Idea

Keep the essential tools (read/edit/write/grep/glob/bash/...) fully announced.
Rarely used tools get only a one-line catalog entry in the system prompt:

```
Additional tools, load their exact syntax with load_tool first:
- image_read: look at an image file (screenshots, diagrams, UI)
```

The entry must say *when* to use the tool, not just its name — a bare name won't
make a small model reach for it. Same progressive-disclosure pattern as
`use_skill`.

### Catch: the model must still be able to *call* the tool

Reading the syntax is half of it. llama-server parses tool calls against the
request's `tools` array and likely rejects unknown names. Options:

- **A — sticky load:** `load_tool("image_read")` returns the description, and
  from the next request on the tool is in the `tools` array for the rest of the
  session. Native schema validation stays. Cost: changing `tools` breaks the
  cached prefix once per loaded tool. Loaded tools must be recorded in the
  transcript so a resumed session rebuilds the same array (otherwise replayed
  calls don't match).
- **B — generic dispatcher:** one fixed `extended_tool(name, args)` tool that
  dispatches on `name`. The `tools` array never changes, so the cache never
  breaks and llama.cpp is happy. Declare `args` as a free-form JSON **object**,
  not a JSON-in-a-string `args_json` — nested escaping is where small models
  fail. Trade-off: no server-side schema constraint on `args`; validation moves
  into the tool and errors must be good enough to self-correct.

Either way: calling a deferred tool before loading it should return its schema
in the error (and, for A, load it), so a model that skips the step loses one
round, not the task. Avoid the name `help` — models will call it for general
questions.

### Note

Not worth it while the catalog is a dozen essential tools. Revisit when it isn't.
