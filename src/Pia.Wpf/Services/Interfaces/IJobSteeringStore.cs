namespace Pia.Services.Interfaces;

public enum JobPlanChangeKind
{
    Edit = 0,
    Insert = 1,
    Remove = 2,
    Move = 3,
}

/// <summary>One change a chat turn made to a running job's plan; an insert carries the id its row will get.</summary>
/// <param name="AfterStepId">Placement for an insert or a move; null with <paramref name="AtStart"/> false means the end.</param>
public sealed record JobPlanChange(
    JobPlanChangeKind Kind,
    Guid StepId,
    Guid? AfterStepId = null,
    bool AtStart = false,
    string? Title = null,
    string? Intent = null);

/// <summary>What a conversation turn asked of a chat job, held until the job's loop can act on it.</summary>
public interface IJobSteeringStore
{
    void QueuePlanChanges(Guid runId, IReadOnlyList<JobPlanChange> changes);

    IReadOnlyList<JobPlanChange> PeekPlanChanges(Guid runId);

    IReadOnlyList<JobPlanChange> TakePlanChanges(Guid runId);

    void MarkChanged(Guid runId, IEnumerable<Guid> stepIds);

    IReadOnlySet<Guid> ChangedSteps(Guid runId);

    void UnmarkChanged(Guid runId, Guid stepId);

    void AddNote(Guid runId, string note);

    IReadOnlyList<string> Notes(Guid runId);

    void RequestPauseAfterStep(Guid runId);

    bool IsPauseAfterStepRequested(Guid runId);

    bool TryConsumePauseAfterStep(Guid runId);

    void DropPauseAfterStep(Guid runId);

    /// <summary>Plan changes the loop could not apply because their step had started or vanished.</summary>
    void RecordDropped(Guid runId, int count);

    int TakeDropped(Guid runId);

    void Forget(Guid runId);

    event EventHandler<Guid>? Changed;
}
