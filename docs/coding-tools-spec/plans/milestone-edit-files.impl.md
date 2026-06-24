# Implementation Plan — "Edit Files" (read_file + search_files + write_file)

**Status:** executable implementation plan. **Date:** 2026-06-23.
**Supersedes:** the sequencing in `milestone-edit-files.md` §7. Background/rationale still lives in
`milestone-edit-files.md` and the per-tool docs (`read_file.reuse.md`, `search_files.plan.md`,
`write_file.reuse.md`).

This doc folds in (a) a full citation re-verification against the live tree on 2026-06-23, (b) the gaps
the milestone doc glossed, and (c) three scope decisions taken in review (below). It is written to be
handed to an implementer with corrected `file:line` anchors.

---

## 0. Decisions locked in review

| Fork | Decision | Consequence |
|---|---|---|
| **write_file scope** | **Full `write_file.reuse.md`** — incl. delta-filtered post-write lint | Phase 2 grows: structured return, arg-hardening, sensitive-path blocklist, internal-content guard, and an in-process lint helper (owned here, reused by future `patch`). |
| **Path model (§0.3)** | **Accept in-base absolute paths** + canonicalization | New permissive resolver; **all four** existing file tools migrate to it. Junction/symlink hole closed. Sensitive-path blocklist now genuinely load-bearing. |
| **Diff-preview fidelity** | **True line-level diff** in the card | Extend `ActionCardInfo` model **and** `ActionCardControl.xaml` beyond the Label/Value row scaffold; hand-roll an LCS line diff (minimal-deps). |

**Baked-in defaults (no further sign-off needed):**

- `TaskAmbient` carries `Guid?` (not `Guid`) — `ChatSession.Id` is nullable; direct test callers bypass the
  manager that assigns it, so a non-nullable payload would NRE.
- `TaskAmbient`'s **reader is `FilesToolHandler`** via the staleness store (keyed by `TaskAmbient.Current`).
  This is what makes §0.1 observable — an ambient with no reader is a no-op.
- `search_files` backend = **hand-rolled in-process, synchronous**; `rg` deferred (honors the minimal-deps
  preference and sidesteps an async refactor for search).
- `read_file` keeps **no hard extension gate**; add a NUL-byte binary sniff for unknown/extensionless files
  (preserves today's "reads anything text" behavior — adopting `DroppedFileReader.Classify`'s allow-list
  verbatim would *narrow* acceptance and regress `.go/.rs/.java/.tsx/.c/.cpp/.h…`).

---

## 1. Corrected citation map (READ FIRST)

The milestone doc's anchors drifted. Corrections that matter:

| Milestone says | Reality |
|---|---|
| `Services/Tools/FilesToolHandler.cs` (all of §1.1) | **`src/Pia.Wpf/Services/FilesToolHandler.cs`** — no `Tools/` subfolder. Line numbers within are exact. |
| "three file tools" | **Four**: `list_files`, `read_file`, `write_file`, `delete_file` (`GetTools` :76-89). |
| Registered at `Bootstrapper.cs:250` | `:250` is **DI only**. Real registration: `PluginService.InitializeBuiltInPlugins` — `FromFilesHandler` at **:86**, `RegisterHandler` at **:90**. |
| "gated by `FilesToolHandler.IsAvailable`" | **Two-layer**: `IsPluginEnabled` in `GetAllTools` (`PluginService.cs:230`) **+** the `isAvailable` callback inside `BuiltInPluginHandler.GetTools` (`:42`, callback set at `:201`). |
| "allow-list only" filtering, add search_files to it | **Wrong.** No-@-command path (`AssistantPromptComposer.cs:49`) passes **all** tools unfiltered; file tools are in **no** allow-list (`GetAtCommandToolMapping` :166-186 has only Memory/Todo/Reminder/Research). **No allow-list edit needed.** |
| files system-prompt at `BuiltInPluginDefaults.cs:93-94` | **`:93` only** (`:94` is `UpdatedAt`). The string enumerates "Tools: list_files, read_file, write_file, delete_file". |
| FromFilesHandler threading "line :201" | **`:196-197`** (the `PluginToolCall(...)` construction). `:201` is `isAvailable:`. Mirror at `FromPluginHandler` **:172-173**. |
| `ActionCardBuilder.Build` :24-63 | **:24-64**. |
| `WaitForUserDecisionAsync` :55-64 | Method at **:64**; `:55` is the `_tcs` field. |
| SafeFolderPath containment "35-44" | Extractable unit is **:31-45** (the root `GetFullPath` the `StartsWith` compares against starts at :31). |

Confirmed-exact (no change): `TokenMapAmbient.cs:18-28`; `ChatSession.cs:46/:197-202/:361/:396-487`;
`IPluginToolHandler.cs:19-20`; `IAssistantPromptComposer.cs:29-33`; `SafeFolderPath.cs:27`;
`FilesToolHandler.cs:23/:56/:72-90/:92-119/:161-163/:177-205/:207-256/:229/:312-314/:325-353`;
`DroppedFileReader.cs:82-115/:117-181/:64-80`; `PluginService.cs:196-207/:222-243/:551-572`;
`ActionCardInfo.cs:34/:37`; `ActionCardControl.xaml:134-166`; `IFilesToolHandler.cs:5-10`;
`AssistantPromptComposer.cs:130-147`.

---

## 2. Phase 0 — Foundation

### 0.1 `TaskAmbient`

- New `src/Pia.Wpf/Services/TaskAmbient.cs` — copy `TokenMapAmbient.cs:18-28` verbatim, backing
  `AsyncLocal<Guid?>` instead of `AsyncLocal<ITokenMapService?>`.
- **Set** alongside the existing `TokenMapAmbient` set at `ChatSession.cs:201-202`
  (`var previousTask = TaskAmbient.Current; TaskAmbient.Current = Id;`). The ambient set here flows down the
  `await` chain into `HandleToolCallWithStatus` (:396) → `HandleToolCall` (:404) → `RouteToolCallAsync`
  (:412), which runs **inside** the same `try`, so tool handlers see it with **zero parameter plumbing**.
- **Restore** in the same `finally` as `TokenMapAmbient` at `ChatSession.cs:361`
  (`TaskAmbient.Current = previousTask;`).
- **Reader:** `FilesToolHandler` reads `TaskAmbient.Current ?? Guid.Empty` to key the staleness store
  (§0.2, §1.1, §2). No other reader.
- Update the doc-comment at `ChatSessionManager.cs:100` (it documents the `TokenMapAmbient` set-in-RunTurnAsync
  pattern) to mention `TaskAmbient` for consistency.
- **Why an ambient, not a signature change:** `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent,
  CancellationToken)` (`:19-20`) is driven by the MS.Extensions.AI tool-invocation path; `FunctionCallContent`
  has no task_id slot. (Note: the interface itself is project-owned — only the *parameter type* is
  MS.Extensions.AI's; the conclusion stands.) `PrepareTurn` (`IAssistantPromptComposer.cs:29-33`) doesn't
  consume task_id, so an earlier set-site buys nothing.
- **Null safety:** `ChatSession.Id` is `Guid?` (`:46`), assigned via `SetIdentity` (`ChatSession.cs:102-109`,
  sets `Id` at :104) from the manager under an `Id is null` guard (`ChatSessionManager.cs:324-329`) before the
  fire-and-forget dispatch (`:435`). On the manager path `Id` is non-null by the time `RunTurnAsync` runs;
  the `Guid?` payload covers direct test callers that skip `SetIdentity`.
- **Acceptance:** a handler reading `TaskAmbient.Current` mid-turn sees that session's `Id`; interleaved
  background turns don't bleed; a direct `RunTurnAsync` call with null `Id` does not NRE.

### 0.2 `IFileStalenessStore`

- Greenfield (no existing store / `Path.GetRealPath` anywhere). New `IFileStalenessStore` + singleton impl.
- API: `RecordRead(Guid taskId, string resolvedPath, DateTime mtimeUtc)` and
  `CheckStaleness(Guid taskId, string resolvedPath, DateTime currentMtimeUtc) → bool stale`.
- Keyed by `(taskId, resolvedPath)` — the **canonicalized resolved** path (§0.3), never the model string.
- DI singleton adjacent to `Bootstrapper.cs:250` (sits between `ITodoToolHandler` :249 and plugin services
  :251+). Inject into `FilesToolHandler`. Tie eviction/lifecycle to `IsAvailable`/`SettingsChanged`
  (the sandbox folder can be cleared at runtime; `PluginService` already rebuilds `_toolNameRoutes` on
  `SettingsChanged` :70).
- **Acceptance:** read records mtime; unchanged-mtime write → `stale=false`; out-of-band touch between read
  and write → `stale=true`.

### 0.3 Path resolver — canonicalization + in-base absolute

- Today `SafeFolderPath.TryResolveInside` rejects **all** rooted paths at `:27`
  (`if (Path.IsPathRooted(trimmed)) return false;`) and enforces lexical containment at **:31-45**.
- **Extract** the lexical-containment unit (`:31-45`: root `GetFullPath`, separator append, combine,
  `StartsWith` guard, root-self guard) into a shared private helper.
- **Add** a clearly-named second entry point (`TryResolveInsideAllowingAbsolute`) that:
  1. accepts rooted/absolute **and** relative inputs,
  2. normalizes via `Path.GetFullPath`, then **canonicalizes via `Path.GetRealPath`** (resolves
     junctions/symlinks),
  3. runs the **shared** containment check against the base.
- **`GetRealPath` on non-existent paths throws** (`write_file` creates new files; `read_file` not-found).
  Canonicalize the **longest existing ancestor** (walk up to the first existing dir), then re-append the
  non-existent leaf and re-run lexical containment on the result. Spec this explicitly — it is the one
  fiddly bit.
- **Migrate all four file tools + search** to the new resolver (decision: accept in-base absolute). Current
  `TryResolveInside` call sites to switch: `read_file` :180, `write_file` :212 (prepare) **and** :235
  (Execute closure), `delete_file` :263 (prepare) **and** :280 (Execute closure), `list_files` (and its
  loop). Because write/delete re-resolve inside the deferred `Execute`, canonicalization covers the
  confirm-time path automatically.
- **Then delete** the now-redundant lexical-only list-net at `FilesToolHandler.cs:161-163` (canonicalization
  in the resolver supersedes it).
- **Do not** add a mode-flag to the original `TryResolveInside`; keep `:27`'s reject-all-rooted contract
  intact for any caller that still wants strict relative-only.
- **Configured-root blind spot:** `UpdateFolder` only `Path.GetFullPath`s the root (`:68`), never reparse-
  resolves it. Canonicalize the configured root once in `UpdateFolder` so a junction in the root path itself
  isn't a hole.
- **Acceptance:** in-base absolute resolves; out-of-base absolute rejected; an in-base junction pointing
  outside is rejected after `GetRealPath`; `..` traversal escaping the base rejected; a new-file path under a
  real in-base dir resolves (non-existent-leaf case).

---

## 3. Phase 1 — Read-only

### 1.1 Enrich `read_file` (`FilesToolHandler.cs:177-205`)

Current: resolve → `File.Exists` → `info.Length > MaxReadBytes` hard-fail (`:192-193`, `MaxReadBytes`=256 KB
`:23`) → `File.ReadAllText` (`:195`) → **bare string** (`:198`). No line numbers, windowing, or binary gate.
Schema path-only (`:312-314`). Only string args parsed (`GetStringArg` :325-336 / `GetOptionalStringArg`
:338-353 — **no int helper exists**).

Build:
- **Numeric arg helper** — add `GetOptionalIntArg` (parse `JsonValueKind.Number`, fall back to string parse,
  then default). None exists today.
- **Schema** — add `offset` (1-indexed, default 1, min 1) and `limit` (default 500, max 2000) to
  `ReadFileSchema`.
- **Line numbering** — emit `LINE|CONTENT` (1-indexed, no padding). Changes model-visible output for **every**
  read (update tests + prompt — §5, §6).
- **Windowing** — slice `[offset, offset+limit)`; out-of-range offset → empty content + correct `total_lines`.
- **Caps** — replace the 256 KB byte hard-fail with a ~100K-char cap on the **formatted** window + a 2000-line
  cap; on overflow return narrow-`offset`/`limit` guidance (don't silently truncate). Pagination hint when a
  large file is read without a narrow window.
- **Async** — `HandleReadFile` is synchronous and `HandleToolCallAsync`'s body is `Task.FromResult`. The
  docx/xlsx readers are `async`. **Make the read path genuinely async** (preferred) rather than blocking with
  `.GetAwaiter().GetResult()`.
- **Structured-doc reuse** — for `.docx`/`.xlsx` call `DroppedFileReader.ReadDocxAsync` (`:82-115`) /
  `ReadXlsxAsync` (`:117-181`) as a pre-pass, then layer numbering/windowing on the returned text.
  **Reconcile caps:** `read_file` = 256 KB; `DroppedFileReader` = 1 MB extracted (`MaxTextBytes` :22, refs
  :69/:105-106/:169/:173) with **8 MB raw-file** caps (`:90`/`:125`, which the milestone's list omits). Decide
  one effective limit. `.ipynb` (JSON cell extraction) is net-new — **defer**.
- **Binary detection** — NUL-byte content-sniff for unknown/extensionless files (do **not** adopt
  `Classify`'s allow-list :24-29 wholesale — it omits `.go/.rs/.java/.tsx/.jsx/.c/.cpp/.h` and would narrow
  acceptance; `read_file` has no extension gate today). Images → "unsupported binary; attach the image
  instead" (no `vision_analyze` tool exists in Pia).
- **mtime record** — after a successful read, `IFileStalenessStore.RecordRead(TaskAmbient.Current ??
  Guid.Empty, resolvedPath, File.GetLastWriteTimeUtc(resolvedPath))`.
- **Path** — via the §0.3 resolver.
- **Logging** — content is **never logged today** (`:183`/`:197` log the *path* via `SensitiveDebug`, not
  payload; byte count via `LogInformation` :196). Any new content logging must go through `SensitiveDebug`.
- **Defer:** per-task read-dedup cache + consecutive-read loop-guard (no context-compression hook in Pia; if
  added later, evict by TTL/turn-count).

### 1.2 New `search_files` tool

- **Backend: hand-rolled in-process, synchronous** (`Directory.EnumerateFiles` +
  `System.Text.RegularExpressions`) with a minimal ignore set (`.git`, `bin`, `obj`, `node_modules`). Stays
  synchronous → drops into the existing `Task.FromResult` switch-expression body cleanly. **`rg` deferred**
  (an `rg.exe` backend is async and would force refactoring `HandleToolCallAsync`; the probe helpers
  `CheckCommandOnPathAsync` :551-572 / `CheckNodeVersionAsync` :520-549 are also **private to
  `PluginService`** and would need duplicating).
- **Wire-up** — add a `search_files` entry to `GetTools` (`:76-89`), a `SearchFilesSchema` beside the existing
  schema methods (`:308-323`), and a new **switch-expression arm** returning `(HandleSearchFiles(root,args),
  null)` before the default (`:116`). Auto-routes via `RegisterHandler` (`:196-207`) / surfaces via
  `GetAllTools` (`:222-243`) — **no `PluginService` edits**. Auto-gated by `IsAvailable` (`:56`) +
  `IsPluginEnabled`.
- **Read-only ⇒ `(result, null)`** — no `FilesToolCall`/approval card.
- **Contract:** `path` (scopes to a subdir under base, §0.3-resolved), `pattern` (regex), output modes
  (content / files-only / count), `offset`+`limit` with truncation hint, multiline-regex warning,
  path-not-found suggestions, diagnostics-vs-results separation. **Re-filter results through the root prefix**
  (defense in depth even in-process).
- **Caps** — add `MaxMatches`/`MaxFilesScanned` constants mirroring `MaxListEntries` (`:25`); honor the
  truncation-message convention.
- **Privacy** — query + matched lines/paths are payload → `SensitiveDebug` only; counts/duration via
  `LogInformation`.

### Phase 1 acceptance
- 5,000-line source file with `offset/limit` → numbered window, not a failure.
- `.docx`/`.xlsx` → numbered extracted text.
- `search_files` over a cloned repo finds matches, excludes `.git/bin/obj/node_modules`, results stay under
  the base.
- Builds clean; `dotnet test` green (read_file tests updated — §6).

---

## 4. Phase 2 — Guarded edit (`write_file`) — full scope

Current: `PrepareWriteFile` (`:207`) → `FilesToolCall` whose `Execute` closure (`:231-256`) re-validates
(`:235`), mkdir (`:240-242`), **non-atomic `File.WriteAllText`** (`:244`, UTF-8 no-BOM, verbatim content) →
bare string. Card detail is `"{content.Length} character(s) will be written."` (`:229`). Two-phase approval
(`ChatSession.cs:396-487`; `WaitForUserDecisionAsync` `ActionCardInfo.cs:64`) reused as-is.

Build (full `write_file.reuse.md`):

**Arg hardening (invariant 11, 10):**
- Add a **missing-vs-present** accessor (alongside `GetStringArg`, which returns `""` for a missing key →
  today a dropped `content` silently writes an empty file). `content` key absent → structured `error`
  ("content missing; re-emit"). Never write.
- `content` present but not a JSON string → type `error` (don't coerce objects/arrays to a file).
- **Internal-content guard** — reject content that is predominantly `N|`-prefixed lines (a `read_file` echo —
  now more likely since §1.1 emits `LINE|CONTENT`) or a dedup-stub. Conservative heuristic (majority of lines
  match `^\d+\|`).

**Sensitive-path blocklist (invariant 7):** after §0.3 resolution, reject Pia's own `%LOCALAPPDATA%\Pia`
config/DB and true system/credential dirs. **Now load-bearing** because §0.3 accepts absolute paths. Keep the
list tight to avoid false positives.

**Atomic write + EOL + BOM (invariants 1, 3, 4):** in the `Execute` closure, replace `File.WriteAllText`:
- Detect existing file's dominant EOL (CRLF vs LF) and leading BOM. **New file → CRLF** (repo is CRLF; LF has
  broken byte-identical raw-string tests — see project memory).
- Normalize content to detected EOL; re-prepend BOM if original had one. (`DroppedFileReader.ReadTextAsync`
  BOM detection at `:64-80` is **decode-only** — it returns a decoded string, not an `Encoding` — so write-back
  preservation is net-new, not free reuse.)
- Write to a temp file **in the same directory** → `FileStream.Flush(true)` → `File.Replace` (preserves ACLs;
  fall back to `File.Move(overwrite)` when no existing target). Delete temp on any error.
- Keep mkdir (`:240-242`) and the `:235` re-validation.

**Structured return (return-shape gap):** `{success, resolved_path, bytes_written, lines, lint, _warning,
error}`. `FilesToolCall.Execute` is `Func<Task<object?>>` so type-compatible — but **verify the tool-loop
serializer handles `object?` returns**, not just strings. Honor `max_result_size_chars = 100_000` on the
serialized result.

**Delta-filtered lint (invariant 6 — headline; owned here, reused by future `patch`):**
- In-process parsers **only** (privacy + minimal-deps; shelling to `tsc`/`py_compile` drags in the code-exec
  model). JSON via `System.Text.Json`. YAML/TOML only if a parser already exists — else `lint: null`.
- Delta filter: parse old content (baseline error set) → parse new → surface **only NEW** errors (so a
  pre-existing broken file isn't blamed on this write).
- Factor as a shared helper (`patch` will reuse it).

**Staleness guard (invariant 8 — net-new):** in `Execute`, `IFileStalenessStore.CheckStaleness`; if changed
since the recorded read, surface a `_warning` (or block). Only sandbox-root re-validation exists today
(`:235`) — there is **no** mtime/content snapshot; this is fresh.

**True line-level diff card (decision):**
- The XAML scaffold (`ActionCardControl.xaml:134-166`) + `ActionCardInfo.OldValueDetails` (`:34`,
  `HasOldValueDetails` `:37`) is a **Label/Value row** list — it does **not** fit a free-form text diff.
  Extend the model (e.g. a `DiffLines` collection with add/remove/context markers) **and** add a XAML
  DataTemplate to render it.
- Hand-roll an LCS line diff (minimal-deps). Compute old→new in `PrepareWriteFile` (read existing file).
- **Thread the preview through TWO records** — add the field to **both** `FilesToolCall`
  (`IFilesToolHandler.cs:5-10`) **and** `PluginToolCall` (`IPluginToolHandler.cs:6-11`); update the mapping at
  `BuiltInPluginHandler.cs:196-197` **and** the parallel `FromPluginHandler` `:172-173`.
- `ActionCardBuilder.Build` (`:24-64`) currently routes files' `Details` through `ParseKeyValueText` (`:49`)
  and never sets `OldValueDetails`. Add a **files-specific branch** (or a dedicated preview field that
  bypasses `ParseKeyValueText`) to populate the diff. `FormatToolTitle` (`:111`) already maps `write_file` →
  `ActionCard_Action_Write`.

**Path / approval:** §0.3 resolver; approval stays **foreground-only** (delegated/background write approval is
a known gap, out of scope).

### Phase 2 acceptance
- Editing a CRLF file + approving keeps CRLF + BOM; git shows a minimal diff.
- Card shows a real old→new line diff, not a char count.
- Crash mid-write cannot corrupt the target (temp+rename).
- Out-of-band modification between read and approval → staleness `_warning`.
- Dropped `content` arg → corrective error, not an empty-file write.
- Writing invalid JSON surfaces a NEW lint error; a pre-existing-broken JSON file does not.

---

## 5. Phase 3 — Registration & prompt gating

- **No new plugin.** The tools stay on `FilesToolHandler`. Real registration is
  `PluginService.InitializeBuiltInPlugins` (`FromFilesHandler` :86, `RegisterHandler` :90); `Bootstrapper:250`
  is DI only.
- **System prompt** — extend the tool enumeration on `BuiltInPluginDefaults.cs:93` (single-line `ConfigJson`;
  currently "Tools: list_files, read_file, write_file, delete_file") to add `search_files` and describe the
  enriched `read_file` (line-numbered/windowed) and `write_file` (diff-approval).
- **Tool-selection tree** — `AssistantPromptComposer.cs:130-147` currently covers only Reminder/Todo/Memory.
  Add a **net-new** files/search branch (it says nothing about file tools today).
- **Do NOT touch any allow-list** — file tools aren't allow-listed; they reach the model via the unfiltered
  no-@-command path (`:49`). Gating is automatic (two-layer `IsPluginEnabled` + `isAvailable`).
- **Action-card label map** — `ActionCardBuilder.cs:109-111` maps tool names → labels; `search_files` is
  read-only (no card) so no entry needed, but verify `write_file`'s label still fits the enriched card.

---

## 6. Testing

- **Update existing `read_file` tests** — line-numbered/windowed output changes the contract; bare-string
  expectations break (expected).
- New unit tests: §0.3 resolver (in-base absolute, out-of-base reject, junction canonicalization,
  non-existent-leaf, `..` escape); §0.2 store record/check; §0.1 ambient isolation + null-`Id` no-NRE; §1.1
  windowing/caps/binary-sniff/docx-xlsx/async; §1.2 regex + ignore-set + pagination + root-prefix filter; §2
  atomic write + BOM/EOL preservation + missing-vs-empty + internal-content guard + sensitive-path blocklist +
  delta-lint (new-error-only) + staleness + diff-preview population.
- Conventions: xunit.v3 + plain `Xunit.Assert` (no FluentAssertions); **new `.cs` files converted to CRLF**.
- Verify by **build + `dotnet test`**, not by driving the app.

---

## 7. Build order (checklist)

1. [ ] **§0.3** path resolver: extract shared containment helper (`SafeFolderPath.cs:31-45`), add
       `TryResolveInsideAllowingAbsolute` with `GetRealPath` canonicalization (+ longest-existing-ancestor for
       new files); canonicalize the configured root in `UpdateFolder`; migrate all four tools + delete the
       list-net (`:161-163`). *(Prereq for safe read/write/delete/search.)*
2. [ ] **§0.2** `IFileStalenessStore` + DI (adjacent `Bootstrapper:250`) + inject; lifecycle tied to
       `IsAvailable`/`SettingsChanged`.
3. [ ] **§0.1** `TaskAmbient` (`Guid?`) + set/restore (`ChatSession.cs:201-202`/`:361`) + wire the reader in
       `FilesToolHandler` + doc-comment (`ChatSessionManager.cs:100`).
4. [ ] **§1.1** `read_file` enrich (numeric arg helper, offset/limit, line numbers, windowing, caps, async
       path, docx/xlsx reuse + cap reconcile, NUL-byte sniff, mtime record). *(`.ipynb` deferred.)*
5. [ ] **§1.2** `search_files` (hand-rolled in-process, sync; caps; SensitiveDebug; root-prefix filter).
6. [ ] **§5** prompt-scaffolding (`BuiltInPluginDefaults.cs:93`, `AssistantPromptComposer.cs:130-147`).
7. [ ] **Ship Phase 1**, gather real usage.
8. [ ] **§2** `write_file`: arg-hardening + internal-content guard → sensitive-path blocklist → atomic +
       BOM/EOL → structured return → delta-lint helper → staleness guard → diff-preview (extend
       `FilesToolCall` **+** `PluginToolCall` + both mappings + `ActionCardBuilder` files branch + model/XAML).
9. [ ] **Ship Phase 2.**

---

## 8. Out of scope (unchanged)

`patch` · `terminal`/`process`/`execute_code` · in-app repo clone/add · per-session cwd ·
output-budgeting subsystem · context-compression + read-dedup-reset · delegated/background write approval ·
`todo`/`clarify`/`delegate_task`/`memory`/`session_search` · `.ipynb` extraction · `rg` backend (deferred,
not cancelled).
