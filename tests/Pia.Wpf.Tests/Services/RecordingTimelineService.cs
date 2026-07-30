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
            _rows.Add(e with { Seq = ++_seq });
    }

    public Task<IReadOnlyList<AgentTimelineEvent>> GetForRunAsync(Guid runId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<AgentTimelineEvent>>([.. _rows.Where(r => r.RunId == runId).OrderBy(r => r.Seq)]);
    }

    public Task<int> PruneOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
}
