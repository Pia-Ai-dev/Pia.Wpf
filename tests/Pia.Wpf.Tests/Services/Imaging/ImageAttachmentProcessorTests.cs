using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Imaging;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Imaging;

/// <summary>Which entry point an attachment came through has to survive preparation: the strip dedups on
/// the path, and a paste has none to dedup on.</summary>
public sealed class ImageAttachmentProcessorTests : IDisposable
{
    private readonly string _dir;

    public ImageAttachmentProcessorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-img-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TempPath.Remove(_dir);

    private static BitmapSource Square(int edge = 8)
    {
        var stride = edge * 3;
        var pixels = new byte[stride * edge];
        Array.Fill(pixels, (byte)0x40);
        return BitmapSource.Create(edge, edge, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
    }

    private string WritePng(string name)
    {
        var path = Path.Combine(_dir, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Square()));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    [Fact]
    public void TryPrepare_FromAFile_CarriesTheSourcePath()
    {
        var path = WritePng("shot.png");

        var attachment = ImageAttachmentProcessor.TryPrepare(path, NullLogger.Instance);

        Assert.NotNull(attachment);
        Assert.Equal(path, attachment!.SourcePath);
    }

    [Fact]
    public void TryPrepare_FromABitmap_LeavesTheSourcePathNull()
    {
        var attachment = ImageAttachmentProcessor.TryPrepare(Square(), NullLogger.Instance);

        Assert.NotNull(attachment);
        Assert.Null(attachment!.SourcePath);
    }

    [Fact]
    public void TryPrepare_GivesEveryAttachmentItsOwnId()
    {
        var path = WritePng("same.png");

        var first = ImageAttachmentProcessor.TryPrepare(path, NullLogger.Instance);
        var second = ImageAttachmentProcessor.TryPrepare(path, NullLogger.Instance);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(Guid.Empty, first!.Id);
        Assert.NotEqual(first.Id, second!.Id);
    }
}
