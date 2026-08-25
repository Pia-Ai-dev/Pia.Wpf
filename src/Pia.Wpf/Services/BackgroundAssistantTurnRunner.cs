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
/// decisions are governed by the request's grant set, the run's autonomy policy and the user's
/// standing grants instead of inline confirmation.
/// PII tokenization is applied via <see cref="TokenMapAmbient"/> for parity with
/// interactive turns. Runs entirely off the UI thread.
/// </summary>
public sealed class BackgroundAssistantTurnRunner : IBackgroundAssistantTurnRunner
{
    private readonly IAiClientService _aiClient;
    private readonly IPluginService _pluginService;
    private readonly IToolPermissionService _permissions;
    private readonly IAssistantPromptComposer _promptComposer;
    private readonly IPersonaService _personaService;
    private readonly IAssistantChatService _chatService;
    private readonly IChatTitleService _titleService;
    private readonly ISettingsService _settingsService;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly IAgentRunService _runService;
    private readonly IExecutingRunStore _executingRuns;
    private readonly ILogger<BackgroundAssistantTurnRunner> _logger;

    public BackgroundAssistantTurnRunner(
        IAiClientService aiClient,
        IPluginService pluginService,
        IToolPermissionService permissions,
        IAssistantPromptComposer promptComposer,
        IPersonaService personaService,
        IAssistantChatService chatService,
        IChatTitleService titleService,
        ISettingsService settingsService,
        Func<ITokenMapService> tokenMapFactory,
        IAgentRunService runService,
        IExecutingRunStore executingRuns,
        ILogger<BackgroundAssistantTurnRunner> logger)
    {
        _aiClient = aiClient;
        _pluginService = pluginService;
        _permissions = permissions;
        _promptComposer = promptComposer;
        _personaService = personaService;
        _chatService = chatService;
        _titleService = titleService;
        _settingsService = settingsService;
        _tokenMapFactory = tokenMapFactory;
        _runService = runService;
        _executingRuns = executingRuns;
        _logger = logger;
    }

    public async Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
    {
        var chatId = Guid.NewGuid();
        AgentRun? run = null;
        try
        {
            // The AgentRuns FK requires its AssistantChats parent row first, and FK enforcement is ON, so the
            // stub chat is persisted up front and left in place on empty/error (a Failed run's ChatId still
            // resolves). Being the FK prerequisite, this save is allowed to propagate — the run bookkeeping
            // below is not.
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

            // Best-effort and never fails the turn: if creation is swallowed, every later run call is guarded
            // by a null check so the turn proceeds run-less.
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

            // Open the composer bracket: the SaveAsync below is a FULL REPLACE with no merge, so a user who
            // opens this chat from history mid-turn and Sends would otherwise have their message deleted
            // outright. Registered only once the run row exists — the release keys off RunChanged, which every
            // `run is not null` guard suppresses for a run-less turn, so a surrogate key could never be cleared
            // and would strand a dead composer. A run-less turn therefore fails OPEN, the recoverable direction.
            if (run is { } registeredRun)
            {
                try { _executingRuns.Register(chatId, registeredRun.Id); }
                catch (Exception ex) { _logger.LogWarning(ex, "Executing-run bookkeeping (register) failed for chat {ChatId}", chatId); }
            }

            var settings = await _settingsService.GetSettingsAsync();
            var tokenizationEnabled = settings.Privacy.TokenizationEnabled;

            var persona = await RunPinResolver.ResolvePersonaAsync(
                _personaService, request.PersonaId,
                settings.UserOperatingMode ?? UserOperatingMode.Personal, _logger);

            // The caller already resolved which provider this turn runs on, so a pinned persona's preferred
            // provider does not re-enter here; only the effort ladder does, and the request's own pin wins it.
            var provider = RunPinResolver.ApplyEffort(
                request.Provider, request.ReasoningEffort, persona.ReasoningEffort);

            // Headless path — no user to click the chip, so never eligible.
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

            // The file tools key per-run state against this id; without it headless writes all shared
            // Guid.Empty. The run's id when bookkeeping created one, else the chat's.
            var previousTask = TaskAmbient.Current;
            TaskAmbient.Current = new TaskContext(run?.Id ?? chatId, WorkingSubpath: null, OnFileTouched: null, ChatId: chatId);

            // Accrue per-round usage into the run ledger; best-effort, never fails the turn.
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
                // NO POLICY ARGUMENT, deliberately: a SingleTurn scheduled job has no plan, no steps and no
                // resume path, so it carries no launch envelope. The consequence is that
                // AgentRunAutoApproveBuiltInWrites reaches an AgentTask job but not one that lands here, which is
                // the RESTRICTIVE direction — relaying the setting would WIDEN an unattended surface. Parity, if
                // wanted, means passing RunAutonomyPolicy.FromSettings(settings) here and saying so in the
                // settings copy, which today names agent runs and voice mode only.
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
                    try
                    {
                        await _runService.FailAsync(
                            run.Id, AgentStepTools.EmptyResponseFailure, ct: ct,
                            failure: FailureMapper.ForReason(AgentStepTools.EmptyResponseFailure));
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (fail/empty) failed for {RunId}", run.Id); }
                }
                return new BackgroundTurnResult(chatId, false, "Empty response");
            }

            var now = DateTime.UtcNow;
            // These stable Ids delimit the run's transcript slice (First/LastMessageId).
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
                try
                {
                    await _runService.FailAsync(
                        run.Id, ex.Message, ct: CancellationToken.None,
                        failure: FailureMapper.ForException(ex));
                }
                catch (Exception bk) { _logger.LogWarning(bk, "Run bookkeeping (fail) failed for {RunId}", run.Id); }
            }
            return new BackgroundTurnResult(chatId, false, ex.Message);
        }
        finally
        {
            // Close the composer bracket on EVERY exit. Idempotent with ChatSessionManager's
            // release-on-RunChanged, which normally gets there first. Never throws into the turn.
            if (run is { } bracketedRun)
            {
                try { _executingRuns.Release(bracketedRun.Id); }
                catch (Exception ex) { _logger.LogWarning(ex, "Executing-run bookkeeping (release) failed for chat {ChatId}", chatId); }
            }
        }
    }

    /// <summary>
    /// The reusable single-exchange engine: build → <see cref="IAiClientService.GetChatCompletionWithToolsAsync"/>
    /// → post-process. Run-service-free and ambient-free — the caller owns the token-map/task ambients and any
    /// run bookkeeping. <see cref="RunAsync"/> calls it once; <c>HeadlessTurnExecutor</c> calls it per step,
    /// accumulating messages across steps.
    /// </summary>
    /// <param name="onUsage"/>Awaited on each round's <see cref="Finished"/>, preserving the single-turn
    /// per-round ledger accrual; null for step turns, where the orchestrator records the step usage from the
    /// returned <see cref="ExchangeResult.Usage"/>.</param>
    public async Task<ExchangeResult> RunExchangeAsync(
        List<ChatMessage> messages,
        AiProvider provider,
        AssistantTurnSetup setup,
        HashSet<string> grantedWrites,
        CancellationToken ct,
        Func<UsageDetails, Task>? onUsage = null,
        AgentContextBudget? contextBudget = null,
        RunAutonomyPolicy? policy = null,
        AgentTimelineScope? timeline = null,
        // Non-null ONLY when the caller put emit_step_result in setup.Tools — i.e. an agent step. It arms the
        // pre-route interception below, so a hallucinated emit_step_result on the single-turn path (which
        // passes nothing) is still the unknown tool it is.
        StepOutcomeStore? outcomeStore = null,
        // Non-null ONLY when the caller owns a run loop that can park it at WaitingForInput. Null resolves
        // CanPark: false, i.e. a hard denial.
        ToolApprovalStore? approvals = null,
        // Non-null ONLY on a real agent step turn, exactly like outcomeStore — it is what arms the pre-route
        // interception of request_user_input below, so the background single-turn path (which passes nothing)
        // still treats a hallucinated request_user_input as the unknown tool it is.
        UserInputRequestStore? userInput = null,
        // The run's envelope denial list (a tool-approval park's Deny, persisted on resume). Null — the
        // single-turn path and every pre-denial caller — resolves HasNamedDenial: false at the gate.
        HashSet<string>? deniedWrites = null)
    {
        var textBuffer = new StringBuilder();
        int? tokens = null;
        string? model = null;
        UsageDetails? usage = null;
        // A tool round just completed; the next TextDelta starts a fresh model turn built on the
        // tool result, not a continuation of whatever is already in textBuffer.
        var pendingRoundBreak = false;

        // The persona rides on the setup (which every caller of this method already builds via
        // PrepareTurn), so no new parameter is needed here — HeadlessTurnExecutor calls this too.
        await foreach (var item in _aiClient.GetChatCompletionWithToolsAsync(
            messages, provider,
            setup.SupportsTools ? setup.Tools : null,
            setup.SupportsTools ? (toolCall, ctx) => HandleToolCallAsync(toolCall, grantedWrites, ctx, policy, timeline, outcomeStore, approvals, userInput, deniedWrites) : null,
            nameof(WindowMode.Assistant), setup.PersonaId, setup.ModelType,
            cancellationToken: ct, contextBudget: contextBudget))
        {
            switch (item)
            {
                case TextDelta td:
                    if (pendingRoundBreak && textBuffer.Length > 0)
                        textBuffer.Append("\n\n");
                    pendingRoundBreak = false;
                    textBuffer.Append(td.Text);
                    break;
                case ToolRoundCompleted:
                    pendingRoundBreak = true;
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
    /// Headless tool dispatch: reads (tools that return an immediate result) always run; writes (tools that
    /// return a pending action) run only if explicitly granted to this job.
    /// </summary>
    /// <param name="dispatch">What the tool LOOP knows and this gate cannot derive: the 1-based round,
    /// persisted on every row this gate writes. The interactive twin takes it for the same reason — a column
    /// populated on one surface and NULL on the other is a silent parity bug.</param>
    private async Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall, HashSet<string> grantedWrites, ToolDispatchContext dispatch,
        RunAutonomyPolicy? policy = null,
        AgentTimelineScope? timeline = null, StepOutcomeStore? outcomeStore = null,
        ToolApprovalStore? approvals = null, UserInputRequestStore? userInput = null,
        HashSet<string>? deniedWrites = null)
    {
        // PRE-ROUTE: the tool has no plugin and no route, so routing would miss it, write an UnknownTool
        // audit row and answer "Unknown tool.". Never gated — declaring an outcome writes nothing outside
        // this step's sink.
        if (outcomeStore is not null
            && string.Equals(toolCall.Name, AgentStepTools.EmitStepResultToolName, StringComparison.Ordinal))
        {
            return outcomeStore.Record(toolCall.Arguments);
        }

        // Same pre-route shape as emit_step_result. Its interactive twin lives at ChatSession.HandleToolCall
        // and the two must stay in step. The question is user payload and is never logged here.
        if (userInput is not null
            && string.Equals(toolCall.Name, AgentStepTools.RequestUserInputToolName, StringComparison.Ordinal))
        {
            return userInput.Record(toolCall.Arguments);
        }

        // Length only, measured once and reused by every emit arm below. Gated on the SINK: the single-turn
        // path passes no scope, and serializing a multi-megabyte argument dictionary to discard the number
        // would be pure waste on the most common path.
        var argsChars = timeline is null ? null : AgentTimelineScope.MeasureArgs(toolCall.Arguments);

        var route = await _pluginService.RouteToolCallAsync(toolCall);
        if (route is null)
        {
            // Parity with the interactive gate: the model-authored name is sanitized for the release-visible
            // Warning line AND for the persisted row, and survives raw only under SensitiveDebug.
            var loggedName = AgentTimelineScope.SanitizeUnroutedToolName(toolCall.Name);
            _logger.LogWarning("Background turn: no handler for tool {ToolName}", loggedName);
            _logger.SensitiveDebug("Unrouted tool call name: {ToolName}", toolCall.Name);
            // Parity with the interactive unrouted arm, including the NULL/NULL instants: no gate was consulted,
            // so there was no question to time. The provider-authored CallId is still recorded.
            timeline?.Emit(ToolGateSurface.Unattended, loggedName, ToolClass.Unknown, pluginId: null,
                ToolGateDecision.UnknownTool, AgentTimelineOutcome.NotExecuted,
                toolCallId: toolCall.CallId, round: dispatch.Round, requestedAt: null, decidedAt: null,
                argsChars);
            return "Unknown tool.";
        }

        var (result, pending) = route.Value;
        if (result is not null)
            return result; // read → always allowed

        if (pending is not null)
        {
            // A park STOPS the exchange rather than merely advising it. The Park arm's answer is only a string,
            // and AiClientService walks the round's REMAINING calls and then continues to the next round — so
            // without this guard a granted, side-effecting call made after the run decided to park still
            // executed, and the step then re-ran from the top on resume and did it a SECOND time.
            // The attempt is recorded but never executed; Park returns false here by construction, so no second
            // audit row is written and the panel never shows a queue of decisions that does not exist.
            if (approvals?.PendingToolName is { } parkedFor)
            {
                approvals.Park(pending.ToolName);
                _logger.LogInformation(
                    "Background turn withheld {ToolName}: the run is already parked on {ParkedToolName}",
                    pending.ToolName, parkedFor);
                return $"Not run: this run is already waiting for a person's approval of '{parkedFor}', so "
                       + $"'{pending.ToolName}' was NOT executed either — nothing more happens in this step. "
                       + "Stop now and produce no further tool calls; the run will be resumed from this step "
                       + "once someone answers.";
            }

            // An ask stops the exchange too, for the same at-most-once reason as the park above: a step that
            // asked is abandoned and re-runs from the top on resume, so a later side-effecting call in the same
            // round would otherwise execute twice for one planned step.
            if (userInput?.Question is not null)
            {
                _logger.LogInformation(
                    "Background turn withheld {ToolName}: the run is stopping to ask the user", pending.ToolName);
                return $"Not run: this run is stopping to ask the person your question, so '{pending.ToolName}' "
                       + "was NOT executed — nothing more happens in this step. Stop now and produce no further "
                       + "tool calls; this step runs again from the beginning once someone answers.";
            }

            var gate = ResolveToolGate(pending, grantedWrites, policy, approvals, deniedWrites);
            return await DispatchGateVerdictAsync(
                pending, toolCall, dispatch, gate, timeline, approvals, argsChars);
        }

        return "Tool call handled.";
    }

    /// <summary>What the unattended gate decides. No allowlist and no card phase, unlike the interactive twin —
    /// see <see cref="ResolveToolGate"/>.</summary>
    private sealed record UnattendedGateResolution(
        ToolClass ToolClass, DateTime AskedAt, ToolGateVerdict Verdict, DateTime ResolvedAt);

    /// <summary>
    /// ONE resolver, shared with the interactive gate. A tool named in this run's grant list executes here —
    /// including a destructive one — because that list is the user's own auditable decision.
    /// </summary>
    private UnattendedGateResolution ResolveToolGate(
        PluginToolCall pending, HashSet<string> grantedWrites, RunAutonomyPolicy? policy,
        ToolApprovalStore? approvals, HashSet<string>? deniedWrites)
    {
        var toolClass = ToolClassifier.Classify(pending.PluginName, IsExternalTool(pending.ToolName));
        // The policy question, bracketed. No human on this surface, so unlike the interactive twin these are the
        // ONLY pair — every answered arm uses them. Usually EQUAL, so nothing may assert strict ordering.
        var askedAt = DateTime.UtcNow;
        var verdict = ToolAutonomy.Resolve(new ToolGateInput(
            ToolGateSurface.Unattended, pending.ToolName, toolClass,
            // The server's own declaration, which widens delete-likeness: the autonomy policy will not cover
            // the tool and the park will not ask about it. A grant list that names it still runs it.
            ServerDeclaredDestructive: pending.ServerDeclaredDestructive,
            // No allowlist unattended: the curated set authorizes VOICE alone, and a hardcoded false keeps this
            // surface off it even if that pin in Resolve ever moved.
            IsAllowlisted: false,
            // The process-scoped middle tier, arriving on the per-step store rather than read ambiently:
            // ambiently it would hand a CHILD run authority its parent narrowed away. A null store answers false.
            HasSessionGrant: approvals?.HasSessionGrant(pending.PluginId, pending.ToolName) == true,
            // Ambient, unlike the session tier above, and the same lookup the interactive gate makes: a standing
            // grant sits in no run's envelope, so NarrowForChild has nothing to narrow and a child run reads the
            // identical persisted fact its parent does.
            HasStandingGrant: _permissions.IsGranted(pending.PluginId, pending.ToolName),
            IsNamedGrant: grantedWrites.Contains(pending.ToolName),
            // The run-scoped denial list a tool-approval park's Deny wrote into the envelope; the
            // resolver's denial tier sits above the park, so a declined tool is refused with "adapt"
            // instead of re-parking. Null (single-turn path) reads as no denials.
            HasNamedDenial: deniedWrites?.Contains(pending.ToolName) == true,
            // The run's autonomy policy, from the launch envelope (or restored from it on resume). Null
            // for the SingleTurn background path, which has no plan and no policy — today's behaviour.
            Policy: policy,
            // May this run stop and ask a human rather than refuse? The executor answers it once per run and
            // hands the answer down in the store; a null store is false, i.e. a hard denial.
            CanPark: approvals?.CanPark == true));

        return new UnattendedGateResolution(toolClass, askedAt, verdict, DateTime.UtcNow);
    }

    /// <summary>Act on the verdict. No card arm: there is no interactive surface here, so the outcomes are
    /// execute, park, refuse-by-denial, and the ungranted-write denial.</summary>
    private async Task<object?> DispatchGateVerdictAsync(
        PluginToolCall pending, FunctionCallContent toolCall, ToolDispatchContext dispatch,
        UnattendedGateResolution gate, AgentTimelineScope? timeline, ToolApprovalStore? approvals, int? argsChars)
    {
        var toolClass = gate.ToolClass;
        var verdict = gate.Verdict;
        var askedAt = gate.AskedAt;
        var resolvedAt = gate.ResolvedAt;

        switch (verdict.Outcome)
        {
            case ToolGateOutcome.AutoRun:
                _logger.LogInformation("Background turn executing {ToolName} ({Decision})",
                    pending.ToolName, verdict.Decision);
                // Only Execute() is bracketed for the timeline; a fault anywhere else is not this tool's
                // outcome. The rethrow keeps a throwing tool's effect on the turn unchanged.
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                object? executed;
                try
                {
                    executed = await pending.Execute();
                }
                catch
                {
                    timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                        verdict.Decision, AgentTimelineOutcome.Error,
                        toolCallId: toolCall.CallId, round: dispatch.Round,
                        requestedAt: askedAt, decidedAt: resolvedAt,
                        argsChars, resultChars: null,
                        durationMs: AgentTimelineScope.ElapsedMs(startedAt));
                    throw;
                }

                timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                    verdict.Decision, AgentTimelineOutcome.Ok,
                    toolCallId: toolCall.CallId, round: dispatch.Round,
                    requestedAt: askedAt, decidedAt: resolvedAt,
                    argsChars,
                    resultChars: (executed as string)?.Length,
                    durationMs: AgentTimelineScope.ElapsedMs(startedAt));
                return executed;

            // The resolver decided a human could legitimately approve this call, so the run stops and asks
            // instead of denying. Nothing is executed or granted here: the store carries the tool NAME out to
            // the executor, which abandons the step, and the orchestrator parks the run naming it. The model is
            // told because it is still mid-exchange — a plain "stop" beats letting it improvise a workaround.
            case ToolGateOutcome.Park:
                var parked = approvals is not null && approvals.Park(pending.ToolName);
                _logger.LogInformation(
                    "Background turn parked {ToolName} for human approval (first={First})", pending.ToolName, parked);
                // Audited only for the call that actually parked the run. A second parked call in the
                // same exchange changes nothing about the run and would otherwise write a row implying a
                // second pending decision.
                if (parked)
                {
                    // THE ONE ARM WITH A NULL DecidedAt. RequestedAt is real — the run genuinely asked — but
                    // nobody will answer THIS row: the human's answer arrives later as a resume that re-runs the
                    // step and writes a fresh row. A timestamp here would claim a decision was made at the
                    // instant the run stopped to ask for one.
                    timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                        verdict.Decision, AgentTimelineOutcome.NotExecuted,
                        toolCallId: toolCall.CallId, round: dispatch.Round,
                        requestedAt: askedAt, decidedAt: null,
                        argsChars);
                }
                return $"Paused: '{pending.ToolName}' needs a person's approval, and this run has asked for one. "
                       + "It did NOT run. Stop now and produce no further tool calls — the run will be "
                       + "resumed from this step once someone answers.";

            // The run asked to use this tool, a person DECLINED it, and the resume carried that denial in
            // the envelope. The step re-runs from the top, so the model hears the answer and adapts — the
            // denial tier in Resolve refuses instead of parking, or this run would re-ask a settled question.
            case ToolGateOutcome.Refuse when verdict.Decision == ToolGateDecision.DeniedForRun:
                _logger.LogInformation("Background turn refused tool {ToolName} the user declined for this run", pending.ToolName);
                timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                    verdict.Decision, AgentTimelineOutcome.NotExecuted,
                    toolCallId: toolCall.CallId, round: dispatch.Round,
                    requestedAt: askedAt, decidedAt: resolvedAt,
                    argsChars);
                return $"Denied: the person declined the use of '{pending.ToolName}' for this run. Do not retry it; "
                       + "finish the step without it, or explain in your reply why the step is impossible without it.";

            default:
                _logger.LogInformation("Background turn denied ungranted write tool {ToolName}", pending.ToolName);
                // verdict.Decision rather than a literal, so the persisted reason is always the one the
                // shared resolver actually returned (DeniedNotGranted on this surface today). A denial is
                // an ANSWER, so both instants are real here too — only the park leaves DecidedAt null.
                timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                    verdict.Decision, AgentTimelineOutcome.NotExecuted,
                    toolCallId: toolCall.CallId, round: dispatch.Round,
                    requestedAt: askedAt, decidedAt: resolvedAt,
                    argsChars);
                return $"Denied: '{pending.ToolName}' is a write action not granted to this background job. Do not retry.";
        }
    }

    /// <summary>
    /// Is this an external/MCP tool? Re-derived from the plugin SERVICE at the gate — the same source the
    /// interactive gate uses — never from a name pattern and never from the pending action.
    /// </summary>
    /// <remarks>A fault answers <c>true</c>, the restrictive direction here: External is what the session
    /// tier's unattended exclusion and the park both refuse, so a fault can only cost the run a capability.
    /// </remarks>
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
