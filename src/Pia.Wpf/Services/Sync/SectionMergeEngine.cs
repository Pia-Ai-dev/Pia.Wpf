using System.Globalization;
using System.Text;
using Pia.Infrastructure.Vault;
using Pia.Models.Vault;
using Pia.Services.Interfaces;

namespace Pia.Services.Sync;

/// <summary>
/// Section-aware 3-way merge for Pia-managed vault documents, implementing the normative oracle in
/// memory-vault format spec §10.1 (per-section decision procedure), §10.2 (reassembly) and §10.3
/// (conflict-marker bytes). The merge is keyed by section slug over the ordered union of base, local
/// and remote slugs. Frontmatter and preamble are taken from whichever side has the newer
/// <c>updated</c> timestamp (tie → local).
/// </summary>
public sealed class SectionMergeEngine
{
    private const string LocalMarker = "<<<<<<< local\n";
    private const string SeparatorMarker = "=======\n";
    private const string RemoteMarker = ">>>>>>> remote\n";

    private readonly MarkdownVaultParser _parser;

    public SectionMergeEngine(MarkdownVaultParser parser)
    {
        _parser = parser;
    }

    /// <summary>
    /// Merge <paramref name="local"/> and <paramref name="remote"/> against their shared
    /// <paramref name="base"/> per spec §10. Returns the reassembled file text and the slugs that
    /// could not auto-resolve (conflict markers are embedded in those sections' bodies, §10.3).
    /// </summary>
    public MergeResult Merge(VaultDocument @base, VaultDocument local, VaultDocument remote)
    {
        // Index each side's sections by slug for O(1) lookup; first occurrence wins (parser already
        // dedupes slugs so collisions cannot happen within one document).
        var baseBySlug = IndexBySlug(@base);
        var localBySlug = IndexBySlug(local);
        var remoteBySlug = IndexBySlug(remote);

        // Ordered union: base order first, then local-new (local order), then remote-new (remote order).
        var orderedSlugs = OrderedUnion(@base, local, remote);

        var conflictedSlugs = new List<string>();
        var mergedSections = new List<(string Heading, string Body)>();

        foreach (var slug in orderedSlugs)
        {
            var b = baseBySlug.TryGetValue(slug, out var bs) ? bs : null;
            var l = localBySlug.TryGetValue(slug, out var ls) ? ls : null;
            var r = remoteBySlug.TryGetValue(slug, out var rs) ? rs : null;

            var lBody = l?.Body; // null == absent on this side
            var rBody = r?.Body;
            var bBody = b?.Body;

            // changed(side) := side differs from base in presence OR content.
            var localChanged = !BodyEquals(lBody, bBody);
            var remoteChanged = !BodyEquals(rBody, bBody);

            // §10.1 — FIRST matching rule wins.
            // Rule 1: L == R (both absent, or both present and equal).
            if (BodyEquals(lBody, rBody))
            {
                if (lBody is not null)
                {
                    mergedSections.Add((HeadingFor(l, r, b), lBody));
                }
                // both absent -> drop.
                continue;
            }

            // Rule 2: !changed(local) -> take remote.
            if (!localChanged)
            {
                if (rBody is not null)
                {
                    mergedSections.Add((HeadingFor(r, l, b), rBody));
                }
                // remote deleted an unchanged section -> drop.
                continue;
            }

            // Rule 3: !changed(remote) -> take local.
            if (!remoteChanged)
            {
                if (lBody is not null)
                {
                    mergedSections.Add((HeadingFor(l, r, b), lBody));
                }
                // local deleted an unchanged section -> drop.
                continue;
            }

            // Rule 4: both changed.
            // 4a: exactly one side absent (edit-vs-delete) -> keep the edited (present) side + flag.
            if (lBody is null || rBody is null)
            {
                var present = lBody ?? rBody!;
                var presentSection = lBody is not null ? l : r;
                mergedSections.Add((HeadingFor(presentSection, l ?? r, b), present));
                conflictedSlugs.Add(slug);
                continue;
            }

            // 4b: both present, L != R (concurrent edits) -> conflict marker + flag.
            mergedSections.Add((HeadingFor(l, r, b), ConflictMarker(lBody, rBody)));
            conflictedSlugs.Add(slug);
        }

        var text = Reassemble(local, remote, mergedSections);
        return new MergeResult(text, conflictedSlugs);
    }

    /// <summary>
    /// §10.3 conflict-marker byte layout, exact:
    /// <c>"&lt;&lt;&lt;&lt;&lt;&lt;&lt; local\n" + nl(L) + "=======\n" + nl(R) + "&gt;&gt;&gt;&gt;&gt;&gt;&gt; remote\n"</c>
    /// where <c>nl(s)</c> = <c>s</c> if empty or already ending with <c>\n</c>, else <c>s + "\n"</c>.
    /// </summary>
    private static string ConflictMarker(string localBody, string remoteBody)
    {
        return LocalMarker + Nl(localBody) + SeparatorMarker + Nl(remoteBody) + RemoteMarker;
    }

    private static string Nl(string s) =>
        s.Length == 0 || s.EndsWith('\n') ? s : s + "\n";

    /// <summary>
    /// §10.2 reassembly: frontmatter + preamble come from the side with the newer <c>updated</c>
    /// timestamp; on a tie, local wins. Section bodies are emitted in ordered-union order as
    /// <c>"## " + Heading + "\n" + body</c>, preserving the supplying side's heading text.
    /// </summary>
    private string Reassemble(
        VaultDocument local, VaultDocument remote, IReadOnlyList<(string Heading, string Body)> sections)
    {
        var winner = SelectFrontmatterSource(local, remote);

        var sb = new StringBuilder();
        sb.Append(FrontmatterAndPreamble(winner));
        foreach (var (heading, body) in sections)
        {
            sb.Append("## ").Append(heading).Append('\n').Append(body);
        }

        return sb.ToString();
    }

    /// <summary>Newer <c>updated</c> wins; tie -> local (spec §10.2, parsed not hardcoded).</summary>
    private static VaultDocument SelectFrontmatterSource(VaultDocument local, VaultDocument remote)
    {
        var localUpdated = ParseUpdated(local);
        var remoteUpdated = ParseUpdated(remote);
        return remoteUpdated > localUpdated ? remote : local;
    }

    private static DateTimeOffset ParseUpdated(VaultDocument doc)
    {
        if (doc.Frontmatter.TryGetValue("updated", out var raw) &&
            DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// The exact frontmatter block + preamble of <paramref name="doc"/>, taken byte-for-byte from its
    /// <see cref="VaultDocument.RawText"/> up to the first section boundary (or EOF if none).
    /// </summary>
    private static string FrontmatterAndPreamble(VaultDocument doc)
    {
        // The frontmatter block ends at, and the preamble runs up to, the first section's heading
        // line. Everything before the first section's BodyStart minus its heading line is exactly the
        // frontmatter + preamble; when there are no sections, the whole RawText is frontmatter+preamble.
        if (doc.Sections.Count == 0)
        {
            return doc.RawText;
        }

        // BodyStart is just after the first section's heading-line terminator; walk back over the
        // heading line itself to recover the "## Heading\n" boundary the parser stripped.
        var first = doc.Sections[0];
        var headingLineStart = doc.RawText.LastIndexOf("## ", first.BodyStart - 1, StringComparison.Ordinal);
        return headingLineStart >= 0 ? doc.RawText[..headingLineStart] : doc.RawText[..first.BodyStart];
    }

    /// <summary>Prefer the supplying side's heading; fall back through the alternate side and base.</summary>
    private static string HeadingFor(VaultSection? primary, VaultSection? alternate, VaultSection? fallback)
    {
        return (primary ?? alternate ?? fallback)!.Heading;
    }

    private static bool BodyEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal);

    private static Dictionary<string, VaultSection> IndexBySlug(VaultDocument doc)
    {
        var map = new Dictionary<string, VaultSection>(StringComparer.Ordinal);
        foreach (var section in doc.Sections)
        {
            map.TryAdd(section.Slug, section);
        }

        return map;
    }

    private static List<string> OrderedUnion(VaultDocument @base, VaultDocument local, VaultDocument remote)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        void Append(VaultDocument doc)
        {
            foreach (var section in doc.Sections)
            {
                if (seen.Add(section.Slug))
                {
                    ordered.Add(section.Slug);
                }
            }
        }

        Append(@base);
        Append(local);
        Append(remote);
        return ordered;
    }
}
