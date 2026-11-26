using System.IO;
using System.Windows.Media.Imaging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// WPF-backed <see cref="IClipboardService"/>. Keeps the System.Windows
/// dependency out of ViewModels (enforced by the architecture tests).
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);

    public void SetImage(byte[] encodedImage)
    {
        using var stream = new MemoryStream(encodedImage);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        System.Windows.Clipboard.SetImage(decoder.Frames[0]);
    }
}
