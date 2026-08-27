using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed class RunWorkspaceWorktreeDeliverableTests : IDisposable
{
    private readonly string _dir;
    private readonly string _source;
    private readonly string _runsBase;
    private readonly FakeGitProcessRunner _runner = new();

    /// <summary>What the first <c>status</c> probe reports the run left behind, as porcelain lines.</summary>
    private string _pending = string.Empty;

    /// <summary>What the post-commit <c>status --ignored</c> probe reports is STILL there.</summary>
    private string _leftover = string.Empty;

    private int _commitExit;

    /// <summary>Exit code for <c>git worktree remove --force</c>; non-zero drives the delete-then-prune fallback.</summary>
    private int _removeExit;

    public RunWorkspaceWorktreeDeliverableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunWt_" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_dir, "files");
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_runsBase);
        File.WriteAllText(Path.Combine(_source, "a.md"), "x");

        _runner.Responder = req =>
        {
            if (IsShowToplevel(req))
                return new GitProcessResult(0, _source.Replace('\\', '/') + "\n", string.Empty, false);
            if (IsStatus(req))
                return new GitProcessResult(0, req.Arguments.Contains("--ignored") ? _leftover : _pending, string.Empty, false);
            if (req.Arguments.Contains("commit"))
                return new GitProcessResult(_commitExit, string.Empty, _commitExit == 0 ? string.Empty : "fatal", false);
            if (IsWorktreeRemove(req))
                return new GitProcessResult(_removeExit, string.Empty, _removeExit == 0 ? string.Empty : "fatal", false);
            return new GitProcessResult(0, string.Empty, string.Empty, false);
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool IsShowToplevel(GitProcessRequest r)
        => r.Arguments.Count >= 2 && r.Arguments[0] == "rev-parse" && r.Arguments[1] == "--show-toplevel";

    private static bool IsStatus(GitProcessRequest r) => r.Arguments.Count > 0 && r.Arguments[0] == "status";

    private static bool IsWorktree(GitProcessRequest r, string verb)
        => r.Arguments.Count > 1 && r.Arguments[0] == "worktree" && r.Arguments[1] == verb;

    private static bool IsWorktreeRemove(GitProcessRequest r) => IsWorktree(r, "remove");

    private RunWorkspaceService Build()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _source });
        return new RunWorkspaceService(_runner, settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
    }

    private string MetadataPath(Guid runId) => Path.Combine(_runsBase, runId + ".workspace.json");

    /// <summary>Worktree mode is reachable because the fake answers <c>--show-toplevel</c> with the source root.</summary>
    private async Task<(RunWorkspaceService Svc, Guid RunId, RunWorkspace Ws)> ProvisionWorktreeAsync()
    {
        var svc = Build();
        var runId = Guid.NewGuid();
        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);
        Assert.Equal(RunWorkspaceMode.Worktree, ws!.Mode);
        return (svc, runId, ws);
    }

    // Nothing else in the build can commit a run's work: the git tools are never granted, so promotion must.
    [Fact]
    public async Task WorktreePromotion_CommitsTheRunsWorkOntoTheRunBranch()
    {
        _pending = "?? report.md\n M notes.md\n";
        var (svc, runId, ws) = await ProvisionWorktreeAsync();

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(RunWorkspaceMode.Worktree, result!.Mode);
        Assert.Equal(2, result.Promoted);            // the two porcelain entries the run left behind
        Assert.False(result.RetainWorkspace);        // the commit took everything, so teardown is safe
        Assert.Equal("pia/run/" + runId, result.BranchName);

        var add = Assert.Single(_runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "add");
        // The run's own working notes stay off the branch — the same pathspec the two status probes carry, or
        // the counts and the retention decision would disagree with what was committed.
        Assert.Equal(new[] { "add", "-A", "--", ".", ":(exclude).scratch" }, add.Arguments);
        Assert.Equal(ws.Root, add.WorkingDirectory);

        Assert.All(
            _runner.Calls.Where(c => c.Arguments.Count > 0 && c.Arguments[0] == "status"),
            c => Assert.Contains(":(exclude).scratch", c.Arguments));

        var commit = Assert.Single(_runner.Calls, c => c.Arguments.Contains("commit"));
        Assert.Equal(ws.Root, commit.WorkingDirectory);
        // Unattended: git refuses to commit without an identity and cannot be prompted, a globally-enabled
        // signing key would block on a passphrase, and repo commit hooks are out-of-band code execution.
        Assert.Contains("user.name=Pia", commit.Arguments);
        Assert.Contains("user.email=pia@pia.invalid", commit.Arguments);
        Assert.Contains("commit.gpgsign=false", commit.Arguments);
        Assert.Contains("--no-verify", commit.Arguments);
        Assert.Contains("pia run " + runId, commit.Arguments);
    }

    [Fact]
    public async Task WorktreePromotion_WithNothingToCommit_CommitsNothingAndReleasesTheWorkspace()
    {
        _pending = string.Empty;
        var (svc, runId, _) = await ProvisionWorktreeAsync();

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Promoted);
        Assert.False(result.RetainWorkspace);
        Assert.DoesNotContain(_runner.Calls, c => c.Arguments.Contains("commit"));
    }

    // A failed commit leaves the work only in the worktree, and a caller reading a non-null result as "finished"
    // would force-remove it.
    [Fact]
    public async Task WorktreePromotion_WhenTheCommitFails_KeepsTheWorkspace()
    {
        _pending = "?? report.md\n";
        _commitExit = 128;
        var (svc, runId, _) = await ProvisionWorktreeAsync();

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.RetainWorkspace);
        Assert.Equal(0, result.Promoted);
        Assert.Equal(1, result.Skipped);
    }

    // The user's own .gitignore applies to files the agent wrote, and neither status --porcelain nor add -A takes
    // them — so a commit can succeed with the deliverable still only on disk.
    [Fact]
    public async Task WorktreePromotion_WhenWorkIsLeftOutsideTheCommit_KeepsTheWorkspace()
    {
        _pending = "?? report.md\n";
        _leftover = "!! build/out.md\n";
        var (svc, runId, _) = await ProvisionWorktreeAsync();

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Promoted);   // the commit did take report.md
        Assert.True(result.RetainWorkspace); // but build/out.md is not on the branch, so the directory stays
        Assert.Contains(_runner.Calls, c => IsStatus(c) && c.Arguments.Contains("--ignored"));
    }

    // Promotion tears the workspace down before the run is marked Completed, and the panel only describes a
    // terminal run — so a describe that required the directory could never answer for a successful run.
    [Fact]
    public async Task ASuccessfulWorktreeRunStillNamesItsBranchAfterTeardown()
    {
        _pending = "?? report.md\n";
        var (svc, runId, ws) = await ProvisionWorktreeAsync();

        var promotion = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);
        Assert.NotNull(promotion);
        Assert.False(promotion!.RetainWorkspace);
        await svc.TearDownAsync(runId, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(ws.Root));

        var outcome = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(RunWorkspaceMode.Worktree, outcome!.Mode);
        Assert.Equal("pia/run/" + runId, outcome.BranchName);
        // The branch is the deliverable, so there are no files left to offer.
        Assert.False(outcome.HasUnpublishedFiles);
    }

    // Naming a branch that received nothing left the user no recovery path; withholding it and offering the
    // files instead makes publishing a real retry of the commit.
    [Fact]
    public async Task AWorktreeRunWhoseCommitFailed_NamesNoBranch_AndOffersTheFilesInstead()
    {
        _pending = "?? report.md\n";
        _commitExit = 128;
        var (svc, runId, ws) = await ProvisionWorktreeAsync();

        var promotion = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);
        Assert.True(promotion!.RetainWorkspace);
        Assert.True(Directory.Exists(ws.Root));   // the premise: the workspace is still the only copy

        var outcome = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(RunWorkspaceMode.Worktree, outcome!.Mode);
        Assert.Null(outcome.BranchName);
        Assert.True(outcome.HasUnpublishedFiles);
    }

    // A failed or cancelled run never promotes, so its document carries no commit stamp either.
    [Fact]
    public async Task AWorktreeRunThatNeverPromoted_NamesNoBranch_AndOffersTheFilesInstead()
    {
        var (svc, runId, ws) = await ProvisionWorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "report.md"), "the run's only output");

        var outcome = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Null(outcome!.BranchName);
        Assert.True(outcome.HasUnpublishedFiles);
    }

    // The stamp means "the directory is gone", so a surviving directory must keep its document for the next sweep
    // to retry; prune has to run even when the remove failed, or the worktree registration leaks permanently.
    [Fact]
    public async Task ATeardownWhoseDirectorySurvives_KeepsTheMetadata_AndStillPrunes()
    {
        var ct = TestContext.Current.CancellationToken;
        _removeExit = 1;
        var (svc, runId, ws) = await ProvisionWorktreeAsync();

        // An exclusive handle inside the workspace is what makes Directory.Delete(recursive) throw part-way through.
        var locked = Path.Combine(ws.Root, "held-open.md");
        using (var handle = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await handle.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            await handle.FlushAsync(ct);

            await svc.TearDownAsync(runId, ct);

            Assert.True(Directory.Exists(ws.Root));           // the premise: the removal really did fail
            Assert.True(File.Exists(MetadataPath(runId)));    // so the record of the registration is kept

            using var doc = JsonDocument.Parse(File.ReadAllText(MetadataPath(runId)));
            Assert.False(doc.RootElement.TryGetProperty("tornDownAtUtc", out var stamp) && stamp.ValueKind != JsonValueKind.Null);
            Assert.Equal(
                SafeFolderPath.Canonicalize(_source),
                SafeFolderPath.Canonicalize(doc.RootElement.GetProperty("mainWorktree").GetString()!));

            Assert.Contains(_runner.Calls, c => IsWorktree(c, "prune"));
        }
    }

    [Fact]
    public async Task TheMetadataSweep_KeepsAFreshlyTornDownStub_AndStillRemovesARealOrphan()
    {
        var (svc, tornDown, _) = await ProvisionWorktreeAsync();
        await svc.TearDownAsync(tornDown, TestContext.Current.CancellationToken);

        var orphan = Guid.NewGuid();
        var orphanWs = await svc.ProvisionAsync(orphan, null, TestContext.Current.CancellationToken);
        Assert.NotNull(orphanWs);
        Directory.Delete(orphanWs!.Root, recursive: true);

        await svc.SweepOrphanMetadataAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(MetadataPath(tornDown)));
        Assert.False(File.Exists(MetadataPath(orphan)));
        // The stub still carries the branch, which is the whole reason it is kept.
        Assert.Equal(
            "pia/run/" + tornDown,
            JsonDocument.Parse(File.ReadAllText(MetadataPath(tornDown))).RootElement.GetProperty("branch").GetString());
    }
}
