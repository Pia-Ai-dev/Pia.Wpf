using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The wrapper the model actually sees around a dropped file: one preamble, one element per
/// file, attribute values escaped and the body left alone.</summary>
public sealed class AssistantPromptComposerAttachedFileTests
{
    private const string Preamble =
        "The user attached the following file(s) to this message. Use them as context for the request.";

    private static PendingFileAttachment File(
        string fileName = "notes.txt",
        PendingFileKind kind = PendingFileKind.Text,
        string text = "hello",
        AttachmentCut? cut = null,
        string? fullPath = null) =>
        new()
        {
            FullPath = fullPath ?? @"C:\work\" + fileName,
            FileName = fileName,
            Kind = kind,
            Text = text,
            Cut = cut,
        };

    private static readonly AttachmentCut ThreeOf900 = new(ShownLines: 3, TotalLines: 900, EndsMidLine: false, ShownPercent: 1);

    [Fact]
    public void BuildAttachedFileBlock_ReturnsEmptyForNoFiles()
    {
        Assert.Equal(string.Empty, AssistantPromptComposer.BuildAttachedFileBlock([]));
    }

    [Fact]
    public void BuildAttachedFileBlock_EmitsThePreambleOnce()
    {
        // Counted over TWO files: with one file "once" and "present" are the same assertion, and the
        // preamble-inside-the-loop mutation survives it.
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File("a.txt"), File("b.txt")]);

        var occurrences = block.Split(Preamble).Length - 1;
        Assert.Equal(1, occurrences);
        Assert.StartsWith(Preamble, block, StringComparison.Ordinal);
        Assert.Equal(2, block.Split("<attached_file ").Length - 1);
    }

    [Theory]
    [InlineData(PendingFileKind.Text, "text")]
    [InlineData(PendingFileKind.Document, "document")]
    [InlineData(PendingFileKind.Email, "email")]
    public void BuildAttachedFileBlock_EmitsNameAndTypeAttributes(PendingFileKind kind, string expectedType)
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock([File("Quarterly Report.docx", kind)]);

        Assert.Contains($"<attached_file name=\"Quarterly Report.docx\" type=\"{expectedType}\">", block);
        Assert.Contains("</attached_file>", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_OmitsTheFullPath()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File("secret.txt", fullPath: @"C:\Users\marco\Private\secret.txt")]);

        Assert.DoesNotContain(@"C:\Users\marco\Private", block);
        Assert.DoesNotContain("Private", block);
        Assert.Contains("name=\"secret.txt\"", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_EscapesAttributeValues()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File("a&b \"<quoted>\".txt")]);

        Assert.Contains("name=\"a&amp;b &quot;&lt;quoted>&quot;.txt\"", block);
        // The raw name would close the attribute early and swallow the rest of the element.
        Assert.DoesNotContain("name=\"a&b", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_AddsTruncatedAttributesOnlyWhenCut()
    {
        var whole = AssistantPromptComposer.BuildAttachedFileBlock([File(text: "12345")]);
        Assert.DoesNotContain("truncated=", whole);
        Assert.DoesNotContain("note=", whole);

        var cut = AssistantPromptComposer.BuildAttachedFileBlock([File(text: "a\nb\nc", cut: ThreeOf900)]);
        Assert.Contains("truncated=\"true\"", cut);
        Assert.Contains("note=\"Showing lines 1-3 of 900. The rest was not sent.\"", cut);
    }

    [Fact]
    public void BuildAttachedFileBlock_PointsAtReadFile_WhenThePathIsKnown()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File(cut: new AttachmentCut(40, 900, EndsMidLine: false, ShownPercent: 4))], _ => "Playground/notes.txt");

        Assert.Contains("The rest was not sent; read it with read_file (path: Playground/notes.txt, offset: 41).", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_ReReadsALineThatWasCutShort()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File(cut: new AttachmentCut(1, 1, EndsMidLine: true, ShownPercent: 7))], _ => "data.json");

        Assert.Contains("Showing lines 1-1 of 1; line 1 is cut short.", block);
        Assert.Contains("offset: 1)", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_AFileWithoutAPath_SaysTheRestWasNotSent()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock([File(cut: ThreeOf900)], _ => null);

        Assert.Contains("The rest was not sent.", block);
        Assert.DoesNotContain("read_file", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_AWholeFile_NeverNamesReadFile() =>
        Assert.DoesNotContain("read_file",
            AssistantPromptComposer.BuildAttachedFileBlock([File()], _ => "notes.txt"));

    [Fact]
    public void BuildAttachedFileBlock_LeavesTheBodyUnescaped()
    {
        // Escaping the body would corrupt every dropped .xml/.html file; only attribute values are escaped.
        var body = "if (a < b && c > \"d\") { }";
        var block = AssistantPromptComposer.BuildAttachedFileBlock([File(text: body)]);

        Assert.Contains(">\n" + body + "\n</attached_file>", block);
        Assert.DoesNotContain("&amp;&amp;", block);
    }
}
