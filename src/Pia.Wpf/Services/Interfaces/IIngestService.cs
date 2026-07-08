namespace Pia.Services.Interfaces;

/// <summary>Why an ingest produced no pages; <see cref="Success"/> when it did.</summary>
public enum IngestOutcome
{
    Success,
    SourceNotFound,
    NonTextSkipped,
    EmptySource,
    NoEntities,
}

/// <summary>
/// Outcome of an ingest run. <see cref="SourceRef"/> is the vault-relative source path that was
/// compiled; <see cref="TouchedPages"/> are the vault-relative <c>memory/topics/&lt;slug&gt;.md</c> pages
/// created or updated (empty when the source was skipped, e.g. a binary/non-text source).
/// </summary>
public record IngestResult(
    string SourceRef, IReadOnlyList<string> TouchedPages, IngestOutcome Outcome = IngestOutcome.Success);

/// <summary>
/// The ingest pipeline (Task 7.1): a fan-out compiler that reads a RAW source from <c>sources/</c>,
/// summarizes/extracts entities from it (via <see cref="IIngestExtractor"/>), and fans the entities out
/// into <c>memory/topics/</c> wiki pages — keeping the index, log and per-page provenance up to date.
/// Each source's facts live in a machine-managed <c>## Source: &lt;sourceRef&gt;</c> section per topic
/// page; re-ingesting the same source replaces exactly that section (never duplicates, never touches
/// manual content). Ingest runs inline; a background-job handle + progress UI is deferred.
/// </summary>
public interface IIngestService
{
    Task<IngestResult> IngestAsync(string sourceRelativePath, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Remove everything <paramref name="sourceRef"/> contributed: its <c>## Source:</c> section and
    /// its <c>sources:</c> frontmatter ref on every page in <paramref name="pages"/>; pages left with
    /// no sections and a whitespace-only preamble are deleted (with their index entry). Missing pages
    /// are skipped; pages without the section still get their frontmatter ref pruned (and the
    /// empty-page check). Appends one <c>ingest</c> journal line when any pages were targeted.
    /// </summary>
    Task RemoveContributionsAsync(string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default);
}
