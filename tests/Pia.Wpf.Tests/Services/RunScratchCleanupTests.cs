using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// What an UN-ISOLATED run leaves behind. An isolated one needs none of this — teardown takes the whole
/// workspace — but the provisioning degrade writes <c>.scratch/</c> into the user's own folder, where
/// <c>list_files</c> then hides it from them.
/// </summary>
public sealed class RunScratchCleanupTests : IDisposable
{
    private readonly string _dir;
    private readonly string _files;
    private readonly string _runsBase;

    /// <summary>Either side of <see cref="RunStart"/>: the user's own file, and one the run wrote.</summary>
    private static readonly DateTime RunStart = new(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeRun = RunStart.AddHours(-1);
    private static readonly DateTime DuringRun = RunStart.AddMinutes(1);

    public RunScratchCleanupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaScratchClean_" + Guid.NewGuid().ToString("N"));
        _files = Path.Combine(_dir, "files");
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_files);
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose() => TempPath.Remove(_dir);

    private RunWorkspaceService Build()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _files });
        return new RunWorkspaceService(
            new FakeGitProcessRunner(), settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
    }

    private string WriteAt(string relPath, DateTime lastWriteUtc, string content = "x")
    {
        var full = Path.Combine(_files, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, lastWriteUtc);
        return full;
    }

    private string Scratch => Path.Combine(_files, ".scratch");

    [Fact]
    public async Task TheRunsOwnNotesGo_AndTheFolderWithThem()
    {
        WriteAt(".scratch/notes.md", DuringRun);
        WriteAt(".scratch/deep/more.md", DuringRun);

        await Build().CleanScratchAsync(null, RunStart, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Scratch));
    }

    /// <summary>The guard that makes this safe to run against the user's own folder.</summary>
    [Fact]
    public async Task AFileTheUserAlreadyHad_Survives_AndKeepsTheFolder()
    {
        var theirs = WriteAt(".scratch/kept.md", BeforeRun);
        var ours = WriteAt(".scratch/notes.md", DuringRun);

        await Build().CleanScratchAsync(null, RunStart, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(theirs));
        Assert.False(File.Exists(ours));
        Assert.True(Directory.Exists(Scratch));
    }

    /// <summary>A timestamp nobody set would make every file "written during the run".</summary>
    [Fact]
    public async Task ADefaultTimestamp_DeletesNothing()
    {
        var ours = WriteAt(".scratch/notes.md", DuringRun);

        await Build().CleanScratchAsync(null, default, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(ours));
    }

    /// <summary>Root-level only, exactly like the list/search carve-out the convention already has.</summary>
    [Fact]
    public async Task ANestedScratchFolder_IsTheUsersAndIsLeftAlone()
    {
        var nested = WriteAt("docs/.scratch/notes.md", DuringRun);

        await Build().CleanScratchAsync(null, RunStart, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(nested));
    }

    /// <summary>A chat scoped to a subfolder wrote its notes THERE, so that is the folder to collect.</summary>
    [Fact]
    public async Task AWorkingSubpath_CollectsTheNotesUnderIt_NotTheBaseRoot()
    {
        WriteAt("project/.scratch/notes.md", DuringRun);
        var atBase = WriteAt(".scratch/elsewhere.md", DuringRun);

        await Build().CleanScratchAsync("project", RunStart, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(_files, "project", ".scratch")));
        Assert.True(File.Exists(atBase));
    }

    [Fact]
    public async Task NoScratchFolderAtAll_IsANoOp()
    {
        var deliverable = WriteAt("report.md", DuringRun);

        await Build().CleanScratchAsync(null, RunStart, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(deliverable));
    }

    /// <summary>Nothing outside <c>.scratch/</c> is in scope, whenever it was written.</summary>
    [Fact]
    public async Task TheDeliverableTheRunJustWrote_IsNotTouched()
    {
        var deliverable = WriteAt("report.md", DuringRun);
        WriteAt(".scratch/notes.md", DuringRun);

        await Build().CleanScratchAsync(null, RunStart, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(deliverable));
        Assert.False(Directory.Exists(Scratch));
    }
}
