using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pia.Models.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class SectionUpsertServiceTests
{
    private static VaultSection Section(string heading, string body)
    {
        var slug = heading.ToLowerInvariant().Replace(' ', '-');
        return new VaultSection(heading, slug, body, 0, body.Length);
    }

    private static VaultDocument Doc(params VaultSection[] sections)
        => new(
            new Dictionary<string, string> { ["id"] = "6f9c0b3e-7c1a-4f2e-9a8b-000000000001", ["type"] = "contact_list" },
            Preamble: string.Empty,
            Sections: sections,
            RawText: string.Empty);

    [Fact]
    public async Task Resolve_exact_heading_match_is_Edit_by_lexical_alone()
    {
        var doc = Doc(Section("John Smith", "- email: a@x\n"));
        var svc = new SectionUpsertService(new StubEmbeddingService());

        var result = await svc.ResolveAsync(doc, "John Smith", "- email: b@x\n");

        Assert.Equal(UpsertBand.Edit, result.Band);
        Assert.Equal("john-smith", result.MatchedSlug);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Resolve_close_subject_is_Edit_via_high_cosine()
    {
        var doc = Doc(Section("John Smith", "- email: a@x\n"));

        // "J Smith" vs "John Smith" is JaroWinkler ~0.78 (below the 0.85 Edit cut on lexical alone).
        // Pin the subject query "J Smith\n- email: b@x\n" and the section's
        // "John Smith\n- email: a@x\n" to the same basis vector -> cosine 1.0, so
        // Max(lexical, vector) >= 0.85 and the embedding path drives the Edit decision.
        var pins = new Dictionary<string, int>
        {
            ["J Smith\n- email: b@x\n"] = 3,
            ["John Smith\n- email: a@x\n"] = 3,
        };
        var svc = new SectionUpsertService(new StubEmbeddingService(pins));

        var result = await svc.ResolveAsync(doc, "J Smith", "- email: b@x\n");

        Assert.Equal(UpsertBand.Edit, result.Band);
        Assert.Equal("john-smith", result.MatchedSlug);
    }

    [Fact]
    public async Task Resolve_mid_score_is_Ambiguous_with_candidates()
    {
        // "Johnny" vs "John Smith" lands JaroWinkler at ~0.69 (in [0.60, 0.85)); vs "Jane Doe" at
        // ~0.53 (below 0.60). Embeddings are orthogonal (distinct unpinned text) so vector ~0 and
        // lexical drives the band -> only "john-smith" qualifies as a candidate.
        var doc = Doc(
            Section("John Smith", "- email: a@x\n"),
            Section("Jane Doe", "- email: c@x\n"));
        var svc = new SectionUpsertService(new StubEmbeddingService());

        var result = await svc.ResolveAsync(doc, "Johnny", "- email: z@x\n");

        Assert.Equal(UpsertBand.Ambiguous, result.Band);
        Assert.Null(result.MatchedSlug);
        Assert.Contains("john-smith", result.Candidates);
        // Candidates are ordered by score descending; the close one comes first.
        Assert.Equal("john-smith", result.Candidates[0]);
    }

    [Fact]
    public async Task Resolve_unrelated_subject_is_Create()
    {
        var doc = Doc(Section("John Smith", "- email: a@x\n"));
        var svc = new SectionUpsertService(new StubEmbeddingService());

        var result = await svc.ResolveAsync(doc, "Quarterly Budget Spreadsheet", "- owner: finance\n");

        Assert.Equal(UpsertBand.Create, result.Band);
        Assert.Null(result.MatchedSlug);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void MergeBullets_replaces_in_place_appends_new_and_keeps_order()
    {
        var svc = new SectionUpsertService(new StubEmbeddingService());

        var merged = svc.MergeBullets(
            existingBody: "- email: a@x\n- name: J\n",
            newBody: "- email: b@x\n- phone: 5\n");

        var lines = merged.Split('\n').Where(l => l.Length > 0).ToArray();
        Assert.Equal(new[] { "- email: b@x", "- name: J", "- phone: 5" }, lines);
    }

    [Fact]
    public void MergeBullets_preserves_non_bullet_new_body_lines()
    {
        var svc = new SectionUpsertService(new StubEmbeddingService());

        // New body carries content that is NOT a top-level "- key: value" bullet: a nested child line,
        // a scalar-array item, and a fenced block line. None may be dropped (lossless merge).
        var merged = svc.MergeBullets(
            existingBody: "- email: a@x\n",
            newBody: "- name: John\n  - city: NYC\n- vip\n```json\n");

        // The top-level bullet still merges in place / appends.
        Assert.Contains("- email: a@x", merged);
        Assert.Contains("- name: John", merged);

        // Non-bullet new-body lines are preserved (appended), not discarded.
        Assert.Contains("  - city: NYC", merged);
        Assert.Contains("- vip", merged);
        Assert.Contains("```json", merged);
    }

    [Fact]
    public void MergeBullets_preserves_trailing_prose()
    {
        var svc = new SectionUpsertService(new StubEmbeddingService());

        var merged = svc.MergeBullets(
            existingBody: "- email: a@x\n- name: J\n\nMet at the Q2 offsite.\n",
            newBody: "- email: b@x\n");

        Assert.Contains("- email: b@x", merged);
        Assert.Contains("- name: J", merged);
        Assert.Contains("Met at the Q2 offsite.", merged);
        // Prose stays after the bullets.
        Assert.True(merged.IndexOf("Met at the Q2 offsite.", StringComparison.Ordinal)
            > merged.IndexOf("- name: J", StringComparison.Ordinal));
    }
}
