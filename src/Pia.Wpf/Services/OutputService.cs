using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public partial class OutputService : IOutputService
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

            SendCharacter(c);

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
        var result = PressCtrlV();
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
            _logger.LogInformation("{Operation}: restoring tracked window '{Title}' (process: {Process})",
                operation, title, process);

            if (!_windowTracking.RestorePreviousWindow())
            {
                _logger.LogWarning("{Operation}: RestorePreviousWindow failed for '{Title}' (process: {Process})",
                    operation, title, process);
                throw new InvalidOperationException(
                    $"Failed to restore previous window '{title}' ({process})");
            }
        }
        else
        {
            _logger.LogInformation("{Operation}: no tracked window, using Alt+Tab", operation);
            PressAltTab();
        }
    }

    private static void SendCharacter(char c)
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = 0;
        inputs[0].ki.wScan = c;
        inputs[0].ki.dwFlags = KEYEVENTF_UNICODE;

        // Key up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = 0;
        inputs[1].ki.wScan = c;
        inputs[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void PressAltTab()
    {
        var inputs = new INPUT[4];

        // Alt down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_MENU;
        inputs[0].ki.wScan = 0;
        inputs[0].ki.dwFlags = 0;

        // Tab down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_TAB;
        inputs[1].ki.wScan = 0;
        inputs[1].ki.dwFlags = 0;

        // Tab up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].ki.wVk = VK_TAB;
        inputs[2].ki.wScan = 0;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;

        // Alt up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].ki.wVk = VK_MENU;
        inputs[3].ki.wScan = 0;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    private static uint PressCtrlV()
    {
        var inputs = new INPUT[4];

        // Ctrl down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_CONTROL;
        inputs[0].ki.wScan = 0;
        inputs[0].ki.dwFlags = 0;

        // V down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_V;
        inputs[1].ki.wScan = 0;
        inputs[1].ki.dwFlags = 0;

        // V up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].ki.wVk = VK_V;
        inputs[2].ki.wScan = 0;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;

        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].ki.wVk = VK_CONTROL;
        inputs[3].ki.wScan = 0;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;

        return SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    private const int INPUT_KEYBOARD = 1;
    private const int KEYEVENTF_UNICODE = 0x0004;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_MENU = 0x12;     // Alt key
    private const ushort VK_TAB = 0x09;      // Tab key
    private const ushort VK_CONTROL = 0x11;  // Ctrl key
    private const ushort VK_V = 0x56;        // V key

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
        private readonly ulong padding;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
