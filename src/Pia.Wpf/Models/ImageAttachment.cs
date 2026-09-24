using System.Windows.Media.Imaging;

namespace Pia.Models;

public sealed class ImageAttachment
{
    /// <summary>What a per-item AutomationId and a remove click identify one of several attachments by —
    /// the bytes cannot, two screenshots of the same window being byte-identical.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public required byte[] JpegBytes { get; init; }
    public required string MimeType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required BitmapSource Thumbnail { get; init; }

    /// <summary>Null for a paste or a screen grab — those have no file behind them.</summary>
    public string? SourcePath { get; init; }
}
