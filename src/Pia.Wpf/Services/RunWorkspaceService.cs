using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IRunWorkspaceService"/> (Batch 06 B4): provisions a run's isolated workspace as a
/// <b>git worktree</b> when the source root is a repository the git tools may already touch, and as a
/// <b>bounded copy</b> of the source tree otherwise (plan D5), and owns the symmetric teardown each mode
/// needs. Both strategies live in ONE type on purpose — the failure this batch most needs to prevent is a
/// worktree torn down with <c>rmdir</c>, which leaves a stale <c>.git/worktrees/&lt;id&gt;</c> registration
/// in the user's repository forever (plan R5/R16).
/// <para>
/// NOTHING here throws. Every fault degrades in the restrictive direction: worktree → copy (the F1–F9 list
/// below) → no isolation at all (<c>null</c>, i.e. the pre-Batch-06 behaviour of writing straight into the
/// assistant files folder). A run must never fail because provisioning got clever.
/// </para>
/// <para>
/// Two accepted behaviours, both release-note items rather than bugs: a worktree starts from a
/// <b>commit</b>, so uncommitted and untracked files in the user's tree are invisible to the run; and
/// worktree mode <b>mutates the user's repository</b> (a worktrees entry plus a branch ref) even though the
/// working tree is untouched. Teardown is therefore exact, and it never deletes the branch.
/// </para>
/// </summary>
public sealed class RunWorkspaceService : IRunWorkspaceService
{
    /// <summary>
    /// Ceiling on the copy-mode source tree. Exceeding either bound provisions NOTHING (the run degrades to
    /// no isolation) rather than half a tree: an agent that sees a truncated folder "recreates" the missing
    /// files, and a later promotion writes those over the originals (B6).
    /// </summary>
    internal const int MaxProvisionedFiles = 2000;

    /// <inheritdoc cref="MaxProvisionedFiles"/>
    internal const long MaxProvisionedBytes = 256L * 1024 * 1024;

    /// <summary>Metadata document shape this build writes and understands. Additive members only — a resume
    /// runs in a different process, and the publish affordance can be clicked days later (B5).</summary>
    private const int MetadataVersion = 1;

    /// <summary>Suffix of the sibling metadata document: <c>&lt;runsBase&gt;\&lt;runId&gt;.workspace.json</c>.</summary>
    private const string MetadataSuffix = ".workspace.json";

    /// <summary>Run-branch prefix. <c>pia/run/&lt;runId&gt;</c> (hyphenated, matching the directory name)
    /// passes <c>git check-ref-format</c> and cannot collide with a user branch.</summary>
    private const string RunBranchPrefix = "pia/run/";

    /// <summary>
    /// How long a TORN-DOWN worktree stub is kept so <see cref="DescribeAsync"/> can still name the run
    /// branch. Deliberately the same seven days <c>HeadlessRunLauncher</c> gives a settled run's workspace —
    /// the window in which the panel is realistically re-opened. The BRANCH itself is never deleted, so what
    /// ages out here is the app's ability to name it, not the deliverable.
    /// </summary>
    private static readonly TimeSpan TornDownStubMaxAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IGitProcessRunner _runner;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<RunWorkspaceService> _logger;

    /// <summary>Base directory for every run workspace. The override mirrors
    /// <c>HeadlessRunLauncher</c>'s parameter name so a test can point both at one temp directory.</summary>
    private readonly string _runsBaseDir;

    public RunWorkspaceService(
        IGitProcessRunner runner,
        ISettingsService settingsService,
        ILogger<RunWorkspaceService> logger,
        string? runsBaseDirOverride = null)
    {
        _runner = runner;
        _settingsService = settingsService;
        _logger = logger;
        _runsBaseDir = runsBaseDirOverride ?? AssistantWorkspace.RunsRoot;
    }

    public string RootFor(Guid runId) => Path.Combine(_runsBaseDir, runId.ToString());

    private string MetadataPathFor(Guid runId) => Path.Combine(_runsBaseDir, runId + MetadataSuffix);

    public async Task<RunWorkspace?> ProvisionAsync(Guid runId, string? workingSubpath, CancellationToken ct)
    {
        try
        {
            // (2) Idempotent reuse FIRST (B11). A resume must land in the same workspace with the same
            // provisionedAtUtc, because that one timestamp decides the promote set (B7) — re-provisioning
            // would make it "everything". A readable document whose directory has since vanished is not a
            // workspace: tear it down (which prunes a stale worktree registration) and provision afresh.
            var existing = ReadMetadata(runId);
            if (existing is not null)
            {
                var existingRoot = RootFor(runId);
                if (Directory.Exists(existingRoot))
                {
                    _logger.LogInformation(
                        "Run {RunId} re-entered its existing {Mode} workspace", runId, existing.ParsedMode);
                    return new RunWorkspace(
                        runId, TryCanonicalize(existingRoot), existing.ParsedMode, existing.SourceRoot!, existing.Branch);
                }

                await TearDownAsync(runId, ct).ConfigureAwait(false);
            }

            // (1) Create + canonicalize — the same three lines the launcher did before this batch.
            var runRoot = RootFor(runId);
            Directory.CreateDirectory(runRoot);
            runRoot = SafeFolderPath.Canonicalize(runRoot);

            // (3) The source root: what the run reads, and what copy mode promotes back to.
            var (sourceRoot, settingsFolder) = await ResolveSourceRootAsync(workingSubpath).ConfigureAwait(false);
            if (sourceRoot is null || settingsFolder is null)
            {
                _logger.LogInformation(
                    "Run {RunId} workspace provisioning skipped: no usable assistant files folder", runId);
                TryDeleteDirectory(runRoot);
                return null;
            }

            // (4)/(5) Worktree when the source root is a repo we may touch, else (6) the bounded copy.
            var worktree = await TryProvisionWorktreeAsync(runId, runRoot, sourceRoot, settingsFolder, ct)
                .ConfigureAwait(false);

            RunWorkspaceMode mode;
            string? mainWorktree;
            string? branch;
            if (worktree is not null)
            {
                mode = RunWorkspaceMode.Worktree;
                mainWorktree = worktree.MainWorktree;
                branch = worktree.Branch;
            }
            else
            {
                mode = RunWorkspaceMode.Copy;
                mainWorktree = null;
                branch = null;
                if (!await CopyInAsync(runId, runRoot, sourceRoot, settingsFolder, ct).ConfigureAwait(false))
                {
                    // F10: copy mode's OWN failure (a throw, or either cap) degrades one step further, to
                    // no isolation. Nothing partial is left behind.
                    TryDeleteDirectory(runRoot);
                    return null;
                }
            }

            // (7) The metadata document is what makes promotion and teardown possible after a restart, so a
            // write failure is fatal to isolation: running isolated with no metadata means nothing can
            // promote the work out or clean the worktree registration up.
            var meta = new WorkspaceMetadataDto
            {
                V = MetadataVersion,
                Mode = mode.ToString(),
                SourceRoot = sourceRoot,
                MainWorktree = mainWorktree,
                Branch = branch,
                ProvisionedAtUtc = DateTime.UtcNow,
                Degraded = mode == RunWorkspaceMode.Copy && worktree is null && _runner.IsGitInstalled,
            };
            if (!TryWriteMetadata(runId, meta))
            {
                await TearDownWithoutMetadataAsync(runId, runRoot, mode, mainWorktree, ct).ConfigureAwait(false);
                return null;
            }

            _logger.LogInformation("Run {RunId} workspace provisioned in {Mode} mode", runId, mode);
            return new RunWorkspace(runId, runRoot, mode, sourceRoot, branch);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never fail a run (guardrail 1): the caller reads null as "no isolation".
            _logger.LogWarning(ex, "Run {RunId} workspace provisioning failed; the run will not be isolated", runId);
            return null;
        }
    }

    /// <summary>
    /// Promote what the run wrote (Batch 06 B7/B9/B10). <c>null</c> means "nothing was promoted and the
    /// workspace is intact", which is the restrictive degrade every unreasonable input takes: no readable
    /// metadata, no workspace directory, a mode this build does not know, or a recorded destination that no
    /// longer resolves inside the CURRENT assistant files folder. Keeping the files where they are is
    /// recoverable — the publish affordance still offers them; writing a workspace we cannot reason about
    /// over the user's folder is not.
    /// <para>
    /// Promotion is TERMINAL-ONLY and happens ONCE per workspace. That invariant is what lets the single
    /// <c>provisionedAtUtc</c> decide the promote set even across a park → resume: nothing has been promoted
    /// yet, so "everything either segment wrote" is the correct set. A second promotion of the same workspace
    /// (a child run promoting before its parent, Batch 07) would re-copy the first one's output over whatever
    /// the destination has accumulated since — do not add one.
    /// </para>
    /// </summary>
    public async Task<RunPromotionResult?> PromoteAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            var meta = ReadMetadata(runId);
            if (meta is null)
            {
                // ReadMetadata already logged WHY (absent / unparseable / foreign version). Nothing is
                // promoted and nothing is deleted: B5's restrictive degrade.
                _logger.LogWarning("Run {RunId} was not promoted: its workspace metadata is not usable", runId);
                return null;
            }

            var runRoot = RootFor(runId);
            if (!Directory.Exists(runRoot))
                return null;

            if (meta.ParsedMode == RunWorkspaceMode.Worktree)
            {
                // B10 / plan D5b: THE BRANCH IS THE DELIVERABLE. Nothing is copied anywhere and there is
                // deliberately no merge, so no unattended path ever has to handle a conflict — but the branch
                // only IS the deliverable once something has been committed onto it, which is what
                // CommitToRunBranchAsync does. The panel says where the output is (B15's Run_Output_Branch
                // line) — without that line the honest user question is "where is my file?".
                return await CommitToRunBranchAsync(runId, runRoot, meta.Branch, ct).ConfigureAwait(false);
            }

            if (meta.ParsedMode != RunWorkspaceMode.Copy)
                return null;

            // B9: the destination is the source root RECORDED AT PROVISIONING, so runRoot\rel →
            // sourceRoot\rel is the pure inverse of the copy-in. It must still exist AND still resolve
            // inside the current assistant files folder — the user may have relocated the folder or edited
            // the setting mid-run, and re-anchoring a promotion onto a folder the run never saw is not a
            // repair.
            var destination = meta.SourceRoot!;
            var (_, settingsFolder) = await ResolveSourceRootAsync(null).ConfigureAwait(false);
            if (settingsFolder is null || !Directory.Exists(destination) || !IsInsideOrEqual(destination, settingsFolder))
            {
                _logger.LogWarning(
                    "Run {RunId} was not promoted: its recorded destination no longer resolves inside the assistant files folder",
                    runId);
                return null;
            }

            return await Task.Run(() => CopyOut(runId, runRoot, destination, meta.ProvisionedAtUtc, ct), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never fail a run (guardrail 1): the work stays in the workspace and the
            // publish affordance can still offer it.
            _logger.LogWarning(ex, "Run {RunId} promotion failed; its work stays in its workspace", runId);
            return null;
        }
    }

    /// <summary>
    /// Worktree mode's promotion: commit the run's work onto its run branch, app-side.
    /// <para>
    /// This exists because "the branch is the deliverable" (plan D5b) was not true of a branch nobody ever
    /// committed to. <b>Nothing else in the build can commit it.</b> An unattended run's grant set is
    /// <c>{write_file}</c> and <c>RunAutonomyPolicy</c>'s presets exclude <c>ToolClass.Git</c> entirely, so the
    /// model's own <c>git_commit</c> is refused as not-granted; and the caller tears this worktree down with
    /// <c>git worktree remove --force</c> the moment a non-null result comes back. Before this, a clean
    /// worktree run therefore deleted its own output and left the branch byte-identical to the base commit.
    /// It stays app-side deliberately (plan R18): committing here adds no new agent capability.
    /// </para>
    /// <para>
    /// Every arm that leaves work outside the commit sets <see cref="RunPromotionResult.RetainWorkspace"/>, so
    /// the caller keeps the directory instead of deleting it: git could not be asked, the commit failed, or
    /// <c>add -A</c> declined to take something (the user's own <c>.gitignore</c> applies inside their
    /// worktree, and <c>status --porcelain</c> alone would not have shown it).
    /// </para>
    /// </summary>
    private async Task<RunPromotionResult?> CommitToRunBranchAsync(
        Guid runId, string runRoot, string? branch, CancellationToken ct)
    {
        // --untracked-files=all so the count is FILES, not collapsed directories: it is the number this
        // promotion reports, and the panel's "N file(s)" line is read by a human.
        var pending = await RunGitAsync(
            runRoot, ["status", "--porcelain", "--untracked-files=all"], GitCommandKind.ReadOnly, ct)
            .ConfigureAwait(false);
        if (!pending.Succeeded)
        {
            _logger.LogWarning(
                "Run {RunId} run-branch commit skipped: git status failed ({Exit}); its workspace is retained",
                runId, pending.ExitCode);
            return new RunPromotionResult(
                RunWorkspaceMode.Worktree, Promoted: 0, Skipped: 0, Conflicts: 0, branch, RetainWorkspace: true);
        }

        var changed = CountPorcelainEntries(pending.StandardOutput);
        if (changed > 0)
        {
            var add = await RunGitAsync(runRoot, ["add", "-A"], GitCommandKind.Mutating, ct).ConfigureAwait(false);
            var commit = add.Succeeded
                ? await RunGitAsync(runRoot, CommitArgsFor(runId), GitCommandKind.Mutating, ct).ConfigureAwait(false)
                : add;
            if (!commit.Succeeded)
            {
                // stderr can name paths, so it goes through the highest DEBUG-erased severity (plan R7);
                // the release-visible line carries the exit code and counts only.
                _logger.SensitiveWarning(
                    "Run {RunId} run-branch commit failed: {Err}", runId, commit.StandardError);
                _logger.LogWarning(
                    "Run {RunId} run-branch commit failed ({Exit}); its workspace is retained with {Count} change(s)",
                    runId, commit.ExitCode, changed);
                return new RunPromotionResult(
                    RunWorkspaceMode.Worktree, Promoted: 0, Skipped: changed, Conflicts: 0, branch, RetainWorkspace: true);
            }
        }

        // Whatever is STILL there after the commit is work the branch does not carry — an ignored build
        // artefact the run produced, most often. Removing the directory would destroy it, so keep it and let
        // the terminal retention rule age it out.
        var leftover = await RunGitAsync(
            runRoot, ["status", "--porcelain", "--untracked-files=all", "--ignored"], GitCommandKind.ReadOnly, ct)
            .ConfigureAwait(false);
        var retain = !leftover.Succeeded || CountPorcelainEntries(leftover.StandardOutput) > 0;

        _logger.LogInformation(
            "Run {RunId} committed {Count} change(s) to its run branch (worktree mode); workspace retained: {Retained}",
            runId, changed, retain);
        return new RunPromotionResult(
            RunWorkspaceMode.Worktree, Promoted: changed, Skipped: 0, Conflicts: 0, branch, RetainWorkspace: retain);
    }

    /// <summary>
    /// The commit invocation. Three deliberate <c>-c</c> overrides, each because this runs UNATTENDED:
    /// <list type="bullet">
    /// <item><c>user.name</c>/<c>user.email</c> — git refuses to commit without an identity and the runner's
    /// environment cannot prompt for one, so the app supplies its own rather than depending on whether the
    /// user configured git. Attributing an agent-authored commit to the app is also the honest record.</item>
    /// <item><c>commit.gpgsign=false</c> — a user with signing on globally would otherwise hit a passphrase
    /// prompt that cannot be answered here, and the commit would fail with the deliverable still in the
    /// worktree.</item>
    /// </list>
    /// <c>--no-verify</c> matches <c>GitToolHandler</c>'s locked decision: repo commit hooks are out-of-band
    /// code execution.
    /// </summary>
    private static string[] CommitArgsFor(Guid runId) =>
    [
        "-c", "user.name=Pia",
        "-c", "user.email=pia@pia.invalid",
        "-c", "commit.gpgsign=false",
        "commit", "--no-verify", "-m", "pia run " + runId,
    ];

    /// <summary>Non-empty lines of a <c>--porcelain</c> status, i.e. changed entries. Never a path: the count
    /// is all that leaves this method.</summary>
    private static int CountPorcelainEntries(string porcelain) =>
        porcelain.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));

    /// <summary>
    /// Copy mode's promote set (B7), decided by mtime against <paramref name="provisionedAtUtc"/>:
    /// <c>File.Copy</c> preserves the source's <c>LastWriteTime</c>, so a copied-in file is OLDER than that
    /// one durable timestamp and a file the agent wrote is NEWER. No manifest, no hash index, and it survives
    /// a resume in a different process.
    /// <para>
    /// DELETIONS ARE NEVER PROPAGATED. A run cannot delete a user file by promoting — that is the difference
    /// between "promote" and "sync", and write arbitration belongs to a later batch.
    /// </para>
    /// </summary>
    private RunPromotionResult? CopyOut(
        Guid runId, string runRoot, string destination, DateTime provisionedAtUtc, CancellationToken ct)
    {
        var (rels, overCap) = CollectPromotableFiles(runRoot, destination, ct);
        if (overCap)
        {
            // The same reasoning as B6's provisioning cap, in the other direction: half a promotion is worse
            // than none, and the files remain publishable by hand.
            _logger.LogInformation(
                "Run {RunId} was not promoted: its workspace exceeds the isolation cap ({FileCount} files)",
                runId, rels.Count);
            return null;
        }

        // An unusable timestamp (a document written by something that omitted it) cannot tell "the agent
        // wrote this" from "we copied this in", so degrade to the one action that is safe either way:
        // create files that do not exist at the destination, and touch nothing that does.
        var stampUsable = provisionedAtUtc > DateTime.MinValue;

        int promoted = 0, skipped = 0, conflicts = 0;
        foreach (var rel in rels)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(runRoot, rel);
            var dest = Path.Combine(destination, rel);
            try
            {
                if (stampUsable && File.GetLastWriteTimeUtc(src) <= provisionedAtUtc)
                    continue; // the run did not touch it

                if (!File.Exists(dest))
                {
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    File.Copy(src, dest, overwrite: false);
                    promoted++;
                    continue;
                }

                if (!stampUsable)
                {
                    skipped++;
                    continue;
                }

                if (IsByteIdentical(src, dest))
                {
                    skipped++; // already correct — copying would only churn its mtime
                    continue;
                }

                if (File.GetLastWriteTimeUtc(dest) > provisionedAtUtc)
                {
                    // The user (or another writer) changed this file WHILE the run was working. An
                    // unattended run must not overwrite that.
                    conflicts++;
                    _logger.SensitiveWarning("Run {RunId} promotion conflict on {Path}", runId, rel);
                    continue;
                }

                File.Copy(src, dest, overwrite: true);
                promoted++;
            }
            catch (Exception ex)
            {
                // One unreadable or locked file must not abandon the rest of the deliverable. Counted as
                // skipped: nothing was written, and the file is still in the workspace.
                skipped++;
                _logger.LogWarning(ex, "Run {RunId} could not promote one file", runId);
            }
        }

        // B14 / plan D8, and it belongs HERE rather than at the caller: the open-file chips an interactive
        // run built point into the workspace, and the caller tears that workspace down the moment this
        // returns. Recorded for the whole workspace, not per promoted file — a byte-identical SKIPPED file is
        // also at the destination, and its chip has to keep opening too. Worktree mode never reaches this
        // method, which is correct: there the file is on a branch, not at a path.
        RunWorkspaceRedirects.Record(runRoot, destination);

        // Counts, ids and enum values only: this line lands in a support-attachable release log and there is
        // no SensitiveError helper, so a path never appears above Debug/Warning-sensitive severity.
        _logger.LogInformation(
            "Run {RunId} promoted {PromotedCount} file(s), skipped {SkippedCount}, {ConflictCount} conflict(s)",
            runId, promoted, skipped, conflicts);
        // A CONFLICT means the run's version of that file was deliberately not written and exists ONLY here.
        // Telling the caller to keep the workspace is the difference between "we kept your edit" and "we
        // silently threw the run's work away": the retained workspace is what the publish affordance can
        // still offer, and re-running the promotion from there reports the same conflict count to the user.
        return new RunPromotionResult(
            RunWorkspaceMode.Copy, promoted, skipped, conflicts, BranchName: null, RetainWorkspace: conflicts > 0);
    }

    /// <summary>Size first, then SHA256 — the same identity test <c>SafeDirectoryMove</c> uses.</summary>
    private static bool IsByteIdentical(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;

        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>
    /// The files inside a run workspace that promotion may consider: the same ignore-pruned, vault-excluded,
    /// capped walk provisioning used on the way in (B6/B7), so <c>.git</c> — including one the model created
    /// itself in copy mode — never travels back out.
    /// </summary>
    private static (List<string> Rels, bool OverCap) CollectPromotableFiles(
        string runRoot, string destination, CancellationToken ct)
    {
        var ignore = SandboxIgnore.ForRoot(runRoot);
        var vaultRoots = new[]
        {
            AssistantWorkspace.VaultRootFor(runRoot),
            AssistantWorkspace.VaultRootFor(destination),
        };
        var (rels, _, overCap) = CollectSourceFiles(runRoot, vaultRoots, ignore, ct);
        return (rels, overCap);
    }

    public Task<RunWorkspaceOutcome?> DescribeAsync(Guid runId, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var meta = ReadMetadata(runId);
                if (meta is null || meta.ParsedMode == RunWorkspaceMode.None)
                    return (RunWorkspaceOutcome?)null;

                // A TORN-DOWN WORKTREE answers from the document alone, and it has to: the panel reads this
                // only for a TERMINAL run (RunProgressViewModel), and on the clean path promotion has already
                // torn the directory down before the run is marked Completed (B8). Requiring the directory
                // here meant D5b's "your output is on branch X" line could render for a FAILED worktree run
                // and never for a successful one — the exact inverse of what it exists for. Nothing is
                // publishable as files: the branch carries the work.
                if (meta.TornDownAtUtc is not null)
                    return meta.ParsedMode == RunWorkspaceMode.Worktree
                        ? new RunWorkspaceOutcome(RunWorkspaceMode.Worktree, meta.Branch, HasUnpublishedFiles: false)
                        : null;

                var root = RootFor(runId);
                if (!Directory.Exists(root))
                    return null;

                // Worktree mode has nothing to publish as FILES — the branch is the deliverable (plan D5b),
                // so the panel offers the branch line and no publish button. Copy mode has something to
                // publish when the run wrote at least one file after provisioning; that is the same
                // "newer than provisionedAtUtc" test the promote set uses (B7), asked as a yes/no.
                var unpublished = meta.ParsedMode == RunWorkspaceMode.Copy
                    && meta.ProvisionedAtUtc > DateTime.MinValue
                    && HasPromotableFileNewerThan(root, meta.SourceRoot!, meta.ProvisionedAtUtc, ct);

                return new RunWorkspaceOutcome(meta.ParsedMode, meta.Branch, unpublished);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId} workspace could not be described", runId);
                return null;
            }
        }, ct);

    public async Task TearDownAsync(Guid runId, CancellationToken ct)
    {
        var root = RootFor(runId);
        var meta = ReadMetadata(runId);

        // Unreadable/absent/foreign-version metadata gets a plain recursive delete and NO prune: nothing
        // says where the repository is, and guessing is worse than leaving one registration behind for the
        // orphan sweep to find.
        await TearDownWithoutMetadataAsync(
            runId, root, meta?.ParsedMode ?? RunWorkspaceMode.None, meta?.MainWorktree, ct).ConfigureAwait(false);

        // Last, so a crash between the two leaves a metadata document the orphan sweep can still act on.
        //
        // A WORKTREE's document is not deleted but STAMPED as torn down and left behind. Two reasons, both
        // load-bearing: D5b's branch line is the only place a successful worktree run's output is named, and
        // the panel asks for it AFTER the terminal settle — i.e. after this method has run; and the stub keeps
        // MainWorktree, so a `worktree remove` that failed above can still be pruned by the metadata sweep.
        // It is a record, not an orphan, so the sweep ages it out instead of removing it on sight.
        if (meta?.ParsedMode == RunWorkspaceMode.Worktree)
        {
            meta.TornDownAtUtc = DateTime.UtcNow;
            if (TryWriteMetadata(runId, meta))
                return;
            // The stub could not be written: fall through and delete, so the sweep is not left reasoning
            // about a document that still claims a live workspace.
        }

        TryDeleteFile(MetadataPathFor(runId));
    }

    public async Task SweepOrphanMetadataAsync(CancellationToken ct)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(_runsBaseDir)) return;
            files = await Task.Run(() => Directory.GetFiles(_runsBaseDir, "*" + MetadataSuffix), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run-workspace metadata sweep: failed to enumerate the runs base directory");
            return;
        }

        var removed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);
            var idPart = name[..^MetadataSuffix.Length];
            if (!Guid.TryParse(idPart, out var runId))
                continue;

            // A workspace that still exists is still live (or still awaiting a publish decision) — leaving
            // it alone is what keeps this pass from being a "delete everything" sweep.
            if (Directory.Exists(RootFor(runId)))
                continue;

            var meta = ReadMetadata(runId);

            // A torn-down worktree stub is a RECORD, not an orphan: its directory is gone by design and its
            // registration was already removed or pruned at teardown. Keep it while the panel may still read
            // D5b's branch line off it, on the same window a settled run's workspace gets.
            if (meta?.TornDownAtUtc is { } tornDownAt && DateTime.UtcNow - tornDownAt < TornDownStubMaxAge)
                continue;

            if (meta?.ParsedMode == RunWorkspaceMode.Worktree && !string.IsNullOrEmpty(meta.MainWorktree))
                await PruneWorktreesAsync(meta.MainWorktree!, ct).ConfigureAwait(false);

            TryDeleteFile(file);
            removed++;
        }

        if (removed > 0)
            _logger.LogInformation("Run-workspace metadata sweep removed {Count} orphaned document(s)", removed);
    }

    // ---- provisioning strategies ----

    private sealed record WorktreeProvision(string MainWorktree, string Branch);

    /// <summary>
    /// The worktree gate. All four conditions must hold, else the caller takes copy mode; every fault below
    /// degrades and none of them fails the run (plan R16):
    /// <list type="bullet">
    /// <item>F1 git is not installed;</item>
    /// <item>F2 the source root is not a repository (<c>rev-parse --show-toplevel</c> exit ≠ 0 or empty
    /// stdout — gate on the exit code, never on a literal <c>false</c>);</item>
    /// <item>F3 git could not be launched at all (the runner's <c>-1</c> start-failure sentinel);</item>
    /// <item>F4 git timed out;</item>
    /// <item>F5 the toplevel cannot be canonicalized;</item>
    /// <item>F6 the toplevel is outside the assistant files folder — the same absolute invariant
    /// <c>GitToolHandler</c> enforces, so provisioning cannot open a side door to a repository the git
    /// tools already refuse;</item>
    /// <item>F7 the repository has no commits (unborn HEAD): a worktree starts from a COMMIT;</item>
    /// <item>F8 <c>worktree add</c> failed for any reason (branch exists, locked index, target not empty);</item>
    /// <item>F9 any exception on the git path.</item>
    /// </list>
    /// </summary>
    private async Task<WorktreeProvision?> TryProvisionWorktreeAsync(
        Guid runId, string runRoot, string sourceRoot, string settingsFolder, CancellationToken ct)
    {
        try
        {
            if (!_runner.IsGitInstalled)
                return null; // F1

            var toplevel = await RunGitAsync(sourceRoot, ["rev-parse", "--show-toplevel"], GitCommandKind.ReadOnly, ct)
                .ConfigureAwait(false);
            if (!toplevel.Succeeded || string.IsNullOrWhiteSpace(toplevel.StandardOutput))
                return null; // F2/F3/F4

            string canonicalTop;
            try
            {
                // git prints a forward-slash absolute path; normalize then canonicalize (resolve reparse
                // points) so the containment comparison below cannot be fooled by a junction.
                canonicalTop = SafeFolderPath.Canonicalize(Path.GetFullPath(toplevel.StandardOutput.Trim()));
            }
            catch
            {
                return null; // F5
            }

            if (!IsInsideOrEqual(canonicalTop, settingsFolder))
            {
                _logger.LogInformation(
                    "Run {RunId} worktree mode declined: the repository toplevel is outside the assistant files folder", runId);
                return null; // F6
            }

            var head = await RunGitAsync(canonicalTop, ["rev-parse", "--verify", "HEAD"], GitCommandKind.ReadOnly, ct)
                .ConfigureAwait(false);
            if (!head.Succeeded)
            {
                _logger.LogInformation("Run {RunId} worktree mode declined: the repository has no commits", runId);
                return null; // F7
            }

            // Measured against git 2.52: `worktree add` accepts an EXISTING EMPTY directory (exit 0) and
            // refuses a non-empty one (exit 128), so the directory created above needs no delete-first
            // dance — and a resume whose metadata was lost lands on F8 and degrades to copy.
            var branch = RunBranchPrefix + runId;
            var add = await RunGitAsync(
                canonicalTop, ["worktree", "add", runRoot, "-b", branch], GitCommandKind.Mutating, ct)
                .ConfigureAwait(false);
            if (!add.Succeeded)
            {
                _logger.SensitiveWarning(
                    "Run {RunId} worktree add failed ({Exit}): {Err}", runId, add.ExitCode, add.StandardError);
                return null; // F8
            }

            return new WorktreeProvision(canonicalTop, branch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} worktree provisioning faulted; falling back to copy mode", runId);
            return null; // F9
        }
    }

    /// <summary>
    /// Copy mode: the source tree is copied IN, bounded and ignore-pruned. This is not optional (B6) — an
    /// unattended run reads the user's existing files through the same tool set it writes with, so an empty
    /// workspace silently breaks "summarise notes.md".
    /// <para>
    /// Two exclusions, each for a stated reason. The <b>memory vault</b> is owned by <c>MemoryService</c>,
    /// the vault watcher and the ingest indexer, which write through their own paths and not through the
    /// file tools — a copy-in/copy-back cycle would fight the indexer and the copy would be stale the
    /// moment the vault is written; the run keeps full memory access through the memory tools, which do not
    /// read <c>WorkspaceRoot</c> at all. Everything <see cref="SandboxIgnore"/> prunes (<c>.git</c>,
    /// <c>bin</c>, <c>obj</c>, <c>node_modules</c> and any <c>.gitignore</c>/<c>.piaignore</c> entry) is
    /// excluded too, so what the run can see in its workspace is exactly what <c>list_files</c> would have
    /// listed in the real folder.
    /// </para>
    /// </summary>
    private Task<bool> CopyInAsync(Guid runId, string runRoot, string sourceRoot, string settingsFolder, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var ignore = SandboxIgnore.ForRoot(sourceRoot);
                var vaultRoots = new[]
                {
                    AssistantWorkspace.VaultRootFor(settingsFolder),
                    AssistantWorkspace.VaultRootFor(sourceRoot),
                };

                // Enumerate FIRST and cap before copying a single byte, so an over-cap source leaves no
                // partial tree behind at all.
                var (rels, bytes, overCap) = CollectSourceFiles(sourceRoot, vaultRoots, ignore, ct);
                if (overCap)
                {
                    _logger.LogInformation(
                        "Run {RunId} workspace provisioning skipped: source exceeds the isolation cap ({FileCount} files, {ByteCount} bytes)",
                        runId, rels.Count, bytes);
                    return false;
                }

                foreach (var rel in rels)
                {
                    ct.ThrowIfCancellationRequested();
                    var src = Path.Combine(sourceRoot, rel);
                    var dest = Path.Combine(runRoot, rel);
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    // File.Copy preserves the source's LastWriteTime, which is what makes a copied-in file
                    // OLDER than provisionedAtUtc and therefore invisible to the promote set (B7).
                    File.Copy(src, dest, overwrite: true);
                }

                _logger.LogInformation(
                    "Run {RunId} workspace copied in {FileCount} file(s), {ByteCount} bytes", runId, rels.Count, bytes);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId} workspace copy failed; the run will not be isolated", runId);
                return false;
            }
        }, ct);

    /// <summary>
    /// Walks <paramref name="source"/> the way <c>list_files</c> does — pruning ignored directories before
    /// descending, discarding anything that escapes the root after junction/symlink resolution, and skipping
    /// protected system/app-data files — and returns the sandbox-relative paths plus their total size.
    /// <c>OverCap</c> short-circuits the walk the moment either bound is exceeded.
    /// </summary>
    private static (List<string> Rels, long Bytes, bool OverCap) CollectSourceFiles(
        string source, string[] vaultRoots, GitignoreMatcher ignore, CancellationToken ct)
    {
        var rels = new List<string>();
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(source);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var relDir = Path.GetRelativePath(source, sub).Replace('\\', '/');
                if (ignore.IsIgnored(relDir, isDirectory: true)) continue;
                if (vaultRoots.Any(v => IsInsideOrEqual(sub, v))) continue;
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(source, sub, out _)) continue;
                stack.Push(sub);
            }

            foreach (var full in Directory.EnumerateFiles(dir))
            {
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(source, full, out var canon)) continue;
                if (SensitivePathGuard.IsBlocked(canon, out _)) continue;

                var rel = Path.GetRelativePath(source, full);
                if (ignore.IsIgnored(rel.Replace('\\', '/'), isDirectory: false)) continue;

                long length;
                try { length = new FileInfo(full).Length; }
                catch { continue; }

                rels.Add(rel);
                bytes += length;
                if (rels.Count > MaxProvisionedFiles || bytes > MaxProvisionedBytes)
                    return (rels, bytes, true);
            }
        }

        return (rels, bytes, false);
    }

    // ---- teardown ----

    /// <summary>
    /// Mode-symmetric removal of the workspace directory, WITHOUT touching the metadata document — shared
    /// by <see cref="TearDownAsync"/> and by provisioning's own rollback (where no document exists yet).
    /// The run branch is never deleted: it is the deliverable (B10).
    /// </summary>
    private async Task TearDownWithoutMetadataAsync(
        Guid runId, string root, RunWorkspaceMode mode, string? mainWorktree, CancellationToken ct)
    {
        if (mode == RunWorkspaceMode.Worktree && !string.IsNullOrEmpty(mainWorktree))
        {
            var removed = false;
            try
            {
                var result = await RunGitAsync(
                    mainWorktree!, ["worktree", "remove", "--force", root], GitCommandKind.Mutating, ct)
                    .ConfigureAwait(false);
                removed = result.Succeeded;
                if (!removed)
                    _logger.SensitiveWarning(
                        "Run {RunId} worktree remove failed ({Exit}): {Err}", runId, result.ExitCode, result.StandardError);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId} worktree remove faulted", runId);
            }

            // The fallback is the half of plan R5 that actually leaks: delete the directory ourselves, then
            // PRUNE, or the user's repository keeps a .git/worktrees/<id> registration forever.
            await Task.Run(() => TryDeleteDirectory(root), ct).ConfigureAwait(false);
            if (!removed)
                await PruneWorktreesAsync(mainWorktree!, ct).ConfigureAwait(false);
            return;
        }

        await Task.Run(() => TryDeleteDirectory(root), ct).ConfigureAwait(false);
    }

    private async Task PruneWorktreesAsync(string mainWorktree, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(mainWorktree, ["worktree", "prune"], GitCommandKind.Mutating, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worktree prune faulted");
        }
    }

    // ---- metadata ----

    /// <summary>
    /// Wire shape of <c>&lt;runsBase&gt;\&lt;runId&gt;.workspace.json</c>. It lives OUTSIDE the workspace on
    /// purpose: the run's file tools are contained to the workspace root, so a document inside it would be
    /// model-writable and the agent could steer its own promotion. <c>mode</c> is a NAME, so a member this
    /// build does not know reads back as <see cref="RunWorkspaceMode.None"/> — "no workspace", the
    /// restrictive direction.
    /// </summary>
    private sealed class WorkspaceMetadataDto
    {
        public int V { get; set; }
        public string? Mode { get; set; }
        public string? SourceRoot { get; set; }
        public string? MainWorktree { get; set; }
        public string? Branch { get; set; }
        public DateTime ProvisionedAtUtc { get; set; }

        /// <summary>True when git WAS available and worktree mode was nonetheless declined — usually because
        /// the source root simply is not a repository, which is the ordinary case. Written for the record
        /// only: surfacing "this run ran unisolated / in copy mode" to the user needs a panel line and a
        /// sixth loc key, which §13.1 books as its own work.</summary>
        public bool Degraded { get; set; }

        /// <summary>
        /// Set when the workspace DIRECTORY is gone and this document is only a record of where the output
        /// went — written by <see cref="TearDownAsync"/> for worktree mode so D5b's branch line survives the
        /// teardown that precedes the terminal settle. An ADDITIVE member of <c>v:1</c>: a build that does not
        /// know it reads the document as a live workspace whose directory has vanished, which every consumer
        /// already handles restrictively.
        /// </summary>
        public DateTime? TornDownAtUtc { get; set; }

        /// <summary>The parsed <see cref="Mode"/>, or <see cref="RunWorkspaceMode.None"/> for a name this
        /// build does not know.</summary>
        public RunWorkspaceMode ParsedMode =>
            Enum.TryParse<RunWorkspaceMode>(Mode, ignoreCase: false, out var parsed) ? parsed : RunWorkspaceMode.None;
    }

    /// <summary>Reads the metadata document, or null when it is absent, unparseable, of a version this build
    /// does not understand, or carries no mode at all. Never throws.</summary>
    private WorkspaceMetadataDto? ReadMetadata(Guid runId)
    {
        try
        {
            var path = MetadataPathFor(runId);
            if (!File.Exists(path)) return null;

            var meta = JsonSerializer.Deserialize<WorkspaceMetadataDto>(File.ReadAllText(path), MetadataJsonOptions);
            // The version check is an EXACT equality, and a document must carry both the mode and the source
            // root it was provisioned from: promotion and teardown are meaningless without them, and
            // "no readable metadata" degrades restrictively everywhere it is consumed.
            if (meta is null || meta.V != MetadataVersion
                || string.IsNullOrEmpty(meta.Mode) || string.IsNullOrEmpty(meta.SourceRoot))
            {
                _logger.LogInformation("Run {RunId} workspace metadata is not readable by this build", runId);
                return null;
            }
            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} workspace metadata could not be read", runId);
            return null;
        }
    }

    private bool TryWriteMetadata(Guid runId, WorkspaceMetadataDto meta)
    {
        try
        {
            File.WriteAllText(MetadataPathFor(runId), JsonSerializer.Serialize(meta, MetadataJsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} workspace metadata could not be written; isolation is abandoned", runId);
            return false;
        }
    }

    // ---- helpers ----

    /// <summary>
    /// The root the workspace is provisioned FROM: the configured assistant files folder, narrowed by
    /// <paramref name="workingSubpath"/> exactly as the file tools narrow it (resolve inside, must exist,
    /// otherwise fall back to the base — never widen). Returns the canonicalized pair
    /// (source root, settings folder), or nulls when no usable folder is configured.
    /// </summary>
    private async Task<(string? SourceRoot, string? SettingsFolder)> ResolveSourceRootAsync(string? workingSubpath)
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var folder = settings.AssistantFilesFolder;
        if (string.IsNullOrWhiteSpace(folder)) return (null, null);

        string canonicalFolder;
        try
        {
            var full = Path.GetFullPath(folder);
            if (!Directory.Exists(full)) return (null, null);
            canonicalFolder = SafeFolderPath.Canonicalize(full);
        }
        catch { return (null, null); }

        if (!string.IsNullOrWhiteSpace(workingSubpath)
            && SafeFolderPath.TryResolveInsideAllowingAbsolute(canonicalFolder, workingSubpath, out var narrowed)
            && Directory.Exists(narrowed))
        {
            return (narrowed, canonicalFolder);
        }

        return (canonicalFolder, canonicalFolder);
    }

    private Task<GitProcessResult> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> args, GitCommandKind kind, CancellationToken ct)
        // Ceiling = the parent of the working directory, mirroring GitToolHandler: upward .git discovery may
        // reach the working directory itself but never cross above it, so provisioning can never bind a
        // repository the user keeps further up their profile.
        => _runner.RunAsync(new GitProcessRequest(workingDirectory, args, kind, TryParentOf(workingDirectory)), ct);

    private static string? TryParentOf(string path)
    {
        try { return Directory.GetParent(path)?.FullName; }
        catch { return null; }
    }

    /// <summary>Inclusive containment: <paramref name="candidate"/> equals <paramref name="root"/> or is
    /// nested under it. Trailing-separator-aware, so a sibling sharing the prefix is not "inside".</summary>
    private static bool IsInsideOrEqual(string candidate, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        string full, fullRoot;
        try
        {
            full = Path.GetFullPath(candidate);
            fullRoot = Path.GetFullPath(root);
        }
        catch { return false; }

        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(SafeFolderPath.WithTrailingSeparator(fullRoot), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Would a promotion have anything to carry out of here?" — asked over exactly the set
    /// <see cref="CopyOut"/> walks (<see cref="CollectPromotableFiles"/>), not over every file on disk. The
    /// two must agree or the panel offers to publish a workspace whose only new files are ones promotion
    /// prunes (a <c>.git</c> the model created in copy mode is the realistic case) and the user gets an offer
    /// that publishes nothing.
    /// </summary>
    private static bool HasPromotableFileNewerThan(
        string root, string destination, DateTime provisionedAtUtc, CancellationToken ct)
    {
        var (rels, overCap) = CollectPromotableFiles(root, destination, ct);
        // Over the cap the walk short-circuits, so the list is a prefix rather than the whole set: there ARE
        // files, which is all this question asks.
        if (overCap) return true;

        foreach (var rel in rels)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(Path.Combine(root, rel)) > provisionedAtUtc) return true;
            }
            catch { /* a vanished or locked file is not evidence either way */ }
        }
        return false;
    }

    private static string TryCanonicalize(string path)
    {
        try { return Directory.Exists(path) ? SafeFolderPath.Canonicalize(path) : path; }
        catch { return path; }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete a run workspace directory");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete a run workspace metadata document");
        }
    }
}
