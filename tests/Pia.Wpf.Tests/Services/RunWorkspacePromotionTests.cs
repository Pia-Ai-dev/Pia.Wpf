using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The workspace is built by hand so the mtime-vs-<c>provisionedAtUtc</c> facts are not a race against the
/// clock, and the collection is shared because the real-shape fact mutates the global redirect registry.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class RunWorkspacePromotionTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private readonly string _dir;

    /// <summary>The promotion destination: the source root the run was provisioned FROM.</summary>
    private readonly string _dest;
    private readonly string _runsBase;
    private readonly FakeGitProcessRunner _runner = new();

    /// <summary>Either side of <see cref="Stamp"/>: "the run did not touch this" and "the run wrote this".</summary>
    private static readonly DateTime Stamp = new(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeProvision = Stamp.AddHours(-1);
    private static readonly DateTime AfterProvision = Stamp.AddMinutes(1);

    public RunWorkspacePromotionTests(RedirectedProfileFixture profile)
    {
        _ = profile;
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunProm_" + Guid.NewGuid().ToString("N"));
        _dest = Path.Combine(_dir, "files");
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_dest);
        Directory.CreateDirectory(_runsBase);
    }

    /// <summary>The one fixture that lives at the real shape, under the developer's actual runs root.</summary>
    private Guid? _realShapeRunId;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        if (_realShapeRunId is { } runId)
        {
            try { Directory.Delete(Path.Combine(AssistantWorkspace.RunsRoot, runId.ToString()), recursive: true); }
            catch { /* best effort */ }
            try { File.Delete(Path.Combine(AssistantWorkspace.RunsRoot, runId + ".workspace.json")); }
            catch { /* best effort */ }
        }
    }

    /// <param name="filesFolder">Defaults to the promotion destination; another value models the user relocating
    /// the folder mid-run.</param>
    private RunWorkspaceService Build(string? filesFolder = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = filesFolder ?? _dest });
        return new RunWorkspaceService(_runner, settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
    }

    private string RunRoot(Guid runId) => Path.Combine(_runsBase, runId.ToString());

    private string MetadataPath(Guid runId) => Path.Combine(_runsBase, runId + ".workspace.json");

    /// <summary>camelCase and <c>v:1</c> — the exact wire shape, because a promotion after a restart reads a document another build wrote.</summary>
    private void WriteMetadata(Guid runId, RunWorkspaceMode mode, DateTime provisionedAtUtc, string? branch = null)
    {
        Directory.CreateDirectory(RunRoot(runId));
        var doc = new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["mode"] = mode.ToString(),
            ["sourceRoot"] = SafeFolderPath.Canonicalize(_dest),
            ["mainWorktree"] = mode == RunWorkspaceMode.Worktree ? SafeFolderPath.Canonicalize(_dest) : null,
            ["branch"] = branch,
            ["provisionedAtUtc"] = provisionedAtUtc,
            ["degraded"] = false,
        };
        File.WriteAllText(MetadataPath(runId), JsonSerializer.Serialize(doc));
    }

    private static void Write(string root, string relative, string content, DateTime lastWriteUtc)
    {
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, lastWriteUtc);
    }

    /// <summary>
    /// The promote set is decided by mtime against <c>provisionedAtUtc</c>; the user-deleted third file is the
    /// discriminator, because without the mtime rule promotion would resurrect it.
    /// </summary>
    [Fact]
    public async Task Promote_CopiesOnlyWhatTheRunWrote()
    {
        var runId = Guid.NewGuid();
        WriteMetadata(runId, RunWorkspaceMode.Copy, Stamp);
        Write(_dest, "a.md", "alpha", BeforeProvision);
        Write(RunRoot(runId), "a.md", "alpha", BeforeProvision);   // copied in, untouched
        Write(RunRoot(runId), "new.md", "written", AfterProvision); // the deliverable
        // Copied in, untouched by the run, and deleted at the destination by the user meanwhile.
        Write(RunRoot(runId), "user-deleted.md", "gone", BeforeProvision);

        var result = await Build().PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Promoted);
        Assert.Equal("written", File.ReadAllText(Path.Combine(_dest, "new.md")));
        // Asserted on the TIMESTAMP, because identical content would hide a copy that happened anyway.
        Assert.Equal(BeforeProvision, File.GetLastWriteTimeUtc(Path.Combine(_dest, "a.md")));
        Assert.False(File.Exists(Path.Combine(_dest, "user-deleted.md")));
    }

    /// <summary>Same size and SHA256, so copying would only churn the mtime for no change in content.</summary>
    [Fact]
    public async Task Promote_SkipsAByteIdenticalDestination()
    {
        var runId = Guid.NewGuid();
        WriteMetadata(runId, RunWorkspaceMode.Copy, Stamp);
        Write(_dest, "same.md", "identical", BeforeProvision);
        Write(RunRoot(runId), "same.md", "identical", AfterProvision); // touched by the run, same bytes

        var result = await Build().PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Promoted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(BeforeProvision, File.GetLastWriteTimeUtc(Path.Combine(_dest, "same.md")));
    }

    /// <summary>The destination changed WHILE the run was working, so an unattended run does not get to overwrite it.</summary>
    [Fact]
    public async Task Promote_NeverOverwritesAFileTheUserChangedDuringTheRun()
    {
        var runId = Guid.NewGuid();
        WriteMetadata(runId, RunWorkspaceMode.Copy, Stamp);
        Write(_dest, "notes.md", "the user's newer edit", AfterProvision);
        Write(RunRoot(runId), "notes.md", "the run's version", AfterProvision);

        var result = await Build().PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Conflicts);
        Assert.Equal(0, result.Promoted);
        Assert.Equal("the user's newer edit", File.ReadAllText(Path.Combine(_dest, "notes.md")));

        // The run's version now exists ONLY in the workspace, which the caller tears down on a non-null result,
        // so the caller has to be told to keep it.
        Assert.True(result.RetainWorkspace);
        Assert.Equal("the run's version", File.ReadAllText(Path.Combine(RunRoot(runId), "notes.md")));
    }

    /// <summary>
    /// The promotion has no channel to the ViewModel — a different DI scope, and the panel is often opened from
    /// history long after — so the conflict count is recorded on the metadata document instead.
    /// </summary>
    [Fact]
    public async Task Promote_RecordsItsConflictCount_SoTheDescribeCanAnnounceItWithoutAPublishClick()
    {
        var ct = TestContext.Current.CancellationToken;
        var conflicted = Guid.NewGuid();
        WriteMetadata(conflicted, RunWorkspaceMode.Copy, Stamp);
        Write(_dest, "notes.md", "the user's newer edit", AfterProvision);
        Write(RunRoot(conflicted), "notes.md", "the run's version", AfterProvision);

        var svc = Build();
        var result = await svc.PromoteAsync(conflicted, ct);
        Assert.Equal(1, result!.Conflicts);

        var outcome = await svc.DescribeAsync(conflicted, ct);

        Assert.NotNull(outcome);
        Assert.Equal(1, outcome!.Conflicts);
        // The count is only worth announcing while the run's version of that file is still recoverable.
        Assert.True(outcome.HasUnpublishedFiles);

        // Control: an ordinary promotion records no count. A clean copy-mode workspace is torn down at
        // promotion, so the describe below is the same call the panel makes.
        var clean = Guid.NewGuid();
        WriteMetadata(clean, RunWorkspaceMode.Copy, Stamp);
        Write(RunRoot(clean), "new.md", "written", AfterProvision);
        await svc.PromoteAsync(clean, ct);

        var cleanOutcome = await svc.DescribeAsync(clean, ct);
        Assert.Equal(0, cleanOutcome?.Conflicts ?? 0);
    }

    /// <summary>Promote is not sync: a deletion inside the workspace is never propagated.</summary>
    [Fact]
    public async Task Promote_NeverDeletesAtTheDestination()
    {
        var runId = Guid.NewGuid();
        WriteMetadata(runId, RunWorkspaceMode.Copy, Stamp);
        Write(_dest, "keep.md", "still here", BeforeProvision);
        // The workspace has no keep.md at all — the run deleted its copy.
        Write(RunRoot(runId), "new.md", "written", AfterProvision);

        await Build().PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_dest, "keep.md")));
        Assert.Equal("still here", File.ReadAllText(Path.Combine(_dest, "keep.md")));
    }

    /// <summary>In worktree mode the BRANCH is the deliverable, which keeps conflict handling out of the unattended path.</summary>
    [Fact]
    public async Task Promote_WorktreeMode_CopiesNothing_AndReportsTheBranch()
    {
        var runId = Guid.NewGuid();
        var branch = "pia/run/" + runId;
        WriteMetadata(runId, RunWorkspaceMode.Worktree, Stamp, branch);
        Write(RunRoot(runId), "committed.md", "on the branch", AfterProvision);

        var result = await Build().PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(RunWorkspaceMode.Worktree, result!.Mode);
        Assert.Equal(0, result.Promoted);
        Assert.Equal(branch, result.BranchName);
        Assert.False(File.Exists(Path.Combine(_dest, "committed.md")));
    }

    /// <summary>Restrictive degrade: promoting nothing is recoverable, overwriting from a workspace we cannot reason about is not.</summary>
    [Fact]
    public async Task Promote_WithUnreadableMetadata_PromotesNothing_AndKeepsTheWorkspace()
    {
        var runId = Guid.NewGuid();
        Directory.CreateDirectory(RunRoot(runId));
        Write(RunRoot(runId), "new.md", "written", AfterProvision);
        File.WriteAllText(MetadataPath(runId), "{ not json at all");

        var result = await Build().PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(File.Exists(Path.Combine(RunRoot(runId), "new.md")));
        Assert.False(File.Exists(Path.Combine(_dest, "new.md")));
    }

    /// <summary>Re-anchoring onto a folder the run never saw is not a repair — it writes the output somewhere nobody asked for.</summary>
    [Fact]
    public async Task Promote_WhenTheSourceRootNoLongerResolvesInsideTheAssistantFolder_IsSkipped()
    {
        var runId = Guid.NewGuid();
        WriteMetadata(runId, RunWorkspaceMode.Copy, Stamp);
        Write(RunRoot(runId), "new.md", "written", AfterProvision);

        var relocated = Path.Combine(_dir, "files-relocated");
        Directory.CreateDirectory(relocated);

        var result = await Build(filesFolder: relocated).PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(Path.Combine(_dest, "new.md")));
        Assert.False(File.Exists(Path.Combine(relocated, "new.md")));
        Assert.True(File.Exists(Path.Combine(RunRoot(runId), "new.md")));
    }

    /// <summary>
    /// The only fact at the real shape: a production workspace sits inside the tree <c>SensitivePathGuard</c>
    /// blocks, and without the carve-out the promote walk returns nothing with no exception at all.
    /// </summary>
    [Fact]
    public async Task Promote_AtTheRealRunsRootShape_ActuallyCopiesFilesOut()
    {
        var runId = Guid.NewGuid();
        _realShapeRunId = runId;
        var realRunsBase = AssistantWorkspace.RunsRoot;
        var runRoot = Path.Combine(realRunsBase, runId.ToString());
        Directory.CreateDirectory(runRoot);

        // The destination stays in temp — the guard governs where the run's files ARE, not where they go.
        var doc = new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["mode"] = nameof(RunWorkspaceMode.Copy),
            ["sourceRoot"] = SafeFolderPath.Canonicalize(_dest),
            ["mainWorktree"] = null,
            ["branch"] = null,
            ["provisionedAtUtc"] = Stamp,
            ["degraded"] = false,
        };
        File.WriteAllText(Path.Combine(realRunsBase, runId + ".workspace.json"), JsonSerializer.Serialize(doc));
        Write(runRoot, "deliverable.md", "written by the run", AfterProvision);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _dest });
        var svc = new RunWorkspaceService(_runner, settings, NullLogger<RunWorkspaceService>.Instance, realRunsBase);

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Promoted);
        Assert.Equal("written by the run", File.ReadAllText(Path.Combine(_dest, "deliverable.md")));
    }

    /// <summary>A <c>.git</c> the model created is ignore-pruned on the way out, so it must not by itself raise the publish offer.</summary>
    [Fact]
    public async Task Describe_DoesNotOfferToPublishFilesThatPromotionWouldPrune()
    {
        var runId = Guid.NewGuid();
        WriteMetadata(runId, RunWorkspaceMode.Copy, Stamp);
        Write(RunRoot(runId), Path.Combine(".git", "HEAD"), "ref: refs/heads/x", AfterProvision);
        Write(RunRoot(runId), "a.md", "copied in", BeforeProvision);
        var svc = Build();

        var pruned = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);
        Assert.NotNull(pruned);
        Assert.False(pruned!.HasUnpublishedFiles);

        // Positive control: a real deliverable DOES raise the offer.
        Write(RunRoot(runId), "deliverable.md", "written", AfterProvision);
        var offered = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);
        Assert.NotNull(offered);
        Assert.True(offered!.HasUnpublishedFiles);
    }
}
