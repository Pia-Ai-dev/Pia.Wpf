using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>In memory only: a queued change, note or mark does not survive an app restart.</summary>
public sealed class JobSteeringStore : IJobSteeringStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<JobPlanChange>> _changes = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _marked = new();
    private readonly Dictionary<Guid, List<string>> _notes = new();
    private readonly Dictionary<Guid, int> _dropped = new();
    private readonly HashSet<Guid> _pauseAfterStep = new();

    public event EventHandler<Guid>? Changed;

    public void QueuePlanChanges(Guid runId, IReadOnlyList<JobPlanChange> changes)
    {
        if (changes.Count == 0)
            return;
        lock (_gate)
        {
            if (!_changes.TryGetValue(runId, out var list))
                _changes[runId] = list = new List<JobPlanChange>();
            list.AddRange(changes);
        }
        Changed?.Invoke(this, runId);
    }

    public IReadOnlyList<JobPlanChange> PeekPlanChanges(Guid runId)
    {
        lock (_gate)
            return _changes.TryGetValue(runId, out var list) ? list.ToList() : [];
    }

    public IReadOnlyList<JobPlanChange> TakePlanChanges(Guid runId)
    {
        List<JobPlanChange>? taken;
        lock (_gate)
            _changes.Remove(runId, out taken);
        if (taken is { Count: > 0 })
            Changed?.Invoke(this, runId);
        return taken ?? [];
    }

    public void MarkChanged(Guid runId, IEnumerable<Guid> stepIds)
    {
        lock (_gate)
        {
            if (!_marked.TryGetValue(runId, out var set))
                _marked[runId] = set = new HashSet<Guid>();
            set.UnionWith(stepIds);
        }
        Changed?.Invoke(this, runId);
    }

    public IReadOnlySet<Guid> ChangedSteps(Guid runId)
    {
        lock (_gate)
            return _marked.TryGetValue(runId, out var set) ? set.ToHashSet() : new HashSet<Guid>();
    }

    public void UnmarkChanged(Guid runId, Guid stepId)
    {
        bool removed;
        lock (_gate)
            removed = _marked.TryGetValue(runId, out var set) && set.Remove(stepId);
        if (removed)
            Changed?.Invoke(this, runId);
    }

    public void AddNote(Guid runId, string note)
    {
        lock (_gate)
        {
            if (!_notes.TryGetValue(runId, out var list))
                _notes[runId] = list = new List<string>();
            list.Add(note);
        }
        Changed?.Invoke(this, runId);
    }

    public IReadOnlyList<string> Notes(Guid runId)
    {
        lock (_gate)
            return _notes.TryGetValue(runId, out var list) ? list.ToList() : [];
    }

    public void RequestPauseAfterStep(Guid runId)
    {
        lock (_gate)
            _pauseAfterStep.Add(runId);
        Changed?.Invoke(this, runId);
    }

    public bool IsPauseAfterStepRequested(Guid runId)
    {
        lock (_gate)
            return _pauseAfterStep.Contains(runId);
    }

    public bool TryConsumePauseAfterStep(Guid runId)
    {
        lock (_gate)
            return _pauseAfterStep.Remove(runId);
    }

    public void DropPauseAfterStep(Guid runId)
    {
        lock (_gate)
            _pauseAfterStep.Remove(runId);
    }

    public void RecordDropped(Guid runId, int count)
    {
        if (count <= 0)
            return;
        lock (_gate)
            _dropped[runId] = _dropped.GetValueOrDefault(runId) + count;
    }

    public int TakeDropped(Guid runId)
    {
        lock (_gate)
            return _dropped.Remove(runId, out var count) ? count : 0;
    }

    public void Forget(Guid runId)
    {
        lock (_gate)
        {
            _changes.Remove(runId);
            _marked.Remove(runId);
            _notes.Remove(runId);
            _dropped.Remove(runId);
            _pauseAfterStep.Remove(runId);
        }
        Changed?.Invoke(this, runId);
    }
}
