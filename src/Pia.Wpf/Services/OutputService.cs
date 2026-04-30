using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Native;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class OutputService : IOutputService
{
    private readonly IWindowTrackingService _windowTracking;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<OutputService> _logger;

    public OutputService(
        IWindowTrackingService windowTracking,
        ISettingsService settingsService,
        ILogger<OutputService> logger)
    {
        _windowTracking = windowTracking;
        _settingsService = settingsService;
        _logger = logger;
    }

    public Task CopyToClipboardAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        Application.Current.Dispatcher.Invoke(() =>
        {
            Clipboard.SetText(text);
        });

        _logger.LogDebug("Copied {Length} chars to clipboard", text.Length);
        return Task.CompletedTask;
    }

    public async Task AutoTypeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;

        RestoreOrSwitchWindow("AutoType");

        // Small delay to allow window to gain focus
        await Task.Delay(100, cancellationToken);

        var settings = await _settingsService.GetSettingsAsync();
        var delay = settings.AutoTypeDelayMs;

        _logger.LogInformation("AutoType: typing {Length} chars with {Delay}ms delay", text.Length, delay);

        foreach (var c in text)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            KeyboardInput.SendCharacter(c);

            if (delay > 0)
                await Task.Delay(delay, cancellationToken);
        }
    }

    public async Task PasteToPreviousWindowAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // First copy to clipboard
        await CopyToClipboardAsync(text);

        // Switch to previous window
        RestoreOrSwitchWindow("PasteToPreviousWindow");

        // Delay to allow window to gain focus (200ms for Electron apps)
        await Task.Delay(200, cancellationToken);

        // Paste with Ctrl+V
        var result = KeyboardInput.PressCtrlV();
        if (result == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("PasteToPreviousWindow: SendInput for Ctrl+V returned 0, Win32 error: {Error}", error);
            throw new InvalidOperationException($"SendInput failed (Win32 error {error})");
        }

        _logger.LogInformation("PasteToPreviousWindow: successfully sent Ctrl+V ({Result} events injected)", result);
    }

    private void RestoreOrSwitchWindow(string operation)
    {
        if (_windowTracking.HasTrackedWindow)
        {
            var title = _windowTracking.GetTrackedWindowTitle();
            var process = _windowTracking.GetTrackedWindowProcessName();
            _logger.LogInformation("{Operation}: restoring tracked window (process: {Process})",
                operation, process);
            _logger.SensitiveDebug("{Operation}: tracked window title was '{Title}'", operation, title);

            if (!_windowTracking.RestorePreviousWindow())
            {
                _logger.LogWarning("{Operation}: RestorePreviousWindow failed (process: {Process})",
                    operation, process);
                _logger.SensitiveDebug("{Operation}: failed window title was '{Title}'", operation, title);
                throw new InvalidOperationException(
                    $"Failed to restore previous window '{title}' ({process})");
            }
        }
        else
        {
            _logger.LogInformation("{Operation}: no tracked window, using Alt+Tab", operation);
            KeyboardInput.PressAltTab();
        }
    }
}
