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

    /// <summary>Exit code for <c>git worktree remove --force</c> — non-zero drives the fallback half of plan R5
    /// (delete the directory ourselves, then PRUNE the registration).</summary>
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
    /// <b>REGRESSION</b> (Phase 3 consolidation pass, the first of the two items `3b66603` opened). A run-branch
    /// commit that FAILED retains the workspace, so the metadata document is intact and un-stamped — and the
    /// describe used to fall through to the directory-exists arm and answer
    /// <c>(Worktree, meta.Branch, HasUnpublishedFiles: false)</c>. The panel then named a branch that received
    /// nothing, worktree mode offered no publish button, and the UI showed no recovery path at all while the
    /// files really were in the workspace for the retention window. With the commit RECORDED at promotion, the
    /// describe does the opposite on that arm: it withholds the branch and OFFERS the files, and publishing them
    /// re-runs <c>CommitToRunBranchAsync</c> — a real retry, not a dead end.
    /// <para>
    /// Neutralization: drop the <c>committed</c> test from the worktree arm of <c>DescribeAsync</c> → the branch
    /// is named again and the offer disappears → red. The non-vacuity control is
    /// <see cref="ASuccessfulWorktreeRunStillNamesItsBranchAfterTeardown"/>: the identical describe on a run
    /// whose commit DID land still names the branch, so this is about the recorded outcome and not about a
    /// branch line that stopped rendering.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b>, the same lie on the other reachable arm. A FAILED or CANCELLED worktree run never
    /// promotes at all (plan D3: only a cleanly drained run promotes automatically), so its document has no
    /// commit stamp either — and the pre-fix describe named its branch just as confidently. This arm was never
    /// filed as a finding because Lens A 2 read the branch line as *intended* for a failed run; it is the same
    /// empty branch.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 consolidation pass — Batch 06 Lens A finding 4's other half). Teardown now
    /// reports whether the directory is actually GONE, and a worktree whose directory SURVIVED keeps its metadata
    /// document: that document is the only record of which repository holds the
    /// <c>.git/worktrees/&lt;id&gt;</c> registration, and it must not be stamped as torn down either — the stamp
    /// means "the directory is gone", and the sweep reads it that way. Keeping it is what lets the next startup
    /// sweep re-enter <c>TearDownAsync</c> through the same document and retry.
    /// <para>
    /// Both removal steps are made to fail the way the finding describes: <c>git worktree remove --force</c>
    /// exits non-zero and an open file handle inside the workspace defeats the recursive delete — precisely the
    /// state a teardown racing a live writer produces.
    /// </para>
    /// <para>
    /// <b>The prune assertion is LOAD-BEARING.</b> Lens A 4's refutation rests on <c>git worktree prune</c>
    /// running UNCONDITIONALLY when the remove failed, whether or not our own delete then worked — prune reclaims
    /// the registration from live git state once the worktree's <c>.git</c> pointer file is gone. A later
    /// simplification that folds that call into a success arm re-opens the permanent leak, and this assertion is
    /// what catches it. Neutralization of the fact itself: delete the surviving-directory early return from
    /// <c>TearDownAsync</c> → the document is stamped and the retry has nothing to read → red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATeardownWhoseDirectorySurvives_KeepsTheMetadata_AndStillPrunes()
    {
        var ct = TestContext.Current.CancellationToken;
        _removeExit = 1;
        var (svc, runId, ws) = await ProvisionWorktreeAsync();

        // A live writer, in the only shape a test can hold one: an exclusive handle on a file inside the
        // workspace, which is what makes Directory.Delete(recursive) throw part-way through.
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
