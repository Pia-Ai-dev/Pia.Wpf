using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IHeadlessRunLauncher"/>. Detaches a goal as an unattended
/// <see cref="RunShape.Planned"/> run (/17.5): stub-chat-first (G-3/R1), create the run, resolve
/// persona + provider, seed an isolated per-run workspace, and dispatch the orchestrator on a fresh DI
/// scope with its own linked CTS. A shared <see cref="RunSlotPool"/> caps concurrency at a user-set,
/// live-resizable width; app shutdown cancels + bounded-awaits in-flight runs so none is left
/// <see cref="AgentRunState.Running"/>.
/// </summary>
public sealed partial class HeadlessRunLauncher : IHeadlessRunLauncher, IAgentRunResumeService, IDisposable
{
    /// <summary>The failure reason for a run whose isolated workspace could not be provisioned. App-owned and
    /// named, like the pause vocabulary below, so the run panel can localize it rather than show it
    /// verbatim.</summary>
    internal const string WorkspaceSetupFailure = "workspace setup failed";

    /// <summary>The failure reason for a run the shutdown sweep cancelled mid-flight. Written with
    /// <c>cancelled: true</c>, so the panel reaches it through the Failed-family visual.</summary>
    internal const string ShutdownInterruptedFailure = "interrupted at shutdown";

    /// <summary>
    /// The pause <c>reason</c> written by the three re-park arms of <see cref="ResumeAsync"/> — a resume that
    /// CAS-claimed the row and then never reached the orchestrator (cancelled or faulted in the slot wait,
    /// the scope build or the executor construction). Same closed, app-owned vocabulary as
    /// <c>"step-cap"</c> / <c>"wall-clock"</c> / <see cref="AgentRunService.ChildrenInterruptedReason"/>.
    /// this used to be a bare literal, which meant BOTH readers fell through to their budget
    /// arm — so a run the USER paused, whose Continue then failed to start, came back announcing "Stopped at
    /// its budget" and invited them to raise budgets that were never reached. It is a named constant for the
    /// same reason the other three are: <see cref="AgentRunService.UserPausedReason"/>'s own doc states that
    /// adding a token to this vocabulary obliges an arm in <c>RunProgressViewModel.DescribePause</c> AND
    /// <see cref="AgentRunNotificationSurface.PausedBodyKey"/>, and a literal cannot carry that obligation.
    /// The row still parks <c>WaitingForInput</c> and stays resumable — this was only ever the panel lying
    /// about WHY.
    /// </summary>
    internal const string ResumeInterruptedReason = "resume-interrupted";

    /// <summary>
    /// Concurrency cap shared by both producers (decision d). A run beyond the width queues on a slot.
    /// user-set and LIVE-RESIZABLE — <see cref="AppSettings.MaxParallelBackgroundRuns"/>, default
    /// <see cref="AppSettings.DefaultParallelBackgroundRuns"/> (the width this was hard-coded to), ceiling
    /// <see cref="AppSettings.MaxParallelBackgroundRunsCap"/>. Applied from TWO places on purpose:
    /// <see cref="OnSettingsChanged"/> covers a raise made WHILE runs are queued (nothing else would apply it
    /// until the next launch, which is exactly the run that is stuck), and the <c>Resize</c> at the top of
    /// <see cref="LaunchCoreAsync"/>/<see cref="ResumeAsync"/> covers cold start, where no save has happened
    /// this session and the event therefore never fires. Both are idempotent — <c>Resize</c> early-returns on
    /// an unchanged width — so the overlap costs a lock acquire, not a behaviour.
    /// It bounds EXECUTION, not DISPATCH: <see cref="LaunchCoreAsync"/> creates the stub chat, the run row and
    /// the workspace and RETURNS before the slot wait, so N due jobs still produce N run rows immediately.
    /// queued IN LAUNCH ORDER. Both dispatch paths take a <see cref="RunSlotPool.Ticket"/> on the calling
    /// thread and hand it to the ticketed wait, so the order the tick created its dispatches in (oldest-due-first)
    /// is the order they enqueue in, instead of whatever order the thread pool happens to start the detached
    /// bodies in. Not strict FIFO admission — see <see cref="RunSlotPool"/> for what is and is not claimed.
    /// </summary>
    private readonly RunSlotPool _slots =
        new(AppSettings.DefaultParallelBackgroundRuns, AppSettings.MaxParallelBackgroundRunsCap);

    /// <summary>
    /// Concurrency cap for CHILD runs — deliberately a SECOND pool, and never
    /// <see cref="_slots"/>. A nested acquire on the shared pool DEADLOCKS, permanently: <see cref="_slots"/>
    /// is waited INSIDE the dispatch task and released only in the <c>finally</c> AFTER
    /// <c>orchestrator.RunAsync</c> RETURNS, so parents holding EVERY permit while each awaits a child that
    /// needs a permit from the same pool can never release one. It takes only as many concurrent parents as
    /// <see cref="_slots"/> is wide — and since T1-1 that width is a USER SETTING, so the deadlock is now
    /// reachable at every width, not just at 2. Nothing in the process can break it: <see cref="StopAsync"/>'s
    /// bounded 5-second wait times out and those runs dangle <c>Running</c> until the next startup sweep. Never
    /// merge these two pools "for simplicity".
    /// Consequence, stated rather than hidden: effective provider concurrency doubles. That is why the
    /// persona roster is the opt-in and why a delegating run's budget must still fit the envelope one
    /// scheduled job may occupy — a fan-out holds one of <see cref="_slots"/> for the parent's wall clock PLUS
    /// every descendant's (Phase 3 R15, and the halved child wall clock in the orchestrator's fan-out).
    /// <b>Corrected:</b> that used to be far worse — <c>ScheduledJobBackgroundService</c> held a
    /// single run lock across <c>await handle.Completion</c>, so a fan-out blocked every scheduled job on the
    /// device for that whole time. The scheduler now dispatches and bookkeeps in a continuation, so this pool
    /// really is the bound it claims to be, and one long fan-out costs the OTHER due jobs a slot, not the tick.
    /// WHY 2, and not wider: it is a fixed width rather than the width of a fan-out, so the worst case a
    /// delegating build can put on the provider is <b>a fixed 2 children per delegating parent, on top of a
    /// user-set parent pool</b> — a number that does not grow when a planner emits a 6-way group. (Until T1-1
    /// this paragraph read "a fixed 2+2", which was true while <see cref="_slots"/> was hard-coded to 2 and is
    /// not a current fact.) A group wider than the pool is not starved, it runs in WAVES: every sibling is
    /// dispatched and awaited (the parent never returns from the wait with a live child, D16), so the only cost
    /// of a narrow pool is elapsed time inside the parent's own halved wall-clock budget. Raising it trades
    /// exactly that for more concurrent provider load and a longer <see cref="StopAsync"/> drain, and it must
    /// stay a SEPARATE number from <see cref="_slots"/> either way. It is deliberately not user-configurable:
    /// the setting T1-1 added sizes the PARENT pool only, and the depth guard (a child never delegates,
    /// 07 ) is what bounds the total rather than this cap.
    /// a <see cref="RunSlotPool"/> like <see cref="_slots"/>, but constructed with its HARD CAP equal to
    /// its width, so "fixed at 2" is enforced by the type and not only by this comment — a stray
    /// <c>Resize</c> on the child pool clamps to 2 instead of widening it.
    /// </summary>
    private readonly RunSlotPool _childSlots = new(ChildSlotWidth, ChildSlotWidth);

    /// <summary>Fixed width of <see cref="_childSlots"/> — and, deliberately, also its hard cap.</summary>
    private const int ChildSlotWidth = 2;

    /// <summary>Cancelled once at shutdown; every run CTS is linked to it.</summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, Task Task)> _inflight = new();

    /// <summary>chat id → run ids launched this session, for same-session workspace cleanup on chat delete.</summary>
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _runsByChat = new();
    private readonly object _runsByChatLock = new();

    private static readonly TimeSpan _workspaceMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Retention floor for a run that has SETTLED: an unanswered
    /// publish offer must not pin a workspace forever. A clean run's workspace is already gone — promotion
    /// tears it down before the run is marked Completed — so this window really only serves the
    /// failed/cancelled runs whose offer the user never answered. Anything NON-terminal keeps the 30-day
    /// floor above, because it may still be resumable. A judgement call, not a measurement: if seven days
    /// turns out to be short, it is one constant.
    /// </summary>
    private static readonly TimeSpan _terminalWorkspaceMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Resume FLOOR: the grant set a resume falls back to when the launch envelope is missing or
    /// unreadable. Deliberately the NARROWEST useful grant — never a destructive one — because a resume
    /// must never be able to widen what the launch actually granted.
    /// </summary>
    private static readonly string[] ResumeFloorGrants = ["write_file"];

    /// <summary>
    /// May this run PARK a promptable-but-ungranted tool call and ask a human, instead of
    /// hard-denying it? Exactly one fact decides it, and it decides it for the whole run: a ROOT run may, a
    /// CHILD run may not.
    /// PRIMARY REASON — a child is a DELEGATE. <see cref="NarrowForChild"/> already states the rule this
    /// follows ("it does the work the parent asked for and it does not get to destroy anything"): a child
    /// receives a strict SUBSET of its parent's authority and can never acquire more, which is what
    /// pins it to. An approval park ACQUIRES authority — the human's Continue adds a grant the run
    /// did not launch with — so allowing it on a child would make the one path by which a delegate ends up
    /// wider than its delegator. The parent is where a widening request belongs, and the parent can make it.
    /// SUPPORTING — a parked child has nowhere to ask. <c>AgentRunNotificationSurface</c> filters child runs
    /// out of the Flow publish, because a Continue card carrying the CHILD's run id is "a transition nothing
    /// supports": a child is only ever re-dispatched by its parent's fan-out. A child that parked for
    /// approval would therefore sit at <c>WaitingForInput</c> with no card and no panel, and its parent would
    /// re-park behind it under <c>ChildrenParkedReason</c> — a run stuck on a question nobody was asked.
    /// (Resuming a parked child by hand IS supported, so this is the supporting argument, not the load-bearing
    /// one.)
    /// </summary>
    private static bool CanParkForApproval(Guid? parentRunId) => parentRunId is null;

    /// <summary>
    /// Which token to re-park with after an interrupted resume: normally <see cref="ResumeInterruptedReason"/>,
    /// but the original token for a needs-goal/needs-input/plan-approval park, since overwriting any of them
    /// would break the resume's re-plan guard, its answer-persistence gate, or the approval card — each keys
    /// on the specific reason.
    /// </summary>
    private static string InterruptedReasonFor(string? parkReason) => parkReason switch
    {
        AgentRunOrchestrator.NeedsGoalReason => AgentRunOrchestrator.NeedsGoalReason,
        AgentRunOrchestrator.NeedsInputReason => AgentRunOrchestrator.NeedsInputReason,
        AgentRunOrchestrator.PlanApprovalReason => AgentRunOrchestrator.PlanApprovalReason,
        _ => ResumeInterruptedReason,
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
    /// Owns BOTH workspace provisioning modes and their symmetric teardown. Trailing
    /// and defaulted: null is the pre-Batch-06 shape — a bare <c>CreateDirectory</c> at launch and a plain
    /// recursive delete on cleanup — which is what the existing launcher suite exercises.
    /// </summary>
    private readonly IRunWorkspaceService? _workspaces;

    /// <summary>
    /// where each dispatch publishes its own cancel sink, so a user pause can interrupt the
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
        // (SensitivePathGuard.BuildAllowedExceptions) and this default can never drift apart.
        _runsBaseDir = runsBaseDirOverride ?? AssistantWorkspace.RunsRoot;
        _workspaces = workspaces;
        _steering = steering;

        // Decision c: delete a run's workspace when its chat (and, by FK cascade, its run) is deleted.
        _chatService.ChatsChanged += OnChatsChanged;
        // The run pool is live-resizable, and THIS is the arm that makes "live" mean anything — the lazy
        // Resize on the dispatch paths cannot help a run that is already queued, which is the one case a user
        // raising the cap is trying to fix. Deliberately NOT an initial read here: the ctor is synchronous and
        // GetSettingsAsync is not, so the width is picked up on the first launch (and on every save after).
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// apply a saved run-pool width immediately. Raising it releases the extra permits at once, so a run
    /// queued on a slot starts without waiting for an unrelated run to finish; lowering it absorbs permits as
    /// running dispatches finish and never preempts one (see <see cref="RunSlotPool"/>).
    /// Fires on EVERY settings save — the settings sliders save per tick — so the work here has to be trivial:
    /// <c>Resize</c> is synchronous, allocation-free and early-returns when the width is unchanged, and only the
    /// PARENT pool is ever resized (<see cref="_childSlots"/> has no setting, and merging the two deadlocks).
    /// </summary>
    private void OnSettingsChanged(object? sender, AppSettings e) => _slots.Resize(e.GetMaxParallelBackgroundRuns());

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
    /// <see cref="_childSlots"/> — see its remarks for why the shared pool deadlocks. Note the T1-1 resize
    /// below targets <see cref="_slots"/> BY NAME rather than this parameter: the setting sizes the parent pool,
    /// and a child launch reading it must not be able to touch the child pool's fixed width.</param>
    /// <param name="childPolicyJson">A child's pre-narrowed grant envelope, which REPLACES the resolve-from-
    /// request path entirely. Null ⇒ the ordinary launch resolution.</param>
    /// <param name="workspaceRootOverride">Non-null ⇒ SKIP <c>_workspaces.ProvisionAsync</c> entirely and pass
    /// this value straight to <c>executor.Initialize(workspaceRoot: …)</c>. A child run SHARES its parent's
    /// workspace: exactly ONE promotion is allowed per workspace, decided by a single
    /// <c>provisionedAtUtc</c>, and in worktree mode a per-child workspace would mean N <c>git worktree add</c>
    /// calls and N branches per fan-out.</param>
    /// <param name="personaIdOverride">A delegated step's assigned roster persona, which becomes the
    /// CHILD's run persona instead of the global per-mode one. Null ⇒ the ordinary resolution.</param>
    private async Task<HeadlessRunHandle> LaunchCoreAsync(
        HeadlessRunRequest req, Guid? parentRunId, RunSlotPool slots, string? childPolicyJson,
        string? workspaceRootOverride, Guid? personaIdOverride, CancellationToken ct)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        // T1-1 cold start: no save has happened this session, so OnSettingsChanged has never fired and the pool
        // would still be at its compiled default. Free to do on every launch (a no-op at an unchanged width),
        // and it reads the settings this method already loaded rather than loading them twice.
        _slots.Resize(settings.GetMaxParallelBackgroundRuns());
        var persona = await ResolveRunPersonaAsync(personaIdOverride, req.PersonaId, settings).ConfigureAwait(false);
        // The provider ladder reads the persona: req.ProviderId (null for a child), then the persona's
        // PreferredProviderId, then the mode default, and it clones to apply ReasoningEffort. So handing it the
        // ASSIGNED persona is what gives a delegated step its specialist's provider and effort too — D5's
        // "each persona running on its own provider" — with no second ladder to keep in step.
        var provider = await ResolveProviderAsync(req.ProviderId, persona, req.ReasoningEffort).ConfigureAwait(false)
            // Typed, because a caller re-arming a one-off needs a verdict it can trust: nothing is written
            // until the stub chat below, so this is the launcher vouching for "nothing spent, nothing written".
            ?? throw new PreModelLaunchException("No provider configured for a headless agent run.");

        // The AgentRuns FK requires its AssistantChats parent row first, and FK enforcement is ON.
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
        // A null GrantedWrites takes the narrow default; an explicitly EMPTY collection still means
        // "no write grants at all" and is preserved as such (never re-widened to the default).
        // A CHILD's envelope arrives ALREADY NARROWED from its parent's and must not be re-resolved from
        // the request or from settings: both would widen it. Read it back through the same reader a resume uses
        // so the executor gets exactly what was persisted, i.e. one source of truth per dispatch.
        var policyJson = childPolicyJson ?? TrySerializeGrantEnvelope(
            req.GrantedWrites ?? HeadlessRunRequest.DefaultGrantedWrites,
            req.Trigger,
            // The autonomy policy is resolved from SETTINGS at launch — the launch never reads the envelope
        // back, so there is nothing else to resolve it from. Off ⇒ null ⇒ the member is
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
        // A fresh launch has no denials; a child inherits its parent's (NarrowForChild carried them into the
        // child envelope). Restored with the same reader a resume uses — one source of truth per dispatch.
        var denied = childPolicyJson is null ? null : TryRestoreDeniedWritesEnvelope(childPolicyJson);

        var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, req.Trigger, req.TriggerRef, req.OwnerDeviceId, Goal: req.Goal,
            PolicyJson: policyJson, ParentRunId: parentRunId,
            // What this dispatch RESOLVED, not what the request asked: a resume has no job store to walk the
            // ladder again from.
            PersonaId: persona.Id,
            ReasoningEffort: RunPinResolver.EffectiveEffort(req.ReasoningEffort, persona.ReasoningEffort),
            // This dispatch walked the whole effort ladder, so its answer — INCLUDING a null one — is the
            // authority a resume freezes on.
            EffortPinRecorded: true), ct)
            .ConfigureAwait(false);

        // The run's ISOLATED workspace under runs\<runId>, carved out of SensitivePathGuard by
        // AssistantWorkspace.RunsRoot. Every file operation this run performs resolves against
        // this directory (see the Initialize call below), so it holds the run's work — not merely scratch —
        // until a later group promotes it out; it is still auto-cleaned on chat delete / startup sweep.
        //
        // ONE contiguous block, deliberately: the decision is "which root does this dispatch get", and a
        // later batch overrides exactly that (a child run inherits its parent's root and must not provision
        // at its own run id). Sprinkling it through the dispatch would make that a rewrite.
        string? runRoot;
        if (parentRunId is not null)
        {
            // A CHILD never provisions. It shares the parent's root verbatim — the
            // parent's own terminal settle promotes everything the whole fan-out wrote, exactly once.
            //
            // The branch is on "is this a child", NOT on "is the override non-null", as the signature prose
            // reads: a null override on a CHILD dispatch means the PARENT ran unisolated (06's no-isolation
            // degrade), and provisioning at the child's own id there would isolate a child whose parent is
            // writing the assistant folder — the two would then not even share a directory. Null propagates,
            // so parent and child are always in the same isolation regime.
            runRoot = workspaceRootOverride;
        }
        else if (_workspaces is not null)
        {
            // The provisioner owns both modes (worktree when the source root is a repo, else a
            // bounded copy) and its symmetric teardown. It NEVER throws and returns null for "no isolation",
            // so the FailAsync settle below is unreachable on this path — see B16 for why that is the
            // intended outcome (degrade rather than fail an unattended run) and why the block stays anyway.
            runRoot = (await _workspaces.ProvisionAsync(run.Id, req.WorkingSubpath, ct).ConfigureAwait(false))?.Root;
        }
        else
        {
            // Legacy path (no provisioner injected): the original create + canonicalize, so a link in the
            // path is not a hole. The run row already exists (Planning), so a workspace-setup failure here
            // must settle it — otherwise the run dangles non-terminal until the next startup sweep.
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
                try
                {
                    await _agentRunService.FailAsync(
                        run.Id, WorkspaceSetupFailure, cancelled: false, CancellationToken.None,
                        FailureMapper.ForReason(WorkspaceSetupFailure)).ConfigureAwait(false);
                }
                catch (Exception fx) { _logger.LogWarning(fx, "Failed to settle headless run {RunId} after workspace-setup failure", run.Id); }
                throw;
            }
        }

        var budget = req.Budget ?? RunProfile.FromBudget(
            settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes);

        // Linked to the shutdown token ONLY — not the caller's ct (a fire-and-forget run must survive the
        // command returning). Shutdown cancels every run; per-run cancel disposes this source.
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

        // THIS dispatch's cancel sink. A local delegate rather than a lambda passed inline,
        // because the same instance is the release token below (reference equality is what stops a finishing
        // dispatch from dropping a live resume's registration). Best-effort by design — a disposed source must
        // never break a pause or a cascade.
        Action steerCancel = () => { try { runCts.Cancel(); } catch { /* already disposed/cancelled */ } };
        _steering?.RegisterDispatch(run.Id, steerCancel);

        // Teardown is keyed on WORKSPACE OWNERSHIP, not on run id. This index exists so
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

        // Claim this dispatch's place in the pool's admission queue HERE, on the thread that decided the
        // order — the scheduler tick awaits its launches oldest-due-first, and without a ticket that order is
        // lost because the slot wait below runs on a thread-pool thread. Deliberately the LAST statement before
        // Task.Run and nothing throwable in between: an unused ticket stalls the pool's chain (see TakeTicket).
        // Taken from the `slots` PARAMETER, unlike the Resize above which deliberately names _slots: the width is
        // the parent pool's setting, but the ordering belongs to whichever pool this dispatch will queue on, and
        // a child's chain must stay separate from its parent's.
        var ticket = slots.TakeTicket();
        var completion = Task.Run(async () =>
        {
            var acquired = false;
            var started = false;
            try
            {
                await slots.WaitAsync(ticket, runCts.Token).ConfigureAwait(false);
                acquired = true;

                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<HeadlessTurnExecutor>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
                // The run is confined to its own workspace. Every read/write/delete/list/search
                // resolves against runRoot with full containment (no escape, no system paths) — the guard
                // permits it because AssistantWorkspace.RunsRoot is an allowed island, and the verifier
                // probes the same root because BeginRunAsync publishes it onto the RunContext. A NULL
                // runRoot is the NO-ISOLATION degrade the provisioner falls back to (G3 F10): the run writes
                // straight into the user's assistant files folder, which is what every build before G2 did.
                //
                // canPark: a ROOT run may stop and ask a human for a promptable capability it was
                // not granted; a CHILD run may not, and hard-denies exactly as before. See CanParkForApproval.
                executor.Initialize(workspaceRoot: runRoot, grants, provider, policy,
                    canPark: CanParkForApproval(parentRunId), deniedWrites: denied, personaOverride: persona);
                started = true;

                // Open the composer bracket. Deliberately HERE and not before `_slots.WaitAsync` above:
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
                // run here so a queued-then-cancelled run is never left non-terminal.
                if (!started)
                {
                    try
                    {
                        await _agentRunService.FailAsync(
                            run.Id, ShutdownInterruptedFailure, cancelled: true, CancellationToken.None,
                            FailureMapper.ForReason(ShutdownInterruptedFailure)).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to settle interrupted headless run {RunId}", run.Id); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Headless run {RunId} launcher task faulted", run.Id);
                // Defense in depth: the orchestrator settles its own terminal state on every path today,
                // but if a future refactor let RunAsync throw after we entered it, the run would dangle Running.
                if (started)
                {
                    try
                    {
                        await _agentRunService.FailAsync(
                            run.Id, ex.Message, cancelled: false, CancellationToken.None,
                            FailureMapper.ForException(ex)).ConfigureAwait(false);
                    }
                    catch (Exception fx) { _logger.LogWarning(fx, "Failed to settle faulted headless run {RunId}", run.Id); }
                }
            }
            finally
            {
                if (acquired) slots.Release();

                // Close the composer bracket on EVERY exit, including the never-started paths (a no-op
                // there). Deliberately AFTER the slot release: this is bookkeeping, and it must never be able
                // to strand the shared concurrency slot. Idempotent with the release ChatSessionManager
                // already did when the terminal RunChanged arrived — CompleteAsync raises that event before
                // this finally runs, so either side may get here first.
                _executingRuns.Release(run.Id);
                RemoveInflight(run.Id, runCts);
                // Drop this dispatch's sink AND any pause request it never consumed — the
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

    /// <inheritdoc />
    public event EventHandler<ResumedRunSettledEventArgs>? ResumedRunSettled;

    public async Task<bool> ResumeAsync(
        Guid runId, string? nudge = null, CancellationToken ct = default, bool declineToolApproval = false)
    {
        var claim = await TryClaimForResumeAsync(runId, nudge, declineToolApproval, ct).ConfigureAwait(false);
        if (claim is not { Run: { } run })
            return false;

        var parkReason = claim.ParkReason;
        var approvedTool = claim.ApprovedTool;

        // The run is now CAS'd WaitingForInput→Running (the claim raised RunChanged(Running), retracting the
        // Flow card and disabling the panel Continue). Any failure between here and the orchestrator loop being
        // attached would leave the run dangling Running — unresumable and losing the parked work until the next
        // startup sweep cancels it. Re-park it on ANY such pre-dispatch failure so it stays resumable
        // (guardrail 1 — a resume error must never wedge a run; guardrail 3 — parked survives).
        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            // T1-1 cold start, symmetric with the launch path: a resume can be the FIRST thing this launcher
            // does in a session (a run parked before a restart), so the width has to be picked up here too.
            _slots.Resize(settings.GetMaxParallelBackgroundRuns());
            // Off the RUN ROW, not the job store: the launcher must not depend on it, and a job's pins can
            // change or vanish between park and Continue.
            var persona = await RunPinResolver.ResolvePersonaAsync(
                _personaService, run.PersonaId,
                settings.UserOperatingMode ?? UserOperatingMode.Personal, _logger).ConfigureAwait(false);
            // The launch already resolved a provider and wrote it onto the run's stub chat, so that row is what
            // carries an explicitly pinned ProviderId across the park — the job store is not consulted here, and
            // the ladder alone would answer whatever the persona/mode default now says. A deleted provider still
            // falls through the ladder inside ResolveProviderAsync rather than failing the resume.
            var launchProviderId = await GetRunProviderIdAsync(run).ConfigureAwait(false);
            var provider = await ResolveProviderAsync(
                    launchProviderId, persona, run.ReasoningEffort, freezeEffort: run.EffortPinRecorded)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No provider configured to resume an agent run.");
            // Restore the write grants the LAUNCH resolved from the run's own envelope — a resume must
            // never widen them, so a narrowly-granted scheduled job that parked at its budget does NOT come
            // back with write+delete over the user's real assistant-files folder. Missing/unreadable/foreign
            // envelope → the FLOOR ({write_file}, never delete_file), logged with the run id only.
            var grants = TryRestoreGrantEnvelope(run.PolicyJson);
            // Whether the row held a document THIS build understands. The approval widening below may only write
            // back over one that it did — see the comment there.
            var envelopeWasReadable = grants is not null;
            if (grants is null)
            {
                _logger.LogInformation(
                    "Resume: run {RunId} has no readable launch-grant envelope; using the write-only floor", run.Id);
                grants = ResumeFloorGrants;
            }

            // The autonomy policy comes ONLY from the run's own envelope, never from settings: a
            // settings flip between park and Continue must not widen a parked run. Absent/unreadable/absent
            // member ⇒ null ⇒ today's behaviour, which is the restrictive direction — unlike the grant list,
            // whose fallback is a floor the run can work with.
            var policy = TryRestorePolicy(run.PolicyJson, _logger);

            // The denial list this run already carries (empty for a never-declined envelope, unlike the grant
            // floor: a missing denial narrows nothing). The decline branch below appends to it.
            var denied = TryRestoreDeniedWritesEnvelope(run.PolicyJson);

            (grants, denied) = await ApplyToolApprovalDecisionAsync(
                run, approvedTool, declineToolApproval, grants, denied, policy, envelopeWasReadable, ct)
                .ConfigureAwait(false);

            // Budget is DELIBERATELY not restored: a FRESH budget envelope IS the "continue" grant
            // that is the whole point of the pause. Only the write grants are restored.
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
            var runRoot = await ResolveResumeWorkspaceRootAsync(run, ct).ConfigureAwait(false);

            var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

            // This dispatch's cancel sink, same shape as the launch path. Registering OVERWRITES,
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

            // T1-3, same rule as the launch path (and for the same reason — see LaunchCoreAsync): a resume that
            // did not take a ticket would jump the queue, being the one dispatch whose wait is not ordered
            // against anything. The PARENT pool's chain, matching the pool the wait below uses.
            var ticket = _slots.TakeTicket();
            var plan = new ResumeDispatchPlan(
                run, persona, provider, budget, grants, policy, denied, runRoot, nudge, parkReason);
            var completion = Task.Run(
                () => RunResumedDispatchAsync(plan, runCts, ticket, steerCancel), CancellationToken.None);

            _inflight[run.Id] = (runCts, completion);
            return true;
        }
        catch (Exception ex)
        {
            // Pre-dispatch failure (settings/persona/provider resolve, workspace create) after the CAS win.
            // Re-park so the run leaves Running and stays resumable; report the resume did not start.
            _logger.LogError(ex, "Resume of run {RunId} failed before dispatch; re-parking", runId);
            try { await _agentRunService.PauseAsync(runId, InterruptedReasonFor(parkReason), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception px) { _logger.LogWarning(px, "Failed to re-park run {RunId} after pre-dispatch resume failure", runId); }
            return false;
        }
    }

    /// <summary>What a won resume claim carries forward. Null from <see cref="TryClaimForResumeAsync"/> means the
    /// claim was not made or was lost, and the caller must return false without touching slots or inflight.</summary>
    private sealed record ResumeClaim(AgentRun Run, string? ParkReason, string? ApprovedTool);

    /// <summary>The resolved state one resumed dispatch runs on, frozen at the moment the dispatch is queued.</summary>
    private sealed record ResumeDispatchPlan(
        AgentRun Run,
        Persona Persona,
        AiProvider Provider,
        RunProfile Budget,
        IReadOnlyList<string> Grants,
        RunAutonomyPolicy? Policy,
        IReadOnlyList<string> Denied,
        string? RunRoot,
        string? Nudge,
        string? ParkReason);

    /// <summary>
    /// Atomic claim FIRST: a panel+Flow race or double-click → only one winner. On the lost path
    /// the caller returns BEFORE touching _slots/_inflight/_runsByChat — no slot leak, no duplicate run.
    /// </summary>
    /// <remarks>
    /// TWO claims, disjoint by SOURCE STATE, chosen from the row already read. An explicit dispatch,
    /// never a range and never "try one, then the other": a run whose state moved between the read and the
    /// CAS is not ours, and the loser's log line says so. A budget park is WaitingForInput; a USER pause is
    /// Paused, and its claim also retires the pause envelope it consumed.
    /// </remarks>
    private async Task<ResumeClaim?> TryClaimForResumeAsync(
        Guid runId, string? nudge, bool declineToolApproval, CancellationToken ct)
    {
        var run = await _agentRunService.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null) { _logger.LogWarning("Resume: run {RunId} not found", runId); return null; }

        // READ THE QUESTION BEFORE ANSWERING IT. Both claims below clear ExtraJson, so the tool
        // name a tool-approval park wrote is gone the instant one of them wins — it has to come off the row we
        // already read at the top of this method. Null for every other park, and for a tool-approval park
        // whose envelope did not survive: a resume that cannot read the question grants nothing extra and the
        // run simply parks again on the same tool, which is the fail-closed direction.
        // Read here, before the claim below NULLs the ExtraJson it lives in — the orchestrator's re-plan guard
        // needs to know why this run parked, and this is the last point that can still tell it.
        var parkReason = run.State == AgentRunState.WaitingForInput ? RunPauseEnvelope.ReadReason(run) : null;
        var approvedTool = parkReason == AgentRunOrchestrator.ToolApprovalReason
            ? RunPauseEnvelope.ReadApprovalTool(run)
            : null;

        // A decline answers a tool-approval park only; on any other park there is no question to say "no" to,
        // and claiming the CAS anyway would turn a budget pause into a denied-tool resume.
        if (declineToolApproval && approvedTool is null)
        {
            _logger.LogInformation("Decline: run {RunId} is not parked on a tool-approval question", runId);
            return null;
        }

        var claimed = run.State switch
        {
            AgentRunState.WaitingForInput => await _agentRunService.TryBeginResumeAsync(runId, ct).ConfigureAwait(false),
            AgentRunState.Paused => await _agentRunService.TryResumeFromPauseAsync(runId, ct).ConfigureAwait(false),
            _ => false,
        };
        if (!claimed)
        {
            _logger.LogInformation("Resume: run {RunId} not claimable (already resumed/not parked)", runId);
            return null;
        }

        // Persisted here, in its own try, before dispatch: the nudge is the user's answer on a clarification
        // park but is otherwise scope-to-dispatch and never persisted, so it would be lost by the next park.
        if (parkReason is AgentRunOrchestrator.NeedsGoalReason or AgentRunOrchestrator.NeedsInputReason
            && !string.IsNullOrWhiteSpace(nudge))
        {
            // The answer text never reaches a log line here — only the reason token does; the text itself
            // goes out on SensitiveDebug inside AppendClarificationAsync.
            try { await _agentRunService.AppendClarificationAsync(run.Id, nudge, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resume: could not record the clarification answer for run {RunId} "
                    + "(reason {Reason}) — the run resumes without it", run.Id, parkReason);
            }
        }

        return new ResumeClaim(run, parkReason, approvedTool);
    }

    /// <summary>
    /// Apply the human's decision to the call that parked the run. Continue IS the approval — the run
    /// stopped and asked "may I use <c>tool</c>?", and the only affordance the card carries is the button the user
    /// just pressed, so pressing it grants that one named tool for this run and nothing else.
    /// </summary>
    /// <remarks>
    /// The pending CALL cannot be replayed — a park outlives the process, and the deferred action's Execute()
    /// delegate does not — so what is applied is the CAPABILITY. The step's row went back to Pending when the run
    /// parked, the drain loop re-runs it from the top, and the same tool call now resolves GrantedByName instead
    /// of parking.
    /// PERSISTED, not merely handed to this dispatch. Two tools, two parks: without persistence the second resume
    /// would restore the launch envelope and forget the first approval, so the run would park on tool A, be
    /// granted A, park on B, be granted B but lose A, park on A again — a livelock paced by a human clicking
    /// Continue. The write is failure-isolated: a fault leaves the run with its launch envelope, i.e. it re-parks
    /// and asks again, never runs ungranted.
    /// </remarks>
    private async Task<(IReadOnlyList<string> Grants, IReadOnlyList<string> Denied)> ApplyToolApprovalDecisionAsync(
        AgentRun run, string? approvedTool, bool declineToolApproval,
        IReadOnlyList<string> grants, IReadOnlyList<string> denied, RunAutonomyPolicy? policy,
        bool envelopeWasReadable, CancellationToken ct)
    {
        if (declineToolApproval && approvedTool is not null)
        {
            // Decline is a DECISION, not the absence of approval: the refusal is persisted onto the
            // envelope so the re-run step's call resolves DeniedForRun ("adapt") instead of parking a
            // second time on the same settled question. Same persist discipline as the widening below —
            // only over a readable envelope, failure-isolated, applied to THIS dispatch either way.
            var deniedNow = denied.Contains(approvedTool, StringComparer.OrdinalIgnoreCase)
                ? denied.ToList()
                : denied.Append(approvedTool).ToList();
            if (envelopeWasReadable)
            {
                try
                {
                    await _agentRunService.UpdatePolicyJsonAsync(
                            run.Id, SerializeGrantEnvelope(grants, run.TriggerKind, policy, deniedNow), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not persist the denied tool for run {RunId}", run.Id);
                }
            }

            _logger.LogInformation("Resume: run {RunId} declined tool {ToolName} for this run", run.Id, approvedTool);
            return (grants, deniedNow);
        }

        if (approvedTool is not null && !grants.Contains(approvedTool, StringComparer.OrdinalIgnoreCase))
        {
            var widened = grants.Append(approvedTool).ToList();
            // ONLY over a document this build actually read. `grants` may be the resume FLOOR
            // ({write_file}), which is a per-dispatch degrade for an absent/corrupt/foreign/future envelope
            // and is deliberately WIDER than some launches — InteractiveEmptyEnvelopeJson exists as its own
            // documented shape for exactly that reason. Serializing a fresh v:1 document on top of it would
            // make the degrade the run's DURABLE record of its own authority: a run that never held
            // write_file would come back holding it on every later resume, and because the resume path
            // re-reads the row and hands PolicyJson to LaunchChildAsync, the next fan-out would narrow its
            // children from the widened envelope instead of the real one. It would also overwrite a document
            // a NEWER build wrote, which no build could then read back.
            //
            // On the floor path the grant is applied to THIS dispatch only, which is precisely what the
            // Continue card promised. The livelock the persist exists to prevent needs a run that parks on two
            // different tools; on an unreadable envelope re-parking and asking again is the fail-closed
            // direction the claim already takes when it cannot read the question at all.
            if (envelopeWasReadable)
            {
                try
                {
                    await _agentRunService.UpdatePolicyJsonAsync(
                            // The ROW's trigger, not the envelope's. GrantEnvelope.Trigger is provenance that
                            // "never widens a grant", and the row is the authoritative copy of the same fact.
                            run.Id, SerializeGrantEnvelope(widened, run.TriggerKind, policy), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not persist the approved tool grant for run {RunId}", run.Id);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Resume: run {RunId} granted {ToolName} for this dispatch only — its launch envelope is "
                    + "not readable by this build, so the floor must not be written back over it",
                    run.Id, approvedTool);
            }

            // Applied to THIS dispatch whether or not the persist landed: the human answered, and the
            // question they answered was about the step that is about to re-run.
            _logger.LogInformation("Resume: run {RunId} granted approved tool {ToolName}", run.Id, approvedTool);
            return (widened, denied);
        }

        return (grants, denied);
    }

    /// <summary>
    /// The workspace a resumed dispatch re-enters. Idempotent: the run's isolated workspace already exists from
    /// the original launch (or is recreated). Returns the canonicalized path rather than discarding it — the
    /// resumed dispatch hands this exact string to <c>Initialize</c>, and recomputing it from Path.Combine there
    /// would give the executor a different string than launch does for the same directory (a link or an 8.3
    /// component in the base dir), i.e. the two call sites would silently drift apart.
    /// </summary>
    private async Task<string?> ResolveResumeWorkspaceRootAsync(AgentRun run, CancellationToken ct)
    {
        if (run.ParentRunId is { } parentId)
        {
            // Change 3, and the reason it is not optional: this path provisions at its OWN
            // run id, and every child owns a stub chat — so a user opening a parked child's chat and
            // pressing Continue would otherwise create a SECOND workspace at the child's id, diverging from
            // the parent's and outliving it until the sweep. Resolve the PARENT's root instead, and only if
            // it still exists: a parent that ran unisolated, or whose workspace is already gone, leaves the
            // child writing the assistant folder — the same coherent degrade as a fresh child dispatch.
            var parentRoot = _workspaces?.RootFor(parentId) ?? Path.Combine(_runsBaseDir, parentId.ToString());
            return Directory.Exists(parentRoot) ? SafeFolderPath.Canonicalize(parentRoot) : null;
        }

        if (_workspaces is not null)
        {
            // Idempotent by construction: a readable metadata document returns the same
            // root, the same mode and the same provisionedAtUtc, which is what keeps the promote set
            // from becoming "everything the workspace contains" after a park → resume.
            return (await _workspaces.ProvisionAsync(run.Id, workingSubpath: null, ct).ConfigureAwait(false))?.Root;
        }

        return SafeFolderPath.Canonicalize(
            Directory.CreateDirectory(Path.Combine(_runsBaseDir, run.Id.ToString())).FullName);
    }

    /// <summary>The resumed dispatch itself: re-acquire a slot, build the scope, and hand the run to the
    /// orchestrator. Every exit re-parks or settles the row, so a claimed run never dangles Running.</summary>
    private async Task RunResumedDispatchAsync(
        ResumeDispatchPlan plan, CancellationTokenSource runCts, RunSlotPool.Ticket ticket, Action steerCancel)
    {
        var run = plan.Run;
        var acquired = false;
        var started = false;
        try
        {
            // Deliberately the PARENT pool even for a resumed child: a resume is a USER
            // act, so nothing is awaiting this dispatch from inside another run's RunAsync, and the
            // nested-acquire deadlock that _childSlots exists to prevent cannot arise here.
            await _slots.WaitAsync(ticket, runCts.Token).ConfigureAwait(false); // re-acquire a slot (guardrail 6)
            acquired = true;

            using var scope = _scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<HeadlessTurnExecutor>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
            // Symmetric with the launch path: a resumed run re-enters the SAME isolated
            // workspace it was parked in, so the Pending remainder sees the work the pre-pause steps
            // left behind. A separate literal from the launch call on purpose — the two have drifted
            // before, so each has its own regression fact.
            executor.Initialize(workspaceRoot: plan.RunRoot, plan.Grants, plan.Provider, plan.Policy,
                canPark: CanParkForApproval(run.ParentRunId), deniedWrites: plan.Denied,
                personaOverride: plan.Persona);
            started = true;
            // Same bracket, same reasoning as the launch path — after the slot wait, before the
            // executor can write. TryBeginResumeAsync already raised RunChanged(Running) at the CAS,
            // i.e. before this line, which is why ChatSessionManager keeps its ActiveRunId-matched
            // term as well as reading this index.
            _executingRuns.Register(run.ChatId, run.Id);
            // ParkReason carries the pre-claim token down to the orchestrator's planning guard — the
            // only way it can get there, since the claim already NULLed the envelope it came from.
            await orchestrator.RunAsync(run, executor, plan.Persona, plan.Provider, plan.Budget, runCts.Token,
                    resume: true, nudge: plan.Nudge, parkReason: plan.ParkReason)
                .ConfigureAwait(false); // resume:true → drains the Pending remainder; re-plans only for
                                        // A needs-goal park with no step rows
        }
        catch (OperationCanceledException)
        {
            // Cancel during resume before entering the orchestrator: the run was CAS'd to Running by
            // the claim, so re-park it (rather than leave it dangling Running) — it stays resumable.
            if (!started)
                await ReParkInterruptedResumeAsync(run.Id, plan.ParkReason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume of run {RunId} faulted", run.Id);
            if (started)
            {
                try
                {
                    await _agentRunService.FailAsync(
                        run.Id, ex.Message, cancelled: false, CancellationToken.None,
                        FailureMapper.ForException(ex)).ConfigureAwait(false);
                }
                catch (Exception fx) { _logger.LogWarning(fx, "Failed to settle faulted resume {RunId}", run.Id); }
            }
            else
            {
                // Faulted before entering the orchestrator (e.g. slot wait, scope/executor construction) —
                // the run was CAS'd to Running but no loop is attached. Re-park it so it stays resumable
                // rather than dangling Running.
                await ReParkInterruptedResumeAsync(run.Id, plan.ParkReason).ConfigureAwait(false);
            }
        }
        finally
        {
            if (acquired) _slots.Release();

            // See the launch path (and the same after-the-slot ordering, for the same reason). A
            // resume dispatch that starts while the previous dispatch is still unwinding re-registers
            // the same key, so this release can close the NEWER bracket — fail-open, which is the
            // recoverable direction (a stale true is not recoverable).
            _executingRuns.Release(run.Id);
            RemoveInflight(run.Id, runCts);
            // See the launch path's finally. The !started arms above re-park the row
            // themselves without entering the orchestrator, so an unconsumed request has to die here.
            _steering?.ReleaseDispatch(run.Id, steerCancel);
            runCts.Dispose();

            // T0-1(b): the resume path's substitute for a handle. LAST, and specifically AFTER the slot
            // release above — same rule as the composer bracket beside it, for the same reason: this is
            // bookkeeping, and a subscriber that blocks or throws must never be able to strand the
            // shared concurrency slot. Raised on EVERY arm, including the !started re-parks, because the
            // subscriber's state check is what tells a re-park apart from a settle; suppressing it here
            // would silently lose the case where the orchestrator DID run and settle. Swallowing is
            // deliberate — nothing in this finally has a caller to throw to.
            try { ResumedRunSettled?.Invoke(this, new ResumedRunSettledEventArgs(run.Id, run.ChatId)); }
            catch (Exception ex) { _logger.LogWarning(ex, "A ResumedRunSettled handler threw for run {RunId}", run.Id); }
        }
    }

    /// <summary>Put a claimed-but-never-dispatched run back where it was, so it stays resumable rather than
    /// dangling Running.</summary>
    private async Task ReParkInterruptedResumeAsync(Guid runId, string? parkReason)
    {
        try { await _agentRunService.PauseAsync(runId, InterruptedReasonFor(parkReason), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to re-park interrupted resume {RunId}", runId); }
    }

    /// <summary>
    /// The deny beside a tool-approval park's approve: resumes the run with the parked tool recorded in its
    /// envelope's denial list, so the re-run step hears "declined — adapt" instead of re-parking.
    /// </summary>
    public Task<bool> DeclineAsync(Guid runId, CancellationToken ct = default) =>
        ResumeAsync(runId, nudge: null, ct, declineToolApproval: true);

    /// <summary>Reject a plan-approval park. Never re-enters the dispatch machinery: the CAS settles the row
    /// directly, and the chat notice rides a scoped orchestrator like every other out-of-dispatch write.</summary>
    public async Task<bool> RejectPlanAsync(Guid runId, CancellationToken ct = default)
    {
        var rejected = await _agentRunService.TryRejectParkedPlanAsync(runId, ct).ConfigureAwait(false);
        if (!rejected)
            return false;

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            var persona = await ResolveRunPersonaAsync(
                personaIdOverride: null, pinnedPersonaId: null, settings).ConfigureAwait(false);

            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
            await orchestrator.PostPlanRejectedNoticeAsync(runId, persona, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: the CAS above already committed, so a failed notice is a missing chat line, not a
            // wedged run.
            _logger.LogWarning(ex, "Failed to post the plan-rejected notice for run {RunId}", runId);
        }

        return true;
    }

    /// <summary>
    /// App shutdown: cancel every dispatch and wait, bounded, for them to unwind.
    /// this path deliberately does <b>not</b> revoke a pending pause request, unlike the four
    /// terminal-intent sites that do. It is the recoverable asymmetry — a run whose pause request is still
    /// unconsumed when the shutdown token fires comes back <see cref="AgentRunState.Paused"/> and RESUMABLE
    /// rather than <c>Cancelled</c>, which is the direction the user asked for and the only one that keeps the
    /// work. Asserted, not merely commented, by
    /// <c>HeadlessRunLauncherTests.Shutdown_DoesNotRevokeAPendingPause_SoTheRunComesBackResumable</c>.
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
            // The retention rule and its mitigation, in one predicate. A
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

            // Second pass: the loop above enumerates DIRECTORIES only, so a metadata document
            // whose workspace is already gone is invisible to it — and in worktree mode that document is the
            // only thing that knows which repository still carries a stale.git/worktrees/<id> registration
            // (plan R5).
            if (_workspaces is not null)
                await _workspaces.SweepOrphanMetadataAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// A delegated step's assigned persona, then the job's pin, then the per-mode resolution. Only the DELEGATED
    /// id is roster-gated (an empty roster is the default), no arm throws, and no log line here names a persona.
    /// </summary>
    private async Task<Persona> ResolveRunPersonaAsync(
        Guid? personaIdOverride, Guid? pinnedPersonaId, AppSettings settings)
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

        return await RunPinResolver.ResolvePersonaAsync(_personaService, pinnedPersonaId, mode, _logger)
            .ConfigureAwait(false);
    }

    /// <summary>The provider the launch resolved, off the run's own chat row. A store fault answers null, which
    /// leaves the resume on the ladder — today's behaviour.</summary>
    private async Task<Guid?> GetRunProviderIdAsync(AgentRun run)
    {
        try
        {
            return await _chatService.GetProviderIdAsync(run.ChatId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not read the launch provider of run {RunId} ({Error}); resolving it from the ladder",
                run.Id, ex.GetType().Name);
            return null;
        }
    }

    /// <param name="freezeEffort">A RESUME whose row recorded what its launch resolved: the persona rung is
    /// withheld, so a null <paramref name="jobEffort"/> means "the launch resolved no effort" and a persona
    /// edited during the park cannot change what the remaining steps cost.</param>
    private async Task<AiProvider?> ResolveProviderAsync(
        Guid? explicitProviderId, Persona persona, ReasoningEffort? jobEffort = null, bool freezeEffort = false)
    {
        AiProvider? provider = null;
        if (explicitProviderId.HasValue)
            provider = await _providerService.GetProviderAsync(explicitProviderId.Value).ConfigureAwait(false);
        if (provider is null && persona.PreferredProviderId.HasValue)
            provider = await _providerService.GetProviderAsync(persona.PreferredProviderId.Value).ConfigureAwait(false);
        provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant).ConfigureAwait(false);
        if (provider is null)
            return null;

        return RunPinResolver.ApplyEffort(
            provider, jobEffort, freezeEffort ? null : persona.ReasoningEffort);
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
    /// Cancelling alone was not enough, and the gap was the whole of a reachable
    /// trigger: <c>Cancel()</c> returns immediately while the step is still inside a <c>write_file</c>, which is
    /// exactly when <c>git worktree remove</c> and a recursive delete BOTH fail. The task awaited here is the one
    /// <see cref="_inflight"/> has always held beside the CTS and nothing read.
    /// BOUNDED, and it tears down anyway on a timeout — that is the pre-existing behaviour, and a failed delete
    /// self-heals: the run row is gone by FK cascade, so the next startup sweep sees <c>run is null</c> and
    /// removes the workspace unconditionally. The dispatch task never faults (every path inside it is caught), so
    /// the catch-all is for a future refactor rather than for today.
    /// </summary>
    private async Task CancelThenTearDownWorkspaceAsync(Guid runId)
    {
        if (_inflight.TryGetValue(runId, out var entry))
        {
        // Revocation site 3, and it must precede the cancel below: deleting the chat is
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
    /// cancel nor await that run. The dispatch's CTS is its identity — it is created per dispatch and
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
        // Beside the ChatsChanged unsubscribe and for the same reason — this launcher is a singleton, and
        // a live handler on the settings service outlives it (in tests, it pins a per-test substitute).
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _shutdownCts.Dispose();
    }
}
