# Auto-Ingest for Vault Sources — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fully automatic ingest of `sources/` documents with replace-per-source semantics, per `docs/superpowers/specs/2026-07-07-auto-ingest-sources-design.md`.

**Architecture:** Three layers. (1) `IngestService` gains machine-managed `## Source: <ref>` sections in topic pages (replacing the merged-preamble writes) plus `RemoveContributionsAsync` — all page surgery lives here. (2) A new `IngestStateStore` (own SQLite connection, `COLLATE NOCASE` PK) records content hashes and touched pages for change detection. (3) A new `AutoIngestService` (`IIngestScheduler`) serializes ALL ingest work — sources watcher, startup reconcile, and the chat tool — through one queue, diffs touched-sets for shrink cleanup, and raises `IngestCompleted` for the Memory view.

**Tech Stack:** .NET 10 WPF, Microsoft.Data.Sqlite, xunit v3 (MTP runner — `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "<FullClassName>"`), existing vault primitives (`IVaultStore`, `MarkdownVaultParser`, `VaultSlug`).

**Conventions that bite:**
- Namespaces are `Pia.*`, NOT `Pia.Wpf.*` (project renamed, namespaces kept).
- Repo `.cs` files are CRLF. If you create a file with the Write tool (LF), convert to CRLF before running raw-string byte-identical tests against it (none planned here, but keep files CRLF for consistency).
- Never log user-named content (source names, page paths, titles) above `SensitiveDebug` — see CLAUDE.md privacy rules.
- Test namespace for wiki tests is `Pia.Tests.Wiki` (see `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs`).
- The known-failing live-network tests live in `Pia.Wpf.Tests.Integration.Providers`; the gate is zero failures OUTSIDE that namespace: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`.

---

## Chunk 1: Page surgery + change-detection state (pure logic, no scheduler)

### Task 1: CRLF-tolerant provenance parsing

`SourcesProvenance.ReadSourceRefs` and `IngestService.EnsureSourceInFrontmatterAsync` require LF-only text (`"---\n"` at index 0). A CRLF page silently loses provenance. Normalize at the read edge.

**Files:**
- Modify: `src/Pia.Wpf/Services/Wiki/SourcesProvenance.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/SourcesProvenanceTests.cs` (create)

- [ ] **Step 1.1: Write the failing tests**

Create `tests/Pia.Wpf.Tests/Wiki/SourcesProvenanceTests.cs`:

```csharp
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

public class SourcesProvenanceTests
{
    [Fact]
    public void ReadSourceRefs_parses_lf_frontmatter()
    {
        var raw = "---\ntype: topic\nsources: [sources/a.txt, sources/b.md]\n---\nBody.\n";
        var refs = SourcesProvenance.ReadSourceRefs(raw);
        Assert.Equal(["sources/a.txt", "sources/b.md"], refs);
    }

    [Fact]
    public void ReadSourceRefs_parses_crlf_frontmatter_identically()
    {
        var raw = "---\r\ntype: topic\r\nsources: [sources/a.txt, sources/b.md]\r\n---\r\nBody.\r\n";
        var refs = SourcesProvenance.ReadSourceRefs(raw);
        Assert.Equal(["sources/a.txt", "sources/b.md"], refs);
    }

    [Fact]
    public void ReadSourceRefs_returns_empty_without_frontmatter()
    {
        Assert.Empty(SourcesProvenance.ReadSourceRefs("No frontmatter here.\n"));
        Assert.Empty(SourcesProvenance.ReadSourceRefs("---\nunclosed\n"));
    }
}
```

- [ ] **Step 1.2: Run to verify the CRLF test fails**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.SourcesProvenanceTests"`
Expected: `ReadSourceRefs_parses_crlf_frontmatter_identically` FAILS (empty list); the LF test passes.

- [ ] **Step 1.3: Implement**

In `SourcesProvenance.ReadSourceRefs`, normalize before parsing (first line of the method):

```csharp
public static IReadOnlyList<string> ReadSourceRefs(string rawText)
{
    // Frontmatter written by Pia is LF, but externally-edited/synced pages may arrive CRLF —
    // provenance drives replace/removal now, so both must parse identically.
    rawText = rawText.Replace("\r\n", "\n");

    var open = rawText.IndexOf("---\n", StringComparison.Ordinal);
    // ... rest unchanged ...
```

- [ ] **Step 1.4: Run tests to verify they pass**

Same command. Expected: 3 PASS.

- [ ] **Step 1.5: Commit**

```bash
git add tests/Pia.Wpf.Tests/Wiki/SourcesProvenanceTests.cs src/Pia.Wpf/Services/Wiki/SourcesProvenance.cs
git commit -m "fix(ingest): CRLF-tolerant sources: frontmatter parsing"
```

(The frontmatter *maintainer* CRLF path is handled in Task 3 where that code is rewritten.)

---

### Task 2: Shared topic-page frontmatter helper

`IngestService` will create topic pages itself (it stops calling `RememberAsync`), so the frontmatter format must come from one place. Extract `MemoryService.BuildFrontmatter` into a static helper both use.

**Files:**
- Create: `src/Pia.Wpf/Infrastructure/Vault/VaultFrontmatter.cs`
- Modify: `src/Pia.Wpf/Services/MemoryService.cs` (delegate `BuildFrontmatter` to the helper)

- [ ] **Step 2.1: Create the helper**

Read `MemoryService.BuildFrontmatter` (around line 789) and its `TimestampFormat` const first; the helper must produce byte-identical output. Create `src/Pia.Wpf/Infrastructure/Vault/VaultFrontmatter.cs`:

```csharp
using System.Globalization;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// The canonical Pia-managed frontmatter block for freshly created vault records. Extracted from
/// MemoryService so IngestService can create topic pages with an identical header without taking a
/// dependency on the whole memory pipeline.
/// </summary>
public static class VaultFrontmatter
{
    // Value taken verbatim from MemoryService.TimestampFormat (line ~571) — verify before deleting
    // the original const there. MemoryService.BumpUpdatedAsync (~line 825) also uses it.
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public static string Build(string type, string title)
    {
        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               $"type: {type}\n" +
               $"title: {title}\n" +
               $"created: {now}\n" +
               $"updated: {now}\n" +
               "schemaVersion: 1\n" +
               "---\n";
    }
}
```

- [ ] **Step 2.2: Delegate MemoryService to it**

Replace the body of `MemoryService.BuildFrontmatter` with `=> VaultFrontmatter.Build(type, title);` (keep the private method so call sites don't churn). Remove the now-unused locals; keep `TimestampFormat` only if other MemoryService code uses it — if so, change those uses to `VaultFrontmatter.TimestampFormat` and delete the local const.

- [ ] **Step 2.3: Build + run the memory tests**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj` then
`dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.IngestServiceTests"` (exercises `MemoryService` page creation).
Expected: build clean, tests PASS (no behavior change).

- [ ] **Step 2.4: Commit**

```bash
git add src/Pia.Wpf/Infrastructure/Vault/VaultFrontmatter.cs src/Pia.Wpf/Services/MemoryService.cs
git commit -m "refactor(memory): extract VaultFrontmatter.Build for reuse by ingest"
```

---

### Task 3: Replace-per-source sections in IngestService

The core rewrite. Ingest writes each entity's facts into a `## Source: <sourceRef>` section per topic page (create page if absent, replace section if present, append if new), computes crosslinks INTO the section body up front, and gains `RemoveContributionsAsync`. `IMemoryService` dependency is dropped.

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IIngestService.cs`
- Modify: `src/Pia.Wpf/Services/Wiki/IngestService.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs`

- [ ] **Step 3.1: Extend the interface**

In `IIngestService.cs` add to the interface:

```csharp
/// <summary>
/// Remove everything <paramref name="sourceRef"/> contributed: its <c>## Source:</c> section and
/// its <c>sources:</c> frontmatter ref on every page in <paramref name="pages"/>; pages left with
/// no sections and a whitespace-only preamble are deleted (with their index entry). Missing pages
/// are skipped; pages without the section still get their frontmatter ref pruned (and the
/// empty-page check). Appends one <c>ingest</c> journal line when any pages were targeted.
/// </summary>
Task RemoveContributionsAsync(string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default);
```

- [ ] **Step 3.2: Write the failing tests**

In `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs`. Note: there is no runnable RED state for this task — the interface change in Step 3.1 already breaks the build until Step 3.3 lands, and these tests call `RemoveContributionsAsync`, which doesn't exist yet. Write them now anyway (they define the target behavior); the first green run is Step 3.5. Add:

```csharp
[Fact]
public async Task IngestAsync_writes_facts_into_a_source_section()
{
    var ingest = BuildIngest(new StubExtractor());
    await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
        TestContext.Current.CancellationToken);

    var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
    Assert.NotNull(acme);
    var section = Assert.Single(acme!.Sections);
    Assert.Equal("Source: sources/sample.txt", section.Heading);
    Assert.Contains("customer", section.Body);
}

[Fact]
public async Task Reingest_replaces_the_source_section_not_appends()
{
    var ingest = BuildIngest(new StubExtractor());
    await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
        TestContext.Current.CancellationToken);

    // v2 of the source: same entity, different fact.
    var ingest2 = BuildIngest(new FixedExtractor(
        new ExtractedEntity("Acme Corp", "- type: former customer")));
    await ingest2.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 9),
        TestContext.Current.CancellationToken);

    var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
    var section = Assert.Single(acme!.Sections.Where(s => s.Heading == "Source: sources/sample.txt"));
    Assert.Contains("former customer", section.Body);
    Assert.DoesNotContain("since: 2024", acme.RawText); // old fact replaced, not kept
}

[Fact]
public async Task Reingest_preserves_manual_preamble_and_foreign_sections()
{
    var ingest = BuildIngest(new StubExtractor());
    await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
        TestContext.Current.CancellationToken);

    // Simulate a manual remember (preamble) and another source's section. The created page reads
    // "---\n\n## Source: ..." (VaultFrontmatter.Build ends with "---\n", then the "\n" separator) —
    // the pattern below must match that exact shape or the Replace is a silent no-op.
    var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
    var withExtras = acme!.RawText.Replace("---\n\n## ", "---\n- manually remembered fact\n\n## ")
        + "\n## Source: sources/other.txt\n\n- from another source\n";
    Assert.Contains("manually remembered fact", withExtras); // guard: the pattern matched
    await _store.WriteAtomicAsync("memory/topics/acme-corp.md", withExtras);

    await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 9),
        TestContext.Current.CancellationToken);

    var after = await _store.ReadAsync("memory/topics/acme-corp.md");
    Assert.Contains("manually remembered fact", after!.RawText);
    Assert.Contains("from another source", after.RawText);
    Assert.Equal(2, after.Sections.Count(s => s.Heading.StartsWith("Source: ")));
}

[Fact]
public async Task RemoveContributionsAsync_removes_section_and_deletes_empty_pages()
{
    var ingest = BuildIngest(new StubExtractor());
    var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
        TestContext.Current.CancellationToken);

    await ingest.RemoveContributionsAsync("sources/sample.txt", result.TouchedPages,
        TestContext.Current.CancellationToken);

    // Pages had ONLY this source's section -> deleted.
    Assert.Null(await _store.ReadAsync("memory/topics/acme-corp.md"));
    Assert.Null(await _store.ReadAsync("memory/topics/john-smith.md"));
}

[Fact]
public async Task RemoveContributionsAsync_keeps_pages_with_other_content()
{
    var ingest = BuildIngest(new StubExtractor());
    var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
        TestContext.Current.CancellationToken);

    var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
    await _store.WriteAtomicAsync("memory/topics/acme-corp.md",
        acme!.RawText + "\n## Source: sources/other.txt\n\n- other fact\n");

    await ingest.RemoveContributionsAsync("sources/sample.txt", result.TouchedPages,
        TestContext.Current.CancellationToken);

    var after = await _store.ReadAsync("memory/topics/acme-corp.md");
    Assert.NotNull(after); // survives — another source still contributes
    Assert.DoesNotContain("Source: sources/sample.txt", after!.RawText);
    Assert.Contains("other fact", after.RawText);
    // Frontmatter ref pruned:
    Assert.DoesNotContain("sources/sample.txt",
        string.Join(",", SourcesProvenance.ReadSourceRefs(after.RawText)));
}
```

Add the helper extractor to the fixture:

```csharp
private sealed class FixedExtractor(params ExtractedEntity[] entities) : IIngestExtractor
{
    public Task<string> SummarizeAsync(string content, CancellationToken ct = default)
        => Task.FromResult("Fixed summary.");
    public Task<IReadOnlyList<ExtractedEntity>> ExtractEntitiesAsync(string content, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExtractedEntity>>(entities);
}
```

- [ ] **Step 3.3: Rewrite IngestService**

In `src/Pia.Wpf/Services/Wiki/IngestService.cs`:

1. **Ctor:** delete the `IMemoryService _memory` field/parameter (DI resolves the new shape automatically; test fixture updated in Step 3.4).
2. **Heading convention** (top of class):

```csharp
/// <summary>Heading prefix of the machine-managed per-source section in a topic page.</summary>
public const string SourceHeadingPrefix = "Source: ";

private static string SourceHeading(string sourceRef) => SourceHeadingPrefix + sourceRef;

private static bool IsSectionFor(VaultSection s, string sourceRef) =>
    s.Heading.Equals(SourceHeadingPrefix + sourceRef, StringComparison.OrdinalIgnoreCase);
```

3. **Fan-out (replaces the `RememberAsync` loop, steps 3–4 of the orchestration):** compute crosslinks up front from the entity set (the old post-hoc `CrosslinkAsync` file-append pass is deleted — links live inside the source section so replace/removal takes them along):

```csharp
var touched = new List<string>();
var firstFactBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var entity in entities)
{
    if (string.IsNullOrWhiteSpace(entity.Subject)) continue;

    var slug = VaultSlug.Slugify(entity.Subject);
    var path = $"memory/topics/{slug}.md";

    var body = new StringBuilder(NormalizeFactsToBullets(entity.Facts));
    foreach (var other in entities)
    {
        if (ReferenceEquals(other, entity) || string.IsNullOrWhiteSpace(other.Subject)) continue;
        var otherSlug = VaultSlug.Slugify(other.Subject);
        if (otherSlug == slug) continue;
        if (entity.Facts.Contains(other.Subject, StringComparison.OrdinalIgnoreCase))
            body.Append($"Related: [[topics/{otherSlug}]]\n");
    }

    await UpsertSourceSectionAsync(path, entity.Subject, sourceRef, body.ToString());
    if (!touched.Contains(path)) touched.Add(path);
    firstFactBySlug[slug] = FirstLine(entity.Facts);
}
```

`NormalizeFactsToBullets`: split on newlines (normalize `\r\n` first), trim, prefix `- ` when a non-empty line doesn't already start with `-`, join with `\n`, ensure trailing `\n`.

4. **Section upsert:**

```csharp
private async Task UpsertSourceSectionAsync(string path, string subject, string sourceRef, string body)
{
    var doc = await _store.ReadAsync(path);
    var sectionText = "## " + SourceHeading(sourceRef) + "\n\n" + body;

    if (doc is null)
    {
        await _store.WriteAtomicAsync(path, VaultFrontmatter.Build("topic", subject) + "\n" + sectionText);
        return;
    }

    var existing = doc.Sections.FirstOrDefault(s => IsSectionFor(s, sourceRef));
    if (existing is null)
    {
        var raw = doc.RawText;
        var sep = raw.EndsWith('\n') ? "\n" : "\n\n";
        await _store.WriteAtomicAsync(path, raw + sep + sectionText);
    }
    else
    {
        // Replace only the section body via the store's byte-range splice primitive. BodyStart is
        // the char right after the heading line's '\n', so the blank separator line belongs to the
        // body — prepend it to keep first-ingest and re-ingest formatting identical.
        await _store.SpliceSectionAsync(path, existing.Slug, "\n" + body);
    }
}
```

Note: `SpliceSectionAsync` targets the section *slug* (`VaultSlug.Slugify` of the heading). Two different refs slugifying identically on one page (e.g. `a b.md` vs `a-b.md`) would collide — accepted edge case; the heading-based `FirstOrDefault` above decides replace-vs-append, so the wrong-splice window requires both refs on the same page.

5. **Removal:**

```csharp
public async Task RemoveContributionsAsync(
    string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default)
{
    sourceRef = sourceRef.Replace('\\', '/'); // separator-tolerant, matching IngestAsync

    foreach (var path in pages)
    {
        ct.ThrowIfCancellationRequested();
        var doc = await _store.ReadAsync(path);
        if (doc is null) continue;

        var section = doc.Sections.FirstOrDefault(s => IsSectionFor(s, sourceRef));
        if (section is not null)
        {
            var raw = doc.RawText;
            var start = HeadingLineStart(raw, section.BodyStart);
            var rebuilt = raw[..start] + raw[section.BodyEnd..];
            await _store.WriteAtomicAsync(path, rebuilt);
            doc = await _store.ReadAsync(path); // re-parse for the emptiness check below
        }

        if (doc is not null)
        {
            await RemoveSourceFromFrontmatterAsync(path, sourceRef);
            doc = await _store.ReadAsync(path);
        }

        if (doc is not null && doc.Sections.Count == 0 && string.IsNullOrWhiteSpace(doc.Preamble))
        {
            await _store.DeleteAsync(path);
            await _index.RemoveEntryAsync(path);
            _logger.SensitiveDebug("Removed now-empty topic page {Path}", path);
        }
    }

    if (pages.Count > 0)
    {
        // Spec §5: removal journals a corresponding ingest log line, mirroring the ingest one.
        await _log.AppendAsync("ingest",
            "removed " + Path.GetFileName(sourceRef) + " -> " + string.Join(", ", TouchedTargets(pages)),
            DateOnly.FromDateTime(DateTime.Now));
    }

    _logger.LogInformation("Removed ingest contributions from {Count} page(s)", pages.Count);
    _logger.SensitiveDebug("Removed contributions of {Source}", sourceRef);
}
```

`HeadingLineStart(string raw, int bodyStart)`: copy the private helper from `MemoryService` (walks back from `BodyStart` past the heading line to the start of the heading line — see `MemoryService.HeadingLineStart` around line 750 for the exact arithmetic and edge cases).

6. **Frontmatter maintenance:** rework `EnsureSourceInFrontmatterAsync` into a shared core so add/remove don't duplicate the parse. Normalize `\r\n` → `\n` on the raw text FIRST (this is the maintainer half of the Task 1 CRLF hardening — the whole file is rewritten LF, which is Pia's native form). Add:

```csharp
private Task EnsureSourceInFrontmatterAsync(string path, string sourceRef) =>
    RewriteSourcesFrontmatterAsync(path, refs =>
    {
        if (!refs.Contains(sourceRef, StringComparer.OrdinalIgnoreCase)) refs.Add(sourceRef);
    });

private Task RemoveSourceFromFrontmatterAsync(string path, string sourceRef) =>
    RewriteSourcesFrontmatterAsync(path, refs =>
        refs.RemoveAll(r => r.Equals(sourceRef, StringComparison.OrdinalIgnoreCase)));
```

`RewriteSourcesFrontmatterAsync(path, Action<List<string>> mutate)` contains the existing open/close/`FindKeyValue`/`ParseFlowList`/`ReplaceKeyLine` logic with `raw = doc.RawText.Replace("\r\n", "\n");` as its first line, applies `mutate`, no-ops when the list is unchanged, and when the list becomes empty writes `sources: []` (keeps the line stable rather than removing the key).

7. **Keep** steps 5–7 of the orchestration (index upsert, journal line, frontmatter provenance) exactly as they are.

- [ ] **Step 3.4: Update the test fixture**

`BuildIngest` loses the memory dependency:

```csharp
private IngestService BuildIngest(IIngestExtractor extractor)
    => new(extractor, _store, _index, _log, _embeddings, NullLogger<IngestService>.Instance);
```

Keep `BuildMemory()` only if other tests in the file still use it; otherwise delete it and any now-unused fixture fields (`_upsert`, `_deleteTracker` — check first).

- [ ] **Step 3.5: Run the ingest test class**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.IngestServiceTests"`
Expected: ALL PASS — including the pre-existing tests (`IngestAsync_creates_a_topic_page_per_entity` asserts `Contains("customer", acme.RawText)` which still holds; if any pre-existing test asserts preamble-resident facts, update it to the section shape and say so in the commit).

- [ ] **Step 3.6: Build the app + run the full wiki namespace**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj` and
`dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-namespace "Pia.Tests.Wiki"`
Expected: clean build (Bootstrapper needs no change — DI resolves the smaller ctor), all wiki tests PASS.

- [ ] **Step 3.7: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IIngestService.cs src/Pia.Wpf/Services/Wiki/IngestService.cs tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs
git commit -m "feat(ingest): replace-per-source sections + RemoveContributionsAsync"
```

---

### Task 4: IngestStateStore

Change-detection state on a dedicated connection (never the shared `SqliteContext.GetConnection()` one — that connection is the recall indexer's single-threaded property).

**Files:**
- Create: `src/Pia.Wpf/Services/Wiki/IngestStateStore.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/IngestStateStoreTests.cs` (create)

- [ ] **Step 4.1: Write the failing tests**

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

public class IngestStateStoreTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly IngestStateStore _store;

    public IngestStateStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-ingeststate-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _store = new IngestStateStore($"Data Source={Path.Combine(_tmpDir, "history.db")}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Upsert_then_get_roundtrips()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.txt", "HASH1", IngestOutcome.Success, ["memory/topics/x.md"], DateTimeOffset.UtcNow));

        var entry = await _store.GetAsync("sources/a.txt");
        Assert.NotNull(entry);
        Assert.Equal("HASH1", entry!.ContentHash);
        Assert.Equal(IngestOutcome.Success, entry.Outcome);
        Assert.Equal(["memory/topics/x.md"], entry.TouchedPages);
    }

    [Fact]
    public async Task SourceRef_lookup_is_case_insensitive()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/A.txt", "HASH1", IngestOutcome.Success, [], DateTimeOffset.UtcNow));

        Assert.NotNull(await _store.GetAsync("sources/a.txt"));

        // Case-variant upsert hits the SAME row, not a second one.
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.TXT", "HASH2", IngestOutcome.Success, [], DateTimeOffset.UtcNow));
        var all = await _store.ListAsync();
        var entry = Assert.Single(all);
        Assert.Equal("HASH2", entry.ContentHash);
    }

    [Fact]
    public async Task Delete_removes_the_row()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.txt", "HASH1", IngestOutcome.NoEntities, [], DateTimeOffset.UtcNow));
        await _store.DeleteAsync("sources/a.txt");
        Assert.Null(await _store.GetAsync("sources/a.txt"));
        Assert.Empty(await _store.ListAsync());
    }
}
```

- [ ] **Step 4.2: Run to verify compile failure**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.IngestStateStoreTests"`
Expected: compile error — `IngestStateStore` does not exist.

- [ ] **Step 4.3: Implement**

Create `src/Pia.Wpf/Services/Wiki/IngestStateStore.cs`:

```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>What was last ingested for one source: content hash, outcome, and touched pages.</summary>
public sealed record IngestStateEntry(
    string SourceRef,
    string ContentHash,
    IngestOutcome Outcome,
    IReadOnlyList<string> TouchedPages,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Change-detection state for auto-ingest, in history.db. Opens a dedicated connection per
/// operation (constructed from <see cref="Pia.Infrastructure.SqliteContext.ConnectionString"/> —
/// the documented pattern for background-thread writers) so it NEVER touches the shared
/// single-threaded connection the recall indexer owns. SourceRef is COLLATE NOCASE: Windows paths
/// are case-insensitive, so case-variant rename events must hit the same row. Local-only, like the
/// chunk index — a second device re-ingests, which replace-per-source semantics make convergent.
/// </summary>
public sealed class IngestStateStore
{
    private readonly string _connectionString;
    private volatile bool _schemaEnsured;

    public IngestStateStore(string connectionString) => _connectionString = connectionString;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_schemaEnsured)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS IngestState (
                    SourceRef TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    ContentHash TEXT NOT NULL,
                    Outcome TEXT NOT NULL,
                    TouchedPages TEXT NOT NULL DEFAULT '[]',
                    UpdatedAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _schemaEnsured = true;
        }
        return connection;
    }

    public async Task<IngestStateEntry?> GetAsync(string sourceRef)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SourceRef, ContentHash, Outcome, TouchedPages, UpdatedAt FROM IngestState WHERE SourceRef = @r";
        command.Parameters.AddWithValue("@r", sourceRef);
        using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    public async Task<IReadOnlyList<IngestStateEntry>> ListAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SourceRef, ContentHash, Outcome, TouchedPages, UpdatedAt FROM IngestState";
        var entries = new List<IngestStateEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) entries.Add(ReadEntry(reader));
        return entries;
    }

    public async Task UpsertAsync(IngestStateEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO IngestState (SourceRef, ContentHash, Outcome, TouchedPages, UpdatedAt)
            VALUES (@r, @h, @o, @p, @u)
            ON CONFLICT(SourceRef) DO UPDATE SET
                ContentHash = excluded.ContentHash,
                Outcome = excluded.Outcome,
                TouchedPages = excluded.TouchedPages,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("@r", entry.SourceRef);
        command.Parameters.AddWithValue("@h", entry.ContentHash);
        command.Parameters.AddWithValue("@o", entry.Outcome.ToString());
        command.Parameters.AddWithValue("@p", JsonSerializer.Serialize(entry.TouchedPages));
        command.Parameters.AddWithValue("@u", entry.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string sourceRef)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM IngestState WHERE SourceRef = @r";
        command.Parameters.AddWithValue("@r", sourceRef);
        await command.ExecuteNonQueryAsync();
    }

    private static IngestStateEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Enum.TryParse<IngestOutcome>(reader.GetString(2), out var outcome) ? outcome : IngestOutcome.Success,
        JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
}
```

- [ ] **Step 4.4: Run tests to verify they pass**

Same command as 4.2. Expected: 3 PASS.

- [ ] **Step 4.5: Commit**

```bash
git add src/Pia.Wpf/Services/Wiki/IngestStateStore.cs tests/Pia.Wpf.Tests/Wiki/IngestStateStoreTests.cs
git commit -m "feat(ingest): IngestState change-detection store (dedicated connection)"
```

*(Spec deviation, intentional: the table is created lazily by the store on its own connection rather than in `SqliteContext.EnsureSchema` — the store must work against test databases the shared context never opens. Behavior is identical; note carried into the spec's status line in Task 11.)*

---

## Chunk 2: Scheduler, wiring, and UI refresh

### Task 5: IIngestScheduler + AutoIngestService

The serial pipeline: manual runs, auto runs (hash-gated), removals, reconcile scan, sources watcher, Stop/Restart, `IngestCompleted`.

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IIngestScheduler.cs`
- Create: `src/Pia.Wpf/Services/Wiki/AutoIngestService.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/AutoIngestServiceTests.cs` (create)

- [ ] **Step 5.1: Create the interface**

`src/Pia.Wpf/Services/Interfaces/IIngestScheduler.cs`:

```csharp
namespace Pia.Services.Interfaces;

/// <summary>
/// The single serial pipeline for ALL ingest work — the sources watcher, the startup reconcile,
/// and the chat <c>ingest</c> tool. One ingest is in flight at any time (each costs two LLM calls
/// and splices topic pages). <see cref="RunAsync"/> is the manual path: it always executes, even
/// when the content hash is unchanged. Automatic triggers hash-skip internally.
/// </summary>
public interface IIngestScheduler
{
    /// <summary>Queue an ingest of <paramref name="sourceRef"/> and await its result.</summary>
    Task<IngestResult> RunAsync(string sourceRef, CancellationToken ct = default);

    /// <summary>Queue removal of everything <paramref name="sourceRef"/> contributed.</summary>
    Task RemoveAsync(string sourceRef, CancellationToken ct = default);

    /// <summary>Raised after each completed ingest or removal (any outcome). May fire on any thread.</summary>
    event EventHandler? IngestCompleted;
}
```

- [ ] **Step 5.2: Write the failing tests**

`tests/Pia.Wpf.Tests/Wiki/AutoIngestServiceTests.cs`. Harness: real temp vault + real `IngestStateStore` + a recording stub `IIngestService`; provider/settings stubbed. Check `IProviderService.GetDefaultProviderAsync`'s exact return type before writing the stub (see `AiIngestExtractionService`'s use) and mirror it; same for `VaultPathProvider` construction (mirror however `VaultSourcesService`'s tests or `VaultWatcher`'s tests build one — it needs `SetRoot(vaultRoot)`).

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

public class AutoIngestServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly string _sourcesDir;
    private readonly IngestStateStore _state;
    private readonly RecordingIngestService _ingest = new();
    private readonly StubProviderService _providers = new() { HasProvider = true };
    private readonly StubSettingsService _settings = new();
    private readonly VaultPathProvider _paths;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;

    public AutoIngestServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-autoingest-{Guid.NewGuid()}");
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        _sourcesDir = Path.Combine(_vaultRoot, "sources");
        Directory.CreateDirectory(_sourcesDir);
        _state = new IngestStateStore($"Data Source={Path.Combine(_tmpDir, "history.db")}");
        _paths = new VaultPathProvider(_vaultRoot); // explicit-path ctor exists
        _store = new VaultStore(_vaultRoot, _parser);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private AutoIngestService Build() => new(
        _ingest, _state, _store, _providers, _settings, _paths,
        NullLogger<AutoIngestService>.Instance);

    private string Seed(string name, string content)
    {
        var path = Path.Combine(_sourcesDir, name);
        File.WriteAllText(path, content);
        return "sources/" + name;
    }

    // ---- stubs ----

    private sealed class RecordingIngestService : IIngestService
    {
        public List<string> IngestCalls { get; } = [];
        public List<(string Source, IReadOnlyList<string> Pages)> RemoveCalls { get; } = [];
        public Func<string, IngestResult>? ResultFor { get; set; }

        public Task<IngestResult> IngestAsync(string sourceRef, DateOnly date, CancellationToken ct = default)
        {
            IngestCalls.Add(sourceRef);
            return Task.FromResult(ResultFor?.Invoke(sourceRef)
                ?? new IngestResult(sourceRef, [$"memory/topics/{Path.GetFileNameWithoutExtension(sourceRef)}.md"]));
        }

        public Task RemoveContributionsAsync(string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default)
        {
            RemoveCalls.Add((sourceRef, pages));
            return Task.CompletedTask;
        }
    }

    // StubProviderService: implement IProviderService's members as throw/default; only
    // GetDefaultProviderAsync matters — return a dummy provider instance when HasProvider, else null.
    // StubSettingsService: GetSettingsAsync returns new AppSettings { AutoIngestSources = Enabled };
    // Enabled defaults to true. SaveSettingsAsync no-op. (Implement whatever other members the
    // interfaces require as no-ops.)

    [Fact]
    public async Task RunAsync_ingests_and_records_state()
    {
        var sourceRef = Seed("a.txt", "v1");
        using var svc = Build();

        var result = await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Success, result.Outcome);
        Assert.Equal([sourceRef], _ingest.IngestCalls);
        var state = await _state.GetAsync(sourceRef);
        Assert.NotNull(state);
        Assert.Equal(result.TouchedPages, state!.TouchedPages);
    }

    [Fact]
    public async Task Reconcile_skips_unchanged_and_reingests_changed()
    {
        var refA = Seed("a.txt", "v1");
        var refB = Seed("b.txt", "v1");
        using var svc = Build();
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _ingest.IngestCalls.Count);

        File.WriteAllText(Path.Combine(_sourcesDir, "a.txt"), "v2"); // change one
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _ingest.IngestCalls.Count); // only a.txt re-ran
        Assert.Equal(refA, _ingest.IngestCalls[^1]);
        _ = refB;
    }

    [Fact]
    public async Task Reconcile_removes_tracked_but_missing_sources_and_deletes_state()
    {
        var sourceRef = Seed("a.txt", "v1");
        using var svc = Build();
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        File.Delete(Path.Combine(_sourcesDir, "a.txt"));
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        var remove = Assert.Single(_ingest.RemoveCalls);
        Assert.Equal(sourceRef, remove.Source);
        Assert.Null(await _state.GetAsync(sourceRef)); // row deleted -> not re-enqueued next startup
    }

    [Fact]
    public async Task Reconcile_without_provider_records_nothing_and_calls_nothing()
    {
        Seed("a.txt", "v1");
        _providers.HasProvider = false;
        using var svc = Build();

        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_ingest.IngestCalls);
        Assert.Empty(await _state.ListAsync()); // retried next startup/change
    }

    [Fact]
    public async Task Shrinking_touched_set_removes_dropped_pages()
    {
        var sourceRef = Seed("a.txt", "v1");
        _ingest.ResultFor = _ => new IngestResult(sourceRef,
            ["memory/topics/x.md", "memory/topics/y.md"]);
        using var svc = Build();
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        _ingest.ResultFor = _ => new IngestResult(sourceRef, ["memory/topics/x.md"]);
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        var remove = Assert.Single(_ingest.RemoveCalls);
        Assert.Equal(["memory/topics/y.md"], remove.Pages);
        Assert.Equal(["memory/topics/x.md"], (await _state.GetAsync(sourceRef))!.TouchedPages);
    }

    [Fact]
    public async Task Degenerate_outcome_after_success_removes_all_contributions()
    {
        var sourceRef = Seed("a.txt", "v1");
        _ingest.ResultFor = _ => new IngestResult(sourceRef, ["memory/topics/x.md"]);
        using var svc = Build();
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        _ingest.ResultFor = _ => new IngestResult(sourceRef, [], IngestOutcome.NoEntities);
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        var remove = Assert.Single(_ingest.RemoveCalls);
        Assert.Equal(["memory/topics/x.md"], remove.Pages);
        var state = await _state.GetAsync(sourceRef);
        Assert.Equal(IngestOutcome.NoEntities, state!.Outcome);
        Assert.Empty(state.TouchedPages);
    }

    [Fact]
    public async Task SourceNotFound_records_nothing()
    {
        var sourceRef = Seed("a.txt", "v1");
        _ingest.ResultFor = _ => new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
        using var svc = Build();

        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Null(await _state.GetAsync(sourceRef));
    }

    [Fact]
    public async Task StartAsync_with_setting_off_does_not_watch_or_reconcile()
    {
        Seed("a.txt", "v1");
        _settings.Enabled = false;
        using var svc = Build();

        await svc.StartAsync(_vaultRoot);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Empty(_ingest.IngestCalls);
    }

    [Fact]
    public async Task IngestCompleted_fires_after_run()
    {
        var sourceRef = Seed("a.txt", "v1");
        using var svc = Build();
        var fired = 0;
        svc.IngestCompleted += (_, _) => Interlocked.Increment(ref fired);

        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Watcher_ingests_a_dropped_file_after_debounce()
    {
        using var svc = Build();
        await svc.StartAsync(_vaultRoot);

        Seed("dropped.txt", "hello");

        // Debounce is 3 s; poll up to 15 s for the serial queue to process it.
        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(["sources/dropped.txt"], _ingest.IngestCalls);
    }

    [Fact]
    public async Task Watcher_collapses_rapid_writes_into_one_ingest()
    {
        using var svc = Build();
        await svc.StartAsync(_vaultRoot);

        // Five writes inside one 3 s debounce window must produce exactly ONE ingest — and the
        // under-lock hash re-check must keep a racing duplicate event from double-spending.
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_sourcesDir, "burst.txt"), $"content v{i}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        await Task.Delay(1000, TestContext.Current.CancellationToken); // grace: no second call

        Assert.Equal(["sources/burst.txt"], _ingest.IngestCalls);
    }

    [Fact]
    public async Task RestartAsync_moves_the_watcher_to_a_new_root()
    {
        using var svc = Build();
        await svc.StartAsync(_vaultRoot);

        var newVault = Path.Combine(_tmpDir, "vault2");
        Directory.CreateDirectory(Path.Combine(newVault, "sources"));
        _paths.SetRoot(newVault); // relocation re-points the provider before restarting
        await svc.RestartAsync(newVault);

        File.WriteAllText(Path.Combine(newVault, "sources", "moved.txt"), "hello");
        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains("sources/moved.txt", _ingest.IngestCalls);
    }
}
```

- [ ] **Step 5.3: Run to verify compile failure**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.AutoIngestServiceTests"`
Expected: compile error — `AutoIngestService` does not exist.

- [ ] **Step 5.4: Implement AutoIngestService**

`src/Pia.Wpf/Services/Wiki/AutoIngestService.cs`:

```csharp
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// The auto-ingest pipeline (spec: docs/superpowers/specs/2026-07-07-auto-ingest-sources-design.md).
/// One serial queue for ALL ingest work — a <c>sources/</c> FileSystemWatcher (any extension; the
/// vault watcher only sees *.md), the startup reconcile scan, and manual tool runs via
/// <see cref="IIngestScheduler"/>. Automatic triggers are hash-gated against
/// <see cref="IngestStateStore"/> and gated on the AutoIngestSources setting + a configured AI
/// provider; the manual path always executes. After every ingest the previous touched-set is
/// diffed against the new one and dropped pages get their contributions removed — that diff is
/// what makes replace-per-source true when a source shrinks or degrades to no entities.
/// Start/Stop/Restart mirror VaultWatcher so folder relocation can release the directory handle.
/// </summary>
public sealed class AutoIngestService : IIngestScheduler, IDisposable
{
    /// <summary>Longer than VaultWatcher's 300 ms: source files arrive by multi-second copy.</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(3);

    private readonly IIngestService _ingest;
    private readonly IngestStateStore _state;
    private readonly IVaultStore _store;
    private readonly IProviderService _providers;
    private readonly ISettingsService _settings;
    private readonly VaultPathProvider _paths;
    private readonly ILogger<AutoIngestService> _logger;

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string? _sourcesDir;
    private bool _disposed;

    public event EventHandler? IngestCompleted;

    public AutoIngestService(
        IIngestService ingest,
        IngestStateStore state,
        IVaultStore store,
        IProviderService providers,
        ISettingsService settings,
        VaultPathProvider paths,
        ILogger<AutoIngestService> logger)
    {
        _ingest = ingest;
        _state = state;
        _store = store;
        _providers = providers;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    // ---- lifecycle (mirrors VaultWatcher so relocation can release the directory handle) ----

    public Task StartAsync() => StartAsync(_paths.VaultRoot);

    public async Task StartAsync(string vaultRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
        {
            return;
        }

        var settings = await _settings.GetSettingsAsync();
        if (!settings.AutoIngestSources)
        {
            _logger.LogInformation("Auto-ingest disabled by setting; manual ingest remains available");
            return;
        }

        // Created defensively: FileSystemWatcher throws on a missing root, and we must not depend
        // on VaultSchemaService's scaffolding order.
        var sourcesDir = Path.Combine(vaultRoot, "sources");
        Directory.CreateDirectory(sourcesDir);
        _sourcesDir = sourcesDir;

        var watcher = new FileSystemWatcher(sourcesDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += OnChangedOrCreated;
        watcher.Changed += OnChangedOrCreated;
        watcher.Renamed += OnRenamed;
        watcher.Deleted += OnDeleted;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        // The reconcile scan is the queue's first work; LLM-bound items drain in the background so
        // startup is never blocked. A watcher event racing the scan is harmless — the second run
        // no-ops on the recorded hash.
        _ = Task.Run(() => ReconcileAsync(CancellationToken.None));

        _logger.LogInformation("Auto-ingest watcher started");
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChangedOrCreated;
            _watcher.Changed -= OnChangedOrCreated;
            _watcher.Renamed -= OnRenamed;
            _watcher.Deleted -= OnDeleted;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        foreach (var timer in _pending.Values)
        {
            timer.Dispose();
        }

        _pending.Clear();
    }

    public Task RestartAsync(string vaultRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        return StartAsync(vaultRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _serial.Dispose();
    }

    // ---- IIngestScheduler ----

    public Task<IngestResult> RunAsync(string sourceRef, CancellationToken ct = default)
        => ExecuteAsync(Normalize(sourceRef), knownHash: null, autoGated: false, ct);

    public async Task RemoveAsync(string sourceRef, CancellationToken ct = default)
    {
        sourceRef = Normalize(sourceRef);
        await _serial.WaitAsync(ct);
        try
        {
            var state = await _state.GetAsync(sourceRef);
            IReadOnlyList<string> pages = state?.TouchedPages is { Count: > 0 } touched
                ? touched
                : await ScanPagesForSourceAsync(sourceRef);
            if (pages.Count > 0)
            {
                await _ingest.RemoveContributionsAsync(sourceRef, pages, ct);
            }

            // Delete the row so the next reconcile doesn't re-enqueue this removal forever.
            await _state.DeleteAsync(sourceRef);
        }
        finally
        {
            _serial.Release();
            RaiseIngestCompleted();
        }
    }

    // ---- reconcile (public for tests; called by StartAsync on the background queue) ----

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync();
        if (!settings.AutoIngestSources)
        {
            return;
        }

        var sourcesDir = _sourcesDir ?? Path.Combine(_paths.VaultRoot, "sources");
        if (!Directory.Exists(sourcesDir))
        {
            return;
        }

        var files = Directory.EnumerateFiles(sourcesDir, "*", SearchOption.AllDirectories).ToList();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await AutoRunAsync(ToRef(sourcesDir, file), ct);
        }

        // Tracked but gone from disk -> the source was deleted while we weren't watching.
        var onDisk = new HashSet<string>(
            files.Select(f => ToRef(sourcesDir, f)), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await _state.ListAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (!onDisk.Contains(entry.SourceRef))
            {
                await RemoveAsync(entry.SourceRef, ct);
            }
        }

        _logger.LogInformation("Auto-ingest reconcile completed over {Count} source file(s)", files.Count);
    }

    // ---- internals ----

    /// <summary>Hash-gated automatic run: skips when content is unchanged since the last record.</summary>
    private async Task AutoRunAsync(string sourceRef, CancellationToken ct)
    {
        try
        {
            if (await _providers.GetDefaultProviderAsync() is null)
            {
                // No record is written, so the source is retried on the next change or startup.
                _logger.LogDebug("Auto-ingest skipped: no AI provider configured");
                return;
            }

            var hash = TryHashFile(sourceRef);
            if (hash is null)
            {
                return; // vanished mid-flight; the Deleted event / next reconcile cleans up
            }

            var state = await _state.GetAsync(sourceRef);
            if (string.Equals(state?.ContentHash, hash, StringComparison.Ordinal))
            {
                return; // unchanged — never re-spend the LLM calls
            }

            await ExecuteAsync(sourceRef, hash, autoGated: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-ingest failed to process a source");
            _logger.SensitiveDebug("Auto-ingest failed on {Source}", sourceRef);
        }
    }

    private async Task<IngestResult> ExecuteAsync(
        string sourceRef, string? knownHash, bool autoGated, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            // The pre-semaphore hash check in AutoRunAsync is only an early-out; it can race an
            // in-flight ingest of the same file (reconcile scan vs watcher event — a real window,
            // since each ingest is two LLM calls). Re-check under the lock so the loser of that
            // race no-ops instead of double-spending. Manual runs (autoGated: false) always run.
            if (autoGated)
            {
                var gate = TryHashFile(sourceRef);
                if (gate is null)
                {
                    return new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
                }

                var recorded = await _state.GetAsync(sourceRef);
                if (string.Equals(recorded?.ContentHash, gate, StringComparison.Ordinal))
                {
                    // Deliberate: the finally still raises IngestCompleted for this no-op — a
                    // spurious sources-overview reload is cheap and keeps the event contract simple.
                    return new IngestResult(sourceRef, recorded!.TouchedPages, recorded.Outcome);
                }

                knownHash = gate;
            }

            var result = await _ingest.IngestAsync(sourceRef, DateOnly.FromDateTime(DateTime.Now), ct);
            if (result.Outcome == IngestOutcome.SourceNotFound)
            {
                return result; // transient: record nothing (spec §4)
            }

            // Without a provider the extractor degrades to NoEntities. Recording THAT would (a)
            // freeze the hash so the source is never retried once a provider exists, and (b) run
            // the shrink-diff below and wipe valid contributions. Treat it as transient instead.
            if (await _providers.GetDefaultProviderAsync() is null)
            {
                return result;
            }

            var hash = knownHash ?? TryHashFile(sourceRef);
            if (hash is null)
            {
                return result; // file vanished after ingest read it; next event settles it
            }

            var previous = await _state.GetAsync(sourceRef);
            IReadOnlyList<string> newTouched =
                result.Outcome == IngestOutcome.Success ? result.TouchedPages : [];
            var dropped = (previous?.TouchedPages ?? [])
                .Where(p => !newTouched.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (dropped.Count > 0)
            {
                // The pages v(n-1) touched but v(n) no longer does — strip the stale sections.
                await _ingest.RemoveContributionsAsync(sourceRef, dropped, ct);
            }

            await _state.UpsertAsync(new IngestStateEntry(
                sourceRef, hash, result.Outcome, newTouched, DateTimeOffset.UtcNow));

            _logger.LogInformation("Auto-ingest completed ({Outcome}, {Count} page(s))",
                result.Outcome, newTouched.Count);
            return result;
        }
        finally
        {
            _serial.Release();
            RaiseIngestCompleted();
        }
    }

    private void RaiseIngestCompleted()
    {
        try
        {
            IngestCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A throwing subscriber must not mask the queue item's own result (this runs in finally).
            _logger.LogWarning(ex, "IngestCompleted subscriber threw");
        }
    }

    /// <summary>Fallback when the state row is missing: find pages via their sources: frontmatter.</summary>
    private async Task<IReadOnlyList<string>> ScanPagesForSourceAsync(string sourceRef)
    {
        var hits = new List<string>();
        foreach (var path in await _store.EnumerateAsync("memory/topics/*.md"))
        {
            var doc = await _store.ReadAsync(path);
            if (doc is not null && SourcesProvenance.ReadSourceRefs(doc.RawText)
                    .Contains(sourceRef, StringComparer.OrdinalIgnoreCase))
            {
                // EnumerateAsync returns native separators (backslash on Windows); the removal
                // pipeline and index keys are forward-slash.
                hits.Add(path.Replace('\\', '/'));
            }
        }

        return hits;
    }

    private string? TryHashFile(string sourceRef)
    {
        var full = Path.Combine(
            _paths.VaultRoot, sourceRef.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            using var stream = File.OpenRead(full);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- watcher plumbing (same debounce shape as VaultWatcher, longer window) ----

    private void OnChangedOrCreated(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.OldFullPath))
        {
            Schedule(e.OldFullPath); // fires as removal — the old ref's file no longer exists
        }

        Schedule(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e) =>
        _logger.LogWarning(e.GetException(), "Auto-ingest watcher error");

    private void Schedule(string fullPath)
    {
        // Directory events carry no ingestable content; a directory delete surfaces per-file.
        if (_disposed || _sourcesDir is null || Directory.Exists(fullPath))
        {
            return;
        }

        var sourceRef = ToRef(_sourcesDir, fullPath);
        var timer = new Timer(_ => Fire(sourceRef), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        if (_pending.TryRemove(sourceRef, out var previous))
        {
            previous.Dispose();
        }

        _pending[sourceRef] = timer;
    }

    private async void Fire(string sourceRef)
    {
        if (_pending.TryRemove(sourceRef, out var timer))
        {
            timer.Dispose();
        }

        try
        {
            var full = Path.Combine(
                _paths.VaultRoot, sourceRef.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                await AutoRunAsync(sourceRef, CancellationToken.None);
            }
            else
            {
                await RemoveAsync(sourceRef, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // A watcher callback must never crash the process; surface and move on.
            _logger.LogWarning(ex, "Auto-ingest failed to process a change");
            _logger.SensitiveDebug("Auto-ingest failed on {Source}", sourceRef);
        }
    }

    private static string ToRef(string sourcesDir, string fullPath) =>
        "sources/" + Path.GetRelativePath(sourcesDir, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string Normalize(string sourceRef) =>
        sourceRef.Trim().Replace('\\', '/').TrimStart('/');
}
```

**Implementation note — the provider gate may be vacuous in production:** `ProviderService.GetDefaultProviderAsync` is `providers.FirstOrDefault()` and a PiaCloud provider reportedly "always exists" (ProviderService.cs ~line 122). Confirm while implementing. If a default provider always exists, keep the null check anyway (it costs nothing and the tests exercise it); an unusable provider then surfaces as an ingest exception → caught in `AutoRunAsync` → nothing recorded → retried next startup, which is exactly the spec's transient-failure rule. Do NOT invent a richer "is the provider usable" predicate in this task.

- [ ] **Step 5.5: Run the test class**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.AutoIngestServiceTests"`
Expected: ALL PASS. The watcher test is timing-dependent; if it flakes on slow CI, raise the poll ceiling — never shrink the debounce for the test.

- [ ] **Step 5.6: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IIngestScheduler.cs src/Pia.Wpf/Services/Wiki/AutoIngestService.cs tests/Pia.Wpf.Tests/Wiki/AutoIngestServiceTests.cs
git commit -m "feat(ingest): AutoIngestService — serial scheduler, sources watcher, reconcile"
```

---

### Task 6: Setting + DI + startup wiring

**Files:**
- Modify: `src/Pia.Wpf/Models/AppSettings.cs` (next to `MeetingAttendeeRosterSnapshotMinutes`, ~line 146)
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (registrations ~line 370; startup ~line 175)

- [ ] **Step 6.1: Add the setting**

In `AppSettings.cs`:

```csharp
// Automatically ingest documents in the vault's sources/ folder (watcher + startup reconcile).
// Each ingest costs two LLM calls to the default provider and writes synced memory pages, so this
// is the consent gate. Gates only the automatic triggers — the chat ingest tool always works.
// JSON-only (no settings UI), like MeetingAttendeeRosterSnapshotMinutes.
public bool AutoIngestSources { get; set; } = true;
```

- [ ] **Step 6.2: Register services**

In `Bootstrapper.cs` next to the existing ingest registrations (~line 370–372):

```csharp
services.AddSingleton(sp => new Pia.Services.Wiki.IngestStateStore(
    sp.GetRequiredService<SqliteContext>().ConnectionString));
services.AddSingleton<Pia.Services.Wiki.AutoIngestService>();
services.AddSingleton<IIngestScheduler>(sp => sp.GetRequiredService<Pia.Services.Wiki.AutoIngestService>());
```

(Verify `SqliteContext` is registered as a singleton — it is resolved elsewhere; if it's registered under a different shape, match it.)

- [ ] **Step 6.3: Start it AFTER the vault watcher**

Directly after the `VaultWatcher.Start()` try/catch (~line 180):

```csharp
// Auto-ingest starts AFTER the vault watcher: recall indexing of Pia's own page writes happens
// only via the live watcher, so ingest-written topic pages must land while it is running. The
// reconcile scan runs on the service's own background queue — startup is never blocked on LLM work.
try
{
    await _serviceProvider.GetRequiredService<Pia.Services.Wiki.AutoIngestService>().StartAsync();
}
catch (Exception ex)
{
    bootstrapLogger.LogWarning(ex, "Failed to start auto-ingest; sources won't auto-compile this session");
}
```

- [ ] **Step 6.4: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: clean.

- [ ] **Step 6.5: Commit**

```bash
git add src/Pia.Wpf/Models/AppSettings.cs src/Pia.Wpf/Bootstrapper.cs
git commit -m "feat(ingest): AutoIngestSources setting + bootstrapper wiring"
```

---

### Task 7: Relocation, tool handler, Memory view refresh

**Files:**
- Modify: `src/Pia.Wpf/Services/AssistantFolderRelocationService.cs`
- Modify: `src/Pia.Wpf/Services/IngestToolHandler.cs`
- Modify: `src/Pia.Wpf/ViewModels/MemoryViewModel.cs`
- Modify: `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs` (tool-handler fixture)
- Modify: `tests/Pia.Wpf.Tests/ViewModels/MemoryViewModelTests.cs` (ctor)
- Modify (if relocation tests construct the service): the corresponding test file

- [ ] **Step 7.1: Relocation hooks**

In `AssistantFolderRelocationService`: inject `Pia.Services.Wiki.AutoIngestService autoIngest` (concrete, like the existing `VaultWatcher` field). In `MoveAsync`:
- after `_watcher.Stop();` (line ~81): `_autoIngest.Stop();` — same reason, its `FileSystemWatcher` holds a handle under the old root.
- in BOTH failure branches after `_watcher.Restart(_paths.VaultRoot);`: `await _autoIngest.RestartAsync(_paths.VaultRoot);`
- in the success path after `_watcher.Restart(newVault);` (line ~102): `await _autoIngest.RestartAsync(newVault);` — restart order mirrors boot (recall watcher first).

Update any test that constructs `AssistantFolderRelocationService` (search `new AssistantFolderRelocationService(`). `AutoIngestService` is sealed and concrete, so those tests need a REAL instance: build it from a temp-db `IngestStateStore` and the same stub `IIngestService`/`IProviderService`/`ISettingsService` shapes as `AutoIngestServiceTests` (extract the stubs to a shared file under `tests/Pia.Wpf.Tests/Wiki/` if reuse gets awkward). Then add one behavioral assertion to the existing relocation success-path test: after `MoveAsync` succeeds, drop a file into `<newVault>/sources/` and poll for the recording stub's ingest call — proving the auto-ingest watcher was stopped for the move and restarted on the NEW root (spec §7's relocation coverage; the `RestartAsync_moves_the_watcher_to_a_new_root` unit test in Task 5 covers the mechanism, this covers the wiring). The two failure branches just need the construction updated; asserting their restart-on-old-root behavior via the same drop-a-file probe is optional if the harness makes it cheap.

- [ ] **Step 7.2: Tool handler goes through the queue**

In `IngestToolHandler`: replace the `IIngestService _ingestService` field/ctor param with `IIngestScheduler _scheduler`, and the call becomes:

```csharp
var result = await _scheduler.RunAsync(sourceRef, cancellationToken);
```

Everything else (outcome → message mapping) is unchanged. In `IngestServiceTests`, `BuildToolHandler` now needs a scheduler; add a minimal passthrough so the existing tool-handler tests keep exercising the real `IngestService`:

```csharp
private sealed class PassthroughScheduler(IIngestService inner) : IIngestScheduler
{
    public event EventHandler? IngestCompleted { add { } remove { } }
    public Task<IngestResult> RunAsync(string sourceRef, CancellationToken ct = default)
        => inner.IngestAsync(sourceRef, DateOnly.FromDateTime(DateTime.UtcNow), ct);
    public Task RemoveAsync(string sourceRef, CancellationToken ct = default)
        => Task.CompletedTask;
}

private IngestToolHandler BuildToolHandler()
    => new(new PassthroughScheduler(BuildIngest(new StubExtractor())),
        NullLogger<IngestToolHandler>.Instance);
```

- [ ] **Step 7.3: Memory view live refresh**

In `MemoryViewModel` (it is `IDisposable`, see line 592):
- add ctor param `IIngestScheduler ingestScheduler`, store in `_ingestScheduler`, and subscribe at the end of the ctor: `_ingestScheduler.IngestCompleted += OnIngestCompleted;`
- handler:

```csharp
// The scheduler raises on background threads; the VM is scoped while the scheduler is a
// singleton, so Dispose MUST unsubscribe or this event pins the VM for the app lifetime.
private void OnIngestCompleted(object? sender, EventArgs e) =>
    System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _ = LoadSourcesAsync());
```

- in `Dispose()`: `_ingestScheduler.IngestCompleted -= OnIngestCompleted;`
- update `MemoryViewModelTests` construction — the file already uses NSubstitute, so `Substitute.For<IIngestScheduler>()` is the whole stub.

- [ ] **Step 7.4: Build + affected test classes**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj` then
`dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-class "Pia.Tests.Wiki.IngestServiceTests"` and the MemoryViewModel + relocation test classes.
Expected: clean build, all PASS.

- [ ] **Step 7.5: Commit**

```bash
git add -A src/Pia.Wpf tests/Pia.Wpf.Tests
git commit -m "feat(ingest): wire scheduler into tool, relocation, and Memory view refresh"
```

---

### Task 8: Full gate + spec status + smoke notes

- [ ] **Step 8.1: Full test gate**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: zero failures (the excluded namespace holds the ~18 known live-network failures).

- [ ] **Step 8.2: Update the spec status line**

In `docs/superpowers/specs/2026-07-07-auto-ingest-sources-design.md`, set `**Status:** Implemented 2026-07-08` and append the Task 4 deviation note (IngestState table created lazily by the store, not in `SqliteContext.EnsureSchema`) to §4.

- [ ] **Step 8.3: Commit**

```bash
git add docs/superpowers/specs/2026-07-07-auto-ingest-sources-design.md
git commit -m "docs(ingest): mark auto-ingest spec implemented"
```

- [ ] **Step 8.4: Human-gated smoke test (leave for the user)**

With a provider configured: launch the app, drop a small `.txt` into `Vault/sources/`, expect within ~10 s a log line `Auto-ingest completed (Success, N page(s))`, topic pages under `memory/topics/` with a `## Source: sources/<name>` section, and the Memory view's sources overview flipping to "Compiled into N topic page(s)" without a manual refresh. Then edit the file (change a fact) and verify the section is REPLACED; delete the file and verify the pages/sections disappear.
