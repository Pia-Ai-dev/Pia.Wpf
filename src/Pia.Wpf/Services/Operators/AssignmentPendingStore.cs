namespace Pia.Services.Operators;

/// <summary>The on-disk shape of <see cref="AssignmentPendingStore"/>.</summary>
public sealed record AssignmentPendingState
{
    public List<PendingAssignment> Pending { get; set; } = [];
}

/// <summary>
/// Every run this device started: the ones still to be collected, and — for a while afterwards — the ones that
/// were.
///
/// It is persisted for two reasons. The app closing mid-run must not lose the artifact, because the server
/// drops the plaintext within its own retention window whether or not anyone comes back for it. And nothing
/// else knows what the user actually asked or which local chat holds the answer: the prompt lives inside the
/// input the server dropped, and the list projection never carried it.
/// </summary>
public interface IAssignmentPendingStore
{
    /// <summary>The runs still OUTSTANDING — not yet stored locally and acknowledged. This is what the drain
    /// pass walks, so a collected run must not appear here.</summary>
    Task<IReadOnlyList<PendingAssignment>> GetAllAsync();

    /// <summary>Every run this device knows about, collected or not, newest first. What the job list joins the
    /// server's own rows to.</summary>
    Task<IReadOnlyList<PendingAssignment>> GetJournalAsync();

    Task AddAsync(PendingAssignment pending);

    /// <summary>
    /// Called once the artifact is committed locally AND acknowledged to the server — never between the two.
    /// The entry is KEPT rather than deleted, stamped with the time: it is the only thing that can still say
    /// which chat holds this run's answer once the server has dropped its copy.
    /// </summary>
    Task MarkCollectedAsync(Guid assignmentId);

    /// <summary>Forgets a run this device will never collect — the server no longer answers for it, so there is
    /// nothing left to store and nothing to link a chat to.</summary>
    Task RemoveAsync(Guid assignmentId);
}

/// <inheritdoc cref="IAssignmentPendingStore"/>
public sealed class AssignmentPendingStore
    : JsonPersistenceService<AssignmentPendingState>, IAssignmentPendingStore
{
    /// <summary>How long a collected run stays in the journal. Past this the server has deleted its row too, so
    /// the list has nothing to show it beside — and an unbounded local history of every background run a user
    /// ever started is not something to keep by accident.</summary>
    internal static readonly TimeSpan JournalRetention = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override string FileName => "pending-assignments.json";

    protected override AssignmentPendingState CreateDefault() => new();

    public async Task<IReadOnlyList<PendingAssignment>> GetAllAsync()
    {
        var state = await LoadAsync();
        return state.Pending.Where(p => p.CollectedAtUtc is null).ToList();
    }

    public async Task<IReadOnlyList<PendingAssignment>> GetJournalAsync()
    {
        var state = await LoadAsync();
        return state.Pending.OrderByDescending(p => p.StartedAtUtc).ToList();
    }

    public async Task AddAsync(PendingAssignment pending)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            state.Pending.RemoveAll(p => p.AssignmentId == pending.AssignmentId);
            state.Pending.Add(pending);
            Prune(state);
            await SaveAsync(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCollectedAsync(Guid assignmentId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            var index = state.Pending.FindIndex(p => p.AssignmentId == assignmentId);
            if (index < 0) return;

            state.Pending[index] = state.Pending[index] with { CollectedAtUtc = DateTime.UtcNow };
            Prune(state);
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

    /// <summary>Only COLLECTED entries age out. An outstanding one is dropped by the orchestrator's own
    /// give-up bound, which is a different question with a different answer.</summary>
    private static void Prune(AssignmentPendingState state) =>
        state.Pending.RemoveAll(p =>
            p.CollectedAtUtc is { } collected && collected < DateTime.UtcNow - JournalRetention);
}
