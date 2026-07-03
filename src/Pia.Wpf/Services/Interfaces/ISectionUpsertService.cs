using Pia.Models.Vault;

namespace Pia.Services.Interfaces;

/// <summary>
/// Result band for an upsert resolution (vault write path, design §3).
/// </summary>
public enum UpsertBand
{
    /// <summary>A single existing section matched confidently; merge into it (<see cref="UpsertResolution.MatchedSlug"/>).</summary>
    Edit,

    /// <summary>One or more sections matched, but none confidently. Defer to the user/model; see <see cref="UpsertResolution.Candidates"/>.</summary>
    Ambiguous,

    /// <summary>No section matched; create a new one.</summary>
    Create,
}

/// <summary>
/// Outcome of <see cref="ISectionUpsertService.ResolveAsync"/>. <see cref="MatchedSlug"/> is set only
/// in the <see cref="UpsertBand.Edit"/> band; <see cref="Candidates"/> (slugs, score-descending) is
/// populated only in the <see cref="UpsertBand.Ambiguous"/> band.
/// </summary>
public sealed record UpsertResolution(UpsertBand Band, string? MatchedSlug, IReadOnlyList<string> Candidates);

/// <summary>
/// Resolves where a structured record (subject + body) should land within a vault document and
/// performs deterministic field-level (bullet) body merges per format spec §4.
/// </summary>
public interface ISectionUpsertService
{
    /// <summary>
    /// Classifies <paramref name="subject"/>/<paramref name="content"/> against the sections of
    /// <paramref name="doc"/> into an <see cref="UpsertBand"/> using max(lexical, vector) similarity.
    /// </summary>
    Task<UpsertResolution> ResolveAsync(VaultDocument doc, string subject, string content);

    /// <summary>
    /// Deterministic field-level merge (spec §4): treats <c>- key: value</c> bullets as an ordered map.
    /// Existing keys are replaced in place; new keys are appended after the last existing bullet;
    /// trailing prose is preserved.
    /// </summary>
    string MergeBullets(string existingBody, string newBody);
}
