# Implementation Plan: `patch` tool (Pia.Wpf)

> **Classification:** `bucket=scratch`. The defining substance of `patch` — a 9-strategy fuzzy
> find/replace engine, V4A multi-file parser, non-exact post-processing chain, unified-diff
> generation, and post-write re-read verification — has **zero** equivalent in Pia.Wpf. The
> existing `write_file` (`FilesToolHandler.cs:207-257`) is a whole-file `File.WriteAllText`
> overwrite with no matching, no diff, and none of the line-ending/BOM/lint-delta/staleness
> machinery the spec says `patch` "shares" with it — **that machinery is itself unbuilt**.
> Reusable pieces are pure scaffolding (`SafeFolderPath`, the `FilesToolCall` pending-action
> guard, `BuiltInPluginHandler`/`PluginService` registration). The engine is net-new.
>
> **Scope of this doc:** `patch` only. Cross-cutting concerns (`task_id` threading, output
> budgeting, lint-delta, staleness registry, workspace root) are flagged as **dependencies owned
> elsewhere** — see [§8 Dependencies](#8-dependencies-owned-elsewhere). This is a PLANNING doc; no
> source is to be written from it without separate sign-off.

Spec source: [`docs/coding-tools-spec/patch.md`](../patch.md) (HIGHEST PRIORITY tool).
Overview invariants: [`docs/coding-tools-spec/overview.md`](../overview.md).
Host layer: [`docs/coding-tools-spec/tool_registration.md`](../tool_registration.md).

---

## 1. Tool contract (restated from spec)

### 1.1 Name & description

- **Name:** `patch`
- **Description (verbatim from spec — the description is part of the contract):**
  > Targeted find-and-replace edits in files. Use this instead of sed/awk in terminal. Uses fuzzy
  > matching (9 strategies) so minor whitespace/indentation differences won't break it. Returns a
  > unified diff. Auto-runs syntax checks after editing. REPLACE MODE (mode='replace', default):
  > find a unique string and replace it. REQUIRED PARAMETERS: mode, path, old_string, new_string.
  > PATCH MODE (mode='patch'): apply V4A multi-file patches for bulk changes. REQUIRED PARAMETERS:
  > mode, patch.
- Registered with `max_result_size_chars = 100_000`.

### 1.2 JSON-Schema parameters

| Param | Type | Default | Required when | Semantics |
|-------|------|---------|---------------|-----------|
| `mode` | string enum `["replace","patch"]` | `"replace"` | always | Edit mode selector. |
| `path` | string | — | `mode=replace` | File to edit. **Relative `..` is allowed here** (worktree navigation) — see [§3.2](#32-the--contradiction-the-sharpest-speccode-collision). |
| `old_string` | string | — | `mode=replace` | Text to find. Must be unique unless `replace_all=true`. Model is told to include surrounding context. |
| `new_string` | string | — | `mode=replace` | Replacement. Empty string `''` = delete the matched region. |
| `replace_all` | boolean | `false` | no | Replace every occurrence instead of requiring uniqueness. |
| `patch` | string | — | `mode=patch` | V4A-format multi-file patch (see [§4](#4-patch-mode-v4a-multi-file-format)). |
| `cross_profile` | boolean | `false` | no | Hermes multi-profile soft-guard opt-out. **Pia has no multi-profile concept → drop or accept-and-ignore.** Recommend: accept in schema for contract fidelity, ignore in handler. |

Top-level `required: ["mode"]`. Per-mode required-arg validation is enforced in the handler (self-healing — see [§3.7](#37-self-healing-arg-validation)).

### 1.3 Return shape

`patch` must return a **structured object** (JSON-serialized), not a plain string. This is a
departure from current handlers, which return mostly `string`. The `object?` return type already
supports this; serialize the shape below.

| Field | Type | When | Notes |
|-------|------|------|-------|
| `success` | bool | always | |
| `diff` | string (unified diff) | on success | Always present on success — auditability. Head+tail truncated under budget. |
| `files_modified` | string[] | on success | Relative (and/or resolved) paths. |
| `resolved_path` | string | on success | Absolute path actually edited (worktree debugging). |
| `lint` / `lsp_diagnostics` | object/null | optional | Delta-filtered; **dependency, not built here** (§8). |
| `_warning` | string/null | optional | Staleness / workspace divergence; non-blocking. **Dependency** (§8). |
| `_hint` | string/null | optional | Failure-mode guidance; escalates after 3+ consecutive failures on the same file. **Depends on `task_id`** (§8). |
| `error` | string | on failure | With "Did you mean?" closest-fuzzy-candidate on zero matches. |

### 1.4 Required invariants (spec)

1. Fuzzy match is **mandatory** — never ship exact-only.
2. 9 strategies tried **in order**; first that yields ≥1 match wins.
3. Non-exact match → 3 correction passes (escape-drift reject → reindent → conditional unescape).
4. Uniqueness rule + `replace_all` applied **end-to-start**.
5. V4A: **reject `..` in header paths**; per-file locks in **sorted path order**.
6. Post-write **re-read verification** (normalize line endings, strip BOM for compare).
7. Line-ending + BOM **preservation**.
8. Delta-filtered syntax check; unified diff in result; staleness `_warning`s.
9. `(task_id, resolved_path)` consecutive-failure counter → escalating `_hint` at 3+.
10. Does **not** hard-require a prior `read_file` — it reads the file itself; only *warns* on staleness.

### 1.5 Inherited / N/A overview invariants

The overview lists 10 global design principles. Most map to dedicated sections above; the remaining two:

- **Line-numbered reads (coordinate system).** `patch` *anchors* on the `LINE_NUM|CONTENT` numbers that `read_file` emits (per patch.md "Related") but does **not** require a prior read — it reads the file itself ([§1.4](#14-required-invariants-spec) invariant 10). No line-number handling is built into `patch` itself.
- **Pagination (`offset`/`limit`).** **N/A to `patch`** — that is a `read_file`/`search_files` concern. `patch`'s large-output risk is the returned `diff`, handled by head+tail truncation under the budget cap ([§9 Q7](#9-open-questions)).

---

## 2. Placement in Pia.Wpf

Build **alongside** `FilesToolHandler`, not inside it (see [§7 Q5](#q5-extend-vs-rebuild-filestoolhandler-build-alongside)). Follows the existing built-in tool-handler convention exactly.

### 2.1 New types

| Type | Path | Role |
|------|------|------|
| `IPatchToolHandler` | `src/Pia.Wpf/Services/Interfaces/IPatchToolHandler.cs` | Mirrors `IFilesToolHandler`: `IsAvailable`, `GetTools()`, `HandleToolCallAsync(...)`, `ExecutePendingActionAsync(...)`. Declares a `PatchToolCall` record (parallel to `FilesToolCall`). |
| `PatchToolHandler` | `src/Pia.Wpf/Services/PatchToolHandler.cs` | Dispatch, arg validation, pending-action prep, structured return assembly, privacy logging. No matching logic (delegates to engine). |
| `FuzzyPatchEngine` | `src/Pia.Wpf/Infrastructure/FuzzyPatchEngine.cs` | **Pure, DI-free, WPF-free `static` class.** The 9-strategy chain + correction passes + uniqueness. This is the testability keystone (§6). |
| `SequenceMatcher` | `src/Pia.Wpf/Infrastructure/SequenceMatcher.cs` | Hand-rolled Ratcliff/Obershelp `.ratio()` equivalent — .NET has none, and it is load-bearing for strategies 8 & 9. |
| `V4APatchParser` | `src/Pia.Wpf/Infrastructure/V4APatchParser.cs` | Parses `*** Begin/Update/Add/Delete/Move/End` headers + hunk lines; emits a structured per-file op list with **its own `..` rejection** on header paths. |
| `UnifiedDiff` | `src/Pia.Wpf/Infrastructure/UnifiedDiff.cs` | Generates unified-diff text from (old, new) line sequences. Hand-rolled (no DiffPlex — §7 Q6). |

> Placing the engine pieces under `Infrastructure/` (where `SafeFolderPath` lives) keeps them
> dependency-free and unit-testable without the WPF host. The handler under `Services/` carries the
> DI, logging, and host-integration concerns.

### 2.2 Reusable patterns to follow

- **Registration via `GetTools()`** — return one `AIFunctionFactory.Create(PatchSchema, "patch", "<desc>")`. Use a private static `PatchSchema(...)` signature method with `[Description]` attributes, exactly like `WriteFileSchema` (`FilesToolHandler.cs:316-319`).
- **Dispatch via `HandleToolCallAsync`** — switch on `toolCall.Name`; `patch` is a **write** → returns a pending action (never executes inline). Read-only validation/match-preview happens at prep time.
- **Pending-action approval guard** — return a `PatchToolCall` record (ToolName, Description, Details, TargetPath, `Execute`) just like `FilesToolCall`. `BuiltInPluginHandler` maps it to a `PluginToolCall`; `ChatSession.HandleToolCall` builds the `ActionCardInfo` and blocks in `ChatState.WaitingForTool` until the user accepts. **Prep computes the match + diff for the card; execute re-verifies** (see [§3.4](#34-prep-vs-execute-the-approval-guard-subtlety)).
- **Sandbox/path-safety** — `SafeFolderPath.TryResolveInside` for V4A header paths and (with the workspace caveat in §3.2) for replace-mode `path`. Re-validate inside the deferred `Execute` lambda, mirroring `FilesToolHandler.cs:235`.
- **Privacy logging** — `LogInformation` for tool actions/byte counts; `LogWarning` for rejections; `_logger.SensitiveDebug(...)` for paths, `old_string`/`new_string`/`patch` payloads, and diff content (all user-content per CLAUDE.md). `#if DEBUG` for the args dump (`FilesToolHandler.cs:97-99`). No full paths in release logs.

### 2.3 DI wiring & registration plug-in points

| Location | Change |
|----------|--------|
| `Bootstrapper.cs` (~line 250, next to `IFilesToolHandler`) | `services.AddSingleton<IPatchToolHandler, PatchToolHandler>();` |
| `BuiltInPluginDefaults.cs` | Add a well-known GUID (next in sequence: `...-000000000007`), add to `PreloadedPluginIds`, add a `SyncPlugin` default with `handlerId` + `systemPromptAddition` describing `patch`'s two modes and the relative-path rules. |
| `BuiltInPluginHandler.cs` | Add `FromPatchHandler(IPatchToolHandler, SyncPlugin)` factory mirroring `FromFilesHandler`. **Gate availability on the same workspace/sandbox `IsAvailable`** so the tool is hidden when no workspace is configured. |
| `PluginService.cs` (~line 86) | Add `"patch" => BuiltInPluginHandler.FromPatchHandler(_patchToolHandler, config)`; inject `IPatchToolHandler` in the ctor (mirror `_filesToolHandler`). |

**Open decision:** whether `patch` is its own plugin pack or folded into the existing `files`
plugin. Recommend a separate `patch` (or `coding-files`) pack so it can be toggled independently and
its workspace-root availability differs from the simple sandbox files plugin.

---

## 3. REPLACE mode: engine design

### 3.1 The 9-strategy chain (in order; first ≥1 match wins)

Signature (match spec exactly):
`FuzzyFindAndReplace(string content, string oldString, string newString) -> (string newContent, int matchCount, string strategyName, string? error)`.
Each strategy returns a list of `(startOffset, endOffset)` **character spans into the original `content`**.

| # | Strategy | Method |
|---|----------|--------|
| 1 | `exact` | Direct substring search. |
| 2 | `line_trimmed` | Trim each line, match line-blocks. |
| 3 | `whitespace_normalized` | Collapse space/tab runs to one space (preserve `\n`); match normalized; map spans back to original. |
| 4 | `indentation_flexible` | Ignore all leading whitespace per line. |
| 5 | `escape_normalized` | Convert literal `\n` `\t` `\r` in pattern to real bytes, then exact. Skip if pattern has none. |
| 6 | `trimmed_boundary` | Trim only first+last lines; middle verbatim. |
| 7 | `unicode_normalized` | Smart quotes/dashes/ellipsis → ASCII on both sides; exact then line-trimmed; map back. |
| 8 | `block_anchor` | Anchor on first+last stripped line; `SequenceMatcher.ratio()` on the joined middle. Threshold **0.50** (1 candidate) / **0.70** (ambiguous). Needs ≥2 pattern lines. **Do NOT loosen** (0.10/0.30 was dangerous). |
| 9 | `context_aware` | Sliding window of `len(pattern_lines)`; per-line `ratio()` on stripped lines ≥ **0.80**; block accepted if `high_similarity_count / pattern_line_count ≥ 0.50`. |

`SequenceMatcher.ratio()` = `2*M / (len_a + len_b)` over the longest-matching-block decomposition
(Ratcliff/Obershelp). Implement and unit-test it standalone — strategies 8 & 9 are wrong without it.

### 3.2 The `..` contradiction (the sharpest spec/code collision)

`SafeFolderPath.TryResolveInside` (`SafeFolderPath.cs:44`) rejects **any** path that resolves outside
root — upward `..` fails the `StartsWith(fullRoot)` check. But the spec says:

- **V4A header paths:** `..` must **always** be rejected (model-generated injection vector) — matches/extends current behavior. ✅ Reuse `TryResolveInside` as-is.
- **Replace-mode `path`:** relative `..` *is* **legitimate** worktree navigation — `TryResolveInside` would wrongly reject it. ❌ Cannot reuse as-is.

**Resolution:** replace-mode `path` needs resolution against a **workspace root** where intra-workspace
`..` is permitted but escapes are still rejected (resolve, then verify the result is still inside the
workspace root). This is **tied to the Q2 workspace-scope decision** ([§7 Q2](#q2-filesystem-scope-introduce-a-workspace-root-open-product-call)). Until that lands, replace-mode cannot be implemented exactly as specified. **This blocks correctness — surfaced prominently in [§9 Open Questions](#9-open-questions).**

### 3.3 Non-exact correction passes (apply in order, only when strategy ≠ `exact`)

A non-exact match means the matched file region differs from `old_string`, so writing `new_string`
verbatim would corrupt the file. Apply:

1. **Escape-drift guard (reject, don't write).** If `old_string` and `new_string` both contain `\'` or `\"` but the matched file region does **not**, the transport injected spurious backslashes → return an error rather than writing literal `\'` into source.
2. **Reindent `new_string`.** Compute each new line's indent relative to the *shallowest* line of `old_string`, then re-anchor onto the matched region's actual base indent (adopts the file's real indent width: model's 2-space → file's 4-space).
3. **Conditional control-char unescape in `new_string`.** `\t`→tab only if the matched region contains a real tab; `\r`→CR only if it contains a real CR. **Never** convert `\n` (newlines serialize fine through JSON). Preserves legit literals like `sep = "\t"`.

### 3.4 Prep vs execute (the approval-guard subtlety)

`patch` is a write → it returns a `PatchToolCall` pending action for the `ActionCardInfo`. But the
match offsets and diff are computed at **prep** time (to populate the card), and the file may change
before the user accepts at **execute** time.

**Decision:** the `Execute` lambda must **recompute the match against current file content and
re-verify**, not blind-write prep-time content. After writing, perform the spec's **post-write
re-read verification** (re-read, normalize line endings, strip BOM, compare). If the file changed
between prep and execute such that the match no longer holds → return an error/`_warning`, do not
write stale content.

### 3.5 Uniqueness / multiplicity

- 0 matches → `error` + "Did you mean?" (closest fuzzy candidate via `SequenceMatcher` ranking).
- >1 match and `replace_all=false` → `error`: "Found N matches. Add context to make it unique, or set replace_all=true." **Do not guess.**
- `replace_all=true` → replace all, applying spans **end-to-start** so earlier offsets stay valid.

### 3.6 Line-ending + BOM preservation, post-write verification

Same machinery the spec says `write_file` shares — **but that machinery is itself unbuilt** in Pia
(`FilesToolHandler` does plain `File.WriteAllText`). So `patch` must implement it directly (or a
shared util both will use): detect the file's dominant CRLF/LF, normalize written content to match;
detect+restore a leading BOM; re-read after write and byte-compare (line-ending-normalized,
BOM-stripped) to catch silent FS/truncation failures.

### 3.7 Self-healing arg validation

Per-mode required-arg checks return a **precise corrective error** (never silent no-op):
- `mode=replace` missing `path`/`old_string`/`new_string` → name the missing arg; if `old_string` present but `new_string` missing, hint "pass empty string to delete."
- `mode=patch` missing `patch` → corrective error.
- Non-string where string expected → type error.
- Detect `old_string`/`content` that is obviously `read_file` display text (`N|` prefixed lines) — the model echoed a tool result back. (Mirrors the **spec'd** `write_file` "Internal-content guard", `write_file.md:62` — note this guard is **not** yet implemented in Pia's `FilesToolHandler.PrepareWriteFile`, so `patch` builds it fresh.)

---

## 4. PATCH mode: V4A multi-file format

```
*** Begin Patch
*** Update File: path/to/file
@@ optional context hint @@
 unchanged context line   (leading space)
-removed line             (leading minus)
+added line               (leading plus)
*** Add File: path/to/new
+line 1 of new file
*** Delete File: path/to/old
*** Move File: old/path -> new/path
*** End Patch
```

- Headers: `*** Update File:`, `*** Add File:`, `*** Delete File:`, `*** Move File: a -> b`. `*** Begin Patch` optional; `*** End Patch` recommended terminator.
- Hunk lines: ` ` context, `-` remove, `+` add. `@@ ... @@` optional locator hint — used to disambiguate which region to apply against (anchor via the same fuzzy engine where possible).
- **Security: reject `..` in any header path** (always — these are model-generated; distinct from replace-mode `path`).
- **Per-file locks in sorted path order** before applying, so concurrent subagents can't interleave/deadlock. Use a process-wide keyed lock (e.g. a `static` `ConcurrentDictionary<string, SemaphoreSlim>` keyed by resolved absolute path); acquire all in sorted order, apply, release in reverse.
- Apply each file's hunks; assemble per-file diffs; aggregate `files_modified`.

---

## 5. Build / implementation checklist

Sequenced **engine-first** (the overview steer: "`patch` is the highest-leverage and hardest to get
right — budget the most effort there"). Each engine item is testable in isolation (§6).

- [ ] `SequenceMatcher.ratio()` (Ratcliff/Obershelp) + its tests — **nothing else is correct without it** (strategies 8 & 9 depend on it).
- [ ] 9 strategies in order; first non-empty wins; spans into original content.
- [ ] Non-exact correction passes: escape-drift reject → relative reindent → conditional `\t`/`\r` unescape.
- [ ] Uniqueness rule + `replace_all` end-to-start application.
- [ ] V4A parser: header types, hunk lines, `@@` locator, **`..` rejection** on header paths, sorted per-file locking.
- [ ] Line-ending + BOM preservation; post-write re-read verification (normalize + BOM-strip compare).
- [ ] Unified-diff generation (hand-rolled `UnifiedDiff`).
- [ ] `mode` dispatch + per-mode self-healing arg validation in `PatchToolHandler`.
- [ ] `PatchToolCall` pending-action wiring; prep computes match+diff for the card, **execute re-verifies** against current file.
- [ ] Structured return assembly (`success`/`diff`/`files_modified`/`resolved_path`/`error`); privacy-safe logging.
- [ ] Registration: `Bootstrapper.cs`, `BuiltInPluginDefaults.cs`, `BuiltInPluginHandler.FromPatchHandler`, `PluginService.cs`.
- [ ] "Did you mean?" closest-fuzzy-candidate suggestion on zero matches.
- [ ] *(deferred — §8 deps)* `(task_id, path)` failure counter → escalating `_hint`; lint-delta; staleness `_warning`; output budgeting.

---

## 6. Test strategy (xunit.v3)

Per `MEMORY.md`: tests run **xunit.v3 + plain `Xunit.Assert`** (no FluentAssertions); MTP via
`global.json`. New `.cs` test files must be **CRLF** (Write tool emits LF — convert).

### 6.1 Pure-engine table tests (the bulk)

`FuzzyPatchEngine` and `SequenceMatcher` are pure functions → table-driven tests, no WPF/DI harness.

| Suite | Cases |
|-------|-------|
| `SequenceMatcherTests` | `.ratio()` against known Ratcliff/Obershelp values; empty strings; identical; disjoint. |
| Per-strategy tests | One suite per strategy (1-9): a fixture that *only* that strategy can match, asserting `strategyName` + span correctness. Critical: assert strategy **order** (e.g. an exact-matchable input never falls through to fuzzy). |
| `BlockAnchorThresholdTests` | Assert 0.50/0.70 boundaries hold; a 10%-similar middle does **not** match (regression guard against the dangerous 0.10/0.30). |
| Correction-pass tests | escape-drift reject; reindent (2-space model → 4-space file); conditional `\t`/`\r` unescape with the `sep = "\t"` literal-preservation case. |
| Uniqueness tests | 0-match → "Did you mean?"; >1 + `replace_all=false` → error; `replace_all=true` end-to-start span integrity. |

### 6.2 Line-ending / BOM fixtures (directly testable)

CRLF, LF, and BOM/no-BOM fixture files → assert the written file preserves the original ending and
BOM, and post-write re-read verification passes. (CRLF/LF preservation is exactly the kind of thing
`MEMORY.md` warns about — keep fixtures byte-exact.)

### 6.3 V4A parser tests

Each header type; multi-file ordering; `@@` locator; **`..` rejection** in every header form;
malformed-patch corrective errors; sorted-lock acquisition order (can assert ordering deterministically).

### 6.4 Handler-level tests

Arg-validation self-healing (each missing-arg path); mode dispatch; structured-return shape
(`success`/`diff`/`files_modified`/`resolved_path`/`error`) serializes correctly; pending-action
prep produces an `ActionCardInfo`-compatible `PatchToolCall`; execute-time re-verification rejects a
changed file. (These need the handler but can stub `ISettingsService`.)

---

## 7. Cross-cutting questions — positions

| # | Question | Position |
|---|----------|----------|
| Q1 | Code-exec security model | **N/A to `patch`** — it is a file edit; the existing pending-action approval guard (`ActionCardInfo`) covers it. The hardline/dangerous-pattern guard belongs to `terminal`/`execute_code` plans. |
| Q2 | Filesystem scope | **The real crux.** Recommend a distinct **"workspace root"** rather than overloading `AppSettings.AssistantFilesFolder` (the simple sandbox). Coding tools need repo-wide access *and* intra-workspace `..`. This is a product/architecture call → **open question** (§9). Privacy: every resolved path stays `SensitiveDebug`/`SafeUrl`-gated regardless of scope. |
| Q3 | Native vs MCP | **Build native.** The 9-strategy fuzzy engine + V4A parser + correction passes have no off-the-shelf MCP/shell equivalent; a filesystem MCP server cannot reproduce this contract. |
| Q4 | `task_id` threading | **AsyncLocal**, mirroring the existing `TokenMapAmbient` pattern — seed from `ChatSession.Id` in `RunTurnAsync`. Avoids changing every handler signature. Unblocks the `(task_id, resolved_path) → consecutive_failures` counter. **Owned by tool_registration**, not solved here (§8). |
| Q5 | Extend vs rebuild `FilesToolHandler` | **Build alongside** — new `IPatchToolHandler`/`PatchToolHandler` + standalone engine. Do **not** extend `FilesToolHandler` in place (risks regressing the current sandbox UX). Note: the "shared with `write_file`" line-ending/BOM/lint-delta util is **itself unbuilt** — `patch` either builds it or a shared util both adopt. |
| Q6 | Python runtime | **N/A to `patch`.** The minimal-deps angle here = **no diff library** (no DiffPlex/FuzzySharp) — hand-roll `SequenceMatcher` and `UnifiedDiff`, consistent with the user's minimal-dependency preference (`MEMORY.md`). |

---

## 8. Dependencies owned elsewhere

These are required by the spec's "shared post-edit machinery" but are **not** built in Pia today.
`patch` consumes them; it should not be the place they are designed. Track separately.

- [ ] **`task_id` threading** (AsyncLocal from `ChatSession.Id`) — owned by `tool_registration`. Blocks `_hint` escalation.
- [ ] **Output budgeting / `max_result_size_chars`** — Pia dispatch has *no* tool-result size cap (capability map confirms). Needed for the 100K cap + head+tail diff truncation.
- [ ] **Lint-delta / LSP diagnostics** — no lint/LSP infrastructure exists. `lint`/`lsp_diagnostics` fields degrade to `null` until built.
- [ ] **Staleness / cross-agent file registry** — no read-state mtime tracking exists. `_warning` degrades to absent until built.
- [ ] **Workspace root** — Q2 product decision. Blocks replace-mode `path` resolution as specified.
- [ ] **Shared line-ending/BOM/atomic-write util** — `write_file` is supposed to share it but is a plain overwrite; first implementer builds it.

---

## 9. Open questions

1. **Workspace root vs sandbox folder (Q2 — blocks correctness).** Replace-mode `path` allows relative `..` (worktree navigation), which `SafeFolderPath.TryResolveInside` currently rejects. Does Pia introduce a dedicated "workspace root" (repo-wide, intra-workspace `..` permitted, escapes rejected), or stay on the single `AssistantFilesFolder` sandbox? Until decided, replace-mode cannot resolve paths exactly as the spec describes. **Highest-priority decision.**
2. **`task_id` source & lifetime.** Confirm `ChatSession.Id` is the right key and that an AsyncLocal seeded in `RunTurnAsync` reaches the handler through `PluginService.RouteToolCallAsync` without signature changes. Background/multi-assistant sessions must each get a distinct key.
3. **Plugin packaging.** Is `patch` its own built-in plugin pack (independent toggle, workspace-root availability) or folded into the existing `files` pack?
4. **Approval-card granularity.** A V4A patch can touch many files. Does the `ActionCardInfo` show one card for the whole patch, per-file cards, or a summary with file count + aggregate diff preview? Current cards are single-action.
5. **Structured-return rendering.** Current `ActionCardBuilder`/result path mostly handles strings. How is the structured `{success, diff, files_modified, ...}` object rendered in the chat result and to the model — JSON string, or a typed result the dispatcher serializes?
6. **`cross_profile` param.** Keep in schema for contract fidelity (accept-and-ignore) or drop entirely since Pia has no multi-profile concept?
7. **Diff size under budget.** With no budgeting layer yet, how is a large `diff` truncated head+tail before re-entering context — a `patch`-local cap as a stopgap, or wait for the shared budgeting layer (§8)?
8. **Lint language coverage.** When lint-delta lands, which languages/extensions does Pia support in-process (JSON/YAML/TOML) vs shelling out, given the privacy-first, minimal-deps constraints?
