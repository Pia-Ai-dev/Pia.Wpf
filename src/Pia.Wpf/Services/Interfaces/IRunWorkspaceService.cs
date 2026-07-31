namespace Pia.Services.Interfaces;

/// <summary>
/// How a run's workspace was provisioned. Serialized by NAME into the workspace metadata document, so this
/// is APPEND-ONLY: never renumber, never rename a member. A name a build does not know reads back as
/// <see cref="None"/>, which means "no isolation" — the restrictive direction (Batch 06 B5).
/// </summary>
public enum RunWorkspaceMode
{
    /// <summary>No isolation: the run writes straight into the assistant files folder (the pre-Batch-06
    /// behaviour, and the degrade every provisioning fault ultimately falls back to).</summary>
    None = 0,

    /// <summary>The source tree was copied into <c>runs\&lt;runId&gt;</c>, bounded and ignore-pruned (B6).</summary>
    Copy = 1,

    /// <summary><c>runs\&lt;runId&gt;</c> is a git worktree of the source repository on its own run branch (plan D5).</summary>
    Worktree = 2,
}

/// <summary>An isolated run workspace, as provisioned. <paramref name="Root"/> is the canonicalized directory
/// every file (and git) operation of the run resolves against; <paramref name="SourceRoot"/> is the root it
/// was provisioned FROM and, in copy mode, the destination a promotion writes back to (B9).</summary>
public sealed record RunWorkspace(Guid RunId, string Root, RunWorkspaceMode Mode, string SourceRoot, string? BranchName);

/// <summary>
/// Outcome of a promotion. Counts only — never a path (privacy-first logging, plan R7).
/// <para>
/// <paramref name="RetainWorkspace"/> is the one member the CALLER must obey: it means the workspace still
/// holds work this promotion did not move, so tearing it down would destroy the only copy. Set for a copy-mode
/// conflict (B7 keeps the user's newer file and skips the run's version) and for a worktree the run-branch
/// commit could not fully take. Trailing and defaulted so every existing construction keeps meaning
/// "everything moved; the workspace is finished with".
/// </para>
/// </summary>
public sealed record RunPromotionResult(
    RunWorkspaceMode Mode, int Promoted, int Skipped, int Conflicts, string? BranchName, bool RetainWorkspace = false);

/// <summary>
/// What the panel needs to tell the user where a settled run's output is (plan D5b / Batch 06 B15).
/// <para>
/// <paramref name="BranchName"/> is null in worktree mode until the run branch actually RECEIVED the run's
/// work: naming a branch that got nothing is the same "where is my file?" failure D5b exists to prevent,
/// pointed the other way, and it was reachable on two arms (a run-branch commit that failed, and a
/// failed/cancelled run that never promoted at all). Withholding the name is paired with
/// <paramref name="HasUnpublishedFiles"/> on those arms, so the panel offers the recovery instead of a claim.
/// </para>
/// <para>
/// <paramref name="Conflicts"/> is how many files the last promotion deliberately left alone because the user
/// changed them while the run was working (B7). Trailing and defaulted: it carries the count from an AUTOMATIC
/// promotion to the panel, which had no channel for it and could only render the number once the user clicked
/// Publish and it was re-counted (Lens A 5 / Lens B 3's remaining half).
/// </para>
/// </summary>
public sealed record RunWorkspaceOutcome(
    RunWorkspaceMode Mode, string? BranchName, bool HasUnpublishedFiles, int Conflicts = 0);

/// <summary>
/// Owns the lifecycle of a run's isolated workspace: provisioning it in one of two modes — a git worktree
/// when the source root is a repository the git tools may touch, else a bounded copy of the source tree
/// (plan D5) — and the SYMMETRIC teardown each mode needs. One interface, one implementation, two
/// strategies (Batch 06 B4): a worktree's teardown is <c>git worktree remove</c>/<c>prune</c>, not
/// <c>rmdir</c>, and splitting create from teardown across two types is exactly the shape that lets the
/// two drift (plan R16).
/// <para>
/// Every member is BOOKKEEPING and must never fail a run (standing guardrail 1): nothing here throws,
/// and every method returns <c>null</c> / does nothing on any fault.
/// <see cref="ProvisionAsync"/> returning <c>null</c> means "no isolation — the pre-Batch-06 behaviour",
/// which every caller handles by passing <c>workspaceRoot: null</c> onward.
/// </para>
/// <para>
/// Registered as a SINGLETON. A child run (Batch 07) resolves the same instance and must inherit its
/// parent's workspace rather than provisioning its own: promotion is terminal-only and once per workspace
/// (B7), so two provisionings under one logical run would race each other's promote set.
/// </para>
/// </summary>
public interface IRunWorkspaceService
{
    /// <summary>The workspace directory for <paramref name="runId"/> — <c>&lt;runsBase&gt;\&lt;runId&gt;</c>.
    /// Pure path arithmetic: it does not create, canonicalize or check anything.</summary>
    string RootFor(Guid runId);

    /// <summary>
    /// Provision (or, on a resume, re-enter) the run's workspace. Idempotent: a run that already has a
    /// readable metadata document comes back to the same root, the same mode and the same provisioning
    /// instant, because the promote set is decided against that one timestamp (B7/B11).
    /// </summary>
    /// <param name="workingSubpath">The chat's working subpath, narrowing the source root exactly as the
    /// file tools would; null for an unattended run. The workspace root corresponds 1:1 to the NARROWED
    /// source root, so an isolated run's own ambient subpath must then be null — narrowing twice would
    /// look for <c>&lt;runRoot&gt;\&lt;subpath&gt;</c> (B6).</param>
    /// <returns>The provisioned workspace, or <c>null</c> for "no isolation".</returns>
    Task<RunWorkspace?> ProvisionAsync(Guid runId, string? workingSubpath, CancellationToken ct);

    /// <summary>
    /// Promote what the run wrote out of its workspace: in copy mode back to the source root it was
    /// provisioned from, in worktree mode by COMMITTING it onto the run branch, because the branch IS the
    /// deliverable (plan D5b) and an unattended run cannot commit for itself. <c>null</c> means nothing was
    /// promoted and the workspace is intact; a non-null result whose
    /// <see cref="RunPromotionResult.RetainWorkspace"/> is set means the workspace must be kept anyway.
    /// </summary>
    Task<RunPromotionResult?> PromoteAsync(Guid runId, CancellationToken ct);

    /// <summary>The mode, branch, "are there still files in there" flag and last conflict count for a settled
    /// run's workspace, or <c>null</c> when the run has none (it never had one, or its copy-mode workspace was
    /// already promoted and torn down). A torn-down WORKTREE still describes: its branch is the deliverable and
    /// the panel has to be able to name it after the terminal settle (D5b), so teardown leaves a metadata stub
    /// behind. Every answer comes from the metadata document and the disk — <b>no git process is spawned
    /// here</b>, which is why "did the branch receive the commit?" is a RECORDED fact rather than a question
    /// asked at describe time: this runs off the dispatcher on every terminal <c>RunChanged</c>.</summary>
    Task<RunWorkspaceOutcome?> DescribeAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Remove the workspace the way its mode requires — <c>git worktree remove</c> (falling back to
    /// <c>rmdir</c> + <c>git worktree prune</c>) for a worktree, a recursive delete for a copy — and drop
    /// the metadata document last. NEVER deletes the run branch: it is the deliverable, which is also why a
    /// worktree's document is REPLACED BY A STUB rather than deleted (see <see cref="DescribeAsync"/>).
    /// <para>
    /// A worktree whose directory SURVIVED the removal (something still holds a handle) keeps its document
    /// un-stamped instead: that document is the only record of which repository holds the worktree registration,
    /// and a later sweep re-enters this method through it to retry. Idempotent, therefore, and safe to call again.
    /// </para>
    /// </summary>
    Task TearDownAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Delete metadata documents whose workspace directory is already gone, pruning the stale worktree
    /// registration first. The startup sweep enumerates DIRECTORIES only, so without this pass the
    /// documents — and, in worktree mode, the <c>.git/worktrees/&lt;id&gt;</c> entry they know how to prune —
    /// would accumulate in the user's repository forever (plan R5). A torn-down worktree STUB is not an
    /// orphan while the panel may still read it, so it is aged out rather than removed on sight.
    /// </summary>
    Task SweepOrphanMetadataAsync(CancellationToken ct);
}
