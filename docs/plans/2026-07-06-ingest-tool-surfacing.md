# Plan — Surface the `ingest` tool + make ingested content recallable

Date: 2026-07-06. Branch: `feature/meeting_attendee`. Brief: `ingest-surfacing-brief.md` (scratchpad) — all facts re-verified against the working tree before this plan was written; no discrepancies found.

Goal: the model can call `ingest(source_ref)` (new built-in plugin), the tool tolerates the files-tool `Vault/…` path spelling, returns a readable string, and — the blocker — freeform vault files (note/project/topic, the format ingest writes) become visible to `recall` via a synthetic preamble chunk in the indexer.

**Decisions already made (do not relitigate):** path mismatch = light-touch normalize in the tool handler + staging recipe in the system prompt; recall visibility = synthetic preamble chunk emitted by `VaultIndexer` with a coordinated snippet branch in `MemoryService`.

**No new .cs files are created** — every step edits an existing file, so the CRLF rule needs no action (the Edit tool preserves existing line endings). If you deviate and create a new .cs file, convert it to CRLF before finishing.

Build: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug`
Test gate: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` — zero failures OUTSIDE `Pia.Wpf.Tests.Integration.Providers` (~18 known live-network failures inside it are pre-existing; ignore only those).
Do NOT commit — leave all changes in the working tree.

Ordering note: steps 1–3 are one atomic unit — `PluginService.InitializeBuiltInPlugins()` throws `InvalidOperationException` for any `Defaults` entry whose `handlerId` has no switch arm, so never build/run with step 1 applied but step 3 missing. Steps 4–6 are independent of each other; step 5 depends on nothing else; step 7 depends on 4–6.

---

## Step 1 — New built-in `ingest` plugin

**File:** `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs`

1. After the `FilesPluginId` field (line 19), add:

```csharp
    public static readonly Guid IngestPluginId = new("10000000-0000-0000-0000-000000000007");
```

2. Add `IngestPluginId` to `PreloadedPluginIds` (lines 21–23):

```csharp
    public static readonly HashSet<Guid> PreloadedPluginIds = [
        MemoryPluginId, TodoPluginId, ReminderPluginId,
        ScheduledResearchPluginId, ResearchHistoryPluginId, FilesPluginId, IngestPluginId];
```

3. Add a new `Defaults` entry after the `[FilesPluginId]` entry (mirror its shape exactly). The `ConfigJson` is a C# raw string literal (`"""…"""`), so `\"` and `\n` inside it are literal two-char JSON escapes — exactly like the existing entries:

```csharp
        [IngestPluginId] = new SyncPlugin
        {
            Id = IngestPluginId,
            Kind = "builtin_tool_pack",
            Name = "ingest",
            Description = "Compile raw documents from the vault's sources folder into recallable memory topic pages.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"ingest","defaultEnabled":true,"systemPromptAddition":"You can compile raw documents into recallable memory. Raw files live in the assistant vault's 'sources/' folder. Call ingest with the vault-relative path (e.g. ingest(\"sources/q2-report.txt\")) to extract the key entities from the file and write one memory topic page per entity — after that the content can be found with recall. To stage a NEW document: use the files tools to write it to 'Vault/sources/<name>' (the vault is the 'Vault' folder inside the assistant files folder), then call ingest(\"sources/<name>\"). Re-ingesting the same source does not create duplicates. Only text files are supported (e.g. txt, md, csv, json, html, xml, log)."}""",
            UpdatedAt = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)
        }
```

Risk/notes: client-only built-in — `IsPreloaded=true` entries are never persisted to DB and never deleted by server sync (`ApplyServerPluginsAsync` skips `PreloadedPluginIds`), so no server change is needed. No test asserts the built-in count or the full `PreloadedPluginIds` set (verified). No resx strings — the prompt is an English string inside `ConfigJson` like all other built-ins.

## Step 2 — `FromIngestHandler` factory

**File:** `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs`

Add after `FromFilesHandler` (line 182), before `GetSystemPromptFromConfig`. Key difference from the other factories: `IIngestToolHandler.HandleToolCallAsync` returns `Task<object?>` (plain result, NO `(result, pending)` tuple — ingest runs inline), so adapt to `(result, null)` and make `executePending` throw (it can never be invoked because `handleCall` never returns a pending action):

```csharp
    /// <summary>
    /// Factory: creates adapter wrapping IIngestToolHandler. Ingest runs inline (no pending-action
    /// confirmation card): the handler returns a plain result, so handleCall adapts it to a
    /// (result, no-pending) tuple and executePending is unreachable.
    /// </summary>
    public static BuiltInPluginHandler FromIngestHandler(
        IIngestToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) => (await handler.HandleToolCallAsync(toolCall, ct), (PluginToolCall?)null),
            _ => throw new InvalidOperationException("The ingest plugin has no pending actions."),
            GetSystemPromptFromConfig(config.ConfigJson));
    }
```

No new usings needed (`Pia.Services.Interfaces` is already imported).

## Step 3 — Wire `ingest` into `PluginService`

**File:** `src/Pia.Wpf/Services/Plugins/PluginService.cs`

1. Add a field after `_filesToolHandler` (line 21): `private readonly IIngestToolHandler _ingestToolHandler;`
2. Add ctor parameter after `IFilesToolHandler filesToolHandler,` (line 46): `IIngestToolHandler ingestToolHandler,` and the assignment `_ingestToolHandler = ingestToolHandler;` after `_filesToolHandler = filesToolHandler;` (line 56).
3. Add a switch arm in `InitializeBuiltInPlugins()` after the `"files"` arm (line 82):

```csharp
                "ingest" => BuiltInPluginHandler.FromIngestHandler(_ingestToolHandler, config),
```

Call sites: **verified — there are NO `new PluginService(...)` call sites anywhere** (src or tests); the only construction is DI (`Bootstrapper.cs:384` registers `IPluginService, PluginService`), and `IIngestToolHandler` is already registered (`Bootstrapper.cs:366`), so the new ctor param resolves automatically. Re-run `grep -rn "new PluginService(" src tests` after editing to confirm nothing appeared. `DiRegistrationTests` passes unchanged (`IIngestToolHandler` is already registered).

## Step 4 — Normalize `source_ref` in the tool handler

**Decision (exact):** normalization lives in **`IngestToolHandler`** (the model-facing adapter), NOT in `IngestService` — the service's contract stays "vault-relative path" and other/future callers are unaffected. `IngestService.IngestAsync` keeps its existing `'\\'→'/'` normalization (harmless overlap).

**File:** `src/Pia.Wpf/Services/IngestToolHandler.cs`

1. In `HandleToolCallAsync`, after the `string.IsNullOrWhiteSpace(sourceRef)` guard (line 56), add:

```csharp
        sourceRef = NormalizeSourceRef(sourceRef);
```

2. Add a private helper (below `IngestSchema`):

```csharp
    // Model-facing lenience: the files tools address the same file as 'Vault/sources/<name>', so
    // accept that spelling (any casing) plus stray leading slashes / backslashes, and canonicalize
    // to the vault-relative form IngestService expects.
    private static string NormalizeSourceRef(string sourceRef)
    {
        var normalized = sourceRef.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("vault/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["vault/".Length..];
        }
        return normalized;
    }
```

## Step 5 — Readable string result (+ outcome plumbing)

The handler must distinguish not-found / non-text / empty / zero-entities, but `IngestResult(SourceRef, TouchedPages)` collapses all four to an empty `TouchedPages`. Add an outcome enum (additive, default `Success` — no existing call site breaks).

**File A:** `src/Pia.Wpf/Services/Interfaces/IIngestService.cs`

Above the `IngestResult` record (line 8) add the enum, and extend the record:

```csharp
/// <summary>Why an ingest produced no pages; <see cref="Success"/> when it did.</summary>
public enum IngestOutcome
{
    Success,
    SourceNotFound,
    NonTextSkipped,
    EmptySource,
    NoEntities,
}

public record IngestResult(
    string SourceRef, IReadOnlyList<string> TouchedPages, IngestOutcome Outcome = IngestOutcome.Success);
```

**File B:** `src/Pia.Wpf/Services/Wiki/IngestService.cs` — tag the four early returns:

- line 83 (`!File.Exists`): `return new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);`
- line 90 (`!IsTextSource`): `return new IngestResult(sourceRef, [], IngestOutcome.NonTextSkipped);`
- line 97 (whitespace content): `return new IngestResult(sourceRef, [], IngestOutcome.EmptySource);`
- line 133 (`touched.Count == 0`): `return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);`
- line 159 success return: unchanged (default `Success`).

**File C:** `src/Pia.Wpf/Services/IngestToolHandler.cs` — replace `return result;` (line 62) with:

```csharp
        return result.Outcome switch
        {
            IngestOutcome.SourceNotFound =>
                $"Error: source '{sourceRef}' was not found. Raw files must be inside the vault's sources/ folder. " +
                "To stage a new file, write it to 'Vault/sources/<name>' with the files tools, then call " +
                "ingest(\"sources/<name>\").",
            IngestOutcome.NonTextSkipped =>
                $"Skipped: '{sourceRef}' is not a text file. Only text sources (e.g. txt, md, csv, json, html, xml, log) can be ingested.",
            IngestOutcome.EmptySource =>
                $"Skipped: '{sourceRef}' is empty — nothing to ingest.",
            IngestOutcome.NoEntities =>
                $"Ingest ran on '{sourceRef}' but extracted no entities, so no memory pages were written.",
            _ =>
                $"Ingested '{sourceRef}' into {result.TouchedPages.Count} memory page(s): " +
                $"{string.Join(", ", result.TouchedPages)}. The content is now available via recall.",
        };
```

Also update the stale XML doc on the class (line 15 "returns the `IngestResult` directly" → "returns a human-readable result string").

Privacy: the strings above are TOOL RESULTS returned to the model — not log lines — so no logging rule applies to them. Keep the existing `_logger.SensitiveDebug("Ingest tool compiled {Source} …")` line (60–61) as is; do not add any Information-level log containing `sourceRef` or page paths.

## Step 6 — Recall-visibility: synthetic preamble chunk

### 6a. Sentinel constant (centralized)

**File:** `src/Pia.Wpf/Infrastructure/Vault/VaultSlug.cs` — add inside the class, above `Slugify`:

```csharp
    /// <summary>
    /// Reserved slug for the synthetic whole-preamble chunk emitted by <c>VaultIndexer</c>.
    /// <see cref="Slugify"/> can never produce it (its output alphabet is [a-z0-9-]; '_' collapses
    /// to '-'), and the parser's collision suffixes ("-2", …) stay in that alphabet too, so it can
    /// never collide with a real ## section slug.
    /// </summary>
    public const string PreambleSlug = "__preamble__";
```

### 6b. Indexer emits the chunk

**File:** `src/Pia.Wpf/Services/VaultIndexer.cs`, method `IndexFileAsync` — insert between the `foreach (var section in doc.Sections)` loop (ends line 98) and the `PruneMissingSectionsAsync` call (line 101):

```csharp
        // Freeform files (note/project/topic — the format ingest and remember("topic", …) write)
        // keep their content in the PREAMBLE, not in ## sections, so without this they produce zero
        // chunks and are invisible to recall. Emit ONE synthetic chunk for a non-empty preamble
        // under the reserved slug; heading = frontmatter title, else the filename.
        if (!string.IsNullOrWhiteSpace(doc.Preamble))
        {
            var heading = doc.Frontmatter.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title)
                ? title
                : Path.GetFileNameWithoutExtension(relativePath);
            var preambleSection = new VaultSection(heading, VaultSlug.PreambleSlug, doc.Preamble.Trim(), 0, 0);

            presentSlugs.Add(VaultSlug.PreambleSlug);
            var preambleHash = ComputeContentHash(preambleSection);
            if (!await IsContentHashUnchangedAsync(connection, relativePath, VaultSlug.PreambleSlug, preambleHash))
            {
                var preambleEmbedding = await _embeddings.GenerateEmbeddingAsync(
                    $"{preambleSection.Heading}\n{preambleSection.Body}");
                await UpsertChunkAsync(connection, relativePath, preambleSection, preambleHash,
                    _embeddings.FloatsToBytes(preambleEmbedding));
                await RefreshFtsAsync(connection, relativePath, preambleSection);
            }
        }
```

Notes:
- `VaultSection` is a positional record (`Heading, Slug, Body, BodyStart, BodyEnd`); `BodyStart/BodyEnd = 0` is fine — the indexer never reads them.
- Reuses the SAME `ComputeContentHash` / `IsContentHashUnchangedAsync` / `UpsertChunkAsync` / `RefreshFtsAsync` helpers — identical skip/embed/FTS semantics as real sections.
- Adding the sentinel to `presentSlugs` makes `PruneMissingSectionsAsync` handle it symmetrically: if a file's preamble later becomes empty/whitespace, the stale preamble chunk (and its FTS row) is pruned.
- Empty-preamble files (structured profile/contacts/preferences, typical index/log) add NO chunk — existing invariants preserved.
- `Path` resolves via ImplicitUsings (System.IO is in the default set) — no new using needed. `Pia.Infrastructure.Vault` (for `VaultSlug`) is already imported (line 6).
- `RebuildAllAsync` needs no edit — it delegates to `IndexFileAsync` per file.

### 6c. Coordinated snippet branch

**File:** `src/Pia.Wpf/Services/MemoryService.cs`, method `BuildSnippetAsync` (line 1055) — insert directly after `if (doc is null) return null;` (line 1058), BEFORE the section loop:

```csharp
        // Synthetic preamble chunk (VaultIndexer): the "section" is the document preamble, not a
        // real ## slug — read it back directly, else the hit would be dropped.
        if (slug == VaultSlug.PreambleSlug)
        {
            var preamble = doc.Preamble.Trim();
            if (preamble.Length == 0) return null;
            return preamble.Length > 200 ? preamble[..200] : preamble;
        }
```

`MemoryService` already uses `VaultSlug` (RememberFreeformAsync), so the using exists. No change to `RecallAsync` itself — the sentinel rows flow through the LIKE/FTS/fuzzy/vector tiers like any chunk (Heading = the topic title, which is exactly what heading-tier matching needs).

Behavior notes (intentional, do not "fix"): any vault *.md with leading prose before its first `##` (including raw markdown files under `sources/`) now gains one preamble chunk — consistent with whole-vault recall. Chunks previously written by older code are keyed by real slugs only, so no migration conflict; but pre-existing freeform files only gain their preamble chunk on the next `IndexFileAsync` for that file (watcher fires on any edit) or on the next `RebuildAllAsync` (folder relocation / vault migration) — see open concern in the final report; do not build a rebuild trigger in this task.

## Step 7 — Tests

### 7a. `tests/Pia.Wpf.Tests/Vault/RecallTests.cs` (extend; usings already sufficient — `Pia.Infrastructure.Vault` is imported)

Add three facts:

```csharp
    [Fact]
    public async Task Recall_returns_freeform_topic_content_by_subject()
    {
        var service = await SeedAndBuildAsync();
        await service.RememberAsync("topic", "Acme Corp", "- customer since 2024");

        var indexer = new VaultIndexer(_ctx, _store, _parser, _embeddings, NullLogger<VaultIndexer>.Instance);
        await indexer.IndexFileAsync("memory/topics/acme-corp.md");

        var hits = await service.RecallAsync("Acme Corp");

        var hit = hits.FirstOrDefault(h => h.FilePath.Replace('\\', '/') == "memory/topics/acme-corp.md");
        Assert.NotNull(hit);
        Assert.Equal("Acme Corp", hit!.Heading);
        Assert.Contains("customer since 2024", hit.Snippet);
    }

    [Fact]
    public async Task Freeform_file_with_preamble_and_no_sections_yields_exactly_one_preamble_chunk()
    {
        await _store.WriteAtomicAsync("memory/topics/plasma-donation.md",
            "---\n" +
            "pia: managed\n" +
            "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000003\n" +
            "type: topic\n" +
            "title: Plasma Donation\n" +
            "schemaVersion: 1\n" +
            "---\n" +
            "- donors must weigh at least 50 kg\n");

        var indexer = new VaultIndexer(_ctx, _store, _parser, _embeddings, NullLogger<VaultIndexer>.Instance);
        await indexer.IndexFileAsync("memory/topics/plasma-donation.md");

        var connection = _ctx.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Heading, Slug FROM Chunks WHERE FilePath = 'memory/topics/plasma-donation.md';";
        using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Plasma Donation", reader.GetString(0));
        Assert.Equal(VaultSlug.PreambleSlug, reader.GetString(1));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task File_with_empty_preamble_gets_no_preamble_chunk()
    {
        await SeedAndBuildAsync(); // memory/profile.md: sections only, empty preamble

        var connection = _ctx.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM Chunks WHERE FilePath = 'memory/profile.md' AND Slug = $s;";
        cmd.Parameters.AddWithValue("$s", VaultSlug.PreambleSlug);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, count);
    }
```

Note: the file's `StubEmbeddingService` returns one fixed vector for all text (everything cosine-matches) — the first test's decisive assertions are the file-scoped hit lookup, the Heading, and the preamble snippet, which only exist if the preamble chunk + snippet branch work.

### 7b. `tests/Pia.Wpf.Tests/Wiki/IngestServiceTests.cs` (extend)

Add `using Microsoft.Extensions.AI;` (for `FunctionCallContent`) — `Pia.Services` is already imported. Add a helper next to `BuildIngest`:

```csharp
    private IngestToolHandler BuildToolHandler()
        => new(BuildIngest(new StubExtractor()), NullLogger<IngestToolHandler>.Instance);
```

Add three facts:

```csharp
    [Fact]
    public async Task Ingest_tool_returns_a_readable_success_string()
    {
        var handler = BuildToolHandler();
        var call = new FunctionCallContent("c1", "ingest",
            new Dictionary<string, object?> { ["source_ref"] = "sources/sample.txt" });

        var result = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.Contains("Ingested 'sources/sample.txt' into 2 memory page(s)", text);
        Assert.Contains("memory/topics/acme-corp.md", text);
        Assert.Contains("memory/topics/john-smith.md", text);
    }

    [Fact]
    public async Task Ingest_tool_normalizes_a_vault_prefixed_source_ref()
    {
        var handler = BuildToolHandler();
        var call = new FunctionCallContent("c1", "ingest",
            new Dictionary<string, object?> { ["source_ref"] = "Vault/sources/sample.txt" });

        var result = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.StartsWith("Ingested 'sources/sample.txt'", text);
        Assert.NotNull(await _store.ReadAsync("memory/topics/acme-corp.md"));
    }

    [Fact]
    public async Task Ingest_tool_reports_a_missing_source_with_the_staging_recipe()
    {
        var handler = BuildToolHandler();
        var call = new FunctionCallContent("c1", "ingest",
            new Dictionary<string, object?> { ["source_ref"] = "sources/missing.txt" });

        var result = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.Contains("not found", text);
        Assert.Contains("Vault/sources/", text);
    }
```

Style: xunit.v3 (`TestContext.Current.CancellationToken` on awaited service calls), plain `Xunit.Assert`, real temp-vault plumbing already present in both files; NSubstitute is not needed here (no new mocks — matches how these two suites are built).

## Step 8 — Build + gate + sanity greps

1. `grep -rn "new PluginService(" src tests` → must show only the constructor declaration.
2. `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug` → exit 0.
3. `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` → zero failures outside `Pia.Wpf.Tests.Integration.Providers`.
4. Do not commit.

---

## Out of scope (listed for the final summary; do NOT build)

- Chat-attachment → `sources/` copy (attachments are image-only, in-memory).
- Background-job handle / progress UI for ingest; lint scheduling; binary/PDF ingestion; model-assisted prose rewrite.
- Server-side seed for GUID `…0007` (client-only preloaded built-in is safe; flag for later cross-repo coordination).
- A one-time index rebuild trigger for pre-existing freeform files (see open concern).
