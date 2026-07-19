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
/// <param name="GrantedWrites">Write tools this run may execute; null = default {write_file, delete_file}.</param>
/// <param name="Budget">Budget envelope; null = the scheduled budget from settings.</param>
public sealed record HeadlessRunRequest(
    string Goal,
    AgentRunTrigger Trigger,
    Guid? TriggerRef = null,
    Guid? OwnerDeviceId = null,
    Guid? ProviderId = null,
    IReadOnlyCollection<string>? GrantedWrites = null,
    RunProfile? Budget = null);

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
    /// the returned <see cref="HeadlessRunHandle.Completion"/> settles when the run reaches a terminal state.
    /// </summary>
    Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct = default);

    /// <summary>Cancel every in-flight run and bounded-await their settle (app shutdown, G-4).</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Startup sweep (decision c): delete orphaned (run row gone → chat cascaded away) and aged
    /// (&gt; 30 days) run-workspace directories under <c>%LOCALAPPDATA%\Pia\runs</c>.
    /// </summary>
    Task RunStartupSweepAsync(CancellationToken ct);
}
