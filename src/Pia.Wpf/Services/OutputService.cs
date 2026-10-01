using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
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

    public Task CopySecretToClipboardAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        Application.Current.Dispatcher.Invoke(() => Clipboard.SetDataObject(ClipboardHistoryExclusion.Wrap(text), copy: true));

        _logger.LogDebug("Copied {Length} chars to clipboard, excluded from history", text.Length);
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
            _logger.LogInformation("{Operation}: restoring tracked window", operation);
            _logger.SensitiveDebug("{Operation}: tracked window was {Process} '{Title}'", operation, process, title);

            if (!_windowTracking.RestorePreviousWindow())
            {
                _logger.LogWarning("{Operation}: RestorePreviousWindow failed", operation);
                _logger.SensitiveDebug("{Operation}: failed window was {Process} '{Title}'", operation, process, title);
                // Callers log this exception at Warning, so it names neither the window nor its process.
                throw new InvalidOperationException("Failed to restore the previous window");
            }
        }
        else
        {
            _logger.LogInformation("{Operation}: no tracked window, using Alt+Tab", operation);
            KeyboardInput.PressAltTab();
        }
    }
}
