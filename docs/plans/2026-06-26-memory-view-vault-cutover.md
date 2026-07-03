# Memory View → Vault Cutover + Migration Activation — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task.
> **Scope note:** WPF-only follow-up. Resolves the **WPF view slice of open-question Q1** ([open-questions](2026-06-07-memory-vault-migration-open-questions.md)) — repoint `MemoryViewModel` onto the vault — and **activates the already-built JSON→MD migration**, which is currently dead-on-arrival. It does **not** drop the legacy `Memories` table or migrate the other five callers (that remains the broader Q1). Mac/Server tracks unaffected.

**Goal:** The Memory screen (`MemoryView`) must show what the assistant actually remembers — markdown sections from the on-disk vault — instead of the legacy SQLite `Memories` table rendered as JSON. As a prerequisite, the existing `VaultMigrationRunner` must actually run on upgrade so a user's pre-vault JSON memories appear in both the assistant's recall and the new view.

**Architecture:** The markdown vault (`%LOCALAPPDATA%\Pia\Vault\memory\`) is already the source of truth for the assistant (`MemoryToolHandler` → `RememberAsync`/`ForgetAsync`/`RecallAsync`). The view is the last consumer still reading the legacy JSON store. We add a small **read/list/edit surface** to `IMemoryService` over the vault, repoint the view at it, and fix the migration guard so legacy rows get converted to markdown on first launch.

**Design reference:** [`2026-06-07-memory-vault-migration-design.md`](2026-06-07-memory-vault-migration-design.md) · **Format spec:** [`../specs/memory-vault-format-v1.md`](../specs/memory-vault-format-v1.md) · **Existing impl plan:** [`2026-06-07-memory-vault-migration-implementation.md`](2026-06-07-memory-vault-migration-implementation.md)

---

## How to read this plan

- Bite-sized TDD: **write failing test → run (fails) → implement → run (passes) → commit**. One commit per task.
- Build/test runs on **Windows / CI only** (`net10.0-windows`), via the MTP runner — **not** VSTest. Gate on no failures **outside** `Pia.Wpf.Tests.Integration.Providers` (the ~18 known live-network failures live there). New tests here are unit/integration and must pass.
- New `.cs` files must be **CRLF** (repo convention; the Write tool emits LF — convert new files).
- Privacy logging (CLAUDE.md): memory titles, section bodies, vault paths, and URLs are **sensitive**. Use `SensitiveDebug` / `SafeUrl` — never log a heading, body, or full path at `Information`.
- DRY/YAGNI: do not build sync, ingest, lint, or table-drop here. See **Out of scope**.

---

## Cross-cutting facts (verified against source)

- **Vault addressing** is a `path#heading` string (e.g. `memory/contacts.md#John Smith`). Before `#` = vault-relative file path; after `#` = section **heading text** (re-slugified to match). A bare path (no `#`) = whole file. Open-coded today only in `MemoryService.ForgetAsync` (`MemoryService.cs:875-886`) — **Task B.1 extracts it**.
- **Read primitives** (`IVaultStore`): `ReadAsync(path) → VaultDocument?`, `EnumerateAsync(globUnderRoot) → IReadOnlyList<string>` (recursive, sorted), `SpliceSectionAsync(path, slug, newBody)`, `DeleteAsync(path)`, `WriteAtomicAsync(path, content)`, `Root`.
- **`VaultDocument(Frontmatter, Preamble, Sections, RawText)`**; `VaultSection(Heading, Slug, Body, BodyStart, BodyEnd)`. `Frontmatter` is a flat `Dictionary<string,string>` (`id`, `type`, `title`, `created`, `updated`, `pia`). **`VaultDocument.Id`/`.Type` throw if the key is absent** (`VaultDocument.cs:19-20`) — always read via `Frontmatter.TryGetValue`, never `.Id`/`.Type`, because non-Pia files exist.
- **Glob gotcha:** `EnumerateAsync("memory/*.md")` walks `memory/` **recursively** (`VaultStore.cs:123`, `SearchOption.AllDirectories`) and is **not** a real glob — it returns `AGENTS.md`, `index.md`, and every file under `notes/`/`projects/`/`topics/`/`.archive/`. This is the root of both the migration bug and the listing logic, so a single shared **record-file predicate** is introduced (Task A.1) and reused by GUARD 2 and the view list.
- **Search** = `RecallAsync(query, topK) → IReadOnlyList<RecallHit(FilePath, Heading, Snippet, Score)>` over the `Chunks` index (`MemoryService.cs:417`). `RecallHit` carries no `Slug`; the reference is composed from `FilePath` + `Heading`.
- **Display grouping** = `VaultIndexService.CanonicalGroups` (`VaultIndexService.cs:29-37`) — the authoritative §8 type order + display names (Personal Profile, Contacts, Preferences, Notes, Projects, Topics). Mirror these, **not** `MemoryObjectTypes.GetDisplayName`.
- **Markdown render host** = `Controls/MarkdownMessageControl.xaml(.cs)` with the public `MarkdownText` string DP (calls `PiaMarkdownRenderer.Render` at `.cs:125`). `PiaMarkdownRenderer` itself returns a `FlowDocument`, not a `UIElement`, so use the control, not the raw renderer.
- **Embeddings are owned by the watcher/indexer.** Vault writes (`SpliceSectionAsync`/`ForgetAsync`) trigger `VaultWatcher` → `VaultIndexer.IndexFileAsync` (300 ms debounce). The view must **not** generate or regenerate embeddings.

---

## Decisions locked (review before executing)

| # | Fork | Decision | Consequence |
|---|------|----------|-------------|
| D1 | Migration fix: reorder vs. narrow guard | **Narrow GUARD 2** to ignore scaffolding/housekeeping files (Task A.1). Keep the deliberate scaffold-first ordering. `VaultVersion` (GUARD 1) stays the authoritative idempotency gate. | One predicate, reused by the view. No change to `InitializeAsync` ordering or the write path. |
| D2 | View item shape: reuse `MemoryObject` vs. new | **New thin `VaultMemoryItem`** keyed by `path#heading`. | Reuse saves little: `ValuePreview` (JSON parse), `IsStale`, dates, and Guid identity all need rework anyway. Clean break; `MemoryObject` stays for the still-legacy callers. |
| D3 | Per-item sort/filter | Group by document type (canonical order); within a group, items **alpha by heading**. **Drop the "Stale" and "Today" filters.** | Frontmatter `updated` is **document-level**, not per-section (`contacts.md` with 50 people shares one timestamp), so per-item recency is meaningless. Search box stays (`RecallAsync`). |
| D4 | Edit affordance | Keep it: a **raw markdown body editor** that writes via a new `UpdateSectionAsync(reference, body)` → `SpliceSectionAsync`. **No** bullet-merge on manual edit (whole-body replace). | Preserves today's edit/save UX without re-deriving `MergeBullets`. JSON validation is removed. |
| D5 | Header metrics | Replace `TotalObjectCount`/`StorageSizeText` with **vault-derived** counts/bytes (Task B.2 returns both). | `GetObjectCountAsync`/`GetStorageSizeAsync` read the legacy table and would show stale/zero data once the table is frozen. |

---

# Phase A — Activate the JSON→MD migration  (depends: nothing)

> **Reference defect.** On upgrade: `VaultVersion = 0`, vault freshly scaffolded. `Bootstrapper.cs:125` `EnsureScaffoldingAsync()` writes `memory/AGENTS.md` (`VaultSchemaService.cs:61`). Then `Bootstrapper.cs:138` runs the migration, whose GUARD 2 (`VaultMigrationRunner.cs:68`) does `EnumerateAsync("memory/*.md")` → recursively matches `AGENTS.md` → `Count > 0` → **Skipped**. `VaultVersion` never advances; legacy JSON is **never** migrated. The migrator itself (type map, `MemoryJsonRenderer`, archive, reindex) is complete and unit-tested — only the guard is wrong.

### Task A.1: Shared record-file predicate; narrow GUARD 2
**Files:** `src/Pia.Wpf/Infrastructure/Vault/VaultStore.cs` (or a new `Infrastructure/Vault/VaultPaths.cs` static helper), `src/Pia.Wpf/Services/Migration/VaultMigrationRunner.cs`.
- Add `static bool VaultPaths.IsRecordFile(string relativePath)`. It must be correct for the **broadest** enumeration scope (`EnumerateAsync` is not a real glob — `"*.md"` walks the whole vault root including `sources/`; see Cross-cutting facts). True iff **all** hold (compare with forward-slash-normalized, `OrdinalIgnoreCase`):
  - ends in `.md`;
  - is under `memory/` (excludes the immutable `sources/` RAW layer);
  - is **not** an exact housekeeping path — `memory/AGENTS.md`, `memory/index.md`, `memory/log.md` (match by **exact relative path**, not bare basename, so a user note `memory/notes/index.md` is **not** dropped);
  - is **not** under `memory/.archive/` or `memory/log/`. *(Confirm whether log is the file `memory/log.md` or a `memory/log/` dir from `VaultLogService`/`VaultIndexService` and exclude the actual form.)*
  - Record locations that pass: `memory/{profile,contacts,preferences}.md` and `memory/{notes,projects,topics}/*.md`.
- In `VaultMigrationRunner.RunAsync` GUARD 2, filter: `existing.Where(VaultPaths.IsRecordFile).Any()` instead of `existing.Count > 0`.
- **Step 1 (failing test):** `VaultPathsTests` — false for `memory/AGENTS.md`, `memory/index.md`, `memory/log.md`, `memory/.archive/x.json`, `sources/raw.md`; **true** for `memory/contacts.md`, `memory/notes/foo.md`, and the edge case `memory/notes/index.md` (a note that slugifies to `index`).
- **Step 2:** run, expect FAIL.
- **Step 3:** implement.
- **Step 4:** run, expect PASS.
- **Step 5 commit:** `fix(memory): exclude scaffolding files from migration populated-vault guard`.

### Task A.2: Integration test for the scaffold→migrate sequence
**Files:** `tests/Pia.Wpf.Tests/Migration/VaultMigrationStartupTests.cs` (new).
- The existing `VaultMigrationRunnerTests` build the runner in isolation and **cannot** catch this class of bug. New test wires a real `VaultStore` (temp root) + a `VaultSchemaService` + the runner over a fake settings store with `VaultVersion = 0` and ≥1 legacy `MemoryObject`.
- Assert: after `EnsureScaffoldingAsync()` **then** `RunAsync()`, the report is **not** `Skipped`, `RecordsWritten ≥ 1`, a record file exists (e.g. `memory/profile.md`), and `VaultVersion == 1`.
- Second test (must keep `VaultVersion = 0` so it exercises **GUARD 2**, not GUARD 1): with a real record file already present (e.g. `memory/contacts.md` written before the run), `RunAsync()` **is** `Skipped` (cross-device safety net still works).
- **Steps 1–5** as above. Commit: `test(memory): cover scaffold-then-migrate startup ordering`.

> After Phase A, an upgrading user's legacy rows land in the vault and are re-indexed by `RebuildAllAsync`. The view (Phase C) then has data to show. **Manual re-run UI is Out of scope** — `VaultVersion` is the gate; QA can reset it via settings.

---

# Phase B — Vault read/list/edit surface on `IMemoryService`  (depends: A)

> No public API enumerates vault memories as sections today; the view must not open-code file walking. Add three methods. All are vault-only; none touch the legacy table.

### Task B.1: Extract a `path#heading` reference parser
**Files:** `src/Pia.Wpf/Services/MemoryService.cs` (+ wherever the shared helper lands, e.g. `Infrastructure/Vault/VaultReference.cs`).
- Add `static (string Path, string? Slug) VaultReference.Parse(string reference)`: split on first `#`; if a heading part exists, `Slug = VaultSlug.Slugify(heading)`; else `Slug = null` (whole-file).
- Refactor `ForgetAsync` (`MemoryService.cs:875-886`) to use it (behavior-preserving).
- **Test:** `Parse("memory/contacts.md#John Smith")` → `("memory/contacts.md","john-smith")`; `Parse("memory/notes/x.md")` → `("memory/notes/x.md", null)`.
- Commit: `refactor(memory): extract path#heading reference parser`.

### Task B.2: `ListMemoriesAsync` — enumerate vault memories + metrics
**Files:** `src/Pia.Wpf/Services/Interfaces/IMemoryService.cs`, `src/Pia.Wpf/Services/MemoryService.cs`, new DTO `src/Pia.Wpf/Models/Vault/VaultMemoryItem.cs`.
- DTO: `record VaultMemoryItem(string Reference, string FilePath, string Type, string Title, string Body, DateTime? Updated)`. `Reference` = `FilePath` + `#` + heading (or bare path for body-in-preamble freeform files). `Type` from `Frontmatter.TryGetValue("type")` (fallback: infer from path via the §7 dirs). `Updated` parsed from frontmatter `updated` (document-level — see D3).
- `Task<IReadOnlyList<VaultMemoryItem>> ListMemoriesAsync()`: `EnumerateAsync("memory/*.md")` (scoped to `memory/`, **not** `"*.md"` — the latter walks `sources/` too) → filter `VaultPaths.IsRecordFile` → `ReadAsync` each → for files **with** sections, one item per `VaultSection` (`Title = Heading`, `Body = section.Body`); for freeform files with **zero** sections, one item from `Preamble` (`Title = Frontmatter["title"]` or filename).
- `Task<(int Count, long Bytes)> GetVaultMemoryStatsAsync()`: count items + sum record-file byte lengths (for header metrics, D5).
- **Test:** seed a temp vault with `memory/profile.md` (2 sections), `memory/contacts.md` (1 section), `memory/notes/foo.md` (preamble-only), plus `memory/AGENTS.md`, `memory/index.md`, and a `sources/raw.md`; assert 4 items, correct types/titles, that `AGENTS.md`/`index.md`/`sources/raw.md` are all excluded, and stats count == 4.
- Commit: `feat(memory): list vault memories as sections for the UI`.

### Task B.3: `UpdateSectionAsync` — manual body edit (D4)
**Files:** `IMemoryService.cs`, `MemoryService.cs`.
- `Task UpdateSectionAsync(string reference, string newBody)`: `VaultReference.Parse`; if `Slug` → `SpliceSectionAsync(path, slug, newBody)`; if bare path → `WriteAtomicAsync` rebuilding the file body under existing frontmatter (preserve frontmatter; bump `updated`). Embeddings reindex via the watcher — do not generate here.
- **Test:** edit a section body, re-read, assert body changed and frontmatter/`id` preserved; sibling sections untouched (byte-range splice).
- Commit: `feat(memory): update a vault section body by reference`.

---

# Phase C — Repoint the Memory view at the vault  (depends: B)

> This is the user-visible change. `MemoryView.xaml` itself barely changes (it binds `MemoryGroups`/`SelectedMemory`); the work is in the VM, the item shape, and the inspector's render host.

### Task C.1: Rework `MemoryViewModel` onto the vault
**Files:** `src/Pia.Wpf/ViewModels/MemoryViewModel.cs`.
- `LoadMemoriesAsync`: when `SearchQuery` empty → `ListMemoriesAsync()`; else → `RecallAsync(SearchQuery)` projected to `VaultMemoryItem` (compose `Reference` from `FilePath`+`Heading`; `Body` = re-read or `Snippet`). Group by `Type` using `VaultIndexService.CanonicalGroups` order + display names; items alpha by `Title` (D3).
- `SelectedMemory` becomes `VaultMemoryItem?`. `MemoryGroupViewModel.Items` becomes `ObservableCollection<VaultMemoryItem>`.
- **Remove:** `SelectedMemoryDataFormatted` (verified dead — no XAML binding), `CopyJsonCommand`→`CopyMarkdownCommand` (copies `Body`), `JsonHelper.FormatJson`/`JsonNode.Parse` usage, the `catch(JsonException)` save branch, the embedding regenerate-on-save block, and the `ActiveFilter` "Stale"/"Today" arms.
- Header metrics from `GetVaultMemoryStatsAsync` (D5). Drop/relabel the embedding-download and regenerate commands' UI (watcher owns reindex) — **defer their removal**; just stop calling them from save.
- **Test:** `MemoryViewModelTests` with a fake `IMemoryService` returning `VaultMemoryItem`s → assert groups built in canonical order, items alpha, stats wired.
- Commit: `feat(memory): drive MemoryViewModel from the vault, not SQLite JSON`.

### Task C.2: Inspector renders markdown
**Files:** `src/Pia.Wpf/Controls/Memory/PiaMemoryInspector.xaml`, `src/Pia.Wpf/Controls/Memory/PiaJsonView.xaml(.cs)`.
- In `PiaMemoryInspector.xaml:15`, replace `<mem:PiaJsonView/>` with the markdown read view + edit decomposition. `PiaJsonView` conflates three concerns — split them:
  - **(a) read render:** `MarkdownMessageControl MarkdownText="{Binding Body}"` (replaces the hand-colorized `JsonHost` TextBlock).
  - **(b) edit:** keep a `TextBox` bound to `EditingData` (now markdown), Save/Cancel row.
  - **(c) source strip:** `SourceLabel`/`OpenSourceCommand` have **no vault equivalent** → drop (no `SourceConversationId` on vault items).
- `PiaInspectorHeader` is generic metadata (Type chip, Title, Updated) — rebind `ShortId`→hide, dates→`Updated` only.
- Commit: `feat(memory): render memory bodies as markdown in the inspector`.

### Task C.3: Edit/delete through vault verbs; row preview
**Files:** `MemoryViewModel.cs`, `src/Pia.Wpf/Controls/Memory/PiaMemoryRow.xaml`, `PiaMemoryCategoryCard.xaml.cs`.
- `ExecuteDeleteMemory` → `ForgetAsync(item.Reference)` (was `DeleteObjectAsync(Guid)`); confirmation uses `Title`.
- `ExecuteSaveEdit` → `UpdateSectionAsync(item.Reference, EditingData)` (was `UpdateObjectDataAsync`); no JSON validation.
- `PiaMemoryRow` preview: bind a markdown snippet (first non-empty line of `Body`, truncated) instead of JSON-derived `ValuePreview`. `PiaMemoryCategoryCard.xaml.cs` selection sync keys off `VaultMemoryItem.Reference` (was `MemoryObject.Id`/`ReferenceEquals`).
- `PiaTypeChip` + `MemoryTypeToBrushConverter`/`MemoryTypeToLabelConverter` are enum-driven and reused as-is (map `Type` string → `MemoryType`).
- **Test:** VM test — delete calls `ForgetAsync` with the right reference; save calls `UpdateSectionAsync`; re-load reflects the change.
- Commit: `feat(memory): wire delete/edit to forget/update-section`.

---

## Final verification

- `dotnet build` clean; `dotnet test` (MTP runner) green **outside** `Pia.Wpf.Tests.Integration.Providers`.
- Manual (Windows): on a profile with legacy JSON memories and `VaultVersion = 0`, launch → migration runs (log: "Vault migration ran: N row(s)") → open Memory view → memories render as markdown, grouped by canonical type, searchable, editable, deletable. Confirm `%LOCALAPPDATA%\Pia\Vault\memory\.archive\` holds the originals.
- Confirm GUARD 2 still skips when a real record file is present (re-launch is idempotent; `VaultVersion == 1`).

## Risks & guardrails

- **Empty-vault-on-upgrade coupling:** if Phase A is skipped, the view shows nothing despite legacy data existing. A and C ship together for upgrading users.
- **Doc-level timestamps (D3):** do not present per-item recency you don't have. Group sort may use doc `updated`; item sort is alphabetical.
- **§7 map divergence:** `MemoryService` write-path dirs vs. `VaultIndexService.TypeForTarget` vs. `AGENTS.md` prose disagree on `topic`. Out of scope to reconcile, but **use `VaultIndexService.CanonicalGroups` for display** so Topics renders correctly regardless.
- **Frontmatter fidelity (Q8):** `MarkdownVaultParser` flattens YAML lists/maps; `UpdateSectionAsync` must use `SpliceSectionAsync` (body-only) for sectioned files to avoid rewriting frontmatter and losing list-valued keys.
- **Privacy:** no headings/bodies/paths in release logs (CLAUDE.md).

## Out of scope (tracked elsewhere — do NOT do here)

- Dropping the legacy `Memories`/`MemoriesFts` tables and retiring `MemoryService` JSON CRUD — **Q1 (Task 4.3)**; gated on all six callers migrated + cross-platform green run. This plan migrates **only `MemoryViewModel`**.
- The other five legacy callers (`AccountSettingsViewModel`, `FirstRunWizardViewModel`, `SyncClientService`, `TokenMapService`, `AutocompleteService`) — Q1.
- Live sync cut-over to vault files — **Q2 (Task 5.3)**.
- **`ExportAllAsync` / the Export button stays on the legacy SQLite table.** Conscious deferral: after the cutover it will export legacy (stale/empty-once-frozen) data, not the vault. Either hide the Export affordance in Task C.1 or accept the stale export until a vault-aware `ExportAllAsync` lands with Q1/Q2. **Decide explicitly during C.1; do not leave it silently wrong.**
- Surfacing the `ingest` tool — **Q3**. Scheduling lint — **Q4**. Background ingest UI — **Q5**.
- A user-facing "re-run migration" button and removing the embedding download/regenerate UI from the status bar (the watcher owns reindex) — follow-up.
- Mac and Server tracks.
