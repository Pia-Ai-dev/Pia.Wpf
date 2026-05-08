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

        IOptimizeFastPathHandle? handle = null;
        using var dialogCts = new CancellationTokenSource();
        Task? dialogTask = null;

        try
        {
            // Track foreground window and capture selection BEFORE opening the Pia
            // window. Showing it first steals focus, so the synthetic Ctrl+C in
            // CaptureAsync would land on Pia (with no selection) instead of the
            // user's app and clipboard would not change.
            _windowTrackingService.TrackWindowAtCursor();
            var captured = await _selectedTextService.CaptureAsync();

            handle = await _windowManagerService.ShowOptimizeAndGetViewModelAsync();
            handle.PrepareForFastPath();
            handle.IsOptimizing = true;
            dialogTask = handle.ShowOptimizingDialogAsync(dialogCts.Token);

            if (string.IsNullOrWhiteSpace(captured))
            {
                handle.ShowFastPathSnackbar("Msg_FastPath_NoContent");
                return;
            }

            handle.InputText = captured;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fast-path optimize failed");
        }
        finally
        {
            if (handle is not null)
                handle.IsOptimizing = false;

            dialogCts.Cancel();
            if (dialogTask is not null)
            {
                try { await dialogTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogDebug(ex, "Fast-path optimizing dialog ended with an error"); }
            }

            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private static bool RequiresTrackedTarget(OutputAction action)
    {
        return action is OutputAction.PasteToPreviousWindow or OutputAction.AutoType;
    }
}
