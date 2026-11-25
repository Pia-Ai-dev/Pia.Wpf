using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Pia.Tests.TestInfrastructure;
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
        TempPath.Remove(_dir);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private int _budgetReads;

    private Task<DroppedFileAttachmentImporter.StageResult> StageAsync(
        IReadOnlyList<string> paths, int availableTokens = AttachmentBudget.MaxTokens,
        params PendingFileAttachment[] alreadyPending) =>
        DroppedFileAttachmentImporter.TryStageAsync(
            paths, alreadyPending,
            () => { _budgetReads++; return Task.FromResult(availableTokens); },
            NullLogger.Instance, _snackbar, _loc, TestContext.Current.CancellationToken);

    private static PendingFileAttachment Pending(string fullPath, string text) => new()
    {
        FullPath = fullPath,
        FileName = Path.GetFileName(fullPath),
        Kind = PendingFileKind.Text,
        Text = text,
    };

    private static string Report(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"line {i} of the report"));

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
        Assert.Null(staged.Cut);
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
        var archive = Write("bundle.zip", "PK");
        var text = Write("notes.txt", "hello");

        var result = await StageAsync([archive, text]);

        Assert.Equal("notes.txt", Assert.Single(result.Staged).FileName);
        AssertShown("Msg_File_UnsupportedAttachment");
    }

    [Fact]
    public async Task TryStageAsync_StagesAPdfAsADocument()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText("the quarterly numbers", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 780), font);
        var path = Path.Combine(_dir, "report.pdf");
        File.WriteAllBytes(path, builder.Build());

        var result = await StageAsync([path]);

        var staged = Assert.Single(result.Staged);
        Assert.Equal("report.pdf", staged.FileName);
        Assert.Equal(PendingFileKind.Document, staged.Kind);
        Assert.Contains("quarterly numbers", staged.Text);
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
    public async Task TryStageAsync_AFileFarOverTheOldCharacterCaps_ArrivesWhole()
    {
        var text = Report(3000);
        Assert.True(text.Length > 40_000);
        var path = Write("long.txt", text);

        var staged = Assert.Single((await StageAsync([path], availableTokens: 32_000)).Staged);

        Assert.Null(staged.Cut);
        Assert.Equal(text, staged.Text);
    }

    [Fact]
    public async Task TryStageAsync_CutsAFileOverTheBudget_AtALineBoundary_AndSaysHowMuchFits()
    {
        var path = Write("big.txt", Report(2000));

        var staged = Assert.Single((await StageAsync([path], availableTokens: 1_000)).Staged);

        var cut = Assert.IsType<AttachmentCut>(staged.Cut);
        Assert.Equal(2000, cut.TotalLines);
        Assert.InRange(cut.ShownLines, 1, 1999);
        Assert.False(cut.EndsMidLine);
        Assert.InRange(cut.ShownPercent, 1, 99);
        Assert.True(TokenEstimator.Estimate(staged.Text) <= 1_000);
        AssertShown("Msg_File_Truncated");
        _loc.Received(1).Format("Msg_File_Truncated",
            Arg.Is<object[]>(a => a.Length == 2 && (string)a[0] == "big.txt" && (int)a[1] == cut.ShownPercent));
    }

    [Fact]
    public async Task TryStageAsync_SharesTheBudgetFairlyAcrossOneDrop()
    {
        var small = Write("small.txt", "a short note");
        var first = Write("first.txt", Report(2000));
        var second = Write("second.txt", Report(2000));

        var result = await StageAsync([first, small, second], availableTokens: 3_000);

        Assert.Equal(3, result.Staged.Count);
        Assert.Null(result.Staged.Single(s => s.FileName == "small.txt").Cut);
        var a = result.Staged.Single(s => s.FileName == "first.txt");
        var b = result.Staged.Single(s => s.FileName == "second.txt");
        Assert.NotNull(a.Cut);
        Assert.NotNull(b.Cut);
        Assert.InRange(Math.Abs(TokenEstimator.Estimate(a.Text) - TokenEstimator.Estimate(b.Text)), 0, 10);
    }

    [Fact]
    public async Task TryStageAsync_CountsOpenChipsAgainstTheBudget()
    {
        var chip = Pending(@"C:\work\earlier.txt", new string('x', 4_000));
        var path = Write("next.txt", Report(2000));

        var staged = Assert.Single((await StageAsync([path], 1_000 + TokenEstimator.Estimate(chip.Text), chip)).Staged);

        Assert.True(TokenEstimator.Estimate(staged.Text) <= 1_000);
    }

    [Fact]
    public async Task TryStageAsync_WithTheBudgetSpent_SkipsTheFile_AndNamesTheBudget()
    {
        var chip = Pending(@"C:\work\first.txt", new string('x', 4_000));
        var path = Write("second.txt", "still worth reading");

        var result = await StageAsync([path], availableTokens: 100, chip);

        Assert.Empty(result.Staged);
        _loc.Received(1).Format("Msg_File_AttachBudget",
            Arg.Is<object[]>(a => a.Length == 1 && (string)a[0] == "second.txt"));
        _loc.DidNotReceive().Format("Msg_File_AttachLimit", Arg.Any<object[]>());
    }

    [Fact]
    public async Task TryStageAsync_ASliverOfBudget_SkipsRatherThanAttachAFewWords()
    {
        var path = Write("big.txt", Report(2000));

        var result = await StageAsync([path], availableTokens: DroppedFileAttachmentImporter.MinShownTokens - 1);

        Assert.Empty(result.Staged);
        AssertShown("Msg_File_AttachBudget");
    }

    [Fact]
    public async Task TryStageAsync_ASingleHugeLine_ArrivesCutMidLine()
    {
        var path = Write("export.json", string.Join(',', Enumerable.Range(0, 50_000)));

        var staged = Assert.Single((await StageAsync([path], availableTokens: 1_000)).Staged);

        Assert.True(staged.Cut!.EndsMidLine);
        Assert.False(string.IsNullOrWhiteSpace(staged.Text));
    }

    [Fact]
    public async Task TryStageAsync_ABlankLineBeforeAHugeLine_ArrivesCutInsideIt_WithoutBlamingTheBudget()
    {
        var path = Write("export.csv", "\n" + string.Join(',', Enumerable.Range(0, 50_000)));

        var staged = Assert.Single((await StageAsync([path], availableTokens: 1_000)).Staged);

        Assert.True(staged.Cut!.EndsMidLine);
        Assert.Equal(2, staged.Cut.ShownLines);
        Assert.True(TokenEstimator.Estimate(staged.Text) > 500);
        _loc.DidNotReceive().Format("Msg_File_AttachBudget", Arg.Any<object[]>());
    }

    [Fact]
    public async Task TryStageAsync_DoesNotAskForTheBudget_WithoutAReadableFile()
    {
        var archive = Write("bundle.zip", "PK");
        var image = Path.Combine(_dir, "shot.png");

        await StageAsync([archive, image]);

        Assert.Equal(0, _budgetReads);
    }
}
