using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models.Vault;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Ingest pipeline — a topic-driven synthesis compiler that turns a RAW source under <c>sources/</c>
/// into <c>memory/topics/</c> wiki pages and keeps the index / log / per-page provenance current.
///
/// <para>Orchestration:</para>
/// <list type="number">
///   <item>Read the raw source text directly under <see cref="IVaultStore.Root"/> (<c>sources/</c> is
///     immutable — read only). Non-text/binary sources are skipped (logged, empty result).</item>
///   <item>Discover the NOTABLE topics via <see cref="IIngestExtractor"/>, grounded in the vault
///     charter (<see cref="VaultCharterService"/>).</item>
///   <item>For each topic, union this source with whatever sources the page already records and
///     re-synthesize the whole managed body across ALL of them (<see cref="IIngestSynthesizer"/>).
///     A manual preamble above the <see cref="ManagedMarker"/> sentinel is preserved verbatim;
///     page identity (<c>id</c>/<c>created</c>) is kept stable via
///     <see cref="VaultFrontmatter.BuildPreserving"/>. The page's category template
///     (<see cref="VaultTemplateService"/>) is passed to the synthesizer so every page of a category
///     carries the same fields.</item>
///   <item>Upsert an index entry per touched page.</item>
///   <item>Append one <c>ingest</c> log line naming the source and touched pages.</item>
/// </list>
///
/// <para><b>Page-on-disk layout.</b> Every managed topic page is exactly
/// <c>---\n&lt;frontmatter incl. sources: + category:&gt;\n---\n[optional manual preamble]\n&lt;!-- pia:managed --&gt;\n&lt;synthesized body&gt;</c>.
/// The <see cref="ManagedMarker"/> line is a mandatory sentinel owned by the writer (the synthesizer
/// returns body text only) and is the single source of truth for the preamble/body split — the split is
/// done on the RAW text, never <see cref="VaultDocument.Preamble"/> (a heading-less body would otherwise
/// fold the whole page into the preamble and accumulate it every re-ingest).</para>
///
/// <para><b>sources: round-trip.</b> The frontmatter maintainer reads the RAW <c>sources:</c> line
/// (never <see cref="VaultDocument.Frontmatter"/>, whose YAML parser flattens flow lists), so a
/// multi-source list round-trips cleanly across add/remove edits.</para>
///
/// <para><b>Transient failures.</b> When topics are discovered but ≥1 page's synthesis comes back empty
/// (provider died mid-run), <see cref="IngestAsync"/> returns <see cref="IngestOutcome.SynthesisFailed"/>
/// so the caller records nothing and retries; pages that DID synthesize are already written.</para>
/// </summary>
public sealed class IngestService : IIngestService
{
    /// <summary>Mandatory sentinel splitting the (optional) manual preamble from the synthesized body.</summary>
    private const string ManagedMarker = "<!-- pia:managed -->";
    private const int MaxConcurrentSynthesis = 4;

    private readonly IIngestExtractor _extractor;
    private readonly IVaultStore _store;
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;
    private readonly IIngestSynthesizer _synth;
    private readonly VaultCharterService _charter;
    private readonly VaultTemplateService _templates;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<IngestService> _logger;

    public IngestService(
        IIngestExtractor extractor,
        IVaultStore store,
        VaultIndexService index,
        VaultLogService log,
        IIngestSynthesizer synth,
        VaultCharterService charter,
        VaultTemplateService templates,
        Func<ITokenMapService> tokenMapFactory,
        ISettingsService settings,
        ILogger<IngestService> logger)
    {
        _extractor = extractor;
        _store = store;
        _index = index;
        _log = log;
        _synth = synth;
        _charter = charter;
        _templates = templates;
        _tokenMapFactory = tokenMapFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IngestResult> IngestAsync(
        string sourceRelativePath, DateOnly date, CancellationToken ct = default)
    {
        var sourceRef = sourceRelativePath.Replace('\\', '/');
        var sourceName = Path.GetFileName(sourceRef);

        // 1. Read the RAW source directly under the vault root (sources/ files are not Pia-managed
        // markdown, so we do NOT parse them through IVaultStore.ReadAsync). Keep the DISTINCT outcomes
        // here — the tool + tests rely on SourceNotFound / NonTextSkipped / EmptySource specifically.
        // Containment guard: source_ref reaches this service from a model tool call, so refuse any
        // absolute path or '..' traversal that resolves OUTSIDE the vault — otherwise an injected
        // prompt could exfiltrate an arbitrary local text file into memory (which syncs).
        // Scope guard: only the RAW layer (sources/) is ingestable. The tool reaches the model,
        // and containment alone would let an ingest("memory/preferences.md") pull Pia's OWN memory back
        // into topic synthesis. Refuse anything outside sources/ before touching the filesystem.
        if (!IsSourcesRef(sourceRef))
        {
            _logger.SensitiveDebug("Ingest source outside sources/ {Source}", sourceRef);
            return new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
        }

        var absolute = ResolveContainedSource(sourceRelativePath);
        if (absolute is null)
        {
            _logger.SensitiveDebug("Ingest source escapes the vault {Source}", sourceRef);
            return new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
        }

        if (!File.Exists(absolute))
        {
            _logger.SensitiveDebug("Ingest source not found {Source}", sourceRef);
            return new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
        }

        if (!SourcesProvenance.IsTextSource(sourceRef))
        {
            // Binary handling (PDF/image extraction) is DEFERRED — skip with an empty result.
            _logger.SensitiveDebug("Ingest skipping non-text source {Source}", sourceRef);
            return new IngestResult(sourceRef, [], IngestOutcome.NonTextSkipped);
        }

        var content = await File.ReadAllTextAsync(absolute, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.SensitiveDebug("Ingest source empty {Source}", sourceRef);
            return new IngestResult(sourceRef, [], IngestOutcome.EmptySource);
        }

        // 2. Discover the notable topics, grounded in the vault charter.
        var charter = await _charter.GetCharterAsync();

        // PII re-identification: ingest runs off any chat turn, so TokenMapAmbient is unset. Build ONE
        // map for this run (when tokenization is enabled), publish it as the ambient turn map around the
        // extraction call ONLY so the TokenizingAiClientService decorator tokenizes the prompt and
        // detokenizes discovered subjects against it, then HOLD it to re-identify each subject below
        // BEFORE it becomes a slug/title. The synthesizer manages its OWN ambient internally, so this
        // scope is deliberately closed before synthesis — the two never overlap. No-op when disabled.
        var tokenMap = await CreateIngestTokenMapAsync();
        var topics = await DiscoverTopicsAsync(tokenMap, content, charter, ct);
        _logger.SensitiveDebug("Ingest {Source} discovered {Count} topics", sourceRef, topics.Count);
        if (topics.Count == 0)
        {
            return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);
        }

        // 3. Topic-driven synthesis: for each topic, union this source with the page's existing sources
        // and re-synthesize the whole managed body across all of them.
        //
        // Pre-pass: re-identify + slugify EVERY subject up front so the known-slug set is complete before
        // any page is synthesized. That makes a within-run forward reference safe — topic A's page (written
        // first) may link to topic B's page (written later in the loop), and B's slug is already known.
        var prepared = new List<(ExtractedTopic Topic, string Subject, string Slug)>();
        var reidentifiedSubjects = 0;
        foreach (var topic in topics)
        {
            // Re-identify the subject BEFORE it becomes a slug/title. The extraction model may have
            // mangled a PII placeholder past the decorator's strict detokenize — bracket-stripped to a
            // bare "Person_1", or lowercased/re-punctuated to "[person-1]" — which would otherwise be
            // written as the page filename ("person-1.md") and title. No-op when tokenization is off.
            var subject = Reidentify(tokenMap, topic.Subject);
            if (!string.Equals(subject, topic.Subject, StringComparison.Ordinal))
            {
                reidentifiedSubjects++;
                _logger.SensitiveDebug("Ingest re-identified a residual placeholder subject {Subject}", subject);
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                continue;
            }

            prepared.Add((topic, subject, VaultSlug.Slugify(subject)));
        }

        if (reidentifiedSubjects > 0)
        {
            // Release-visible privacy signal (count only — the subject values are user PII → SensitiveDebug
            // above): the extraction model mangled ≥1 placeholder so it slipped past the strict decorator.
            _logger.LogWarning(
                "Ingest re-identified {Count} residual placeholder subject(s) the extraction model mangled",
                reidentifiedSubjects);
        }

        // The link vocabulary for BOTH grounding (synthesizer prompt) and reconciliation (deterministic
        // backstop): slugs already on disk ∪ slugs this run will create.
        var knownSlugs = await BuildKnownTopicSlugsAsync();
        foreach (var p in prepared)
        {
            knownSlugs.Add(p.Slug);
        }

        // Synthesis calls are the slow part (one full LLM round-trip per topic) and are independent of
        // each other — knownSlugs is already frozen above — so run up to MaxConcurrentSynthesis at once.
        // Index upserts happen afterward, sequentially, to avoid concurrent writers on the shared index.
        var synthesisGate = new SemaphoreSlim(MaxConcurrentSynthesis);
        var synthesisTasks = prepared.Select(async p =>
        {
            var (topic, subject, slug) = p;
            var path = $"memory/topics/{slug}.md";

            var existing = await _store.ReadAsync(path);
            var sourceRefs = ReadPageSources(existing);
            if (!sourceRefs.Contains(sourceRef, StringComparer.OrdinalIgnoreCase))
            {
                sourceRefs.Add(sourceRef);
            }

            var title = existing?.Frontmatter.GetValueOrDefault("title") is { Length: > 0 } t
                ? t
                : subject;
            var category = existing?.Frontmatter.GetValueOrDefault("category") is { Length: > 0 } c
                ? c
                : topic.Category;

            await synthesisGate.WaitAsync(ct);
            try
            {
                var summary = await SynthesizePageAsync(path, title, category, sourceRefs, charter, knownSlugs, ct);
                return (path, summary);
            }
            finally
            {
                synthesisGate.Release();
            }
        }).ToList();

        var synthesisResults = await Task.WhenAll(synthesisTasks);

        var touched = new List<string>();
        var synthFailures = 0;
        foreach (var (path, summary) in synthesisResults)
        {
            if (summary is null)
            {
                synthFailures++;
                continue;
            }

            touched.Add(path);
            await _index.UpsertEntryAsync(path, summary);
        }

        // Transient-failure guard: topics WERE discovered but at least one synthesis call produced
        // nothing (provider died mid-run, model error). Report SynthesisFailed so AutoIngestService
        // records NOTHING for this source — no hash freeze, no shrink-diff wipe of previously-touched
        // pages — and the source is retried on the next change / startup reconcile. Pages that DID
        // synthesize are already written; the retry just re-synthesizes them (idempotent). Skip the
        // journal line too — the clean retry journals.
        if (synthFailures > 0)
        {
            _logger.LogWarning("Ingest synthesis produced no body for {Count} topic(s); source will be retried",
                synthFailures);
            return new IngestResult(sourceRef, touched, IngestOutcome.SynthesisFailed);
        }

        if (touched.Count == 0)
        {
            return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);
        }

        // 4. Journal one ingest line.
        await _log.AppendAsync("ingest", sourceName + " -> " + string.Join(", ", TouchedTargets(touched)), date);

        return new IngestResult(sourceRef, touched);
    }

    /// <inheritdoc />
    public async Task RemoveContributionsAsync(
        string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default)
    {
        sourceRef = sourceRef.Replace('\\', '/'); // separator-tolerant, matching IngestAsync

        // Same grounding + reconciliation vocabulary as IngestAsync so re-synthesis on removal is equally
        // guarded. Built once up front: a page fully removed in this batch stays in the set, so a link to
        // it could survive — benign and self-healing (the next ingest re-synthesizes and re-reconciles).
        var knownSlugs = await BuildKnownTopicSlugsAsync();

        var stale = new List<string>();
        foreach (var path in pages)
        {
            ct.ThrowIfCancellationRequested();
            var doc = await _store.ReadAsync(path);
            if (doc is null)
            {
                continue;
            }

            var remaining = ReadPageSources(doc);
            remaining.RemoveAll(r => r.Equals(sourceRef, StringComparison.OrdinalIgnoreCase));

            if (remaining.Count == 0)
            {
                // No source contributes any longer — drop the page and its index entry.
                await _store.DeleteAsync(path);
                await _index.RemoveEntryAsync(path);
                _logger.SensitiveDebug("Removed now-empty topic page {Path}", path);
                continue;
            }

            // 1. DETERMINISTIC (always, no LLM): prune the ref from the sources: line so provenance
            //    (VaultSourcesService counts, ScanPagesForSourceAsync) stays truthful even with no
            //    provider configured.
            await RemoveSourceFromFrontmatterAsync(path, sourceRef);

            // 2. BEST-EFFORT: re-synthesize the body from the remaining sources. On empty synthesis
            //    (no provider / model error) keep the old body — stale, but it SELF-HEALS: the next
            //    ingest of any remaining source re-synthesizes this page. (SynthesizePageAsync rewrites
            //    the sources: line again on success — redundant with step 1, same list, harmless.)
            var title = doc.Frontmatter.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(path);
            var category = doc.Frontmatter.GetValueOrDefault("category") ?? "concept";
            var summary = await SynthesizePageAsync(
                path, title, category, remaining, await _charter.GetCharterAsync(), knownSlugs, ct);
            if (summary is not null)
            {
                await _index.UpsertEntryAsync(path, summary);
            }
            else
            {
                stale.Add(path);
            }
        }

        if (pages.Count > 0)
        {
            // Removal journals a corresponding ingest log line, mirroring the ingest one. When a page's
            // body could not be re-synthesized (no provider), surface the stale count in the journal.
            var line = "removed " + Path.GetFileName(sourceRef) + " -> " + string.Join(", ", TouchedTargets(pages));
            if (stale.Count > 0)
            {
                line += $" ({stale.Count} page(s) stale — re-synthesis needs an AI provider)";
            }

            await _log.AppendAsync("ingest", line, DateOnly.FromDateTime(DateTime.Now));
        }

        if (stale.Count > 0)
        {
            // Release-visible: count only (page titles are user-named → SensitiveDebug for the names).
            _logger.LogWarning(
                "Ingest removal left {Count} page(s) with a stale body; re-synthesis needs an AI provider",
                stale.Count);
            _logger.SensitiveDebug("Stale pages after removal: {Pages}", string.Join(", ", stale));
        }

        _logger.LogInformation("Removed ingest contributions from {Count} page(s)", pages.Count);
        _logger.SensitiveDebug("Removed contributions of {Source}", sourceRef);
    }

    // ---- PII re-identification (extraction subjects) ----

    // Build ONE token map for this ingest run, or null when tokenization is disabled. The decorator
    // will NOT initialize an ambient map it did not create (its _initialized latch), so we initialize
    // it here — mirroring AiIngestSynthesisService.
    private async Task<ITokenMapService?> CreateIngestTokenMapAsync()
    {
        var settings = await _settings.GetSettingsAsync();
        if (!settings.Privacy.TokenizationEnabled)
        {
            return null;
        }

        var map = _tokenMapFactory();
        try
        {
            await map.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize token map for ingest extraction");
        }

        return map;
    }

    // Run topic discovery with this run's map published as the ambient turn map, restoring the previous
    // ambient afterwards. Scoped to the extraction call ONLY (the synthesizer manages its own ambient),
    // so it can never interfere with synthesis. Straight pass-through when the map is null (disabled).
    private async Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
        ITokenMapService? tokenMap, string content, string charter, CancellationToken ct)
    {
        if (tokenMap is null)
        {
            return await _extractor.DiscoverTopicsAsync(content, charter, ct);
        }

        var previousAmbient = TokenMapAmbient.Current;
        TokenMapAmbient.Current = tokenMap;
        try
        {
            return await _extractor.DiscoverTopicsAsync(content, charter, ct);
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
        }
    }

    // Re-identify a topic subject before it becomes a slug/title. Recovers BOTH the bracketed-loose
    // form ([person-1], [Person_1]) via DetokenizeLoose AND the bare title-leak shape (Person_1) via
    // DetokenizeBare. No-op when the map is null (tokenization disabled).
    private static string Reidentify(ITokenMapService? tokenMap, string subject)
    {
        if (tokenMap is null || string.IsNullOrEmpty(subject))
        {
            return subject;
        }

        return tokenMap.DetokenizeBare(tokenMap.DetokenizeLoose(subject));
    }

    /// <inheritdoc />
    public async Task<bool> RebuildPageAsync(string pagePath, CancellationToken ct = default)
    {
        pagePath = pagePath.Replace('\\', '/'); // separator-tolerant, matching IngestAsync

        var doc = await _store.ReadAsync(pagePath);
        if (doc is null)
        {
            return false;
        }

        // The FULL recorded list — unlike RemoveContributionsAsync, a rebuild prunes nothing.
        var sourceRefs = ReadPageSources(doc);
        if (sourceRefs.Count == 0)
        {
            _logger.SensitiveDebug("Rebuild skipped for {Path}: the page records no sources", pagePath);
            return false;
        }

        var title = doc.Frontmatter.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(pagePath);
        var category = doc.Frontmatter.GetValueOrDefault("category") ?? "concept";

        // Not optional: with an empty set BuildLinkInstruction forbids wikilinks outright, so a rebuild
        // would silently strip every link off the page.
        var knownSlugs = await BuildKnownTopicSlugsAsync();

        var summary = await SynthesizePageAsync(
            pagePath, title, category, sourceRefs, await _charter.GetCharterAsync(), knownSlugs, ct);
        if (summary is null)
        {
            return false;
        }

        await _index.UpsertEntryAsync(pagePath, summary);
        await _log.AppendAsync(
            "rebuild", string.Join(", ", TouchedTargets([pagePath])), DateOnly.FromDateTime(DateTime.Now));
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListTopicPagesAsync()
    {
        // Scoped to memory/topics/ on purpose: EnumerateAsync is not a real glob, so "memory/*.md" would
        // walk the whole subtree and hand back AGENTS.md, index.md, log.md and templates.md.
        var pages = await _store.EnumerateAsync("memory/topics/*.md");
        return pages.Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    // ---- shared synthesis writer ----

    // Returns the index one-liner, or null when synthesis produced nothing (page left untouched).
    private async Task<string?> SynthesizePageAsync(
        string path, string title, string category,
        List<string> sourceRefs, string charter, IReadOnlySet<string> knownSlugs, CancellationToken ct)
    {
        var sources = new List<(string Ref, string Text)>();
        foreach (var r in sourceRefs)
        {
            var text = await TryReadSourceAsync(r, ct);
            if (text is not null)
            {
                sources.Add((r, text));
            }
        }

        if (sources.Count == 0)
        {
            return null;
        }

        var existing = await _store.ReadAsync(path);
        var template = await _templates.GetTemplateAsync(category);
        var page = await _synth.SynthesizeAsync(title, category, charter, template, sources, knownSlugs, ct);
        if (string.IsNullOrWhiteSpace(page.Body))
        {
            return null;
        }

        // Deterministic backstop: rewrite kept links to their canonical on-disk slug and strip any dangling
        // link to plain text, so the body that lands on disk carries ONLY wikilinks that resolve — clearing
        // dead links in the stored source after creation, independent of what the model emitted.
        var body = WikiLinkReconciler.Reconcile(page.Body, knownSlugs);

        // Manual preamble = raw text between the frontmatter close and the sentinel, split on the RAW
        // text (never doc.Preamble). "" for a new page or one with no manual text above the marker.
        var preamble = ExtractManualPreamble(existing?.RawText);

        var sb = new StringBuilder();
        sb.Append(VaultFrontmatter.BuildPreserving(existing, title, category)); // preserves id/created
        sb.Append('\n');
        if (preamble.Length > 0)
        {
            sb.Append(preamble.TrimEnd()).Append("\n\n");
        }

        sb.Append(ManagedMarker).Append('\n');
        sb.Append(body.Trim()).Append('\n');

        var content = WriteSourcesLine(sb.ToString(), sourceRefs); // "sources: [a, b]" into the block
        await _store.WriteAtomicAsync(path, content);
        _logger.SensitiveDebug("Ingest synthesized topic page {Path}", path);
        return page.Summary;
    }

    // The slug of every topic page currently on disk, canonicalized through VaultSlug.Slugify so set
    // membership is tested on the same canonical form the reconciler computes from a link target. In-app
    // filenames are already canonical lowercase slugs (so this is a no-op for them); it only hardens the
    // rare hand-added page. Ordinal set: both sides are now guaranteed canonical, so no case folding.
    private async Task<HashSet<string>> BuildKnownTopicSlugsAsync()
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in await _store.EnumerateAsync("memory/topics/*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name))
            {
                slugs.Add(VaultSlug.Slugify(name));
            }
        }

        return slugs;
    }

    // Resolve a vault-relative source ref to an absolute path under the vault root, or null if it
    // escapes containment. Shared predicate for the initial guard and the per-topic union reads.
    private string? ResolveContainedSource(string sourceRelativePath)
    {
        var rootFull = Path.GetFullPath(_store.Root);
        var absolute = Path.GetFullPath(
            Path.Combine(rootFull, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return absolute.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? absolute
            : null;
    }

    // Per-topic union read: containment + IsTextSource + exists, else null (silently skipped).
    private async Task<string?> TryReadSourceAsync(string sourceRef, CancellationToken ct)
    {
        var absolute = ResolveContainedSource(sourceRef);
        if (absolute is null || !IsSourcesRef(sourceRef) || !File.Exists(absolute)
            || !SourcesProvenance.IsTextSource(sourceRef))
        {
            // IsSourcesRef also drops any stale non-sources/ ref that a pre-guard page may still list in
            // its `sources:` frontmatter, so it never re-contributes to a union merge.
            return null;
        }

        return await File.ReadAllTextAsync(absolute, ct);
    }

    // True only for refs under the RAW layer. Separator-tolerant to match IngestAsync, which
    // normalizes '\\' → '/' before calling.
    private static bool IsSourcesRef(string sourceRef)
        => sourceRef.Replace('\\', '/').StartsWith("sources/", StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadPageSources(VaultDocument? doc)
        => doc is null ? new() : SourcesProvenance.ReadSourceRefs(doc.RawText).ToList();

    // Everything after the closing '---' line and before the sentinel; "" if no sentinel or no such text.
    private static string ExtractManualPreamble(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var body = StripFrontmatter(raw);
        var marker = body.IndexOf(ManagedMarker, StringComparison.Ordinal);
        var preamble = marker < 0 ? body : body[..marker]; // no sentinel (user page) → all of it is manual
        return preamble.Trim();
    }

    // The content after the closing '---' delimiter line, or "" when there is no leading frontmatter.
    private static string StripFrontmatter(string raw)
    {
        var s = raw.Replace("\r\n", "\n");
        if (!TryFindFrontmatterBlock(s, out _, out var closeStart))
        {
            return s;
        }

        var afterClose = s[closeStart..]; // "---\n<body...>"
        var nl = afterClose.IndexOf('\n');
        return nl < 0 ? string.Empty : afterClose[(nl + 1)..];
    }

    // Locates the leading "---\n...\n---" frontmatter block in already LF-normalized text. On success,
    // fmStart is the index just past the opening delimiter (the start of the keys block) and closeStart
    // is the index of the closing delimiter line (so text[fmStart..closeStart] is the keys block and
    // text[closeStart..] starts at "---\n<body...>"). False when there is no leading block.
    private static bool TryFindFrontmatterBlock(string normalizedRaw, out int fmStart, out int closeStart)
    {
        var open = normalizedRaw.IndexOf("---\n", StringComparison.Ordinal);
        if (open != 0)
        {
            fmStart = closeStart = -1;
            return false;
        }

        var close = normalizedRaw.IndexOf("\n---", open + 3, StringComparison.Ordinal);
        if (close < 0)
        {
            fmStart = closeStart = -1;
            return false;
        }

        fmStart = open + 4;
        closeStart = close + 1;
        return true;
    }

    // ---- sources: frontmatter maintainer (best-effort YAML flow list) ----

    // Set the sources: line on an in-memory page's frontmatter block (used by the synthesis writer).
    private static string WriteSourcesLine(string content, IReadOnlyList<string> sourceRefs)
    {
        var raw = content.Replace("\r\n", "\n");
        if (!TryFindFrontmatterBlock(raw, out var fmStart, out var closeStart))
        {
            return content;
        }

        var fmBody = raw[fmStart..closeStart]; // keys block, ends with the '\n' before '---'
        var afterFm = raw[closeStart..];       // starts at the closing '---' line

        var newLine = "sources: [" + string.Join(", ", sourceRefs) + "]\n";
        var existing = SourcesProvenance.FindKeyValue(fmBody, "sources:");
        var newFmBody = existing is null
            ? fmBody + newLine
            : ReplaceKeyLine(fmBody, "sources:", newLine);

        return raw[..fmStart] + newFmBody + afterFm;
    }

    private Task RemoveSourceFromFrontmatterAsync(string path, string sourceRef) =>
        RewriteSourcesFrontmatterAsync(path, refs =>
            refs.RemoveAll(r => r.Equals(sourceRef, StringComparison.OrdinalIgnoreCase)));

    private async Task RewriteSourcesFrontmatterAsync(string path, Action<List<string>> mutate)
    {
        var doc = await _store.ReadAsync(path);
        if (doc is null)
        {
            return;
        }

        // Normalize CRLF up front — the whole file is rewritten LF (Pia's native form), and the index
        // math below must not straddle a '\r'.
        var raw = doc.RawText.Replace("\r\n", "\n");
        if (!TryFindFrontmatterBlock(raw, out var fmStart, out var closeStart))
        {
            // No leading frontmatter block — leave the file untouched.
            return;
        }

        var fmBody = raw[fmStart..closeStart]; // keys block, ends with the '\n' before '---'
        var afterFm = raw[closeStart..];       // starts at the closing '---' line

        // Read the existing sources: value from the RAW frontmatter, NOT doc.Frontmatter — the parser
        // flattens a YAML flow list to its .NET type name, which would round-trip back into the file as
        // garbage and corrupt the frontmatter (unparseable YAML) on the next ingest.
        var existing = SourcesProvenance.FindKeyValue(fmBody, "sources:");
        var refs = SourcesProvenance.ParseFlowList(existing);
        var before = refs.ToList();
        mutate(refs);
        if (refs.SequenceEqual(before, StringComparer.Ordinal))
        {
            return; // nothing to record/prune
        }

        // An emptied list is written as "sources: []" — the key line stays stable rather than vanishing.
        var newLine = "sources: [" + string.Join(", ", refs) + "]\n";

        // Append the sources: key when absent, else replace the existing line in place.
        var newFmBody = existing is null
            ? fmBody + newLine
            : ReplaceKeyLine(fmBody, "sources:", newLine);

        // afterFm begins with the closing '---' delimiter; newFmBody already supplied the trailing newline.
        var rebuilt = raw[..fmStart] + newFmBody + afterFm;
        await _store.WriteAtomicAsync(path, rebuilt);
        _logger.SensitiveDebug("Ingest updated sources: frontmatter on page {Path}", path);
    }

    private static string ReplaceKeyLine(string fmBody, string keyPrefix, string newLine)
    {
        var sb = new StringBuilder();
        var replaced = false;
        foreach (var line in fmBody.Split('\n'))
        {
            if (!replaced && line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                sb.Append(newLine.TrimEnd('\n')).Append('\n');
                replaced = true;
            }
            else if (line.Length > 0)
            {
                sb.Append(line).Append('\n');
            }
        }

        return replaced ? sb.ToString() : fmBody + newLine;
    }

    // ---- helpers ----

    private static IEnumerable<string> TouchedTargets(IEnumerable<string> touched) =>
        touched.Select(p =>
        {
            var n = p.Replace('\\', '/');
            if (n.StartsWith("memory/", StringComparison.Ordinal))
            {
                n = n["memory/".Length..];
            }

            return n.EndsWith(".md", StringComparison.Ordinal) ? n[..^3] : n;
        });
}
