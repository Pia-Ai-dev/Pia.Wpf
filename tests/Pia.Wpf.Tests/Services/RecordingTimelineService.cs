using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>
/// In-memory <see cref="IAgentTimelineService"/> for the gate suites.
/// <para>
/// The gate tests deliberately do NOT use the real store: <c>AgentTimelineEvents.RunId</c> has an enforced
/// foreign key, so emitting against a run id with no <c>AgentRuns</c> row would have its INSERT rejected,
/// logged as a warning and dropped — a silently green "zero rows" test and a baffling red "N rows" one. This
/// fake records what the gate asked for, which is exactly what those facts are about.
/// </para>
/// </summary>
internal sealed class RecordingTimelineService : IAgentTimelineService
{
    private readonly object _gate = new();
    private readonly List<AgentTimelineEvent> _rows = [];
    private long _seq;

    /// <summary>
    /// <c>StepOrdinal</c>'s allocator, mirroring <c>AgentTimelineService</c>'s: per STEP, and no entry (so no
    /// ordinal) for a run-level row. Present because the real service assigns this column, not the gate — a
    /// fake that left it null would make every gate assertion about it read null and look like a gate bug.
    /// </summary>
    private readonly Dictionary<Guid, long> _stepSeq = [];

    /// <summary>Drives the failure-isolation fact: a broken bookkeeping store must not fail a step.</summary>
    public bool ThrowOnEmit { get; set; }

    public IReadOnlyList<AgentTimelineEvent> Rows
    {
        get { lock (_gate) return [.. _rows]; }
    }

    public void Emit(AgentTimelineEvent e)
    {
        if (ThrowOnEmit)
            throw new InvalidOperationException("the timeline store is broken");

        lock (_gate)
        {
            long? stepOrdinal = null;
            if (e.StepId is { } stepId && e.Kind == AgentTimelineEventKind.ToolCall)
            {
                _stepSeq.TryGetValue(stepId, out var last);
                stepOrdinal = last + 1;
                _stepSeq[stepId] = stepOrdinal.Value;
            }

            _rows.Add(e with { Seq = ++_seq, StepOrdinal = stepOrdinal });
        }
    }

    public Task<IReadOnlyList<AgentTimelineEvent>> GetForRunAsync(Guid runId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<AgentTimelineEvent>>([.. _rows.Where(r => r.RunId == runId).OrderBy(r => r.Seq)]);
    }

    public Task<int> PruneOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
}
