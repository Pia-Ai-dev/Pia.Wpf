namespace Pia.Services.Interfaces;

/// <summary>
/// The single serial pipeline for ALL ingest work — the sources watcher, the startup reconcile,
/// and the chat <c>ingest</c> tool. One ingest is in flight at any time (each costs two LLM calls
/// and splices topic pages). <see cref="RunAsync"/> is the manual path: it always executes, even
/// when the content hash is unchanged. Automatic triggers hash-skip internally.
/// </summary>
public interface IIngestScheduler
{
    /// <summary>Queue an ingest of <paramref name="sourceRef"/> and await its result.</summary>
    Task<IngestResult> RunAsync(string sourceRef, CancellationToken ct = default);

    /// <summary>Queue removal of everything <paramref name="sourceRef"/> contributed.</summary>
    Task RemoveAsync(string sourceRef, CancellationToken ct = default);

    /// <summary>Raised after each completed ingest or removal (any outcome). May fire on any thread.</summary>
    event EventHandler? IngestCompleted;
}
