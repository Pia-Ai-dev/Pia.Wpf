using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pia.Services.Screen;

internal static class FrameUniformityDetector
{
    public const double UniformShareThreshold = 0.98;
    public const int SampleBudget = 250_000;

    private const int LevelsPerChannel = 32;

    /// <summary>Share of the sampled pixels sitting in the largest 5-bit-per-channel colour bin. Quantizing that
    /// coarsely absorbs dithering; a bin that still splits can only lower the share, which errs toward sending.</summary>
    public static double DominantShare(BitmapSource frame)
    {
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return 1.0;
        }

        BitmapSource source = frame;
        if (frame.Format != PixelFormats.Bgr32)
        {
            source = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        }

        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)width * height / SampleBudget)));
        var bins = new int[LevelsPerChannel * LevelsPerChannel * LevelsPerChannel];
        var row = new byte[width * 4];
        var sampled = 0;
        var largest = 0;

        for (var y = 0; y < height; y += step)
        {
            source.CopyPixels(new Int32Rect(0, y, width, 1), row, width * 4, 0);

            for (var x = 0; x < width; x += step)
            {
                var offset = x * 4;
                var bin = ((row[offset] >> 3) << 10) | ((row[offset + 1] >> 3) << 5) | (row[offset + 2] >> 3);
                var count = ++bins[bin];
                if (count > largest)
                {
                    largest = count;
                }

                sampled++;
            }
        }

        return sampled == 0 ? 1.0 : (double)largest / sampled;
    }

    public static bool IsNearUniform(BitmapSource frame) => DominantShare(frame) >= UniformShareThreshold;
}
