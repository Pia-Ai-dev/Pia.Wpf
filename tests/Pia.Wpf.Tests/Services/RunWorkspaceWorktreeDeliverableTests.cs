using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Phase 3 fix pass, closing Batch 06's Lens A finding 1 / Lens B finding 2 (worktree mode destroyed the run's
/// output at teardown) and Lens A finding 2 / Lens B finding 1 (the "output is on branch X" line could never
/// render for a SUCCESSFUL worktree run). Both defects lived in the interaction between three methods, so every
/// fact here drives the REAL <see cref="RunWorkspaceService"/> through the production sequence —
/// provision → promote → teardown → describe — rather than injecting an outcome. That is the point: the
/// pre-existing coverage asserted a describe result the real service could not produce for a completed run.
/// <para>
/// Git-free: <see cref="FakeGitProcessRunner"/> answers every invocation with a canned result and records it,
/// so the command SHAPES (identity overrides, <c>--no-verify</c>, the two status probes) are assertable without
/// a repository. The runs base is a per-test temp directory — these facts delete workspaces.
/// </para>
/// </summary>
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

    private RunWorkspaceService Build()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _source });
        return new RunWorkspaceService(_runner, settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
    }

    private string MetadataPath(Guid runId) => Path.Combine(_runsBase, runId + ".workspace.json");

    /// <summary>Provisions in worktree mode and returns the workspace. The gate passes because
    /// <c>--show-toplevel</c> reports the source root and every other invocation exits 0.</summary>
    private async Task<(RunWorkspaceService Svc, Guid RunId, RunWorkspace Ws)> ProvisionWorktreeAsync()
    {
        var svc = Build();
        var runId = Guid.NewGuid();
        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);
        Assert.Equal(RunWorkspaceMode.Worktree, ws!.Mode);
        return (svc, runId, ws);
    }

    /// <summary>
    /// <b>REGRESSION</b> — the must-fix itself. Nothing else in the build can commit a run's work: the default
    /// grant set is <c>{write_file}</c> and every autonomy preset excludes <c>ToolClass.Git</c>, so the model's
    /// own <c>git_commit</c> is refused as not-granted. Before this fix the promotion copied nothing, reported
    /// success, and the caller then ran <c>git worktree remove --force</c> — so the branch stayed
    /// byte-identical to the base commit and the deliverable existed nowhere. Neutralization: delete the
    /// <c>add</c>/<c>commit</c> pair from <c>CommitToRunBranchAsync</c> → <c>Promoted</c> is 0 and no commit is
    /// recorded.
    /// </summary>
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
        Assert.Equal(new[] { "add", "-A" }, add.Arguments);
        Assert.Equal(ws.Root, add.WorkingDirectory);

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

    /// <summary>
    /// <b>GUARD</b> on the empty case: a run that wrote nothing must not produce an empty commit, and its
    /// workspace is still safe to remove. Cannot red on the fix above (there is nothing to lose), which is why
    /// it is labelled a guard.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b>. A commit that FAILS (an unwritable index, a git that vanished, a hook the
    /// <c>-c</c> overrides did not cover) leaves the work only in the worktree directory, so the promotion must
    /// tell the caller to keep it. Without <c>RetainWorkspace</c> the caller reads a non-null result as
    /// "finished" and force-removes the worktree — the same data loss, one arm further along.
    /// Neutralization: return <c>RetainWorkspace: false</c> from the commit-failure arm → red.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b> on the hole a "did the commit succeed?" test would not see: the run root is a checkout
    /// of the USER's repository, so the user's own <c>.gitignore</c> applies to files the agent wrote.
    /// <c>status --porcelain</c> does not list those and <c>add -A</c> will not take them, so a commit can
    /// succeed while the deliverable is still only on disk. The post-commit <c>--ignored</c> probe is what
    /// catches it. Neutralization: drop the second status probe (or its <c>--ignored</c> flag) → red.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b>, and the fact the pre-existing coverage could not be: after the PRODUCTION sequence
    /// (promote, then tear down, then the panel's terminal describe) a worktree run can still be told which
    /// branch its output is on. Promotion tears the workspace down BEFORE the run is marked Completed (B8), and
    /// the panel only describes a TERMINAL run — so requiring the directory made D5b's branch line renderable
    /// for a failed worktree run and never for a successful one, the exact inverse of what it exists for.
    /// Neutralization: delete the torn-down stamp from <c>TearDownAsync</c> → describe returns null → red.
    /// </summary>
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
        // The branch is the deliverable, so there are no FILES to offer — a publish button here would promise
        // something worktree mode never has (plan D5b).
        Assert.False(outcome.HasUnpublishedFiles);
    }

    /// <summary>
    /// <b>GUARD</b>. The stub is a record, not an orphan: the metadata sweep must not delete it out from under
    /// the panel on the next startup. Non-vacuity is the second document — a worktree whose directory vanished
    /// WITHOUT a teardown, i.e. the crash shape the sweep exists for — which is still removed in the same pass.
    /// </summary>
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
        // And the stub still carries the branch, which is the whole reason it is kept.
        Assert.Equal(
            "pia/run/" + tornDown,
            JsonDocument.Parse(File.ReadAllText(MetadataPath(tornDown))).RootElement.GetProperty("branch").GetString());
    }
}
