# Auto-Ingest for Vault Sources — Design

**Date:** 2026-07-07
**Branch:** `feature/meeting_attendee`
**Status:** Approved (all sections); ready for implementation planning.

---

## 1. Problem

Ingest — the pipeline that compiles a RAW document under `sources/` into `memory/topics/` wiki
pages (`IngestService`, two LLM calls per source via `AiIngestExtractionService`) — is strictly
on-demand today: it only runs when the assistant's `ingest` tool is invoked from chat. Users who
drop documents into `sources/` see them recall-indexed (embeddings) but never compiled: the Memory
view's sources overview shows "Not ingested yet" indefinitely, and nothing re-compiles a source
that changed after its first ingest.

Two further gaps block honest re-ingestion:

1. **No change detection.** Nothing records what content was ingested, so "changed since last
   ingest" is undecidable.
2. **No per-source provenance inside pages.** Ingest merges entity facts into the topic page
   preamble via `RememberAsync("topic", …)`, interleaved with manual remembers and other sources.
   Re-ingesting a changed source can only append; facts from the old version are never removed.

## 2. Goal

Fully automatic ingest with **replace-per-source** semantics:

- A document added to or changed in `sources/` is ingested automatically (watcher, debounced).
- At startup, sources that changed while the app was closed are re-ingested; sources deleted
  while closed are cleaned up.
- Re-ingest **replaces** what that source previously contributed to topic pages.
- Deleting a source removes its contributions (delete = replace with nothing); pages that end up
  empty are deleted.

### User decisions (from brainstorming Q&A)

| Question | Decision |
|----------|----------|
| Consent model | Fully automatic; settings toggle to disable (default ON) |
| Re-ingest semantics | Replace per-source facts (not additive) |
| Source deletion | Remove its contributions; delete pages that become empty |
| Provenance mechanism | A: machine-managed source sections in topic pages |

### Non-goals

- Binary source handling (PDF/images) — stays deferred; non-text sources remain "cannot be
  ingested".
- A progress UI / background-job handle for ingest — stays deferred ("ingest runs inline").
- Stale-fact cleanup *within* a page's manual content, cross-device dedup of ingest cost, or a
  per-feature provider override (ingest keeps using the default provider).
- Settings UI for the toggle — JSON-only setting, matching the `MeetingAttendeeRosterSnapshotMinutes`
  precedent.

## 3. Triggers and scheduling

New `AutoIngestService` (registered in `Bootstrapper`), implementing a small scheduler interface
(`IIngestScheduler`: `Task<IngestResult> RunAsync(string sourceRef, CancellationToken)`,
`Task RemoveAsync(string sourceRef, CancellationToken)`, event `IngestCompleted`) so other
components share the same serial pipeline:

- **Watcher.** Owns a `FileSystemWatcher` on `<vault>/sources`, filter `*.*`, recursive — the
  existing `VaultWatcher` only sees `*.md` and stays untouched. The directory is created
  defensively before the watcher is constructed (`FileSystemWatcher` throws on a missing root;
  don't depend on `VaultSchemaService` scaffolding order). Created/Changed/Renamed-in enqueue an
  ingest of that source; Deleted/Renamed-out enqueue a contribution-removal. Per-path debounce
  like `VaultWatcher`'s, but with a longer window (~3 s) so multi-second file copies settle
  before we read them.
- **Relocation.** `AutoIngestService` exposes `Stop()`/`Restart(root)` mirroring `VaultWatcher`'s,
  and `AssistantFolderRelocationService` stops it before `SafeDirectoryMove` (its directory handle
  on the old root would otherwise fail the move — the exact failure the existing `VaultWatcher`
  stop prevents) and restarts it on the new root (old root on move failure). `Stop()` releases the
  watcher handle and stops dequeuing; an already in-flight item may finish — its page writes
  serialize behind relocation's exclusive `IVaultWriteGate` lease and re-resolve against the new
  root. Restart order mirrors boot: `AutoIngestService` restarts after `VaultWatcher.Restart`, so
  the "recall watcher first" invariant holds across relocation too.
- **Serial queue.** ALL ingest work funnels through one serial queue — watcher events, the
  startup reconcile, and the chat `ingest` tool (`IngestToolHandler` calls
  `IIngestScheduler.RunAsync` instead of `IIngestService` directly, and thereby also records
  `IngestState`). A single ingest in flight at any time: each costs 2 LLM calls and splices topic
  pages; one queue removes provider hammering and page write races by construction, for the
  manual path too. The queue itself is always available — the `AutoIngestSources` setting gates
  only the automatic *triggers* (watcher + reconcile), never the tool.
- **Startup order and reconcile.** `Bootstrapper` starts `VaultWatcher` FIRST, then
  `AutoIngestService`. This is deliberate: recall indexing of Pia's own page writes happens only
  via the live `VaultWatcher`, so ingest-written topic pages must land while it is running.
  Startup work never blocks boot — the reconcile *scan* (enumerate `sources/**`, hash-compare
  against `IngestState`, enqueue new/changed → ingest and tracked-but-missing → removal,
  unchanged → skip) is itself the queue's first item, and LLM-bound work drains in the
  background. A watcher event may double-enqueue a file the scan also found; the second run
  no-ops on the recorded hash. **First-run backlog is intended:** with an empty `IngestState`,
  the first reconcile enqueues every existing source (2 LLM calls each) and drains FIFO; a chat
  `ingest` tool call issued during that drain waits its turn (visible in the log). No priority
  lane — accepted for simplicity.
- **Gates.** Automatic triggers short-circuit when the `AutoIngestSources` setting (bool,
  default ON) is off, or when no AI provider is configured (logged once, retried naturally on
  the next startup/change — see §4 outcome rules).

## 4. Change-detection state

New `IngestState` table in history.db (created via `SqliteContext.EnsureSchema`, next to the
chunk index):

| Column | Meaning |
|--------|---------|
| `SourceRef` (PK, `COLLATE NOCASE`) | Vault-relative ref, forward slashes (`sources/business plan.md`) — NOCASE so case-variant rename events hit the same row |
| `ContentHash` | SHA-256 of the file's raw bytes at ingest time |
| `Outcome` | The `IngestOutcome` enum value: `Success`, `NoEntities`, `NonTextSkipped`, `EmptySource` |
| `TouchedPages` | JSON array of `memory/topics/<slug>.md` paths the source contributed to; `[]` on degenerate outcomes |
| `UpdatedAt` | Timestamp |

Rules:

- State is written when ingest **completes**, including the degenerate outcomes (`NoEntities`,
  `NonTextSkipped`, `EmptySource`) — an unchanged file must never retry-loop.
- A transient failure (no provider, LLM/network error) records **nothing**, so the source is
  retried on the next change or startup. `SourceNotFound` (file vanished between enqueue and
  dequeue) also records nothing — the pending Deleted event / next reconcile performs the removal.
- **Connection discipline:** `IngestState` access opens its own dedicated `SqliteConnection` via
  `SqliteContext.ConnectionString` — the documented pattern for background-thread writers (see the
  Flow-persistence note on `SqliteContext`). The shared `GetConnection()` connection stays the
  single-threaded property of the recall indexer and must not be touched from the ingest queue.
- Local-only, like the recall index: a second device syncing the same vault re-runs ingest there;
  replace-per-source semantics make that convergent (same sections get rewritten, not duplicated).
- Hashing raw bytes means a pure line-ending flip (LF↔CRLF resave) re-ingests — intentional:
  cheaper than normalizing, and the re-ingest is convergent.
- `TouchedPages` is the removal fast path; if it is missing/stale, removal falls back to scanning
  `memory/topics/*.md` for the source's section marker.
- A completed removal **deletes the source's `IngestState` row** — otherwise every startup
  reconcile would re-enqueue "tracked-but-missing → removal" for the same long-gone source.

## 5. Replace-per-source writes (IngestService change)

Ingest stops merging bullets into the page preamble (`RememberAsync("topic", …)` is no longer
called by ingest; the tool/API path for manual remembers is unchanged). Instead, each entity's
facts land in a **machine-managed section** of `memory/topics/<slug>.md`:

```markdown
## Source: sources/business plan.md

- fact …
- fact …
Related: [[topics/other-page]]
```

- **Upsert.** If the page is missing, create it (standard frontmatter, empty preamble) and append
  the source section. If the page exists, replace the body of the section whose heading exactly
  matches `## Source: <sourceRef>`, or append the section when absent. Deterministic string
  surgery via the existing section byte-range machinery (`VaultDocument.Sections`); no LLM, no
  fuzzy matching.
- **Crosslinks** ("Related:" lines) move inside the source's own section so they are replaced
  together with it — no dangling links accumulate at the end of the file.
- **Shrinking touched-set.** After each ingest the scheduler diffs the source's previous
  `IngestState.TouchedPages` against the new result and runs `RemoveContributionsAsync` on the
  dropped pages — v2 of a source that no longer mentions entity B strips its section from B's
  page (deleting B's page if that empties it). A previously-`Success` source that degrades to a
  degenerate outcome (`NoEntities`/`EmptySource`, e.g. the file was emptied) is the limiting
  case: new touched-set `[]`, so ALL previous contributions are removed and `TouchedPages` is
  overwritten with `[]`. This is what makes "replace what the source previously contributed"
  unconditionally true.
- **Removal.** Owned by `IngestService` as a new `RemoveContributionsAsync(sourceRef,
  touchedPages)` — the scheduler decides *when*, `IngestService` owns all page surgery. It deletes
  the `## Source: <sourceRef>` section from every touched page and removes the ref from the page's
  `sources:` frontmatter. If the page then has no other sections and a whitespace-only preamble,
  delete the page file and its `index.md` entry (the existing `VaultWatcher` sees the `.md`
  deletion and prunes the recall chunks). Manual content — the preamble and any non-`## Source:`
  sections — is never touched.
- **Ref normalization.** Source refs are normalized to forward slashes before hashing, state
  lookups, and heading matches, and heading/ref comparisons are `OrdinalIgnoreCase` — Windows
  paths are case-insensitive, so `Sources/X.md` arriving via a rename event must hit the same
  section as `sources/x.md`.
- **Provenance display** is unchanged: page-level `sources:` frontmatter stays maintained (added
  on ingest, pruned on removal), so `VaultSourcesService` and the Memory view's sources overview
  keep working as-is.
- **Index/log.** `index.md` upsert per touched page and the one-line `ingest` journal entry stay;
  removal writes a corresponding `ingest` log line ("removed …").
- **Hardening (targeted, in scope).** `SourcesProvenance.ReadSourceRefs` and IngestService's
  frontmatter maintainer currently require LF-only text (`"---\n"` at position 0); both become
  CRLF-tolerant, since provenance now drives replace/removal behavior, not just display.
- **User edits inside a `## Source:` section are overwritten** on the next re-ingest of that
  source. This is the accepted trade-off of approach A; the section heading makes the managed
  region visible.

## 6. Feedback and failure handling

Background and silent, matching the vault watcher's posture:

- One `LogInformation` per completed ingest/removal (counts only); source names and refs are
  user-named content → `SensitiveDebug`.
- **Refresh channel:** `IIngestScheduler` raises an `IngestCompleted` event after each completed
  ingest/removal. `MemoryViewModel` subscribes (it already depends on `IVaultSourcesService`),
  marshals to the dispatcher, and reloads the sources list — rows flip from "Not ingested yet" to
  "Compiled into N topic page(s)" without reopening the view. `MemoryViewModel` is scoped while
  the scheduler is a singleton, so the VM **must unsubscribe on dispose/deactivation** or the
  event pins it for the app lifetime.
- Errors never crash the queue: per-item try/catch, warn-level log, continue with the next item.
- No toasts, no progress ring (deferred with the background-job handle).

## 7. Testing

Unit tests against the existing seams (`IIngestExtractor` fake, temp-dir vault store, in-memory
SQLite):

- **Replace matrix.** Ingest → change source → re-ingest replaces the source section and
  preserves the preamble, foreign `## Source:` sections, and manual sections. Delete removes the
  section, prunes frontmatter, and deletes now-empty pages (and only those). Shrinking
  touched-set: v2 touching fewer pages strips the dropped pages; `Success` → `NoEntities`
  removes everything.
- **Reconcile matrix.** New / changed / unchanged / deleted / no-provider / setting-off; verify
  unchanged sources cause zero extractor calls.
- **State rules.** Degenerate outcomes persist a hash (no retry loop); transient failures persist
  nothing (retry happens); a completed removal deletes the state row (reconcile enqueues each
  removal exactly once).
- **Relocation.** Stop/Restart invoked around the move, queue quiesced, restart after
  `VaultWatcher.Restart` — mirroring the existing relocation coverage for `VaultWatcher`.
- **CRLF.** `ReadSourceRefs` + frontmatter maintainer parse CRLF and LF frontmatter identically.
- **Watcher.** Debounce collapse and serial execution, mirroring the existing `VaultWatcher`
  test approach.
