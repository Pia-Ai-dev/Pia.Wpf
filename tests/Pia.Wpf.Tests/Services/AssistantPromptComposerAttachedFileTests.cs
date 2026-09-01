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
        bool truncated = false,
        int originalCharCount = 5,
        string? fullPath = null) =>
        new()
        {
            FullPath = fullPath ?? @"C:\work\" + fileName,
            FileName = fileName,
            Kind = kind,
            Text = text,
            Truncated = truncated,
            OriginalCharCount = originalCharCount,
        };

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
    public void BuildAttachedFileBlock_AddsTruncatedAttributesOnlyWhenTruncated()
    {
        var whole = AssistantPromptComposer.BuildAttachedFileBlock(
            [File(text: "12345", truncated: false, originalCharCount: 5)]);
        Assert.DoesNotContain("truncated=", whole);
        Assert.DoesNotContain("note=", whole);

        var cut = AssistantPromptComposer.BuildAttachedFileBlock(
            [File(text: "12345", truncated: true, originalCharCount: 900)]);
        Assert.Contains("truncated=\"true\"", cut);
        Assert.Contains("note=\"Showing the first 5 of 900 characters.\"", cut);
    }

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
