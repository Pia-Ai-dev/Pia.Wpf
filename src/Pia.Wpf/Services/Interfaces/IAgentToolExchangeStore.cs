using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Payload-bearing per-run store, never on the sync wire, never throwing into a step. Call/Result rows are what
/// the MODEL saw (tokenized when on); ParkedCall/WithheldCall what the GATE saw, detokenized and replayable.
/// </summary>
public interface IAgentToolExchangeStore
{
    /// <summary>
    /// Persist one tool round's new messages. All-or-nothing: past the per-run bound the whole batch is rolled
    /// back, so a call is never stored without its result.
    /// </summary>
    Task RecordAsync(Guid runId, Guid? stepId, int round, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>Anchor this step's unanchored rows to the chat message they precede on the re-seed.</summary>
    Task<int> SealStepAsync(Guid runId, Guid? stepId, Guid anchorMessageId, CancellationToken ct = default);

    /// <summary>
    /// The run's Call/Result rows in <c>Seq</c> order. Returned verbatim, NOT detokenized: these rows already
    /// are what the model saw, so re-seeding them reproduces the in-process path.
    /// </summary>
    Task<IReadOnlyList<AgentToolExchangeRow>> ReadCarriedAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Drop every row of a run. Called when the run settles, so detokenized rows do not outlive it.</summary>
    Task<int> PurgeRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Retention sweep: rows older than <paramref name="cutoff"/> by their own <c>CreatedAt</c>, plus every row
    /// of an already-terminal run — the second clause is what closes the leak when a process dies before the
    /// run's own purge.
    /// </summary>
    Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>
    /// Append the gate's detokenized park record. Exempt from the per-run bound: this is the row a human's
    /// Continue press replays, so dropping it would disable an approval they just gave.
    /// </summary>
    Task AppendParkedAsync(IReadOnlyList<AgentToolExchangeRow> rows, CancellationToken ct = default);

    /// <summary>The tool's parked/withheld rows still awaiting a replay, oldest first.</summary>
    Task<IReadOnlyList<AgentToolExchangeRow>> GetReplayableAsync(Guid runId, string toolName, CancellationToken ct = default);

    /// <summary>
    /// Mark the named tools' unreplayed rows stale. Run ONCE per persist pass, not per row, or four parked
    /// calls of one tool in a single round supersede each other.
    /// </summary>
    Task<int> SupersedeUnreplayedAsync(Guid runId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default);

    /// <summary>
    /// Claim a row for replay. The UPDATE is conditional and the rows-affected result is the answer, which is
    /// the structural half of at-most-once execution.
    /// </summary>
    Task<bool> TryMarkReplayedAsync(Guid id, DateTime replayedAt, CancellationToken ct = default);

    /// <summary>Attach the replay's own result to the row it was replayed from.</summary>
    Task SetResultAsync(Guid id, string? resultText, CancellationToken ct = default);

    /// <summary>
    /// Drop the declined tool's replayable rows. Scoped to that tool alone — a run-wide delete would destroy
    /// another tool's surviving withheld call and the context seed the resume needs.
    /// </summary>
    Task<int> DeleteReplayableAsync(Guid runId, string toolName, CancellationToken ct = default);

    /// <summary>The newest parked call for that tool still awaiting a decision, for the approval surfaces.</summary>
    Task<AgentToolExchangeRow?> GetParkedCallAsync(Guid runId, string toolName, CancellationToken ct = default);
}

/// <summary>
/// The per-step sink handed to the executor: an immutable <c>(store, runId, stepId)</c> triple, so the write
/// site does not carry the ids. Mirrors <c>AgentTimelineScope</c>; a null scope means record nothing.
/// </summary>
public sealed class AgentToolExchangeScope
{
    private readonly IAgentToolExchangeStore _store;

    public AgentToolExchangeScope(IAgentToolExchangeStore store, Guid runId, Guid? stepId)
    {
        _store = store;
        RunId = runId;
        StepId = stepId;
    }

    public Guid RunId { get; }

    /// <summary>The step this turn belongs to, or null for a run-level turn.</summary>
    public Guid? StepId { get; }

    public Task RecordAsync(int round, IReadOnlyList<ChatMessage> messages) =>
        _store.RecordAsync(RunId, StepId, round, messages);

    public Task SealAsync(Guid anchorMessageId) =>
        _store.SealStepAsync(RunId, StepId, anchorMessageId);
}
