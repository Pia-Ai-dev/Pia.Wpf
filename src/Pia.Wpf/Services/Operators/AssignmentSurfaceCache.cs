using Microsoft.Extensions.Logging;
using Pia.Shared.Operators;

namespace Pia.Services.Operators;

/// <summary>One shared read of the surface: <see cref="IAssignmentApiClient"/> caches nothing, and neither
/// caller — a constructor-time availability check, per-keystroke autocomplete — can afford a live probe.</summary>
public interface IAssignmentSurfaceCache
{
    /// <summary>Last known surface; <see cref="AssignmentSurface.Hidden"/> until the first refresh.</summary>
    AssignmentSurface Surface { get; }

    /// <summary>Raised when <see cref="Surface"/> flips between hidden and available, not on every refresh.</summary>
    event EventHandler? Changed;

    Task<AssignmentSurface> RefreshAsync(CancellationToken ct = default);

    /// <summary>Ordinal match against the surface's skills; null when the surface is hidden or the name is
    /// unknown.</summary>
    AssignmentSkill? FindSkill(string skillName);

    /// <summary>The run list behind a short TTL, so a per-keystroke caller does not become a per-keystroke HTTP
    /// request. Null propagates: the server could not answer.</summary>
    Task<IReadOnlyList<AssignmentDto>?> GetRunsAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IAssignmentSurfaceCache"/>
public sealed class AssignmentSurfaceCache : IAssignmentSurfaceCache
{
    internal static readonly TimeSpan RunsTtl = TimeSpan.FromSeconds(15);

    private const int RunPageSize = 50;

    private readonly IAssignmentApiClient _api;
    private readonly TimeProvider _time;
    private readonly ILogger<AssignmentSurfaceCache> _logger;
    private readonly SemaphoreSlim _runsGate = new(1, 1);

    private volatile AssignmentSurface _surface = AssignmentSurface.Hidden;
    private IReadOnlyList<AssignmentDto>? _runs;
    private DateTimeOffset _runsReadAt;

    public AssignmentSurfaceCache(
        IAssignmentApiClient api,
        TimeProvider time,
        ILogger<AssignmentSurfaceCache> logger)
    {
        _api = api;
        _time = time;
        _logger = logger;
    }

    public AssignmentSurface Surface => _surface;

    public event EventHandler? Changed;

    public async Task<AssignmentSurface> RefreshAsync(CancellationToken ct = default)
    {
        AssignmentSurface next;
        try
        {
            next = await _api.GetSurfaceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read the background-assignment surface; keeping it hidden.");
            next = AssignmentSurface.Hidden;
        }

        var wasAvailable = _surface.Available;
        _surface = next;

        if (wasAvailable != next.Available)
        {
            _logger.LogInformation("Background-assignment surface is now {State} with {SkillCount} skill(s)",
                next.Available ? "available" : "hidden", next.Skills.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return next;
    }

    public AssignmentSkill? FindSkill(string skillName)
    {
        var surface = _surface;
        if (!surface.Available || string.IsNullOrWhiteSpace(skillName)) return null;

        return surface.Skills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<AssignmentDto>?> GetRunsAsync(CancellationToken ct = default)
    {
        await _runsGate.WaitAsync(ct);
        try
        {
            if (_runs is not null && _time.GetUtcNow() - _runsReadAt < RunsTtl) return _runs;

            IReadOnlyList<AssignmentDto>? rows;
            try
            {
                rows = await _api.ListAsync(0, RunPageSize, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(ex, "Could not read the background-assignment run list.");
                rows = null;
            }

            // An unanswered read is not a shorter list: leave the TTL unarmed so the next caller retries, and
            // hand back null rather than the last good rows, which a caller would read as current.
            if (rows is null) return null;

            _runs = rows;
            _runsReadAt = _time.GetUtcNow();
            return _runs;
        }
        finally
        {
            _runsGate.Release();
        }
    }
}
