using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Integration;

[Trait("Category", "Integration")]
public class MemoryToolIntegrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly SectionUpsertService _upsert;
    private readonly ILocalizationService _localization;

    public MemoryToolIntegrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _upsert = new SectionUpsertService(_embeddings);

        _localization = Substitute.For<ILocalizationService>();
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => string.Format(ci.ArgAt<string>(0), ci.ArgAt<object[]>(1)));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    private MemoryService BuildMemoryService()
        => new(_ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store, _upsert);

    private MemoryToolHandler BuildHandler(IMemoryService memory, IIngestScheduler? ingestScheduler = null)
        => new(memory, _embeddings, _localization, ingestScheduler ?? Substitute.For<IIngestScheduler>(),
            NullLogger<MemoryToolHandler>.Instance);

    private static FunctionCallContent RememberCall(string type, string subject, string content)
        => new(
            callId: Guid.NewGuid().ToString(),
            name: "remember",
            arguments: new Dictionary<string, object?>
            {
                ["type"] = type,
                ["subject"] = subject,
                ["content"] = content,
            });

    // DEDUP PROOF: two remember tool-calls for the SAME subject must collapse into ONE "## John Smith"
    // section with a merged body. This runs with NO API key — we call HandleToolCallAsync directly with a
    // FunctionCallContent for "remember", then ExecutePendingActionAsync on the returned pending action,
    // over a REAL MemoryService (real SectionUpsertService + StubEmbeddingService) writing to a temp vault.
    [Fact]
    public async Task Remember_SameSubjectTwice_DedupsIntoOneSection()
    {
        var memory = BuildMemoryService();
        var handler = BuildHandler(memory);

        // First call: creates the section. Resolution band is Create -> pending action -> execute (write).
        var (firstResult, firstPending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- email: a@x"), TestContext.Current.CancellationToken);
        Assert.Null(firstResult);
        Assert.NotNull(firstPending);
        Assert.Equal("remember", firstPending!.ToolName);
        await handler.ExecutePendingActionAsync(firstPending);

        // Second call, SAME subject: resolution band is Edit -> pending action -> execute (merge, no dup).
        var (secondResult, secondPending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- phone: 5"), TestContext.Current.CancellationToken);
        Assert.Null(secondResult);
        Assert.NotNull(secondPending);
        await handler.ExecutePendingActionAsync(secondPending);

        // Assert: contacts.md has EXACTLY ONE "## John Smith" section with a merged body.
        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        var sections = doc!.Sections.Where(s => s.Heading == "John Smith").ToList();
        Assert.Single(sections);

        Assert.Contains("a@x", sections[0].Body);
        Assert.Contains("phone: 5", sections[0].Body);
    }

    // recall returns hits immediately (no pending action). After remembering a contact, recalling its
    // subject must surface a RecallHit for that section.
    [Fact]
    public async Task Recall_AfterRemember_ReturnsImmediateHits()
    {
        var memory = BuildMemoryService();
        var handler = BuildHandler(memory);

        var (_, pending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- email: a@x"), TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        await handler.ExecutePendingActionAsync(pending!);

        // Index the vault so the Chunks-backed recall has something to match.
        await memory.RecallAsync("John Smith");

        var recallCall = new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: "recall",
            arguments: new Dictionary<string, object?> { ["query"] = "John Smith" });

        var (result, recallPending) = await handler.HandleToolCallAsync(recallCall, TestContext.Current.CancellationToken);

        // recall is immediate: a result object, never a pending action. The tool now wraps the hits in a
        // RecallResult that carries the standing "topic hits are expandable" Note (the drill nudge); the
        // bare hit list stays on IMemoryService.RecallAsync for the Vault view.
        Assert.Null(recallPending);
        Assert.NotNull(result);
        var recallResult = Assert.IsType<RecallResult>(result);
        Assert.NotNull(recallResult.Hits);
        Assert.False(string.IsNullOrWhiteSpace(recallResult.Note));
    }

    private void SeedFile(string relativePath, string content)
    {
        var full = Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // A topic page whose `sources:` provenance cites a raw source, plus that source. The revenue figure
    // lives ONLY in the source and is merely alluded to in the topic — the drill scenario in miniature.
    private void SeedAcmeTopicAndSource()
    {
        SeedFile("memory/topics/acme-corp.md",
            "---\ntype: topic\ncategory: organization\ntitle: Acme Corp\n"
            + "sources: [sources/acme-notes.txt]\nupdated: 2026-07-12T00:00:00Z\n---\n"
            + "<!-- pia:managed -->\nAcme Corp is a global supplier.\n\n"
            + "The exact revenue figure lives in the cited source.\n");
        SeedFile("sources/acme-notes.txt", "Acme revenue in 2025 was 4.2 billion USD.\n");
    }

    private static FunctionCallContent NavCall(string name, IDictionary<string, object?>? args = null)
        => new(
            callId: Guid.NewGuid().ToString(),
            name: name,
            arguments: args ?? new Dictionary<string, object?>());

    // browse_index (orient rung) returns the category map, and each entry carries a read_topic HANDLE —
    // the vault-relative path — not just a title, so the model can chain it straight into read_topic.
    [Fact]
    public async Task BrowseIndex_ReturnsCategoriesWithTopicHandles()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            NavCall("browse_index"), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        var index = Assert.IsType<BrowseIndexResult>(result);
        var entries = index.Categories.SelectMany(c => c.Entries).ToList();
        Assert.Contains(entries, e => e.Ref == "memory/topics/acme-corp.md");
    }

    // A synthesized topic with ## subheadings must surface as ONE orient-map entry for the page, not one
    // per subheading (which would clutter the map and hide the topic title).
    [Fact]
    public async Task BrowseIndex_SubheadedTopic_CollapsesToOnePageEntry()
    {
        SeedFile("memory/topics/widget.md",
            "---\ntype: topic\ncategory: product\ntitle: Widget\n---\n<!-- pia:managed -->\nIntro.\n\n"
            + "## History\nStuff.\n\n## Design\nMore.\n");
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("browse_index"), TestContext.Current.CancellationToken);

        var index = Assert.IsType<BrowseIndexResult>(result);
        var widgetEntries = index.Categories
            .SelectMany(c => c.Entries)
            .Where(e => e.Ref == "memory/topics/widget.md")
            .ToList();
        Assert.Single(widgetEntries);
    }

    // Without a summary the model has to read_topic every entry to find out what it says — which is the
    // whole cost of a large vault. The map has to be triageable on its own.
    [Fact]
    public async Task BrowseIndex_EntriesCarryAOneLineSummary()
    {
        SeedFile("memory/topics/widget.md",
            "---\ntype: topic\ncategory: product\ntitle: Widget\n---\n<!-- pia:managed -->\n"
            + "A Widget is the unit Acme ships.\n\nMore detail follows.\n");
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("browse_index"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            Assert.IsType<BrowseIndexResult>(result).Categories.SelectMany(c => c.Entries),
            e => e.Ref == "memory/topics/widget.md");
        Assert.Equal("A Widget is the unit Acme ships.", entry.Summary);
    }

    // A page with ## headings is split into one item per section and its preamble is dropped, so the
    // summary can only come from the first section. Topic templates steer to bullets over headings for
    // exactly this reason; pinned so the weaker fallback is a known cost, not a surprise.
    [Fact]
    public async Task BrowseIndex_SummaryOfASubheadedTopicComesFromItsFirstSection()
    {
        SeedFile("memory/topics/gadget.md",
            "---\ntype: topic\ncategory: product\ntitle: Gadget\n---\n<!-- pia:managed -->\n"
            + "Intro prose that the section split discards.\n\n## History\nShipped in 2024.\n");
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("browse_index"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            Assert.IsType<BrowseIndexResult>(result).Categories.SelectMany(c => c.Entries),
            e => e.Ref == "memory/topics/gadget.md");
        Assert.Equal("Shipped in 2024.", entry.Summary);
    }

    // A person page opens with its template's field list; surfacing a personnel number as the summary
    // would be both useless and a needless disclosure in a map the model reads wholesale.
    [Fact]
    public async Task BrowseIndex_SummarySkipsTemplateFieldBullets()
    {
        SeedFile("memory/topics/ilka-brenner.md",
            "---\ntype: topic\ncategory: person\ntitle: Ilka Brenner\n---\n<!-- pia:managed -->\n"
            + "- personnel number: 4711\n- role: unknown\n\nOwns the Acme account.\n");
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("browse_index"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            Assert.IsType<BrowseIndexResult>(result).Categories.SelectMany(c => c.Entries),
            e => e.Ref == "memory/topics/ilka-brenner.md");
        Assert.Equal("Owns the Acme account.", entry.Summary);
        Assert.DoesNotContain("4711", entry.Summary, StringComparison.Ordinal);
    }

    // read_topic (read rung) returns the FULL body (frontmatter + managed sentinel stripped) and surfaces
    // the source refs the page cites — the handles read_source consumes.
    [Fact]
    public async Task ReadTopic_ReturnsFullBodyAndCitedSources()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_topic", new Dictionary<string, object?> { ["reference"] = "memory/topics/acme-corp.md" }),
            TestContext.Current.CancellationToken);

        var topic = Assert.IsType<TopicRead>(result);
        Assert.True(topic.Found);
        Assert.Contains("global supplier", topic.Body);
        Assert.DoesNotContain("pia:managed", topic.Body);   // sentinel stripped
        Assert.DoesNotContain("type: topic", topic.Body);   // frontmatter stripped
        Assert.Contains("sources/acme-notes.txt", topic.Sources);
    }

    // A topic with no `sources:` frontmatter must surface an EMPTY ref list (visible fail) — never a
    // silent dead-end — so the model can fall back to browse_index.
    [Fact]
    public async Task ReadTopic_NoProvenance_SurfacesEmptySources()
    {
        SeedFile("memory/topics/orphan.md",
            "---\ntype: topic\ncategory: concept\ntitle: Orphan\n---\n<!-- pia:managed -->\nNo sources frontmatter here.\n");
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_topic", new Dictionary<string, object?> { ["reference"] = "memory/topics/orphan.md" }),
            TestContext.Current.CancellationToken);

        var topic = Assert.IsType<TopicRead>(result);
        Assert.True(topic.Found);
        Assert.Empty(topic.Sources);
    }

    // The policy guard (IsRecallIndexable) — distinct from containment — rejects a sources/ path from
    // read_topic: raw sources are reached only through read_source.
    [Fact]
    public async Task ReadTopic_SourcesPath_RejectedByPolicyGuard()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_topic", new Dictionary<string, object?> { ["reference"] = "sources/acme-notes.txt" }),
            TestContext.Current.CancellationToken);

        var topic = Assert.IsType<TopicRead>(result);
        Assert.False(topic.Found);
    }

    // read_source (drill rung) reads the raw primary text the topic only summarized.
    [Fact]
    public async Task ReadSource_ReadsRawText()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_source", new Dictionary<string, object?> { ["reference"] = "sources/acme-notes.txt" }),
            TestContext.Current.CancellationToken);

        var source = Assert.IsType<SourceRead>(result);
        Assert.True(source.Found);
        Assert.Contains("4.2 billion", source.Text);
    }

    // Containment guard: a ../ escape that stays under a sources/ prefix (so it passes the scope check) is
    // still rejected because it resolves outside the vault.
    [Fact]
    public async Task ReadSource_PathTraversal_RejectedByContainment()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_source", new Dictionary<string, object?> { ["reference"] = "sources/../../secret.txt" }),
            TestContext.Current.CancellationToken);

        var source = Assert.IsType<SourceRead>(result);
        Assert.False(source.Found);
    }

    // Scope guard: only sources/ is readable — a memory/ ref is rejected before any read.
    [Fact]
    public async Task ReadSource_NonSourcesRef_RejectedByScope()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_source", new Dictionary<string, object?> { ["reference"] = "memory/topics/acme-corp.md" }),
            TestContext.Current.CancellationToken);

        var source = Assert.IsType<SourceRead>(result);
        Assert.False(source.Found);
    }

    // Guard-bypass regression: a ../ ref that STAYS inside the vault but resolves under sources/ must not
    // read a raw source through read_topic. The policy check runs on the canonical path, not the literal
    // "memory/..." prefix.
    [Fact]
    public async Task ReadTopic_TraversalIntoSources_RejectedByPolicy()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_topic", new Dictionary<string, object?> { ["reference"] = "memory/../sources/acme-notes.txt" }),
            TestContext.Current.CancellationToken);

        var topic = Assert.IsType<TopicRead>(result);
        Assert.False(topic.Found);
    }

    // Guard-bypass regression: a ../ ref under a sources/ prefix that resolves into memory/ must not read a
    // non-source through read_source. The scope check runs on the canonical path, not the literal
    // "sources/..." prefix.
    [Fact]
    public async Task ReadSource_TraversalIntoMemory_RejectedByScope()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_source", new Dictionary<string, object?> { ["reference"] = "sources/../memory/topics/acme-corp.md" }),
            TestContext.Current.CancellationToken);

        var source = Assert.IsType<SourceRead>(result);
        Assert.False(source.Found);
    }

    // forget returns a pending action whose Execute removes the addressed section.
    [Fact]
    public async Task Forget_PendingAction_RemovesSection()
    {
        var memory = BuildMemoryService();
        var handler = BuildHandler(memory);

        var (_, rememberPending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- email: a@x"), TestContext.Current.CancellationToken);
        Assert.NotNull(rememberPending);
        await handler.ExecutePendingActionAsync(rememberPending!);

        var forgetCall = new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: "forget",
            arguments: new Dictionary<string, object?> { ["reference"] = "memory/contacts.md#John Smith" });

        var (forgetResult, forgetPending) = await handler.HandleToolCallAsync(forgetCall, TestContext.Current.CancellationToken);
        Assert.Null(forgetResult);
        Assert.NotNull(forgetPending);
        Assert.Equal("forget", forgetPending!.ToolName);
        await handler.ExecutePendingActionAsync(forgetPending);

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);
        Assert.DoesNotContain(doc!.Sections, s => s.Heading == "John Smith");
    }

    // Ref-normalization regression: the files tools address a source as 'Vault/sources/<name>'
    // (relative to the assistant files folder); read_source must tolerate that spelling too, not just
    // the memory-tools' own 'sources/<name>' (relative to the vault root).
    [Fact]
    public async Task ReadSource_VaultPrefixedRef_Normalizes()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_source", new Dictionary<string, object?> { ["reference"] = "Vault/sources/acme-notes.txt" }),
            TestContext.Current.CancellationToken);

        var source = Assert.IsType<SourceRead>(result);
        Assert.True(source.Found);
        Assert.Contains("4.2 billion", source.Text);
    }

    // Same normalization, and the leading-slash tolerance read_topic previously lacked (read_source had
    // it, read_topic did not — an inconsistency fixed alongside the Vault/-prefix normalization).
    [Fact]
    public async Task ReadTopic_LeadingSlashRef_Normalizes()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, _) = await handler.HandleToolCallAsync(
            NavCall("read_topic", new Dictionary<string, object?> { ["reference"] = "/memory/topics/acme-corp.md" }),
            TestContext.Current.CancellationToken);

        var topic = Assert.IsType<TopicRead>(result);
        Assert.True(topic.Found);
    }

    private static FunctionCallContent UpdateSourceCall(string reference, string content)
        => new(
            callId: Guid.NewGuid().ToString(),
            name: "update_source",
            arguments: new Dictionary<string, object?> { ["reference"] = reference, ["content"] = content });

    private static IngestResult SuccessfulReingest(string sourceRef, params string[] touchedPages)
        => new(sourceRef, touchedPages);

    // Happy path: update_source previews a real diff, writes the new content, and re-ingests
    // synchronously via IIngestScheduler.RunAsync — the same manual entry point the ingest tool uses.
    [Fact]
    public async Task UpdateSource_HappyPath_WritesContentAndReingests()
    {
        SeedAcmeTopicAndSource();
        var scheduler = Substitute.For<IIngestScheduler>();
        scheduler.RunAsync("sources/acme-notes.txt", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SuccessfulReingest("sources/acme-notes.txt", "memory/topics/acme-corp.md")));
        var handler = BuildHandler(BuildMemoryService(), scheduler);

        var (result, pending) = await handler.HandleToolCallAsync(
            UpdateSourceCall("sources/acme-notes.txt", "Acme revenue in 2025 was 5.0 billion USD.\n"),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("update_source", pending!.ToolName);
        Assert.NotNull(pending.DiffPreview);
        Assert.NotEmpty(pending.DiffPreview!);
        Assert.Equal("sources/acme-notes.txt", pending.TargetPath);

        var execResult = await handler.ExecutePendingActionAsync(pending);
        Assert.Contains("Updated", execResult?.ToString());
        Assert.Contains("acme-corp.md", execResult?.ToString());

        var written = await File.ReadAllTextAsync(
            Path.Combine(_vaultRoot, "sources", "acme-notes.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("5.0 billion", written);
        await scheduler.Received(1).RunAsync("sources/acme-notes.txt", Arg.Any<CancellationToken>());
    }

    // update_source only corrects an EXISTING source — creating one stays on the write_file+ingest path.
    [Fact]
    public async Task UpdateSource_MissingSource_ReturnsErrorWithoutPendingAction()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            UpdateSourceCall("sources/does-not-exist.txt", "new text"), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("not found", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Argument hardening: a missing 'content' key must not be treated as "replace with empty" — that
    // would silently wipe the source if the model forgot the argument.
    [Fact]
    public async Task UpdateSource_MissingContentArg_ReturnsErrorWithoutPendingAction()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            NavCall("update_source", new Dictionary<string, object?> { ["reference"] = "sources/acme-notes.txt" }),
            TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("content", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Scope guard: only sources/ can be updated here — reuses read_source's own scope check.
    [Fact]
    public async Task UpdateSource_NonSourcesRef_RejectedByScope()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            UpdateSourceCall("memory/topics/acme-corp.md", "malicious replacement"),
            TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("sources/", result?.ToString());
    }

    // Containment guard: reuses read_source's own containment check (../ escape).
    [Fact]
    public async Task UpdateSource_PathTraversal_RejectedByContainment()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            UpdateSourceCall("sources/../../secret.txt", "malicious replacement"),
            TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("outside", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // TOCTOU guard: if the source changes on disk between preview and approval, the approved diff no
    // longer matches current content — Execute must refuse rather than silently clobber it.
    [Fact]
    public async Task UpdateSource_ChangedOnDiskSincePreview_BlockedByToctou()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (_, pending) = await handler.HandleToolCallAsync(
            UpdateSourceCall("sources/acme-notes.txt", "The corrected figure.\n"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(pending);

        var path = Path.Combine(_vaultRoot, "sources", "acme-notes.txt");
        File.WriteAllText(path, "Someone else edited this out of band.\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var execResult = await handler.ExecutePendingActionAsync(pending!);
        Assert.Contains("changed on disk", execResult?.ToString(), StringComparison.OrdinalIgnoreCase);

        // The out-of-band edit must survive — the blocked write never touched the file.
        var onDisk = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("out of band", onDisk);
    }

    private static FunctionCallContent CreateSourceCall(string reference, string content)
        => new(
            callId: Guid.NewGuid().ToString(),
            name: "create_source",
            arguments: new Dictionary<string, object?> { ["reference"] = reference, ["content"] = content });

    // Happy path: create_source previews an all-added diff, writes a brand-new file, and ingests
    // synchronously via IIngestScheduler.RunAsync — no separate ingest call needed.
    [Fact]
    public async Task CreateSource_HappyPath_WritesContentAndIngests()
    {
        var scheduler = Substitute.For<IIngestScheduler>();
        scheduler.RunAsync("sources/new-notes.txt", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SuccessfulReingest("sources/new-notes.txt", "memory/topics/new-notes.md")));
        var handler = BuildHandler(BuildMemoryService(), scheduler);

        var (result, pending) = await handler.HandleToolCallAsync(
            CreateSourceCall("sources/new-notes.txt", "Fresh content pasted from chat.\n"),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("create_source", pending!.ToolName);
        Assert.NotNull(pending.DiffPreview);
        Assert.All(pending.DiffPreview!, d => Assert.Equal(DiffLineKind.Added, d.Kind));
        Assert.Equal("sources/new-notes.txt", pending.TargetPath);

        var execResult = await handler.ExecutePendingActionAsync(pending);
        Assert.Contains("Created", execResult?.ToString());
        Assert.Contains("new-notes.md", execResult?.ToString());

        var written = await File.ReadAllTextAsync(
            Path.Combine(_vaultRoot, "sources", "new-notes.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("Fresh content pasted from chat.", written);
        await scheduler.Received(1).RunAsync("sources/new-notes.txt", Arg.Any<CancellationToken>());
    }

    // A nested ref must auto-create its parent directory — AtomicTextWriter does not do this itself.
    [Fact]
    public async Task CreateSource_NestedRef_CreatesParentDirectory()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (_, pending) = await handler.HandleToolCallAsync(
            CreateSourceCall("sources/meetings/2026-08-11.txt", "Meeting notes.\n"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(pending);

        await handler.ExecutePendingActionAsync(pending!);

        var written = await File.ReadAllTextAsync(
            Path.Combine(_vaultRoot, "sources", "meetings", "2026-08-11.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("Meeting notes.", written);
    }

    // create_source only stages a NEW source — an existing ref is rejected and pointed at update_source.
    [Fact]
    public async Task CreateSource_AlreadyExists_ReturnsErrorPointingAtUpdateSource()
    {
        SeedAcmeTopicAndSource();
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            CreateSourceCall("sources/acme-notes.txt", "replacement"), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("already exists", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update_source", result?.ToString());
    }

    // Argument hardening: mirrors update_source's own missing-content guard.
    [Fact]
    public async Task CreateSource_MissingContentArg_ReturnsErrorWithoutPendingAction()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            NavCall("create_source", new Dictionary<string, object?> { ["reference"] = "sources/new.txt" }),
            TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("content", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Scope guard: reuses the same TryResolveSourceScope chain update_source's equivalent test covers.
    [Fact]
    public async Task CreateSource_NonSourcesRef_RejectedByScope()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            CreateSourceCall("memory/topics/new-topic.md", "malicious content"),
            TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("sources/", result?.ToString());
    }

    // Containment guard: reuses the same ../ escape case update_source's equivalent test covers.
    [Fact]
    public async Task CreateSource_PathTraversal_RejectedByContainment()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (result, pending) = await handler.HandleToolCallAsync(
            CreateSourceCall("sources/../../secret.txt", "malicious content"),
            TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.Contains("outside", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Create-side collision guard (the TOCTOU equivalent for a create): if the ref appears on disk
    // between preview and approval, Execute must refuse rather than overwrite it.
    [Fact]
    public async Task CreateSource_AppearedOnDiskSincePreview_BlockedByCollisionGuard()
    {
        var handler = BuildHandler(BuildMemoryService());

        var (_, pending) = await handler.HandleToolCallAsync(
            CreateSourceCall("sources/race.txt", "My content.\n"), TestContext.Current.CancellationToken);
        Assert.NotNull(pending);

        SeedFile("sources/race.txt", "Someone else created this out of band.\n");

        var execResult = await handler.ExecutePendingActionAsync(pending!);
        Assert.Contains("now exists", execResult?.ToString(), StringComparison.OrdinalIgnoreCase);

        // The out-of-band file must survive — the blocked write never touched it.
        var onDisk = await File.ReadAllTextAsync(
            Path.Combine(_vaultRoot, "sources", "race.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("out of band", onDisk);
    }
}
