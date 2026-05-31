using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;

namespace Pia.Services.Imaging;

public static class ImageAttachmentProcessor
{
    private const int MaxLongEdge = 1568;
    private const int ThumbnailMaxEdge = 300;
    private const long ThresholdBytes = 3_500_000;
    private static readonly int[] QualitySteps = [85, 70, 55];

    public static ImageAttachment? TryPrepare(string filePath, ILogger logger)
    {
        try
        {
            logger.SensitiveDebug("Preparing image attachment from {File}", filePath);

            BitmapFrame frame;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return null;
                frame = decoder.Frames[0];
            }

            var resized = ResizeIfNeeded(frame, MaxLongEdge);
            var bytes = TryEncode(resized);
            if (bytes is null)
            {
                logger.LogInformation("Image too large after re-encoding");
                return null;
            }

            var thumb = ResizeIfNeeded(resized, ThumbnailMaxEdge);
            if (!thumb.IsFrozen && thumb.CanFreeze) thumb.Freeze();

            return new ImageAttachment
            {
                JpegBytes = bytes,
                MimeType = "image/jpeg",
                Width = resized.PixelWidth,
                Height = resized.PixelHeight,
                Thumbnail = thumb,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to prepare image attachment ({Type})", ex.GetType().Name);
            logger.SensitiveDebug("Image attachment failure for {File}: {Error}", filePath, ex);
            return null;
        }
    }

    private static BitmapSource ResizeIfNeeded(BitmapSource source, int maxEdge)
    {
        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxEdge) return source;
        double scale = (double)maxEdge / longest;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        if (transformed.CanFreeze) transformed.Freeze();
        return transformed;
    }

    private static byte[]? TryEncode(BitmapSource source)
    {
        foreach (var quality in QualitySteps)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            if (ms.Length <= ThresholdBytes) return ms.ToArray();
        }
        return null;
    }
}
