# edit-files — Open Questions

Consolidated open questions, deferred findings, and decisions-to-confirm from the edit-files implementation on `feature/coding_tools` (baseline `f456466` → HEAD `bfb41dc`). Build is green; the only failing tests are 18 pre-existing live-network provider tests (see Cross-cutting). Every code/security review finding was fixed **except the three deferred items** listed under Cross-cutting.

> Line numbers are as of the review snapshot; the 11 review-fixes reshaped `FilesToolHandler.cs`, so some cited lines may have drifted.

**No TODO/FIXME/HACK markers were added by this work.** The only such marker in `src/` (`PluginService.cs:347 // TODO: implement RestApiPluginToolHandler`) predates this milestone (introduced by an unrelated research-view merge).

---

## Resolutions (2026-06-23, reviewed with the user)

Recorded decisions from the post-implementation review. Items not listed remain open.

- **Q1.1-a (read_file size caps) — CONFIRMED.** 1 MB raw-byte input ceiling + 100K-char/2000-line output caps; the docx/xlsx container-vs-extracted (8 MB vs 1 MB) asymmetry is accepted.
- **Q-write-a (write-time lint JSON-only) — CONFIRMED.** YAML/TOML/other formats return `lint: null`; no feedback for them this milestone.
- **QX-a (`FileStalenessStore` naming) — CONFIRMED.** Keep the architecture allow-list extension (`"Store"`); do not rename the class.
- **Q-write-b (diff-card UX) — PENDING the user's visual check.** Fully implemented (color + `+`/`-` gutter); the user will verify rendering in their themes later.
- **Q-write-d / Q0.2-a (staleness) — RESOLVED & IMPLEMENTED.** Kept advisory for the read→preview gap (the approval card already shows the true current-disk-vs-new diff, so the human is the backstop) **and added a narrow blocking guard for the post-approval window**: `write_file` captures the previewed file's mtime at prepare time and, at execute time, *blocks* (returns a re-read-and-retry error, no write) if the file changed — or a file appeared where a create was previewed — between preview and apply. "Unknown key = not-stale" is unchanged (writing without a prior read, e.g. creating a new file, stays legal). See the updated **Q-write-d** below.

> Implemented in a follow-up commit after `d902dec`: `FilesToolHandler.PrepareWriteFile`/`ExecuteWriteAsync` + 3 new tests in `FilesToolHandlerWriteTests.cs`. Build green; 640 passing / 18 pre-existing network failures.

---

## Phase 0.3 — path resolver (`SafeFolderPath`)

### Q0.3-a — `GetRealPath` does not exist on net10.0-windows; replaced with a P/Invoke
- **Question:** Is the hand-rolled `SafeFolderPath.Canonicalize` (`GetFinalPathNameByHandle` via `CreateFileW` + `FILE_FLAG_BACKUP_SEMANTICS`, stripping `\\?\` / `\\?\UNC\`) the accepted canonicalization primitive going forward?
- **Why it matters:** The spec mandated `Path.GetRealPath`, which fails to compile here (CS0117). The substitute is the same OS primitive `GetRealPath` calls on Windows and matches the repo's minimal-deps interop style, but it is a load-bearing security primitive (junction/symlink resolution) now diverging from the written spec.
- **Where:** `src/Pia.Wpf/Infrastructure/SafeFolderPath.cs`
- **Recommended default:** Keep the P/Invoke; update the spec anchor to reflect that `GetRealPath` is unavailable on this target.

### Q0.3-b — `HandleListFiles` re-canonicalizes the root per enumerated entry
- **Question:** Accept the per-entry root re-canonicalization (a fresh `CreateFileW` handle on every iteration, up to ~500 at the `MaxListEntries` cap) as-is, or hoist the canonicalized root once?
- **Why it matters:** Correct but wasteful — up to ~500 handle opens per `list_files` call. Flagged as a non-blocking follow-up.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs` (`HandleListFiles`)
- **Recommended default:** Hoist the canonicalized root once outside the loop in a later cleanup phase; not blocking.

---

## Phase 0.2 — staleness store (`FileStalenessStore`)

### Q0.2-a — "unknown key = not stale" default (CONFIRM)
- **Question:** Confirm `CheckStaleness` returns `false` (not-stale) for an unknown `(taskId, path)` key.
- **Why it matters:** This means a write proceeds without warning if the model never `read_file`'d first — a deliberate hole in the lost-update advisory. It pairs with Q-write-d (advisory-only guard): together they mean lost-update overwrites are always possible.
- **Where:** `src/Pia.Wpf/Services/FileStalenessStore.cs:27-28`
- **Recommended default:** Keep not-stale-on-unknown (documented + tested); revisit only if the guard becomes blocking.

### Q0.2-b — `OrdinalIgnoreCase` path keying
- **Question:** Confirm keying the store with `OrdinalIgnoreCase` (rather than raw ordinal) is desired.
- **Why it matters:** Matches `SafeFolderPath`'s containment comparison and avoids false "unknown" misses from drive-letter/casing differences on Windows; callers pass already-canonicalized paths.
- **Where:** `src/Pia.Wpf/Services/FileStalenessStore.cs`
- **Recommended default:** Keep `OrdinalIgnoreCase`.

> Eviction/lifecycle (§0.2) was a phase deviation but is **now fixed** — `Clear()` is wired to `FilesToolHandler.OnSettingsChanged`. Not open.

---

## Phase 0.1 — `TaskAmbient`

### Q0.1-a — reader wiring deferred to 1.1/2 (resolved, confirm)
- **Question:** Confirm that not wiring a `FilesToolHandler` reader in 0.1 was intended (the reader reads `TaskAmbient.Current ?? Guid.Empty`, landing in 1.1/2).
- **Why it matters:** This overrode `impl.md` build-order item 3, which mentioned reader wiring in 0.1. The explicit task instruction took precedence. The reader is in fact wired in 1.1/2 now, so this is resolved — flagged only so the build-order deviation is on record.
- **Where:** `src/Pia.Wpf/Services/TaskAmbient.cs`, `src/Pia.Wpf/Services/FilesToolHandler.cs`
- **Recommended default:** No action; closed.

---

## Phase 1.1 — `read_file` enrichment

### Q1.1-a — reconciled read size cap (CONFIRM)
- **Question:** Confirm the chosen effective ceilings: 1 MB raw-byte **input** cap for plain text (aligned with `DroppedFileReader.MaxTextBytes`), plus a ~100K-char **formatted-window** cap and a 2000-line cap as separate output caps.
- **Why it matters:** The spec asked to pick ONE effective limit and note it. There is an asymmetry to ratify: a `.docx`/`.xlsx` **container** on disk may be up to 8 MB raw (DroppedFileReader's ×8 cap), yet its **extracted** text is still capped at 1 MB.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs` (`HandleReadFileAsync`)
- **Recommended default:** Keep the 1 MB input ceiling + 100K-char/2000-line output caps; document the docx/xlsx container-vs-extracted asymmetry.

### Q1.1-b — UTF-16 files rejected as binary
- **Question:** Accept that real UTF-16 source files are rejected by the NUL-byte binary sniff (UTF-16 ASCII is ~50% NUL), making the UTF-16 BOM decode branches effectively unreachable for such files?
- **Why it matters:** Correctness limitation for UTF-16 source (rare in a coding sandbox). Could be refined to BOM-check before the NUL sniff.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs` (`LooksBinary` / `DecodeText`)
- **Recommended default:** Accept as a documented limitation; refine only if UTF-16 source appears in practice.

### Q1.1-c — load-then-decode vs streaming
- **Question:** Keep `File.ReadAllBytesAsync` (load then decode, bounded by the 1 MB ceiling) rather than streaming line-by-line?
- **Why it matters:** Memory is safe under the 1 MB cap; true streaming was the advisor's optional more-code alternative.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs`
- **Recommended default:** Keep load-then-decode.

---

## Phase 1.2 — `search_files`

### Q1.2-a — output line format
- **Question:** Ratify the per-line layout: content `rel:line:text`, files `rel`, count `rel: N`, each under a `matches=N` header.
- **Why it matters:** The impl doc fixed the modes and truncation-hint wording but not the exact per-line layout; a grep-like `path:line:text` convention was chosen.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs` (`HandleSearchFiles`)
- **Recommended default:** Keep grep-style `rel:line:text`.

### Q1.2-b — search default `limit` = 100
- **Question:** Confirm `search_files` default limit is 100 (clamped to `MaxMatches=500`), distinct from `read_file`'s 500 default.
- **Why it matters:** The doc does not fix a search default; match windows are typically smaller than file windows.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs`
- **Recommended default:** Keep 100.

> The medium scoped-search-relative-path bug, the missing per-file size cap, and the read-side blocklist were review findings and are **now fixed**. Not open.

---

## §5 — registration & prompt gating

### Q5-a — step-4 decision-tree text has no unit test (lint/prompt coverage)
- **Question:** Accept that the new tool-selection step-4 branch in `AssistantPromptComposer.BuildSystemPrompt` is verified by build + manual review only, with no dedicated unit test?
- **Why it matters:** The text lives in a private instance method; existing tests only cover public statics. Adding a test would require widening visibility. The public `ConfigJson` enumeration IS asserted.
- **Where:** `src/Pia.Wpf/Services/AssistantPromptComposer.cs` (~:130-149)
- **Recommended default:** Accept build+manual verification; do not widen visibility just to test prompt text.

---

## write_file (full scope)

### Q-write-a — lint coverage is JSON-only (CONFIRM)
- **Question:** Confirm `WriteLintHelper` lints only JSON (via `System.Text.Json`) and returns `null` for YAML/TOML/other, surfacing only NEW errors (delta vs old baseline).
- **Why it matters:** Sets user expectations — no lint feedback for YAML/TOML/etc. on write. Reusable by future patch work.
- **Where:** `src/Pia.Wpf/Infrastructure/WriteLintHelper.cs`
- **Recommended default:** Keep JSON-only for this milestone; expand formats later if desired.

### Q-write-b — diff-card UX (CONFIRM)
- **Question:** Confirm the diff-card UX: color-coded add/remove via a hand-rolled LCS `LineDiff`, gated on `HasDiff`, with a `+`/`-`/space gutter (`Display` property) so the distinction survives loss of color.
- **Why it matters:** This is the user's approval surface — they judge exactly what changes. The gutter was added per the accessibility nit; confirm the overall card (colors, detokenization, no key/value parsing) reads correctly.
- **Where:** `src/Pia.Wpf/Controls/ActionCardControl.xaml`, `src/Pia.Wpf/Models/ActionCardInfo.cs`, `src/Pia.Wpf/Services/ActionCardBuilder.cs`
- **Recommended default:** Keep as built; confirm colors render in dark/high-contrast themes.

### Q-write-c — structured return shape (8th `created` field; write vs delete asymmetry)
- **Question:** Accept the structured `WriteResult` return carrying an 8th `created` (bool) field beyond the spec's 7, and that `write_file` returns a structured object (incl. errors as `WriteResult.Failed`) while `delete_file` still returns bare strings?
- **Why it matters:** The model serializes the object as the tool result; `created` is a harmless extra signal. The write/delete contract asymmetry is intentional but worth ratifying.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs` (`WriteResult`)
- **Recommended default:** Keep `created` and the structured-write/bare-delete split.

### Q-write-d — staleness guard — RESOLVED & IMPLEMENTED (advisory + post-approval block)
- **Decision:** Two-tier guard. (1) **Advisory** for the read→preview gap — the approval card's "old" side is read fresh at prepare time, so the human reviewing the diff already sees any out-of-band change; a non-blocking `_warning` is secondary signal for the model. (2) **Blocking** for the post-approval window — `PrepareWriteFile` captures the previewed file's mtime; `ExecuteWriteAsync` re-samples at apply time and returns a re-read-and-retry error (no write) if the file's mtime changed, or if a file now exists where a *create* was previewed. This closes the one real silent-clobber hole: the user approved a specific diff, and the file is no longer in the state that diff was built from.
- **Why this shape:** Foreground, human-approved, single-user tool (background/delegated writes are out of scope). Hard-blocking the read→preview gap would add friction to a case the human already sees on the card; the post-approval gap is the genuinely unsafe one. "Unknown key = not-stale" (Q0.2-a) is retained so writing without a prior read (new files, model-known content) stays legal.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs` (`PrepareWriteFile` preview-mtime capture; `ExecuteWriteAsync` post-approval block). Tests: `FilesToolHandlerWriteTests.Write_FileChangedSincePreview_IsBlocked_NoClobber`, `Write_CreateBecameOverwrite_IsBlocked_NoClobber`, `Write_UnchangedSincePreview_Succeeds`.
- **Status:** Done; build green, new tests pass. No longer open.

### Q-write-e — `LooksLikeReadFileEcho` false-positive on numeric pipe-delimited data — DEFERRED (nit)
- **Question:** Accept that the internal-content guard can misclassify legitimate 3+-line numeric pipe-delimited data (e.g. `12|widget` / `13|gadget`) as a read_file echo and block the write?
- **Why it matters:** Real corner case for tabular/lookup data files. The heuristic is spec-faithful (§4 "conservative") with a 3-line floor + majority threshold.
- **Where:** `src/Pia.Wpf/Services/FilesToolHandler.cs:806`
- **Recommended default:** Accept as documented limitation; if precision matters, also require the `total_lines=` header or strictly sequential 1-based numbering before rejecting.

---

## Cross-cutting

### QX-a — `FileStalenessStore` naming: allowlist vs rename (DECISION TO CONFIRM)
- **Question:** Confirm extending the architecture naming allow-list with `"Store"` (done) is preferred over renaming the class (e.g. `FileStalenessTracker`/`...Service`).
- **Why it matters:** `NamingConventionTests.ServiceClasses_MustFollowNamingConvention` failed because the class ends in `Store`. The allow-list was extended to clear it (the test is now green). Renaming would ripple through §0.2 DI/tests.
- **Where:** `tests/Pia.Wpf.Tests/Architecture/NamingConventionTests.cs`, `src/Pia.Wpf/Services/FileStalenessStore.cs`
- **Recommended default:** Keep the allow-list extension; do not rename.

### QX-b — blocklist roots: raw env vars vs `Environment.GetFolderPath` — DEFERRED (security low)
- **Question:** Should `SensitivePathGuard` build blocked roots from `Environment.GetFolderPath(SpecialFolder.*)` instead of raw env vars (`LOCALAPPDATA`/`APPDATA`/`WINDIR`)?
- **Why it matters:** If an env var is unset or diverges from what the app actually uses, that root silently drops from the blocklist. The load-bearing canonicalization-asymmetry part of this finding was fixed; this source-consistency sub-point was deferred to avoid changing which roots are blocked outside this milestone.
- **Where:** `src/Pia.Wpf/Infrastructure/SensitivePathGuard.cs:81-85`
- **Recommended default:** Defer; swap the source in a focused follow-up that re-verifies blocklist coverage.

### QX-c — outstanding failing tests (pre-existing, environmental)
- **Status:** 18 failing tests, all in `Pia.Wpf.Tests.Integration.Providers` (`OpenRouterProviderHandlerTests`, `VLlmProviderHandlerTests`) — live-network calls to LLM endpoints unreachable in this environment (`LlmTimeoutException` / connection refused). **Zero coding-tools tests fail.** Post-fix suite: 668 total, 637 passed, 18 failed, 13 skipped.
- **Recommended default:** Treat as environmental; no action in this milestone.

---

## Status after the 2026-06-23 review

Resolved with the user: **Q-write-d / Q0.2-a** (staleness → advisory + post-approval block, implemented), **Q1.1-a** (read caps confirmed), **Q-write-a** (lint JSON-only confirmed), **QX-a** (keep `Store` allow-list entry). See **Resolutions** at the top.

**Still pending:**
1. **Q-write-b** — the diff-card approval UX (colors + `+`/`-` gutter): the user will visually verify rendering in their themes.

**Lower-priority / deferred (acknowledged, no action this milestone):** Q0.3-b (per-entry root re-canonicalization in `list_files`), Q1.1-b (UTF-16 rejected by NUL sniff), Q-write-e (echo-guard false-positive on numeric pipe data), QX-b (blocklist roots from env vars vs `GetFolderPath`). Plus the cross-cutting milestone deferrals (patch, terminal/exec, in-app clone, read-dedup, `.ipynb`, `rg`).
