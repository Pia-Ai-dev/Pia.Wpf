# Memory Vault Migration — Design

- **Date:** 2026-06-07
- **Status:** Design (approved in brainstorm; not yet planned for implementation)
- **Scope:** Full stack — Pia.Wpf (Windows), Pia.Mac (PiaKit), and the Pia E2EE sync server
- **Related:** [`2026-06-07-post-quantum-e2ee-migration.md`](2026-06-07-post-quantum-e2ee-migration.md)

## Problem

Assistant-mode memory is stored as opaque JSON blobs in a local database (SQLite on WPF via raw `Microsoft.Data.Sqlite`, GRDB on Mac, PostgreSQL + E2EE on the server). Two pain points:

1. **Duplication.** The model creates new memory objects even when near-identical ones exist. Root cause is the *write path*: `MemoryToolHandler.create_object` → `MemoryService.CreateObjectAsync` unconditionally `INSERT`s a new row with a fresh GUID. The tool descriptions *ask* the model to dedup first, and a `find_duplicates` tool (cosine ≥ 0.7) exists, but nothing enforces it.
2. **No interoperability.** Knowledge is locked in DB blobs. Users want to read, edit, and link it from external tools like Obsidian. Root cause is the *storage format*: JSON-in-a-DB is invisible to the file-based tools users already live in.

These are distinct: dedup is a write-path problem; interop is a format problem. The design fixes both, and adds a layer for the "user dumps lots of content" case.

## Current system (as found)

| Concern | WPF | Mac | Server |
|---|---|---|---|
| Store | SQLite `history.db` | GRDB | PostgreSQL |
| Model | `src/Pia.Wpf/Models/MemoryObject.cs` | `PiaKit/.../Models/MemoryObject.swift` | `Pia.Server/Models/ServerMemory.cs` |
| Service | `src/Pia.Wpf/Services/MemoryService.cs` | `LiveMemoryService.swift` | — |
| Tools | `src/Pia.Wpf/Services/MemoryToolHandler.cs` | `MemoryToolHandler.swift` | — |
| Sync DTO | `src/Pia.Shared/Models/SyncMemory.cs` | — | `EncryptedPayload` + `WrappedDek` |

`MemoryObject`: `Id` (GUID), `Type` (personal_profile / contact_list / preference / note / project / skill / context), `Label`, `Data` (free-form JSON string), `Embedding` (BLOB), timestamps, source back-refs. Retrieval is a hybrid of LIKE + FTS5 + Jaro-Winkler fuzzy + vector cosine.

## Decisions

| # | Decision | Choice |
|---|---|---|
| 1 | Scope | Full stack, including the E2EE sync server |
| 2 | Source of truth | **Files-first** — the markdown vault is canonical; the DB demotes to a rebuildable index |
| 3 | File/identity model | **One doc per memory-type** for structured types; identity = file path (+ heading), GUIDs dropped |
| 4 | Ownership boundary | **Pia writes only inside `/memory/`; indexes the whole vault** (incl. user notes) for recall |
| 5 | Write/merge path | **Deterministic section upsert** — resolve by heading via fuzzy+embedding, edit-or-create in code |
| 6 | Sync conflict policy | **Section-aware 3-way merge** with git-style markers as fallback |
| 7 | Knowledge layer | Adopt **Karpathy's LLM-Wiki** as a second layer (compiled `topics/`), alongside structured memory |
| 8 | Ingest & Lint | **In scope now** (not phase-2) |

A core tension that everything must respect: **E2EE vs. plaintext files.** The sync server only ever sees `EncryptedPayload`/`WrappedDek`. Local files are plaintext (Obsidian requires it); sync encrypts the file bytes. These coexist — and local plaintext is *not* a regression, because today's `history.db` is already plaintext at rest. E2EE only ever protected sync.

## Architecture — two layers, one vault

The vault holds two Pia-owned layers serving different jobs, plus the user's own files:

- **Structured memory** — *"what Pia knows about you."* Small, stable, per-type living docs with deterministic section upsert. (profile / contacts / preferences.)
- **Knowledge wiki** (Karpathy LLM-Wiki) — *"what Pia compiled from content you gave it."* Raw sources are ingested into compiled, cross-linked entity/concept pages. This is what scales when the user adds lots of content.

### Vault layout

```
Vault/                       # configurable root; default %LOCALAPPDATA%\Pia\Vault | ~/Library/Application Support/Pia/Vault
  sources/                   # RAW layer (immutable): PDFs, pasted text, transcripts. Pia READS, never edits.
  memory/                    # Pia-owned. Pia writes ONLY here.
    index.md                 # catalog: every page + 1-line summary, by category. LLM reads first.
    log.md                   # append-only journal: ## [YYYY-MM-DD] operation | description
    AGENTS.md                # the Schema: conventions, human-editable, co-evolved
    profile.md               # structured — personal_profile (sections = facts)
    contacts.md              # structured — contact_list (sections = ## <person>)
    preferences.md           # structured — preference (sections = prefs)
    notes/<slug>.md          # freeform, user-directed notes (one file each)
    projects/<slug>.md       # freeform projects (one file each)
    topics/<slug>.md         # COMPILED wiki — one entity/concept page each, [[linked]]
    .archive/                # originals kept on merge/migration for rollback
  <user's own .md files>     # the user's vault content. Read-only to Pia, indexed for recall.
```

Structured types → **one doc, records as `##` headings**. Freeform types (note/project) and compiled wiki entities → **one file each**.

### File format

YAML frontmatter (Obsidian-native, hidden in reading view):

```yaml
---
pia: managed          # ownership marker — Pia only edits files carrying this
type: contact_list    # or: personal_profile | preference | note | project | topic
title: Contacts
created: 2026-06-07T09:00:00Z
updated: 2026-06-07T09:30:00Z
sources:              # provenance (wiki/topic pages): which raw sources back this page
  - sources/q2-report.pdf
schemaVersion: 1      # both clients agree on layout via this
---
```

Records inside a structured doc are addressed by heading; identity is the **heading slug** (`john-smith`), not a GUID — so files stay clean and `[[contacts#John Smith]]` links work natively. Unknown frontmatter keys are passed through untouched (users may add their own).

## Write path — deterministic section/page upsert

The tool surface collapses to three intent-level tools:

- **`recall(query)`** — searches the *whole vault* (Pia's docs + user notes), returns ranked snippets as `file#heading` refs. Replaces `query_memory`.
- **`remember(type, subject, content)`** — the upsert; enforces dedup.
- **`forget(ref)`** — removes a section or file.

`remember` algorithm (in code, not left to the model):

1. Load the type's doc; split into sections by `##` heading.
2. Score `subject` vs existing headings (Jaro-Winkler) and `content` vs section bodies (embeddings) — reuse the existing search infra.
3. Three bands:
   - **High (≥ ~0.85):** edit that section — merge in new info.
   - **Ambiguous (~0.6–0.85):** do not guess. Return candidate headings to the model to pick or reject. This is the safety valve against bad merges.
   - **Low:** create a new section (or new file for freeform/topic types).

Within-section merge: structured records are `- key: value` bullets → deterministic field-level upsert (replace the `- email:` line, add new keys). Prose sections → hand *only that section* + the new info to the model to rewrite (bounded scope, no whole-doc data-loss risk).

Edits are a **byte-range splice** of the target section — everything outside it is preserved byte-for-byte, so user formatting, frontmatter, and the file-watcher diff stay clean.

The same upsert runs at **page granularity** during Ingest (resolve `topics/<slug>.md`), so dedup is enforced identically in both layers.

## Index & recall — the DB's new job

The DB stops being the store and becomes a **disposable index**: deleted or schema-changed → rebuilt by walking the vault. Nothing canonical lives there.

One row per **section** (not per file):

```
chunks(file_path, heading, content_hash, embedding BLOB, fts_text, indexed_at)
```

Per-section chunking yields precise `contacts.md#John Smith` recall refs and lets re-embedding skip unchanged sections (keyed by `content_hash`). **Embeddings live only here, never in the .md** — keeps files clean for Obsidian.

File-watcher (`FileSystemWatcher` on WPF; FSEvents/`DispatchSource` on Mac):

- change → re-parse, diff sections by hash, re-embed only what changed, refresh FTS;
- delete → drop rows; rename → delete + add;
- debounced, so an Obsidian save and a Pia `remember` flow through the *same* path. Pia's own writes look like file changes to the indexer — no special-casing.

Scope: the indexer covers the **whole vault** — Pia's `/memory/` and the user's own notes — so `recall` surfaces the user's knowledge. Include/exclude globs + a per-file size cap keep large vaults sane; skipped files are logged per the privacy rules (paths hashed à la `SafeUrl`, never raw content).

Boundary: Pia *indexes* user notes locally but only *syncs* its own `/memory/` (and `sources/`). The user's broader vault syncs however they already do (Obsidian Sync, iCloud, git).

## Sync & E2EE

**Sync unit = the file.** Each Pia-owned file maps to one encrypted record: AES-GCM `EncryptedPayload` (the file bytes) + `WrappedDek` — the shape `ServerMemory`/`SyncMemory` already have. Repurpose the row: key by **relative file path** instead of GUID; `Data` becomes the file content. Minimal server churn. The server stays **format-agnostic** — it stores encrypted bytes and never parses markdown.

This is independent of the in-flight post-quantum migration, which changes *how* the DEK is wrapped, not *what* gets wrapped — the two compose.

`sources/` syncs too, but with a **size cap** so large binaries (PDFs) can stay local-only.

### Conflict resolution — section-aware 3-way merge

The real tax on choosing per-type docs: two devices editing different records in `contacts.md` offline = a whole-file conflict. Because records are `##`-delimited, the merge diffs **per section** against the last-synced base (server retains it as merge base — standard 3-way state):

- A edited *John*, B edited *Alice* → auto-merges cleanly (the common case).
- Both edited *John* → git-style conflict marker *inside that section*, surfaced to the user; `recall` flags it so the assistant can offer to reconcile.

Rejected alternatives: last-writer-wins (silently loses an edit) and conflict-copy files (`contacts (conflict).md` clutter).

## The LLM-Wiki layer (Karpathy)

Three layers — Raw Sources (immutable) → Wiki (LLM-maintained markdown) → Schema (`AGENTS.md`) — with three operations.

### Ingest (the fan-out compiler)

Triggered when a user drops a file in `sources/`, pastes a long block, or flags a conversation:

1. Read the raw source (immutable in `sources/`).
2. Summarize + extract entities/concepts.
3. For **each** entity → the deterministic upsert at page granularity (resolve `topics/<slug>.md` by slug+embedding → edit or create).
4. Add `[[wikilinks]]` between touched pages and related existing ones.
5. Refresh touched lines in `index.md`; append `## [2026-06-07] ingest | <source> → topics/x, topics/y` to `log.md`.

Each topic page records `sources:` in frontmatter, so every claim is **traceable to a raw source** — that makes recall cite-able and lets Lint detect staleness. One source may touch 10–15 pages.

### Query

`recall` synthesizes answers with citations. Optionally, a good synthesized answer is persisted as a new `topics/` page (flagged YAGNI for v1 — enable later if useful).

### Lint (periodic coherence pass)

Runs after N ingests, on a schedule, or on demand. Checks:

- **Contradictions** (John's email differs across pages),
- **Stale claims** (backing source changed/removed),
- **Orphans** (no inbound `[[links]]`),
- **Missing cross-refs** (mentions an entity that has a page but isn't linked),
- **Duplicate pages/sections** (subsumes `find_duplicates`),
- **Gap pages** (`[[foo]]` with no `foo.md`).

**Auto-fixes safe items** (add cross-refs, merge clear duplicates → originals to `.archive/`); **flags risky items** (contradictions) for user/assistant resolution. All findings land in `log.md`.

### Where Ingest & Lint run

Background tasks with progress surfaced in the UI — a big-PDF ingest or full-vault lint must not block chat.

## Migration (JSON → vault)

One-time, **reversible** conversion that runs existing `MemoryObject` rows through a renderer:

- `personal_profile` → `profile.md` (fields as `- key: value` bullets);
- `contact_list` → `contacts.md` (one `## <name>` section per array entry);
- `preference` → `preferences.md`;
- `note`/`project` → `notes/<slug>.md` / `projects/<slug>.md` (`Label` → filename + title);
- arbitrary nested JSON → nested bullets; genuinely irregular blobs → fenced ` ```json `. **Lossless, not pretty** — nothing dropped.

Migration **writes through the same `remember` upsert path**, not raw file dumps. Payoffs: idempotent (re-running converges), and it **collapses today's duplicates as it goes** (embedding-clustering feeds an LLM-assisted merge per cluster) — so the vault starts clean. Pain point #1 fixed retroactively.

Safety:

- Originals archived to `/memory/.archive/`; old SQLite `Data` left untouched until the vault is validated — full rollback.
- Old embedding BLOBs are *not* migrated; the indexer recomputes per-section embeddings on first scan (cheap; chunk granularity changed anyway).

Across devices: migrate on one device, let the files sync; others detect an already-populated vault via a `vaultVersion` marker in sync state and **skip re-migration** (no triple-write).

## Cross-platform split

The **shared artifact is the format spec, not code** — `Pia.Shared` is .NET-only and `PiaKit` is Swift, so each client implements the same versioned contract (layout, frontmatter, section conventions, 3-way merge). `schemaVersion` in frontmatter keeps them honest. The server needs no format knowledge.

Per-platform components:

- **New:** MarkdownParser (frontmatter + section split), VaultStore (atomic write: temp-file + rename), Indexer (file-watcher → DB), MergeEngine (3-way), MigrationRunner, IngestPipeline, Linter.
- **Reused:** search tiers, embeddings, fuzzy matcher, `find_duplicates` — repointed from whole-memories to sections/pages.

## Risks

1. **Round-trip fidelity** — mitigate with byte-range splice of the target section only; never full re-serialize.
2. **Obsidian open while Pia writes** — atomic write + debounced re-read handles the race.
3. **Embedding parity** across WPF/Mac — recall diverges if embeddings differ (a latent issue today). Decide a parity strategy at implementation time.
4. **Large user vaults** — globs + size cap + hash-based incremental indexing.
5. **Recall over user notes** surfacing stale/irrelevant content — needs ranking + scoping; consider opt-in.

## Explicitly NOT building (YAGNI)

- Local-at-rest encryption (breaks Obsidian).
- Per-section GUIDs (`file#heading` is enough).
- Real-time CRDT collaboration (section-aware 3-way merge suffices).
- Migrating old embeddings.
- A custom Obsidian plugin (works with vanilla Obsidian).

## Open questions for implementation

- Exact frontmatter schema and required fields (lock `schemaVersion: 1`).
- Embedding-parity strategy across WPF and Mac.
- Recall-over-user-notes: on by default or opt-in (privacy)?
- Behavior when the user points the vault at a pre-existing Obsidian vault.
- `sources/` size cap value and large-binary handling.

## Suggested phasing

1. **Vault core** — format spec, MarkdownParser, VaultStore (atomic writes), frontmatter, `schemaVersion`.
2. **Index & recall** — disposable DB index, file-watcher, per-section chunking, `recall`.
3. **Write path** — deterministic section upsert, `remember`/`forget`; retire `create_object`/`update_object`/`append_to_list`.
4. **Migration** — renderer + dedup-on-migrate, `.archive/`, rollback, `vaultVersion` guard.
5. **Sync** — file-keyed encrypted records, section-aware 3-way merge.
6. **Wiki layer** — `sources/`, `topics/`, `index.md`, `log.md`, `AGENTS.md`.
7. **Ingest & Lint** — fan-out compiler + coherence pass as background tasks.
8. **Mac parity** — port the contract to PiaKit.
```

