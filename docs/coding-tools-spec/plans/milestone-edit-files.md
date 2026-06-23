# Milestone Plan — "Edit Files" (read_file + search_files + write_file)

**Status:** plan, not code. **Date:** 2026-06-23. **Scope:** the first shippable coding-capability subset for Pia.Wpf.

This is a cross-tool *milestone* plan that supersedes the per-tool planning docs for the three tools it covers, by folding in the decisions taken when the open questions were discussed (see `README.md` → "Open Questions to Discuss"). The per-tool docs (`read_file.reuse.md`, `search_files.plan.md`, `write_file.reuse.md`) remain the detailed background; this doc is the executable sequence.

> **Citation caveat.** `file:line` anchors below were captured from a fresh scoping pass of the current tree on 2026-06-23. Treat them as starting points; verify against the live code before editing (line numbers drift).

---

## 1. Milestone scope & the decisions behind it

**In scope:** `read_file`, `search_files`, `write_file` — enough for Pia to find, read, and edit code under guarded approval.

**Explicitly deferred:** `patch`, `terminal`, `process`, `execute_code`, `todo`, `clarify`, `delegate_task`, `memory`, `session_search`. Whole-file `write_file` covers editing without the 9-strategy fuzzy `patch` engine; everything execution-related is the apex-risk tier and waits.

> This deliberately departs from `README.md`'s "patch at #3, hardest-first" build order in favor of **value-first / risk-last**: ship comprehension + guarded edit before investing in the hardest (patch) and riskiest (exec) tools.

### Resolved open questions

| README open Q | Resolution for this milestone |
|---|---|
| **#2 Filesystem scope (sandbox vs workspace root)** | **Managed-workspace model.** Keep the existing base `AppSettings.AssistantFilesFolder` (`%LOCALAPPDATA%\Pia\workdir`). Every repo Pia handles is cloned **below** that base; Pia edits the copy. No separate workspace root reaching arbitrary disk → the existing `SafeFolderPath` containment guarantee is **preserved**. Open Q #2 dissolves into a config reuse + one contained relaxation (see Phase 0.3). |
| **#3 Extend vs rebuild `FilesToolHandler`** | **Extend in place.** One base ⇒ one file-tool surface ⇒ no name collision and no parallel-plugin gating dance. Cost: enriching `read_file` changes its output contract for *all* reads (line numbers / structured), so existing tests + prompt guidance must be updated deliberately. |
| **#1 task_id threading** | **`AsyncLocal` ambient**, mirroring `TokenMapAmbient`. New `TaskAmbient` set in `ChatSession.RunTurnAsync` from `ChatSession.Id`, restored in `finally`. **Not** a handler-signature change (the signature is owned by `Microsoft.Extensions.AI`; tools are built once in `GetTools()`). |
| **#5 Code-execution consent** | N/A for this milestone (no exec). `write_file` reuses the **existing two-phase action-card approval** unchanged, foreground-only. Background/delegated approval is a known gap (not delegatable) and is out of scope here. |
| **#7 Output budgeting** | Not built. `read_file` windowing + a `search_files` result cap are sufficient *local* caps. The per-turn spill subsystem waits for `terminal`/`execute_code`. |
| **#8 Cross-tool mtime store** | Built minimally: a DI singleton keyed by `(task_id, resolved_path)` recording mtime-at-read for `write_file` staleness. |
| **#9 Context compression** | Confirmed absent in Pia. Don't block on it: give the read-dedup cache **TTL / turn-count eviction** instead of a "reset after summarize" hook. (Dedup itself is optional for v1 — see Phase 1.) |

### Workspace usage model (consequences to honor)

- **"Copy in" = `git clone` in** (or copy *with* the `.git` folder). Edits land on the copy under `workdir`; the git remote is the path back (commit/push). A flat copy without `.git` is an editing dead-end.
- **In-app "add repo / clone" helper is deferred.** Repos are placed under `workdir` manually for now; a first-class clone action belongs with the later `terminal`/git work.
- **No per-session active-repo cwd yet.** The agent navigates the whole base via `search_files`/`list_files` and addresses files repo-relative (`my-repo/src/Foo.cs`) or by in-base absolute path. A cwd only earns its keep with `terminal`.

---

## 2. Phase 0 — Foundation (no user-facing tools)

Small, but underpins everything later. Build and test before any tool work.

### 0.1 `TaskAmbient` (task_id flow)

- New `src/Pia.Wpf/Services/TaskAmbient.cs`, a static `AsyncLocal<Guid?>`-backed class with a `Current` property — a direct sibling of `TokenMapAmbient` (`src/Pia.Wpf/Services/TokenMapAmbient.cs:18-28`).
- Set it in `ChatSession.RunTurnAsync` immediately after the `TokenMapAmbient` set (`src/Pia.Wpf/ViewModels/Models/ChatSession.cs:201-202`), value = `this.Id` (`ChatSession.cs:46`). Restore the previous value in the same `finally` as `TokenMapAmbient` (`ChatSession.cs:361`).
- **Why this set-site, not the handler signature or `PrepareTurn`:** `IPluginToolHandler.HandleToolCallAsync` (`src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs:19-20`) is invoked by MS.Extensions.AI machinery and tools are built once in `GetTools()`, so task_id can't be a parameter. `PrepareTurn` (`IAssistantPromptComposer.cs:29-33`) doesn't consume task_id, so setting earlier buys nothing. `RunTurnAsync` is dispatched fire-and-forget (`ChatSessionManager.cs:435`) and owns the only clean set/restore scope. `ChatSession.Id` is assigned synchronously before dispatch (`ChatSessionManager.cs:324-329`).
- **Acceptance:** a tool handler reading `TaskAmbient.Current` during a turn sees that session's `Id`; interleaved background turns don't bleed (mirror the isolation comment at `ChatSession.cs:198-200`).

### 0.2 File-staleness store (open Q #8)

- New `IFileStalenessStore` + singleton impl. API: `RecordRead(Guid taskId, string resolvedPath, DateTime mtimeUtc)` and `CheckStaleness(Guid taskId, string resolvedPath, DateTime currentMtimeUtc) → bool stale`.
- Keyed by `(task_id, resolved_path)` — the **resolved** path from `SafeFolderPath`, never the model-supplied relative string.
- DI-registered as a singleton (alongside `IFilesToolHandler` at `src/Pia.Wpf/Bootstrapper.cs:250`); injected into `FilesToolHandler`.
- **Acceptance:** read records mtime; an unchanged-mtime write sees `stale=false`; an out-of-band file touch between read and write sees `stale=true`.

### 0.3 `SafeFolderPath` — contained relaxation + canonicalization

- Today `TryResolveInside` rejects **all** rooted paths unconditionally (`src/Pia.Wpf/Infrastructure/SafeFolderPath.cs:27`) and enforces containment lexically (`:35-44`, root guard `:45`).
- Add a **second, clearly-named** entry point (e.g. `TryResolveInsideAllowingAbsolute`) that:
  1. accepts rooted/absolute **and** relative inputs,
  2. normalizes (`Path.GetFullPath`), then **canonicalizes via `Path.GetRealPath`** to resolve junctions/symlinks,
  3. runs the **same** containment check (extracted into a shared private helper) against the base.
- **Do not** add a mode-flag to the existing `TryResolveInside` — keep `:27`'s unconditional rejection isolated and auditable. The new method shares the *containment* logic but never the sandbox's reject-all-rooted contract.
- **Why canonicalization is now critical:** the whole safety story rests on "everything stays under the base." A cloned repo can contain a junction/symlink pointing outside; the existing "safety net" at `FilesToolHandler.cs:161-163` is lexical-only and ineffective against junctions. `Path.GetRealPath` before containment closes that.
- **Acceptance:** in-base absolute path resolves; out-of-base absolute path rejected; a junction inside the base pointing outside is rejected after canonicalization; relative traversal (`..`) escaping the base is rejected.

---

## 3. Phase 1 — Read-only (ship for value, zero write risk)

### 1.1 Enrich `read_file` on `FilesToolHandler`

Current state: `HandleReadFile` (`FilesToolHandler.cs:177-205`) returns a bare `File.ReadAllText` string (`:195,198`), no line numbers, no windowing, and **hard-fails** files over 256 KB (`MaxReadBytes`, `:23`; check + error at `:192-193`). Schema is path-only (`ReadFileSchema`, `:312-314`).

Build:
- **Line numbering** — emit `LINE|CONTENT` (1-indexed). This changes model-visible output for *every* read (see Testing).
- **Windowing** — add `offset` (1-indexed, default 1) and `limit` (default 500, max 2000) to `ReadFileSchema`; slice `[offset, offset+limit)`. Extend `GetStringArg`/`GetOptionalStringArg` (`:325-353`) for integer args.
- **Replace the 256 KB hard-fail** with a ~100K-char output cap on the formatted window + a 2000-line cap; on overflow, return guidance to narrow `offset`/`limit` rather than failing. Add a pagination hint for large files read without a narrow window.
- **Structured-doc reuse** — for `.docx`/`.xlsx`, call `DroppedFileReader.ReadDocxAsync`/`ReadXlsxAsync` (`src/Pia.Wpf/Helpers/DroppedFileReader.cs:82-115,117-181`) as a pre-pass, then layer numbering/windowing on the returned text. **Reconcile the two cap regimes** (DroppedFileReader's 1 MB `TooLarge` at `:22,69,105-106,169,173` vs. the new output cap). `.ipynb` (JSON cell extraction) is genuinely net-new and may be deferred.
- **Binary detection** — `DroppedFileReader.Classify` (`:41-53`) is precise for docx/xlsx/image/audio/pdf but its `TextExtensions` blocklist (`:24-29`) omits `.go/.rs/.java/.tsx…` and treats extensionless files (`Dockerfile`/`Makefile`) as Unsupported (`:44`). Add an **orthogonal NUL-byte content-sniff** for unknown/extensionless files. Images → return "unsupported binary; attach the image instead" (no `vision_analyze` tool exists in Pia).
- **Record mtime** after a successful read via `IFileStalenessStore.RecordRead(TaskAmbient.Current ?? Guid.Empty, resolvedPath, File.GetLastWriteTimeUtc(resolvedPath))`.
- **Path resolution** uses the Phase 0.3 `…AllowingAbsolute` resolver.
- **Defer for v1:** the per-task read-dedup cache and consecutive-read loop-guard. If added, evict by TTL/turn-count (no context-compression hook exists — open Q #9).

### 1.2 New `search_files` tool

- Add to `FilesToolHandler` (same base, same handler): `SearchFilesSchema` method, an `AIFunctionFactory.Create` entry in `GetTools()` (pattern at `FilesToolHandler.cs:72-89`), and a `search_files` case in the dispatch switch (`:92-119`). Routing is automatic via `PluginService` auto-indexing (`PluginService.cs:196-207`). **Read-only ⇒ return `(result, null)`, no action card.**
- **Backend: fallback-first, hand-rolled.** Primary = pure .NET (`Directory.EnumerateFiles` + `System.Text.RegularExpressions`) with a **minimal ignore set** (`.git`, `bin`, `obj`, `node_modules`). Optionally shell out to `rg.exe` *if present on PATH* (reuse the subprocess/probe pattern at `PluginService.cs:551-572`) for `.gitignore` fidelity + speed. No bundled binary, honoring the minimal-dependency preference; bundle `rg` later only if gitignore fidelity becomes load-bearing.
- **Contract:** `path` (scopes search to a repo subdir under base, resolved via Phase 0.3), `pattern` (regex), output modes (content / files-only / count), `offset`+`limit` pagination with a truncation hint, multiline-regex warning, path-not-found suggestions, diagnostics-vs-results separation.
- **Privacy:** matched lines and paths are file-content payload → log only via `SensitiveDebug` (pattern at `FilesToolHandler.cs:183,197`).

### Phase 1 acceptance

- Read a 5,000-line source file with `offset/limit` and get a numbered window, not a failure.
- Read a `.docx`/`.xlsx` and get numbered extracted text.
- `search_files` over a cloned repo finds matches and excludes `.git/bin/obj/node_modules`.
- Builds clean; `dotnet test` green (with read_file tests updated — see Testing).

---

## 4. Phase 2 — Guarded edit (`write_file`)

Current state: `PrepareWriteFile` + `Execute` (`FilesToolHandler.cs:207-256`) does a **non-atomic** `File.WriteAllText` (`:244`), drops BOM, doesn't preserve line endings, and the approval card shows only "`{content.Length} character(s) will be written.`" (`:229`). Two-phase approval flow is solid and reused as-is (`ChatSession.cs:396-487`; `WaitForUserDecisionAsync` at `ActionCardInfo.cs:55-64`).

Build:
- **Atomic write** — write to a temp file in the target directory, then `File.Move(temp, final, overwrite: true)`.
- **Encoding/BOM + line-ending preservation** — before overwrite, detect the existing file's encoding/BOM (reuse `DroppedFileReader.ReadTextAsync`'s BOM detection, `DroppedFileReader.cs:64-80`) and CRLF-vs-LF style; write back with the same encoding and normalize new content to the original EOL. Directly relevant to this repo (CRLF) — preservation prevents whole-file phantom diffs in git.
- **Diff-preview approval card (near-free)** — `ActionCardInfo.OldValueDetails` + the side-by-side XAML are **already wired but never fed** (`ActionCardInfo.cs:34,37`; `ActionCardControl.xaml:140-165`). In `PrepareWriteFile`, read the existing file and compute an old→new preview; add a field to `FilesToolCall` (`IFilesToolHandler.cs:5-10`), thread it through `BuiltInPluginHandler.FromFilesHandler` (`BuiltInPluginHandler.cs:185-202`), and populate `card.OldValueDetails` in `ActionCardBuilder.Build` (`ActionCardBuilder.cs:24-63`).
- **Staleness guard** — in `Execute`, call `IFileStalenessStore.CheckStaleness`; if the file changed since the recorded read, surface a warning in the result (or block) rather than silently overwriting.
- **Path resolution** uses the Phase 0.3 resolver. Approval stays **foreground-only**; do not attempt delegated/background write approval (known gap).

### Phase 2 acceptance

- Editing a CRLF file and approving keeps CRLF + BOM; git shows a minimal diff.
- The approval card shows an old→new preview, not just a char count.
- A crash mid-write cannot corrupt the target (temp+rename).
- Out-of-band modification between read and approval triggers the staleness warning.

---

## 5. Registration & prompt gating

- No new plugin: the three tools live on the existing `FilesToolHandler`, registered once (`Bootstrapper.cs:250`), surfaced via `PluginService.GetAllTools` (`PluginService.cs:222-243`), gated by the handler's `IsAvailable` (`FilesToolHandler.cs:56`; factory `BuiltInPluginHandler.cs:185-202,201`).
- **Update prompt scaffolding** so the model gets correct guidance for the enriched/added tools: the hardcoded tool-selection text (`AssistantPromptComposer.cs:130-147`) and the file-plugin system-prompt addition (`BuiltInPluginDefaults.cs:93-94`). Tool-name filtering is allow-list only (`AssistantPromptComposer.cs:42-50`) — `search_files` must be added wherever the file tools are allow-listed.

---

## 6. Testing

- **Update existing `read_file` tests** — enriched output (line numbers + structured/windowed) changes the contract; bare-string expectations will break. This is expected, not a regression.
- New unit tests: `SafeFolderPath` relaxation + junction canonicalization (0.3); `IFileStalenessStore` record/check (0.2); `read_file` windowing + caps + binary sniff (1.1); `search_files` regex + ignore-set + pagination (1.2); `write_file` atomicity + BOM/EOL preservation + diff-card population + staleness (2).
- Honor repo conventions: xunit.v3 + plain `Xunit.Assert` (no FluentAssertions); new `.cs` files converted to CRLF.
- Verify by **build + `dotnet test`**, not by driving the app.

---

## 7. Build order (checklist)

1. [ ] 0.1 `TaskAmbient` + set/restore in `RunTurnAsync`
2. [ ] 0.2 `IFileStalenessStore` + DI + inject
3. [ ] 0.3 `SafeFolderPath` relaxation + `Path.GetRealPath` canonicalization
4. [ ] 1.1 `read_file` enrich (line numbers, windowing, caps, docx/xlsx reuse, binary sniff, mtime record)
5. [ ] 1.2 `search_files` (hand-rolled + ignore set; optional `rg`)
6. [ ] Prompt-scaffolding + allow-list updates (§5)
7. [ ] **Ship Phase 1**, gather real usage
8. [ ] 2 `write_file` enrich (atomic, BOM/EOL, diff card, staleness)
9. [ ] **Ship Phase 2**

---

## 8. Out of scope (and why)

`patch` (hardest; whole-file write suffices for now) · `terminal`/`process`/`execute_code` (apex exec risk; consent model + runtime decisions deferred) · in-app repo clone/add (manual for now) · per-session cwd (needs `terminal`) · output-budgeting subsystem (#7) · context-compression + read-dedup-reset (#9) · delegated/background write approval (not delegatable today) · `todo`/`clarify`/`delegate_task`/`memory`/`session_search`.
