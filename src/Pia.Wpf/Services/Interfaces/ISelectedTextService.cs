namespace Pia.Services.Interfaces;

public interface ISelectedTextService
{
    /// <summary>
    /// Captures the text currently selected in the foreground window.
    /// Sends Ctrl+C to the foreground window, reads the resulting clipboard text,
    /// and restores the prior clipboard contents.
    /// Must be called on the UI/STA thread while the foreign window still has focus.
    /// Returns null when no text was selected (clipboard didn't change),
    /// the clipboard contained non-text data, or an error prevented capture.
    /// </summary>
    Task<string?> CaptureAsync(CancellationToken cancellationToken = default);
}
