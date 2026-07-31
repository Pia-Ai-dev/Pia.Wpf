using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 06 G4: what a completed run's promotion actually copies (B7's mtime promote set), where it copies it
/// (B9's recorded destination), and the two cases where it deliberately copies NOTHING — worktree mode, where
/// the branch is the deliverable (plan D5b), and any input the service cannot reason about.
/// <para>
/// The workspace is built BY HAND rather than through <c>ProvisionAsync</c>: every fact here turns on the
/// relationship between a file's <c>LastWriteTimeUtc</c> and the ONE durable <c>provisionedAtUtc</c> the
/// metadata document carries, and writing that timestamp explicitly is the difference between a fact and a
/// race against the clock. It also keeps these facts git-free.
/// </para>
/// <para>
/// In the <c>RunWorkspaceRedirectsStatic</c> collection because
/// <see cref="Promote_AtTheRealRunsRootShape_ActuallyCopiesFilesOut"/> drives a real <c>CopyOut</c>, whose
/// <c>RunWorkspaceRedirects.Record</c> call lands at a root the containment gate ACCEPTS and therefore mutates
/// the process-global registry. <c>RunWorkspaceRedirectsTests</c> deliberately overflows that registry's entry
/// cap, so the two must not run concurrently.
/// </para>
/// </summary>
[Collection("RunWorkspaceRedirectsStatic")]
public sealed class RunWorkspacePromotionTests : IDisposable
{
    private readonly string _dir;

    /// <summary>The promotion destination: the source root the run was provisioned FROM (B9).</summary>
    private readonly string _dest;
    private readonly string _runsBase;
    private readonly FakeGitProcessRunner _runner = new();

    /// <summary>One hour ago / one minute from now, relative to <see cref="Stamp"/>: "the run did not touch
    /// this" and "the run wrote this".</summary>
    private static readonly DateTime Stamp = new(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeProvision = Stamp.AddHours(-1);
    private static readonly DateTime AfterProvision = Stamp.AddMinutes(1);

    public RunWorkspacePromotionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunProm_" + Guid.NewGuid().ToString("N"));
        _dest = Path.Combine(_dir, "files");
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_dest);
        Directory.CreateDirectory(_runsBase);
    }

    /// <summary>Cleaned up by <see cref="Dispose"/>: the ONE fixture that lives at the real shape, under the
    /// developer's actual runs root (see <see cref="Promote_AtTheRealRunsRootShape_ActuallyCopiesFilesOut"/>).</summary>
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

    /// <param name="filesFolder">The CURRENTLY configured assistant files folder. Defaults to the promotion
    /// destination; a different value models the user relocating the folder mid-run (T-G4-7).</param>
    private RunWorkspaceService Build(string? filesFolder = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = filesFolder ?? _dest });
        return new RunWorkspaceService(_runner, settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
    }

    private string RunRoot(Guid runId) => Path.Combine(_runsBase, runId.ToString());

    private string MetadataPath(Guid runId) => Path.Combine(_runsBase, runId + ".workspace.json");

    /// <summary>
    /// Writes the sibling metadata document the way <c>ProvisionAsync</c> would. camelCase and <c>v:1</c> —
    /// the exact wire shape, because promotion after a restart reads a document some other build wrote.
    /// </summary>
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
    /// T-G4-1, <b>REGRESSION</b>. The promote set is what the RUN wrote, decided by mtime against the one
    /// durable <c>provisionedAtUtc</c>: a copied-in file keeps the source's timestamp and is therefore older,
    /// a file the agent wrote is newer. Promoting everything back would rewrite files the run never touched —
    /// mtime churn that wakes the vault watcher and the sync delta, and (worse) a silent revert of a user edit
    /// made during the run.
    /// <para>
    /// The DISCRIMINATING half is the third file. An untouched file whose content matches the destination is
    /// protected by the byte-identity skip as well, so on its own it cannot tell the mtime rule from the
    /// identity rule: with the mtime skip neutralized it still stays put. A copied-in file the USER DELETED at
    /// the destination during the run has no destination to compare against — without the mtime rule
    /// promotion "creates a missing file" and resurrects it. A promotion must never undelete.
    /// </para>
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
        // The untouched file was not rewritten — asserted on its TIMESTAMP, because identical content would
        // hide a copy that happened anyway.
        Assert.Equal(BeforeProvision, File.GetLastWriteTimeUtc(Path.Combine(_dest, "a.md")));
        Assert.False(File.Exists(Path.Combine(_dest, "user-deleted.md")));
    }

    /// <summary>
    /// T-G4-2, <b>REGRESSION</b>. A file the run rewrote to exactly what was already there is skipped: same
    /// size, same SHA256, so copying it would only churn its mtime for no change in content.
    /// </summary>
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

    /// <summary>
    /// T-G4-3, <b>REGRESSION</b>. The B7 conflict rule, and the one that protects a real user edit: the
    /// destination changed WHILE the run was working, so an unattended run does not get to overwrite it. It is
    /// counted and reported, never silently dropped.
    /// </summary>
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

        // <b>REGRESSION</b> (Phase 3 fix pass, Batch 06 Lens A finding 5 / Lens B finding 3). Keeping the
        // user's edit is only half the decision: the RUN's version of notes.md was deliberately not written and
        // now exists ONLY in the workspace, which the caller tears down the moment a non-null result comes
        // back. Telling the caller to keep it is the difference between "we kept your edit" and "we silently
        // threw the run's work away". Neutralization: RetainWorkspace: false → red.
        Assert.True(result.RetainWorkspace);
        Assert.Equal("the run's version", File.ReadAllText(Path.Combine(RunRoot(runId), "notes.md")));
    }

    /// <summary>
    /// T-G4-4, <b>GUARD</b>. Promote is not sync: a deletion inside the workspace is never propagated, so a
    /// background run cannot delete a user's file by finishing. Write arbitration belongs to a later batch.
    /// </summary>
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

    /// <summary>
    /// T-G4-5, <b>REGRESSION</b>. Plan D5b: in worktree mode the BRANCH is the deliverable. Nothing is copied
    /// and nothing is merged — which is what keeps conflict handling out of an unattended path entirely — and
    /// the branch name comes back so the panel can say where the output is.
    /// </summary>
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

    /// <summary>
    /// T-G4-6, <b>GUARD</b>. B5's restrictive degrade: an unreadable metadata document promotes NOTHING and
    /// keeps the workspace. "Promote nothing and leave the files where they are" is recoverable — the publish
    /// offer and the folder are both still there; "overwrite the user's folder from a workspace we cannot
    /// reason about" is not.
    /// </summary>
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

    /// <summary>
    /// T-G4-7, <b>REGRESSION</b>. B9: the recorded destination must still resolve inside the CURRENT assistant
    /// files folder. The user relocated the folder (or edited the setting) between provisioning and the
    /// terminal settle, so re-anchoring the promotion onto a folder the run never saw is not a repair — it is
    /// writing a run's output somewhere nobody asked for.
    /// </summary>
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
    /// <b>REGRESSION</b>, and the one fact here that runs at the REAL shape (plan R1). Every other fixture in
    /// this file roots its runs base under <c>Path.GetTempPath()</c>, which is outside every blocked root — but
    /// in production a run workspace lives inside <c>%LOCALAPPDATA%\Pia</c>, which
    /// <c>SensitivePathGuard</c> blocks wholesale and G1's carve-out re-opens. The promote walk asks
    /// <c>IsBlocked</c> about every file it considers, so without that carve-out covering the runs tree the walk
    /// returns NOTHING: zero promoted, no exception, and a green gate over a promotion that silently does
    /// nothing. This is the "successful write at the real shape" plan R1 asks for, on the read side.
    /// <para>
    /// The workspace directory is Guid-named deliberately: <c>RunStartupSweepAsync</c> skips any name that is
    /// not a parseable Guid, so a fixture leaked by a crashed test run would otherwise sit in the developer's
    /// real runs folder forever. <see cref="Dispose"/> removes it and its metadata document.
    /// </para>
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

    /// <summary>
    /// <b>GUARD</b>. The panel's offer and the promotion must agree about what is publishable: a
    /// <c>.git</c> the model created inside its own workspace (copy mode has no repository — a stated
    /// release-note behaviour) is ignore-pruned on the way out, so it must not by itself make
    /// <see cref="RunWorkspaceOutcome.HasUnpublishedFiles"/> true. Otherwise the user gets an offer that
    /// publishes nothing.
    /// </summary>
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

        // Positive control: a real deliverable DOES raise the offer, so the assertion above is about pruning
        // rather than about a Describe that always says no.
        Write(RunRoot(runId), "deliverable.md", "written", AfterProvision);
        var offered = await svc.DescribeAsync(runId, TestContext.Current.CancellationToken);
        Assert.NotNull(offered);
        Assert.True(offered!.HasUnpublishedFiles);
    }
}
