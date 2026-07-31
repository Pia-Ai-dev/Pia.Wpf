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

    /// <summary>Cancelled once at shutdown; every run CTS is linked to it (G-4).</summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, Task Task)> _inflight = new();

    /// <summary>chat id → run ids launched this session, for same-session workspace cleanup on chat delete.</summary>
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _runsByChat = new();
    private readonly object _runsByChatLock = new();

    private static readonly TimeSpan _workspaceMaxAge = TimeSpan.FromDays(30);

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
        string? runsBaseDirOverride = null)
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

        // Decision c: delete a run's workspace when its chat (and, by FK cascade, its run) is deleted.
        _chatService.ChatsChanged += OnChatsChanged;
    }

    public async Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var persona = await _personaService.ResolveActiveAsync(
            WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal).ConfigureAwait(false);
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
        var grants = req.GrantedWrites ?? HeadlessRunRequest.DefaultGrantedWrites;

        // The autonomy policy is resolved from SETTINGS at launch — the launch never reads the envelope back,
        // so there is nothing else to resolve it from (04 D9/D10). Off ⇒ null ⇒ the member is omitted and the
        // persisted document stays byte-identical to a pre-Batch-04 one.
        var policy = RunAutonomyPolicy.FromSettings(settings);

        var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, req.Trigger, req.TriggerRef, req.OwnerDeviceId, Goal: req.Goal,
            PolicyJson: TrySerializeGrantEnvelope(grants, req.Trigger, policy)), ct)
            .ConfigureAwait(false);

        // The run's ISOLATED workspace under runs\<runId> (§17.2), carved out of SensitivePathGuard by
        // AssistantWorkspace.RunsRoot (Batch 06 B1). Every file operation this run performs resolves against
        // this directory (see the Initialize call below), so it holds the run's work — not merely scratch —
        // until a later group promotes it out; it is still auto-cleaned on chat delete / startup sweep.
        // Canonicalize so a link in the path is not a hole. The run row already exists (Planning), so a
        // workspace-setup failure here must settle it — otherwise the run dangles non-terminal until the
        // next startup sweep (G-4).
        string runRoot;
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

        var budget = req.Budget ?? RunProfile.FromBudget(
            settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes);

        // Linked to the shutdown token ONLY — not the caller's ct (a fire-and-forget run must survive the
        // command returning). Shutdown cancels every run; per-run cancel disposes this source.
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

        lock (_runsByChatLock)
        {
            if (!_runsByChat.TryGetValue(chatId, out var set))
                _runsByChat[chatId] = set = new HashSet<Guid>();
            set.Add(run.Id);
        }

        _logger.LogInformation("Headless run {RunId} launched (chat {ChatId}, trigger {Trigger})", run.Id, chatId, req.Trigger);
        _logger.SensitiveDebug("Headless run {RunId} goal: {Goal}", run.Id, req.Goal);

        var completion = Task.Run(async () =>
        {
            var acquired = false;
            var started = false;
            try
            {
                await _slots.WaitAsync(runCts.Token).ConfigureAwait(false);
                acquired = true;

                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<HeadlessTurnExecutor>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
                // Batch 06 G2: the run is confined to its own workspace. Every read/write/delete/list/search
                // resolves against runRoot with full containment (no escape, no system paths) — the guard
                // permits it because AssistantWorkspace.RunsRoot is an allowed island (B1), and the verifier
                // probes the same root because BeginRunAsync publishes it onto the RunContext (B3). Passing
                // null here instead is the NO-ISOLATION degrade: the run would write straight into the user's
                // assistant files folder, which is what every build before this commit did.
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
                if (acquired) _slots.Release();

                // A2: close the composer bracket on EVERY exit, including the never-started paths (a no-op
                // there). Deliberately AFTER the slot release: this is bookkeeping, and it must never be able
                // to strand the shared concurrency slot. Idempotent with the release ChatSessionManager
                // already did when the terminal RunChanged arrived — CompleteAsync raises that event before
                // this finally runs, so either side may get here first.
                _executingRuns.Release(run.Id);
                RemoveInflight(run.Id, runCts);
                runCts.Dispose();
            }
        }, CancellationToken.None);

        _inflight[run.Id] = (runCts, completion);
        return new HeadlessRunHandle(run.Id, chatId, completion);
    }

    public async Task<bool> ResumeAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _agentRunService.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null) { _logger.LogWarning("Resume: run {RunId} not found", runId); return false; }

        // Atomic claim FIRST (guardrail 2): a panel+Flow race or double-click → only one winner. On the
        // lost path we return BEFORE touching _slots/_inflight/_runsByChat — no slot leak, no duplicate run.
        if (!await _agentRunService.TryBeginResumeAsync(runId, ct).ConfigureAwait(false))
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
            var runRoot = SafeFolderPath.Canonicalize(
                Directory.CreateDirectory(Path.Combine(_runsBaseDir, run.Id.ToString())).FullName);

            var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            lock (_runsByChatLock)
            {
                if (!_runsByChat.TryGetValue(run.ChatId, out var set))
                    _runsByChat[run.ChatId] = set = new HashSet<Guid>();
                set.Add(run.Id);
            }

            _logger.LogInformation("Resuming run {RunId} (chat {ChatId})", run.Id, run.ChatId);

            var completion = Task.Run(async () =>
            {
                var acquired = false;
                var started = false;
                try
                {
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
                    await orchestrator.RunAsync(run, executor, persona, provider, budget, runCts.Token, resume: true)
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
                    remove = run is null || Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow - _workspaceMaxAge;
                }
                catch { remove = false; }

                if (remove)
                    TryDeleteDirectory(dir);
            }
        }, ct);
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
            var dir = Path.Combine(_runsBaseDir, runId.ToString());
            TryDeleteDirectory(dir);
        }
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
