using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Pins <see cref="DroppedFileReader.Classify"/>, the extension→kind map that decides how a dropped
/// file is read. Written before the branch chain became a lookup table, so the table is held to the
/// chain's answers.
/// </summary>
public sealed class DroppedFileReaderClassifyTests
{
    [Theory]
    [InlineData("C:\\drop\\report.docx", FileKind.Docx)]
    [InlineData("C:\\drop\\book.xlsx", FileKind.Xlsx)]
    [InlineData("C:\\drop\\macros.xlsm", FileKind.Xlsx)]
    [InlineData("C:\\drop\\paper.pdf", FileKind.Pdf)]
    [InlineData("C:\\drop\\shot.png", FileKind.Image)]
    [InlineData("C:\\drop\\shot.webp", FileKind.Image)]
    [InlineData("C:\\drop\\voice.m4a", FileKind.Audio)]
    [InlineData("C:\\drop\\voice.flac", FileKind.Audio)]
    [InlineData("C:\\drop\\notes.md", FileKind.Text)]
    [InlineData("C:\\drop\\Program.cs", FileKind.Text)]
    [InlineData("C:\\drop\\.gitignore", FileKind.Text)]
    [InlineData("relative/path/DATA.CSV", FileKind.Text)] // case-insensitive extension match
    [InlineData("C:\\drop\\REPORT.DOCX", FileKind.Docx)]
    public void Classify_MapsKnownExtensions(string path, FileKind expected)
        => Assert.Equal(expected, DroppedFileReader.Classify(path));

    [Theory]
    [InlineData("C:\\drop\\archive.zip")]
    [InlineData("C:\\drop\\installer.exe")]
    [InlineData("C:\\drop\\legacy.doc")]
    [InlineData("C:\\drop\\noextension")]
    [InlineData("C:\\drop\\trailingdot.")]
    [InlineData("")]
    public void Classify_UnsupportedForUnknownOrMissingExtension(string path)
        => Assert.Equal(FileKind.Unsupported, DroppedFileReader.Classify(path));
}
