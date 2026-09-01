namespace Pia.Services.Interfaces;

/// <summary>Why an ingest produced no pages; <see cref="Success"/> when it did.</summary>
public enum IngestOutcome
{
    Success,
    SourceNotFound,
    NonTextSkipped,
    EmptySource,
    NoEntities,

    /// <summary>Notable topics WERE discovered, but at least one page's synthesis came back empty
    /// (provider died mid-run / model error). Treated as transient like <see cref="SourceNotFound"/>:
    /// the caller records nothing and retries on the next change/reconcile. Pages that DID synthesize
    /// are already written; the retry re-synthesizes them idempotently.</summary>
    SynthesisFailed,
}

/// <summary>
/// Outcome of an ingest run. <see cref="SourceRef"/> is the vault-relative source path that was
/// compiled; <see cref="TouchedPages"/> are the vault-relative <c>memory/topics/&lt;slug&gt;.md</c> pages
/// created or updated (empty when the source was skipped, e.g. a binary/non-text source).
/// </summary>
public record IngestResult(
    string SourceRef, IReadOnlyList<string> TouchedPages, IngestOutcome Outcome = IngestOutcome.Success);

/// <summary>
/// The ingest pipeline: a topic-driven synthesis compiler that reads a RAW source from <c>sources/</c>,
/// discovers the notable topics in it (via <see cref="IIngestExtractor"/>) grounded in the vault charter,
/// and for each topic re-synthesizes a single <c>memory/topics/&lt;slug&gt;.md</c> wiki page across ALL
/// sources that mention it (via <see cref="IIngestSynthesizer"/>) — keeping the index, log and per-page
/// <c>sources:</c> provenance up to date. A manual preamble above the <c>&lt;!-- pia:managed --&gt;</c>
/// sentinel is preserved verbatim across re-synthesis; page identity (<c>id</c>/<c>created</c>) is stable.
/// Ingest runs inline; a background-job handle + progress UI is deferred.
/// </summary>
public interface IIngestService
{
    Task<IngestResult> IngestAsync(string sourceRelativePath, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Remove <paramref name="sourceRef"/> from every page in <paramref name="pages"/>. The
    /// <c>sources:</c> frontmatter ref is pruned deterministically (no LLM). A page left with no
    /// remaining sources is deleted together with its index entry. A page that still has other sources
    /// is kept and its body is re-synthesized best-effort from those remaining sources; if synthesis
    /// produces nothing (no provider / model error) the old body is kept (stale — it self-heals on the
    /// next ingest of any remaining source). Missing pages are skipped. Appends one <c>ingest</c>
    /// journal line when any pages were targeted, noting the stale-page count when applicable.
    /// </summary>
    Task RemoveContributionsAsync(string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default);

    /// <summary>
    /// Re-synthesize one existing topic page from the sources it already records — how a changed
    /// <c>memory/templates.md</c> or <c>memory/charter.md</c> reaches a page that is already on disk.
    /// The manual preamble above the sentinel survives, as does page identity. Returns <c>false</c> and
    /// leaves the page byte-identical when it does not exist, records no sources, or synthesis comes back
    /// empty (no provider / model error).
    /// </summary>
    Task<bool> RebuildPageAsync(string pagePath, CancellationToken ct = default);

    /// <summary>Every topic page currently on disk, vault-relative, for a bulk rebuild.</summary>
    Task<IReadOnlyList<string>> ListTopicPagesAsync();

    /// <summary>
    /// Fold <paramref name="loserPath"/> into <paramref name="keeperPath"/>: union their <c>sources:</c>,
    /// archive and delete the loser, drop its index entry, retarget every wikilink that pointed at it, and
    /// re-synthesize the keeper over the widened union. Carrying the provenance is the part that makes the
    /// merge stick — without it the next ingest of the loser's sources simply mints the page again.
    /// Returns false when either page is missing or the two are the same page.
    /// </summary>
    Task<bool> MergeTopicPagesAsync(string keeperPath, string loserPath, CancellationToken ct = default);
}
