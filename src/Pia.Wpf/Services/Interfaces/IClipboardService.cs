namespace Pia.Services.Interfaces;

/// <summary>
/// Abstraction over the system clipboard so ViewModels don't take a direct
/// dependency on System.Windows. Implemented in the WPF layer.
/// </summary>
public interface IClipboardService
{
    /// <summary>Places UTF-16 text on the clipboard. May throw if the clipboard is locked.</summary>
    void SetText(string text);
}
