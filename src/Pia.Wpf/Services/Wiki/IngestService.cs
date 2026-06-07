using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
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
///   <item>For each entity, <see cref="IMemoryService.RememberAsync"/>("topic", …,
///     createOnAmbiguous: true) — page-granularity dedup, so re-ingesting the same source does not
///     create duplicate pages/sections. Collect the touched <c>memory/topics/&lt;slug&gt;.md</c> paths.</item>
///   <item>Crosslink (best-effort, minimal — see note).</item>
///   <item>Upsert an index entry per touched page.</item>
///   <item>Append one <c>ingest</c> log line naming the source and touched pages.</item>
///   <item>Record the source in each touched page's <c>sources:</c> frontmatter (best-effort).</item>
/// </list>
///
/// <para><b>Crosslink scope.</b> Minimal: for each touched page whose body mentions another touched
/// entity's subject and that is not already linked, a single <c>Related: [[topics/&lt;slug&gt;]]</c>
/// line is appended. No reverse-link maintenance or transitive crosslinking.</para>
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
    private static readonly string[] TextExtensions =
        [".txt", ".md", ".markdown", ".text", ".csv", ".json", ".log", ".html", ".htm", ".xml"];

    private readonly IIngestExtractor _extractor;
    private readonly IMemoryService _memory;
    private readonly IVaultStore _store;
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<IngestService> _logger;

    public IngestService(
        IIngestExtractor extractor,
        IMemoryService memory,
        IVaultStore store,
        VaultIndexService index,
        VaultLogService log,
        IEmbeddingService embeddings,
        ILogger<IngestService> logger)
    {
        _extractor = extractor;
        _memory = memory;
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
        var absolute = Path.Combine(_store.Root, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            _logger.SensitiveDebug("Ingest source not found {Source}", sourceRef);
            return new IngestResult(sourceRef, []);
        }

        if (!IsTextSource(sourceRef))
        {
            // Binary handling (PDF/image extraction) is DEFERRED — skip with an empty result.
            _logger.SensitiveDebug("Ingest skipping non-text source {Source}", sourceRef);
            return new IngestResult(sourceRef, []);
        }

        var content = await File.ReadAllTextAsync(absolute, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.SensitiveDebug("Ingest source empty {Source}", sourceRef);
            return new IngestResult(sourceRef, []);
        }

        // 2. Summarize + extract.
        var summary = await _extractor.SummarizeAsync(content, ct);
        var entities = await _extractor.ExtractEntitiesAsync(content, ct);
        _logger.SensitiveDebug("Ingest {Source} extracted {Count} entities", sourceRef, entities.Count);

        // 3. Fan out: one topic page per entity. createOnAmbiguous keeps a write always landing while the
        // deterministic upsert path dedups a re-ingested source into the SAME page (no duplicates).
        var touched = new List<string>();
        var firstFactBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        var subjectBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (string.IsNullOrWhiteSpace(entity.Subject))
            {
                continue;
            }

            await _memory.RememberAsync("topic", entity.Subject, entity.Facts, createOnAmbiguous: true);

            var slug = VaultSlug.Slugify(entity.Subject);
            var path = $"memory/topics/{slug}.md";
            if (!touched.Contains(path))
            {
                touched.Add(path);
            }

            firstFactBySlug[slug] = FirstLine(entity.Facts);
            subjectBySlug[slug] = entity.Subject;
        }

        if (touched.Count == 0)
        {
            _logger.SensitiveDebug("Ingest produced no topic pages for {Source}", sourceRef);
            return new IngestResult(sourceRef, []);
        }

        // 4. Crosslink (best-effort, minimal): if one touched page's body mentions another touched
        // entity's subject and is not already linked, append a single "Related: [[topics/<slug>]]" line.
        await CrosslinkAsync(touched, subjectBySlug);

        // 5. Index entry per touched page (entity first-fact line, else the overall summary).
        foreach (var path in touched)
        {
            var slug = VaultSlug.Slugify(Path.GetFileNameWithoutExtension(path));
            var oneLine = firstFactBySlug.TryGetValue(slug, out var fact) && !string.IsNullOrWhiteSpace(fact)
                ? fact
                : summary;
            await _index.UpsertEntryAsync(path, oneLine);
        }

        // 6. Journal one ingest line.
        await _log.AppendAsync("ingest", sourceName + " -> " + string.Join(", ", TouchedTargets(touched)), date);

        // 7. Provenance: record the source in each touched page's frontmatter (best-effort).
        foreach (var path in touched)
        {
            await EnsureSourceInFrontmatterAsync(path, sourceRef);
        }

        return new IngestResult(sourceRef, touched);
    }

    // ---- crosslink (minimal) ----

    private async Task CrosslinkAsync(IReadOnlyList<string> touched, IReadOnlyDictionary<string, string> subjectBySlug)
    {
        foreach (var path in touched)
        {
            var doc = await _store.ReadAsync(path);
            if (doc is null)
            {
                continue;
            }

            var body = doc.RawText;
            var lines = new List<string>();
            var ownSlug = VaultSlug.Slugify(Path.GetFileNameWithoutExtension(path));

            foreach (var (otherSlug, otherSubject) in subjectBySlug)
            {
                if (otherSlug == ownSlug)
                {
                    continue;
                }

                var link = $"[[topics/{otherSlug}]]";
                if (body.Contains(otherSubject, StringComparison.OrdinalIgnoreCase)
                    && !body.Contains(link, StringComparison.Ordinal))
                {
                    lines.Add($"Related: {link}");
                }
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var separator = body.EndsWith('\n') ? string.Empty : "\n";
            var appended = body + separator + string.Join('\n', lines) + "\n";
            await _store.WriteAtomicAsync(path, appended);
            _logger.SensitiveDebug("Ingest crosslinked page {Path} ({Count} link(s))", path, lines.Count);
        }
    }

    // ---- sources: frontmatter maintainer (best-effort YAML flow list) ----

    private async Task EnsureSourceInFrontmatterAsync(string path, string sourceRef)
    {
        var doc = await _store.ReadAsync(path);
        if (doc is null)
        {
            return;
        }

        var raw = doc.RawText;
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

        var existing = doc.Frontmatter.TryGetValue("sources", out var current) ? current : null;
        var refs = ParseFlowList(existing);
        if (refs.Contains(sourceRef, StringComparer.Ordinal))
        {
            return; // already recorded
        }

        refs.Add(sourceRef);
        var newLine = "sources: [" + string.Join(", ", refs) + "]\n";

        string newFmBody;
        if (existing is null)
        {
            // Append the sources: key at the end of the keys block.
            newFmBody = fmBody + newLine;
        }
        else
        {
            // Replace the existing sources: line in place.
            newFmBody = ReplaceKeyLine(fmBody, "sources:", newLine);
        }

        // afterFm begins with the closing '---' delimiter; newFmBody already supplied the trailing newline.
        var rebuilt = raw[..(open + 4)] + newFmBody + afterFm;
        await _store.WriteAtomicAsync(path, rebuilt);
        _logger.SensitiveDebug("Ingest recorded source {Source} on page {Path}", sourceRef, path);
    }

    // Parse a flattened "sources" value back into individual refs. The parser may flatten a YAML list to
    // either a flow form "[a, b]" or a space/newline-joined scalar; handle both leniently.
    private static List<string> ParseFlowList(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        foreach (var part in trimmed.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim().Trim('-').Trim();
            if (item.Length > 0 && !result.Contains(item, StringComparer.Ordinal))
            {
                result.Add(item);
            }
        }

        return result;
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

    private static bool IsTextSource(string sourceRef)
    {
        var ext = Path.GetExtension(sourceRef);
        if (string.IsNullOrEmpty(ext))
        {
            return true; // extension-less files are treated as text (best-effort)
        }

        return TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

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
