using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// WPF-backed <see cref="IClipboardService"/>. Keeps the System.Windows
/// dependency out of ViewModels (enforced by the architecture tests).
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
