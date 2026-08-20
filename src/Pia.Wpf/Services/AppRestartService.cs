using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Runs its own awaited pre-exit sequence rather than trusting <c>App.OnExit</c>, which is
/// <c>async void</c> and races process death after its first await.
/// </summary>
public class AppRestartService : IAppRestartService
{
    private readonly ISyncClientService _syncClientService;
    private readonly ITrayIconService _trayIconService;
    private readonly ILogger<AppRestartService> _logger;
    private int _latched;

    public AppRestartService(
        ISyncClientService syncClientService,
        ITrayIconService trayIconService,
        ILogger<AppRestartService> logger)
    {
        _syncClientService = syncClientService;
        _trayIconService = trayIconService;
        _logger = logger;
    }

    public async Task RestartAsync()
    {
        if (Interlocked.Exchange(ref _latched, 1) != 0)
            return;

        // Both steps are best-effort: the overlay that got us here has no dismiss, so a throw would
        // strand the user with no way to retry.
        try
        {
            // Capped because the wait is unbounded behind a per-request HTTP timeout, and a push left in
            // flight costs nothing: the data is already local and the cursor only advances on success.
            var stopped = _syncClientService.StopBackgroundSyncAndWaitAsync();
            if (await Task.WhenAny(stopped, Task.Delay(SyncStopTimeout)) != stopped)
                _logger.LogWarning("Timed out waiting for background sync to stop; restarting anyway");
            else
                await stopped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop background sync before restarting");
        }

        try
        {
            _trayIconService.PrepareForExit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to tear the UI down before restarting");
        }

        _logger.LogInformation("Restarting to apply a changed policy");
        RequestRestartAndShutdown();
    }

    /// <summary>Shortened by tests, which cannot wait out a real one.</summary>
    protected virtual TimeSpan SyncStopTimeout => TimeSpan.FromSeconds(10);

    /// <summary>The one step a test may not run: it ends the process.</summary>
    protected virtual void RequestRestartAndShutdown()
    {
        App.RequestRestart();
        Application.Current?.Shutdown();
    }
}
