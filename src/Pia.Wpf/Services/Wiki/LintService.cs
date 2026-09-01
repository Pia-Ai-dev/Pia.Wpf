using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models.Vault;
using Pia.Services.Interfaces;
using Pia.Services.Search;

namespace Pia.Services.Wiki;

/// <summary>
/// Coherence lint pass (Task 7.2). A whole-vault sweep over the <c>memory/</c> pages that runs six
/// checks and journals every finding to <c>memory/log.md</c>:
///
/// <list type="bullet">
///   <item><b>Contradiction</b> (FLAG): the same entity field key (<c>- key: value</c> bullet) carries
///     DIFFERENT values across pages for the same entity (entity = a topic page's <c>title</c>, or a
///     structured doc's <c>## heading</c>).</item>
///   <item><b>Stale</b> (FLAG): a page's <c>sources:</c> frontmatter references a <c>sources/</c> file
///     that no longer exists. (Change/hash detection is deferred — missing-file is the testable core.)</item>
///   <item><b>Orphan</b> (FLAG): a topic page whose link target never appears as a <c>[[wikilink]]</c>
///     in any OTHER page body. Structured docs (profile/contacts/preferences) and the housekeeping
///     files are excluded — they are roots/derived artifacts, not wiki entities.</item>
///   <item><b>MissingXref</b> (AUTO-FIX): a page body mentions an entity by its exact heading/title text
///     where that entity has its own topic page but is not <c>[[linked]]</c> — a
///     <c>[[topics/&lt;slug&gt;]]</c> link is appended to the page and the finding is
///     <see cref="LintFinding.AutoFixed"/>.</item>
///   <item><b>Duplicate</b> (AUTO-FIX/merge): two TOPIC pages whose body embeddings have cosine
///     ≥ 0.9 — one is moved to <c>memory/.archive/&lt;name&gt;.md</c> and the original deleted.</item>
///   <item><b>GapPage</b> (FLAG): a <c>[[foo]]</c> wikilink whose target file <c>foo.md</c> does not
///     exist.</item>
/// </list>
///
/// <para><b>Scheduling.</b> This run is on-demand only. The run-after-N-ingests / scheduled trigger is
/// DEFERRED — a host (the ingest pipeline or a scheduler) calls <see cref="RunAsync"/>.</para>
///
/// <para><b>Embeddings.</b> Duplicate detection prefers the stored vector from the <c>Chunks</c> table
/// (<c>SELECT Embedding ... WHERE FilePath=…</c>); when no row is present (e.g. a page not yet indexed)
/// it recomputes the vector over the page body via <see cref="IEmbeddingService"/>.</para>
/// </summary>
public sealed class LintService : ILintService
{
    private const string PagesGlob = "memory/*.md";
    private const string ArchiveDir = "memory/.archive";
    private const float DuplicateThreshold = 0.9f;

    // [[target]] or [[target#Heading]] — capture the file portion (before any '#'), trimmed.
    private static readonly Regex WikilinkRef = new(@"\[\[([^\]\|#]+)(?:#[^\]\|]*)?(?:\|[^\]]*)?\]\]",
        RegexOptions.Compiled);

    // A field bullet "- key: value" (§4): key is up to the first ": ", value is the rest.
    private static readonly Regex FieldBullet = new(@"^- ([^:]+): (.*)$", RegexOptions.Compiled);

    // Housekeeping files that are never wiki entities/orphan candidates.
    private static readonly HashSet<string> Housekeeping = new(StringComparer.Ordinal)
    {
        "index", "log", "AGENTS", "charter", "templates",
    };

    private readonly IVaultStore _store;
    private readonly SqliteContext _context;
    private readonly IEmbeddingService _embeddings;
    private readonly VaultLogService _log;
    private readonly IIngestService _ingest;
    private readonly ILogger<LintService> _logger;

    public LintService(
        IVaultStore store,
        SqliteContext context,
        IEmbeddingService embeddings,
        VaultLogService log,
        IIngestService ingest,
        ILogger<LintService> logger)
    {
        _store = store;
        _context = context;
        _embeddings = embeddings;
        _log = log;
        _ingest = ingest;
        _logger = logger;
    }

    public async Task<LintReport> RunAsync(DateOnly date, bool applyFixes = true, CancellationToken ct = default)
    {
        // Load every memory page once (excluding the housekeeping files). Auto-fix checks rewrite the
        // store and re-read the affected pages as needed.
        var pages = await LoadPagesAsync(ct);
        var findings = new List<LintFinding>();

        findings.AddRange(CheckContradictions(pages));
        findings.AddRange(CheckStale(pages));
        findings.AddRange(CheckOrphans(pages));
        findings.AddRange(await CheckMissingXrefsAsync(pages, applyFixes, ct));
        findings.AddRange(CheckGapPages(pages));
        findings.AddRange(await CheckDuplicatesAsync(pages, applyFixes, ct));

        // Journal each finding (sensitive: path/value detail never goes to a plain log line).
        foreach (var finding in findings)
        {
            await _log.AppendAsync("lint", $"{finding.Kind}: {finding.Detail}", date);
            _logger.SensitiveDebug("Lint {Kind} autofixed={Auto}: {Detail}",
                finding.Kind, finding.AutoFixed, finding.Detail);
        }

        return new LintReport(findings);
    }

    // ---- page model ----

    private sealed record Page(string Path, string Target, VaultDocument Doc)
    {
        public string Title => Doc.Frontmatter.TryGetValue("title", out var t) ? t : Target;
        public bool IsTopic => Target.StartsWith("topics/", StringComparison.Ordinal);
        public string Body => Doc.Sections.Count == 0 ? Doc.Preamble : Doc.RawText;
    }

    private async Task<List<Page>> LoadPagesAsync(CancellationToken ct)
    {
        var pages = new List<Page>();
        foreach (var path in await _store.EnumerateAsync(PagesGlob))
        {
            ct.ThrowIfCancellationRequested();
            var normalizedPath = path.Replace('\\', '/');

            // Never lint archived copies: a previous duplicate-merge moved the loser here, and
            // re-loading it would resurrect contradiction/missing-xref noise against the live page
            // (the merge would self-defeat across runs).
            if (normalizedPath.StartsWith(ArchiveDir + "/", StringComparison.Ordinal))
            {
                continue;
            }

            var target = ToTarget(path);
            if (target is null || Housekeeping.Contains(target))
            {
                continue;
            }

            var doc = await _store.ReadAsync(path);
            if (doc is not null)
            {
                pages.Add(new Page(normalizedPath, target, doc));
            }
        }

        return pages;
    }

    // ---- 1. Contradictions (FLAG) ----

    private static IEnumerable<LintFinding> CheckContradictions(IReadOnlyList<Page> pages)
    {
        // Map (entity, key) -> set of (value, originating page). A value clash is a contradiction.
        var byEntityKey = new Dictionary<(string Entity, string Key), List<(string Value, string Path)>>();

        foreach (var page in pages)
        {
            foreach (var (entity, body) in EnumerateRecords(page))
            {
                foreach (var line in SplitLines(body))
                {
                    var m = FieldBullet.Match(line.TrimEnd('\r'));
                    if (!m.Success)
                    {
                        continue;
                    }

                    var key = m.Groups[1].Value.Trim();
                    var value = m.Groups[2].Value.Trim();
                    var k = (entity, key);
                    if (!byEntityKey.TryGetValue(k, out var values))
                    {
                        byEntityKey[k] = values = [];
                    }

                    if (!values.Any(v => string.Equals(v.Value, value, StringComparison.Ordinal)))
                    {
                        values.Add((value, page.Path));
                    }
                }
            }
        }

        foreach (var ((entity, key), values) in byEntityKey)
        {
            if (values.Count > 1)
            {
                var rendered = string.Join(" | ", values.Select(v => $"{v.Value} ({v.Path})"));
                yield return new LintFinding(
                    LintKind.Contradiction,
                    $"'{entity}' field '{key}' has conflicting values: {rendered}",
                    AutoFixed: false);
            }
        }
    }

    // A topic/freeform page is one entity (title + preamble bullets); a structured doc is many records
    // (one per ## heading). Yields (entity-name, record-body) pairs for the contradiction grouping.
    private static IEnumerable<(string Entity, string Body)> EnumerateRecords(Page page)
    {
        if (page.Doc.Sections.Count == 0)
        {
            yield return (page.Title, page.Doc.Preamble);
            yield break;
        }

        foreach (var section in page.Doc.Sections)
        {
            yield return (section.Heading, section.Body);
        }
    }

    // ---- 2. Stale (FLAG) ----

    private IEnumerable<LintFinding> CheckStale(IReadOnlyList<Page> pages)
    {
        foreach (var page in pages)
        {
            if (!page.Doc.Frontmatter.TryGetValue("sources", out var sources)
                || string.IsNullOrWhiteSpace(sources))
            {
                continue;
            }

            foreach (var sourceRef in ParseFlowList(sources))
            {
                var absolute = System.IO.Path.Combine(
                    _store.Root, sourceRef.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.File.Exists(absolute))
                {
                    yield return new LintFinding(
                        LintKind.Stale,
                        $"{page.Path} references missing source '{sourceRef}'",
                        AutoFixed: false);
                }
            }
        }
    }

    // ---- 3. Orphans (FLAG) ----

    private static IEnumerable<LintFinding> CheckOrphans(IReadOnlyList<Page> pages)
    {
        // Inbound link targets gathered from EVERY page body (a page does not link itself into existence).
        var linkedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            foreach (var target in WikilinkTargets(page.Body))
            {
                linkedTargets.Add(target);
            }
        }

        foreach (var page in pages)
        {
            // Only topic pages are orphan candidates; structured roots/derived docs are excluded.
            if (!page.IsTopic)
            {
                continue;
            }

            if (!linkedTargets.Contains(page.Target))
            {
                yield return new LintFinding(
                    LintKind.Orphan,
                    $"{page.Path} has no inbound [[wikilink]] from any other page",
                    AutoFixed: false);
            }
        }
    }

    // ---- 4. MissingXref (AUTO-FIX) ----

    private async Task<IEnumerable<LintFinding>> CheckMissingXrefsAsync(
        List<Page> pages, bool applyFixes, CancellationToken ct)
    {
        var findings = new List<LintFinding>();

        // Entity name (exact heading/title text) -> its own topic-page target, for entities that have a
        // dedicated topic page. Used to spot an unlinked mention.
        var entityTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (page.IsTopic)
            {
                entityTarget[page.Title] = page.Target;
            }
        }

        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[i];
            var raw = page.Doc.RawText;       // write-back operates on the whole file (preserves frontmatter)
            var content = page.Body;          // match only against page content, never frontmatter values
            var toLink = new List<(string Entity, string Target)>();

            foreach (var (entity, target) in entityTarget)
            {
                if (target == page.Target)
                {
                    continue; // never self-link
                }

                var link = $"[[{target}]]";
                // Word-boundary mention so a short title (e.g. "Ace") does not match inside a larger
                // word (e.g. "Space"); only insert if the page does not already link the target.
                if (MentionsEntity(content, entity)
                    && !raw.Contains(link, StringComparison.Ordinal))
                {
                    toLink.Add((entity, target));
                }
            }

            if (toLink.Count == 0)
            {
                continue;
            }

            var sb = new StringBuilder(raw);
            if (!raw.EndsWith('\n'))
            {
                sb.Append('\n');
            }

            foreach (var (entity, target) in toLink)
            {
                sb.Append("See also: [[").Append(target).Append("]]\n");
                findings.Add(new LintFinding(
                    LintKind.MissingXref,
                    $"{page.Path} mentions '{entity}' — {(applyFixes ? "inserted" : "would insert")} [[{target}]]",
                    AutoFixed: applyFixes));
            }

            if (!applyFixes)
            {
                continue;
            }

            var rewritten = sb.ToString();
            await _store.WriteAtomicAsync(page.Path, rewritten);

            // Keep the in-memory page in sync so later checks (gap/duplicate) see the inserted links.
            var reread = await _store.ReadAsync(page.Path);
            if (reread is not null)
            {
                pages[i] = page with { Doc = reread };
            }
        }

        return findings;
    }

    // ---- 5. GapPages (FLAG) ----

    private IEnumerable<LintFinding> CheckGapPages(IReadOnlyList<Page> pages)
    {
        var existing = new HashSet<string>(pages.Select(p => p.Target), StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            foreach (var target in WikilinkTargets(page.Body))
            {
                if (existing.Contains(target))
                {
                    continue;
                }

                // Confirm against disk (a target may be a structured/housekeeping page excluded from
                // `pages`, or any other vault file) before declaring a gap.
                var relative = $"memory/{target}.md";
                var absolute = System.IO.Path.Combine(
                    _store.Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(absolute))
                {
                    continue;
                }

                if (reported.Add(target))
                {
                    yield return new LintFinding(
                        LintKind.GapPage,
                        $"[[{target}]] links to a page that does not exist",
                        AutoFixed: false);
                }
            }
        }
    }

    // ---- 6. Duplicates (AUTO-FIX / merge) ----

    private async Task<IEnumerable<LintFinding>> CheckDuplicatesAsync(
        List<Page> pages, bool applyFixes, CancellationToken ct)
    {
        var findings = new List<LintFinding>();

        var topics = pages.Where(p => p.IsTopic).ToList();
        var vectors = new List<(Page Page, float[] Embedding)>();
        foreach (var page in topics)
        {
            var emb = await GetEmbeddingAsync(page, ct);
            if (emb is { Length: > 0 })
            {
                vectors.Add((page, emb));
            }
        }

        var archived = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < vectors.Count; i++)
        {
            if (archived.Contains(vectors[i].Page.Path))
            {
                continue;
            }

            for (var j = i + 1; j < vectors.Count; j++)
            {
                if (archived.Contains(vectors[j].Page.Path))
                {
                    continue;
                }

                var score = VectorSearchHelper.CosineSimilarity(vectors[i].Embedding, vectors[j].Embedding);
                if (score < DuplicateThreshold)
                {
                    continue;
                }

                // Merge: keep the first (i), fold the second (j) into it. A real merge, not just an
                // archive — the loser's sources: move across, or the next ingest of one of them mints
                // the page again and the pass undoes itself.
                var loser = vectors[j].Page;
                var keeper = vectors[i].Page;
                if (!applyFixes)
                {
                    archived.Add(loser.Path); // keep the dry run's pairing identical to the real one
                    findings.Add(new LintFinding(
                        LintKind.Duplicate,
                        $"{loser.Path} duplicates {keeper.Path} (cosine {score:F2}) — would merge into {keeper.Path}",
                        AutoFixed: false));
                    continue;
                }

                if (await _ingest.MergeTopicPagesAsync(keeper.Path, loser.Path, ct))
                {
                    archived.Add(loser.Path);
                    findings.Add(new LintFinding(
                        LintKind.Duplicate,
                        $"{loser.Path} duplicates {keeper.Path} (cosine {score:F2}) — merged into {keeper.Path}",
                        AutoFixed: true));
                }
            }
        }

        return findings;
    }

    private async Task<bool> ArchiveAsync(Page loser, CancellationToken ct)
    {
        var doc = await _store.ReadAsync(loser.Path);
        if (doc is null)
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        var name = System.IO.Path.GetFileName(loser.Path);
        await _store.WriteAtomicAsync($"{ArchiveDir}/{name}", doc.RawText);
        await _store.DeleteAsync(loser.Path);
        return true;
    }

    // Whole-word/phrase mention of an entity in page content (alphanumeric boundaries on both sides),
    // so a short title like "Ace" does not spuriously match inside "Space".
    private static bool MentionsEntity(string content, string entity)
    {
        if (string.IsNullOrEmpty(entity))
        {
            return false;
        }

        var pattern = "(?<![A-Za-z0-9])" + Regex.Escape(entity) + "(?![A-Za-z0-9])";
        return Regex.IsMatch(content, pattern);
    }

    // Stored Chunks vector for the page (first row), else recompute over the page body.
    private async Task<float[]?> GetEmbeddingAsync(Page page, CancellationToken ct)
    {
        var stored = ReadStoredEmbedding(page.Path);
        if (stored is { Length: > 0 })
        {
            return stored;
        }

        if (!_embeddings.IsModelAvailable)
        {
            return null;
        }

        return await _embeddings.GenerateEmbeddingAsync(page.Body, ct);
    }

    private float[]? ReadStoredEmbedding(string path)
    {
        try
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Embedding FROM Chunks WHERE FilePath = @FilePath AND Embedding IS NOT NULL LIMIT 1";
            command.Parameters.AddWithValue("@FilePath", path);
            var result = command.ExecuteScalar();
            return result is byte[] bytes && bytes.Length > 0 ? _embeddings.BytesToFloats(bytes) : null;
        }
        catch
        {
            // Best-effort: a missing/locked Chunks table falls back to recompute.
            return null;
        }
    }

    // ---- helpers ----

    // Vault-relative target (path without ".md", '/'-separated) for a memory/*.md page, or null if the
    // path is not a memory page.
    private static string? ToTarget(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("memory/", StringComparison.Ordinal))
        {
            normalized = normalized["memory/".Length..];
        }

        if (!normalized.EndsWith(".md", StringComparison.Ordinal))
        {
            return null;
        }

        return normalized[..^3];
    }

    private static IEnumerable<string> WikilinkTargets(string body)
    {
        foreach (Match m in WikilinkRef.Matches(body))
        {
            var target = m.Groups[1].Value.Trim();
            if (target.Length > 0)
            {
                yield return target;
            }
        }
    }

    // Parse a "sources" frontmatter value (flow list "[a, b]" or a flattened scalar) into refs.
    private static IEnumerable<string> ParseFlowList(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        foreach (var part in trimmed.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim().Trim('-').Trim();
            if (item.Length > 0)
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
