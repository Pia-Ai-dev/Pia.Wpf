using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IHeadlessRunLauncher"/>. Detaches a goal as an unattended
/// <see cref="RunShape.Planned"/> run (§17.1/17.5): stub-chat-first (G-3/R1), create the run, resolve
/// persona + provider, seed an isolated per-run workspace, and dispatch the orchestrator on a fresh DI
/// scope with its own linked CTS. A shared <see cref="SemaphoreSlim"/> caps concurrency; app shutdown
/// cancels + bounded-awaits in-flight runs so none is left <see cref="AgentRunState.Running"/> (G-4).
/// </summary>
public sealed class HeadlessRunLauncher : IHeadlessRunLauncher, IAgentRunResumeService, IDisposable
{
    /// <summary>Concurrency cap shared by both producers (decision d). A 3rd run queues on the slot.</summary>
    private readonly SemaphoreSlim _slots = new(2, 2);

    /// <summary>
    /// Concurrency cap for CHILD runs (Batch 07 D7) — deliberately a SECOND semaphore, and never
    /// <see cref="_slots"/>. A nested acquire on the shared pool DEADLOCKS, permanently: <see cref="_slots"/>
    /// is waited INSIDE the dispatch task and released only in the <c>finally</c> AFTER
    /// <c>orchestrator.RunAsync</c> RETURNS, so two parents holding the two permits while each awaits a child
    /// that needs a permit from the same pool can never release one. It takes exactly TWO concurrent parents —
    /// i.e. the configured cap — and nothing in the process can break it: <see cref="StopAsync"/>'s bounded
    /// 5-second wait times out and both runs dangle <c>Running</c> until the next startup sweep. Never merge
    /// these two pools "for simplicity" (07 §7.1).
    /// <para>
    /// Consequence, stated rather than hidden: effective provider concurrency doubles to 2+2. That is why the
    /// persona roster is the opt-in (07 D1) and why a delegating run's budget must still fit the envelope one
    /// scheduled job may occupy — <c>ScheduledJobBackgroundService</c> holds its <c>_runLock</c> across
    /// <c>await handle.Completion</c>, so a fan-out blocks every scheduled job for the parent's wall clock
    /// PLUS every descendant's (Phase 3 R15, and the halved child wall clock in the orchestrator's fan-out).
    /// </para>
    /// <para>
    /// WHY 2, and not wider: it MIRRORS <see cref="_slots"/> rather than the width of a fan-out, so the worst
    /// case a delegating build can put on the provider is a fixed 2+2 — a number that does not grow when a
    /// planner emits a 6-way group. A group wider than the pool is not starved, it runs in WAVES: every sibling
    /// is dispatched and awaited (the parent never returns from the wait with a live child, D16), so the only
    /// cost of a narrow pool is elapsed time inside the parent's own halved wall-clock budget. Raising it trades
    /// exactly that for more concurrent provider load and a longer <see cref="StopAsync"/> drain, and it must
    /// stay a SEPARATE number from <see cref="_slots"/> either way. It is deliberately not user-configurable:
    /// no setting exists for it, and the depth guard (a child never delegates, 07 §7.5) is what bounds the
    /// total rather than this cap.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _childSlots = new(2, 2);

    /// <summary>Cancelled once at shutdown; every run CTS is linked to it (G-4).</summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, Task Task)> _inflight = new();

    /// <summary>chat id → run ids launched this session, for same-session workspace cleanup on chat delete.</summary>
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _runsByChat = new();
    private readonly object _runsByChatLock = new();

    private static readonly TimeSpan _workspaceMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Retention floor for a run that has SETTLED (Batch 06 B12, plan D3's retention rule): an unanswered
    /// publish offer must not pin a workspace forever. A clean run's workspace is already gone — promotion
    /// tears it down before the run is marked Completed (B8) — so this window really only serves the
    /// failed/cancelled runs whose offer the user never answered. Anything NON-terminal keeps the 30-day
    /// floor above, because it may still be resumable. A judgement call, not a measurement: if seven days
    /// turns out to be short, it is one constant.
    /// </summary>
    private static readonly TimeSpan _terminalWorkspaceMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Resume FLOOR (D1): the grant set a resume falls back to when the launch envelope is missing or
    /// unreadable. Deliberately the NARROWEST useful grant — never a destructive one — because a resume
    /// must never be able to widen what the launch actually granted.
    /// </summary>
    private static readonly string[] ResumeFloorGrants = ["write_file"];

    /// <summary>
    /// Envelope shape currently written/understood by this launcher. Anything else → the floor.
    /// <para>
    /// Batch 04 added the <c>policy</c> member WITHOUT touching this (04 D1). The reader below compares with
    /// <c>!=</c>, so a bump would make every envelope written before that batch unreadable → the resume floor
    /// → and for an interactive-origin envelope (<c>grantedWrites: []</c>) the floor is WIDER than the launch,
    /// i.e. a silent escalation of every in-flight interactive run. <see cref="GrantEnvelopeJsonOptions"/>
    /// sets no <c>UnmappedMemberHandling</c>, so additive members interoperate in both directions for free.
    /// </para>
    /// </summary>
    private const int GrantEnvelopeVersion = 1;

    /// <summary>
    /// The exact document <c>SerializeGrantEnvelope([], AgentRunTrigger.User)</c> produces with no policy.
    /// Used by <c>ChatSessionManager</c> when serialization FAULTS: <c>null</c> there would make the resume
    /// fall back to <see cref="ResumeFloorGrants"/> (<c>{write_file}</c>), which is WIDER than what an
    /// interactive launch granted (nothing). Deliberately carries no <c>policy</c> member — a fault fallback
    /// grants nothing and auto-approves nothing, and narrower-on-fault is the only acceptable direction.
    /// Pinned byte-for-byte against the serializer by <c>HeadlessRunLauncherPolicyTests</c>.
    /// </summary>
    internal const string InteractiveEmptyEnvelopeJson = """{"v":1,"grantedWrites":[],"trigger":"User"}""";

    private static readonly JsonSerializerOptions GrantEnvelopeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAssistantChatService _chatService;
    private readonly IAgentRunService _agentRunService;
    private readonly ISettingsService _settingsService;
    private readonly IProviderService _providerService;
    private readonly IPersonaService _personaService;
    private readonly IExecutingRunStore _executingRuns;
    private readonly ILogger<HeadlessRunLauncher> _logger;

    /// <summary>Base directory for all run workspaces (<c>%LOCALAPPDATA%\Pia\runs</c> in production, precedent
    /// SqliteContext). Injectable so tests never point the destructive startup sweep at the real user folder.</summary>
    private readonly string _runsBaseDir;

    /// <summary>
    /// Owns BOTH workspace provisioning modes and their symmetric teardown (Batch 06 G3/plan D5). Trailing
    /// and defaulted: null is the pre-Batch-06 shape — a bare <c>CreateDirectory</c> at launch and a plain
    /// recursive delete on cleanup — which is what the existing launcher suite exercises.
    /// </summary>
    private readonly IRunWorkspaceService? _workspaces;

    /// <summary>
    /// Batch 08 D1: where each dispatch publishes its own cancel sink, so a user pause can interrupt the
    /// in-flight step of a run THIS process is running and the run's loop can tell that interrupt from a Stop.
    /// Trailing and defaulted: null ⇒ nothing registers a sink, no pause request can ever be recorded against a
    /// run of this launcher, and every cancel is the pre-Batch-08 terminal cancel.
    /// </summary>
    private readonly IRunSteeringStore? _steering;

    private bool _disposed;

    public HeadlessRunLauncher(
        IServiceScopeFactory scopeFactory,
        IAssistantChatService chatService,
        IAgentRunService agentRunService,
        ISettingsService settingsService,
        IProviderService providerService,
        IPersonaService personaService,
        IExecutingRunStore executingRuns,
        ILogger<HeadlessRunLauncher> logger,
        string? runsBaseDirOverride = null,
        IRunWorkspaceService? workspaces = null,
        IRunSteeringStore? steering = null)
    {
        _scopeFactory = scopeFactory;
        _chatService = chatService;
        _agentRunService = agentRunService;
        _settingsService = settingsService;
        _providerService = providerService;
        _personaService = personaService;
        _executingRuns = executingRuns;
        _logger = logger;
        // AssistantWorkspace.RunsRoot (not an inline Path.Combine) so the guard's carve-out
        // (SensitivePathGuard.BuildAllowedExceptions) and this default can never drift apart (Batch 06 B1).
        _runsBaseDir = runsBaseDirOverride ?? AssistantWorkspace.RunsRoot;
        _workspaces = workspaces;
        _steering = steering;

        // Decision c: delete a run's workspace when its chat (and, by FK cascade, its run) is deleted.
        _chatService.ChatsChanged += OnChatsChanged;
    }

    public Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct)
        => LaunchCoreAsync(req, parentRunId: null, _slots, childPolicyJson: null,
               workspaceRootOverride: null, personaIdOverride: null, ct);

    /// <inheritdoc />
    public Task<HeadlessRunHandle> LaunchChildAsync(
        HeadlessRunRequest req, Guid parentRunId, string? parentPolicyJson, string? parentWorkspaceRoot,
        Guid? personaId = null, CancellationToken ct = default)
        => LaunchCoreAsync(req, parentRunId, _childSlots,
               // NEVER the launch default and never the resume floor: a child's grants are a strict subset of
               // its parent's, with every delete-like name stripped (G9's NarrowForChild, Phase 3 R13).
               TrySerializeChildEnvelope(parentPolicyJson, req.Trigger, _logger),
               workspaceRootOverride: parentWorkspaceRoot, personaIdOverride: personaId, ct);

    /// <inheritdoc />
    public Task CancelAsync(Guid runId)
    {
        // Best-effort by design: only a run THIS PROCESS is dispatching is in _inflight, so a child parked in
        // a previous process is simply not here and the caller falls back to settling its row. RemoveInflight
        // guarantees a finishing dispatch cannot have evicted a live one, so the entry found here is the live
        // dispatch's own CTS. Never throws — a disposed source must not break a cascade.
        try
        {
            if (_inflight.TryGetValue(runId, out var entry))
            {
                entry.Cts.Cancel();
                _logger.LogInformation("Cancelled in-flight run {RunId}", runId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel in-flight run {RunId}", runId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The ONE dispatch path both <see cref="LaunchAsync"/> and <see cref="LaunchChildAsync"/> go through —
    /// extracted rather than forked, because the workspace decision, the bracket, the slot discipline and the
    /// four settle paths must never diverge between a parent and a child dispatch.
    /// </summary>
    /// <param name="parentRunId">Non-null ⇒ this is a CHILD run of that parent, recorded on the run row so
    /// the depth guard and the promotion guard can both read it (G9/G10).</param>
    /// <param name="slots">Which concurrency pool this dispatch queues on. A child MUST get
    /// <see cref="_childSlots"/> — see its remarks for why the shared pool deadlocks.</param>
    /// <param name="childPolicyJson">A child's pre-narrowed grant envelope, which REPLACES the resolve-from-
    /// request path entirely. Null ⇒ the ordinary launch resolution.</param>
    /// <param name="workspaceRootOverride">Non-null ⇒ SKIP <c>_workspaces.ProvisionAsync</c> entirely and pass
    /// this value straight to <c>executor.Initialize(workspaceRoot: …)</c>. A child run SHARES its parent's
    /// workspace: Batch 06 B7 allows exactly ONE promotion per workspace, decided by a single
    /// <c>provisionedAtUtc</c>, and in worktree mode a per-child workspace would mean N <c>git worktree add</c>
    /// calls and N branches per fan-out (06 §13.4 / 07 §7.6).</param>
    /// <param name="personaIdOverride">A delegated step's assigned roster persona (07 D3/D5), which becomes the
    /// CHILD's run persona instead of the global per-mode one. Null ⇒ the ordinary resolution.</param>
    private async Task<HeadlessRunHandle> LaunchCoreAsync(
        HeadlessRunRequest req, Guid? parentRunId, SemaphoreSlim slots, string? childPolicyJson,
        string? workspaceRootOverride, Guid? personaIdOverride, CancellationToken ct)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var persona = await ResolveRunPersonaAsync(personaIdOverride, settings).ConfigureAwait(false);
        // The provider ladder reads the persona: req.ProviderId (null for a child), then the persona's
        // PreferredProviderId, then the mode default, and it clones to apply ReasoningEffort. So handing it the
        // ASSIGNED persona is what gives a delegated step its specialist's provider and effort too — D5's
        // "each persona running on its own provider" — with no second ladder to keep in step.
        var provider = await ResolveProviderAsync(req.ProviderId, persona).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No provider configured for a headless agent run.");

        // G-3/R1: the AgentRuns FK requires its AssistantChats parent row first, and FK enforcement is ON.
        // Persist a stub chat up front (awaited — allowed to propagate); the executor finalizes it once at
        // EndRunAsync. On any failure path the stub remains so a Failed run's ChatId still resolves.
        await _chatService.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = DeriveTitle(req.Goal),
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            ProviderId = provider.Id,
            Messages = [],
        }, ct).ConfigureAwait(false);

        // Resolve the write grants BEFORE the run row exists so the resolved set can be persisted with it
        // (D1). A null GrantedWrites takes the narrow default; an explicitly EMPTY collection still means
        // "no write grants at all" and is preserved as such (never re-widened to the default).
        // A CHILD's envelope arrives ALREADY NARROWED from its parent's (G9) and must not be re-resolved from
        // the request or from settings: both would widen it. Read it back through the same reader a resume uses
        // so the executor gets exactly what was persisted, i.e. one source of truth per dispatch.
        var policyJson = childPolicyJson ?? TrySerializeGrantEnvelope(
            req.GrantedWrites ?? HeadlessRunRequest.DefaultGrantedWrites,
            req.Trigger,
            // The autonomy policy is resolved from SETTINGS at launch — the launch never reads the envelope
            // back, so there is nothing else to resolve it from (04 D9/D10). Off ⇒ null ⇒ the member is
            // omitted and the persisted document stays byte-identical to a pre-Batch-04 one.
            RunAutonomyPolicy.FromSettings(settings));

        var grants = childPolicyJson is null
            ? req.GrantedWrites ?? HeadlessRunRequest.DefaultGrantedWrites
            // Not `?? ResumeFloorGrants`: the floor is WIDER than a child that inherited nothing, and
            // TrySerializeChildEnvelope guarantees a readable document, so null here would be a bug, not a
            // legacy shape. The empty set is the only narrowing-safe fallback.
            : TryRestoreGrantEnvelope(childPolicyJson) ?? [];
        var policy = childPolicyJson is null
            ? RunAutonomyPolicy.FromSettings(settings)
            : TryRestorePolicy(childPolicyJson, _logger);

        var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, req.Trigger, req.TriggerRef, req.OwnerDeviceId, Goal: req.Goal,
            PolicyJson: policyJson, ParentRunId: parentRunId), ct)
            .ConfigureAwait(false);

        // The run's ISOLATED workspace under runs\<runId> (§17.2), carved out of SensitivePathGuard by
        // AssistantWorkspace.RunsRoot (Batch 06 B1). Every file operation this run performs resolves against
        // this directory (see the Initialize call below), so it holds the run's work — not merely scratch —
        // until a later group promotes it out; it is still auto-cleaned on chat delete / startup sweep.
        //
        // ONE contiguous block, deliberately: the decision is "which root does this dispatch get", and a
        // later batch overrides exactly that (a child run inherits its parent's root and must not provision
        // at its own run id). Sprinkling it through the dispatch would make that a rewrite.
        string? runRoot;
        if (parentRunId is not null)
        {
            // Batch 07 §7.6 change 1: a CHILD never provisions. It shares the parent's root verbatim — the
            // parent's own terminal settle promotes everything the whole fan-out wrote, exactly once.
            //
            // The branch is on "is this a child", NOT on "is the override non-null" as §7.2's signature prose
            // reads: a null override on a CHILD dispatch means the PARENT ran unisolated (06's no-isolation
            // degrade), and provisioning at the child's own id there would isolate a child whose parent is
            // writing the assistant folder — the two would then not even share a directory. Null propagates,
            // so parent and child are always in the same isolation regime.
            runRoot = workspaceRootOverride;
        }
        else if (_workspaces is not null)
        {
            // Batch 06 G3: the provisioner owns both modes (worktree when the source root is a repo, else a
            // bounded copy) and its symmetric teardown. It NEVER throws and returns null for "no isolation",
            // so the FailAsync settle below is unreachable on this path — see B16 for why that is the
            // intended outcome (degrade rather than fail an unattended run) and why the block stays anyway.
            runRoot = (await _workspaces.ProvisionAsync(run.Id, workingSubpath: null, ct).ConfigureAwait(false))?.Root;
        }
        else
        {
            // Legacy path (no provisioner injected): the original create + canonicalize, so a link in the
            // path is not a hole. The run row already exists (Planning), so a workspace-setup failure here
            // must settle it — otherwise the run dangles non-terminal until the next startup sweep (G-4).
            // A throw is still possible here, which is why this guard is kept rather than "restored" above.
            try
            {
                runRoot = Path.Combine(_runsBaseDir, run.Id.ToString());
                Directory.CreateDirectory(runRoot);
                runRoot = SafeFolderPath.Canonicalize(runRoot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Headless run {RunId} workspace setup failed", run.Id);
                try { await _agentRunService.FailAsync(run.Id, "workspace setup failed", cancelled: false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception fx) { _logger.LogWarning(fx, "Failed to settle headless run {RunId} after workspace-setup failure", run.Id); }
                throw;
            }
        }

        var budget = req.Budget ?? RunProfile.FromBudget(
            settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes);

        // Linked to the shutdown token ONLY — not the caller's ct (a fire-and-forget run must survive the
        // command returning). Shutdown cancels every run; per-run cancel disposes this source.
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

        // Batch 08 D1: THIS dispatch's cancel sink. A local delegate rather than a lambda passed inline,
        // because the same instance is the release token below (reference equality is what stops a finishing
        // dispatch from dropping a live resume's registration). Best-effort by design — a disposed source must
        // never break a pause or a cascade.
        Action steerCancel = () => { try { runCts.Cancel(); } catch { /* already disposed/cancelled */ } };
        _steering?.RegisterDispatch(run.Id, steerCancel);

        // Batch 07 §7.6: teardown is keyed on WORKSPACE OWNERSHIP, not on run id. This index exists so
        // OnChatsChanged can tear down the workspaces a chat's runs own — and a CHILD owns none: it writes its
        // parent's directory. Registering it would make deleting the child's stub chat call
        // TearDownWorkspaceAsync(childId), which in worktree mode is a `git worktree remove` and in either mode
        // resolves through the provisioner, i.e. a real removal attempt against a directory that belongs to the
        // parent and its still-running siblings. Not registering is the rule; the parent's own registration is
        // what cleans the shared workspace up. Mirrored in ResumeAsync — a parked child owns a stub chat, so
        // Continue reaches that path too.
        if (parentRunId is null)
        {
            lock (_runsByChatLock)
            {
                if (!_runsByChat.TryGetValue(chatId, out var set))
                    _runsByChat[chatId] = set = new HashSet<Guid>();
                set.Add(run.Id);
            }
        }

        _logger.LogInformation("Headless run {RunId} launched (chat {ChatId}, trigger {Trigger}, parent={HasParent})",
            run.Id, chatId, req.Trigger, parentRunId is not null);
        _logger.SensitiveDebug("Headless run {RunId} goal: {Goal}", run.Id, req.Goal);

        var completion = Task.Run(async () =>
        {
            var acquired = false;
            var started = false;
            try
            {
                await slots.WaitAsync(runCts.Token).ConfigureAwait(false);
                acquired = true;

                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<HeadlessTurnExecutor>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
                // Batch 06 G2: the run is confined to its own workspace. Every read/write/delete/list/search
                // resolves against runRoot with full containment (no escape, no system paths) — the guard
                // permits it because AssistantWorkspace.RunsRoot is an allowed island (B1), and the verifier
                // probes the same root because BeginRunAsync publishes it onto the RunContext (B3). A NULL
                // runRoot is the NO-ISOLATION degrade the provisioner falls back to (G3 F10): the run writes
                // straight into the user's assistant files folder, which is what every build before G2 did.
                executor.Initialize(workspaceRoot: runRoot, grants, provider, policy);
                started = true;

                // A2: open the composer bracket. Deliberately HERE and not before `_slots.WaitAsync` above:
                // the queue-wait window is FAIL-OPEN and that is correct, because the executor re-seeds from
                // the persisted rows when the run begins (HeadlessTurnExecutor.BeginRunAsync), so a message
                // landing before this run's first write is not lost — whereas covering the wait would disable
                // the composer for minutes during which nothing can go wrong. Released in the finally below
                // AND by ChatSessionManager's RunChanged handler, whichever gets there first.
                _executingRuns.Register(chatId, run.Id);

                await orchestrator.RunAsync(run, executor, persona, provider, budget, runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // If we never entered the orchestrator (it settles its own terminal state), settle the
                // run here so a queued-then-cancelled run is never left non-terminal (G-4).
                if (!started)
                {
                    try { await _agentRunService.FailAsync(run.Id, "interrupted at shutdown", cancelled: true, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to settle interrupted headless run {RunId}", run.Id); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Headless run {RunId} launcher task faulted", run.Id);
                // Defense in depth (G-4): the orchestrator settles its own terminal state on every path today,
                // but if a future refactor let RunAsync throw after we entered it, the run would dangle Running.
                if (started)
                {
                    try { await _agentRunService.FailAsync(run.Id, ex.Message, cancelled: false, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception fx) { _logger.LogWarning(fx, "Failed to settle faulted headless run {RunId}", run.Id); }
                }
            }
            finally
            {
                if (acquired) slots.Release();

                // A2: close the composer bracket on EVERY exit, including the never-started paths (a no-op
                // there). Deliberately AFTER the slot release: this is bookkeeping, and it must never be able
                // to strand the shared concurrency slot. Idempotent with the release ChatSessionManager
                // already did when the terminal RunChanged arrived — CompleteAsync raises that event before
                // this finally runs, so either side may get here first.
                _executingRuns.Release(run.Id);
                RemoveInflight(run.Id, runCts);
                // Batch 08 D1: drop this dispatch's sink AND any pause request it never consumed — the
                // !started arm above settles the row itself and never enters the orchestrator, so nothing
                // there would ever consume one. Ownership-guarded like RemoveInflight beside it, and for the
                // same reason: a resume dispatch may already have registered its own sink.
                _steering?.ReleaseDispatch(run.Id, steerCancel);
                runCts.Dispose();
            }
        }, CancellationToken.None);

        _inflight[run.Id] = (runCts, completion);
        return new HeadlessRunHandle(run.Id, chatId, completion);
    }

    public async Task<bool> ResumeAsync(Guid runId, string? nudge = null, CancellationToken ct = default)
    {
        var run = await _agentRunService.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null) { _logger.LogWarning("Resume: run {RunId} not found", runId); return false; }

        // Atomic claim FIRST (guardrail 2): a panel+Flow race or double-click → only one winner. On the
        // lost path we return BEFORE touching _slots/_inflight/_runsByChat — no slot leak, no duplicate run.
        //
        // Batch 08: TWO claims now, disjoint by SOURCE STATE, chosen from the row we already read. An explicit
        // dispatch, never a range (D7) and never "try one, then the other": a run whose state moved between the
        // read and the CAS is not ours, and the loser's log line below says so. A budget park is
        // WaitingForInput; a USER pause is Paused, and its claim also retires the pause envelope it consumed.
        var claimed = run.State switch
        {
            AgentRunState.WaitingForInput => await _agentRunService.TryBeginResumeAsync(runId, ct).ConfigureAwait(false),
            AgentRunState.Paused => await _agentRunService.TryResumeFromPauseAsync(runId, ct).ConfigureAwait(false),
            _ => false,
        };
        if (!claimed)
        {
            _logger.LogInformation("Resume: run {RunId} not claimable (already resumed/not parked)", runId);
            return false;
        }

        // The run is now CAS'd WaitingForInput→Running (the claim raised RunChanged(Running), retracting the
        // Flow card and disabling the panel Continue). Any failure between here and the orchestrator loop being
        // attached would leave the run dangling Running — unresumable and losing the parked work until the next
        // startup sweep cancels it. Re-park it on ANY such pre-dispatch failure so it stays resumable
        // (guardrail 1 — a resume error must never wedge a run; guardrail 3 — parked survives).
        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            var persona = await _personaService.ResolveActiveAsync(
                WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal).ConfigureAwait(false);
            // persona/provider are NOT persisted on the run — resolve the current default (same as the launch
            // path). Minor assumption: a resumed run may run on a different default provider than its origin.
            var provider = await ResolveProviderAsync(null, persona).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No provider configured to resume an agent run.");
            // Restore the write grants the LAUNCH resolved from the run's own envelope (D1) — a resume must
            // never widen them, so a narrowly-granted scheduled job that parked at its budget does NOT come
            // back with write+delete over the user's real assistant-files folder. Missing/unreadable/foreign
            // envelope → the FLOOR ({write_file}, never delete_file), logged with the run id only.
            var grants = TryRestoreGrantEnvelope(run.PolicyJson);
            if (grants is null)
            {
                _logger.LogInformation(
                    "Resume: run {RunId} has no readable launch-grant envelope; using the write-only floor", run.Id);
                grants = ResumeFloorGrants;
            }

            // The autonomy policy comes ONLY from the run's own envelope, never from settings (04 D10): a
            // settings flip between park and Continue must not widen a parked run. Absent/unreadable/absent
            // member ⇒ null ⇒ today's behaviour, which is the restrictive direction — unlike the grant list,
            // whose fallback is a floor the run can work with.
            var policy = TryRestorePolicy(run.PolicyJson, _logger);

            // Budget is DELIBERATELY not restored: a FRESH budget envelope IS the "continue" grant
            // (guardrail 4) — that is the whole point of the pause. Only the write grants are restored.
            // The ledger is persisted and accrues across resumes (never reset).
            var budget = RunProfile.FromBudget(
                settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes);

            // Idempotent: the run's isolated workspace already exists from the original launch (or is
            // recreated). CAPTURE the canonicalized path rather than discarding it — the resumed dispatch
            // hands this exact string to Initialize below, and recomputing it from Path.Combine there would
            // give the executor a different string than launch does for the same directory (a link or an
            // 8.3 component in the base dir), i.e. the two call sites would silently drift apart.
            //
            // Deliberately the SAME contiguous block shape as the launch path, so the rule a later batch has
            // to add here — a resumed CHILD run must not provision at its own run id — is a local edit.
            string? runRoot;
            if (run.ParentRunId is { } parentId)
            {
                // Batch 07 §7.6 change 3, and the reason it is not optional: this method provisions at its OWN
                // run id, and every child owns a stub chat — so a user opening a parked child's chat and
                // pressing Continue would otherwise create a SECOND workspace at the child's id, diverging from
                // the parent's and outliving it until the sweep. Resolve the PARENT's root instead, and only if
                // it still exists: a parent that ran unisolated, or whose workspace is already gone, leaves the
                // child writing the assistant folder — the same coherent degrade as a fresh child dispatch.
                var parentRoot = _workspaces?.RootFor(parentId) ?? Path.Combine(_runsBaseDir, parentId.ToString());
                runRoot = Directory.Exists(parentRoot) ? SafeFolderPath.Canonicalize(parentRoot) : null;
            }
            else if (_workspaces is not null)
            {
                // Idempotent by construction (G3 B11 step 2): a readable metadata document returns the same
                // root, the same mode and the same provisionedAtUtc, which is what keeps the promote set
                // from becoming "everything the workspace contains" after a park → resume.
                runRoot = (await _workspaces.ProvisionAsync(run.Id, workingSubpath: null, ct).ConfigureAwait(false))?.Root;
            }
            else
            {
                runRoot = SafeFolderPath.Canonicalize(
                    Directory.CreateDirectory(Path.Combine(_runsBaseDir, run.Id.ToString())).FullName);
            }

            var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

            // Batch 08 D1: this dispatch's cancel sink, same shape as the launch path. Registering OVERWRITES,
            // which is the point — a resume can start while the previous dispatch is still unwinding, and the
            // sink a pause must fire is the one belonging to the loop that is actually running.
            Action steerCancel = () => { try { runCts.Cancel(); } catch { /* already disposed/cancelled */ } };
            _steering?.RegisterDispatch(run.Id, steerCancel);

            // The same non-registration rule as the launch path, for the same reason: teardown is keyed on
            // WORKSPACE OWNERSHIP, not on run id, and a resumed child still owns no directory of its own
            // (the branch above is what keeps that true). See LaunchCoreAsync's registration for the argument.
            if (run.ParentRunId is null)
            {
                lock (_runsByChatLock)
                {
                    if (!_runsByChat.TryGetValue(run.ChatId, out var set))
                        _runsByChat[run.ChatId] = set = new HashSet<Guid>();
                    set.Add(run.Id);
                }
            }

            _logger.LogInformation("Resuming run {RunId} (chat {ChatId}, parent={HasParent})",
                run.Id, run.ChatId, run.ParentRunId is not null);

            var completion = Task.Run(async () =>
            {
                var acquired = false;
                var started = false;
                try
                {
                    // Deliberately the PARENT pool even for a resumed child (Batch 07 §7.1): a resume is a USER
                    // act, so nothing is awaiting this dispatch from inside another run's RunAsync, and the
                    // nested-acquire deadlock that _childSlots exists to prevent cannot arise here.
                    await _slots.WaitAsync(runCts.Token).ConfigureAwait(false); // re-acquire a slot (guardrail 6)
                    acquired = true;

                    using var scope = _scopeFactory.CreateScope();
                    var executor = scope.ServiceProvider.GetRequiredService<HeadlessTurnExecutor>();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
                    // Batch 06 G2, symmetric with the launch path: a resumed run re-enters the SAME isolated
                    // workspace it was parked in, so the Pending remainder sees the work the pre-pause steps
                    // left behind. A separate literal from the launch call on purpose — the two have drifted
                    // before, so each has its own regression fact.
                    executor.Initialize(workspaceRoot: runRoot, grants, provider, policy);
                    started = true;
                    // A2: same bracket, same reasoning as the launch path — after the slot wait, before the
                    // executor can write. TryBeginResumeAsync already raised RunChanged(Running) at the CAS,
                    // i.e. before this line, which is why ChatSessionManager keeps its ActiveRunId-matched
                    // term as well as reading this index.
                    _executingRuns.Register(run.ChatId, run.Id);
                    await orchestrator.RunAsync(run, executor, persona, provider, budget, runCts.Token, resume: true, nudge: nudge)
                        .ConfigureAwait(false); // resume:true → no re-plan, drains the Pending remainder (D1)
                }
                catch (OperationCanceledException)
                {
                    // Cancel during resume before entering the orchestrator: the run was CAS'd to Running by
                    // the claim, so re-park it (rather than leave it dangling Running) — it stays resumable.
                    if (!started)
                    {
                        try { await _agentRunService.PauseAsync(run.Id, "resume-interrupted", CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to re-park interrupted resume {RunId}", run.Id); }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Resume of run {RunId} faulted", run.Id);
                    if (started)
                    {
                        try { await _agentRunService.FailAsync(run.Id, ex.Message, cancelled: false, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception fx) { _logger.LogWarning(fx, "Failed to settle faulted resume {RunId}", run.Id); }
                    }
                    else
                    {
                        // Faulted before entering the orchestrator (e.g. slot wait, scope/executor construction) —
                        // the run was CAS'd to Running but no loop is attached. Re-park it so it stays resumable
                        // rather than dangling Running (guardrail 1/3).
                        try { await _agentRunService.PauseAsync(run.Id, "resume-interrupted", CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception px) { _logger.LogWarning(px, "Failed to re-park interrupted resume {RunId}", run.Id); }
                    }
                }
                finally
                {
                    if (acquired) _slots.Release();

                    // A2: see the launch path (and the same after-the-slot ordering, for the same reason). A
                    // resume dispatch that starts while the previous dispatch is still unwinding re-registers
                    // the same key, so this release can close the NEWER bracket — fail-open, which is the
                    // recoverable direction (a stale true is not recoverable).
                    _executingRuns.Release(run.Id);
                    RemoveInflight(run.Id, runCts);
                    // Batch 08 D1: see the launch path's finally. The !started arms above re-park the row
                    // themselves without entering the orchestrator, so an unconsumed request has to die here.
                    _steering?.ReleaseDispatch(run.Id, steerCancel);
                    runCts.Dispose();
                }
            }, CancellationToken.None);

            _inflight[run.Id] = (runCts, completion);
            return true;
        }
        catch (Exception ex)
        {
            // Pre-dispatch failure (settings/persona/provider resolve, workspace create) after the CAS win.
            // Re-park so the run leaves Running and stays resumable; report the resume did not start.
            _logger.LogError(ex, "Resume of run {RunId} failed before dispatch; re-parking", runId);
            try { await _agentRunService.PauseAsync(runId, "resume-interrupted", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception px) { _logger.LogWarning(px, "Failed to re-park run {RunId} after pre-dispatch resume failure", runId); }
            return false;
        }
    }

    /// <summary>
    /// App shutdown: cancel every dispatch and wait, bounded, for them to unwind.
    /// <para>
    /// Batch 08 D1: this path deliberately does <b>not</b> revoke a pending pause request, unlike the four
    /// terminal-intent sites that do. It is the recoverable asymmetry — a run whose pause request is still
    /// unconsumed when the shutdown token fires comes back <see cref="AgentRunState.Paused"/> and RESUMABLE
    /// rather than <c>Cancelled</c>, which is the direction the user asked for and the only one that keeps the
    /// work. Asserted, not merely commented, by
    /// <c>HeadlessRunLauncherTests.Shutdown_DoesNotRevokeAPendingPause_SoTheRunComesBackResumable</c>.
    /// </para>
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        try { _shutdownCts.Cancel(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Headless launcher shutdown cancel threw"); }

        var tasks = _inflight.Values.Select(v => v.Task).ToArray();
        if (tasks.Length == 0) return;

        try
        {
            // Tasks self-catch cancellation and settle their own run state; bound the wait so a stuck
            // step can't hang shutdown.
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Headless launcher StopAsync timed out or faulted waiting for {Count} run(s)", tasks.Length);
        }
    }

    public Task RunStartupSweepAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            string[] dirs;
            try
            {
                if (!Directory.Exists(_runsBaseDir)) return;
                dirs = Directory.GetDirectories(_runsBaseDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Headless run-workspace sweep: failed to enumerate {Base}", _runsBaseDir);
                return;
            }

            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (!Guid.TryParse(name, out var runId))
                    continue; // not a run workspace

                bool remove;
                try
                {
                    var run = await _agentRunService.GetAsync(runId, ct).ConfigureAwait(false);
                    // Batch 06 B12 / plan D3's retention rule and plan R5's mitigation, in one predicate. A
                    // run the DB no longer has goes immediately (unchanged). A SETTLED run keeps its
                    // workspace only long enough for the user to answer the publish offer. A state this
                    // build does not know falls through to the 30-day floor with everything non-terminal:
                    // it may still be resumable, and deleting a resumable run's only copy of its work is
                    // the one mistake this sweep must not make.
                    var terminal = run?.State is AgentRunState.Completed
                        or AgentRunState.Failed or AgentRunState.Cancelled;
                    var maxAge = terminal ? _terminalWorkspaceMaxAge : _workspaceMaxAge;
                    remove = run is null || Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow - maxAge;
                }
                catch { remove = false; }

                if (remove)
                    await TearDownWorkspaceAsync(runId, ct).ConfigureAwait(false);
            }

            // Second pass (Batch 06 G3): the loop above enumerates DIRECTORIES only, so a metadata document
            // whose workspace is already gone is invisible to it — and in worktree mode that document is the
            // only thing that knows which repository still carries a stale .git/worktrees/<id> registration
            // (plan R5).
            if (_workspaces is not null)
                await _workspaces.SweepOrphanMetadataAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// The run persona for one dispatch: the delegated step's ASSIGNED roster persona when there is one, else
    /// the global per-mode resolution every ordinary launch takes.
    /// <para>
    /// Two narrowings, both mirroring <c>StepPersonaResolver</c> so the two seams agree. The id must still be on
    /// the CURRENT roster for the current operating mode — a plan outlives the setting that produced it (a
    /// replan, a resume, a roster the user has since edited), and an id the user has withdrawn is not this
    /// run's business even if it resolves. And it must still resolve to a persona: one deleted between plan and
    /// dispatch must not reach a prompt as a blank system message.
    /// </para>
    /// <para>
    /// NEVER throws for the override's sake (guardrail 1): every arm ends at the per-mode resolution, which is
    /// exactly the pre-fix behaviour. Ids and counts only in the logs — a persona NAME is user-named content.
    /// </para>
    /// </summary>
    private async Task<Persona> ResolveRunPersonaAsync(Guid? personaIdOverride, AppSettings settings)
    {
        var mode = settings.UserOperatingMode ?? UserOperatingMode.Personal;
        if (personaIdOverride is { } id && id != Guid.Empty)
        {
            try
            {
                if (!settings.GetAgentPersonaRoster(mode).Contains(id))
                {
                    _logger.LogInformation(
                        "Delegated run persona {PersonaId} is not on the current roster; using the mode persona ({Reason})",
                        id, "off-roster");
                }
                else if (await _personaService.GetPersonaAsync(id).ConfigureAwait(false) is { } assigned)
                {
                    return assigned;
                }
                else
                {
                    _logger.LogInformation(
                        "Delegated run persona {PersonaId} could not be resolved; using the mode persona ({Reason})",
                        id, "unresolvable-persona");
                }
            }
            catch (Exception ex)
            {
                // Exception TYPE only: a persona store's message can embed a persona name.
                _logger.LogWarning(
                    "Delegated run persona {PersonaId} could not be read ({Error}); using the mode persona",
                    id, ex.GetType().Name);
            }
        }

        return await _personaService.ResolveActiveAsync(WindowMode.Assistant, mode).ConfigureAwait(false);
    }

    private async Task<AiProvider?> ResolveProviderAsync(Guid? explicitProviderId, Persona persona)
    {
        AiProvider? provider = null;
        if (explicitProviderId.HasValue)
            provider = await _providerService.GetProviderAsync(explicitProviderId.Value).ConfigureAwait(false);
        if (provider is null && persona.PreferredProviderId.HasValue)
            provider = await _providerService.GetProviderAsync(persona.PreferredProviderId.Value).ConfigureAwait(false);
        provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant).ConfigureAwait(false);
        if (provider is null)
            return null;

        if (persona.ReasoningEffort.HasValue)
        {
            provider = provider.Clone();
            provider.ReasoningEffort = persona.ReasoningEffort.Value;
        }
        return provider;
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        if (e.Kind != AssistantChatChangeKind.Deleted)
            return;

        HashSet<Guid>? runIds;
        lock (_runsByChatLock)
        {
            if (!_runsByChat.TryGetValue(e.Id, out runIds))
                return;
            _runsByChat.TryRemove(e.Id, out _);
        }

        foreach (var runId in runIds)
        {
            // Cancel AND UNWIND, then tear down — off this synchronous handler, because the teardown may spawn
            // git (worktree remove/prune) and the unwind takes as long as the run's current step does.
            CancelThenTearDownWorkspaceAsync(runId).SafeFireAndForget(_logger);
        }
    }

    /// <summary>
    /// How long a chat deletion waits for the run it just cancelled to actually unwind before removing the
    /// workspace. A judgement call, not a measurement: long enough for a step to observe its token and drop the
    /// file handles it is holding, short enough that a wedged dispatch cannot defer the cleanup indefinitely.
    /// </summary>
    private static readonly TimeSpan _unwindBeforeTeardown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Plan R4, both halves. This directory is the only copy of a non-promoted run's work, so it is never
    /// deleted under a LIVE writer: cancel the dispatch first AND WAIT FOR IT TO UNWIND. (Deleting the chat is
    /// an explicit user act that cascades the run row away, so the files going with it is the intent; racing a
    /// running step while doing it is not.)
    /// <para>
    /// Cancelling alone was not enough, and the gap was the whole of Batch 06 Lens A finding 4's reachable
    /// trigger: <c>Cancel()</c> returns immediately while the step is still inside a <c>write_file</c>, which is
    /// exactly when <c>git worktree remove</c> and a recursive delete BOTH fail. The task awaited here is the one
    /// <see cref="_inflight"/> has always held beside the CTS and nothing read.
    /// </para>
    /// <para>
    /// BOUNDED, and it tears down anyway on a timeout — that is the pre-existing behaviour, and a failed delete
    /// self-heals: the run row is gone by FK cascade, so the next startup sweep sees <c>run is null</c> and
    /// removes the workspace unconditionally. The dispatch task never faults (every path inside it is caught), so
    /// the catch-all is for a future refactor rather than for today.
    /// </para>
    /// </summary>
    private async Task CancelThenTearDownWorkspaceAsync(Guid runId)
    {
        if (_inflight.TryGetValue(runId, out var entry))
        {
            // Batch 08 D1, revocation site 3, and it must precede the cancel below: deleting the chat is
            // TERMINAL intent (the run row goes with it by FK cascade and its workspace is about to be
            // removed), so an unconsumed pause request must never be read by the unwinding loop as "the user
            // asked to pause". Paired with the cancel it guards rather than done unconditionally at the top —
            // with no _inflight entry there is no dispatch, so RecordPauseRequest would have refused and
            // ReleaseDispatch has already dropped anything stale.
            _steering?.RevokePauseRequest(runId);

            // Best-effort: a disposed CTS throws here and must not stop the cleanup.
            try { entry.Cts.Cancel(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to cancel run {RunId} before workspace teardown", runId); }

            try
            {
                // No linked shutdown token deliberately: this races _shutdownCts's own disposal, and StopAsync
                // already awaits every _inflight task, so a shutdown cannot be left waiting on this one.
                await entry.Task.WaitAsync(_unwindBeforeTeardown).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Run {RunId} did not unwind within the teardown wait; removing its workspace anyway", runId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId} faulted while unwinding before its workspace teardown", runId);
            }
        }

        await TearDownWorkspaceAsync(runId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The ONE workspace-removal path. Worktree mode needs <c>git worktree remove</c>/<c>prune</c>, not a
    /// recursive delete, or the user's repository keeps a stale registration forever (plan R5/R16) — so
    /// every caller goes through the provisioner, which owns create and teardown symmetrically.
    /// <see cref="TryDeleteDirectory"/> remains as the fallback for the no-provisioner shape, which is what
    /// keeps the existing launcher suite passing unmodified.
    /// </summary>
    private async Task TearDownWorkspaceAsync(Guid runId, CancellationToken ct)
    {
        if (_workspaces is not null)
        {
            await _workspaces.TearDownAsync(runId, ct).ConfigureAwait(false);
            return;
        }

        TryDeleteDirectory(Path.Combine(_runsBaseDir, runId.ToString()));
    }

    /// <summary>
    /// Drops this dispatch's own <see cref="_inflight"/> entry — and ONLY its own. The same run id is
    /// dispatched more than once over its life (launch → park → resume, and a resume can start while the
    /// previous dispatch is still unwinding its <c>finally</c>), so an unconditional
    /// <c>TryRemove(run.Id)</c> lets a finishing dispatch evict a LIVE one: shutdown would then neither
    /// cancel nor await that run (G-4). The dispatch's CTS is its identity — it is created per dispatch and
    /// disposed right after this call.
    /// </summary>
    private void RemoveInflight(Guid runId, CancellationTokenSource ownCts)
    {
        if (_inflight.TryGetValue(runId, out var entry) && ReferenceEquals(entry.Cts, ownCts))
            _inflight.TryRemove(new KeyValuePair<Guid, (CancellationTokenSource Cts, Task Task)>(runId, entry));
    }

    /// <summary>
    /// The no-provisioner fallback for <see cref="TearDownWorkspaceAsync"/> — and the ONLY
    /// <c>Directory.Delete</c> in this type, deliberately: a worktree-mode workspace deleted this way would
    /// leave a stale registration behind, so every other removal site routes through the provisioner.
    /// </summary>
    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete headless run workspace {Dir}", dir);
        }
    }

    /// <summary>
    /// Serialize the launch-grant envelope, swallowing any serializer fault (guardrail 1 — this is
    /// bookkeeping and must never fail a launch). A null result means the resume will apply the FLOOR,
    /// which is the safe direction to degrade in.
    /// </summary>
    private string? TrySerializeGrantEnvelope(
        IReadOnlyCollection<string> grants, AgentRunTrigger trigger, RunAutonomyPolicy? policy)
    {
        try
        {
            return SerializeGrantEnvelope(grants, trigger, policy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize the launch-grant envelope; resume will use the floor");
            return null;
        }
    }

    /// <summary>
    /// Serialize the grants a launch resolved into the opaque <c>AgentRuns.PolicyJson</c> envelope (D1).
    /// The run service stores the string verbatim and never parses it, so the shape stays private to this
    /// launcher; <c>v</c> lets a later shape change be detected instead of misread.
    /// </summary>
    /// <param name="policy">The run's autonomy policy, or null. Null OMITS the member entirely (not
    /// <c>"policy":null</c>), so a policy-less document is byte-identical to a pre-Batch-04 one.</param>
    internal static string SerializeGrantEnvelope(
        IReadOnlyCollection<string> grants, AgentRunTrigger trigger, RunAutonomyPolicy? policy = null)
        => JsonSerializer.Serialize(
            new GrantEnvelope
            {
                V = GrantEnvelopeVersion,
                GrantedWrites = grants.ToList(),
                Trigger = trigger.ToString(),
                Policy = policy is null
                    ? null
                    : new PolicyDto { AutoApproveClasses = policy.AutoApproveClasses.Select(c => c.ToString()).ToList() },
            },
            GrantEnvelopeJsonOptions);

    /// <summary>
    /// The grant set + policy a CHILD run inherits: a strict SUBSET of the parent's, never the default and
    /// never the resume floor. A child is a delegate — it does the work the parent asked for and it does not
    /// get to destroy anything, so every delete-like NAME is stripped even when the parent held it (the parent
    /// can still delete, in its own steps).
    /// <para>
    /// An UNREADABLE parent envelope yields the EMPTY grant set, NOT
    /// <c>HeadlessRunRequest.DefaultGrantedWrites</c> and NOT <see cref="ResumeFloorGrants"/>: falling through
    /// to a default would let a child that inherits nothing readable end up WIDER than its parent, which is the
    /// one thing this helper exists to make impossible (Phase 3 R13). "Readable" means exactly what it means at
    /// resume, because this is the same reader — <see cref="TryRestoreGrantEnvelope"/>.
    /// </para>
    /// <para>
    /// The policy passes through UNCHANGED. It is a tool-CLASS set that can never cover a delete-like tool
    /// (04 D6 — the floor in <c>ToolAutonomy.Resolve</c> is evaluated before any policy branch), so narrowing it
    /// further would only make a child unable to do the work it was delegated, and it is ⊆ the parent's by
    /// construction. Pinned by <c>HeadlessRunLauncherChildRunTests</c>.
    /// </para>
    /// <para>
    /// Name filtering is legitimate HERE: this file is not one of <c>ToolAutonomyRuleTests.GateFiles</c> — it
    /// AUTHORS a grant list rather than gating a call, exactly like <c>ScheduledJobToolHandler.ParseGrantedTools</c>
    /// does at create time. The execution gates are untouched and still the only boundary.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<string> Grants, RunAutonomyPolicy? Policy) NarrowForChild(
        string? parentPolicyJson, ILogger? logger = null)
    {
        var inherited = TryRestoreGrantEnvelope(parentPolicyJson) ?? [];
        var grants = inherited.Where(g => !ToolPermissionService.IsDeleteLike(g)).ToList();

        // COUNT only. A grant name can be an MCP-adjacent string, which is not ours to write to a support log —
        // the same rule TryRestorePolicy's dropped-class count follows.
        if (grants.Count != inherited.Count)
            logger?.LogInformation("Child run grants dropped {DroppedCount} delete-like names the parent held", inherited.Count - grants.Count);

        return (grants, TryRestorePolicy(parentPolicyJson, logger));
    }

    /// <summary>
    /// The child's <c>PolicyJson</c>: <see cref="NarrowForChild"/>'s result through the EXISTING <c>v:1</c>
    /// serializer. The envelope version is deliberately NOT bumped — additive members only, because
    /// <see cref="GrantEnvelopeVersion"/> is compared with <c>!=</c> (see its remarks).
    /// </summary>
    /// <param name="trigger">The PARENT's trigger kind. Provenance only — "diagnostics only; never consulted to
    /// widen a grant", as <see cref="GrantEnvelope.Trigger"/> says.</param>
    /// <remarks>
    /// Unlike <see cref="TrySerializeGrantEnvelope"/>, a serializer fault here falls back to
    /// <see cref="InteractiveEmptyEnvelopeJson"/> and NOT to <c>null</c>: null would make the child's resume
    /// apply <see cref="ResumeFloorGrants"/> (<c>{write_file}</c>), which can be WIDER than the parent — the
    /// identical argument that constant already exists for. Its <c>"trigger":"User"</c> then misreports a
    /// Schedule-parent's child, which is acceptable precisely because trigger never widens anything. That arm is
    /// a GUARD, not a fixed defect: serializing a <c>List&lt;string&gt;</c> plus a class-name list cannot
    /// realistically fault, so it is unreachable in practice and is not covered by a red-before-green demo.
    /// </remarks>
    internal static string TrySerializeChildEnvelope(
        string? parentPolicyJson, AgentRunTrigger trigger, ILogger? logger = null)
    {
        var (grants, policy) = NarrowForChild(parentPolicyJson, logger);
        try
        {
            return SerializeGrantEnvelope(grants, trigger, policy);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to serialize a child run's grant envelope; granting the child nothing");
            return InteractiveEmptyEnvelopeJson;
        }
    }

    /// <summary>
    /// Read the run's autonomy policy back out of the envelope (04 D10). Returns <c>null</c> — meaning
    /// "TODAY'S BEHAVIOUR", NOT the grant floor — for an absent/unreadable envelope, an absent <c>policy</c>
    /// member, or a member whose class names this build does not recognise. Never throws.
    /// <para>
    /// The asymmetry against <see cref="TryRestoreGrantEnvelope"/> is the whole backward-compatibility
    /// guarantee: an unreadable envelope loses the POLICY before it loses the grant list, and losing the
    /// policy is always the restrictive direction. An unreadable grant list has to fall back to something the
    /// run can work with; an unreadable policy falls back to nothing. The two readers therefore apply the same
    /// readability test — version AND a present <c>grantedWrites</c> — so "readable" cannot mean one thing here
    /// and another there; only the FALLBACK differs.
    /// </para>
    /// <para>
    /// Class names are validated as <see cref="ToolClass"/> members and nothing more. They are deliberately NOT
    /// intersected with <c>RunAutonomyPolicy.PresetClasses</c>: that list is the SETTINGS preset, not "everything
    /// an envelope may legally carry", so pinning the reader to it would silently narrow the first per-run policy
    /// a later batch authors, with no failing test to explain why. §13.2's filtering belongs at the point a
    /// policy is AUTHORED from untrusted input, which is a different chokepoint from this resume reader.
    /// </para>
    /// <para>
    /// A resume calls this and NEVER <c>RunAutonomyPolicy.FromSettings</c>: the envelope is the run's
    /// authority of record, so flipping the setting between park and Continue cannot widen a parked run.
    /// Unrecognised class names are dropped and only their COUNT is logged — an MCP-adjacent string is not
    /// ours to write to a support log.
    /// </para>
    /// </summary>
    internal static RunAutonomyPolicy? TryRestorePolicy(string? policyJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<GrantEnvelope>(policyJson, GrantEnvelopeJsonOptions);

            // The SAME readability test TryRestoreGrantEnvelope applies, `GrantedWrites is null` included, so
            // both halves of the reader agree on what "a readable envelope" means. Without it the documented
            // asymmetry INVERTS for one document shape: `{"v":1,"policy":{…}}` with no grantedWrites made the
            // grant half fall back to the {write_file} floor as if the envelope were unreadable while this half
            // handed back a full policy — a resumed run auto-running by class with no named grant behind it.
            if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.GrantedWrites is null)
                return null;

            var names = envelope.Policy?.AutoApproveClasses;
            if (names is null || names.Count == 0)
                return null;

            var classes = new List<ToolClass>();
            var dropped = 0;
            foreach (var name in names)
            {
                // OrdinalIgnoreCase against the enum member names. Unknown is dropped like any unparseable
                // name: RunAutonomyPolicy.Covers hardcodes it to false anyway, so carrying it would only make
                // the restored policy look wider than it is.
                if (!string.IsNullOrWhiteSpace(name)
                    && Enum.TryParse<ToolClass>(name.Trim(), ignoreCase: true, out var parsed)
                    && parsed != ToolClass.Unknown)
                {
                    if (!classes.Contains(parsed))
                        classes.Add(parsed);
                }
                else
                {
                    dropped++;
                }
            }

            if (dropped > 0)
                logger?.LogInformation("Restored run policy dropped {DroppedCount} unrecognised class names", dropped);

            // No usable class ⇒ no policy, which is today's behaviour rather than an empty-but-present one.
            return classes.Count == 0 ? null : new RunAutonomyPolicy(classes);
        }
        catch (Exception)
        {
            // Garbage / foreign JSON is a "no policy" case, not an error case.
            return null;
        }
    }

    /// <summary>
    /// Read the grant list a launch persisted, so a resume restores exactly what the launch granted and
    /// can never widen it (D1). Returns <c>null</c> — meaning "apply the resume FLOOR" — when the envelope
    /// is absent, unparseable, of an unknown version, or carries no <c>grantedWrites</c> member at all.
    /// A present-but-EMPTY list is honoured as an empty grant set (a launch that granted no writes must
    /// not gain any on resume). Never throws.
    /// </summary>
    internal static IReadOnlyList<string>? TryRestoreGrantEnvelope(string? policyJson)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<GrantEnvelope>(policyJson, GrantEnvelopeJsonOptions);
            if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.GrantedWrites is null)
                return null;

            return envelope.GrantedWrites
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // Garbage / foreign JSON in PolicyJson is a floor case, not an error case.
            return null;
        }
    }

    /// <summary>
    /// The launch-grant envelope persisted on <c>AgentRuns.PolicyJson</c>. Private to this file
    /// (see <see cref="SerializeGrantEnvelope"/>); camelCase on the wire like the rest of this codebase.
    /// </summary>
    private sealed class GrantEnvelope
    {
        /// <summary>Envelope version. Absent/unknown → the reader applies the resume floor.</summary>
        public int V { get; set; }

        /// <summary>The write-tool names the LAUNCH resolved. A resume restores exactly this.</summary>
        public List<string>? GrantedWrites { get; set; }

        /// <summary>Origin trigger — diagnostics only; never consulted to widen a grant.</summary>
        public string? Trigger { get; set; }

        /// <summary>
        /// Batch 04 autonomy policy. ADDITIVE at <c>v:1</c> — <see cref="GrantEnvelopeVersion"/> is
        /// deliberately NOT bumped (see its remarks). <c>WhenWritingNull</c> is scoped to THIS member, not to
        /// the shared options object, so a policy-less document stays byte-identical to a pre-04 one and
        /// nothing has to be argued about <c>V</c> / <c>GrantedWrites</c> / <c>Trigger</c>.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PolicyDto? Policy { get; set; }
    }

    /// <summary>
    /// Wire shape of the autonomy policy. Class NAMES, not ordinals: a name an older build cannot parse is
    /// DROPPED (restrictive) instead of silently colliding with a member it does know.
    /// </summary>
    private sealed class PolicyDto
    {
        public List<string>? AutoApproveClasses { get; set; }
    }

    private static string DeriveTitle(string goal)
    {
        var collapsed = TextFormatting.CollapseWhitespace(goal);
        const int max = 40;
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chatService.ChatsChanged -= OnChatsChanged;
        _shutdownCts.Dispose();
    }
}
