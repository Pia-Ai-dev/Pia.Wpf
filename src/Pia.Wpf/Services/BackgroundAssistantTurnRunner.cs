using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IBackgroundAssistantTurnRunner"/>. Reuses the same AI + plugin
/// pipeline as an interactive chat turn (<see cref="IAiClientService.GetChatCompletionWithToolsAsync"/>
/// + <see cref="IPluginService.RouteToolCallAsync"/>) but with no action-card UI: tool
/// decisions are governed by the request's grant set instead of inline confirmation.
/// PII tokenization is applied via <see cref="TokenMapAmbient"/> for parity with
/// interactive turns. Runs entirely off the UI thread.
/// </summary>
public sealed class BackgroundAssistantTurnRunner : IBackgroundAssistantTurnRunner
{
    private readonly IAiClientService _aiClient;
    private readonly IPluginService _pluginService;
    private readonly IAssistantPromptComposer _promptComposer;
    private readonly IPersonaService _personaService;
    private readonly IAssistantChatService _chatService;
    private readonly IChatTitleService _titleService;
    private readonly ISettingsService _settingsService;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly IAgentRunService _runService;
    private readonly ILogger<BackgroundAssistantTurnRunner> _logger;

    public BackgroundAssistantTurnRunner(
        IAiClientService aiClient,
        IPluginService pluginService,
        IAssistantPromptComposer promptComposer,
        IPersonaService personaService,
        IAssistantChatService chatService,
        IChatTitleService titleService,
        ISettingsService settingsService,
        Func<ITokenMapService> tokenMapFactory,
        IAgentRunService runService,
        ILogger<BackgroundAssistantTurnRunner> logger)
    {
        _aiClient = aiClient;
        _pluginService = pluginService;
        _promptComposer = promptComposer;
        _personaService = personaService;
        _chatService = chatService;
        _titleService = titleService;
        _settingsService = settingsService;
        _tokenMapFactory = tokenMapFactory;
        _runService = runService;
        _logger = logger;
    }

    public async Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
    {
        var chatId = Guid.NewGuid();
        AgentRun? run = null;
        try
        {
            // R1: the AgentRuns FK requires its AssistantChats parent row to exist first, and FK
            // enforcement is ON. Persist a minimal stub chat up front so the FK target exists (it is
            // finalized by the full SaveAsync on success, and left in place on empty/error so a Failed
            // run's ChatId still resolves). Only then create the run. The stub save is the FK
            // prerequisite, so — unlike the run bookkeeping below — it is allowed to propagate.
            var stubTime = DateTime.UtcNow;
            await _chatService.SaveAsync(new SyncAssistantChat
            {
                Id = chatId,
                SchemaVersion = 1,
                Title = request.Title,
                CreatedAt = stubTime,
                UpdatedAt = stubTime,
                LastAccessedAt = stubTime,
                WindowMode = WindowMode.Assistant.ToString(),
                ProviderId = request.Provider.Id,
                Messages = [],
            }, ct);

            // Run bookkeeping is best-effort and never fails the turn (§12.5). If creation is
            // swallowed, every later run call is guarded by a null check so the turn proceeds run-less.
            try
            {
                run = await _runService.CreateAsync(new AgentRunCreateRequest(
                    chatId, RunShape.SingleTurn, request.Trigger,
                    request.TriggerRef, request.OwnerDeviceId, Goal: request.Prompt), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run bookkeeping (create) failed for chat {ChatId}", chatId);
            }

            var settings = await _settingsService.GetSettingsAsync();
            var tokenizationEnabled = settings.Privacy.TokenizationEnabled;

            var persona = await _personaService.ResolveActiveAsync(
                WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal);

            // The job's provider takes precedence over the persona's preferred provider
            // (the caller already resolved which provider this job runs on); the persona
            // still contributes the reasoning-effort override, mirroring the interactive path.
            var provider = request.Provider;
            if (persona.ReasoningEffort.HasValue)
            {
                provider = provider.Clone();
                provider.ReasoningEffort = persona.ReasoningEffort.Value;
            }

            // Headless path — no user to click the chip (R7) → never eligible.
            var turnSetup = _promptComposer.PrepareTurn(persona, provider, [], tokenizationEnabled,
                suggestAgentModeEligible: false);

            _logger.LogInformation(
                "Background turn {ChatId}: provider={ProviderId}, supportsTools={SupportsTools}, toolCount={ToolCount}, grantedWrites={GrantedWrites}",
                chatId, provider.Id, turnSetup.SupportsTools, turnSetup.Tools?.Count ?? 0, request.GrantedWriteTools.Count);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, turnSetup.SystemPrompt),
                new(ChatRole.User, request.Prompt),
            };

            var grantedWrites = new HashSet<string>(request.GrantedWriteTools, StringComparer.OrdinalIgnoreCase);

            var tokenMap = _tokenMapFactory();
            var previousAmbient = TokenMapAmbient.Current;
            if (tokenizationEnabled)
            {
                try { await tokenMap.InitializeAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to initialize token map for background turn {ChatId}", chatId); }
                TokenMapAmbient.Current = tokenMap;
            }

            // Set the task ambient around the run so the file tools key per-run state against a real
            // task id (the deferred-from-1.1 fix — headless writes were previously scoped to
            // Guid.Empty). TaskId = run.Id when bookkeeping created the run, else the chatId. §13.6.
            var previousTask = TaskAmbient.Current;
            TaskAmbient.Current = new TaskContext(run?.Id ?? chatId, WorkingSubpath: null, OnFileTouched: null);

            // Accrue per-round usage into the run ledger (best-effort — §12.5), exactly as before.
            Func<UsageDetails, Task>? onUsage = null;
            if (run is { } createdRun)
            {
                onUsage = async u =>
                {
                    try { await _runService.AddUsageAsync(createdRun.Id, stepId: null, u, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (usage) failed for {RunId}", createdRun.Id); }
                };
            }

            ExchangeResult exchange;
            try
            {
                exchange = await RunExchangeAsync(messages, provider, turnSetup, grantedWrites, ct, onUsage);
            }
            finally
            {
                // Restore on the same logical async flow before persisting.
                TokenMapAmbient.Current = previousAmbient;
                TaskAmbient.Current = previousTask;
            }

            var visible = exchange.Visible;
            var thinking = exchange.Thinking;
            var tokens = exchange.Tokens;
            var model = exchange.Model;

            if (string.IsNullOrWhiteSpace(visible))
            {
                _logger.LogWarning("Background turn {ChatId} produced empty content", chatId);
                if (run is not null)
                {
                    try { await _runService.FailAsync(run.Id, "Empty response", ct: ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (fail/empty) failed for {RunId}", run.Id); }
                }
                return new BackgroundTurnResult(chatId, false, "Empty response");
            }

            var now = DateTime.UtcNow;
            // R3: these stable message Ids delimit the run's transcript slice (First/LastMessageId).
            var userMsgId = Guid.NewGuid();
            var assistantMsgId = Guid.NewGuid();
            var chat = new SyncAssistantChat
            {
                Id = chatId,
                SchemaVersion = 1,
                Title = request.Title,
                CreatedAt = now,
                UpdatedAt = now,
                LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(),
                ProviderId = request.Provider.Id,
                Messages =
                [
                    new SyncAssistantChatMessage
                    {
                        Id = userMsgId,
                        Role = "user",
                        Content = request.Prompt,
                        Timestamp = now,
                    },
                    new SyncAssistantChatMessage
                    {
                        Id = assistantMsgId,
                        Role = "assistant",
                        Content = visible,
                        ThinkingContent = string.IsNullOrEmpty(thinking) ? null : thinking,
                        Timestamp = now,
                        Tokens = tokens,
                        ModelName = model,
                        Persona = new SyncMessagePersona { Id = persona.Id, Name = persona.Name, Emoji = persona.Emoji },
                    },
                ],
            };

            // Title precedence: caller-supplied > LLM-generated > derived-from-prompt.
            if (string.IsNullOrWhiteSpace(chat.Title))
            {
                try { chat.Title = await _titleService.GenerateAsync(request.Prompt, visible, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Auto-title failed for background chat {ChatId}", chatId); }
            }
            if (string.IsNullOrWhiteSpace(chat.Title))
                chat.Title = DeriveTitle(request.Prompt);

            await _chatService.SaveAsync(chat, ct);
            _logger.LogInformation("Background turn persisted assistant chat {ChatId} ({MessageCount} messages)",
                chatId, chat.Messages.Count);
            _logger.SensitiveDebug("Background chat {ChatId} title: {Title}", chatId, chat.Title);

            if (run is not null)
            {
                try
                {
                    await _runService.SetRunMessageRangeAsync(run.Id, userMsgId, assistantMsgId, ct);
                    await _runService.CompleteAsync(run.Id, ct: ct);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (complete) failed for {RunId}", run.Id); }
            }

            return new BackgroundTurnResult(chatId, true, null);
        }
        catch (OperationCanceledException)
        {
            if (run is not null)
            {
                try { await _runService.FailAsync(run.Id, null, cancelled: true, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (cancel) failed for {RunId}", run.Id); }
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background assistant turn {ChatId} failed", chatId);
            if (run is not null)
            {
                try { await _runService.FailAsync(run.Id, ex.Message, ct: CancellationToken.None); }
                catch (Exception bk) { _logger.LogWarning(bk, "Run bookkeeping (fail) failed for {RunId}", run.Id); }
            }
            return new BackgroundTurnResult(chatId, false, ex.Message);
        }
    }

    /// <summary>
    /// The reusable single-exchange engine (§13.6): build → <see cref="IAiClientService.GetChatCompletionWithToolsAsync"/>
    /// → post-process (think-tag split + web-citation strip). Run-service-free and ambient-free — the
    /// caller owns the token-map/task ambients and any run bookkeeping. The single-turn
    /// <see cref="RunAsync"/> calls it once; <c>HeadlessTurnExecutor</c> calls it per step, accumulating
    /// messages across steps. <paramref name="onUsage"/> is awaited on each round's <see cref="Finished"/>
    /// (preserves the single-turn per-round ledger accrual); null for step turns (the orchestrator records
    /// the step usage from the returned <see cref="ExchangeResult.Usage"/>).
    /// <para>
    /// <paramref name="contextBudget"/> is relayed to the AI client so the in-step tool loop can be
    /// compacted between rounds. Agent step turns pass one; the background single-turn path
    /// (<see cref="RunAsync"/>) leaves it null and is therefore bit-for-bit unchanged.
    /// </para>
    /// </summary>
    public async Task<ExchangeResult> RunExchangeAsync(
        List<ChatMessage> messages,
        AiProvider provider,
        AssistantTurnSetup setup,
        HashSet<string> grantedWrites,
        CancellationToken ct,
        Func<UsageDetails, Task>? onUsage = null,
        AgentContextBudget? contextBudget = null)
    {
        var textBuffer = new StringBuilder();
        int? tokens = null;
        string? model = null;
        UsageDetails? usage = null;

        await foreach (var item in _aiClient.GetChatCompletionWithToolsAsync(
            messages, provider,
            setup.SupportsTools ? setup.Tools : null,
            setup.SupportsTools ? toolCall => HandleToolCallAsync(toolCall, grantedWrites) : null,
            nameof(WindowMode.Assistant), ct, contextBudget))
        {
            switch (item)
            {
                case TextDelta td:
                    textBuffer.Append(td.Text);
                    break;
                case Finished finished:
                    if (finished.Usage is { } u)
                    {
                        usage = u;
                        var total = (int)((u.InputTokenCount ?? 0) + (u.OutputTokenCount ?? 0));
                        if (total > 0) tokens = total;
                        if (onUsage is not null)
                            await onUsage(u);
                    }
                    model = finished.Model;
                    break;
            }
        }

        var (visible, thinking) = StreamThinkTagParser.Parse(textBuffer.ToString());
        if (setup.WebSearchActive)
        {
            var (cleaned, _) = WebCitationExtractor.Extract(visible);
            visible = cleaned;
        }

        return new ExchangeResult(visible, string.IsNullOrEmpty(thinking) ? null : thinking, usage, model, tokens);
    }

    /// <summary>The post-processed output of one <see cref="RunExchangeAsync"/> call.</summary>
    public sealed record ExchangeResult(string Visible, string? Thinking, UsageDetails? Usage, string? Model, int? Tokens);

    /// <summary>
    /// Headless tool dispatch: reads (tools that return an immediate result) always run;
    /// writes (tools that return a pending action) run only if explicitly granted to this job — with one
    /// FLOOR no grant can lift: a destructive EXTERNAL (MCP) tool never runs unattended (B2).
    /// </summary>
    private async Task<object?> HandleToolCallAsync(FunctionCallContent toolCall, HashSet<string> grantedWrites)
    {
        // MCP flows through the same grant gate as a built-in write: the Phase-2 MCP handler returns a
        // deferred PluginToolCall (below), so an ungranted MCP call is denied by the pending-action branch
        // and a granted NON-destructive one executes. No MCP-specific pre-check is needed here anymore.
        var route = await _pluginService.RouteToolCallAsync(toolCall);
        if (route is null)
        {
            _logger.LogWarning("Background turn: no handler for tool {ToolName}", toolCall.Name);
            return "Unknown tool.";
        }

        var (result, pending) = route.Value;
        if (result is not null)
            return result; // read → always allowed

        if (pending is not null)
        {
            if (grantedWrites.Contains(pending.ToolName))
            {
                // Destructive-external FLOOR (B2): a delete-like EXTERNAL (MCP) tool is refused even when it
                // IS in the grant set. There is no user here to confirm an irreversible action against a
                // third-party system, and an MCP tool's name and effect are server-defined — a grant list
                // authored days earlier (or a server that renamed its tools) cannot be treated as informed
                // consent for that. Scoped to external tools ONLY: IsDeleteLike("delete_file") is true for
                // the BUILT-IN file tool, and an explicit grant for a built-in delete is the user's own
                // auditable decision, so it still executes.
                if (ToolPermissionService.IsDeleteLike(pending.ToolName) && IsExternalTool(pending.ToolName))
                {
                    _logger.LogWarning("Background turn refused granted destructive external tool {ToolName}", pending.ToolName);
                    return $"Denied: '{pending.ToolName}' is a destructive external (MCP) tool and never runs unattended, "
                           + "even when granted. Do not retry.";
                }

                _logger.LogInformation("Background turn executing granted write tool {ToolName}", pending.ToolName);
                return await pending.Execute();
            }

            _logger.LogInformation("Background turn denied ungranted write tool {ToolName}", pending.ToolName);
            return $"Denied: '{pending.ToolName}' is a write action not granted to this background job. Do not retry.";
        }

        return "Tool call handled.";
    }

    /// <summary>
    /// Is this an external/MCP tool? Re-derived from the plugin SERVICE at the gate — the same source the
    /// interactive gate uses — never from a name pattern and never from the pending action, so a renamed or
    /// spoofed tool cannot talk its way out of the destructive-external floor. A derivation fault fails
    /// CLOSED (treat as external): the only consequence is extra friction on a granted built-in delete.
    /// </summary>
    private bool IsExternalTool(string toolName)
    {
        try
        {
            return _pluginService.IsMcpTool(toolName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not derive MCP-ness for tool {ToolName}; treating it as external", toolName);
            return true;
        }
    }

    private static string DeriveTitle(string prompt)
    {
        var collapsed = TextFormatting.CollapseWhitespace(prompt);
        const int max = 40;
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }
}
