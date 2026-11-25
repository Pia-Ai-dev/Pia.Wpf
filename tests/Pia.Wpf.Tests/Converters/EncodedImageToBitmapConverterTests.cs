using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pia.Converters;
using Xunit;

namespace Pia.Tests.Converters;

public class EncodedImageToBitmapConverterTests
{
    private static object? Convert(object? value) =>
        new EncodedImageToBitmapConverter().Convert(value!, typeof(ImageSource), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void JpegBytes_BecomeAFrozenBitmapOfTheSameSize()
    {
        var pixels = new byte[4 * 3 * 4];
        var source = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var bitmap = Assert.IsAssignableFrom<BitmapSource>(Convert(stream.ToArray()));

        Assert.True(bitmap.IsFrozen);
        Assert.Equal(4, bitmap.PixelWidth);
        Assert.Equal(3, bitmap.PixelHeight);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]
    public void MissingOrUndecodableBytes_ReturnNull(byte[]? bytes)
    {
        Assert.Null(Convert(bytes));
    }
}
