using System.IO;
using System.Text;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed class KbManagerLocalFilesTests : IDisposable
{
    // A private parent, so the "outside" file a traversal test aims at never lands in the shared temp folder.
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "pia-kbm-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public KbManagerLocalFilesTests()
    {
        _root = Path.Combine(_parent, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TempPath.Remove(_parent);

    private string Write(string relative, string text) => WriteBytes(relative, Encoding.UTF8.GetBytes(text));

    private string WriteBytes(string relative, byte[] bytes)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    [Theory]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("notes.markdown", "text/markdown")]
    [InlineData("NOTES.TXT", "text/plain")]
    [InlineData("notes.docx", null)]
    [InlineData("notes", null)]
    public void ContentTypeFor_AcceptsOnlyTextAndMarkdown(string path, string? expected)
    {
        Assert.Equal(expected, KbManagerLocalFiles.ContentTypeFor(path));
    }

    [Fact]
    public void TryRead_AMarkdownFileInside_ReturnsItsText()
    {
        Write("docs/guide.md", "# Guide");

        Assert.True(KbManagerLocalFiles.TryRead(_root, "docs/guide.md", out var file, out _));
        Assert.Equal("# Guide", file.Content);
        Assert.Equal(KbManagerLimits.Markdown, file.ContentType);
        Assert.Equal("docs/guide.md", file.RelativePath);
    }

    [Theory]
    [InlineData("..\\outside.md")]
    [InlineData("../outside.md")]
    [InlineData("docs/../../outside.md")]
    public void TryRead_TraversalOutOfTheRoot_IsRefused(string path)
    {
        File.WriteAllText(Path.Combine(_parent, "outside.md"), "secret");

        Assert.False(KbManagerLocalFiles.TryRead(_root, path, out _, out var error));
        Assert.Contains("outside the assistant files folder", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_AnAbsolutePathElsewhere_IsRefused()
    {
        var elsewhere = Path.Combine(_parent, "elsewhere.md");
        File.WriteAllText(elsewhere, "secret");

        Assert.False(KbManagerLocalFiles.TryRead(_root, elsewhere, out _, out _));
    }

    [Theory]
    [InlineData("bin/notes.md")]
    [InlineData(".git/notes.md")]
    [InlineData("node_modules/pkg/readme.md")]
    public void TryRead_AFileUnderAnIgnoredFolder_IsRefused(string path)
    {
        Write(path, "x");

        Assert.False(KbManagerLocalFiles.TryRead(_root, path, out _, out var error));
        Assert.Contains("ignore rules", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_AFileAnotherProcessHoldsOpen_IsARefusalNotAnException()
    {
        var full = Write("locked.md", "# Text");
        using var hold = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(KbManagerLocalFiles.TryRead(_root, "locked.md", out _, out var error));
        Assert.Equal("That file cannot be read right now; nothing was sent.", error);
    }

    [Fact]
    public void TryRead_AFileThePiaignoreExcludes_IsRefused()
    {
        Write(".piaignore", "private.md\n");
        Write("private.md", "x");

        Assert.False(KbManagerLocalFiles.TryRead(_root, "private.md", out _, out _));
    }

    [Fact]
    public void TryRead_AnotherExtension_IsRefused()
    {
        Write("report.docx", "x");

        Assert.False(KbManagerLocalFiles.TryRead(_root, "report.docx", out _, out var error));
        Assert.Contains(".txt, .md and .markdown", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_OverTenMegabytes_IsRefused()
    {
        WriteBytes("big.txt", Enumerable.Repeat((byte)'a', KbManagerLimits.MaxContentBytes + 1).ToArray());

        Assert.False(KbManagerLocalFiles.TryRead(_root, "big.txt", out _, out var error));
        Assert.Contains("at most", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_ExactlyTenMegabytes_IsAccepted()
    {
        WriteBytes("edge.txt", Enumerable.Repeat((byte)'a', KbManagerLimits.MaxContentBytes).ToArray());

        Assert.True(KbManagerLocalFiles.TryRead(_root, "edge.txt", out var file, out _));
        Assert.Equal(KbManagerLimits.MaxContentBytes, file.SizeBytes);
    }

    [Fact]
    public void TryRead_InvalidUtf8_IsRefused()
    {
        WriteBytes("broken.txt", [0x48, 0xC3, 0x28, 0x49]);

        Assert.False(KbManagerLocalFiles.TryRead(_root, "broken.txt", out _, out var error));
        Assert.Contains("UTF-8", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_AByteOrderMark_IsStripped()
    {
        WriteBytes("bom.txt", [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);

        Assert.True(KbManagerLocalFiles.TryRead(_root, "bom.txt", out var file, out _));
        Assert.Equal("hi", file.Content);
    }

    [Fact]
    public void TryRead_WhitespaceOnly_IsRefused()
    {
        Write("blank.md", "  \r\n\t ");

        Assert.False(KbManagerLocalFiles.TryRead(_root, "blank.md", out _, out var error));
        Assert.Contains("empty", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_AMissingFile_SaysSo()
    {
        Assert.False(KbManagerLocalFiles.TryRead(_root, "missing.md", out _, out var error));
        Assert.Contains("not found", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Q3/Q4: plan*?", "Q3_Q4_ plan__")]
    [InlineData("CON", "_CON")]
    [InlineData("...", "document")]
    [InlineData("guide.md", "guide")]
    [InlineData("  spaced  ", "spaced")]
    public void SafeFileName_ProducesAUsableName(string title, string expected)
    {
        Assert.Equal(expected, KbManagerLocalFiles.SafeFileName(title));
    }

    [Fact]
    public void SafeFileName_CapsTheLength()
    {
        Assert.Equal(120, KbManagerLocalFiles.SafeFileName(new string('x', 300)).Length);
    }

    [Fact]
    public void TrySaveNew_DefaultsToTheRootWithTheTitleAsName()
    {
        Assert.True(KbManagerLocalFiles.TrySaveNew(_root, null, "Onboarding", KbManagerLimits.Markdown, "# Hi", out var saved, out _));

        Assert.Equal("Onboarding.md", saved);
        Assert.Equal("# Hi", File.ReadAllText(Path.Combine(_root, "Onboarding.md")));
    }

    [Fact]
    public void TrySaveNew_NameTaken_WritesNumberedCopyAndLeavesTheOriginal()
    {
        var existing = Write("Onboarding.md", "mine");

        Assert.True(KbManagerLocalFiles.TrySaveNew(_root, null, "Onboarding", KbManagerLimits.Markdown, "theirs", out var saved, out _));

        Assert.Equal("Onboarding (1).md", saved);
        Assert.Equal("mine", File.ReadAllText(existing));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_root, "Onboarding (1).md")));
    }

    [Fact]
    public void TrySaveNew_IntoAFolder_UsesTheTitleThere()
    {
        Directory.CreateDirectory(Path.Combine(_root, "exports"));

        Assert.True(KbManagerLocalFiles.TrySaveNew(_root, "exports", "Plan", KbManagerLimits.PlainText, "x", out var saved, out _));

        Assert.Equal("exports/Plan.txt", saved);
    }

    [Fact]
    public void TrySaveNew_AnExplicitFileName_IsKept()
    {
        Assert.True(KbManagerLocalFiles.TrySaveNew(_root, "copy.md", "Plan", KbManagerLimits.Markdown, "x", out var saved, out _));

        Assert.Equal("copy.md", saved);
    }

    [Fact]
    public void TrySaveNew_OutsideTheRoot_IsRefused()
    {
        Assert.False(KbManagerLocalFiles.TrySaveNew(_root, "../escape.md", "Plan", KbManagerLimits.Markdown, "x", out _, out _));
    }

    [Fact]
    public void TrySaveNew_AnExplicitNameWithAnotherExtension_IsRefused()
    {
        Assert.False(KbManagerLocalFiles.TrySaveNew(_root, "copy.exe", "Plan", KbManagerLimits.Markdown, "x", out _, out _));
    }

    [Theory]
    [InlineData("README.md:hidden.md")]
    [InlineData(".env:x.md")]
    [InlineData("docs:stream.txt")]
    public void TrySaveNew_AnAlternateDataStreamPath_IsRefusedAndCreatesNothing(string path)
    {
        var host = Write(path.Split(':')[0], "host");

        Assert.False(KbManagerLocalFiles.TrySaveNew(_root, path, "Plan", KbManagerLimits.Markdown, "x", out _, out var error));

        Assert.Contains("alternate data stream", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, path)));
        Assert.Equal("host", File.ReadAllText(host));
    }

    [Fact]
    public void TryRead_AnAlternateDataStreamPath_IsRefused()
    {
        Write("README.md", "host");

        Assert.False(KbManagerLocalFiles.TryRead(_root, "README.md:hidden.md", out _, out var error));
        Assert.Contains("alternate data stream", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySaveNew_AnAbsolutePathInsideTheRoot_IsStillAccepted()
    {
        Assert.True(KbManagerLocalFiles.TrySaveNew(
            _root, Path.Combine(_root, "abs.md"), "Plan", KbManagerLimits.Markdown, "x", out var saved, out _));

        Assert.Equal("abs.md", saved);
    }

    // Windows 10 still maps a device name to the device when an extension follows it.
    [Theory]
    [InlineData("NUL.report", "_NUL.report")]
    [InlineData("Aux. rules", "_Aux. rules")]
    [InlineData("con .notes", "_con .notes")]
    [InlineData("COM¹", "_COM¹")]
    [InlineData("LPT³.draft", "_LPT³.draft")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("conout$.log", "_conout$.log")]
    [InlineData("Console", "Console")]
    [InlineData("COM10", "COM10")]
    public void SafeFileName_ADeviceNameBeforeAnyDot_IsEscaped(string title, string expected)
    {
        Assert.Equal(expected, KbManagerLocalFiles.SafeFileName(title));
    }

    [Fact]
    public void SafeFileName_TheLengthCap_NeverLeavesHalfASurrogatePair()
    {
        var name = KbManagerLocalFiles.SafeFileName("a" + string.Concat(Enumerable.Repeat("😀", 150)));

        Assert.False(char.IsHighSurrogate(name[^1]));
        Assert.True(name.Length <= 120);
    }

    [Fact]
    public void TrySaveNew_AFolderHoldsTheName_WritesANumberedCopy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Plan.md"));

        Assert.True(KbManagerLocalFiles.TrySaveNew(_root, null, "Plan", KbManagerLimits.Markdown, "x", out var saved, out _));

        Assert.Equal("Plan (1).md", saved);
    }

    [Theory]
    [InlineData("bin/")]
    [InlineData("bin/copy.md")]
    [InlineData(".git/copy.md")]
    public void TrySaveNew_IntoAnIgnoredFolder_IsRefused(string path)
    {
        Assert.False(KbManagerLocalFiles.TrySaveNew(_root, path, "Plan", KbManagerLimits.Markdown, "x", out _, out _));
        Assert.False(Directory.Exists(Path.Combine(_root, "bin")) && Directory.EnumerateFiles(Path.Combine(_root, "bin")).Any());
    }

    [Fact]
    public void TrySaveNew_AJunctionOutOfTheRoot_IsRefusedAndWritesNothingOutside()
    {
        var outside = Path.Combine(_parent, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "exports");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using (var proc = System.Diagnostics.Process.Start(psi)!) proc.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed");

        Assert.False(KbManagerLocalFiles.TrySaveNew(_root, "exports", "Plan", KbManagerLimits.Markdown, "x", out _, out _));
        Assert.False(KbManagerLocalFiles.TryRead(_root, "exports/Plan.md", out _, out _));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
}
