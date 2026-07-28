using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
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

    /// <summary>Envelope shape currently written/understood by this launcher. Anything else → the floor.</summary>
    private const int GrantEnvelopeVersion = 1;

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
        ILogger<HeadlessRunLauncher> logger,
        string? runsBaseDirOverride = null)
    {
        _scopeFactory = scopeFactory;
        _chatService = chatService;
        _agentRunService = agentRunService;
        _settingsService = settingsService;
        _providerService = providerService;
        _personaService = personaService;
        _logger = logger;
        _runsBaseDir = runsBaseDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia", "runs");

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

        var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, req.Trigger, req.TriggerRef, req.OwnerDeviceId, Goal: req.Goal,
            PolicyJson: TrySerializeGrantEnvelope(grants, req.Trigger)), ct)
            .ConfigureAwait(false);

        // Per-run scratch/temp workspace under runs\<runId> (§17.2). Real deliverables go to the assistant
        // files folder (see the Initialize call below), so this directory holds only ephemeral run temp and is
        // auto-cleaned on chat delete / startup sweep. Canonicalize so a link in the path is not a hole. The run
        // row already exists (Planning), so a workspace-setup failure here must settle it — otherwise the run
        // dangles non-terminal until the next startup sweep (G-4).
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
                // workspaceRoot: null → real deliverables write to the assistant files folder with full
                // read/write/delete, contained, like an interactive chat (only MCP is withheld). runRoot stays
                // the run's ephemeral scratch area. Passing runRoot here instead would confine the run to it
                // (the reserved opt-in-sandbox seam).
                executor.Initialize(workspaceRoot: null, grants, provider);
                started = true;
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
                _inflight.TryRemove(run.Id, out _);
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

            // Budget is DELIBERATELY not restored: a FRESH budget envelope IS the "continue" grant
            // (guardrail 4) — that is the whole point of the pause. Only the write grants are restored.
            // The ledger is persisted and accrues across resumes (never reset).
            var budget = RunProfile.FromBudget(
                settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes);

            // Idempotent: the run's ephemeral workspace already exists from the original launch (or is recreated).
            _ = SafeFolderPath.Canonicalize(
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
                executor.Initialize(workspaceRoot: null, grants, provider);
                started = true;
                await orchestrator.RunAsync(run, executor, persona, provider, budget, runCts.Token, resume: true)
                    .ConfigureAwait(false); // resume:true → no re-plan, drains the Pending remainder (D1)
            }
            catch (OperationCanceledException)
            {
                // Cancel during resume before entering the orchestrator: the run was CAS'd to Running by the
                // claim, so re-park it (rather than leave it dangling Running) — it stays resumable.
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
                _inflight.TryRemove(run.Id, out _);
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
    private string? TrySerializeGrantEnvelope(IReadOnlyCollection<string> grants, AgentRunTrigger trigger)
    {
        try
        {
            return SerializeGrantEnvelope(grants, trigger);
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
    internal static string SerializeGrantEnvelope(IReadOnlyCollection<string> grants, AgentRunTrigger trigger)
        => JsonSerializer.Serialize(
            new GrantEnvelope
            {
                V = GrantEnvelopeVersion,
                GrantedWrites = grants.ToList(),
                Trigger = trigger.ToString(),
            },
            GrantEnvelopeJsonOptions);

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
