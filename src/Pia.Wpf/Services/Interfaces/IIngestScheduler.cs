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

    /// <summary>
    /// The source ref currently being compiled, or <c>null</c> when the queue is idle. The scheduler
    /// owns this truth so a view opened mid-ingest (e.g. during the startup reconcile) can show the
    /// running indicator without having observed the <see cref="IngestStarted"/> event. Set before the
    /// real ingest work begins and cleared when it ends. Read from any thread.
    /// </summary>
    string? CurrentSourceRef { get; }

    /// <summary>Raised when real ingest work begins for a source (payload = the source ref); paired with
    /// <see cref="IngestCompleted"/>. Skipped for hash-gated no-ops. May fire on any thread.</summary>
    event EventHandler<string>? IngestStarted;

    /// <summary>Raised after each completed ingest or removal (any outcome). May fire on any thread.</summary>
    event EventHandler? IngestCompleted;
}
