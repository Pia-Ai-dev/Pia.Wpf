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
    private readonly IExecutingRunStore _executingRuns;
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
        IExecutingRunStore executingRuns,
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
        _executingRuns = executingRuns;
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

            // A2: open the composer bracket for this SingleTurn run. This shape was gated NOWHERE before:
            // ChatSessionManager matched runs by ChatSession.ActiveRunId and RestoreActiveRunAsync only ever
            // attaches RunShape.Planned, so a user opening this chat from history mid-turn could Send — and the
            // single SaveAsync below is a FULL REPLACE with no merge and no bound, so their message was deleted
            // outright. Registered only once the run row exists: the handler-side release keys off RunChanged,
            // which every `run is not null` guard below suppresses for a run-less turn, so a surrogate key
            // could never be cleared and would strand a dead composer. A run-less turn therefore fails OPEN,
            // which is the recoverable direction. Bookkeeping — a fault must never fail the turn (§12.5).
            if (run is { } registeredRun)
            {
                try { _executingRuns.Register(chatId, registeredRun.Id); }
                catch (Exception ex) { _logger.LogWarning(ex, "Executing-run bookkeeping (register) failed for chat {ChatId}", chatId); }
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
                // NO POLICY ARGUMENT, and that is a DECISION, not an oversight (Batch 04 §0.2 + review close).
                // A SingleTurn scheduled job has no plan, no steps and no resume path, so it carries no launch
                // envelope — and this batch deliberately did not give it one. The visible consequence is that
                // AgentRunAutoApproveBuiltInWrites behaves differently for the two kinds of scheduled job: an
                // AgentTask job (ScheduledJobBackgroundService → HeadlessRunLauncher) auto-approves the preset
                // classes, while a Research job lands here and still answers "Denied: 'write_file' is a write
                // action not granted to this background job" for the identical tool. The direction is
                // RESTRICTIVE, so nothing unsafe follows; relaying the setting here would WIDEN an unattended
                // surface, which is not a change to make on the back of a reviewer nit. If a later batch wants
                // parity, the fix is to pass RunAutonomyPolicy.FromSettings(settings) here — with a test — and
                // to say so in the settings copy, which today names agent runs and voice mode only.
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
        finally
        {
            // A2: close the composer bracket on EVERY exit — success, empty response, cancel, fault. Idempotent
            // with ChatSessionManager's release-on-RunChanged, which normally gets there first because
            // CompleteAsync raises the terminal event before this finally runs. Never throws into the turn.
            if (run is { } bracketedRun)
            {
                try { _executingRuns.Release(bracketedRun.Id); }
                catch (Exception ex) { _logger.LogWarning(ex, "Executing-run bookkeeping (release) failed for chat {ChatId}", chatId); }
            }
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
        AgentContextBudget? contextBudget = null,
        RunAutonomyPolicy? policy = null,
        AgentTimelineScope? timeline = null,
        // hermes #9. Non-null ONLY when the caller put emit_step_result in setup.Tools — i.e. an agent step.
        // It is what arms the pre-route interception below, so the background single-turn path (which passes
        // nothing) still treats a hallucinated emit_step_result as the unknown tool it is.
        StepOutcomeStore? outcomeStore = null,
        // hermes #16. Non-null ONLY when the caller owns a run loop that can park it at WaitingForInput — i.e.
        // an agent step. Null (the background single-turn path) resolves CanPark: false and keeps the hard
        // denial exactly as it was.
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

        // The persona rides on the setup (which every caller of this method already builds via
        // PrepareTurn), so no new parameter is needed here — HeadlessTurnExecutor calls this too.
        await foreach (var item in _aiClient.GetChatCompletionWithToolsAsync(
            messages, provider,
            setup.SupportsTools ? setup.Tools : null,
            setup.SupportsTools ? (toolCall, ctx) => HandleToolCallAsync(toolCall, grantedWrites, ctx, policy, timeline, outcomeStore, approvals, userInput, deniedWrites) : null,
            nameof(WindowMode.Assistant), setup.PersonaId,
            cancellationToken: ct, contextBudget: contextBudget))
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
    /// <param name="dispatch">What the tool LOOP knows and this gate cannot derive (T2-14): the 1-based round,
    /// persisted on every row this gate writes. The interactive twin takes the same parameter for the same
    /// reason, and the two must stay in step — a column populated on one surface and NULL on the other is a
    /// silent parity bug (AgentTimelineParityTests holds them together).</param>
    private async Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall, HashSet<string> grantedWrites, ToolDispatchContext dispatch,
        RunAutonomyPolicy? policy = null,
        AgentTimelineScope? timeline = null, StepOutcomeStore? outcomeStore = null,
        ToolApprovalStore? approvals = null, UserInputRequestStore? userInput = null,
        HashSet<string>? deniedWrites = null)
    {
        // emit_step_result (hermes #9): PRE-ROUTE special case, the unattended twin of ChatSession's
        // suggest_agent_mode seam and placed for the same reason — the tool has no plugin and no route, so
        // RouteToolCallAsync would miss it, emit a ToolGateDecision.UnknownTool audit row and hand the model
        // "Unknown tool.". Short-circuiting here also keeps every other tool path byte-for-byte unchanged.
        // Never gated: declaring an outcome writes nothing and touches nothing outside this step's sink.
        if (outcomeStore is not null
            && string.Equals(toolCall.Name, AgentStepTools.EmitStepResultToolName, StringComparison.Ordinal))
        {
            return outcomeStore.Record(toolCall.Arguments);
        }

        // request_user_input: a third pre-route special case, the same shape as emit_step_result above — no
        // plugin, no route. Its interactive twin lives at ChatSession.HandleToolCall; the two must stay in step.
        // The question is user-derived payload — it is never logged here; the store's SensitiveDebug renders it.
        if (userInput is not null
            && string.Equals(toolCall.Name, AgentStepTools.RequestUserInputToolName, StringComparison.Ordinal))
        {
            return userInput.Record(toolCall.Arguments);
        }

        // MCP flows through the same grant gate as a built-in write: the Phase-2 MCP handler returns a
        // deferred PluginToolCall (below), so an ungranted MCP call is denied by the pending-action branch
        // and a granted NON-destructive one executes. No MCP-specific pre-check is needed here anymore.
        // Length only, measured once and reused by every emit arm below (03 §3). Gated on the SINK for the same
        // reason as the interactive twin: the SingleTurn background path passes no scope, and serializing a
        // multi-megabyte argument dictionary to discard the number is pure waste on the most common path.
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
            // ---- hermes #16 CONTAINMENT: a park STOPS the exchange, it does not merely advise it ----
            // The Park arm below answers the model with a string asking it to stop, but a string is not a
            // control-flow construct: AiClientService walks the REMAINING FunctionCallContents of the same round
            // in a sequential foreach and then continues to the next round, so without this guard every call the
            // model makes AFTER the run has decided to park still reached Execute() — including the GRANTED,
            // side-effecting ones, which resolve AutoRun and never look at `approvals` at all.
            //
            // That was an at-most-once violation, not just wasted work. The executor discards this step's whole
            // attempt (no transcript append, no interim persist) and the orchestrator puts the row back to
            // Pending, so the resumed step re-runs from the top with no record of the work — and does it a
            // SECOND time. Pre-#16 the ungranted call was simply refused and the step ran to completion once.
            //
            // The attempt is still recorded in the store (never executed): Park's first-wins rule is what keeps
            // the envelope naming the call that actually stopped the run, and ParkedCalls is what tells the log
            // the model kept going. It returns false here by construction — PendingToolName is already set — so
            // no second audit row is written and the panel never shows a queue of decisions that does not exist.
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

            // 04 D5: ONE resolver, shared with the interactive gate. The destructive-external FLOOR (B2) is
            // evaluated inside Resolve BEFORE any policy or grant branch, so it stays unliftable: there is no
            // user here to confirm an irreversible action against a third-party system, and an MCP tool's name
            // and effect are server-defined, so a grant list authored days earlier (or a server that renamed
            // its tools) is not informed consent. Scoped to external tools ONLY — IsDeleteLike("delete_file")
            // is true for the BUILT-IN file tool, and an explicit grant for a built-in delete is the user's own
            // auditable decision, so it still executes.
            var toolClass = ToolClassifier.Classify(pending.PluginName, IsExternalTool(pending.ToolName));
            // T2-14: the POLICY question, bracketed. There is no human on this surface, so unlike the
            // interactive twin these two are the ONLY pair — every arm below that got an answer uses them.
            // Usually EQUAL (Resolve is a few comparisons, DateTime.UtcNow is ~1 ms), so nothing may assert
            // strict ordering.
            var askedAt = DateTime.UtcNow;
            var verdict = ToolAutonomy.Resolve(new ToolGateInput(
                ToolGateSurface.Unattended, pending.ToolName, toolClass,
                // T2-7b: this is the surface where the server's declaration bites hardest — a declared-
                // destructive external tool hits the FLOOR and is refused outright, with no park, exactly as a
                // delete-NAMED one already was. There is no human here to weigh it against the card.
                ServerDeclaredDestructive: pending.ServerDeclaredDestructive,
                // No allowlist unattended: there is no user to have curated it, and IToolPermissionService is
                // injected nowhere in this file. That is today's behaviour restated, not a regression.
                IsAllowlisted: false,
                // hermes #15: the PROCESS-scoped middle tier. It arrives on the same per-step store CanPark
                // does, and for the same reason there is still no IToolPermissionService here: read ambiently,
                // it would hand a CHILD run authority its parent narrowed away. A null store — every
                // SingleTurn background call — and a store that may not park both answer false.
                HasSessionGrant: approvals?.HasSessionGrant(pending.PluginId, pending.ToolName) == true,
                // Persisted "always allow" grants are an INTERACTIVE concept and have never applied here.
                HasStandingGrant: false,
                IsNamedGrant: grantedWrites.Contains(pending.ToolName),
                // The run-scoped denial list a tool-approval park's Deny wrote into the envelope; the
                // resolver's denial tier sits above the park, so a declined tool is refused with "adapt"
                // instead of re-parking. Null (single-turn path) reads as no denials.
                HasNamedDenial: deniedWrites?.Contains(pending.ToolName) == true,
                // The run's autonomy policy, from the launch envelope (or restored from it on resume). Null
                // for the SingleTurn background path, which has no plan and no policy — today's behaviour.
                Policy: policy,
                // hermes #16: may this run stop and ask a human rather than refuse? The executor answers it
                // once per run (root run + a real step turn) and hands the answer down in the store; a null
                // store — every SingleTurn background call — is false, i.e. the pre-#16 hard denial.
                CanPark: approvals?.CanPark == true));
            var resolvedAt = DateTime.UtcNow;

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

                // hermes #16 THE UNATTENDED APPROVAL PARK. The resolver has decided this call is one a human
                // could legitimately approve, so the run stops and asks instead of denying. Nothing is
                // executed here and nothing is granted here: the store carries the tool NAME out to the
                // executor, the executor abandons the step, and the orchestrator parks the run at
                // WaitingForInput with that name in its pause envelope. If the human presses Continue, the
                // resume adds the name to the run's grants and the step re-runs from the top.
                //
                // The model is told, because it is still mid-exchange and about to be asked for more output:
                // a plain "stop" beats letting it improvise a workaround for a call that is pending approval.
                case ToolGateOutcome.Park:
                    var parked = approvals is not null && approvals.Park(pending.ToolName);
                    _logger.LogInformation(
                        "Background turn parked {ToolName} for human approval (first={First})", pending.ToolName, parked);
                    // Audited only for the call that actually parked the run. A second parked call in the
                    // same exchange changes nothing about the run and would otherwise write a row implying a
                    // second pending decision.
                    if (parked)
                    {
                        // THE ONE ARM WITH A NULL DecidedAt (T2-14). RequestedAt is real — the run genuinely
                        // asked — but nobody has answered yet, and nobody will answer THIS row: the human's
                        // answer arrives later as a resume that re-runs the step from the top and writes a
                        // FRESH GrantedByName row. Back-filling this one would break the write-once model
                        // AgentTimelineEvent's remarks describe, and a `decidedAt: DateTime.UtcNow` here would
                        // claim a decision was made at the instant the run stopped to ask for one.
                        timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                            verdict.Decision, AgentTimelineOutcome.NotExecuted,
                            toolCallId: toolCall.CallId, round: dispatch.Round,
                            requestedAt: askedAt, decidedAt: null,
                            argsChars);
                    }
                    return $"Paused: '{pending.ToolName}' needs a person's approval, and this run has asked for one. "
                           + "It did NOT run. Stop now and produce no further tool calls — the run will be "
                           + "resumed from this step once someone answers.";

                case ToolGateOutcome.Refuse when verdict.Decision == ToolGateDecision.DeniedDestructiveFloor:
                    _logger.LogWarning("Background turn refused granted destructive external tool {ToolName}", pending.ToolName);
                    // The policy DID answer — with a refusal — so both instants are real.
                    timeline?.Emit(ToolGateSurface.Unattended, pending.ToolName, toolClass, pending.PluginId,
                        verdict.Decision, AgentTimelineOutcome.NotExecuted,
                        toolCallId: toolCall.CallId, round: dispatch.Round,
                        requestedAt: askedAt, decidedAt: resolvedAt,
                        argsChars);
                    return $"Denied: '{pending.ToolName}' is a destructive external (MCP) tool and never runs unattended, "
                           + "even when granted. Do not retry.";

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
