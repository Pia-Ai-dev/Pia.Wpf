using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>
/// In-memory <see cref="IAgentTimelineService"/> for the gate suites; the real store's <c>RunId</c> foreign key
/// would silently drop rows emitted against a run that has no <c>AgentRuns</c> row.
/// </summary>
internal sealed class RecordingTimelineService : IAgentTimelineService
{
    private readonly object _gate = new();
    private readonly List<AgentTimelineEvent> _rows = [];
    private long _seq;

    // Mirrors the real service's per-step StepOrdinal allocator; leaving the column null would read as a gate bug.
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
