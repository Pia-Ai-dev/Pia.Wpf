namespace Pia.Services.Interfaces;

/// <summary>
/// Outcome of an ingest run. <see cref="SourceRef"/> is the vault-relative source path that was
/// compiled; <see cref="TouchedPages"/> are the vault-relative <c>memory/topics/&lt;slug&gt;.md</c> pages
/// created or updated (empty when the source was skipped, e.g. a binary/non-text source).
/// </summary>
public record IngestResult(string SourceRef, IReadOnlyList<string> TouchedPages);

/// <summary>
/// The ingest pipeline (Task 7.1): a fan-out compiler that reads a RAW source from <c>sources/</c>,
/// summarizes/extracts entities from it (via <see cref="IIngestExtractor"/>), and fans the entities out
/// into <c>memory/topics/</c> wiki pages through <see cref="IMemoryService.RememberAsync"/> — keeping
/// the index, log and per-page provenance up to date. Re-ingesting the same source must NOT create
/// duplicate topic pages/sections (dedup is delegated to the deterministic remember/upsert path).
/// Ingest runs inline; a background-job handle + progress UI is deferred.
/// </summary>
public interface IIngestService
{
    Task<IngestResult> IngestAsync(string sourceRelativePath, DateOnly date, CancellationToken ct = default);
}
