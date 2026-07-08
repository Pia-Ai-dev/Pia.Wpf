using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models.Vault;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Ingest pipeline (Task 7.1) — a fan-out compiler that turns a RAW source under <c>sources/</c> into
/// <c>memory/topics/</c> wiki pages and keeps the index / log / per-page provenance current.
///
/// <para>Orchestration (mirrors the plan reference):</para>
/// <list type="number">
///   <item>Read the raw source text directly under <see cref="IVaultStore.Root"/> (<c>sources/</c> is
///     immutable — read only). Non-text/binary sources are skipped (logged, empty result); binary
///     handling is deferred.</item>
///   <item>Summarize + extract entities via <see cref="IIngestExtractor"/>.</item>
///   <item>For each entity, upsert the machine-managed <c>## Source: &lt;sourceRef&gt;</c> section on
///     <c>memory/topics/&lt;slug&gt;.md</c>. Re-ingesting the same source REPLACES exactly that
///     section, so a source's contribution never duplicates and manual content (preamble, other
///     sections) is never touched. Crosslinks (<c>Related: [[topics/&lt;slug&gt;]]</c> to co-extracted
///     entities mentioned in the facts) live INSIDE the section so replace/removal takes them along.</item>
///   <item>Upsert an index entry per touched page.</item>
///   <item>Append one <c>ingest</c> log line naming the source and touched pages.</item>
///   <item>Record the source in each touched page's <c>sources:</c> frontmatter (best-effort).</item>
/// </list>
///
/// <para><see cref="RemoveContributionsAsync"/> is the inverse: strip the source's section and
/// frontmatter ref from each page; pages left with no sections and a whitespace-only preamble are
/// deleted together with their index entry.</para>
///
/// <para><b>sources: round-trip limitation.</b> The frontmatter maintainer writes/extends a YAML flow
/// list (<c>sources: [sources/a, sources/b]</c>). Because the parser flattens YAML lists to a single
/// string (same root cause as the index.md §2.3 limitation), multi-source round-trip across edits is
/// best-effort.</para>
///
/// <para><b>Deferred:</b> a long-running background-job handle + progress UI — ingest runs inline.</para>
/// </summary>
public sealed class IngestService : IIngestService
{
    /// <summary>Heading prefix of the machine-managed per-source section in a topic page.</summary>
    public const string SourceHeadingPrefix = "Source: ";

    private static string SourceHeading(string sourceRef) => SourceHeadingPrefix + sourceRef;

    private static bool IsSectionFor(VaultSection s, string sourceRef) =>
        s.Heading.Equals(SourceHeadingPrefix + sourceRef, StringComparison.OrdinalIgnoreCase);

    private readonly IIngestExtractor _extractor;
    private readonly IVaultStore _store;
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<IngestService> _logger;

    public IngestService(
        IIngestExtractor extractor,
        IVaultStore store,
        VaultIndexService index,
        VaultLogService log,
        IEmbeddingService embeddings,
        ILogger<IngestService> logger)
    {
        _extractor = extractor;
        _store = store;
        _index = index;
        _log = log;
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task<IngestResult> IngestAsync(
        string sourceRelativePath, DateOnly date, CancellationToken ct = default)
    {
        var sourceRef = sourceRelativePath.Replace('\\', '/');
        var sourceName = Path.GetFileName(sourceRef);

        // 1. Read the RAW source directly under the vault root (sources/ files are not Pia-managed
        // markdown, so we do NOT parse them through IVaultStore.ReadAsync).
        var rootFull = Path.GetFullPath(_store.Root);
        var absolute = Path.GetFullPath(
            Path.Combine(rootFull, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        // Containment guard: source_ref reaches this service from a model tool call, so refuse any
        // absolute path or '..' traversal that resolves OUTSIDE the vault — otherwise an injected
        // prompt could exfiltrate an arbitrary local text file into memory (which syncs).
        if (!absolute.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

        // 2. Summarize + extract.
        var summary = await _extractor.SummarizeAsync(content, ct);
        var entities = await _extractor.ExtractEntitiesAsync(content, ct);
        _logger.SensitiveDebug("Ingest {Source} extracted {Count} entities", sourceRef, entities.Count);

        // 3. Fan out: one machine-managed "## Source: <ref>" section per entity's topic page — replaced
        // in place on re-ingest, so the same source never duplicates content. Crosslinks are computed up
        // front from the extracted entity set and written INTO the section body (not appended to the
        // page) so they are replaced/removed together with the section.
        var touched = new List<string>();
        var firstFactBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (string.IsNullOrWhiteSpace(entity.Subject))
            {
                continue;
            }

            var slug = VaultSlug.Slugify(entity.Subject);
            var path = $"memory/topics/{slug}.md";

            var body = new StringBuilder(NormalizeFactsToBullets(entity.Facts));
            foreach (var other in entities)
            {
                if (ReferenceEquals(other, entity) || string.IsNullOrWhiteSpace(other.Subject))
                {
                    continue;
                }

                var otherSlug = VaultSlug.Slugify(other.Subject);
                if (otherSlug == slug)
                {
                    continue;
                }

                if (entity.Facts.Contains(other.Subject, StringComparison.OrdinalIgnoreCase))
                {
                    body.Append($"Related: [[topics/{otherSlug}]]\n");
                }
            }

            await UpsertSourceSectionAsync(path, entity.Subject, sourceRef, body.ToString());
            if (!touched.Contains(path))
            {
                touched.Add(path);
            }

            firstFactBySlug[slug] = FirstLine(entity.Facts);
        }

        if (touched.Count == 0)
        {
            _logger.SensitiveDebug("Ingest produced no topic pages for {Source}", sourceRef);
            return new IngestResult(sourceRef, [], IngestOutcome.NoEntities);
        }

        // 4. Index entry per touched page (entity first-fact line, else the overall summary).
        foreach (var path in touched)
        {
            var slug = VaultSlug.Slugify(Path.GetFileNameWithoutExtension(path));
            var oneLine = firstFactBySlug.TryGetValue(slug, out var fact) && !string.IsNullOrWhiteSpace(fact)
                ? fact
                : summary;
            await _index.UpsertEntryAsync(path, oneLine);
        }

        // 5. Journal one ingest line.
        await _log.AppendAsync("ingest", sourceName + " -> " + string.Join(", ", TouchedTargets(touched)), date);

        // 6. Provenance: record the source in each touched page's frontmatter (best-effort).
        foreach (var path in touched)
        {
            await EnsureSourceInFrontmatterAsync(path, sourceRef);
        }

        return new IngestResult(sourceRef, touched);
    }

    /// <inheritdoc />
    public async Task RemoveContributionsAsync(
        string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default)
    {
        sourceRef = sourceRef.Replace('\\', '/'); // separator-tolerant, matching IngestAsync

        foreach (var path in pages)
        {
            ct.ThrowIfCancellationRequested();
            var doc = await _store.ReadAsync(path);
            if (doc is null)
            {
                continue;
            }

            var section = doc.Sections.FirstOrDefault(s => IsSectionFor(s, sourceRef));
            if (section is not null)
            {
                // Splice out the heading LINE + body, so the whole source record disappears.
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

    // ---- per-source section upsert ----

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

    // Normalize an entity's facts blob into "- " bullet lines, one per non-empty input line, with a
    // trailing '\n' so the section body always ends on a line boundary.
    private static string NormalizeFactsToBullets(string facts)
    {
        var sb = new StringBuilder();
        foreach (var line in facts.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith('-'))
            {
                sb.Append("- ");
            }

            sb.Append(trimmed).Append('\n');
        }

        return sb.ToString();
    }

    // Given a section's BodyStart (the byte just after the heading line's '\n' terminator), return the
    // index of the first byte of that heading line. BodyStart-1 is the heading line's own terminator;
    // we walk back ONE MORE '\n' (the terminator of the line before the heading) and step past it, or
    // to 0 when the heading is the file's first line.
    private static int HeadingLineStart(string raw, int bodyStart)
    {
        // Index of the heading line's terminating '\n' (BodyStart-1), if the body started after one.
        var headingTerminator = bodyStart - 1;
        if (headingTerminator < 0 || headingTerminator > raw.Length - 1 || raw[headingTerminator] != '\n')
        {
            // No trailing '\n' on the heading line (heading is the file's last line); scan from end.
            headingTerminator = raw.Length;
        }

        var lineBefore = raw.LastIndexOf('\n', Math.Max(headingTerminator - 1, 0));
        return lineBefore < 0 ? 0 : lineBefore + 1;
    }

    // ---- sources: frontmatter maintainer (best-effort YAML flow list) ----

    private Task EnsureSourceInFrontmatterAsync(string path, string sourceRef) =>
        RewriteSourcesFrontmatterAsync(path, refs =>
        {
            if (!refs.Contains(sourceRef, StringComparer.OrdinalIgnoreCase))
            {
                refs.Add(sourceRef);
            }
        });

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
        var open = raw.IndexOf("---\n", StringComparison.Ordinal);
        if (open != 0)
        {
            // No leading frontmatter block — leave the file untouched.
            return;
        }

        var close = raw.IndexOf("\n---", open + 3, StringComparison.Ordinal);
        if (close < 0)
        {
            return;
        }

        var fmBody = raw[(open + 4)..(close + 1)]; // keys block, ends with the '\n' before '---'
        var afterFm = raw[(close + 1)..];          // starts at the closing '---' line

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
        var rebuilt = raw[..(open + 4)] + newFmBody + afterFm;
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

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.TrimStart('-', '*', ' ', '\t').Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }
}
