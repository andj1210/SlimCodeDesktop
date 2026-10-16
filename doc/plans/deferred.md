# Deferred features

Ideas intentionally parked for later. Not rushed — revisit when there's time to
think them through.

---

## Build/test output: better error extraction

**Status:** deferred (current head+tail elision is good enough for now)
**Logged:** 2026-05-22

### Problem

`ProjectCommandTool`'s `Smart` mode, on failure, returns the captured output
with the middle elided (first 120 + last 40 lines). This assumes errors live at
the ends. A multi-translation-unit compile breaks that: lots of "compiling X… ok"
noise at the start and a summary at the end, with the actual error (`error C2065`,
etc.) buried in the **middle** — exactly the part elision drops. Head+tail can
silently lose the one line that matters.

Kept as-is for now because: the 100 KB capture cap means elision only triggers on
genuinely long output (>~160 lines), and the failure mode is "model sees fewer
errors," not "model is misled."

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
- Likely shipped as a third `CommandOutputMode` (`Full` / `Smart` / `Errors`) so
  the modes can be compared on real output.
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
**Logged:** 2026-05-22

### Goal

When forking a session (the `↶` revert button), optionally refresh the frozen
system prompt instead of carrying the original one forward verbatim. Recompute a
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
- `OnRevertRequested` (fork) currently carries the original frozen prompt
  forward unchanged.

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
   meta claims another). The fork path re-emits both today.

### Cleaner alternative (front-runner)

Most of the value is "a forked/long-lived session should pick up newly-configured
build/test commands." That needs no base-prompt surgery:

- **On fork, if the project's command set changed, inject a small
  `[Project commands] …` system message** via the existing `AppendSystemMessage`
  mechanism (same pattern as mode-switch announcements).
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
