using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public sealed class PendingFileAttachmentTests
{
    /// <summary>A SymbolRegular member above U+FFFF compiles clean and renders a garbage letter, so nothing
    /// but a test catches it — and 2,863 of the 9,235 members are up there.</summary>
    [Theory]
    [InlineData(PendingFileKind.Text)]
    [InlineData(PendingFileKind.Document)]
    [InlineData(PendingFileKind.Email)]
    public void Icon_IsInsideTheBasicMultilingualPlane(PendingFileKind kind)
    {
        var attachment = new PendingFileAttachment
        {
            FullPath = @"C:\work\notes.txt",
            FileName = "notes.txt",
            Kind = kind,
            Text = "hello",
            Truncated = false,
            OriginalCharCount = 5,
        };

        Assert.True((int)attachment.Icon <= 0xFFFF,
            $"{kind} maps to SymbolRegular.{attachment.Icon} at U+{(int)attachment.Icon:X}, which is outside " +
            "the BMP and renders as a garbage letter.");
    }

    [Fact]
    public void Icon_IsDistinctPerKind()
    {
        // A single fallback for every kind would satisfy the BMP theory above while showing one icon
        // for a mail, a spreadsheet and a log file.
        var icons = Enum.GetValues<PendingFileKind>()
            .Select(k => new PendingFileAttachment
            {
                FullPath = @"C:\work\notes.txt",
                FileName = "notes.txt",
                Kind = k,
                Text = "hello",
                Truncated = false,
                OriginalCharCount = 5,
            }.Icon)
            .ToList();

        Assert.Equal(icons.Count, icons.Distinct().Count());
    }
}
