using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class FastPathOptimizerService : IFastPathOptimizer
{
    private readonly ILogger<FastPathOptimizerService> _logger;
    private readonly IWindowManagerService _windowManagerService;
    private readonly IWindowTrackingService _windowTrackingService;
    private readonly ISelectedTextService _selectedTextService;
    private readonly ISettingsService _settingsService;
    private int _isRunning;

    public FastPathOptimizerService(
        ILogger<FastPathOptimizerService> logger,
        IWindowManagerService windowManagerService,
        IWindowTrackingService windowTrackingService,
        ISelectedTextService selectedTextService,
        ISettingsService settingsService)
    {
        _logger = logger;
        _windowManagerService = windowManagerService;
        _windowTrackingService = windowTrackingService;
        _selectedTextService = selectedTextService;
        _settingsService = settingsService;
    }

    public async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _logger.LogDebug("Fast-path optimize ignored because another run is already active");
            return;
        }

        try
        {
            // Track foreground window and capture selection BEFORE opening the Pia
            // window. Showing it first steals focus, so the synthetic Ctrl+C in
            // CaptureAsync would land on Pia (with no selection) instead of the
            // user's app and clipboard would not change.
            _windowTrackingService.TrackWindowAtCursor();
            var captured = await _selectedTextService.CaptureAsync();

            var handle = await _windowManagerService.ShowOptimizeAndGetViewModelAsync();
            handle.PrepareForFastPath();

            if (string.IsNullOrWhiteSpace(captured))
            {
                handle.ShowFastPathSnackbar("Msg_FastPath_NoContent");
                return;
            }

            if (!string.IsNullOrEmpty(handle.InputText))
            {
                handle.ShowFastPathInsertAnywaySnackbar(captured, () => RunInsertAnywayContinuationAsync(captured));
                return;
            }

            await RunFastPathWithInputAsync(handle, captured);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fast-path optimize failed");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private async Task RunFastPathWithInputAsync(IOptimizeFastPathHandle handle, string captured)
    {
        using var dialogCts = new CancellationTokenSource();
        Task? dialogTask = null;

        try
        {
            handle.InputText = captured;
            handle.IsOptimizing = true;
            dialogTask = handle.ShowOptimizingDialogAsync(dialogCts.Token);

            var optimized = await handle.RunFastPathOptimizeAsync();
            if (!optimized || !handle.IsComparisonView)
                return;

            var settings = await _settingsService.GetSettingsAsync();
            if (RequiresTrackedTarget(settings.DefaultOutputAction) && !_windowTrackingService.HasTrackedWindow)
            {
                handle.ShowFastPathSnackbar("Msg_FastPath_NoTargetWindow");
                return;
            }

            var accepted = await handle.RunFastPathAcceptAsync();
            if (accepted && !handle.IsComparisonView && string.IsNullOrWhiteSpace(handle.InputText) && string.IsNullOrWhiteSpace(handle.OptimizedText))
                _windowManagerService.HideWindow(WindowMode.Optimize);
        }
        finally
        {
            handle.IsOptimizing = false;
            dialogCts.Cancel();
            if (dialogTask is not null)
            {
                try { await dialogTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogDebug(ex, "Fast-path optimizing dialog ended with an error"); }
            }
        }
    }

    private async Task RunInsertAnywayContinuationAsync(string captured)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _logger.LogDebug("Fast-path Insert-anyway ignored because another run is already active");
            return;
        }

        try
        {
            // Re-acquire the handle: the user may have hidden/closed/reopened the window
            // between the snackbar and the click. Calling Show... again ensures a live VM.
            var handle = await _windowManagerService.ShowOptimizeAndGetViewModelAsync();
            handle.PrepareForFastPath();
            await RunFastPathWithInputAsync(handle, captured);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fast-path Insert-anyway continuation failed");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private static bool RequiresTrackedTarget(OutputAction action)
    {
        return action is OutputAction.PasteToPreviousWindow or OutputAction.AutoType;
    }
}
