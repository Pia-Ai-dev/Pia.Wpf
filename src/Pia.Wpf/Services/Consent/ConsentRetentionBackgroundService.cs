using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Paths;

namespace Pia.Services.Consent;

/// <summary>
/// <c>Bootstrapper</c> carries the retention window; this only stops a session left running for weeks from
/// drifting past it, so there is deliberately no pass before the first tick.
/// </summary>
public sealed class ConsentRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly ILogger<ConsentRetentionBackgroundService> _logger;

    public ConsentRetentionBackgroundService(ILogger<ConsentRetentionBackgroundService> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RunPass();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    // Private on purpose: this resolves the REAL profile, which no test may sweep.
    private void RunPass()
    {
        try
        {
            var outcome = ConsentRetention.Sweep(
                PiaPaths.ConsentEvidenceDirectory,
                PiaPaths.ConsentAuditDirectory,
                ConsentRetention.DefaultRetainedDays);
            ConsentRetention.LogOutcome(_logger, outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent retention pass failed");
        }
    }
}
