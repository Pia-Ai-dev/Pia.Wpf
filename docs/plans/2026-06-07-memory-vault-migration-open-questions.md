# Memory Vault Migration — Open Questions & Handoff (WPF track)

- **Date:** 2026-06-07
- **Branch:** `feature/memory_update` (off `feature/personas`)
- **Status:** WPF track of the memory-vault migration is **implemented and compile-verified**; this doc lists everything deferred / undecided so a fresh session can pick up without prior context.
- **Source plans:** [`2026-06-07-memory-vault-migration-design.md`](2026-06-07-memory-vault-migration-design.md), [`2026-06-07-memory-vault-migration-implementation.md`](2026-06-07-memory-vault-migration-implementation.md). **Format contract:** [`../specs/memory-vault-format-v1.md`](../specs/memory-vault-format-v1.md).

---

## 0. Orientation (read first if you're a new session)

**What was built.** A files-first markdown "vault" replaces opaque JSON-in-SQLite for assistant memory. The markdown vault (`%LOCALAPPDATA%\Pia\Vault`) is the source of truth; the SQLite DB demotes to a rebuildable per-section index. The model-facing tool surface is now exactly **`recall` / `remember` / `forget`** with dedup enforced in code. A Karpathy-style wiki layer (`sources/` raw → `topics/` compiled → `index.md`/`log.md`/`AGENTS.md`) plus Ingest and Lint were added. Sync gained a section-aware 3-way merge engine.

**Phases done (Phase 0→1→2→3→4→6→7→5-WPF).** ~33 commits on `feature/memory_update`, from `a7f40ba` (spec) through `7b827e4`. One commit per task; each phase had an adversarial verify + code-review stage and review-driven fixes.

**Component map (all inside the single `Pia.Wpf` project — `Pia.Models`/`Pia.Infrastructure`/`Pia.Services` are folders/namespaces, NOT separate projects):**

| Concern | Type(s) | Location / namespace |
|---|---|---|
| Doc model | `VaultDocument`, `VaultSection` | `src/Pia.Wpf/Models/Vault/` · `Pia.Models.Vault` |
| Parser / slug | `MarkdownVaultParser`, `VaultSlug` | `src/Pia.Wpf/Infrastructure/Vault/` · `Pia.Infrastructure.Vault` |
| Atomic store | `IVaultStore`, `VaultStore`, `VaultPathProvider` | `src/Pia.Wpf/Infrastructure/Vault/` |
| Index | `Chunks`/`ChunksFts` schema in `SqliteContext`; `IVaultIndexer`/`VaultIndexer`, `VaultWatcher` | `src/Pia.Wpf/Infrastructure/SqliteContext.cs`; `src/Pia.Wpf/Services/` · `Pia.Services` |
| Recall | `MemoryService.RecallAsync`, `RecallHit` | `src/Pia.Wpf/Services/` · `Pia.Services.Interfaces` |
| Write path | `ISectionUpsertService`/`SectionUpsertService`, `MemoryService.RememberAsync`/`ResolveRememberAsync`/`ForgetAsync`, `MemoryToolHandler` (recall/remember/forget) | `src/Pia.Wpf/Services/` |
| Migration | `MemoryJsonRenderer`, `IVaultMigrationRunner`/`VaultMigrationRunner`; `AppSettings.VaultVersion` marker | `src/Pia.Wpf/Services/Migration/` · `Pia.Services.Migration` |
| Wiki | `VaultIndexService`, `VaultLogService`, `VaultSchemaService` | `src/Pia.Wpf/Services/Wiki/` · `Pia.Services.Wiki` |
| Ingest/Lint | `IIngestExtractor`/`AiIngestExtractionService`, `IIngestService`/`IngestService`, `IIngestToolHandler`/`IngestToolHandler`, `ILintService`/`LintService` | `src/Pia.Wpf/Services/Wiki/` + `src/Pia.Wpf/Services/` |
| Sync merge | `SyncMemory.Path`, `SectionMergeEngine`+`MergeResult`, `SyncBaseStore`, `IVaultSyncService`/`VaultSyncService`, `SyncMapper.ToVaultSyncMemory`/`FromVaultSyncMemory`, `VaultSyncPayload` | `src/Pia.Shared/Models/`, `src/Pia.Wpf/Services/Sync/`, `src/Pia.Wpf/Infrastructure/Sync/` |

**BUILD/TEST REALITY (critical).** `dotnet 10.0.300` IS installed on the dev Mac, but `Pia.Wpf` + tests target `net10.0-windows` (WPF).
- **Compile-check works on macOS:** `dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -p:EnableWindowsTargeting=true` → 0 errors achievable. (Without the flag: `NETSDK1100`.)
- **Tests cannot execute on macOS:** `dotnet test` → exit 150 / "No frameworks were found" (`Microsoft.WindowsDesktop.App` has no osx-arm64 runtime). **All runtime test pass/fail is UNVERIFIED and deferred to a Windows/CI runner.** Everything below labeled "compile-verified" means 0 build errors, not a green test run.
- Baseline test-build warning count is **66** pre-existing (xUnit1051 / a few CS8604) in unrelated files; new code is warning-clean against that baseline.

**Cross-repo.** The spec is **byte-identical across `Pia.Wpf`, `Pia` (server), `Pia.Mac`** (sha `415f3b9`). The **Mac and Server tracks are being worked in parallel** in the sibling repos (`../Pia`, `../Pia.Mac`) — do not touch them; only the shared spec is synced.

---

## 1. Scope decisions to confirm — the "cut-over trio" (highest priority)

These three were deliberately deferred because doing them mid-migration would break live behavior. They are the natural next effort and are interrelated.

### Q1 — Retire the legacy `Memories` table + JSON CRUD (plan Task 4.3, NOT done)
- **Current state:** `Memories`/`MemoriesFts` tables and `MemoryService.CreateObjectAsync`/`UpdateObjectAsync`/`UpdateObjectDataAsync`/`AppendToListAsync`/`DeleteObjectAsync` are **still present and live**.
- **Why deferred:** still consumed by `MemoryViewModel`, `AccountSettingsViewModel`, `FirstRunWizardViewModel`, `SyncClientService`, `TokenMapService`, `AutocompleteService` (none migrated to the vault). The plan's own guardrail gates the drop on cross-platform validation (a Windows/CI green run), which has not happened.
- **To resolve:** migrate those UI/sync callers to the vault `recall`/`remember`/`forget` + `IVaultStore` path, validate on Windows/CI, then drop the tables and remove the CRUD methods (and `MemoryObject` JSON paths). `VaultMigrationRunner` already converts existing rows + archives originals to `memory/.archive/`.
- **Decision needed:** approve a follow-up effort to migrate the 6 callers and drop the table?

### Q2 — Live sync cut-over to vault files (plan Task 5.3, partial)
- **Current state:** `SyncClientService` still syncs `MemoryObject` rows via the existing `SyncMapper.ToSyncMemory`/`FromSyncMemory` (`{Type,Label,Data}`). The **new** vault-file path exists and is tested but is **not wired into the live pull loop**: `SectionMergeEngine`, `SyncBaseStore` (per-file base snapshots under `%LOCALAPPDATA%\Pia\SyncBase\<id>.md`), `VaultSyncService.ReconcileOnPullAsync`, and `SyncMapper.ToVaultSyncMemory`/`FromVaultSyncMemory` (`{path,content}` envelope, E2EE-on encrypts + leaves `Path` null per C5, E2EE-off sets `Path`+`Data`).
- **Why deferred:** repointing the live loop would break `MemoryObject` sync before the vault is the active unit (couples to Q1).
- **To resolve:** after Q1, repoint `SyncClientService.PullChangesAsync`/push to enumerate vault files, key by frontmatter `id`, run `VaultSyncService.ReconcileOnPullAsync` on pull, advance base snapshots. The server side (`ServerMemory.Path`, EF migration) is already done in the `../Pia` repo (`feat(sync): path-aware memory sync payload`).
- **Note:** `SyncMemory.Path` was added (additive, backward-compatible) in this repo.

### Q3 — Surface the `ingest` tool to the model (plan Task 7.1, partial)
- **Current state:** `IngestToolHandler` is implemented + DI-registered, but **not surfaced to the model**. The assistant aggregates tools via `PluginService`/`BuiltInPluginHandler` (a `SyncPlugin`-keyed config switch + `From*Handler` factory in `BuiltInPluginDefaults`), not directly in `AssistantViewModel`.
- **To resolve:** add a `SyncPlugin` config entry + a `FromIngestHandler` factory so `ingest(source_ref)` reaches the model. Also decide background-job vs inline (see Q5).
- **Decision needed:** surface it now, or keep ingest internal/triggered another way?

---

## 2. Deferred features

### Q4 — Lint scheduling
`LintService.RunAsync(DateOnly, ct)` is **on-demand only**. The plan's "run after N ingests / on a schedule" trigger is not wired. Existing scheduled-job infra (`IScheduledJobService`, `ScheduledJobBackgroundService`) could host it. **Decide trigger policy and wire it.**

### Q5 — Background-job + progress UI for ingest
`IngestService.IngestAsync` runs **inline/synchronously**. The plan wants a long-running background job with a progress surface (like other background jobs). Deferred. **Decide whether ingest needs the job-handle + progress UI treatment.**

### Q6 — Model-assisted prose rewrite & ambiguous-cluster merge
- `remember` does deterministic `- key: value` **bullet** field-merge; arbitrary prose `content` is **appended**, not restructured into bullets (spec §4's "hand the section to the model to rewrite" is deferred — needs a model call, not unit-testable without an API key).
- Migration auto-merges only **confident (≥0.85)** duplicates; ambiguous-band rows **force-create** (lossless) rather than doing the design's "embedding-clustering + LLM-assisted merge per cluster" (also needs a model). **Decide if/when to add the model-assisted paths.**

### Q7 — Binary source ingestion
`IngestService` only reads text sources (`.txt/.md/.csv/.json/...`); binary (PDF/image) sources are skipped with a debug log. **PDF text extraction is out of scope** — decide if needed.

---

## 3. Known limitations (some design-conformant)

### Q8 — Parser flattens YAML frontmatter (spec §2.3 gap, root cause for several items)
`MarkdownVaultParser` parses frontmatter into `Dictionary<string,string>` via `value.ToString()`, so **YAML lists/maps don't round-trip** (a list becomes a useless `System.Collections...` string). Consequences:
- `VaultIndexService` rewriting `index.md` preserves **scalar** unknown keys only (commit `bb82529`); complex unknown keys are lost on rewrite.
- Topic `sources:` provenance (a list, spec §2.2) is best-effort across multiple sources on the same page.
- `SectionMergeEngine` reassembly is body-keyed (unaffected), but full-frontmatter rewrites elsewhere share the limitation.
- **Note:** `MemoryService.BumpUpdatedAsync` avoids this by line-splicing only the `updated:` line (preserves all other bytes), so per-section edits are fine — only *full-frontmatter rewrites* drop complex keys.
- **To resolve (optional):** extend the parser to retain raw frontmatter (raw lines or a structured YAML node) so list/map keys round-trip byte-for-byte. Affects all clients via the spec.

### Q9 — Merge heading round-trip / rename tracking (conformant to spec §10.1 oracle)
`SectionMergeEngine.Reassemble` rebuilds heading lines as `"## " + trimmedHeading + "\n"`, so **CRLF / trailing-whitespace headings normalize to LF** through a merge, and **slug-preserving heading renames** (e.g. `## John` → `## JOHN`, same slug) follow the body-supplying side (a rename can be silently dropped if the other side edits the body). Both are **conformant** to the slug-keyed §10.1 oracle but worth a spec note if byte-perfect CRLF heading round-trip or rename-tracking is desired.

---

## 4. Minor cleanups (quality, not blocking — none fixed)

- **Shared frontmatter/line helpers:** the §2 frontmatter block is hand-assembled in ~4 places (`MemoryService`, `VaultIndexService`, `VaultLogService`, `VaultSchemaService`) and `SplitLines`/flow-list parsing is duplicated across ingest/lint/index. Extract `Pia.Infrastructure.Vault.VaultFrontmatter.Build(...)` + a `SplitLines` util + a wikilink-target extractor.
- **`remember` double-resolve:** `HandleRemember` resolves (embeds) for the card preview, then the Execute lambda re-resolves (re-embeds) at commit — ~2× embeddings per `remember`. Kept deliberately (re-resolving at commit is safer if the file changed between preview and commit); optimize by threading the resolution if desired.
- **Edit double-write:** `RememberAsync` Edit does `SpliceSectionAsync` then `BumpUpdatedAsync` = two atomic writes (two watcher events). Fold the `updated:` bump into the splice write.
- **`IngestService` crosslink** fires only on full-name mentions and is untested/effectively dead for short-form mentions; relax to word-boundary (like lint) + add a test, or drop it.
- **`SectionMergeEngine.FrontmatterAndPreamble`** recovers the preamble boundary via `RawText.LastIndexOf("## ")` (substring), which could mis-truncate a preamble containing an inline `## `. Robust fix needs a parser-exposed heading-line-start offset on `VaultSection`.
- **`VaultMigrationRunner` `RecordsWritten`** counts actual writes now; `Dropped` should always be 0 with `createOnAmbiguous:true` (defensive).

---

## 5. Recommended next steps

1. **Run `dotnet test` on Windows/CI** — nothing is runtime-verified except `SectionMergeEngine` (validated via an extracted net10.0 harness). Prioritize the highest runtime-only risk:
   - **Phase 2:** FTS5 **contentless** + rowid-join recall, and vector recall ranking.
   - **Phase 3:** `SectionUpsertService` band thresholds (0.85 / 0.60) and `MergeBullets`.
   - **Phase 4:** migration losslessness (Edit-band nested/array/fenced preservation) + idempotency/guards.
2. **Decide the cut-over trio (Q1–Q3)** — likely one follow-on effort: migrate UI/sync callers → drop `Memories` (4.3) → repoint live sync to vault files (5.3) → surface `ingest`.
3. **Decide parser frontmatter fidelity (Q8)** — it's the root of several §2.3 gaps and is cross-platform (touch the spec + both clients).

## 6. Verification & adversarial-pass record (for trust)

- **Phase 3 dedup proof:** 3/3 adversarial skeptics **failed to refute** — two `remember`s of the same subject → exactly one merged section (`Assert.Single`).
- **Phase 5 merge:** 3/3 skeptics confirmed the **implementation correct on every spec §10.1 branch** (incl. delete-of-unchanged → DROP, which the plan's reference C# got wrong); one skeptic ran it in a real net10.0 harness. Test-coverage gaps they found were then closed (exact conflict-marker bytes, Rule 3, Rule 4a, true Rule 1).
- Code reviews caught + fixed: tool-rename breakage (system prompt / action cards / tokenizer write-ops), migration Edit-band data loss + ambiguous silent-drop, Windows-only chunk-key separator bug, lint self-defeat across runs + spurious xref insertion, index.md unknown-key drop.
