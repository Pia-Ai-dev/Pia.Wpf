# Ingest Topic Synthesis (Karpathy LLM-Wiki) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild ingest so each `memory/topics/*.md` page is one LLM-synthesized narrative merged from *all* sources that mention it, gated by a charter-grounded notability filter, with a category-grouped index — replacing today's per-source bullet-dump extractor.

**Architecture:** Ingest flips from source-driven fan-out to **topic-driven synthesis**. Extraction is reduced to *notable-topic discovery* (name + coarse category, charter-grounded). A new `IIngestSynthesizer` re-reads the union of a topic's raw sources and writes the page body as prose + `[[wikilinks]]`. On any change to a topic's source set the body is fully re-synthesized (a manual preamble above the managed body is preserved). Provenance lives only in frontmatter `sources:`; the `## Source:` section/splice machinery is deleted. A new `category` frontmatter key sub-groups the index's Topics section (the `type` stays `topic`, so recall/schema/sync are untouched).

**Tech Stack:** C# / .NET 10, xUnit v3 (MTP runner), `Microsoft.Extensions.AI`, existing `IVaultStore`/`VaultIndexService`/`VaultLogService`/`AutoIngestService`.

**Design doc:** `docs/superpowers/specs/2026-07-08-ingest-topic-synthesis-design.md`

**Baseline gate (per branch memory):** run tests with the MTP runner, `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`; the only acceptable pre-existing failures are the Mistral `magistral` case and the VaultSync E2EE case. No new failures beyond those.

---

## File Structure

**Create:**
- `src/Pia.Wpf/Services/Wiki/VaultCharterService.cs` — resolves charter text (charter.md → profile.md → empty).
- `src/Pia.Wpf/Services/Interfaces/IIngestSynthesizer.cs` — synthesizer interface + `SynthesizedPage` record.
- `src/Pia.Wpf/Services/Wiki/AiIngestSynthesisService.cs` — production synthesizer (one LLM call per topic).
- `tests/Pia.Wpf.Tests/Wiki/VaultCharterServiceTests.cs`
- `tests/Pia.Wpf.Tests/Wiki/AiIngestSynthesisServiceTests.cs` (parse/degrade only — no live LLM).

**Modify:**
- `src/Pia.Wpf/Services/Interfaces/IIngestExtractor.cs` — `ExtractedEntity(Subject, Facts)` → `ExtractedTopic(Subject, Category)`; drop `SummarizeAsync` usage from ingest (keep or remove per Task 2).
- `src/Pia.Wpf/Services/Wiki/AiIngestExtractionService.cs` — charter-grounded topic discovery; hardened notability prompt; parse `{subject, category}`.
- `src/Pia.Wpf/Services/Wiki/IngestService.cs` — topic-driven synthesis rewrite; delete `## Source:` machinery; shared `SynthesizePageAsync`; preamble preservation; `category` frontmatter.
- `src/Pia.Wpf/Infrastructure/Vault/VaultFrontmatter.cs` — `Build` overload that also writes `category:`.
- `src/Pia.Wpf/Services/Wiki/VaultIndexService.cs` — sub-group the Topics section by page `category`.
- `src/Pia.Wpf/Bootstrapper.cs` — register `VaultCharterService`, `IIngestSynthesizer`.
- `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs` — rewrite stubs/asserts for the new pipeline.
- `tests/Pia.Wpf.Tests/Wiki/AutoIngestServiceTests.cs` — adjust any `## Source:`/facts assumptions.

**Unchanged (verify only):** `AutoIngestService` (uses `IngestResult.TouchedPages` diff + `RemoveContributionsAsync` — both contracts preserved), `IngestStateStore`, `IIngestScheduler`, `SourcesProvenance`.

---

## Chunk 1: Charter + notability-aware topic discovery

### Task 1: `VaultCharterService`

**Files:**
- Create: `src/Pia.Wpf/Services/Wiki/VaultCharterService.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/VaultCharterServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// VaultCharterServiceTests.cs — real temp VaultStore, same setup shape as IngestServiceTests.
[Fact]
public async Task Returns_charter_when_present()
{
    await _store.WriteAtomicAsync("memory/charter.md",
        VaultFrontmatter.Build("note", "Charter") + "\nPia is a privacy-first AI assistant.");
    var svc = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);
    (await svc.GetCharterAsync()).Should().Contain("privacy-first"); // or Assert.Contains
}

[Fact]
public async Task Falls_back_to_profile_then_empty()
{
    var svc = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);
    Assert.Equal(string.Empty, await svc.GetCharterAsync()); // neither file present

    await _store.WriteAtomicAsync("memory/profile.md",
        VaultFrontmatter.Build("personal_profile", "Profile") + "\nOwner is a solo dev.");
    Assert.Contains("solo dev", await svc.GetCharterAsync());
}
```

> Use plain `Xunit.Assert` (this repo dropped FluentAssertions — see project memory). Copy the temp-`VaultStore` ctor/`Dispose` boilerplate from `IngestServiceTests`.

- [ ] **Step 2: Run to verify fail** — `dotnet test ... --filter-class "*VaultCharterServiceTests"` → FAIL (type missing).

- [ ] **Step 3: Implement**

```csharp
namespace Pia.Services.Wiki;

/// <summary>
/// Resolves the vault "charter" — a short statement of what this vault is about — fed into ingest
/// extraction so only topics notable to the vault's purpose become pages. Resolution order:
/// memory/charter.md → memory/profile.md → empty. Returns the page BODY (preamble + sections), not
/// frontmatter. Never throws; a missing/empty vault yields "".
/// </summary>
public sealed class VaultCharterService
{
    private readonly IVaultStore _store;
    private readonly ILogger<VaultCharterService> _logger;

    public VaultCharterService(IVaultStore store, ILogger<VaultCharterService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<string> GetCharterAsync()
    {
        foreach (var path in new[] { "memory/charter.md", "memory/profile.md" })
        {
            var doc = await _store.ReadAsync(path);
            var body = BodyOf(doc);
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body.Trim();
            }
        }

        return string.Empty;
    }

    private static string BodyOf(VaultDocument? doc)
    {
        if (doc is null)
        {
            return string.Empty;
        }

        var parts = new List<string> { doc.Preamble };
        parts.AddRange(doc.Sections.Select(s => "## " + s.Heading + "\n" + s.Body));
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
```

- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(ingest): add VaultCharterService for notability grounding`

### Task 2: Extraction → charter-grounded topic discovery

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IIngestExtractor.cs`
- Modify: `src/Pia.Wpf/Services/Wiki/AiIngestExtractionService.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/` (new `AiIngestExtractionServiceTests` for `ParseTopics`, or extend existing)

- [ ] **Step 1: Write failing parser tests.** New record + parser:

```csharp
// record ExtractedTopic(string Subject, string Category);
[Fact]
public void ParseTopics_reads_subject_and_category_json()
{
    var topics = AiIngestExtractionService.ParseTopics(
        """[{"subject":"Pia","category":"product"},{"subject":"GDPR","category":"regulation"}]""");
    Assert.Equal(2, topics.Count);
    Assert.Equal("Pia", topics[0].Subject);
    Assert.Equal("regulation", topics[1].Category);
}

[Fact]
public void ParseTopics_defaults_missing_category_to_concept()
{
    var topics = AiIngestExtractionService.ParseTopics("""[{"subject":"WPF"}]""");
    Assert.Equal("concept", topics[0].Category);
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement.** In `IIngestExtractor.cs` replace the record and the extract method signature:

```csharp
/// <summary>One notable topic discovered in a source: page title + a coarse category
/// (person/organization/product/concept/regulation/technology/other) used only for index grouping.</summary>
public record ExtractedTopic(string Subject, string Category);

public interface IIngestExtractor
{
    /// <summary>Discover the notable topics in <paramref name="content"/>, grounded in
    /// <paramref name="charter"/> (may be empty). Returns [] when nothing is notable.</summary>
    Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
        string content, string charter, CancellationToken ct = default);
}
```

In `AiIngestExtractionService.cs`: rename/replace `ExtractEntitiesAsync`→`DiscoverTopicsAsync`; drop `SummarizeAsync` and the facts machinery. New prompt:

```csharp
var charterBlock = string.IsNullOrWhiteSpace(charter)
    ? ""
    : "This knowledge base is about:\n" + charter + "\n\n";

var prompt =
    charterBlock +
    "List the NOTABLE topics in the document below that deserve their own wiki page — real people, " +
    "organizations, products, named concepts, technologies, or regulations that carry meaning for this " +
    "knowledge base. DO NOT include generic dictionary/legal-boilerplate terms (e.g. \"Use\", " +
    "\"Software\", \"Documentation\", \"Agreement\", \"Scope\"), generic verbs, or section labels. " +
    "Respond with a JSON array of objects, each {\"subject\": name, \"category\": one of " +
    "person|organization|product|concept|regulation|technology|other}. JSON only.\n\n" +
    Truncate(content);
```

Keep the defensive `ExtractJsonArray` helper; rewrite the parser to `ParseTopics` reading `subject`+`category` (default category `"concept"` when absent/blank; skip blank subjects). Keep the line-fallback (bare `Subject` → category `concept`).

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `feat(ingest): reduce extraction to charter-grounded topic discovery`

---

## Chunk 2: Synthesizer

### Task 3: `IIngestSynthesizer` + `AiIngestSynthesisService`

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IIngestSynthesizer.cs`
- Create: `src/Pia.Wpf/Services/Wiki/AiIngestSynthesisService.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/AiIngestSynthesisServiceTests.cs`

- [ ] **Step 1: Write failing tests** for output parsing + graceful degradation (no provider → empty). The synthesizer contract:

```csharp
/// <summary>A synthesized topic page: the managed markdown body (prose + [[topics/slug]] links)
/// and a one-line summary for the index.</summary>
public record SynthesizedPage(string Body, string Summary);

public interface IIngestSynthesizer
{
    /// <summary>Write the topic page body for <paramref name="title"/> by synthesizing across ALL
    /// <paramref name="sources"/> (each a (ref, rawText) pair). Empty body ⇒ caller skips the page.</summary>
    Task<SynthesizedPage> SynthesizeAsync(
        string title, string category, string charter,
        IReadOnlyList<(string Ref, string Text)> sources, CancellationToken ct = default);
}
```

Test with a fake `IProviderService` returning null → assert `SynthesizeAsync` returns empty `Body`. Test the summary/body split parser (see Step 3) directly if extracted to an `internal static` helper.

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement** `AiIngestSynthesisService` (mirror `AiIngestExtractionService` ctor: `IAiClientService`, `IProviderService`, logger; `MaxSourceChars = 12000` per source, truncated). Prompt:

```csharp
var prompt =
    (string.IsNullOrWhiteSpace(charter) ? "" : "Knowledge base context:\n" + charter + "\n\n") +
    $"Write a concise wiki page for the topic \"{title}\" (category: {category}). Synthesize a SINGLE " +
    "coherent explanation across ALL the sources below — merge overlapping facts, reconcile them, and " +
    "note contradictions explicitly. Start with a one-sentence definition, then short prose or bullets. " +
    "Link related topics inline using [[topics/<slug>]] where <slug> is the lowercase-hyphen form of the " +
    "topic name. Do NOT include a title heading or frontmatter. First output a line " +
    "'SUMMARY: <one sentence>' then a blank line then the page body.\n\n" +
    string.Join("\n\n", sources.Select(s => $"--- SOURCE: {s.Ref} ---\n{Truncate(s.Text)}"));
```

Parse: first `SUMMARY:` line → `Summary`; remainder → `Body`. If no `SUMMARY:` line, `Summary` = first non-empty body line, `Body` = whole text. No provider / blank model output → `new SynthesizedPage("", "")`. Never throw.

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `feat(ingest): add AiIngestSynthesisService (cross-source page synthesis)`

---

## Chunk 3: IngestService rewrite

### Task 4: `VaultFrontmatter` — category key + identity-preserving rebuild

**Files:** Modify `src/Pia.Wpf/Infrastructure/Vault/VaultFrontmatter.cs`; test `tests/Pia.Wpf.Tests/Vault/` (add to an existing frontmatter test class or a new one).

> **Why identity-preserving matters:** re-synthesis rewrites the *whole* page (frontmatter + body) every time. `Build` mints a fresh `Guid.NewGuid()` + new `created`/`updated` on each call (`VaultFrontmatter.cs:15-23`). If ingest called `Build` on every re-ingest, a topic page's `id` would change on every source edit — breaking the "stable id preserved" invariant (`VaultIndexService.BuildFrontmatter` reuses `id`/`created`, `VaultIndexService.cs:186-197`; `VaultWikiTests.cs:74` asserts it) and churning sync (identity is keyed on `id`). So re-synthesis MUST reuse the existing `id`/`created`.

- [ ] **Step 1: Failing tests**
  1. `Build("topic", "Pia", "product")` output contains `type: topic` AND `category: product` (category line after `title:`); the 2-arg `Build("note","X")` still emits no `category:` line.
  2. `BuildPreserving(existingDoc, "Pia", "product")` reuses the existing doc's `id` and `created`, sets a fresh `updated`, and writes `type: topic` + `category: product`. (Seed `existingDoc` by parsing a page built earlier — assert same `id`, same `created`, `updated` >= `created`.)
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement**
  - Overload `Build(string type, string title, string? category)` — inserts `category: {category}\n` after the title line when `category` is non-null; the existing 2-arg overload delegates with `category: null`.
  - `public static string BuildPreserving(VaultDocument? existing, string title, string category)` — always `type: topic`; reuses `existing.Frontmatter["id"]`/`["created"]` when present (else fresh, mirroring `VaultIndexService.BuildFrontmatter`), fresh `updated`, writes `category`. This is the ONLY frontmatter builder `SynthesizePageAsync` calls.
- [ ] **Step 4: PASS.**
- [ ] **Step 5: Commit** — `feat(vault): frontmatter category key + identity-preserving rebuild`

### Task 5: IngestService — topic-driven synthesis core

**Files:** Modify `src/Pia.Wpf/Services/Wiki/IngestService.cs`; rewrite `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs`.

Inject `IIngestSynthesizer` and `VaultCharterService` (add ctor params; update Bootstrapper + tests). Keep `IEmbeddingService` param only if still used — otherwise remove and update DI/tests (verify with a usage search first).

- [ ] **Step 1: Write the failing tests** (rewrite the file). A fake extractor returns fixed topics; a fake synthesizer returns a deterministic body per title. Assert:
  1. `Ingest_creates_synthesized_topic_pages` — page exists, body == fake synthesis, frontmatter has `type: topic` + `category:`, `sources:` lists the source; NO `## Source:` heading present.
  2. `Reingest_after_second_source_unions_sources` — ingest source A (topic T), then source B (also T); T's `sources:` lists both, and the fake synthesizer was called for T with BOTH raw texts.
  3. `Ingest_preserves_manual_preamble` — pre-write a page with a manual preamble above the `<!-- pia:managed -->` sentinel; ingest twice. The fake synthesizer body MUST contain no `##` heading (the realistic case that would otherwise be mis-parsed as preamble). Assert the preamble survives verbatim and appears exactly once (no accumulation across the two ingests), and the body below the sentinel is the latest synthesis.
  4. `Ingest_touches_only_notable_topics` — extractor returns 2 topics → exactly 2 pages, index has 2 entries.
  5. `Reingest_preserves_id_and_created` — ingest topic T (capture its frontmatter `id` + `created`), then ingest a second source that also mentions T; assert T's `id` and `created` are unchanged and `updated` advanced.

Fake synthesizer example:

```csharp
private sealed class FakeSynthesizer : IIngestSynthesizer
{
    public List<(string Title, int SourceCount)> Calls { get; } = new();
    public Task<SynthesizedPage> SynthesizeAsync(string title, string category, string charter,
        IReadOnlyList<(string Ref, string Text)> sources, CancellationToken ct = default)
    {
        Calls.Add((title, sources.Count));
        return Task.FromResult(new SynthesizedPage(
            $"{title} is a synthesized topic from {sources.Count} source(s).", $"{title} summary"));
    }
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement the rewrite.** Replace the body of `IngestAsync` (keep steps 1–2 guards: containment, `File.Exists`, `IsTextSource`, non-empty). Then:

```csharp
var charter = await _charter.GetCharterAsync();
var topics = await _extractor.DiscoverTopicsAsync(content, charter, ct);
if (topics.Count == 0)
    return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);

var touched = new List<string>();
foreach (var topic in topics)
{
    if (string.IsNullOrWhiteSpace(topic.Subject)) continue;
    var slug = VaultSlug.Slugify(topic.Subject);
    var path = $"memory/topics/{slug}.md";

    // Union of contributing sources = whatever the page already records ∪ this source.
    var existing = await _store.ReadAsync(path);
    var sourceRefs = ReadPageSources(existing);          // from frontmatter sources: (raw), see below
    if (!sourceRefs.Contains(sourceRef, StringComparer.OrdinalIgnoreCase))
        sourceRefs.Add(sourceRef);

    var title = existing?.Frontmatter.GetValueOrDefault("title") is { Length: > 0 } t ? t : topic.Subject;
    var category = existing?.Frontmatter.GetValueOrDefault("category") is { Length: > 0 } c ? c : topic.Category;

    var summary = await SynthesizePageAsync(path, title, category, sourceRefs, charter, ct);
    if (summary is null) continue;                       // empty synthesis (no provider) — skip
    touched.Add(path);
    await _index.UpsertEntryAsync(path, summary);
}

if (touched.Count == 0)
    return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);

await _log.AppendAsync("ingest", sourceName + " -> " + string.Join(", ", TouchedTargets(touched)), date);
return new IngestResult(sourceRef, touched);
```

**Page-on-disk layout (fixed).** Every managed topic page is exactly:

```
---\n<frontmatter incl. sources: + category:>\n---\n
[optional manual preamble text]
<!-- pia:managed -->
<synthesized body>
```

The `<!-- pia:managed -->` line is a **mandatory sentinel** owned by the writer (NOT the synthesizer — the synthesizer returns body text only). It is the single source of truth for the preamble/body split. **Do NOT rely on `doc.Preamble`**: the parser sets `Preamble` to *all* content up to the first `## ` heading (`MarkdownVaultParser.cs:67-71`), and synthesized bodies normally have no `##`, so `doc.Preamble` would be the entire previous body → the preamble would accumulate/duplicate the whole page on every re-ingest. Split the RAW text on the sentinel string instead.

Shared helper used by ingest AND removal — reads each source's raw text, synthesizes, writes `frontmatter + preamble + sentinel + body`, preserving identity + manual preamble. Returns the index summary (or null on empty):

```csharp
private const string ManagedMarker = "<!-- pia:managed -->";

// Returns the index one-liner, or null when synthesis produced nothing (page left untouched).
private async Task<string?> SynthesizePageAsync(
    string path, string title, string category,
    List<string> sourceRefs, string charter, CancellationToken ct)
{
    var sources = new List<(string Ref, string Text)>();
    foreach (var r in sourceRefs)
    {
        var text = await TryReadSourceAsync(r, ct);       // containment + IsTextSource + exists; null-skips
        if (text is not null) sources.Add((r, text));
    }
    if (sources.Count == 0) return null;

    var existing = await _store.ReadAsync(path);
    var page = await _synth.SynthesizeAsync(title, category, charter, sources, ct);
    if (string.IsNullOrWhiteSpace(page.Body)) return null;

    // Manual preamble = raw text between the frontmatter close and the sentinel, split on the RAW text
    // (never doc.Preamble). "" for a new page or one with no manual text above the marker.
    var preamble = ExtractManualPreamble(existing?.RawText);   // see helper below

    var sb = new StringBuilder();
    sb.Append(VaultFrontmatter.BuildPreserving(existing, title, category)); // preserves id/created
    sb.Append('\n');
    if (preamble.Length > 0) sb.Append(preamble.TrimEnd()).Append("\n\n");
    sb.Append(ManagedMarker).Append('\n');
    sb.Append(page.Body.Trim()).Append('\n');

    var content = sb.ToString();
    // Record the source set in frontmatter via the EXISTING helper (keep it): reuse
    // RewriteSourcesFrontmatterAsync's line-building, or set "sources: [a, b]" directly on the block
    // we just built. Prefer factoring the sources-line writer so both create + rewrite share it.
    content = WriteSourcesLine(content, sourceRefs);   // "sources: [a, b]" into the frontmatter block
    await _store.WriteAtomicAsync(path, content);
    return page.Summary;
}

// Everything after the closing '---\n' and before the sentinel; "" if no sentinel or no such text.
private static string ExtractManualPreamble(string? raw)
{
    if (string.IsNullOrEmpty(raw)) return string.Empty;
    var body = StripFrontmatter(raw);                  // content after the closing '---' line
    var marker = body.IndexOf(ManagedMarker, StringComparison.Ordinal);
    var preamble = marker < 0 ? body : body[..marker]; // no sentinel (user page) → all of it is manual
    return preamble.Trim();
}
```

Notes for the implementer:
- **`ReadPageSources`:** `private static List<string> ReadPageSources(VaultDocument? doc) => doc is null ? new() : SourcesProvenance.ReadSourceRefs(doc.RawText).ToList();` — `ReadSourceRefs` returns `IReadOnlyList<string>` (`SourcesProvenance.cs:36`), so `.ToList()` is required before `.Add`/`.RemoveAll`.
- **`StripFrontmatter` / `WriteSourcesLine`:** reuse the frontmatter-splitting logic already in `RewriteSourcesFrontmatterAsync` (open `---\n`, close `\n---`) rather than hand-rolling a second parser; factor the shared bits out. Keep `SourcesProvenance.ParseFlowList`/`FindKeyValue`.
- **`TryReadSourceAsync`:** factor the containment guard + `IsTextSource` + `File.ReadAllTextAsync` out of the old `IngestAsync` step 1 so both the initial guard and the per-topic reads share it. It resolves `sourceRef` under `_store.Root`, applies the containment check (`StartsWith(rootFull + sep)`), skips non-text/missing → returns null.
- **Delete** `UpsertSourceSectionAsync`, `NormalizeFactsToBullets`, `HeadingLineStart`, `SourceHeadingPrefix`, `SourceHeading`, `IsSectionFor`, `FirstLine`, and the crosslink loop — all obsolete. Keep `SpliceSectionAsync` on `IVaultStore` (still used by `MemoryService.cs:666,918`); only the ingest-side usage goes.
- **`_embeddings` ctor param is unused** (assigned, never read) — remove it from `IngestService` and update the ctor call sites (Bootstrapper + tests).

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `feat(ingest): topic-driven cross-source synthesis pipeline`

### Task 6: `RemoveContributionsAsync` → re-synthesize from remaining sources

**Files:** Modify `IngestService.cs`; add tests to `IngestServiceTests.cs`.

- [ ] **Step 1: Failing tests:**
  1. `Remove_last_source_deletes_page_and_index` — page with one source; remove it → page + index entry gone.
  2. `Remove_one_of_two_sources_resynthesizes` — page with sources A,B; remove A → page remains, `sources:` == [B], synthesizer re-called with only B's text.
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement.** Rewrite `RemoveContributionsAsync`:

```csharp
foreach (var path in pages)
{
    var doc = await _store.ReadAsync(path);
    if (doc is null) continue;

    var remaining = ReadPageSources(doc);
    remaining.RemoveAll(r => r.Equals(sourceRef, StringComparison.OrdinalIgnoreCase));

    if (remaining.Count == 0)
    {
        await _store.DeleteAsync(path);
        await _index.RemoveEntryAsync(path);
        continue;
    }

    var title = doc.Frontmatter.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(path);
    var category = doc.Frontmatter.GetValueOrDefault("category") ?? "concept";
    var summary = await SynthesizePageAsync(path, title, category, remaining, await _charter.GetCharterAsync(), ct);
    if (summary is not null) await _index.UpsertEntryAsync(path, summary);
}
// keep the existing removal log line.
```

Delete the old section-splice removal code.

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `feat(ingest): re-synthesize pages on source removal`

---

## Chunk 4: Categorized index

### Task 7: Sub-group the Topics section by `category`

**Files:** Modify `src/Pia.Wpf/Services/Wiki/VaultIndexService.cs`; test `tests/Pia.Wpf.Tests/Wiki/` (new `VaultIndexServiceTests` or extend).

The top-level `## Topics` group stays (path-derived). Within it, emit `### People / ### Organizations / ### Products / ### Concepts / ### Regulations / ### Technology / ### Other` sub-headings ordered canonically, reading each topic page's frontmatter `category` at rewrite time.

- [ ] **Step 1: Failing test** — seed two topic pages (`category: product`, `category: regulation`), upsert both, read `index.md`; assert `## Topics` contains `### Products` before `### Regulations`, each with its entry; a page with no `category` lands under `### Other`.
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement.** In `RewriteAsync`, when `type == "topic"`, instead of a flat entry list, group that list by the page's `category` (read via `await _store.ReadAsync("memory/" + target + ".md")`, frontmatter `category`, default `other`) and emit `###` sub-headings in a fixed order. Add:

```csharp
private static readonly (string Category, string Display)[] TopicCategories =
[
    ("person", "People"), ("organization", "Organizations"), ("product", "Products"),
    ("concept", "Concepts"), ("regulation", "Regulations"), ("technology", "Technology"),
    ("other", "Other"),
];
```

Keep the whole rewrite deterministic (entries already ordinal-sorted). Note: reading N pages per rewrite is acceptable (ingest is serial/background, topic count is small).

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `feat(index): sub-group topics by category`

---

## Chunk 5: Wiring, migration, verification

### Task 8: Bootstrapper registration

**Files:** Modify `src/Pia.Wpf/Bootstrapper.cs` (near lines 379–398).

- [ ] **Step 1:** Add `services.AddSingleton<Pia.Services.Wiki.VaultCharterService>();` and `services.AddSingleton<IIngestSynthesizer, Pia.Services.Wiki.AiIngestSynthesisService>();`. Confirm `IngestService`'s new ctor params resolve.
- [ ] **Step 2: Build** — `dotnet build src/Pia.Wpf/Pia.Wpf.csproj` → succeeds.
- [ ] **Step 3: Commit** — `chore(di): register charter + synthesizer services`

### Task 9: One-time migration (code path, not prose)

**Why a code path:** `IngestStateStore` is SQLite-backed (`Bootstrapper.cs:384`, `SqliteContext.ConnectionString`), so an operator cannot "clear the state file." And the hash gate (`AutoIngestService.cs:248-252`) means that if the old topic pages are deleted but the state rows remain, reconcile sees unchanged hashes and **no-ops** — the fresh re-ingest never happens. So migration must clear state in code.

**Files:**
- Modify: `src/Pia.Wpf/Services/Wiki/IngestStateStore.cs` — add `Task ClearAllAsync()`.
- Modify: `src/Pia.Wpf/Models/AppSettings.cs` — add `int IngestSchemaVersion { get; set; } = 0;` (JSON-only, mirrors the `AutoIngestSources` precedent).
- Modify: `src/Pia.Wpf/Bootstrapper.cs` — run the one-shot migration on startup, before `AutoIngestService.StartAsync()` (line ~187).
- Test: `tests/Pia.Wpf.Tests/Wiki/IngestStateStoreTests.cs` — `ClearAllAsync_removes_all_rows`.

- [ ] **Step 1: Failing test** for `IngestStateStore.ClearAllAsync()` — upsert two entries, `ClearAllAsync()`, assert `ListAsync()` is empty.
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement**
  - `ClearAllAsync()` — `DELETE FROM` the state table (own connection per op, per the store's existing pattern — never the shared `SqliteContext.GetConnection()`).
  - Startup migration: insert it in the window **after `VaultWatcher.Start()` (`Bootstrapper.cs:175`) and before `AutoIngestService.StartAsync()` (line 187)** — so the live watcher de-indexes the deleted topic pages (put it ahead of the watcher and the old recall chunks are briefly orphaned until reconcile rewrites them). If `settings.IngestSchemaVersion < 1`: enumerate `memory/topics/*.md` via `IVaultStore.EnumerateAsync`, `DeleteAsync` each and `VaultIndexService.RemoveEntryAsync` each (so stale index entries go too); `await ingestState.ClearAllAsync()`; set `IngestSchemaVersion = 1` and save settings. Guard the whole thing in try/catch + log (a migration failure must not block startup). The subsequent `AutoIngestService` reconcile then rebuilds every source fresh under the synthesis pipeline.
- [ ] **Step 4: PASS.** Build the client (`dotnet build src/Pia.Wpf/Pia.Wpf.csproj`).
- [ ] **Step 5: Commit** — `feat(ingest): one-time migration to synthesis pipeline`

### Task 10: Full baseline gate + manual smoke

- [ ] **Step 1:** Run the full suite with the baseline filter (MTP runner). Expected: no failures beyond the 2 known pre-existing ones.
- [ ] **Step 2:** Update `AutoIngestServiceTests` if any assertion depended on `## Source:` sections or `ExtractedEntity.Facts`; re-run until green.
- [ ] **Step 3 (human-gated):** With a real provider configured, drop a source into `sources/`, confirm a synthesized topic page + categorized index entry appears in the Memory view; change the source and confirm re-synthesis; delete it and confirm removal/re-synthesis. (Per project memory: do NOT use winwright — build/run and observe.)
- [ ] **Step 4: Commit** any test adjustments — `test(ingest): align auto-ingest tests with synthesis pipeline`

---

## Risks & notes for the implementer

- **Parser preamble/body boundary (Task 5)** — resolved by the mandatory `<!-- pia:managed -->` sentinel, split on the RAW page text (NOT `doc.Preamble`, which folds a no-`##` body into the preamble and would accumulate the whole page each re-ingest). The writer owns the sentinel in one place; the synthesizer never emits it. Read `MarkdownVaultParserTests` to confirm the sentinel line survives a parse/re-serialize round-trip.
- **Identity (Task 4/5)** — re-synthesis rewrites the whole page, so it MUST go through `VaultFrontmatter.BuildPreserving` to keep `id`/`created` stable (sync keys on `id`; `VaultWikiTests.cs:74` asserts it). Never call plain `Build` on an existing page.
- **CRLF:** new `.cs` files must be CRLF (project memory) — convert before the byte-sensitive tests run.
- **Cost:** ingest now makes 1 discovery call + 1 synthesis call per touched topic (re-reading the union of raw sources each time). Acceptable at this vault's scale; `AutoIngestService` already serializes and hash-gates.
- **v1 limitation (from spec §8):** cross-source union only covers sources that each independently surface a topic as notable; a future lint pass can broaden it. Do not attempt to solve it here.
- **Recall indexing (advisory):** a heading-less synthesized body means the whole content region (preamble + sentinel + body) is one `PreambleSlug` recall chunk (`VaultIndexer.cs:172`) — functionally fine, body stays recall-visible. The literal `<!-- pia:managed -->` marker lands in the embedded text; harmless, but optionally strip it from the text handed to the indexer if it ever matters.
- **`SummarizeAsync` removal:** the old index one-liner fell back to the source summary. The synthesizer now supplies the summary, so `SummarizeAsync` can be deleted from `IIngestExtractor` — verify no other caller (grep) before removing.
