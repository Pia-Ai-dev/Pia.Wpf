namespace Pia.Services.Operators;

/// <summary>The on-disk shape of <see cref="AssignmentPendingStore"/>.</summary>
public sealed record AssignmentPendingState
{
    public List<PendingAssignment> Pending { get; set; } = [];
}

/// <summary>
/// The runs this device started and has not finished collecting yet.
///
/// It is persisted rather than held in memory for one reason: the app closing mid-run must not lose the
/// artifact. The server drops the plaintext within its own retention window whether or not anyone comes back
/// for it, so a run nobody remembers is a run whose result is gone.
/// </summary>
public interface IAssignmentPendingStore
{
    Task<IReadOnlyList<PendingAssignment>> GetAllAsync();

    Task AddAsync(PendingAssignment pending);

    /// <summary>Called once the artifact is committed locally AND acknowledged to the server — never
    /// between the two.</summary>
    Task RemoveAsync(Guid assignmentId);
}

/// <inheritdoc cref="IAssignmentPendingStore"/>
public sealed class AssignmentPendingStore
    : JsonPersistenceService<AssignmentPendingState>, IAssignmentPendingStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override string FileName => "pending-assignments.json";

    protected override AssignmentPendingState CreateDefault() => new();

    public async Task<IReadOnlyList<PendingAssignment>> GetAllAsync()
    {
        var state = await LoadAsync();
        return state.Pending.ToList();
    }

    public async Task AddAsync(PendingAssignment pending)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            state.Pending.RemoveAll(p => p.AssignmentId == pending.AssignmentId);
            state.Pending.Add(pending);
            await SaveAsync(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid assignmentId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            if (state.Pending.RemoveAll(p => p.AssignmentId == assignmentId) == 0) return;
            await SaveAsync(state);
        }
        finally
        {
            _gate.Release();
        }
    }
}
