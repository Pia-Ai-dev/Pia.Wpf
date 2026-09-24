using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class FrameUniformityDetectorTests
{
    [Fact]
    public void AllBlack_IsUniform()
    {
        var frame = Solid(800, 600, 0, 0, 0);

        Assert.Equal(1.0, FrameUniformityDetector.DominantShare(frame));
        Assert.True(FrameUniformityDetector.IsNearUniform(frame));
    }

    [Fact]
    public void AllWhite_IsUniform()
    {
        var frame = Solid(800, 600, 255, 255, 255);

        Assert.Equal(1.0, FrameUniformityDetector.DominantShare(frame));
        Assert.True(FrameUniformityDetector.IsNearUniform(frame));
    }

    [Fact]
    public void BlackWithACursorSizedBlob_IsStillUniform()
    {
        var pixels = new byte[800 * 600 * 4];
        Paint(pixels, 800, 0, 0, 80, 60, 255, 255, 255);

        Assert.True(FrameUniformityDetector.IsNearUniform(Create(800, 600, pixels)));
    }

    [Fact]
    public void BlackWithATitleBar_IsNotUniform()
    {
        var pixels = new byte[1920 * 1080 * 4];
        Paint(pixels, 1920, 0, 0, 1920, 40, 0x2B, 0x2B, 0x2B);

        var frame = Create(1920, 1080, pixels);

        Assert.False(FrameUniformityDetector.IsNearUniform(frame));
        Assert.InRange(FrameUniformityDetector.DominantShare(frame), 0.9, 0.98);
    }

    [Fact]
    public void NoiseInsideOneQuantizationBin_IsUniform()
    {
        var random = new Random(7);
        var pixels = new byte[400 * 300 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)random.Next(8);
            pixels[i + 1] = (byte)random.Next(8);
            pixels[i + 2] = (byte)random.Next(8);
        }

        Assert.Equal(1.0, FrameUniformityDetector.DominantShare(Create(400, 300, pixels)));
    }

    [Fact]
    public void TwoToneFrame_IsNotUniform()
    {
        var pixels = new byte[400 * 300 * 4];
        Paint(pixels, 400, 0, 0, 400, 150, 255, 255, 255);

        var frame = Create(400, 300, pixels);

        Assert.False(FrameUniformityDetector.IsNearUniform(frame));
        Assert.InRange(FrameUniformityDetector.DominantShare(frame), 0.49, 0.51);
    }

    [Fact]
    public void Bgra32Input_IsConvertedNotRejected()
    {
        var pixels = new byte[200 * 200 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255;
        }

        var frame = BitmapSource.Create(200, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 200 * 4);
        frame.Freeze();

        Assert.True(FrameUniformityDetector.IsNearUniform(frame));
    }

    [Fact]
    public void AFourKFrame_StaysInsideTheSampleBudget()
    {
        var frame = Solid(3840, 2160, 0, 0, 0);

        var stopwatch = Stopwatch.StartNew();
        var uniform = FrameUniformityDetector.IsNearUniform(frame);
        stopwatch.Stop();

        Assert.True(uniform);
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"sampling a 4K frame took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ThresholdConstant_IsPinned() =>
        Assert.Equal(0.98, FrameUniformityDetector.UniformShareThreshold);

    private static BitmapSource Solid(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        Paint(pixels, width, 0, 0, width, height, blue, green, red);
        return Create(width, height, pixels);
    }

    private static void Paint(
        byte[] pixels, int stridePixels, int x, int y, int width, int height, byte blue, byte green, byte red)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = ((row * stridePixels) + column) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
            }
        }
    }

    private static BitmapSource Create(int width, int height, byte[] pixels)
    {
        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        frame.Freeze();
        return frame;
    }
}
