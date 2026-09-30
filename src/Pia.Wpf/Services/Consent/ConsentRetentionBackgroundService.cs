using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Paths;

namespace Pia.Services.Consent;

/// <summary>
/// Sweeps v2 evidence at start and daily; the v1 window runs daily only, because <c>Bootstrapper</c> already swept
/// it at launch.
/// </summary>
public sealed class ConsentRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly ConsentLifetimeService _lifetime;
    private readonly ILogger<ConsentRetentionBackgroundService> _logger;

    public ConsentRetentionBackgroundService(
        ConsentLifetimeService lifetime, ILogger<ConsentRetentionBackgroundService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Off the starting thread: StartAsync runs this synchronously up to its first real await, and the
            // vault scan never yields.
            await Task.Run(() => RunLifetimePassAsync(stoppingToken), stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                RunRetentionPass();
                await RunLifetimePassAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    // Private on purpose: this resolves the REAL profile, which no test may sweep.
    private void RunRetentionPass()
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

    private async Task RunLifetimePassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _lifetime.SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Consent evidence lifetime pass failed");
        }
    }
}
