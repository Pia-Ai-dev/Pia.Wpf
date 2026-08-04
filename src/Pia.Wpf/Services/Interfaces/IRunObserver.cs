using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// A read-only BYSTANDER on the run timeline: it is handed every event the audit path accepted, and it can do
/// nothing else (T2-G1).
/// <para>
/// <b>Why it exists.</b> A future OTel span exporter, a file trace, a live diagnostics pane — each of those
/// wants the same event stream <c>AgentTimelineEvents</c> already carries. Without this seam each one would
/// grow its own capture point in the two gates, and the gates would drift. There is exactly ONE append
/// entrypoint in the system (<see cref="IAgentTimelineService.Emit"/>); this is a second consumer of it, not a
/// second producer.
/// </para>
/// <para>
/// <b>The contract, and why the shape enforces it.</b> The audit table is the system of record; an observer is
/// a bystander whose opinion nothing waits for. The method returns <c>void</c> and takes no
/// <c>CancellationToken</c> — an observer that cannot report failure and cannot be awaited cannot quietly
/// become a write path, and a caller that has nothing to await cannot accidentally couple the run to it.
/// <see cref="AgentTimelineEvent"/> is an already-complete immutable record: the observer receives the same
/// row the table stores, service-assigned <c>Seq</c> and <c>StepOrdinal</c> included, and can mutate nothing.
/// </para>
/// <para>
/// <b>What an implementation must not do.</b> Touch <c>SqliteContext</c>, open a connection, or persist to any
/// store this app owns — a second writer on the history file is precisely what this seam exists to avoid, and
/// there is no second connection to hand out. Block for long: notifications are serialized on one chain, so a
/// slow observer delays the OTHER observers (never the audit write, never the run). Throw for signal: the
/// exception is caught and logged as a type, and the stream carries on.
/// </para>
/// <para>
/// <b>Registration.</b> Zero registrations is the normal, supported state and costs nothing — MS.DI resolves
/// <c>IEnumerable&lt;IRunObserver&gt;</c> to an empty sequence and <c>AgentTimelineService</c> skips the
/// notification path entirely. A consumer adds itself ADDITIVELY
/// (<c>services.AddSingleton&lt;IRunObserver, X&gt;()</c>); never <c>TryAdd</c>, which would let one
/// registration silently exclude another.
/// </para>
/// </summary>
public interface IRunObserver
{
    /// <summary>
    /// Called once per event the audit path ACCEPTED, in <c>Seq</c> order, on a background thread that holds no
    /// lock.
    /// <para>
    /// "Accepted", not "durably written", is the honest word: the notification is queued at the same moment the
    /// INSERT is, so a row whose INSERT later fails was still observed. The alternative — notifying only after
    /// the write lands — would couple this seam to SQLite latency, which is the one thing it must not do.
    /// Events the audit path DROPPED (everything past the per-run cap) are correspondingly NOT observed, so
    /// the two consumers never disagree about what happened; the synthetic truncation marker IS observed,
    /// because it is a row.
    /// </para>
    /// <para>
    /// May be called re-entrantly-safe from any thread: calling back into <c>Emit</c> is permitted (the row is
    /// written and capped as usual) but will not itself produce a notification, which is what stops a
    /// self-feeding observer from generating an unbounded notification chain.
    /// </para>
    /// </summary>
    void OnTimelineEvent(AgentTimelineEvent e);
}
