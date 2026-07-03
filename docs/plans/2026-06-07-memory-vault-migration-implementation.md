# Memory Vault Migration — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Execution note:** This plan is designed to be run by **ultracode dynamic workflows**. Each phase is a workflow run; within a phase, the per-repo tracks (WPF ‖ Mac ‖ Server) are parallelizable except where a task lists an explicit dependency. The **Dependency Graph** and **Parallelization** notes below tell the workflow what may fan out.

**Goal:** Migrate assistant-mode memory from opaque JSON-in-a-database to a files-first markdown vault that is Obsidian-interoperable, deduplicates by construction, and adds a Karpathy-style LLM-Wiki layer — across Pia.Wpf, Pia.Mac, and the Pia sync server.

**Architecture:** The markdown vault on disk becomes the source of truth. The local DB (SQLite/GRDB) demotes to a *rebuildable* per-section index (FTS + embeddings) driven by a file-watcher. Writes go through a **deterministic section upsert** that resolves a record by heading (fuzzy + embedding) and edits-or-creates, so duplicates can't form. Sync stays E2EE and zero-knowledge: each file = one record keyed by a frontmatter GUID, with the path and content encrypted in the payload; **section-aware 3-way merge runs client-side**, server stays last-writer-wins. A second "wiki" layer ingests raw sources into compiled, cross-linked `topics/` pages with `index.md`/`log.md` and Ingest/Query/Lint operations.

**Tech Stack:** C#/.NET 10 (WPF client `net10.0-windows`, server `net10.0`, EF Core + Postgres/SQLite, Markdig, YamlDotNet, xUnit v3, NSubstitute); Swift (PiaKit, GRDB, swift-markdown, Yams, Swift Testing); shared markdown format spec; ONNX MiniLM-384 embeddings (WPF) / Apple NLEmbedding (Mac).

**Design reference:** [`2026-06-07-memory-vault-migration-design.md`](2026-06-07-memory-vault-migration-design.md)

---

## How to read this plan

This is a multi-repo migration, not a single-feature plan, so it adapts the standard bite-sized-TDD format:

- **Reference code is given in full for the hard, novel algorithms** (markdown parser, section upsert, 3-way merge, ingest, lint). C# is the reference implementation; the Mac track mirrors the same logic in Swift. Do not reinvent these.
- **Mechanical tasks** (CRUD plumbing, DI wiring, DTO field additions) are specified by exact files + signatures + test names + acceptance criteria; the executing agent writes the bodies against the real files in front of it.
- Every task still follows **write failing test → run (fails) → implement → run (passes) → commit**.
- **DRY / YAGNI / TDD / frequent commits** throughout.

### Build/test reality (read before executing)

| Repo | Build/test | Where it can run |
|---|---|---|
| Pia.Wpf | `dotnet build` / `dotnet test` (`net10.0-windows`) | **Windows / CI only** — not on the dev Mac (no dotnet, Windows TFM) |
| Pia server | `dotnet test tests/Pia.Server.Tests` (`net10.0`, in-memory SQLite) | **Windows/Linux/CI with dotnet** — not on the dev Mac |
| Pia.Mac | `swift test` in `Packages/PiaKit`; `xcodebuild ... -scheme Pia` | **macOS locally** |

Workflows must route WPF/server tasks to a dotnet-capable runner and Mac tasks to macOS. Where a task says "run the test," that means on the appropriate runner.

### Branches

- Pia.Wpf: `feature/memory_update` (already created off `feature/personas`).
- Pia.Mac: create `feature/memory_update`.
- Pia: create `feature/memory_update`.

Commit frequently (one commit per task's final step). Keep the shared format spec (Phase 0) identical across repos.

---

## Dependency Graph (for workflow fan-out)

```
Phase 0  Shared format spec ............... BLOCKS everything
Phase 1  Vault core .................. [WPF ‖ Mac]            depends: P0
Phase 2  Index & recall ............. [WPF ‖ Mac]            depends: P1
Phase 3  Write path (upsert) ........ [WPF ‖ Mac]            depends: P1, P2
Phase 4  Migration .................. [WPF ‖ Mac]            depends: P1, P2, P3
Phase 5  Sync & E2EE ................ [Server ‖ WPF ‖ Mac]   depends: P1 (clients), P0 (server); server track independent of P2–P4
Phase 6  Wiki layer ................. [WPF ‖ Mac]            depends: P1, P2, P3
Phase 7  Ingest & Lint .............. [WPF ‖ Mac]            depends: P6
```

Critical path: P0 → P1 → P2 → P3 → {P4, P6 → P7}. The **server track of P5** has no dependency beyond P0 and can run as early as P1 (it only repurposes the payload shape). WPF and Mac are independent tracks throughout — the only thing binding them is the Phase 0 spec.

---

## Cross-cutting contracts (apply in every phase)

**C1 — Frontmatter is the sync identity.** Every Pia-managed file carries:
```yaml
---
pia: managed
id: 6f9c…-uuid          # STABLE sync identity (server key). Never changes on rename/move.
type: contact_list      # personal_profile | preference | note | project | topic
title: Contacts
created: 2026-06-07T09:00:00Z
updated: 2026-06-07T09:30:00Z
sources: [sources/q2.pdf]   # topic/wiki pages only — provenance
schemaVersion: 1
---
```
The human/Obsidian identity is the **path**; the GUID is invisible to the user but is what the server row is keyed on. Unknown frontmatter keys are preserved verbatim (users add their own).

**C2 — Edits are byte-range splices.** Never re-serialize a whole file to change one section; splice only the target section's byte range so user formatting, unknown frontmatter, and the watcher diff stay clean.

**C3 — The DB is disposable.** Nothing canonical lives in SQLite/GRDB. Deleting the index DB and rebuilding from the vault must be a supported, tested operation.

**C4 — Embeddings never leave the device** and are never written into `.md`. Each platform uses its own model; no cross-platform parity requirement.

**C5 — Server stays zero-knowledge.** Path + content + frontmatter are inside `EncryptedPayload` when E2EE is active. The server never gains a plaintext path column that leaks content when E2EE is on (see Phase 5).

**C6 — Type taxonomy reconciliation.** WPF currently has 7 types (`personal_profile, contact_list, preference, note, project, skill, context`); Mac has 4 (`personal_profile, contact_list, preference, note`). The spec (Phase 0) fixes the canonical set as `personal_profile, contact_list, preference, note, project, topic`; `skill`/`context` migrate to `note`. Both clients implement the same set.

---

# Phase 0 — Shared format spec (BLOCKS ALL)

**Goal:** One authoritative spec both clients implement identically. No code; this is the contract.

### Task 0.1: Author the vault format spec

**Files:**
- Create: `Pia.Wpf/docs/specs/memory-vault-format-v1.md` (canonical copy)
- Copy verbatim to: `Pia.Mac/docs/specs/memory-vault-format-v1.md`, `Pia/docs/specs/memory-vault-format-v1.md`

**Step 1 — Write the spec.** It must pin down, exactly:

1. **Vault layout** (from design):
   ```
   Vault/
     sources/                 # RAW, Pia reads-never-edits
     memory/                  # Pia writes only here
       index.md  log.md  AGENTS.md
       profile.md  contacts.md  preferences.md
       notes/<slug>.md  projects/<slug>.md  topics/<slug>.md
       .archive/
     <user .md>               # read-only to Pia, indexed
   ```
2. **Frontmatter schema** (C1) — required keys, types, ISO-8601 UTC timestamps, `schemaVersion: 1`.
3. **Section convention** — records inside structured docs are `##` (level-2) headings; the **slug** = kebab-case of the heading (the section identity). Body = everything until the next `##` or EOF.
4. **Structured-record body format** — `- key: value` bullet lines for deterministic field merge; free prose allowed below the bullets.
5. **Wikilinks** — `[[file]]` and `[[file#Heading]]`; relative to vault root, no extension.
6. **Slug rules** — lowercase, spaces→`-`, strip punctuation, collision suffix `-2`, `-3`.
7. **Canonical type set** (C6) and the structured-vs-freeform mapping.
8. **`index.md` format** — grouped by type, one line per page: `- [[topics/foo]] — <one-line summary>`.
9. **`log.md` format** — append-only, `## [YYYY-MM-DD] <op> | <description>` (grep-parseable).
10. **3-way merge semantics** (C2 + Phase 5) — per-section, conflict-marker fallback format.
11. **Sync envelope** — what's inside `EncryptedPayload` (Phase 5): `{ path, content }`.

**Step 2 — Acceptance.** A reviewer agent reads the spec and confirms a developer with zero context could produce a byte-identical file from it. No ambiguity in slug rules, frontmatter, or section boundaries.

**Step 3 — Commit** in all three repos: `docs: add memory vault format spec v1`.

**Parallelization:** single task, must complete before Phase 1 starts in any repo.

---

# Phase 1 — Vault core  [WPF ‖ Mac]

**Goal:** Parse, represent, and atomically write vault files per the spec. This is the foundation; everything else builds on the parser and store.

## Reference data model (C# reference; Swift mirrors)

```csharp
// Pia.Wpf/src/Pia.Models/Vault/VaultDocument.cs
public sealed record VaultSection(
    string Heading,         // "John Smith"
    string Slug,            // "john-smith"
    string Body,            // text after the heading line, until next ## / EOF (no trailing heading)
    int BodyStart,          // byte offset into RawText where Body begins
    int BodyEnd);           // byte offset where Body ends

public sealed record VaultDocument(
    IReadOnlyDictionary<string, string> Frontmatter,  // raw scalar values; lists kept as raw text
    string Preamble,        // text between frontmatter and first '##'
    IReadOnlyList<VaultSection> Sections,
    string RawText)         // exact original bytes, for splice-based edits (C2)
{
    public Guid Id => Guid.Parse(Frontmatter["id"]);
    public string Type => Frontmatter["type"];
}
```

## WPF track

### Task 1.1: Add YamlDotNet; create the markdown parser

**Files:**
- Modify: `Pia.Wpf/src/Pia.Wpf/Pia.Wpf.csproj` (add `<PackageReference Include="YamlDotNet" Version="16.*" />`) — Markdig 1.1.3 is already present and provides the `YamlFrontMatterExtension`, but we parse frontmatter values with YamlDotNet for typed access.
- Create: `Pia.Wpf/src/Pia.Models/Vault/VaultDocument.cs`
- Create: `Pia.Wpf/src/Pia.Infrastructure/Vault/MarkdownVaultParser.cs`
- Test: `Pia.Wpf/tests/Pia.Wpf.Tests/Vault/MarkdownVaultParserTests.cs`

**Step 1 — Write failing tests** (xUnit v3, no API key needed — pure parsing):

```csharp
public class MarkdownVaultParserTests
{
    private readonly MarkdownVaultParser _parser = new();

    [Fact]
    public void Parses_frontmatter_and_sections()
    {
        var md = "---\nid: 11111111-1111-1111-1111-111111111111\ntype: contact_list\ntitle: Contacts\nschemaVersion: 1\n---\nIntro line.\n\n## John Smith\n- email: john@x.com\n\n## Alice Jones\n- phone: 555\n";
        var doc = _parser.Parse(md);

        Assert.Equal("contact_list", doc.Type);
        Assert.Equal("Intro line.", doc.Preamble.Trim());
        Assert.Equal(2, doc.Sections.Count);
        Assert.Equal("john-smith", doc.Sections[0].Slug);
        Assert.Contains("email: john@x.com", doc.Sections[0].Body);
    }

    [Fact]
    public void RawText_is_preserved_exactly()
    {
        var md = "---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\nbody\n";
        Assert.Equal(md, _parser.Parse(md).RawText);
    }

    [Fact]
    public void Slug_collision_and_punctuation_rules() // per spec §6
    {
        var doc = _parser.Parse("---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\n## Café (work)!\n## Café (work)!\n");
        Assert.Equal("cafe-work", doc.Sections[0].Slug);
        Assert.Equal("cafe-work-2", doc.Sections[1].Slug);
    }
}
```

**Step 2 — Run, expect FAIL** (`MarkdownVaultParser` not defined):
`dotnet test tests/Pia.Wpf.Tests --filter "FullyQualifiedName~MarkdownVaultParserTests"` → FAIL.

**Step 3 — Implement** `MarkdownVaultParser.Parse(string) : VaultDocument`. Reference algorithm:
- If text starts with `---\n`, read up to the next line that is exactly `---`; parse that block with YamlDotNet into the frontmatter dictionary.
- Preamble = text from end-of-frontmatter to the first line matching `^## `.
- Walk lines; each `^## (.+)$` starts a new section. Heading = captured text trimmed; slug per spec §6 (lowercase, strip non-alphanumerics to `-`, collapse `-`, dedupe with `-N`). Body = bytes from after the heading line to the byte before the next heading / EOF; record `BodyStart`/`BodyEnd` offsets into `RawText`.
- Keep `RawText` untouched.

**Step 4 — Run, expect PASS.**

**Step 5 — Commit:** `feat(vault): markdown vault parser + document model`.

### Task 1.2: Atomic vault store (read/write/splice)

**Files:**
- Create: `Pia.Wpf/src/Pia.Infrastructure/Vault/VaultStore.cs` + `IVaultStore.cs`
- Test: `Pia.Wpf/tests/Pia.Wpf.Tests/Vault/VaultStoreTests.cs`

**Interface:**
```csharp
public interface IVaultStore
{
    string Root { get; }
    Task<VaultDocument?> ReadAsync(string relativePath);
    Task WriteAtomicAsync(string relativePath, string content);              // temp-file + File.Move replace
    Task SpliceSectionAsync(string relativePath, string slug, string newBody); // C2: replace only [BodyStart,BodyEnd)
    Task<IReadOnlyList<string>> EnumerateAsync(string globUnderRoot);          // ".md" files
    Task DeleteAsync(string relativePath);
}
```

**Step 1 — Failing tests:** (a) write→read round-trips bytes; (b) `SpliceSectionAsync` changes only the target section and leaves frontmatter + sibling sections byte-identical; (c) atomic write leaves no `.tmp` on success and original intact if write throws (simulate via a store seam).

**Step 3 — Implement:** `WriteAtomicAsync` = write to `path + ".tmp"`, then `File.Move(tmp, path, overwrite: true)`. `SpliceSectionAsync` = `Read` → find section by slug → `string.Concat(RawText[..BodyStart], newBody, RawText[BodyEnd..])` → `WriteAtomicAsync`. Root defaults via a `VaultPathProvider` (Task 1.3).

**Step 5 — Commit:** `feat(vault): atomic vault store with section splice`.

### Task 1.3: Vault path provider + DI

**Files:**
- Create: `Pia.Wpf/src/Pia.Infrastructure/Vault/VaultPathProvider.cs` (default `%LOCALAPPDATA%\Pia\Vault`, override from settings)
- Modify: `Pia.Wpf/src/Pia.Wpf/Bootstrapper.cs` (~line 163, near `SqliteContext` registration) — register `IVaultStore`, `MarkdownVaultParser`, `VaultPathProvider` as singletons.
- Test: `VaultPathProviderTests` — default path + settings override.

**Commit:** `feat(vault): vault path provider + DI registration`.

## Mac track (mirror of 1.1–1.3 in Swift)

### Task 1.4: Add swift-markdown + Yams

**Files:** Modify `Pia.Mac/Packages/PiaKit/Package.swift` deps:
```swift
.package(url: "https://github.com/apple/swift-markdown.git", from: "0.4.0"),
.package(url: "https://github.com/jpsim/Yams.git", from: "5.1.0"),
```
Add `Markdown` and `Yams` to the `PiaKit` target dependencies. **Commit:** `chore(piakit): add swift-markdown + Yams`.

### Task 1.5: Swift parser + model + store

**Files:**
- Create: `Packages/PiaKit/Sources/PiaKit/Vault/VaultDocument.swift` (mirror the record model; `struct VaultDocument`, `struct VaultSection`).
- Create: `Packages/PiaKit/Sources/PiaKit/Vault/MarkdownVaultParser.swift`.
- Create: `Packages/PiaKit/Sources/PiaKit/Vault/VaultStore.swift` (actor; `FileManager` + atomic `replaceItemAt`).
- Test: `Packages/PiaKit/Tests/PiaKitTests/Vault/MarkdownVaultParserTests.swift`, `VaultStoreTests.swift` (Swift Testing; temp dir per test like `MemoryServiceTests`).

**Test parity:** port the three parser tests and the splice/atomic tests verbatim (same fixtures, same expected slugs) to guarantee cross-platform identical parsing — this is how the Phase 0 contract is enforced in code.

Use `Markdown.Document(parsing:)` to find heading nodes (level 2) and `SourceRange` for byte offsets; parse frontmatter with `Yams.load`.

**Run:** `cd Packages/PiaKit && swift test --filter Vault`. **Commit:** `feat(piakit-vault): parser, model, atomic store`.

### Task 1.6: Wire into DependencyContainer

**Files:** Modify `Pia.Mac/Pia.Mac/DependencyContainer.swift` (~line 184, near `LiveMemoryService`) — instantiate `VaultStore` + parser, store as properties. **Commit:** `feat(piakit-vault): DI wiring`.

---

# Phase 2 — Index & recall  [WPF ‖ Mac]  (depends: P1)

**Goal:** Turn the DB into a disposable per-section index fed by a debounced file-watcher, and expose `recall` over the whole vault.

## WPF track

### Task 2.1: Replace the Memories schema with a chunk index

**Files:**
- Modify: `Pia.Wpf/src/Pia.Infrastructure/SqliteContext.cs` (lines 74–87 schema; 556–586 FTS) — add a new `Chunks` table + `ChunksFts`, keep `Memories` only until migration (Phase 4) is done, then drop in Task 4.x.

**New schema:**
```sql
CREATE TABLE IF NOT EXISTS Chunks (
    FilePath TEXT NOT NULL,
    Heading  TEXT NOT NULL,
    Slug     TEXT NOT NULL,
    ContentHash TEXT NOT NULL,
    Embedding BLOB,
    IndexedAt TEXT NOT NULL,
    PRIMARY KEY (FilePath, Slug)
);
CREATE INDEX IF NOT EXISTS IX_Chunks_FilePath ON Chunks(FilePath);
CREATE VIRTUAL TABLE IF NOT EXISTS ChunksFts USING fts5(
    FilePath UNINDEXED, Heading, Body, content='', contentless_delete=1);
```
(Contentless FTS so we don't duplicate bodies; we store bodies in FTS only.)

**Test:** `SqliteContextTests` — schema creates; insert+query a chunk row.

**Commit:** `feat(index): chunk index schema`.

### Task 2.2: VaultIndexer — (re)build from files

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/VaultIndexer.cs` + `IVaultIndexer.cs`
- Test: `Pia.Wpf/tests/Pia.Wpf.Tests/Vault/VaultIndexerTests.cs`

**Interface & behavior:**
```csharp
public interface IVaultIndexer
{
    Task RebuildAllAsync();                       // C3: wipe Chunks, walk vault, index every section
    Task IndexFileAsync(string relativePath);     // upsert chunks for one file, diff by ContentHash
    Task RemoveFileAsync(string relativePath);
}
```
Reference logic for `IndexFileAsync`: parse the file; for each section compute `ContentHash = SHA256(Heading + "\n" + Body)`; if an existing chunk row has the same hash, skip; else (re)embed via `IEmbeddingService.GenerateEmbeddingAsync($"{Heading}\n{Body}")`, upsert the row + FTS. Delete chunk rows for sections no longer present.

**Tests (use a real temp SQLite + a `StubEmbeddingService` returning a fixed vector):**
- `RebuildAllAsync` over a 2-file vault yields N chunk rows = total sections.
- Re-indexing an unchanged file does **not** call the embedding service (assert via NSubstitute `DidNotReceive`).
- Editing one section re-embeds only that section.
- `RemoveFileAsync` drops its rows.

**Commit:** `feat(index): vault indexer with content-hash incremental embedding`.

### Task 2.3: File-watcher → indexer

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/VaultWatcher.cs` (wraps `System.IO.FileSystemWatcher`, recursive, `*.md`, 300 ms debounce per path).
- Modify: `Bootstrapper.cs` — register + start on app init (after vault path known).
- Test: `VaultWatcherTests` — write a file → within debounce window `IVaultIndexer.IndexFileAsync` is invoked for it; delete → `RemoveFileAsync`. Use a temp dir and a real watcher with a short await; mock the indexer.

**Note:** Pia's own writes flow through the same watcher (no special-casing). Guard against re-indexing a file Pia just wrote by comparing content hashes (idempotent — IndexFileAsync already no-ops unchanged sections).

**Commit:** `feat(index): file-watcher feeding the indexer`.

### Task 2.4: `recall` over the whole vault

**Files:**
- Modify: `Pia.Wpf/src/Pia.Wpf/Services/MemoryService.cs` — repoint `HybridSearchAsync` (line 335) at the `Chunks` table; add `Task<IReadOnlyList<RecallHit>> RecallAsync(string query, int topK = 10)`.
- Create: `RecallHit` record `(string FilePath, string Heading, string Snippet, float Score)`.
- Test: `RecallTests` — seed a vault + index; query returns ranked `file#heading` hits from both `/memory/` and a user file outside it (proves whole-vault scope, C-design).

**Keep the existing tiered scoring** (LIKE 0.6 / FTS 0.7 / fuzzy / vector 0.8) but operate on chunk rows; join back to files for snippets. **Commit:** `feat(recall): whole-vault hybrid recall over chunks`.

## Mac track (mirror 2.1–2.4)

### Task 2.5–2.8: GRDB chunk index, indexer, watcher, recall

**Files:**
- Modify: `Packages/PiaKit/Sources/PiaKit/Database/DatabaseContext.swift` — add `chunks` table + `chunksFts` (mirror 2.1) in a new migration step.
- Create: `Packages/PiaKit/Sources/PiaKit/Vault/VaultIndexer.swift` (actor; mirror 2.2; reuse `LiveEmbeddingService.floatsToData`).
- Create: `Packages/PiaKit/Sources/PiaKit/Vault/VaultWatcher.swift` — use `DispatchSource.makeFileSystemObjectSource` on a directory FD (or a small recursive monitor); 300 ms debounce.
- Modify: `Packages/PiaKit/Sources/PiaKit/Services/Protocols/MemoryService.swift` + `LiveMemoryService.swift` — add `recall(query:topK:)`, repoint `hybridSearch` at `chunks`.
- Tests: mirror the four WPF test specs in Swift Testing with temp GRDB + `StubEmbeddingService`.

**Run:** `swift test --filter Vault`. **Commits:** one per task, mirroring WPF messages.

---

# Phase 3 — Write path: deterministic section upsert  [WPF ‖ Mac]  (depends: P1, P2)

**Goal:** The three-tool surface (`recall`, `remember`, `forget`) with dedup enforced in code. This is the fix for pain point #1.

## Reference algorithm (C# reference; Swift mirrors)

```csharp
public enum UpsertBand { Edit, Ambiguous, Create }

public sealed record UpsertResolution(
    UpsertBand Band, string? MatchedSlug, IReadOnlyList<string> Candidates);

// In SectionUpsertService
public async Task<UpsertResolution> ResolveAsync(VaultDocument doc, string subject, string content)
{
    var subjEmb = await _embeddings.GenerateEmbeddingAsync($"{subject}\n{content}");
    string? best = null; double bestScore = 0; var candidates = new List<(string slug,double s)>();
    foreach (var s in doc.Sections)
    {
        var lexical = JaroWinkler.Similarity(subject, s.Heading);   // existing fuzzy matcher
        var vector  = Cosine(subjEmb, await EmbeddingFor(doc, s));  // from Chunks index
        var score   = Math.Max(lexical, vector);
        candidates.Add((s.Slug, score));
        if (score > bestScore) { bestScore = score; best = s.Slug; }
    }
    if (bestScore >= 0.85) return new(UpsertBand.Edit, best, []);
    if (bestScore >= 0.60) return new(UpsertBand.Ambiguous, null,
        candidates.Where(c => c.s >= 0.60).OrderByDescending(c => c.s).Select(c => c.slug).ToList());
    return new(UpsertBand.Create, null, []);
}
```

**Field-level body merge** (deterministic for `- key: value` bullets): parse existing body bullets into an ordered map; apply new bullets as upserts (replace value for an existing key, append new keys); leave non-bullet prose untouched. For prose-only sections in the `Edit` band, hand *only that section* to the model to rewrite (bounded — never the whole doc).

## WPF track

### Task 3.1: SectionUpsertService (resolution + merge)

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/SectionUpsertService.cs` + interface.
- Test: `SectionUpsertServiceTests`.

**Failing tests:**
- Resolve returns `Edit` when subject ≈ existing heading (seed `## John Smith`, subject "John S." with a stub embedding that makes cosine high) → matched slug `john-smith`.
- Returns `Ambiguous` with candidates in the 0.60–0.85 band.
- Returns `Create` when nothing is close.
- Bullet merge: existing `- email: a@x` + new `- email: b@x, - phone: 5` → `email` replaced, `phone` appended, ordering stable.
- Prose preserved when merging bullets.

**Commit:** `feat(write): deterministic section upsert resolution + field merge`.

### Task 3.2: Rewrite MemoryToolHandler to {recall, remember, forget}

**Files:**
- Modify: `Pia.Wpf/src/Pia.Wpf/Services/MemoryToolHandler.cs` (lines 35–76 tool list; dispatch 79–116). Remove `create_object`/`update_object`/`append_to_list`/`merge_memories`/`find_duplicates`. Add:
  - `recall(query)` → `MemoryService.RecallAsync`, returns hits (immediate).
  - `remember(type, subject, content)` → resolve via `SectionUpsertService`; on `Edit`/`Create` produce a `MemoryToolCall` pending-action (keeps the existing confirmation-card UX); on `Ambiguous` return the candidate list to the model (no write) so it re-calls with a disambiguated subject.
  - `forget(ref)` → delete section (splice out) or file.
- Modify: `Pia.Wpf/src/Pia.Wpf/Services/MemoryService.cs` — add `RememberAsync(type, subject, content)` and `ForgetAsync(ref)` that operate via `IVaultStore.SpliceSectionAsync` / file create. **Retire** the JSON CRUD methods (`CreateObjectAsync`, `UpdateObjectAsync`, `AppendToListAsync`, etc.) once callers are migrated.
- Modify: `IMemoryService.cs`, `IMemoryToolHandler.cs`.
- Test: `tests/Pia.Wpf.Tests/Integration/MemoryToolIntegrationTests.cs` — replace the old `RememberName_ShouldQueryThenCreate` expectation: assert that two `remember` calls for the same subject produce **one** section, not two (dedup proof). This test can run without an API key by calling the handler directly with a stub model.

**Commit:** `feat(write): replace memory tools with recall/remember/forget`.

### Task 3.3: Update assistant prompt + action cards

**Files:**
- Modify: `Pia.Wpf/src/Pia.Wpf/Services/AssistantPromptComposer.cs` — update the memory tool guidance text to describe `remember`'s dedup semantics; remove references to retired tools.
- Modify: action-card localization strings (`Tool_Memory_*`) for the new verbs.
- Test: composer test asserting the tool list now contains exactly `recall`, `remember`, `forget`.

**Commit:** `feat(write): assistant prompt + cards for new memory tools`.

## Mac track (mirror 3.1–3.3)

### Task 3.4–3.6

**Files:**
- Create: `Packages/PiaKit/Sources/PiaKit/Vault/SectionUpsertService.swift` (mirror 3.1; reuse the existing Jaro-Winkler + cosine in `LiveMemoryService`).
- Modify: `Packages/PiaKit/Sources/PiaKit/Services/ToolHandlers/MemoryToolHandler.swift` (toolDefinitions 57–161; dispatch 169–214) → `recall`/`remember`/`forget`; keep the `.pending`/`.immediate` `ToolCallResult` pattern and the post-execute embedding refresh (move it to fire via the watcher+indexer instead — the indexer now owns embeddings, so `executePendingAction` no longer needs to embed; simplify).
- Modify: `Packages/PiaKit/Sources/PiaKit/Services/Protocols/MemoryService.swift` + `LiveMemoryService.swift` accordingly. Drop the Mac-only `schema` param paths (no longer meaningful with markdown bodies).
- Modify: `Pia.Mac/Pia.Mac/Features/Assistant/AssistantViewModel.swift` (tool aggregation 530–545; dispatch 943–1073) for the new tool names.
- Tests: mirror 3.1/3.2 specs in Swift Testing, including the **dedup proof** test.

**Commits:** mirror WPF.

---

# Phase 4 — Migration: JSON → vault  [WPF ‖ Mac]  (depends: P1–P3)

**Goal:** One-time, reversible conversion that also collapses existing duplicates, idempotent because it writes through `remember`.

## WPF track

### Task 4.1: MemoryObject → markdown renderer

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Migration/MemoryJsonRenderer.cs`
- Test: `MemoryJsonRendererTests`

**Behavior (per design):** `personal_profile`→`profile.md` bullets; `contact_list` array→`contacts.md` one `## <name>` per entry; `preference`→`preferences.md`; `note`/`project`→file per item; `skill`/`context`→`note` (C6). Arbitrary nested JSON → nested bullets; irregular → fenced ` ```json `. **Lossless** — a round-trip test asserts every JSON leaf value appears in the rendered markdown.

**Commit:** `feat(migrate): memory JSON → markdown renderer`.

### Task 4.2: MigrationRunner (dedup-on-write, archive, guard)

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Migration/VaultMigrationRunner.cs`
- Test: `VaultMigrationRunnerTests`

**Behavior:**
1. Read all `MemoryObject` rows from the (old) `Memories` table.
2. For each, render and **write through `RememberAsync`** (so near-duplicates merge via Phase 3 resolution rather than producing duplicate sections).
3. Archive each original JSON to `/memory/.archive/<id>.json`; leave the old `Memories` table intact.
4. Write a `vaultVersion` marker (settings/sync state). On startup, skip migration if the marker is set or the vault is already populated.
5. After success, `RebuildAllAsync` the index, then (separate task) drop the `Memories` table.

**Tests:** seed two near-identical `personal_profile` rows → after migration, `profile.md` has merged facts, `.archive/` has both originals, marker set; running again is a no-op (idempotent). Seed an irregular-JSON row → renders losslessly.

**Commit:** `feat(migrate): vault migration runner with dedup + archive + guard`.

### Task 4.3: Drop legacy Memories table + retire MemoryObject JSON paths

**Files:** Modify `SqliteContext.cs` (gate the old table behind "drop after migration"); remove now-dead JSON CRUD in `MemoryService`. Test: schema after migration has no `Memories`/`MemoriesFts`. **Commit:** `chore(migrate): drop legacy memory tables`.

## Mac track (mirror 4.1–4.3)

### Task 4.4–4.6

Mirror in `Packages/PiaKit/Sources/PiaKit/Migration/` with Swift Testing; the GRDB `memories` table is the source rows; `vaultVersion` lives in settings. Cross-device guard: a device that pulls an already-populated vault via sync must **not** re-migrate (check marker + presence). **Commits:** mirror WPF.

---

# Phase 5 — Sync & E2EE  [Server ‖ WPF ‖ Mac]  (depends: P0; clients also P1)

**Goal:** Sync the vault as file-keyed encrypted records; reconcile with **client-side section-aware 3-way merge**; server stays zero-knowledge LWW.

## Server track (independent — can start after P0)

### Task 5.1: Repurpose the sync payload for files

**Decision (from recon):** keep `ServerMemory` composite key `(Id, UserId)` and the whole sync algorithm. Do **not** make path a server key (C5). The encrypted payload's inner object changes from `{Type, Label, Data}` to `{ path, content }`. For the **non-E2EE** path only, add a nullable plaintext `Path` column so plaintext sync still round-trips.

**Files:**
- Modify: `Pia/src/Pia.Server/Models/ServerMemory.cs` — add `public string? Path { get; set; }`.
- Modify: `Pia/src/Pia.Server/Data/PiaDbContext.cs` (lines 137–149) — `entity.Property(e => e.Path).HasMaxLength(1024);`.
- Modify: `Pia/lib/Pia.Wpf/src/Pia.Shared/Models/SyncMemory.cs` — add `public string? Path { get; set; }` (kept null/omitted when E2EE active; path then lives inside `EncryptedPayload`).
- Migration: `dotnet ef migrations add AddPathToMemory -p src/Pia.Server` (nullable, no default).
- Test: `Pia/tests/Pia.Server.Tests/Sync/` — push a `SyncMemory` with `Path` + `Data` (markdown), pull it back, assert round-trip; push an E2EE record (Path null, `EncryptedPayload`/`WrappedDek` set), assert server stores+returns opaquely and never populates `Path`.

**Commit (server):** `feat(sync): path-aware memory sync payload`.

**Note:** the server change is additive and backward-compatible; old clients keep working. The post-quantum migration is unaffected (it touches key wrapping, not payloads).

## Client tracks — MergeEngine (WPF reference; Mac mirrors)

### Task 5.2 (WPF): Section-aware 3-way merge engine

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Sync/SectionMergeEngine.cs`
- Test: `SectionMergeEngineTests`

**Reference algorithm:**
```csharp
public string Merge(VaultDocument @base, VaultDocument local, VaultDocument remote)
{
    var b = @base.Sections.ToDictionary(s => s.Slug);
    var l = local.Sections.ToDictionary(s => s.Slug);
    var r = remote.Sections.ToDictionary(s => s.Slug);
    var result = new List<(string slug, string text)>();

    foreach (var slug in OrderedUnion(@base, local, remote))   // base order, then new-local, then new-remote
    {
        b.TryGetValue(slug, out var bs); l.TryGetValue(slug, out var ls); r.TryGetValue(slug, out var rs);
        var lb = ls?.Body; var rb = rs?.Body; var bb = bs?.Body;

        if (ls is null && rs is null) continue;                 // deleted on both
        if (lb == rb) { result.Add((slug, ls!.Body)); continue; }       // identical (incl. both-add-same)
        if (lb == bb && rs is not null) { result.Add((slug, rb!)); continue; } // only remote changed
        if (rb == bb && ls is not null) { result.Add((slug, lb!)); continue; } // only local changed
        if ((ls is null) ^ (rs is null))                        // edit/delete conflict → keep the edit, flag
            { result.Add((slug, (ls ?? rs)!.Body) ); /* log conflict */ continue; }
        result.Add((slug, ConflictMarker(lb!, rb!)));           // both changed same section
    }
    return Reassemble(local.Frontmatter /*newer updated wins*/, local.Preamble, result);
}
```
`ConflictMarker` = git-style `<<<<<<< local … ======= … >>>>>>> remote` inside the section body (spec §10).

**Failing tests:**
- Disjoint edits (local edits John, remote edits Alice) auto-merge → both edits present, no markers.
- Same-section concurrent edit → conflict marker present.
- Add on one side only → added.
- Delete on one side, untouched on other → deleted.

**Commit:** `feat(sync): section-aware 3-way merge engine`.

### Task 5.3 (WPF): Wire merge into the sync client

**Files:**
- Modify: `Pia.Wpf/src/Pia.Wpf/Services/SyncMapper.cs` (lines 380–422 `ToSyncMemory`/`FromSyncMemory`) — encrypt/decrypt `{ path, content }` instead of `{Type, Label, Data}`; reuse `IE2EEService.EncryptRecord/DecryptRecord` unchanged.
- Modify: `Pia.Wpf/src/Pia.Wpf/Services/SyncClientService.cs` — on pull, before writing a remote file that has local unsynced edits, fetch the stored **base** (last-synced copy), run `SectionMergeEngine.Merge(base, local, remote)`, write the result via `IVaultStore`, update the base. Store the base copy under `%LOCALAPPDATA%\Pia\SyncBase\<id>.md`.
- Create: `SyncBaseStore.cs` (per-file last-synced snapshot).
- Test: `SyncClientService` merge-on-pull test with a stubbed server payload.

**Commit:** `feat(sync): client-side merge-on-pull with base snapshots`.

## Mac track

### Task 5.4–5.5

Mirror 5.2/5.3 in `Packages/PiaKit/Sources/PiaKit/Sync/SectionMergeEngine.swift` and `LiveSyncClientService.swift` (lines 113–205 `syncNow`; encrypt/decrypt via `LiveE2EEService.encryptRecord/decryptRecord`). Base snapshots under Application Support. Port the four merge tests. **Commits:** mirror WPF.

---

# Phase 6 — Wiki layer  [WPF ‖ Mac]  (depends: P1–P3)

**Goal:** Add the Karpathy three-layer structure: `sources/` (raw), `topics/` (compiled), and the `index.md` / `log.md` / `AGENTS.md` housekeeping files.

## WPF track

### Task 6.1: index.md & log.md maintainers

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Wiki/VaultIndexFile.cs` — `Task UpsertEntryAsync(string path, string summary)` / `RemoveEntryAsync(path)`; rewrites the grouped catalog deterministically (spec §8).
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Wiki/VaultLog.cs` — `Task AppendAsync(string op, string description)` (spec §9; append-only, atomic).
- Tests: catalog stays sorted/grouped after upserts; log lines are grep-parseable and append-only.

**Commit:** `feat(wiki): index.md + log.md maintainers`.

### Task 6.2: AGENTS.md (the schema doc) + sources/ ingestion entry points

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Wiki/VaultSchemaDoc.cs` — writes a default `memory/AGENTS.md` describing conventions (seeded from the Phase 0 spec, human-editable thereafter; not overwritten if present).
- Modify: vault layout creation to ensure `sources/` exists; `recall` already indexes it (Phase 2 covers whole-vault scope).
- Test: AGENTS.md created on first run, preserved on subsequent runs.

**Commit:** `feat(wiki): AGENTS.md schema doc + sources/ scaffolding`.

## Mac track

### Task 6.3–6.4: mirror 6.1–6.2 in `Packages/PiaKit/Sources/PiaKit/Wiki/`. **Commits:** mirror WPF.

---

# Phase 7 — Ingest & Lint  [WPF ‖ Mac]  (depends: P6)

**Goal:** The fan-out Ingest compiler and the Lint coherence pass, both as background tasks.

## Reference orchestration (WPF reference; Mac mirrors)

```csharp
// IngestPipeline.RunAsync(sourcePath)
1. content   = ReadSource(sourcePath);                       // sources/ is immutable
2. summary   = await _ai.SummarizeAsync(content);            // model call
3. entities  = await _ai.ExtractEntitiesAsync(content);      // model call → [{subject, facts}]
4. foreach (e in entities)
       await _memory.RememberAsync("topic", e.Subject, e.Facts);  // Phase 3 upsert = dedup at page level
5. await _crosslinker.LinkAsync(touchedPages);               // add [[wikilinks]] among touched + related
6. foreach (p in touchedPages) await _indexFile.UpsertEntryAsync(p, oneLineSummary(p));
7. await _log.AppendAsync("ingest", $"{sourceName} -> {string.Join(\", \", touchedPages)}");
   // each topic page records sources: front-matter (provenance)
```

```csharp
// Linter.RunAsync() → LintReport (writes findings to log.md; auto-fixes safe items)
- Contradictions: group chunks by entity; flag conflicting `- key:` values across pages.
- Stale: topic pages whose `sources:` file is missing/changed (hash) → flag.
- Orphans: pages with no inbound [[link]] (scan all bodies) → flag.
- MissingXref: a body mentions an entity that has a page but isn't linked → AUTO-FIX (insert link).
- Duplicates: cosine ≥ 0.9 across topic pages → merge (originals to .archive/).
- GapPages: [[foo]] with no foo.md → flag.
```

## WPF track

### Task 7.1: IngestPipeline (background task)

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Wiki/IngestPipeline.cs` + `ICrosslinker.cs`.
- Modify: assistant tool surface — add an `ingest(source_ref)` tool (long-running → returns a job handle; progress surfaced like other background jobs).
- Test: `IngestPipelineTests` with a stub AI client returning fixed entities → asserts: topic pages created via `remember` (dedup honored on re-ingest of the same source), `index.md` updated, `log.md` appended, `sources:` provenance written.

**Commit:** `feat(wiki): ingest pipeline`.

### Task 7.2: Linter (background task)

**Files:**
- Create: `Pia.Wpf/src/Pia.Wpf/Services/Wiki/Linter.cs`.
- Test: `LinterTests` — seed contradictions/orphans/gap links/duplicates; assert each is detected; assert missing-xref + duplicate are auto-fixed and others flagged in `log.md`.
- Schedule: run after N ingests and on-demand; reuse the existing scheduled-job infrastructure.

**Commit:** `feat(wiki): lint coherence pass`.

## Mac track

### Task 7.3–7.4: mirror 7.1–7.2 in `Packages/PiaKit/Sources/PiaKit/Wiki/`, surfaced as background tasks in `AssistantViewModel`. **Commits:** mirror WPF.

---

## Final verification (all repos)

- WPF/server: `dotnet test` green on a Windows/CI runner; `dotnet build -c Release` clean.
- Mac: `swift test` green; `xcodebuild -scheme Pia` clean.
- Manual smoke (per repo): create memories via chat → confirm `.md` files appear and are valid Obsidian notes; edit a note in Obsidian → confirm `recall` reflects it; trigger sync on two devices with offline edits → confirm section-aware merge; ingest a sample PDF → confirm `topics/` pages + `index.md` + `log.md`; run lint → confirm report.
- Use `superpowers:verification-before-completion` before claiming done; do not assert green without the runner output.

## Risks & guardrails (carry into execution)

- **No silent truncation** — log skipped files (size cap / globs) per `Pia.Logging` privacy rules (hash paths, never raw content).
- **Privacy logging** — vault paths and memory content are sensitive: use `SensitiveDebug` / `SafeUrl` for any path or body in logs.
- **Reversibility** — keep `.archive/` and the legacy table until Phase 4 is validated on each platform before Task 4.3/4.6 drop them.
- **Embedding model divergence (WPF vs Mac)** — acceptable (C4); never compare vectors across devices; each device rebuilds its own index.
- **Verify WPF MemoryObject path** — recon reported both `src/Pia.Wpf/Models/MemoryObject.cs` and `src/Pia.Models/MemoryObject.cs`; confirm the real location before editing.

## Suggested workflow shape (for ultracode execution)

- One workflow run per phase. Phase 0 first (single agent). Phases 1–4, 6–7: fan out a WPF agent and a Mac agent in parallel (worktree isolation), each executing that phase's tasks task-by-task with a review/verify step between tasks. Phase 5: three parallel tracks (server, WPF, Mac), server may run earlier.
- Gate each phase on the prior phase's tests passing on the appropriate runner. Use an adversarial verify step on the dedup proof (Phase 3) and the merge tests (Phase 5) — these are the correctness-critical pieces.
