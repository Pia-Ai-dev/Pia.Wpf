using System.Collections.Concurrent;
using System.IO;
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
public sealed class HeadlessRunLauncher : IHeadlessRunLauncher, IDisposable
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

        var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, req.Trigger, req.TriggerRef, req.OwnerDeviceId, Goal: req.Goal), ct)
            .ConfigureAwait(false);

        // Isolated per-run workspace (§17.2/G-1). Canonicalize so a link in the path is not a hole. The run row
        // already exists (Planning), so a workspace-setup failure here must settle it — otherwise the run dangles
        // non-terminal until the next startup sweep (G-4).
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

        var grants = req.GrantedWrites ?? new[] { "write_file", "delete_file" };
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
                executor.Initialize(runRoot, grants, provider);
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
