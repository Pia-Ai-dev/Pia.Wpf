using System.Text;
using System.Text.RegularExpressions;
using Pia.Models.Vault;
using Pia.Services.Interfaces;
using Pia.Services.Search;
using Pia.Services.Similarity;

namespace Pia.Services;

/// <summary>
/// Resolves the target section for a structured upsert and performs deterministic field-level
/// (bullet) body merges per the vault format spec (§4). See <see cref="ISectionUpsertService"/>.
/// </summary>
public sealed class SectionUpsertService : ISectionUpsertService
{
    // Bands per the write-path design.
    private const double EditThreshold = 0.85;
    private const double AmbiguousThreshold = 0.60;

    // Field bullet: ^- (key): (value)$  — key is everything up to the first ": ".
    private static readonly Regex BulletRegex = new(@"^- ([^:]+): (.*)$", RegexOptions.Compiled);

    private readonly IEmbeddingService _embeddings;

    public SectionUpsertService(IEmbeddingService embeddings)
        => _embeddings = embeddings;

    public async Task<UpsertResolution> ResolveAsync(VaultDocument doc, string subject, string content)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);

        if (doc.Sections.Count == 0)
        {
            return new UpsertResolution(UpsertBand.Create, null, []);
        }

        var subjectEmbedding = await _embeddings.GenerateEmbeddingAsync($"{subject}\n{content}");

        var scored = new List<(string Slug, double Score)>(doc.Sections.Count);
        foreach (var section in doc.Sections)
        {
            var lexical = JaroWinkler.Similarity(subject, section.Heading);
            var sectionEmbedding = await _embeddings.GenerateEmbeddingAsync($"{section.Heading}\n{section.Body}");
            var vector = VectorSearchHelper.CosineSimilarity(subjectEmbedding, sectionEmbedding);
            var score = Math.Max(lexical, vector);
            scored.Add((section.Slug, score));
        }

        var best = scored.MaxBy(s => s.Score);

        if (best.Score >= EditThreshold)
        {
            return new UpsertResolution(UpsertBand.Edit, best.Slug, []);
        }

        if (best.Score >= AmbiguousThreshold)
        {
            var candidates = scored
                .Where(s => s.Score >= AmbiguousThreshold)
                .OrderByDescending(s => s.Score)
                .Select(s => s.Slug)
                .ToList();
            return new UpsertResolution(UpsertBand.Ambiguous, null, candidates);
        }

        return new UpsertResolution(UpsertBand.Create, null, []);
    }

    /// <summary>
    /// Prose-handling choice (spec §4): the merge reassembles the existing body's <c>- key: value</c>
    /// bullet block first — in original order, with matching keys' values replaced in place and brand-new
    /// keys appended after the last bullet — then re-appends every non-bullet (prose / blank) line from
    /// the existing body, in their original relative order, after the bullet block. New body lines that
    /// are not field bullets are ignored (field-level merge handles bullets only; prose authoring is a
    /// separate, model-bounded operation). Blank lines from the existing body are preserved as prose, so
    /// a bullets-then-blank-then-prose layout round-trips. The original line terminator (LF vs CRLF) and
    /// a trailing terminator are preserved.
    /// </summary>
    public string MergeBullets(string existingBody, string newBody)
    {
        ArgumentNullException.ThrowIfNull(existingBody);
        ArgumentNullException.ThrowIfNull(newBody);

        var newline = existingBody.Contains("\r\n") ? "\r\n" : "\n";
        var endsWithNewline = existingBody.EndsWith('\n');

        var existingLines = SplitLines(existingBody);

        // Ordered map of bullet keys -> value, plus the trailing prose lines (in original order).
        var keyOrder = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var prose = new List<string>();

        foreach (var line in existingLines)
        {
            var match = BulletRegex.Match(line);
            if (match.Success)
            {
                var key = match.Groups[1].Value.Trim();
                if (!values.ContainsKey(key))
                {
                    keyOrder.Add(key);
                }
                values[key] = match.Groups[2].Value;
            }
            else
            {
                prose.Add(line);
            }
        }

        // Apply the new body's bullets as upserts.
        foreach (var line in SplitLines(newBody))
        {
            var match = BulletRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups[1].Value.Trim();
            if (!values.ContainsKey(key))
            {
                keyOrder.Add(key);
            }
            values[key] = match.Groups[2].Value;
        }

        var output = new List<string>(keyOrder.Count + prose.Count);
        foreach (var key in keyOrder)
        {
            output.Add($"- {key}: {values[key]}");
        }
        output.AddRange(prose);

        var sb = new StringBuilder();
        for (var i = 0; i < output.Count; i++)
        {
            sb.Append(output[i]);
            if (i < output.Count - 1 || endsWithNewline)
            {
                sb.Append(newline);
            }
        }

        return sb.ToString();
    }

    // Splits into logical lines, dropping a single trailing empty line produced by a final newline
    // (CRLF-safe). An empty input yields no lines.
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            return lines;
        }

        var normalized = text.Replace("\r\n", "\n");
        var split = normalized.Split('\n');
        var count = split.Length;
        // A trailing newline produces a final empty element; drop exactly that one.
        if (count > 0 && split[count - 1].Length == 0)
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            lines.Add(split[i]);
        }

        return lines;
    }
}
