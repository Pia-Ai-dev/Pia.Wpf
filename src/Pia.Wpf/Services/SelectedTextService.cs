using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Native;
using Pia.Services.Interfaces;

namespace Pia.Services;

public partial class SelectedTextService : ISelectedTextService
{
    private const int PollIntervalMs = 20;
    private const int PollAttempts = 10; // 200 ms total

    private readonly ILogger<SelectedTextService> _logger;

    public SelectedTextService(ILogger<SelectedTextService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var seqBefore = GetClipboardSequenceNumber();
        var savedClipboard = SnapshotClipboard();

        var sendResult = KeyboardInput.PressCtrlCReleasingModifiers();
        if (sendResult == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("Capture: SendInput Ctrl+C returned 0 (Win32 error {Error})", error);
            return null;
        }

        var changed = false;
        for (var i = 0; i < PollAttempts; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            await Task.Delay(PollIntervalMs, cancellationToken);

            if (GetClipboardSequenceNumber() != seqBefore)
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            _logger.LogDebug("Capture: clipboard sequence unchanged after Ctrl+C — assuming no selection");
            return null;
        }

        string? capturedText = null;
        try
        {
            if (Clipboard.ContainsText())
                capturedText = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture: failed to read text from clipboard");
        }

        TryRestoreClipboard(savedClipboard);

        if (string.IsNullOrEmpty(capturedText))
        {
            _logger.LogDebug("Capture: clipboard changed but contained no text (image/file/other format)");
            return null;
        }

        _logger.LogInformation("Capture: captured {Length} chars from selection", capturedText.Length);
        _logger.SensitiveDebug("Capture: selection text was '{Text}'", capturedText);
        return capturedText;
    }

    private DataObject? SnapshotClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current == null)
                return null;

            var snapshot = new DataObject();
            var anyCopied = false;
            foreach (var format in current.GetFormats())
            {
                try
                {
                    var data = current.GetData(format);
                    if (data != null)
                    {
                        snapshot.SetData(format, data);
                        anyCopied = true;
                    }
                }
                catch
                {
                    // Some formats cannot be serialized cross-process or carry stale COM pointers; skip them.
                }
            }
            return anyCopied ? snapshot : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture: failed to snapshot existing clipboard for restore");
            return null;
        }
    }

    private void TryRestoreClipboard(DataObject? savedClipboard)
    {
        try
        {
            if (savedClipboard != null)
                Clipboard.SetDataObject(savedClipboard, copy: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture: failed to restore prior clipboard contents");
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();
}
