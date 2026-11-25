using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed partial class RunProgressViewModel
{
    private Action? _detachJobSteering;
    private Func<Guid, (IReadOnlyList<JobPlanChange> Queued, IReadOnlySet<Guid> Changed)>? _readJobOverlay;

    /// <summary>Plan changes from the chat that the job applies before its next step; null when none wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueuedChangesLine))]
    private string? _queuedChangesLine;

    public bool HasQueuedChangesLine => QueuedChangesLine is not null;

    internal void AttachJobSteering(IJobSteeringStore? store)
    {
        if (store is null || _detachJobSteering is not null)
            return;
        store.Changed += OnJobSteeringChanged;
        _detachJobSteering = () => store.Changed -= OnJobSteeringChanged;
        _readJobOverlay = id => (store.PeekPlanChanges(id), store.ChangedSteps(id));
        ApplyJobOverlay();
    }

    private void DetachJobSteering()
    {
        _detachJobSteering?.Invoke();
    }

    private void OnJobSteeringChanged(object? sender, Guid runId)
    {
        if (runId == _runId)
            _uiContext.Post(_ => ApplyJobOverlay(), null);
    }

    internal void ApplyJobOverlay()
    {
        if (_readJobOverlay is not { } read || _disposed)
            return;

        var (queued, changed) = read(_runId);
        var marked = changed.ToHashSet();
        marked.UnionWith(queued.Select(c => c.StepId));
        foreach (var row in Steps)
            row.IsChangedFromChat = row.Status == AgentStepStatus.Pending && marked.Contains(row.StepId);

        QueuedChangesLine = queued.Count == 0 ? null : _localization.Format("JobTools_QueuedChanges", queued.Count);
    }
}
