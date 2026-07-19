using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Off-thread act-step executor (§13.1/§B.5). Wraps the refactored
/// <see cref="BackgroundAssistantTurnRunner.RunExchangeAsync"/> exchange engine, accumulating
/// messages across steps and persisting the chat once at <see cref="EndRunAsync"/> (title
/// precedence unchanged). Created in a fresh DI scope per run. No streaming, no action-card UI —
/// writes run only if granted. Sets <c>TaskAmbient.Current</c> with the run-stable TaskId for the
/// whole run so file tools key per-run state (§16 R9); the file-chip sink is null (headless has no
/// message UI — TaskId correctness is the point).
/// </summary>
public sealed class HeadlessTurnExecutor : IAgentTurnExecutor
{
    private readonly BackgroundAssistantTurnRunner _engine;
    private readonly IAssistantChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly IAssistantPromptComposer _promptComposer;
    private readonly IChatTitleService _titleService;
    private readonly IPluginService _pluginService;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly ILogger<HeadlessTurnExecutor> _logger;

    // Per-run accumulating state.
    private readonly List<ChatMessage> _messages = new();
    private readonly List<SyncAssistantChatMessage> _persisted = new();
    private readonly HashSet<string> _grantedWrites = new(StringComparer.OrdinalIgnoreCase);
    private AssistantTurnSetup _setup = default!;
    private Persona _persona = default!;
    private AiProvider _provider = default!;
    private ITokenMapService? _tokenMap;
    private bool _tokenizationEnabled;
    private Guid _chatId;
    private Guid _runId;

    // Seeded by the launcher via Initialize before the orchestrator runs (§17.3).
    private string? _workspaceRoot;
    private AiProvider? _providerOverride;

    public HeadlessTurnExecutor(
        BackgroundAssistantTurnRunner engine,
        IAssistantChatService chatService,
        ISettingsService settingsService,
        IPersonaService personaService,
        IProviderService providerService,
        IAssistantPromptComposer promptComposer,
        IChatTitleService titleService,
        IPluginService pluginService,
        Func<ITokenMapService> tokenMapFactory,
        ILogger<HeadlessTurnExecutor> logger)
    {
        _engine = engine;
        _chatService = chatService;
        _settingsService = settingsService;
        _personaService = personaService;
        _providerService = providerService;
        _promptComposer = promptComposer;
        _titleService = titleService;
        _pluginService = pluginService;
        _tokenMapFactory = tokenMapFactory;
        _logger = logger;
    }

    /// <summary>
    /// Seed the per-run workspace root, granted write tools, and an optional provider override
    /// (the launcher's resolved provider, kept in lock-step with the orchestrator's planner so the two
    /// never diverge). Called from the launcher's fresh DI scope BEFORE <c>orchestrator.RunAsync</c>.
    /// </summary>
    public void Initialize(string workspaceRoot, IReadOnlyCollection<string> grantedWrites, AiProvider? providerOverride = null)
    {
        _workspaceRoot = workspaceRoot;
        _providerOverride = providerOverride;
        _grantedWrites.Clear();
        foreach (var w in grantedWrites)
            _grantedWrites.Add(w);
    }

    public async Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct)
    {
        _chatId = run.ChatId;

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        _tokenizationEnabled = settings.Privacy.TokenizationEnabled;

        _persona = await _personaService.ResolveActiveAsync(
            WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal).ConfigureAwait(false);

        // Prefer the launcher-resolved override so the executor and the orchestrator's planner run on
        // the SAME provider (honors a scheduled job's ProviderId); fall back to persona-preferred/default.
        var provider = _providerOverride;
        if (provider is null)
        {
            provider = _persona.PreferredProviderId.HasValue
                ? await _providerService.GetProviderAsync(_persona.PreferredProviderId.Value).ConfigureAwait(false)
                : null;
            provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant).ConfigureAwait(false);
            if (provider is null)
                throw new InvalidOperationException("No provider configured for a headless agent run.");
            if (_persona.ReasoningEffort.HasValue)
            {
                provider = provider.Clone();
                provider.ReasoningEffort = _persona.ReasoningEffort.Value;
            }
        }
        _provider = provider;

        // Headless path — no user to click the chip (R7) → never eligible.
        _setup = _promptComposer.PrepareTurn(_persona, _provider, [], _tokenizationEnabled,
            suggestAgentModeEligible: false);

        // G-2: MCP tools return an immediate result and so bypass the unattended write-gate. Strip them
        // from the headless tool list (capability removal); the gate in BackgroundAssistantTurnRunner
        // denies any that slip through. MCP re-enablement for unattended runs is Phase 2 (§17.4).
        if (_setup.Tools is { Count: > 0 })
        {
            var filtered = _setup.Tools.Where(t => !_pluginService.IsMcpTool(t.Name)).ToList();
            if (filtered.Count != _setup.Tools.Count)
                _setup = _setup with { Tools = filtered };
        }

        // Initialize the run's token map. NOTE: the ambients are set PER STEP (in RunExchangeStepAsync),
        // not here — an AsyncLocal set after an await inside BeginRunAsync would NOT propagate into the
        // separately-awaited ExecuteStepAsync (ExecutionContext flows down, not back out), so a run-scoped
        // bracket here would leave the exchange with no ambient. Setting it around each exchange keeps the
        // run-stable TaskId live where it is read (§16 R9).
        _runId = run.Id;
        _tokenMap = _tokenMapFactory();
        if (_tokenizationEnabled)
        {
            try { await _tokenMap.InitializeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to initialize token map for headless run {RunId}", run.Id); }
        }

        // Seed the accumulating transcript: system + the goal as the opening user message.
        _messages.Clear();
        _persisted.Clear();
        _messages.Add(new ChatMessage(ChatRole.System, _setup.SystemPrompt));

        var goalMsgId = Guid.NewGuid();
        _messages.Add(new ChatMessage(ChatRole.User, ctx.Goal));
        _persisted.Add(new SyncAssistantChatMessage
        {
            Id = goalMsgId,
            Role = "user",
            Content = ctx.Goal,
            Timestamp = DateTime.UtcNow,
        });
    }

    public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct) =>
        RunExchangeStepAsync(BuildInstruction(step.Ordinal, step.Intent ?? string.Empty, step.ExpectedArtifact), ct);

    public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        RunExchangeStepAsync(ctx.Goal, ct);

    private async Task<StepTurnResult> RunExchangeStepAsync(string instruction, CancellationToken ct)
    {
        // Append the EPHEMERAL step instruction to a COPY — the accumulating _messages keeps the
        // clean transcript (system + goal + one assistant reply per step) — §13.7.
        var exchangeMessages = new List<ChatMessage>(_messages)
        {
            new(ChatRole.User, instruction),
        };

        // Per-step ambient bracket (§16 R9): run-stable TaskId, no file-chip sink (headless has no UI).
        // Set here — not in BeginRunAsync — so the AsyncLocal is live inside THIS exchange's flow.
        var previousAmbient = TokenMapAmbient.Current;
        var previousTask = TaskAmbient.Current;
        if (_tokenizationEnabled)
            TokenMapAmbient.Current = _tokenMap;
        TaskAmbient.Current = new TaskContext(_runId, WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _workspaceRoot);

        BackgroundAssistantTurnRunner.ExchangeResult exchange;
        try
        {
            exchange = await _engine.RunExchangeAsync(exchangeMessages, _provider, _setup, _grantedWrites, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StepTurnResult(false, true, "cancelled", string.Empty, null, Guid.Empty, Guid.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless step exchange failed for chat {ChatId}", _chatId);
            return new StepTurnResult(false, false, ex.Message, string.Empty, null, Guid.Empty, Guid.Empty);
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
            TaskAmbient.Current = previousTask;
        }

        var succeeded = !string.IsNullOrWhiteSpace(exchange.Visible);
        var assistantMsgId = Guid.NewGuid();

        // The visible reply IS persisted and IS carried forward as context for later steps.
        _messages.Add(new ChatMessage(ChatRole.Assistant, exchange.Visible));
        _persisted.Add(new SyncAssistantChatMessage
        {
            Id = assistantMsgId,
            Role = "assistant",
            Content = exchange.Visible,
            ThinkingContent = exchange.Thinking,
            Timestamp = DateTime.UtcNow,
            Tokens = exchange.Tokens,
            ModelName = exchange.Model,
            Persona = new SyncMessagePersona { Id = _persona.Id, Name = _persona.Name, Emoji = _persona.Emoji },
        });

        return new StepTurnResult(
            Succeeded: succeeded,
            Cancelled: false,
            Error: succeeded ? null : "Empty response",
            VisibleText: exchange.Visible,
            Usage: exchange.Usage,
            FirstMessageId: assistantMsgId,
            LastMessageId: assistantMsgId);
    }

    public async Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
    {
        try
        {
            // Persist the accumulated chat once (title precedence: LLM-generated > derived-from-goal).
            var now = DateTime.UtcNow;
            string? title = null;
            var firstAssistant = _persisted.FirstOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
            if (firstAssistant is not null)
            {
                try { title = await _titleService.GenerateAsync(ctx.Goal, firstAssistant.Content, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Auto-title failed for headless run {RunId}", run.Id); }
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                var collapsed = TextFormatting.CollapseWhitespace(ctx.Goal);
                title = collapsed.Length <= 40 ? collapsed : collapsed[..40].TrimEnd() + "…";
            }

            var chat = new SyncAssistantChat
            {
                Id = _chatId,
                SchemaVersion = 1,
                Title = title,
                CreatedAt = run.CreatedAt == default ? now : run.CreatedAt,
                UpdatedAt = now,
                LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(),
                ProviderId = _provider.Id,
                Messages = [.. _persisted],
            };

            await _chatService.SaveAsync(chat, ct).ConfigureAwait(false);
            _logger.LogInformation("Headless run {RunId} persisted chat {ChatId} ({MessageCount} messages)",
                run.Id, _chatId, chat.Messages.Count);
            _logger.SensitiveDebug("Headless run {RunId} chat title: {Title}", run.Id, title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist headless run {RunId} chat {ChatId}", run.Id, _chatId);
        }
        // Ambients are set + restored per step (RunExchangeStepAsync); nothing to restore here.
    }

    private static string BuildInstruction(int ordinal, string intent, string? expectedArtifact)
    {
        var instruction = $"Execute step {ordinal + 1}: {intent}.";
        if (!string.IsNullOrEmpty(expectedArtifact))
            instruction += $" Expected: {expectedArtifact}";
        return instruction;
    }
}
