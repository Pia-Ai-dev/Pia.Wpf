using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>Staging is where every cap and every refusal lives, and each refusal owes the user a snackbar —
/// a silently dropped file looks like a failed drag.</summary>
public sealed class DroppedFileAttachmentImporterTests : IDisposable
{
    private readonly string _dir;
    private readonly ISnackbarService _snackbar = Substitute.For<ISnackbarService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public DroppedFileAttachmentImporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // Every resolved string is its own key, so an asserted snackbar message names the branch that fired.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Task<DroppedFileAttachmentImporter.StageResult> StageAsync(
        IReadOnlyList<string> paths, params PendingFileAttachment[] alreadyPending) =>
        DroppedFileAttachmentImporter.TryStageAsync(
            paths, alreadyPending, NullLogger.Instance, _snackbar, _loc, TestContext.Current.CancellationToken);

    private static PendingFileAttachment Pending(string fullPath, int chars) => new()
    {
        FullPath = fullPath,
        FileName = Path.GetFileName(fullPath),
        Kind = PendingFileKind.Text,
        Text = new string('x', chars),
        Truncated = false,
        OriginalCharCount = chars,
    };

    private void AssertShown(string messageKey, int times = 1) =>
        _snackbar.Received(times).Show(
            Arg.Any<string>(), messageKey, Arg.Any<ControlAppearance>(), Arg.Any<IconElement?>(),
            Arg.Any<TimeSpan>());

    [Fact]
    public async Task TryStageAsync_StagesATextFile()
    {
        var path = Write("notes.txt", "the quarterly numbers");

        var result = await StageAsync([path]);

        var staged = Assert.Single(result.Staged);
        Assert.Equal(path, staged.FullPath);
        Assert.Equal("notes.txt", staged.FileName);
        Assert.Equal(PendingFileKind.Text, staged.Kind);
        Assert.Equal("the quarterly numbers", staged.Text);
        Assert.False(staged.Truncated);
        Assert.Equal("the quarterly numbers".Length, staged.OriginalCharCount);
        Assert.Empty(result.ImagePaths);
    }

    [Fact]
    public async Task TryStageAsync_SeparatesImagePathsFromStagedFiles()
    {
        var text = Write("notes.txt", "hello");
        var image = Path.Combine(_dir, "shot.png");

        var result = await StageAsync([image, text]);

        Assert.Equal(image, Assert.Single(result.ImagePaths));
        Assert.Equal("notes.txt", Assert.Single(result.Staged).FileName);
    }

    [Fact]
    public async Task TryStageAsync_SkipsADuplicatePath()
    {
        var path = Write("notes.txt", "hello");

        // Cased differently, because a Windows path is the same file either way.
        var result = await StageAsync([path, path.ToUpperInvariant()]);

        Assert.Single(result.Staged);
        AssertShown("Msg_File_DuplicateAttachment");
    }

    [Fact]
    public async Task TryStageAsync_StopsAtMaxPendingFiles()
    {
        var paths = Enumerable.Range(0, DroppedFileAttachmentImporter.MaxPendingFiles + 1)
            .Select(i => Write($"notes{i}.txt", "hello"))
            .ToList();

        var result = await StageAsync(paths);

        Assert.Equal(DroppedFileAttachmentImporter.MaxPendingFiles, result.Staged.Count);
        AssertShown("Msg_File_AttachLimit");
    }

    [Fact]
    public async Task TryStageAsync_TruncatesAFileOverMaxFileChars()
    {
        var chars = DroppedFileAttachmentImporter.MaxFileChars + 500;
        var path = Write("big.txt", new string('a', chars));

        var result = await StageAsync([path]);

        var staged = Assert.Single(result.Staged);
        Assert.Equal(DroppedFileAttachmentImporter.MaxFileChars, staged.Text.Length);
        Assert.True(staged.Truncated);
        Assert.Equal(chars, staged.OriginalCharCount);
        AssertShown("Msg_File_Truncated");
    }

    [Fact]
    public async Task TryStageAsync_TruncatesAgainstTheRunningTotal()
    {
        // Three files each well under the per-file cap: only the running total can cut the third.
        const int each = 15_000;
        var paths = Enumerable.Range(0, 3)
            .Select(i => Write($"part{i}.txt", new string('a', each)))
            .ToList();

        var result = await StageAsync(paths);

        Assert.Equal(3, result.Staged.Count);
        Assert.False(result.Staged[0].Truncated);
        Assert.False(result.Staged[1].Truncated);
        Assert.True(result.Staged[2].Truncated);
        Assert.Equal(DroppedFileAttachmentImporter.MaxTotalChars - (2 * each), result.Staged[2].Text.Length);
        Assert.Equal(each, result.Staged[2].OriginalCharCount);
    }

    [Fact]
    public async Task TryStageAsync_SkipsAnEmptyFile()
    {
        var path = Write("blank.txt", "   \r\n\t ");

        var result = await StageAsync([path]);

        Assert.Empty(result.Staged);
        AssertShown("Msg_File_Empty");
    }

    [Fact]
    public async Task TryStageAsync_SkipsAnUnsupportedFileButKeepsTheRest()
    {
        var pdf = Write("paper.pdf", "%PDF-1.4");
        var text = Write("notes.txt", "hello");

        var result = await StageAsync([pdf, text]);

        Assert.Equal("notes.txt", Assert.Single(result.Staged).FileName);
        AssertShown("Msg_File_UnsupportedAttachment");
    }

    [Fact]
    public async Task TryStageAsync_CountsAlreadyPendingFilesTowardBothCaps()
    {
        var pending = Enumerable.Range(0, DroppedFileAttachmentImporter.MaxPendingFiles - 1)
            .Select(i => Pending($@"C:\work\earlier{i}.txt", 8_750))
            .ToArray();
        var first = Write("next.txt", new string('a', 15_000));
        var second = Write("last.txt", new string('a', 100));

        var result = await StageAsync([first, second], pending);

        // The total cap cut the one file that fitted under the count cap…
        var staged = Assert.Single(result.Staged);
        Assert.Equal("next.txt", staged.FileName);
        Assert.True(staged.Truncated);
        Assert.Equal(
            DroppedFileAttachmentImporter.MaxTotalChars - (pending.Length * 8_750), staged.Text.Length);
        // …and the count cap refused the next one outright.
        AssertShown("Msg_File_AttachLimit");
    }

    [Fact]
    public async Task TryStageAsync_OverTheReadCeiling_SaysTooLargeToAttach()
    {
        var path = Write("huge.txt", new string('a', DroppedFileReader.MaxTextBytes + 1));

        var result = await StageAsync([path]);

        Assert.Empty(result.Staged);
        // The Assistant no longer inserts anything, so Optimize's "too large to insert" is the wrong sentence.
        AssertShown("Msg_File_TooLargeAttachment");
        _loc.DidNotReceive().Format("Msg_File_TooLarge", Arg.Any<object[]>());
    }

    [Fact]
    public async Task TryStageAsync_WithTheCharacterBudgetSpent_NamesTheBudgetNotTheFileCount()
    {
        var pending = new[]
        {
            Pending(@"C:\work\first.txt", DroppedFileAttachmentImporter.MaxFileChars),
            Pending(@"C:\work\second.txt", DroppedFileAttachmentImporter.MaxFileChars),
        };
        var path = Write("third.txt", "still worth reading");

        var result = await StageAsync([path], pending);

        Assert.Empty(result.Staged);
        // Two attached files are nowhere near the five-file ceiling, so the count message would be a lie.
        AssertShown("Msg_File_AttachBudget");
        _loc.Received(1).Format(
            "Msg_File_AttachBudget",
            Arg.Is<object[]>(args => args.Length == 1 && (string)args[0] == "third.txt"));
        _loc.DidNotReceive().Format("Msg_File_AttachLimit", Arg.Any<object[]>());
    }

    [Fact]
    public async Task TryStageAsync_ThreeFilesInOneDrop_RefuseTheThirdOnTheBudget()
    {
        var first = Write("a.txt", new string('a', 25_000));
        var second = Write("b.txt", new string('b', 25_000));
        var third = Write("c.txt", "short enough on its own");

        var result = await StageAsync([first, second, third]);

        // Two truncated files exhaust the 40,000-character message budget; the third has three of the
        // five slots free, so a count message would name a limit nothing here came near.
        Assert.Equal(2, result.Staged.Count);
        AssertShown("Msg_File_AttachBudget");
        _loc.DidNotReceive().Format("Msg_File_AttachLimit", Arg.Any<object[]>());
    }
}
