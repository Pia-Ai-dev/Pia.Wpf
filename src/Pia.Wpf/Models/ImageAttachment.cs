using System.Windows.Media.Imaging;

namespace Pia.Models;

public sealed class ImageAttachment
{
    public required byte[] JpegBytes { get; init; }
    public required string MimeType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required BitmapSource Thumbnail { get; init; }
}
