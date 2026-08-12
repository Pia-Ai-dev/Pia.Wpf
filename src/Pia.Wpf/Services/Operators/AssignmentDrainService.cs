using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Operators;

/// <summary>
/// Keeps asking the server about the runs this device is waiting on, from startup onwards.
///
/// The startup pass is the point of the whole class: without it, closing the app mid-run loses the artifact
/// silently — the server keeps the row but drops the plaintext within its own retention window, so nobody
/// would ever come back for it. Idle when nothing is pending, which is the normal state.
/// </summary>
public sealed class AssignmentDrainService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly IAssignmentRunOrchestrator _coordinator;
    private readonly IAssignmentPendingStore _pending;
    private readonly ILogger<AssignmentDrainService> _logger;

    public AssignmentDrainService(
        IAssignmentRunOrchestrator coordinator,
        IAssignmentPendingStore pending,
        ILogger<AssignmentDrainService> logger)
    {
        _coordinator = coordinator;
        _pending = pending;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Reads a cached file, so a tick with nothing outstanding costs nothing and never touches the
                // network — this must not become a background poll of a server the user may not even have.
                if ((await _pending.GetAllAsync()).Count > 0)
                {
                    var finished = await _coordinator.DrainAsync(stoppingToken);
                    if (finished > 0)
                        _logger.LogInformation("Stored and acknowledged {Count} background assignment(s).", finished);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never fatal: the pending entry survives, so the next tick tries again and the artifact is
                // still on the server until it is both stored and acknowledged.
                _logger.LogWarning(ex, "A background-assignment pass failed; it will be retried.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
