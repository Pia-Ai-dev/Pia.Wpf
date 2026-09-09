using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class ScreenCaptureThumbnailsTests
{
    [Fact]
    public void Create_ScalesTheLongEdge_AndFreezes()
    {
        var thumbnail = ScreenCaptureThumbnails.Create(Frame(1920, 1080), 240);

        Assert.Equal(240, thumbnail.PixelWidth);
        Assert.Equal(135, thumbnail.PixelHeight);
        Assert.True(thumbnail.IsFrozen);
    }

    [Fact]
    public void Create_PortraitFrames_ScaleByHeight()
    {
        var thumbnail = ScreenCaptureThumbnails.Create(Frame(1080, 1920), 240);

        Assert.Equal(240, thumbnail.PixelHeight);
        Assert.Equal(135, thumbnail.PixelWidth);
    }

    [Fact]
    public void Create_ReturnsTheSourceItself_WhenItAlreadyFits()
    {
        var source = Frame(200, 100);

        Assert.Same(source, ScreenCaptureThumbnails.Create(source, 240));
    }

    [Fact]
    public void Create_ReturnsAnOwnedCopy_NotAViewOverTheSource()
    {
        // A TransformedBitmap would keep the whole 33 MB frame alive for the life of the dialog.
        Assert.IsType<WriteableBitmap>(ScreenCaptureThumbnails.Create(Frame(1920, 1080), 240));
    }

    [Fact]
    public void Create_PickerMaxEdge_IsTheOneTheDialogUses()
    {
        Assert.Equal(240, ScreenCaptureThumbnails.PickerMaxEdge);
    }

    private static BitmapSource Frame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);

        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }
}
