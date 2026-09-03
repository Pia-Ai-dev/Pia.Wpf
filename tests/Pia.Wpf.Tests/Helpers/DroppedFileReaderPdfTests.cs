using System.IO;
using System.Threading;
using Pia.Helpers;
using Pia.Tests.TestInfrastructure;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// A dropped PDF becomes text like a .docx does. The one case that is not a failure to report as
/// one: a scan, which parses fine and simply has no text layer.
/// </summary>
public sealed class DroppedFileReaderPdfTests : IDisposable
{
    private readonly string _dir;

    public DroppedFileReaderPdfTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-pdfread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TempPath.Remove(_dir);

    [Fact]
    public async Task ReadsTheTextOfEveryPage()
    {
        var path = WritePdf("two-pages.pdf", ["Quarterly figures", "Signed in Hamburg"]);

        var result = await DroppedFileReader.ReadPdfAsync(path, CancellationToken.None);

        Assert.Equal(DroppedFileReader.ReadStatus.Ok, result.Status);
        Assert.Contains("Quarterly figures", result.Text);
        Assert.Contains("Signed in Hamburg", result.Text);
    }

    [Fact]
    public async Task AScanWithNoTextLayerIsReportedAsSuch()
    {
        var path = WritePdf("scan.pdf", [null]);

        var result = await DroppedFileReader.ReadPdfAsync(path, CancellationToken.None);

        Assert.Equal(DroppedFileReader.ReadStatus.Failed, result.Status);
        Assert.Equal(DroppedFileReader.NoTextLayer, result.Error);
    }

    [Fact]
    public async Task SomethingThatIsNotAPdfFailsWithoutThrowing()
    {
        var path = Path.Combine(_dir, "not-really.pdf");
        File.WriteAllText(path, "just some words");

        var result = await DroppedFileReader.ReadPdfAsync(path, CancellationToken.None);

        Assert.Equal(DroppedFileReader.ReadStatus.Failed, result.Status);
        Assert.NotEqual(DroppedFileReader.NoTextLayer, result.Error);
    }

    /// <summary>One page per entry; a null entry is a page with no text on it, i.e. a scan.</summary>
    private string WritePdf(string fileName, string?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var text in pages)
        {
            var page = builder.AddPage(595, 842);
            if (text is not null)
                page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 780), font);
        }

        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }
}
