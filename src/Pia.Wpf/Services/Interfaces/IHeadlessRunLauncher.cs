using Pia.Models;
using Pia.Services;

namespace Pia.Services.Interfaces;

/// <summary>
/// Request to detach a goal as an unattended headless <see cref="RunShape.Planned"/> run (§17.1).
/// The primary producer is the user-facing "Run in background" command; the scheduler emits the same
/// request for an <c>AgentTask</c> job (secondary producer).
/// </summary>
/// <param name="Goal">The run goal (user content — never logged at Information level).</param>
/// <param name="Trigger">Provenance (User for the detach command, Schedule for a scheduled job).</param>
/// <param name="TriggerRef">e.g. the <c>ScheduledJob.Id</c> for a scheduled run.</param>
/// <param name="OwnerDeviceId">Owning device, carried onto the run row (scheduled jobs).</param>
/// <param name="ProviderId">Explicit provider (scheduled job); null = persona-preferred/default.</param>
/// <param name="GrantedWrites">
/// Write tools this run may execute. <c>null</c> = <see cref="HeadlessRunRequest.DefaultGrantedWrites"/>;
/// an explicitly EMPTY collection means no write grants at all and is honoured as such.
/// </param>
/// <param name="Budget">Budget envelope; null = the scheduled budget from settings.</param>
public sealed record HeadlessRunRequest(
    string Goal,
    AgentRunTrigger Trigger,
    Guid? TriggerRef = null,
    Guid? OwnerDeviceId = null,
    Guid? ProviderId = null,
    IReadOnlyCollection<string>? GrantedWrites = null,
    RunProfile? Budget = null)
{
    /// <summary>
    /// The write grants an unattended run receives when the request names none (A1). Deliberately
    /// <c>{write_file}</c> only: an unattended run still writes real deliverables into the shared
    /// assistant files folder (owner decision d1bf62d), but it must not be able to DESTROY existing
    /// files unless the caller explicitly asked for that — an explicit <see cref="GrantedWrites"/>
    /// containing <c>delete_file</c> still works. Single source of truth: the launcher applies it and
    /// the scheduled-job approval card renders it as the effective default.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultGrantedWrites = ["write_file"];
}

/// <summary>Handle to a launched headless run — its ids and a task that completes when the run settles.</summary>
public sealed record HeadlessRunHandle(Guid RunId, Guid ChatId, Task Completion);

/// <summary>
/// Launches and supervises unattended headless agent runs (§17.1/17.5). Singleton: owns the shared
/// concurrency cap, the shutdown token, and the in-flight/cleanup maps. Each run executes in a fresh
/// DI scope with its own linked CTS and its own isolated workspace under
/// <c>%LOCALAPPDATA%\Pia\runs\&lt;runId&gt;</c>.
/// </summary>
public interface IHeadlessRunLauncher
{
    /// <summary>
    /// Stub-chat-first (G-3/R1) then create the Planned run, resolve persona/provider, seed the run
    /// workspace, and dispatch the orchestrator fire-and-forget. Returns once the run is queued/started;
    /// the returned <see cref="HeadlessRunHandle.Completion"/> settles when the run reaches a terminal state
    /// OR a budget pause (<see cref="AgentRunState.WaitingForInput"/>, a non-terminal park).
    /// </summary>
    Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct = default);

    /// <summary>
    /// Dispatch a CHILD run of <paramref name="parentRunId"/> (Batch 07 D7): the same path
    /// <see cref="LaunchAsync"/> takes, on a SEPARATE concurrency pool so siblings run in parallel while the
    /// parent awaits them — a nested acquire on the shared pool deadlocks permanently (07 §7.1).
    /// <para>
    /// Three things differ from a plain launch, all of them narrowing. The child's grant envelope is derived
    /// from <paramref name="parentPolicyJson"/> and is a strict subset of it, never the launch default and
    /// never the resume floor (Phase 3 R13). The child runs INSIDE the parent's workspace
    /// (<paramref name="parentWorkspaceRoot"/> — the orchestrator's <c>ctx.WorkspaceRoot</c>) and provisions
    /// nothing of its own, because promotion is once per workspace (Batch 06 B7): <c>null</c> means the parent
    /// runs unisolated and the child then does too, so the two are always in the same regime. And the child's
    /// run row carries <c>ParentRunId</c>, which is what stops it delegating further and stops it promoting.
    /// </para>
    /// <para>
    /// <see cref="HeadlessRunHandle.Completion"/> carries the same caveat as on <see cref="LaunchAsync"/>: it
    /// settles on a budget PAUSE as well as on a terminal state, so a caller must re-read the run row before
    /// treating the child as finished.
    /// </para>
    /// </summary>
    /// <param name="personaId">The roster persona the DELEGATED STEP was assigned (its
    /// <c>AssignedPersonaId</c>), or null. Non-null makes it the child's RUN persona — its system prompt, its
    /// preferred provider and its reasoning effort — which is the whole substance of multi-persona: without it
    /// the child resolves the global per-mode persona and the specialist the plan chose never runs anywhere,
    /// while the panel still draws that specialist's avatar on the step. A REQUEST, never a guarantee: the
    /// launcher honours it only while it is still on the roster and still resolves, and otherwise takes the
    /// ordinary per-mode resolution rather than failing the dispatch.</param>
    Task<HeadlessRunHandle> LaunchChildAsync(
        HeadlessRunRequest req, Guid parentRunId, string? parentPolicyJson, string? parentWorkspaceRoot,
        Guid? personaId = null, CancellationToken ct = default);

    /// <summary>
    /// Cancel ONE in-flight run by id — the mechanism a parent's cascade uses when its own token fires
    /// (07 D16). A no-op for a run this process is not currently dispatching (a run parked in a previous
    /// process is not in the in-flight map), so a caller that needs the row settled must do that itself.
    /// Never throws.
    /// </summary>
    Task CancelAsync(Guid runId);

    /// <summary>Cancel every in-flight run and bounded-await their settle (app shutdown, G-4).</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Startup sweep (decision c): delete orphaned (run row gone → chat cascaded away) and aged
    /// (&gt; 30 days) run-workspace directories under <c>%LOCALAPPDATA%\Pia\runs</c>.
    /// </summary>
    Task RunStartupSweepAsync(CancellationToken ct);
}
