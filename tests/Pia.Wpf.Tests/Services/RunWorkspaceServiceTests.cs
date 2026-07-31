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
/// Batch 06 G3: the two workspace provisioning modes (a git worktree when the source root is a repository we
/// may touch, else a bounded copy of the source tree — plan D5), the degrade-to-copy fault list, and the
/// SYMMETRIC teardown each mode needs. Git-free throughout: <see cref="FakeGitProcessRunner"/> answers
/// <c>rev-parse</c>/<c>worktree</c> with canned results and records every request, so the gate and the
/// teardown command shapes are asserted without a real repository.
/// <para>
/// The runs base is a per-test temp directory, never the real <c>%LOCALAPPDATA%\Pia\runs</c>: these tests
/// DELETE workspaces and prune metadata.
/// </para>
/// </summary>
public sealed class RunWorkspaceServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _source;
    private readonly string _runsBase;
    private readonly FakeGitProcessRunner _runner = new();

    public RunWorkspaceServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunWs_" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_dir, "files");
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private RunWorkspaceService Build() => BuildFor(_source);

    /// <summary>The "no usable assistant files folder" shape, which <see cref="Build"/> deliberately cannot
    /// express: a <c>filesFolder</c> parameter defaulting to the source root coalesces a null argument back to
    /// the source and the degrade goes untested (it did — that is what made the guard below pass vacuously).</summary>
    private RunWorkspaceService BuildWithoutFilesFolder() => BuildFor(null);

    private RunWorkspaceService BuildFor(string? filesFolder)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = filesFolder });
        return new RunWorkspaceService(_runner, settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
    }

    private void WriteSource(string relative, string content = "x")
    {
        var full = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string MetadataPath(Guid runId) => Path.Combine(_runsBase, runId + ".workspace.json");

    private JsonElement ReadMetadata(Guid runId)
        => JsonDocument.Parse(File.ReadAllText(MetadataPath(runId))).RootElement;

    private static bool IsShowToplevel(GitProcessRequest r)
        => r.Arguments.Count >= 2 && r.Arguments[0] == "rev-parse" && r.Arguments[1] == "--show-toplevel";

    private static bool IsVerifyHead(GitProcessRequest r)
        => r.Arguments.Count >= 2 && r.Arguments[0] == "rev-parse" && r.Arguments[1] == "--verify";

    private static bool IsWorktree(GitProcessRequest r, string sub)
        => r.Arguments.Count >= 2 && r.Arguments[0] == "worktree" && r.Arguments[1] == sub;

    private static GitProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty, TimedOut: false);
    private static GitProcessResult Exit(int code) => new(code, string.Empty, "fatal", TimedOut: false);

    /// <summary>Arms the runner so the whole worktree gate passes for a repository at
    /// <paramref name="toplevel"/>: <c>--show-toplevel</c> reports it, <c>--verify HEAD</c> exits 0 (the repo
    /// has a commit — a worktree can only start from one), and every <c>worktree</c> call succeeds.</summary>
    private void ArrangeRepoWithCommits(string toplevel) => _runner.Responder = req =>
        IsShowToplevel(req) ? Ok(toplevel.Replace('\\', '/') + "\n") : Ok();

    // ---- copy mode ----

    /// <summary>
    /// T-G3-1, <b>REGRESSION</b>. Copy mode copies the source tree IN. This is B6's whole justification as a
    /// fact: an unattended run reads the user's existing files through the same tool set it writes with, so an
    /// empty workspace silently breaks "summarise notes.md" — a functional regression, not a nicety.
    /// </summary>
    [Fact]
    public async Task CopyMode_CopiesTheSourceTree_SoTheRunCanReadExistingFiles()
    {
        _runner.IsGitInstalled = false; // F1: no git at all, so the gate cannot even be attempted
        WriteSource("a.md", "alpha");
        WriteSource(Path.Combine("sub", "b.md"), "beta");
        var runId = Guid.NewGuid();

        var ws = await Build().ProvisionAsync(runId, workingSubpath: null, TestContext.Current.CancellationToken);

        Assert.NotNull(ws);
        Assert.Equal(RunWorkspaceMode.Copy, ws!.Mode);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(ws.Root, "a.md")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(ws.Root, "sub", "b.md")));

        // The recorded source root is what a promotion writes back to (B9), so it is part of the fact.
        Assert.Equal(
            SafeFolderPath.Canonicalize(_source),
            ReadMetadata(runId).GetProperty("sourceRoot").GetString());
    }

    /// <summary>
    /// T-G3-2, <b>REGRESSION</b>. What copy mode leaves OUT, each for a stated reason (B6): the memory vault
    /// (owned by MemoryService / the vault watcher / the ingest indexer, which write through their own paths —
    /// the run still reaches memory through the memory tools) and everything <c>SandboxIgnore</c> prunes, so
    /// what the run sees in its workspace is exactly what <c>list_files</c> would have listed.
    /// <c>keep.md</c> is the positive control: without it a provisioner that copied NOTHING would pass.
    /// </summary>
    [Fact]
    public async Task CopyMode_ExcludesTheVaultAndIgnoredTrees()
    {
        _runner.IsGitInstalled = false;
        WriteSource("keep.md");
        WriteSource(Path.Combine(AssistantWorkspace.VaultSubfolderName, "memory", "m.md"));
        WriteSource(Path.Combine(".git", "config"));
        WriteSource(Path.Combine("bin", "x.dll"));
        WriteSource(Path.Combine("node_modules", "p", "i.js"));

        var ws = await Build().ProvisionAsync(Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(ws);
        Assert.True(File.Exists(Path.Combine(ws!.Root, "keep.md")));
        Assert.False(File.Exists(Path.Combine(ws.Root, AssistantWorkspace.VaultSubfolderName, "memory", "m.md")));
        Assert.False(File.Exists(Path.Combine(ws.Root, ".git", "config")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "bin", "x.dll")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "node_modules", "p", "i.js")));
    }

    /// <summary>
    /// T-G3-3, <b>REGRESSION</b>. Over either cap, provisioning leaves NOTHING behind and returns null (no
    /// isolation, today's behaviour). Running a partial tree is the one outcome worse than not isolating: the
    /// agent would see a truncated folder, "recreate" the missing files, and a promotion would write them
    /// over the originals.
    /// </summary>
    [Fact]
    public async Task CopyMode_OverTheFileCap_ReturnsNull_AndLeavesNoWorkspace()
    {
        _runner.IsGitInstalled = false;
        for (var i = 0; i <= RunWorkspaceService.MaxProvisionedFiles; i++)
            File.WriteAllText(Path.Combine(_source, $"f{i}.md"), string.Empty);
        var runId = Guid.NewGuid();

        var ws = await Build().ProvisionAsync(runId, null, TestContext.Current.CancellationToken);

        Assert.Null(ws);
        Assert.False(Directory.Exists(Path.Combine(_runsBase, runId.ToString())));
        Assert.False(File.Exists(MetadataPath(runId)));
    }

    // ---- worktree mode ----

    /// <summary>
    /// T-G3-4, <b>REGRESSION</b>. Plan D5's worktree half: a source root that is a repository with commits,
    /// inside the assistant files folder, is provisioned as a worktree on a fresh run branch. The ARGUMENT
    /// LIST is asserted, not a substring — <c>Arguments[0]</c> is the string "worktree" for add, remove and
    /// prune alike, so a subcommand check would discriminate nothing.
    /// </summary>
    [Fact]
    public async Task WorktreeMode_AddsAWorktreeOnTheRunBranch()
    {
        WriteSource("a.md");
        ArrangeRepoWithCommits(_source);
        var runId = Guid.NewGuid();

        var ws = await Build().ProvisionAsync(runId, null, TestContext.Current.CancellationToken);

        Assert.NotNull(ws);
        Assert.Equal(RunWorkspaceMode.Worktree, ws!.Mode);
        Assert.Equal($"pia/run/{runId}", ws.BranchName);

        var add = Assert.Single(_runner.Calls, c => IsWorktree(c, "add"));
        Assert.Equal(new[] { "worktree", "add", ws.Root, "-b", $"pia/run/{runId}" }, add.Arguments);
        // Run against the repository, not the workspace: `worktree add` must be issued from the main worktree.
        Assert.Equal(SafeFolderPath.Canonicalize(_source), add.WorkingDirectory);

        // Copy mode's tree is NOT laid down on top of a worktree — git owns that directory's contents.
        Assert.False(File.Exists(Path.Combine(ws.Root, "a.md")));
    }

    /// <summary>
    /// T-G3-5, <b>REGRESSION</b>. The executable form of plan R16: EVERY fault in B11's F1–F9 list degrades
    /// to copy mode with a usable workspace. None throws, none returns null, and none fails the run. One row
    /// per fault, because a single "something went wrong" row would let eight of the nine gates rot.
    /// </summary>
    [Theory]
    [InlineData("F1")] // git not installed
    [InlineData("F2")] // source root is not a repo (rev-parse exit != 0)
    [InlineData("F2-empty")] // rev-parse exit 0 with empty stdout
    [InlineData("F3")] // git could not be launched at all (the runner's -1 start-failure sentinel)
    [InlineData("F4")] // git timed out
    [InlineData("F5")] // the toplevel cannot be canonicalized
    [InlineData("F6")] // the toplevel is outside the assistant files folder
    [InlineData("F7")] // the repo has no commits (unborn HEAD)
    [InlineData("F8")] // worktree add failed
    [InlineData("F9")] // any exception on the git path
    public async Task WorktreeGate_DegradesToCopy_OnEveryFaultInTheList(string fault)
    {
        WriteSource("a.md", "alpha");
        var outside = Path.Combine(_dir, "outside-repo");
        Directory.CreateDirectory(outside);
        var missing = Path.Combine(_source, "gone-" + Guid.NewGuid().ToString("N"));
        var top = SafeFolderPath.Canonicalize(_source).Replace('\\', '/') + "\n";

        switch (fault)
        {
            case "F1": _runner.IsGitInstalled = false; break;
            case "F2": _runner.Responder = r => IsShowToplevel(r) ? Exit(128) : Ok(); break;
            case "F2-empty": _runner.Responder = r => Ok(); break;
            case "F3": _runner.Responder = r => IsShowToplevel(r) ? Exit(-1) : Ok(); break;
            case "F4":
                _runner.Responder = r => IsShowToplevel(r)
                    ? new GitProcessResult(0, top, string.Empty, TimedOut: true)
                    : Ok();
                break;
            case "F5": _runner.Responder = r => IsShowToplevel(r) ? Ok(missing.Replace('\\', '/')) : Ok(); break;
            case "F6": _runner.Responder = r => IsShowToplevel(r) ? Ok(outside.Replace('\\', '/')) : Ok(); break;
            case "F7": _runner.Responder = r => IsVerifyHead(r) ? Exit(128) : IsShowToplevel(r) ? Ok(top) : Ok(); break;
            case "F8": _runner.Responder = r => IsWorktree(r, "add") ? Exit(128) : IsShowToplevel(r) ? Ok(top) : Ok(); break;
            case "F9": _runner.Responder = r => throw new InvalidOperationException("git exploded"); break;
            default: Assert.Fail("unknown fault row " + fault); break;
        }

        var ws = await Build().ProvisionAsync(Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(ws);
        Assert.Equal(RunWorkspaceMode.Copy, ws!.Mode);
        Assert.Null(ws.BranchName);
        // "Usable" means the run can actually read the user's files, not merely that a directory exists.
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(ws.Root, "a.md")));
    }

    // ---- idempotence + metadata ----

    /// <summary>
    /// T-G3-6, <b>REGRESSION</b>. A resume re-enters the SAME workspace with the SAME provisioning instant:
    /// that one timestamp decides the promote set (B7), so a second provisioning would make the promote set
    /// "everything the workspace contains". In worktree mode it also must not issue a second
    /// <c>worktree add</c> — that fails, and the degrade would silently drop the run into copy mode.
    /// </summary>
    [Fact]
    public async Task Provision_IsIdempotent_SoAResumeLandsInTheSameWorkspaceWithTheSameTimestamp()
    {
        WriteSource("a.md");
        ArrangeRepoWithCommits(_source);
        var svc = Build();
        var runId = Guid.NewGuid();

        var first = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        var stamp = ReadMetadata(runId).GetProperty("provisionedAtUtc").GetString();

        var second = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Root, second!.Root);
        Assert.Equal(first.Mode, second.Mode);
        Assert.Equal(first.BranchName, second.BranchName);
        Assert.Equal(stamp, ReadMetadata(runId).GetProperty("provisionedAtUtc").GetString());
        Assert.Single(_runner.Calls, c => IsWorktree(c, "add"));
    }

    /// <summary>
    /// T-G3-7, <b>GUARD</b>. The metadata document round-trips at <c>v:1</c>, and a version this build does
    /// not understand reads as "no workspace" in the RESTRICTIVE direction: nothing is described, nothing is
    /// promoted, and — the load-bearing half — nothing is deleted. "Promote nothing and leave the files where
    /// they are" is recoverable; acting on a document we cannot reason about is not.
    /// <para>
    /// The <c>PromoteAsync</c> leg is satisfied at this commit by G3's documented no-op (promotion lands with
    /// the next group). It is asserted here anyway so the group that implements the real promote path inherits
    /// the obligation rather than having to invent it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Metadata_RoundTripsAtV1_AndAnUnknownVersionReadsAsNoWorkspace()
    {
        _runner.IsGitInstalled = false;
        WriteSource("a.md");
        var svc = Build();
        var runId = Guid.NewGuid();

        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);

        var meta = ReadMetadata(runId);
        Assert.Equal(1, meta.GetProperty("v").GetInt32());
        Assert.Equal("Copy", meta.GetProperty("mode").GetString());
        Assert.NotNull(await svc.DescribeAsync(runId, TestContext.Current.CancellationToken));

        File.WriteAllText(MetadataPath(runId), """{"v":99,"mode":"Copy","sourceRoot":"C:\\nope"}""");

        Assert.Null(await svc.DescribeAsync(runId, TestContext.Current.CancellationToken));
        Assert.Null(await svc.PromoteAsync(runId, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(ws!.Root, "a.md")));
        Assert.True(File.Exists(MetadataPath(runId)));
    }

    /// <summary>
    /// <b>REGRESSION</b> for the one thing <c>DescribeAsync</c> decides on its own: whether a settled run
    /// still has files sitting in its workspace. Both mtimes are SET explicitly rather than slept for —
    /// <c>DateTime.UtcNow</c> has ~15 ms granularity while a file time is far finer, so a "write it and it
    /// will be newer" test would be a coin flip on a fast machine.
    /// </summary>
    [Fact]
    public async Task Describe_ReportsUnpublishedFiles_OnlyWhenTheRunWroteAfterProvisioning()
    {
        _runner.IsGitInstalled = false;
        WriteSource("a.md");
        var svc = Build();
        var runId = Guid.NewGuid();

        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);
        var provisionedAt = DateTime.Parse(
            ReadMetadata(runId).GetProperty("provisionedAtUtc").GetString()!,
            null, System.Globalization.DateTimeStyles.RoundtripKind);

        // Control first: everything in the workspace is older than the provisioning instant, i.e. copied in
        // and untouched. Without this leg a Describe that always said "yes" would pass.
        File.SetLastWriteTimeUtc(Path.Combine(ws!.Root, "a.md"), provisionedAt.AddMinutes(-1));
        var untouched = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);
        Assert.NotNull(untouched);
        Assert.Equal(RunWorkspaceMode.Copy, untouched!.Mode);
        Assert.False(untouched.HasUnpublishedFiles);

        File.WriteAllText(Path.Combine(ws.Root, "out.md"), "deliverable");
        File.SetLastWriteTimeUtc(Path.Combine(ws.Root, "out.md"), provisionedAt.AddMinutes(1));

        var wrote = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);
        Assert.NotNull(wrote);
        Assert.True(wrote!.HasUnpublishedFiles);
    }

    // ---- teardown ----

    /// <summary>
    /// T-G3-8, <b>REGRESSION</b>. Teardown is symmetric with provisioning: a worktree is removed THROUGH GIT,
    /// from the main worktree recorded at provisioning, and the run branch is never deleted — it is the
    /// deliverable (plan D5b). An <c>rmdir</c> here would leave a <c>.git/worktrees/&lt;id&gt;</c> registration
    /// in the user's repository forever (plan R5).
    /// </summary>
    [Fact]
    public async Task TearDown_WorktreeMode_RemovesTheWorktree_AndNeverDeletesTheBranch()
    {
        WriteSource("a.md");
        ArrangeRepoWithCommits(_source);
        var svc = Build();
        var runId = Guid.NewGuid();
        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);

        await svc.TearDownAsync(runId, TestContext.Current.CancellationToken);

        var remove = Assert.Single(_runner.Calls, c => IsWorktree(c, "remove"));
        Assert.Equal(new[] { "worktree", "remove", "--force", ws!.Root }, remove.Arguments);
        Assert.Equal(SafeFolderPath.Canonicalize(_source), remove.WorkingDirectory);
        Assert.DoesNotContain(_runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "branch");
        Assert.False(File.Exists(MetadataPath(runId)));
    }

    /// <summary>
    /// T-G3-9, <b>REGRESSION</b>. The half of plan R5 that actually leaks: when <c>worktree remove</c> fails
    /// (a locked index, a file held open, a git that vanished), the directory is deleted here AND the stale
    /// registration is pruned. Without the prune the user's repository accumulates a dead worktree entry per
    /// failed teardown.
    /// </summary>
    [Fact]
    public async Task TearDown_WhenWorktreeRemoveFails_FallsBackToRmdirThenPrune()
    {
        WriteSource("a.md");
        ArrangeRepoWithCommits(_source);
        var svc = Build();
        var runId = Guid.NewGuid();
        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);
        // The fake never really created a worktree, so put something in the directory: the fallback has to
        // delete a non-empty tree, which is the case that actually happens in production.
        File.WriteAllText(Path.Combine(ws!.Root, "left-behind.md"), "x");

        var top = SafeFolderPath.Canonicalize(_source).Replace('\\', '/') + "\n";
        _runner.Responder = r => IsWorktree(r, "remove") ? Exit(1) : IsShowToplevel(r) ? Ok(top) : Ok();

        await svc.TearDownAsync(runId, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(ws.Root));
        Assert.Contains(_runner.Calls, c => IsWorktree(c, "prune"));
        Assert.False(File.Exists(MetadataPath(runId)));
    }

    /// <summary>
    /// T-G3-10, <b>REGRESSION</b>. The startup sweep enumerates DIRECTORIES only, so a metadata document
    /// whose workspace is already gone is invisible to it — and in worktree mode that document is the only
    /// thing that knows which repository still carries the stale registration. The second document, whose
    /// directory DOES exist, is the non-vacuity control: a sweep that deleted everything would pass without it.
    /// </summary>
    [Fact]
    public async Task SweepOrphanMetadata_PrunesAndDeletesAMetadataFileWhoseDirectoryIsGone()
    {
        WriteSource("a.md");
        ArrangeRepoWithCommits(_source);
        var svc = Build();

        var orphan = Guid.NewGuid();
        var live = Guid.NewGuid();
        var orphanWs = await svc.ProvisionAsync(orphan, null, TestContext.Current.CancellationToken);
        var liveWs = await svc.ProvisionAsync(live, null, TestContext.Current.CancellationToken);
        Assert.NotNull(orphanWs);
        Assert.NotNull(liveWs);

        // Simulate the crash shape: the directory is gone (git removed it, or a user deleted it) but the
        // document — and the repository's worktrees entry — survives.
        Directory.Delete(orphanWs!.Root, recursive: true);
        _runner.Calls.Clear();

        await svc.SweepOrphanMetadataAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(MetadataPath(orphan)));
        Assert.Contains(_runner.Calls, c => IsWorktree(c, "prune"));
        Assert.True(File.Exists(MetadataPath(live)));
        Assert.True(Directory.Exists(liveWs!.Root));
    }

    /// <summary>
    /// <b>GUARD</b> on the narrowing rule (B6): the workspace corresponds 1:1 to the NARROWED source root, so
    /// an isolated run's own subpath must then be null — narrowing twice would look for
    /// <c>&lt;runRoot&gt;\&lt;subpath&gt;</c>, which does not exist.
    /// </summary>
    [Fact]
    public async Task CopyMode_WithAWorkingSubpath_ProvisionsFromTheNarrowedRoot()
    {
        _runner.IsGitInstalled = false;
        WriteSource(Path.Combine("sub", "b.md"), "beta");
        WriteSource("outside-the-subpath.md");
        var runId = Guid.NewGuid();

        var ws = await Build().ProvisionAsync(runId, "sub", TestContext.Current.CancellationToken);

        Assert.NotNull(ws);
        Assert.True(File.Exists(Path.Combine(ws!.Root, "b.md")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "outside-the-subpath.md")));
        Assert.Equal(
            SafeFolderPath.Canonicalize(Path.Combine(_source, "sub")),
            ReadMetadata(runId).GetProperty("sourceRoot").GetString());
    }

    /// <summary>
    /// <b>GUARD</b>. No usable assistant files folder ⇒ no isolation (null), not an exception and not an empty
    /// workspace. Bookkeeping must never fail a run.
    /// </summary>
    [Fact]
    public async Task Provision_WithNoAssistantFilesFolder_ReturnsNull_AndLeavesNoWorkspace()
    {
        var runId = Guid.NewGuid();

        var ws = await BuildWithoutFilesFolder().ProvisionAsync(runId, null, TestContext.Current.CancellationToken);

        Assert.Null(ws);
        Assert.False(Directory.Exists(Path.Combine(_runsBase, runId.ToString())));
    }
}
