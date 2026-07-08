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
///     <see cref="VaultFrontmatter.BuildPreserving"/>.</item>
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

    private readonly IIngestExtractor _extractor;
    private readonly IVaultStore _store;
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;
    private readonly IIngestSynthesizer _synth;
    private readonly VaultCharterService _charter;
    private readonly ILogger<IngestService> _logger;

    public IngestService(
        IIngestExtractor extractor,
        IVaultStore store,
        VaultIndexService index,
        VaultLogService log,
        IIngestSynthesizer synth,
        VaultCharterService charter,
        ILogger<IngestService> logger)
    {
        _extractor = extractor;
        _store = store;
        _index = index;
        _log = log;
        _synth = synth;
        _charter = charter;
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
        var topics = await _extractor.DiscoverTopicsAsync(content, charter, ct);
        _logger.SensitiveDebug("Ingest {Source} discovered {Count} topics", sourceRef, topics.Count);
        if (topics.Count == 0)
        {
            return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);
        }

        // 3. Topic-driven synthesis: for each topic, union this source with the page's existing sources
        // and re-synthesize the whole managed body across all of them.
        var touched = new List<string>();
        var synthFailures = 0;
        foreach (var topic in topics)
        {
            if (string.IsNullOrWhiteSpace(topic.Subject))
            {
                continue;
            }

            var slug = VaultSlug.Slugify(topic.Subject);
            var path = $"memory/topics/{slug}.md";

            var existing = await _store.ReadAsync(path);
            var sourceRefs = ReadPageSources(existing);
            if (!sourceRefs.Contains(sourceRef, StringComparer.OrdinalIgnoreCase))
            {
                sourceRefs.Add(sourceRef);
            }

            var title = existing?.Frontmatter.GetValueOrDefault("title") is { Length: > 0 } t
                ? t
                : topic.Subject;
            var category = existing?.Frontmatter.GetValueOrDefault("category") is { Length: > 0 } c
                ? c
                : topic.Category;

            var summary = await SynthesizePageAsync(path, title, category, sourceRefs, charter, ct);
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
                path, title, category, remaining, await _charter.GetCharterAsync(), ct);
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

    // ---- shared synthesis writer ----

    // Returns the index one-liner, or null when synthesis produced nothing (page left untouched).
    private async Task<string?> SynthesizePageAsync(
        string path, string title, string category,
        List<string> sourceRefs, string charter, CancellationToken ct)
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
        var page = await _synth.SynthesizeAsync(title, category, charter, sources, ct);
        if (string.IsNullOrWhiteSpace(page.Body))
        {
            return null;
        }

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
        sb.Append(page.Body.Trim()).Append('\n');

        var content = WriteSourcesLine(sb.ToString(), sourceRefs); // "sources: [a, b]" into the block
        await _store.WriteAtomicAsync(path, content);
        _logger.SensitiveDebug("Ingest synthesized topic page {Path}", path);
        return page.Summary;
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
        if (absolute is null || !File.Exists(absolute) || !SourcesProvenance.IsTextSource(sourceRef))
        {
            return null;
        }

        return await File.ReadAllTextAsync(absolute, ct);
    }

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
