using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class CaptureResultTests
{
    [Fact]
    public void Success_RequiresAFrozenBitmap()
    {
        var unfrozen = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgr32, null);

        Assert.Throws<ArgumentException>(() => CaptureResult.Success(WindowTarget(), unfrozen));
    }

    [Fact]
    public void Success_CarriesTheBitmapAndItsPixelSize()
    {
        var result = CaptureResult.Success(WindowTarget(), Frozen(7, 5));

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Width);
        Assert.Equal(5, result.Height);
        Assert.Equal(CaptureFailureReason.None, result.Reason);
    }

    [Fact]
    public void Failed_HasNoBitmap_AndCarriesTheReason()
    {
        var result = CaptureResult.Failed(WindowTarget(), CaptureFailureReason.Minimized);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Bitmap);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
        Assert.Equal(CaptureFailureReason.Minimized, result.Reason);
    }

    [Fact]
    public void WindowTarget_ToString_OmitsTheTitle()
    {
        var text = (WindowTarget() with { Title = "quarterly layoffs" }).ToString();

        Assert.DoesNotContain("quarterly", text);
        Assert.Contains("notepad", text);
        Assert.Contains("640x480", text);
    }

    [Fact]
    public void MonitorTarget_ToString_NamesTheDeviceNotATitle()
    {
        var monitor = new CaptureTarget(
            CaptureTargetKind.Monitor, 0, @"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440),
            string.Empty, "should never be set", IsPrimary: true, IsMinimized: false);

        var text = monitor.ToString();

        Assert.DoesNotContain("never", text);
        Assert.Contains(@"\\.\DISPLAY1", text);
        Assert.Contains("2560x1440", text);
        Assert.Contains("primary", text);
    }

    [Fact]
    public void KeyFor_HasNoMessageForASuccessfulCapture()
    {
        Assert.Null(ScreenCaptureFailureText.KeyFor(CaptureFailureReason.None));
        Assert.Equal("Msg_Screen_UniformFrame", ScreenCaptureFailureText.KeyFor(CaptureFailureReason.UniformFrame));
    }

    [Fact]
    public void PixelRect_Contains_IsInclusiveOfEdges()
    {
        var outer = new PixelRect(0, 0, 100, 50);

        Assert.True(outer.Contains(new PixelRect(0, 0, 100, 50)));
        Assert.True(outer.Contains(new PixelRect(8, 8, 84, 34)));
        Assert.False(outer.Contains(new PixelRect(-1, 0, 100, 50)));
        Assert.False(outer.Contains(new PixelRect(0, 0, 101, 50)));
        Assert.False(outer.Contains(new PixelRect(0, 1, 100, 50)));
    }

    [Fact]
    public void PixelRect_IsEmpty_WhenEitherAxisHasNoExtent()
    {
        Assert.True(new PixelRect(10, 10, 0, 40).IsEmpty);
        Assert.True(new PixelRect(10, 10, 40, 0).IsEmpty);
        Assert.True(new PixelRect(10, 10, -3, 40).IsEmpty);
        Assert.False(new PixelRect(-32000, -32000, 40, 40).IsEmpty);
    }

    private static CaptureTarget WindowTarget() => new(
        CaptureTargetKind.Window, 0x1234, string.Empty, new PixelRect(10, 20, 640, 480),
        "notepad", "Untitled", IsPrimary: false, IsMinimized: false);

    private static BitmapSource Frozen(int width, int height)
    {
        var frame = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        frame.Freeze();
        return frame;
    }
}
