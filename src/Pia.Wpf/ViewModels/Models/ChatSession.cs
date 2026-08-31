using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// Owns one conversation's runtime state — its own message list, its own
/// <see cref="CancellationTokenSource"/>, its own token map, and its
/// <see cref="ChatState"/> — and hosts the relocated assistant run loop
/// (<see cref="RunTurnAsync"/>). It is a plain runtime model (not an
/// <c>ObservableObject</c> view model); UI side-effects are surfaced as events
/// the manager / active view model handle.
///
/// Threading: <see cref="RunTurnAsync"/> is UI-thread-affine — it is started on
/// the UI dispatcher and never uses <c>Task.Run</c> or
/// <c>ConfigureAwait(false)</c>, so every continuation (streaming writes,
/// collection mutations) resumes on the captured UI <c>SynchronizationContext</c>.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly IAiClientService _aiClientService;
    private readonly IPluginService _pluginService;
    private readonly IActionCardBuilder _actionCardBuilder;
    private readonly IToolPermissionService _permissions;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger _logger;

    /// <summary>
    /// Synchronous "is this session the manager's active one?" probe, used inside the
    /// turn's <c>finally</c> to decide Completed (background) vs Idle (active) without
    /// an <c>await</c> — so it serializes against the manager's SetActive under the
    /// single-threaded UI dispatcher (closes the finalize-vs-switch race).
    /// </summary>
    private readonly Func<ChatSession, bool> _isActive;

    /// <summary>This session's own PII token map — durable handle for in-turn and out-of-turn token work.</summary>
    public ITokenMapService TokenMap { get; }

    public Guid? Id { get; internal set; }
    public DateTime CreatedAt { get; internal set; }
    public Guid? ProviderId { get; internal set; }

    /// <summary>
    /// Per-chat working directory, RELATIVE to the assistant-files sandbox root
    /// (forward slashes); null/empty = sandbox root. Mirrors <see cref="ProviderId"/>
    /// as a per-chat field; set via <see cref="SetWorkingDirectory"/>.
    /// </summary>
    public string? WorkingDirectory { get; internal set; }

    public string? Title { get; internal set; }
    public ObservableCollection<AssistantMessage> Messages { get; } = new();
    public ChatState State { get; private set; } = ChatState.Idle;

    /// <summary>True while a turn is in flight — Running or WaitingForTool.</summary>
    public bool IsStreaming => State is ChatState.Running or ChatState.WaitingForTool;

    internal CancellationTokenSource? Cts { get; private set; }
    internal bool AutoTitleApplied { get; set; }

    /// <summary>
    /// Manager-owned LRU stamp — higher means more recently made active. The reaper
    /// (<see cref="ChatSessionManager.ReapStaleSessions"/>) orders sessions by this to
    /// keep the most-recently-active ones. Set only by
    /// <see cref="ChatSessionManager.SetActive"/>; not part of any persisted or public
    /// contract.
    /// </summary>
    internal long LastActivatedSequence { get; set; }

    private bool _disposed;

    /// <summary>
    /// The sink <see cref="HandleToolCall"/> writes an <c>emit_step_result</c> declaration into,
    /// or null when no step turn is in flight. Set at the top of <see cref="RunStepTurnAsync"/> and cleared
    /// in its <c>finally</c>; the verdict is read from the METHOD-LOCAL sink afterwards, not from this field,
    /// so the clear cannot lose the claim. Non-null gates the interception, so it must be back to null before
    /// an ordinary chat turn runs or a hallucinated <c>emit_step_result</c> there would be swallowed instead of
    /// answered "Unknown tool.".
    /// </summary>
    private StepOutcomeStore? _stepOutcomeStore;

    /// <summary>The interactive twin of <see cref="_stepOutcomeStore"/> for <c>request_user_input</c>; non-null is the gate on the pre-route interception.</summary>
    private UserInputRequestStore? _userInputRequest;

    /// <summary>
    /// Each step's tool call/result messages, keyed by the <see cref="AssistantMessage.Id"/> of the reply they
    /// belong to. The live half of cross-step tool context: deliberately NOT in <see cref="Messages"/>, which
    /// is rendered and persisted, and read only by <see cref="BuildStepChatMessagesAsync"/>.
    /// </summary>
    private readonly Dictionary<Guid, List<ChatMessage>> _stepToolExchanges = new();

    /// <summary>Raised on every real state transition (no-op on unchanged value).</summary>
    public event EventHandler<ChatStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a turn completes (any terminal state) — active VM persists / followups / TTS.</summary>
    public event EventHandler<TurnCompletedEventArgs>? TurnCompleted;

    /// <summary>Raised when an accepted write-action succeeded — active VM shows a snackbar.</summary>
    public event EventHandler<ToolSucceededEventArgs>? ToolSucceeded;

    /// <summary>Raised on a handled error (not cancellation) — active VM shows a snackbar / restores composer.</summary>
    public event EventHandler<RunFailedEventArgs>? RunFailed;

    /// <summary>
    /// Raised when an IN-FLIGHT (non-terminal) run wants the transcript so far made durable — the manager
    /// answers it with the same <c>PersistAsync</c> the terminal path uses. Deliberately NOT
    /// <see cref="TurnCompleted"/>: a mid-run step is not a finished turn, so raising that instead would
    /// settle terminal state, fire follow-ups/TTS and present a parked run as complete (guardrail 5).
    /// The single-turn <see cref="RunTurnAsync"/> path never raises this — its terminal
    /// <see cref="TurnCompleted"/> already persists, and its ordering must stay byte-stable.
    /// </summary>
    internal event EventHandler? PersistRequested;

    public ChatSession(
        ITokenMapService tokenMap,
        IAiClientService aiClientService,
        IPluginService pluginService,
        IActionCardBuilder actionCardBuilder,
        IToolPermissionService permissions,
        ILocalizationService localizationService,
        ILogger logger,
        Func<ChatSession, bool> isActive)
    {
        TokenMap = tokenMap;
        _aiClientService = aiClientService;
        _pluginService = pluginService;
        _actionCardBuilder = actionCardBuilder;
        _permissions = permissions;
        _localizationService = localizationService;
        _logger = logger;
        _isActive = isActive;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>Sets identity/metadata once the chat is first persisted (or hydrated from the store).</summary>
    internal void SetIdentity(Guid id, DateTime createdAt, Guid? providerId, string? title, bool autoTitleApplied)
    {
        Id = id;
        CreatedAt = createdAt;
        ProviderId = providerId;
        Title = title;
        AutoTitleApplied = autoTitleApplied;
    }

    internal void SetTitle(string? title) => Title = title;

    internal void SetProviderId(Guid? providerId) => ProviderId = providerId;

    /// <summary>
    /// The live (or most-recently-selected) Planned <see cref="Pia.Models.AgentRun"/> for this chat,
    /// or null when the chat has no run to surface. Set by the manager on the UI thread when a
    /// Planned turn starts; the active VM watches <see cref="ActiveRunChanged"/> to embed the run-progress panel.
    /// </summary>
    public Guid? ActiveRunId { get; private set; }

    /// <summary>Raised when <see cref="ActiveRunId"/> changes (UI thread — the Planned branch runs on it).</summary>
    public event EventHandler<Guid?>? ActiveRunChanged;

    /// <summary>Sets the active run id and notifies (no-op when unchanged).</summary>
    public void SetActiveRun(Guid? runId)
    {
        if (ActiveRunId == runId)
            return;
        ActiveRunId = runId;
        ActiveRunChanged?.Invoke(this, runId);
    }

    /// <summary>
    /// True while a run attached to this chat is executing under an executor this session does NOT own —
    /// i.e. headlessly, on a run pool thread (W2). That executor is a second full-chat writer, so this
    /// session must not write: a live turn's full replace would delete the rows the run's steps produced, and
    /// the run's own model context never sees the user's message anyway, so the resulting conversation would
    /// be garbled even if nothing were lost. Set only for a re-attached (hydrated) run — a session that creates
    /// its own run has its own <see cref="IsStreaming"/> to block Send.
    /// </summary>
    public bool ForeignRunActive { get; private set; }

    /// <summary>Raised when <see cref="ForeignRunActive"/> changes (marshaled to the UI thread by the manager).</summary>
    public event EventHandler<bool>? ForeignRunActiveChanged;

    /// <summary>Sets <see cref="ForeignRunActive"/> and notifies (no-op when unchanged).</summary>
    public void SetForeignRunActive(bool active)
    {
        if (ForeignRunActive == active)
            return;
        ForeignRunActive = active;
        ForeignRunActiveChanged?.Invoke(this, active);
    }

    /// <summary>True while this chat's run is parked for plan approval — narrower than
    /// <see cref="ForeignRunActive"/>, which stays false for any park so "continue in chat" stays open.</summary>
    public bool PlanApprovalParkActive { get; private set; }

    /// <summary>Raised when <see cref="PlanApprovalParkActive"/> changes (marshaled to the UI thread by the manager).</summary>
    public event EventHandler<bool>? PlanApprovalParkActiveChanged;

    public void SetPlanApprovalParkActive(bool active)
    {
        if (PlanApprovalParkActive == active)
            return;
        PlanApprovalParkActive = active;
        PlanApprovalParkActiveChanged?.Invoke(this, active);
    }

    /// <summary>
    /// Sets the per-chat working directory. Trims, treats empty as null (= sandbox root),
    /// and normalizes separators to forward slashes (the stored/relative convention).
    /// </summary>
    internal void SetWorkingDirectory(string? relativePath)
    {
        var trimmed = relativePath?.Trim();
        WorkingDirectory = string.IsNullOrEmpty(trimmed) ? null : trimmed.Replace('\\', '/');
    }

    /// <summary>Surface a pre-turn failure (e.g. no provider configured) as a <see cref="RunFailed"/> event.</summary>
    internal void RaiseRunFailed(RunFailedEventArgs args) => RunFailed?.Invoke(this, args);

    /// <summary>
    /// Raise <see cref="TurnCompleted"/> from a Planned run's per-run finalize (the live executor's
    /// EndRunAsync mirror) — the single-turn path raises it inline in <see cref="RunTurnAsync"/>'s finally.
    /// </summary>
    internal void RaiseTurnCompleted(TurnCompletedEventArgs args) => TurnCompleted?.Invoke(this, args);

    /// <summary>
    /// Ask the manager to make the transcript so far durable (per-step durability, E2). Raised by the live
    /// executor after each completed step — persist ONLY: no terminal settle, no
    /// <see cref="TurnCompleted"/>. Never throws: persistence is bookkeeping and must never fail the step
    /// that produced the content (guardrail 1).
    /// </summary>
    internal void RequestPersist()
    {
        try { PersistRequested?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "Interim persist request failed for chat {ChatId}", Id); }
    }

    /// <summary>Single funnel for state writes — no-ops on unchanged value, raises <see cref="StateChanged"/>.</summary>
    internal void SetState(ChatState next)
    {
        if (State == next) return;
        var old = State;
        State = next;
        _logger.LogInformation("Chat {ChatId} state {State} at {Utc:o}",
            Id, next, DateTime.UtcNow);
        StateChanged?.Invoke(this, new ChatStateChangedEventArgs { OldState = old, NewState = next });
    }

    /// <summary>
    /// Creates the per-turn <see cref="CancellationTokenSource"/> up front — before the
    /// manager resolves settings/persona/provider — so a Cancel click during that
    /// setup-await window lands on a live CTS instead of being silently lost
    /// (open-question C1). <see cref="RunTurnAsync"/> reuses this CTS rather than
    /// recreating it. Only ever called when no turn is in flight (send/regenerate are
    /// <c>!IsStreaming</c>-gated), so the defensive dispose cannot clobber a running turn.
    /// </summary>
    internal CancellationToken BeginTurn()
    {
        Cts?.Dispose();
        Cts = new CancellationTokenSource();
        return Cts.Token;
    }

    /// <summary>Releases the per-turn CTS when a turn is abandoned before <see cref="RunTurnAsync"/> runs (setup failure).</summary>
    internal void DisposeCts()
    {
        Cts?.Dispose();
        Cts = null;
    }

    /// <summary>Cancels the in-flight turn and any pending action cards. Never disposes the Cts (the turn's finally does).</summary>
    public void Cancel()
    {
        Cts?.Cancel();
        foreach (var msg in Messages)
            CancelPendingActionCards(msg);
    }

    private static void CancelPendingActionCards(AssistantMessage? message)
    {
        if (message is null) return;
        foreach (var card in message.ActionCards)
        {
            if (card.IsPending)
                card.CancelCommand.Execute(null);
        }
    }

    /// <summary>
    /// The relocated run loop. Streams the assistant reply, dispatches tool calls
    /// (blocking on the action-card confirmation for write ops — the canonical
    /// <see cref="ChatState.WaitingForTool"/> transition), and drives the state
    /// machine. UI-thread-affine: no <c>Task.Run</c>, no <c>ConfigureAwait(false)</c>.
    /// </summary>
    public async Task RunTurnAsync(ChatTurnRequest request, CancellationToken externalToken)
    {
        var userMessage = request.UserMessage;
        var assistantMessage = request.AssistantMessage;
        var provider = request.Provider;
        var turnSetup = request.TurnSetup;
        var atCommands = request.AtCommands;
        var injectedFileContext = request.InjectedFileContext;
        var regenerationInstruction = request.RegenerationInstruction;
        var tokenizationEnabled = request.TokenizationEnabled;

        // Reuse the CTS created by BeginTurn() (so a cancel during the manager's
        // setup-await window is honored — open-question C1). Fall back to creating one
        // for direct callers (e.g. unit tests) that invoke RunTurnAsync without BeginTurn.
        Cts ??= CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = Cts.Token;

        // Flip to Running synchronously (before the first await) so IsStreaming is
        // observable on the UI thread before this method yields — preserves the
        // immediate send-button disable / voice-mode gate that today's synchronous
        // IsStreaming=true at line 309 provided.
        ProviderId = provider.Id;
        SetState(ChatState.Running);

        // Make THIS session's token map the ambient map for the decorator for the
        // duration of this logical turn. AsyncLocal flows down each await continuation
        // and is isolated across interleaved turns, so two background turns never
        // share a PII namespace. Restored in finally.
        var previousAmbient = TokenMapAmbient.Current;
        TokenMapAmbient.Current = TokenMap;

        // Likewise expose THIS session's Id as the ambient task id so tool handlers
        // (FilesToolHandler) can key per-task state for the in-flight turn with zero
        // parameter plumbing. Id is Guid? — null on direct test callers that skip the
        // manager's SetIdentity. Restored in the same finally as the token map.
        var previousTask = TaskAmbient.Current;
        // Wire a per-turn file-touch sink so the file tools can surface each file they read/write to
        // THIS turn's answer as an open-file chip. The run loop is UI-affine (no ConfigureAwait(false)),
        // so the callback adds to the bound collection directly — same contract as message.ActionCards.Add.
        TaskAmbient.Current = new TaskContext(Id, WorkingDirectory, touch =>
            assistantMessage.AddOrUpgradeFileRef(new FileRef(touch.AbsolutePath, touch.Kind switch
            {
                FileTouchKind.Created => FileRefKind.Created,
                FileTouchKind.Updated => FileRefKind.Updated,
                _ => FileRefKind.Read,
            })), ChatId: Id,
            OnSourceCited: citation => assistantMessage.AddSource(ToSourceRef(citation)));

        var succeeded = false;
        try
        {
            // Honor a cancel that fired during the setup-await window (C1): bail before
            // calling the AI client, routing into the OperationCanceledException catch
            // below so the turn settles to Idle with the cancelled snackbar.
            token.ThrowIfCancellationRequested();

            var supportsTools = turnSetup.SupportsTools;
            var webSearchActive = turnSetup.WebSearchActive;
            var fullSystemPrompt = turnSetup.SystemPrompt;
            var tools = turnSetup.Tools;

            // This INTERACTIVE list is deliberately never compacted — "no interactive regression" is a
            // standing guardrail, and agent context compaction is an agent-run mechanism only. The
            // guardrail is structural rather than a runtime flag: this builder is a separate,
            // synchronous method body from the step builder (BuildStepChatMessagesAsync), which is the
            // only one that compacts. Keep them separate. Compacting here would silently change
            // ordinary chats, and the library's bytes/4 scoring counts an image attachment at its raw
            // byte size, which would make an image-bearing chat an active regression.
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, fullSystemPrompt)
            };

            foreach (var msg in Messages)
            {
                if (msg == assistantMessage)
                    continue;

                var hasInjection = !string.IsNullOrEmpty(injectedFileContext) || !string.IsNullOrEmpty(regenerationInstruction);
                if (msg == userMessage && (atCommands.Count > 0 || hasInjection))
                {
                    // Swap the @-command tokens for the items they name (when present), then append any
                    // @Files content the manager read at setup and/or a styled-regeneration instruction so
                    // the model sees them inline. Injection is ephemeral — msg.Content (the persisted/
                    // displayed text) is unchanged, so history never bloats and the user's bubble stays clean.
                    var stripped = atCommands.Count > 0 ? AtCommandParser.SubstituteCommands(msg.Content) : msg.Content;
                    var parts = new[] { stripped, injectedFileContext, regenerationInstruction }
                        .Where(p => !string.IsNullOrEmpty(p));
                    var visible = string.Join("\n\n", parts);
                    // ToChatMessage(overrideText) preserves an image attachment — the prior text-only
                    // ChatMessage construction here silently dropped it.
                    chatMessages.Add(msg.ToChatMessage(visible));
                }
                else
                    chatMessages.Add(msg.ToChatMessage());
            }

            // Stream consumption + tool loop — extracted so the Planned
            // step path (RunStepTurnAsync) can reuse the identical exchange body. It throws
            // on every provider/exception type; RunTurnAsync keeps today's catches verbatim.
            // The returned usage is discarded here (the single-turn path has no step ledger).
            await RunModelExchangeAsync(assistantMessage, chatMessages, provider, tools,
                supportsTools, webSearchActive, tokenizationEnabled, token,
                personaId: turnSetup.PersonaId, personaModelType: turnSetup.ModelType);

            // Reached the end of the turn without an exception — matches today's
            // "followups run as the last line of try" gate.
            succeeded = true;
        }
        catch (Pia.Services.Exceptions.LlmTimeoutException ex)
        {
            // Provider name is a user-named item (CLAUDE.md) — omit it from this
            // release-surviving log; the localized user message already carries it.
            _logger.LogError(ex, "AI response timed out (seconds={Seconds})", ex.TimeoutSeconds);
            var localizedMessage = _localizationService.Format("Msg_Assistant_ResponseTimedOut", ex.ProviderName, ex.TimeoutSeconds);
            if (string.IsNullOrEmpty(assistantMessage.Content))
                assistantMessage.Content = localizedMessage;
            SetState(ChatState.Error);
            RunFailed?.Invoke(this, new RunFailedEventArgs
            {
                Kind = RunFailureKind.Timeout,
                Title = _localizationService["Msg_Error"],
                Message = localizedMessage,
            });
        }
        catch (Pia.Services.Exceptions.LlmTruncatedException ex)
        {
            // Provider name is a user-named item (CLAUDE.md) — omit it from this
            // release-surviving log; the localized user message already carries it.
            _logger.LogWarning(ex, "AI response truncated by token cap (partialChars={PartialChars})", ex.PartialLength);
            var localizedMessage = _localizationService.Format("Msg_Assistant_ResponseTruncated", ex.ProviderName);
            assistantMessage.Content = string.IsNullOrEmpty(assistantMessage.Content)
                ? localizedMessage
                : assistantMessage.Content + "\n\n" + localizedMessage;
            SetState(ChatState.Error);
            RunFailed?.Invoke(this, new RunFailedEventArgs
            {
                Kind = RunFailureKind.Truncated,
                Title = _localizationService["Msg_Warning"],
                Message = localizedMessage,
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // User cancelled — not an error; settle to Idle and surface the cancelled snackbar. The token
            // guard keeps a transport cancellation (an HTTP timeout escaped conversion) off this arm.
            RunFailed?.Invoke(this, new RunFailedEventArgs
            {
                Kind = RunFailureKind.Generic,
                Title = _localizationService["Msg_Cancelled"],
                Message = _localizationService["Msg_Assistant_ResponseCancelled"],
            });
        }
        catch (Exception ex) when (ex.Message.Contains("EnableVision is false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Provider rejected image attachment (vision disabled)");
            var localized = _localizationService["Msg_Assistant_ProviderNoVision"];
            assistantMessage.Content = localized;
            // Session-local: drop the message pair (operates on this session's own list).
            Messages.Remove(assistantMessage);
            Messages.Remove(userMessage);
            SetState(ChatState.Error);
            RunFailed?.Invoke(this, new RunFailedEventArgs
            {
                Kind = RunFailureKind.VisionRejected,
                Title = _localizationService["Msg_Warning"],
                Message = localized,
                RestoreInputText = userMessage.Content,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get AI response");
            if (string.IsNullOrEmpty(assistantMessage.Content))
                assistantMessage.Content = $"Error: {ex.Message}";
            SetState(ChatState.Error);
            RunFailed?.Invoke(this, new RunFailedEventArgs
            {
                Kind = RunFailureKind.Generic,
                Title = _localizationService["Msg_Error"],
                Message = _localizationService.Format("Msg_Assistant_ResponseFailed", ex.Message),
            });
        }
        finally
        {
            // Per-exchange cleanup, shared with RunStepTurnAsync: empty-response
            // synthesis + IsStreaming=false + safety-net PII detokenize + ambient restore.
            var emptyResponseMessage = CleanupPerExchange(assistantMessage, tokenizationEnabled,
                token.IsCancellationRequested, previousAmbient, previousTask);
            var emptyResponse = emptyResponseMessage is not null;

            // Per-run terminal finalize — stays inline in RunTurnAsync only.
            Cts?.Dispose();
            Cts = null;

            // Terminal-state decision (the finalize-vs-switch race point). This whole
            // block is synchronous — the is-active read, the SetState, and (via
            // SetState→StateChanged→manager) the notifier-route all run with no await
            // in between, so they serialize against SetActive on the UI dispatcher.
            // A background turn that produced real content lands in Completed (unread);
            // the active turn (or a cancelled/empty/error one) lands in Idle/Error.
            if (State != ChatState.Error)
            {
                var producedContent = succeeded && !emptyResponse;
                SetState(producedContent && !_isActive(this)
                    ? ChatState.Completed
                    : ChatState.Idle);
            }

            if (emptyResponseMessage is not null)
            {
                RunFailed?.Invoke(this, new RunFailedEventArgs
                {
                    Kind = RunFailureKind.Empty,
                    Title = _localizationService["Msg_Warning"],
                    Message = emptyResponseMessage,
                });
            }

            // Empty-response synthesis means no real model content — not a success
            // for follow-up purposes (mirrors today: empty content skipped followups).
            TurnCompleted?.Invoke(this, new TurnCompletedEventArgs { Succeeded = succeeded && !emptyResponse });
        }
    }

    /// <summary>
    /// The shared model-exchange body: stream consumption + the tool loop +
    /// reasoning-timer + web-citation post-process. Both <see cref="RunTurnAsync"/> and
    /// <see cref="RunStepTurnAsync"/> call it. It <b>throws</b> on every exception type the
    /// callers catch — the catch handlers are NOT part of this body. Returns the last
    /// <see cref="Finished"/> usage so the step path can populate its ledger; the single-turn
    /// path discards it (it already applies stats via <see cref="ApplyStats"/>).
    /// </summary>
    private async Task<UsageDetails?> RunModelExchangeAsync(
        AssistantMessage assistantMessage,
        IList<ChatMessage> chatMessages,
        AiProvider provider,
        IList<AITool>? tools,
        bool supportsTools,
        bool webSearchActive,
        bool tokenizationEnabled,
        CancellationToken token,
        AgentContextBudget? contextBudget = null,
        RunAutonomyPolicy? policy = null,
        AgentTimelineScope? timeline = null,
        // The turn's persona, sent as X-Pia-Persona. A parameter rather than a field because the two
        // callers source it differently: the interactive turn reads it off the AssistantTurnSetup, a
        // Planned step off its own per-step persona attribution.
        Guid? personaId = null,
        // The persona's model-routing hint (metadata.pia_persona_type). Sourced the same way as
        // personaId; a Planned step passes nothing and routes on the mode default.
        string? personaModelType = null,
        // Where this turn's tool call/result messages accumulate, so the NEXT step can be built on them.
        // Only a Planned step passes one — the interactive turn has no next step and would grow it forever.
        List<ChatMessage>? toolExchangeSink = null)
    {
        var rawBuffer = new StringBuilder();
        // Reasoning reaches us via two channels that never overlap for a given provider:
        // a separate ReasoningDelta stream (TextReasoningContent / OpenRouter `reasoning`)
        // and inline <think> tags parsed out of the visible text. Merge both into ThinkingContent.
        var reasoningBuffer = new StringBuilder();
        var tagThinking = string.Empty;
        // A tool round just completed; the next TextDelta starts a fresh model turn built on the
        // tool result, not a continuation of whatever is already in rawBuffer.
        var pendingRoundBreak = false;

        void UpdateThinking()
        {
            var separate = reasoningBuffer.ToString().Trim();
            var combined = (separate.Length > 0, tagThinking.Length > 0) switch
            {
                (true, true) => $"{separate}\n\n{tagThinking}",
                (true, false) => separate,
                (false, true) => tagThinking,
                _ => string.Empty,
            };
            if (!string.IsNullOrEmpty(combined))
                assistantMessage.ThinkingContent = combined;
        }

        // Time the user-visible "thinking" phase: the live indicator is up from the moment
        // the message streams, so the timer starts here (not at the first reasoning token)
        // and the "Thought for Ns" chip survives turns whose provider forwarded no trace.
        var reasoningStartedAt = DateTime.Now;
        var reasoningTimed = false;

        void StopReasoningTimer()
        {
            if (!reasoningTimed)
            {
                reasoningTimed = true;
                var seconds = Math.Max(1, (int)Math.Round((DateTime.Now - reasoningStartedAt).TotalSeconds));
                var duration = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
                assistantMessage.ReasoningDurationLabel =
                    _localizationService.Format("Assistant_ThoughtForDuration", duration);
            }
        }

        UsageDetails? usage = null;

        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            chatMessages, provider, tools,
            supportsTools ? (toolCall, ctx) => HandleToolCallWithStatus(toolCall, assistantMessage, tokenizationEnabled, ctx, policy, timeline) : null,
            nameof(WindowMode.Assistant),
            personaId,
            personaModelType,
            cancellationToken: token,
            contextBudget: contextBudget))
        {
            switch (item)
            {
                case TextDelta td:
                    if (pendingRoundBreak && rawBuffer.Length > 0)
                        rawBuffer.Append("\n\n");
                    pendingRoundBreak = false;
                    rawBuffer.Append(td.Text);
                    var (visible, thinking) = StreamThinkTagParser.Parse(rawBuffer.ToString());

                    if (!string.IsNullOrEmpty(visible))
                        StopReasoningTimer(); // first answer token ends the thinking phase (set label first)
                    assistantMessage.Content = visible;
                    tagThinking = thinking;
                    UpdateThinking();
                    break;

                case ReasoningDelta rd:
                    reasoningBuffer.Append(rd.Text);
                    UpdateThinking();
                    break;

                case ToolRoundCompleted:
                    pendingRoundBreak = true;
                    break;

                case ToolRoundExchange round:
                    toolExchangeSink?.AddRange(round.Messages);
                    break;

                case Finished finished:
                    assistantMessage.IsProtectedRoute = finished.Protected;
                    assistantMessage.ToolRoundsExhausted = finished.ToolRoundsExhausted;
                    usage = finished.Usage;
                    ApplyStats(assistantMessage, finished, provider);
                    break;
            }
        }

        // Reasoning-only / no-visible-answer turns: close the timer at stream end.
        StopReasoningTimer();

        if (webSearchActive)
            ApplyWebCitations(assistantMessage);

        return usage;
    }

    /// <summary>
    /// The shared per-exchange cleanup: empty-response synthesis, IsStreaming=false,
    /// safety-net PII detokenization, and ambient restore. Both <see cref="RunTurnAsync"/> and
    /// <see cref="RunStepTurnAsync"/> run it. The per-run terminal finalize (Cts dispose / terminal
    /// state decision / empty snackbar / TurnCompleted) is NOT here — it stays inline in
    /// <see cref="RunTurnAsync"/> and is mirrored by the live executor's EndRunAsync.
    /// Returns the synthesized empty-response placeholder text, or null if the exchange produced content.
    /// Callers reuse the returned text for their own snackbar/error surfaces instead of re-picking the
    /// generic key, so a tool-rounds-exhausted turn reads consistently everywhere it is reported.
    /// </summary>
    private string? CleanupPerExchange(
        AssistantMessage assistantMessage,
        bool tokenizationEnabled,
        bool cancelled,
        ITokenMapService? previousAmbient,
        TaskContext? previousTask)
    {
        string? emptyResponseMessage = null;
        // Don't fabricate empty-response text when the message pair was removed
        // (vision rejection) or the turn was cancelled (C1) — a cancelled turn must
        // not also report "empty" and raise a second snackbar over the Cancelled one.
        if (Messages.Contains(assistantMessage) && string.IsNullOrEmpty(assistantMessage.Content)
            && !cancelled)
        {
            // A tool-rounds-exhausted turn already got a tools-disabled wrap-up call in AiClientService —
            // reaching here means even THAT came back with no text, so the generic "didn't respond" wording
            // would be misleading; it did try; the round budget ran out on it.
            emptyResponseMessage = assistantMessage.ToolRoundsExhausted
                ? _localizationService["Msg_Assistant_ToolRoundsExhausted"]
                : _localizationService["Msg_Assistant_EmptyResponse"];
            _logger.LogWarning("SendMessage completed but assistant response content is empty — tool calls may not have been processed or streaming yielded no visible text (toolRoundsExhausted={ToolRoundsExhausted})",
                assistantMessage.ToolRoundsExhausted);
            assistantMessage.Content = emptyResponseMessage;
        }

        assistantMessage.IsStreaming = false;

        // Final full-pass de-tokenization as safety net (own map).
        if (tokenizationEnabled && !string.IsNullOrEmpty(assistantMessage.Content))
            assistantMessage.Content = TokenMap.Detokenize(assistantMessage.Content);

        // Restore the previous ambient map before the terminal decision (must be
        // restored on the same logical async flow — done synchronously here).
        TokenMapAmbient.Current = previousAmbient;
        TaskAmbient.Current = previousTask;

        return emptyResponseMessage;
    }

    /// <summary>
    /// Runs one act step-turn of a <see cref="RunShape.Planned"/> run. Builds
    /// context from the visible transcript + an EPHEMERAL User-role step instruction (never added
    /// to <see cref="Messages"/> / persisted), creates a persona-attributed target
    /// <see cref="AssistantMessage"/>, runs <see cref="RunModelExchangeAsync"/> + the shared
    /// per-exchange cleanup, and returns the result. Exceptions become
    /// <c>StepTurnResult(Succeeded=false, …)</c> — no <see cref="ChatState.Error"/>, no RunFailed
    /// snackbar, and NO per-run finalize (the orchestrator's EndRunAsync owns that). The run stays
    /// <see cref="ChatState.Running"/> across steps; a mid-step tool-approval WaitingForTool flap
    /// inside <see cref="HandleToolCall"/> is the one exception, and correctly so.
    /// </summary>
    internal async Task<StepTurnResult> RunStepTurnAsync(StepTurnSpec spec, RunContext ctx, CancellationToken ct)
    {
        // Persona-attributed VISIBLE target message — one assistant message per step.
        var assistantMessage = new AssistantMessage(ChatRole.Assistant)
        {
            IsStreaming = true,
            Persona = spec.Persona,
        };
        Messages.Add(assistantMessage);

        // Per-step ambients: TaskId = run-STABLE spec.RunId, but the TaskContext OBJECT is
        // re-set per step so the touch sink targets THIS step's message. Token map ambient re-set too.
        var previousAmbient = TokenMapAmbient.Current;
        TokenMapAmbient.Current = TokenMap;
        var previousTask = TaskAmbient.Current;
        TaskAmbient.Current = new TaskContext(
            spec.RunId,
            // Same one-narrowing rule as the run context: an isolated run's workspace root
            // already IS the narrowed root, so passing the subpath too would probe <runRoot>\<subpath>. The
            // ORDINARY interactive turn (RunTurnAsync) is a separate construction and keeps passing
            // WorkingDirectory unconditionally — only a Planned run's steps isolate.
            spec.WorkspaceRoot is null ? WorkingDirectory : null,
            touch =>
                assistantMessage.AddOrUpgradeFileRef(new FileRef(touch.AbsolutePath, touch.Kind switch
                {
                    FileTouchKind.Created => FileRefKind.Created,
                    FileTouchKind.Updated => FileRefKind.Updated,
                    _ => FileRefKind.Read,
                })),
            // Confines this step's file tools to the run's workspace. The chip built just above
            // therefore carries a path inside runs\<runId>, which is why opening one resolves through
            // RunWorkspaceRedirects once the run's work is promoted out (plan D8).
            spec.WorkspaceRoot,
            ChatId: Id,
            OnSourceCited: citation => assistantMessage.AddSource(ToSourceRef(citation)));

        // Armed IFF offered: the sink exists exactly when LiveTurnExecutor.BuildSpec put
        // emit_step_result in this step's tool list, derived from the list itself rather than from a second
        // flag so the two cannot drift. A step on a tool-less provider gets no sink and lands on the
        // unconfirmed fallback, which is right — it could never have declared anything.
        var outcomeStore = spec.SupportsTools && AgentStepTools.OffersStepResultTool(spec.Tools)
            ? new StepOutcomeStore()
            : null;
        _stepOutcomeStore = outcomeStore;

        // Armed on the same condition as the declaration sink above, so "offered" and "accepted" cannot drift.
        var userInputStore = outcomeStore is null
            ? null
            : new UserInputRequestStore(AgentStepTools.OffersRequestUserInputTool(spec.Tools));
        _userInputRequest = userInputStore;

        var succeeded = false;
        var cancelled = false;
        // Distinct from `succeeded`, which the finally below rewrites: this stays true only if the exchange
        // itself returned without throwing, and it is what keeps a step's own declaration from overriding a
        // timeout, a truncation or a crash. The model gets a vote on its work, not on the transport.
        var exchangeCompleted = false;
        // Whether CleanupPerExchange synthesized the "assistant did not return a response" placeholder into the
        // visible message. Hoisted out of the finally because the RESULT has to know: the placeholder is UI text
        // for a blank chat bubble, not something a step produced. See the VisibleText argument below.
        var emptyResponseSynthesized = false;
        string? error = null;
        UsageDetails? usage = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            var chatMessages = await BuildStepChatMessagesAsync(spec, ctx, assistantMessage, ct);
            // Same budget the step request was compacted with, relayed so the in-step tool loop is
            // bounded too. The INTERACTIVE call site (RunTurnAsync) deliberately leaves it null.
            // spec.Persona is THIS step's attribution (a step can run under a different persona than the
            // run default), so the header follows the step rather than the run.
            usage = await RunModelExchangeAsync(assistantMessage, chatMessages, spec.Provider,
                spec.Tools, spec.SupportsTools, spec.WebSearchActive, spec.TokenizationEnabled, ct,
                AgentContextBudget.From(spec.Provider), spec.Policy, spec.Timeline, spec.Persona.Id,
                toolExchangeSink: StepToolExchangeSink(assistantMessage.Id));
            succeeded = true;
            exchangeCompleted = true;
        }
        catch (Pia.Services.Exceptions.LlmTimeoutException ex)
        {
            _logger.LogError(ex, "Agent step AI response timed out (seconds={Seconds})", ex.TimeoutSeconds);
            error = _localizationService.Format("Msg_Assistant_ResponseTimedOut", ex.ProviderName, ex.TimeoutSeconds);
            if (string.IsNullOrEmpty(assistantMessage.Content))
                assistantMessage.Content = error;
        }
        catch (Pia.Services.Exceptions.LlmTruncatedException ex)
        {
            _logger.LogWarning(ex, "Agent step AI response truncated by token cap (partialChars={PartialChars})", ex.PartialLength);
            var notice = _localizationService.Format("Msg_Assistant_ResponseTruncated", ex.ProviderName);
            assistantMessage.Content = string.IsNullOrEmpty(assistantMessage.Content)
                ? notice
                : assistantMessage.Content + "\n\n" + notice;
            error = notice;
        }
        // The token guard keeps a transport cancellation (an HTTP timeout escaped conversion) off this
        // arm: only a fired step token counts as a user/host cancel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            error = "cancelled";
        }
        catch (Exception ex) when (ex.Message.Contains("EnableVision is false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Provider rejected image attachment (vision disabled) in agent step");
            error = _localizationService["Msg_Assistant_ProviderNoVision"];
            if (string.IsNullOrEmpty(assistantMessage.Content))
                assistantMessage.Content = error;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent step failed to get AI response");
            error = ex.Message;
            if (string.IsNullOrEmpty(assistantMessage.Content))
                assistantMessage.Content = $"Error: {ex.Message}";
        }
        finally
        {
            // Shared per-exchange cleanup — clears IsStreaming + detokenizes PII + restores ambients.
            // NO per-run finalize (no Cts dispose, no terminal state decision, no snackbar, no TurnCompleted).
            var emptyResponseMessage = CleanupPerExchange(assistantMessage, spec.TokenizationEnabled,
                ct.IsCancellationRequested, previousAmbient, previousTask);
            emptyResponseSynthesized = emptyResponseMessage is not null;
            if (emptyResponseMessage is not null)
            {
                succeeded = false;
                error ??= emptyResponseMessage;
            }

            // Disarm before anything else can run a turn on this session — the verdict is read from the
            // method-local `outcomeStore` below, so this loses nothing.
            _stepOutcomeStore = null;
            // Same disarm, same reason, same beat. Read below from the method-local `userInputStore`.
            _userInputRequest = null;
        }

        // ---- The step-success decision ----
        // WAS: exception-absence (plus the empty-response downgrade above) — the live half of "the model
        // produced output, therefore the step worked". A step that ran, failed, and then explained its
        // failure in perfect prose threw nothing, so it recorded Done.
        // NOW: the step's own declaration wins over BOTH premises. succeeded:false records Failed however
        // much text came with it; succeeded:true clears the empty-response downgrade and records Done.
        // Gated on exchangeCompleted && !cancelled: a claim made in an early tool round must not paper over a
        // timeout, a truncation or a crash that happened afterwards, and a cancelled step is the user's call.
        var claim = exchangeCompleted && !cancelled ? outcomeStore?.Claim : null;
        if (claim is not null)
        {
            succeeded = claim.Succeeded;
            // The model's own reason replaces the generic text — the orchestrator hands Error straight to
            // ReplanAsync, so this is what the replanner gets to work from.
            error = claim.Succeeded
                ? null
                : string.IsNullOrWhiteSpace(claim.Summary)
                    ? AgentStepTools.UndetailedFailure
                    : claim.Summary;
        }

        // THE FALLBACK: no declaration keeps the old predicate, but the step is recorded as UNCONFIRMED
        // (Outcome stays null) rather than silently treated as vouched-for. Failing a step that never called
        // the tool would fail-closed on every provider without tool-calling — see StepOutcomeSignal's remarks.
        // Ids and booleans only; the summary is user-adjacent model prose and never rises above SensitiveDebug.
        // Template deliberately does NOT begin "Agent run {RunId} step {Ordinal}": that exact prefix is the
        // key ChatSessionStepTurnTests uses to Assert.Single the context-compaction diff line, and a second
        // Information line starting the same way would silently make that assertion ambiguous.
        _logger.LogInformation(
            "Step outcome for run {RunId} step {StepOrdinal}: offered={Offered} confirmed={Confirmed} succeeded={Succeeded} declarations={Declarations}"
            + " artifactReported={ArtifactReported}",
            spec.RunId, spec.Ordinal, outcomeStore is not null, claim is not null, succeeded && error is null,
            outcomeStore?.AcceptedCalls ?? 0, !string.IsNullOrWhiteSpace(claim?.ArtifactRef));
        if (claim is not null)
            _logger.SensitiveDebug("Step outcome summary: {Summary} artifact: {Artifact}", claim.Summary, claim.ArtifactRef);

        // A cancel outranks the run's own question, but a timeout/truncation/crash should not discard it.
        var askedQuestion = cancelled ? null : userInputStore?.Question;
        if (askedQuestion is not null)
        {
            // Counts only — the question text itself is never logged at this level.
            _logger.LogInformation(
                "Agent step asked the user for input on run {RunId} step {StepOrdinal}: asks={Asks} refused={Refused}",
                spec.RunId, spec.Ordinal, userInputStore!.AcceptedCalls, userInputStore.RefusedCalls);
        }
        else if (userInputStore is { CanAsk: false, RefusedCalls: > 0 })
        {
            // A delegated step tried to ask and was redirected to emit_step_result instead.
            _logger.LogInformation(
                "Agent step on run {RunId} step {StepOrdinal} refused {Refused} mid-plan ask(s) on a delegated step",
                spec.RunId, spec.Ordinal, userInputStore.RefusedCalls);
        }

        // Stable Guid Id (AssistantMessage ctor self-assigns) → the R3 transcript slice.
        var id = assistantMessage.Id;
        var stepSucceeded = succeeded && error is null;
        return new StepTurnResult(
            Succeeded: stepSucceeded,
            Cancelled: cancelled,
            Error: error,
            // THE EMPTY-RESPONSE PLACEHOLDER IS NOT A RESULT. When the step declared success with no
            // visible text, the claim block above rightly flips `succeeded` back to true — but the localized
            // "The assistant did not return a response." that CleanupPerExchange synthesized is still sitting in
            // the message. It belongs there: it is UI text so the chat does not render a blank bubble. It does
            // NOT belong in VisibleText, which becomes CompletedStepSummary.VisibleText and is rendered to the
            // critic as `result: …` directly under `- [ok, declared] <title>` — a step presented as a declared
            // success and contradicted on the very next line, which is a false premise fed to the one reader the
            // #9 tags exist for. The headless executor carries the empty string here (it has no placeholder at
            // all), so this is also what makes the two executors agree about what such a step carries.
            //
            // Scoped to the SUCCESS path on purpose: a step that really did fail with no text is honestly
            // described by the placeholder, and CleanupPerExchange is shared with ordinary chat turns and is
            // deliberately not touched.
            VisibleText: emptyResponseSynthesized && stepSucceeded
                ? string.Empty
                : assistantMessage.Content ?? string.Empty,
            Usage: usage,
            FirstMessageId: id,
            LastMessageId: id,
            Outcome: claim,
            // Unlike the headless twin, does not blank VisibleText: the reply is already on screen and persisted.
            UserInputQuestion: askedQuestion);
    }

    /// <summary>
    /// Builds the model context for a step exchange: the system prompt + the full visible transcript
    /// so far (excluding the streaming target) + one trailing EPHEMERAL User-role step instruction.
    /// The instruction message is a local — it is never added to <see cref="Messages"/> / persisted.
    /// The finished list is compacted against the provider's context budget so a long run cannot overflow the
    /// window and fail a step; compaction returns a NEW list, so the displayed and persisted transcript is
    /// unaffected. This is the LIVE half of executor parity — LiveTurnExecutor builds no message list of its
    /// own, so the parity seam lives here.
    /// </summary>
    /// <summary>The list this step's tool exchanges accumulate into, created on first use for the step's reply.</summary>
    private List<ChatMessage> StepToolExchangeSink(Guid assistantMessageId)
    {
        if (!_stepToolExchanges.TryGetValue(assistantMessageId, out var sink))
        {
            sink = new List<ChatMessage>();
            _stepToolExchanges[assistantMessageId] = sink;
        }

        return sink;
    }

    private async Task<List<ChatMessage>> BuildStepChatMessagesAsync(StepTurnSpec spec, RunContext ctx, AssistantMessage assistantMessage, CancellationToken ct)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, spec.SystemPrompt),
        };

        // ToChatMessage() carries text and an optional image and no tool content, so a step's calls and results
        // are spliced back in ahead of the reply they belong to — the live twin of what HeadlessTurnExecutor
        // keeps in _messages.
        foreach (var message in Messages)
        {
            if (message == assistantMessage)
                continue;
            if (_stepToolExchanges.TryGetValue(message.Id, out var exchanges))
                chatMessages.AddRange(exchanges);
            chatMessages.Add(message.ToChatMessage());
        }

        string instruction;
        if (spec.UseGoalVerbatim)
        {
            instruction = ctx.Goal;
        }
        else
        {
            instruction = $"Execute step {spec.Ordinal + 1}: {spec.Intent}.";
            if (!string.IsNullOrEmpty(spec.ExpectedArtifact))
                instruction += $" Expected: {spec.ExpectedArtifact}";
            instruction += " " + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint;
        }

        // The ONLY place a user steering note may ride — a ChatRole.User message, never System.
        chatMessages.Add(new ChatMessage(ChatRole.User, ctx.AppendNudge(instruction)));

        // Cleared before compaction, and by construction — _stepToolExchanges holds the full results and the
        // next step must still be able to be the one that gets them verbatim.
        var carried = spec.SupportsTools && spec.Tools is { Count: > 0 }
            ? AgentToolCarryover.ClearOldResults(chatMessages)
            : AgentToolCarryover.WithoutToolExchanges(chatMessages);

        // No ConfigureAwait(false) — this session is UI-thread-affine (see the class remarks), and the
        // caller resumes into code that touches Messages and the streaming target message.
        var compacted = await AgentContextCompactor.CompactAsync(carried, AgentContextBudget.From(spec.Provider), _logger, ct);

        // WHICH run and WHICH step lost context. The compactor logs the counts but holds neither id;
        // RunContext carries no run id either, but the step spec carries BOTH (it is already read for
        // spec.RunId when the ambient TaskContext is set), so the correlation happens here. Ordinal is
        // the raw 0-based value — the instruction text above says step Ordinal + 1. Counts and ids only:
        // this lands in a support-attachable log.
        if (compacted.Count != chatMessages.Count)
        {
            _logger.LogInformation(
                "Agent run {RunId} step {StepOrdinal} context compaction changed the step request from {BeforeCount} to {AfterCount} messages",
                spec.RunId, spec.Ordinal, chatMessages.Count, compacted.Count);
        }

        return compacted;
    }

    private async Task<object?> HandleToolCallWithStatus(
        FunctionCallContent toolCall, AssistantMessage message, bool tokenizationEnabled,
        ToolDispatchContext dispatch, RunAutonomyPolicy? policy = null, AgentTimelineScope? timeline = null)
    {
        message.ToolCallCount++;
        message.ToolCallCountLabel = _localizationService.Format("Assistant_ToolCallCount", message.ToolCallCount);
        message.StatusText = _actionCardBuilder.ResolveStatusText(toolCall.Name);
        var result = await HandleToolCall(toolCall, message, tokenizationEnabled, dispatch, policy, timeline);
        message.StatusText = _localizationService["Msg_Assistant_StatusThinking"];
        return result;
    }

    /// <summary>Reads a string tool-call argument, tolerating both a raw string and a <see cref="JsonElement"/>.</summary>
    private static string? ExtractStringArg(IDictionary<string, object?>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => value.ToString(),
        };
    }

    /// <param name="policy">The run's autonomy policy, from <c>StepTurnSpec.Policy</c>. Null on the
    /// ordinary interactive turn path (<see cref="RunTurnAsync"/>), which has no run — and null therefore
    /// means today's behaviour, byte for byte.</param>
    /// <param name="timeline">The step's audit sink, from <c>StepTurnSpec.Timeline</c>. Null on the
    /// ordinary interactive turn path, which has no run to attach a row to — so that path emits nothing.</param>
    /// <param name="dispatch">What the tool LOOP knows and this gate cannot derive: the 1-based round.
    /// Recorded on the audit row so a support log's "Round 3/10" line and the row for the call it dispatched
    /// can be lined up. Not optional and not nullable — every call into this gate comes from the loop.</param>
    private async Task<object?> HandleToolCall(
        FunctionCallContent toolCall, AssistantMessage message, bool tokenizationEnabled,
        ToolDispatchContext dispatch, RunAutonomyPolicy? policy = null, AgentTimelineScope? timeline = null)
    {
        // Every log line in this method names the tool through THIS local, never toolCall.Name directly. These
        // lines run BEFORE routing, so they are reachable with a model-authored name that no route will ever
        // recognize — and a log level is not a privacy gate, it is runtime-configurable (CLAUDE.md). A name that
        // does route is a route-table key and passes the shape check untouched; the raw string stays available
        // in a DEBUG build through the SensitiveDebug on the unrouted arm.
        var loggedName = AgentTimelineScope.SanitizeUnroutedToolName(toolCall.Name);
        _logger.LogInformation("Handling tool call: {ToolName}", loggedName);
        _logger.LogDebug("Tool call {ToolName} with {ArgCount} arguments", loggedName, toolCall.Arguments?.Count ?? 0);
#if DEBUG
        Debug.WriteLine($"[Tool Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif

        // suggest_agent_mode: pre-route special-case. RouteToolCallAsync would return null for this
        // unknown tool and dead-end at "Unknown tool.", so intercept BEFORE routing. Records a typed chip
        // on the streaming message + returns a short ack; never gated, always succeeds; every other
        // tool path is byte-for-byte unchanged because this short-circuits before RouteToolCallAsync.
        if (string.Equals(toolCall.Name, "suggest_agent_mode", StringComparison.Ordinal))
        {
            var reason = ExtractStringArg(toolCall.Arguments, "reason") ?? string.Empty;
            _logger.SensitiveDebug("suggest_agent_mode reason: {Reason}", reason); // user/model content
            // Idempotent — at most one chip per message even if the model calls twice.
            if (!message.HasAgentModeSuggestion)
            {
                var goal = Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
                // UI-affine loop: this handler runs on the UI thread, so the ObservableCollection add is safe.
                message.AgentModeSuggestions.Add(new AgentModeSuggestion(goal, reason));
            }
            return "Noted — offered Agent mode to the user.";
        }

        // emit_step_result: the second pre-route special case, for the same structural reason as
        // the one above — no plugin, no GUID, no _toolNameRoutes entry, so routing would miss it, log a
        // warning, emit a ToolGateDecision.UnknownTool audit row and answer "Unknown tool.". Gated on the
        // SINK, not on the name alone: an ordinary chat turn is never offered this tool, so a model that
        // invents the name there must still get the honest unknown-tool answer. Never gated for approval —
        // declaring an outcome writes nothing outside this step's sink. Its unattended twin lives at
        // BackgroundAssistantTurnRunner.HandleToolCallAsync and must stay in step with this one.
        if (_stepOutcomeStore is { } outcomeStore
            && string.Equals(toolCall.Name, AgentStepTools.EmitStepResultToolName, StringComparison.Ordinal))
        {
            return outcomeStore.Record(toolCall.Arguments);
        }

        // request_user_input: gated on the sink so a delegated step is redirected to emit_step_result instead of dead-ending; the question is never logged here.
        if (_userInputRequest is { } userInputStore
            && string.Equals(toolCall.Name, AgentStepTools.RequestUserInputToolName, StringComparison.Ordinal))
        {
            dispatch.Stop?.RequestStop();
            return userInputStore.Record(toolCall.Arguments);
        }

        // Length only, measured once and reused by every emit arm below — the serialized arguments
        // themselves never leave AgentTimelineScope.MeasureArgs). GATED ON THE SINK: measuring serializes the
        // whole argument dictionary, so on the ordinary interactive turn (timeline == null) it would materialize
        // a multi-megabyte write_file body on the UI thread and discard it — and "null means today's behaviour,
        // byte for byte" would be false. Reads never emit either, but they cannot be told apart before routing.
        var argsChars = timeline is null ? null : AgentTimelineScope.MeasureArgs(toolCall.Arguments);

        var routeResult = await _pluginService.RouteToolCallAsync(toolCall);
        if (routeResult is null)
        {
            // Sanitized at Warning, raw at SensitiveDebug: this is the one place the name is MODEL-authored, so
            // the release-visible line must not be able to carry a path, while "why did routing miss?" still
            // needs the string verbatim in a DEBUG build.
            _logger.LogWarning("No handler found for tool {ToolName}", loggedName);
            _logger.SensitiveDebug("Unrouted tool call name: {ToolName}", toolCall.Name);
            // "The model called a tool that does not exist, 12 times" is a real audit fact, and it cannot
            // flood: the round loop is bounded and the model gets the error text back. No plugin and no
            // class, because there is no route to derive either from. The NAME is model-authored on this arm
            // alone, so it is the one arm that sanitizes (see SanitizeUnroutedToolName).
            // NULL/NULL for the two instants: routing missed, so no gate was ever consulted and there was no
            // question to time. The CALL ID is still recorded — it is provider-authored on every arm, unlike
            // the name — so an unrouted call can still be matched to the provider round-trip that made it.
            timeline?.Emit(ToolGateSurface.Interactive, loggedName, ToolClass.Unknown, pluginId: null,
                ToolGateDecision.UnknownTool, AgentTimelineOutcome.NotExecuted,
                toolCallId: toolCall.CallId, round: dispatch.Round, requestedAt: null, decidedAt: null,
                argsChars);
            return "Unknown tool.";
        }

        var (result, pendingAction) = routeResult.Value;
        _logger.LogDebug("Plugin route returned: hasResult={HasResult}, hasPending={HasPending}",
            result is not null, pendingAction is not null);

        if (result is string resultStr)
        {
            _logger.SensitiveDebug("Tool {ToolName} result ({Length} chars): {Preview}",
                toolCall.Name,
                resultStr.Length,
                resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr);
        }

        if (result is not null)
            return result;

        // For write operations, show inline action card.
        if (pendingAction is not null)
        {
            // An ask stops the exchange: otherwise a write called after the park would raise a card whose approval would re-execute once the step re-runs.
            // Only a same-round call can reach here now — the ask itself already stopped the loop.
            if (_userInputRequest?.Question is not null)
            {
                dispatch.Stop?.RequestStop();
                _logger.LogInformation(
                    "Withheld tool {ToolName}: the run is stopping to ask the user", loggedName);
                return $"Not run: this run is stopping to ask the person your question, so '{pendingAction.ToolName}' "
                       + "was NOT executed — nothing more happens in this step. Stop now and produce no further "
                       + "tool calls; this step runs again from the beginning once someone answers.";
            }

            var gate = ResolveToolGate(pendingAction, policy);
            var pluginId = gate.PluginId;
            var tool = gate.Tool;
            var toolClass = gate.ToolClass;

            // The accepted/auto-approved success path: execute, fire ToolSucceeded, re-init the
            // memory token map, return the result. Shared by AllowOnce, AlwaysAllow, and bypass.
            //
            // <paramref name="decision"/> is the audit reason this call was authorized. Only the
            // Execute() call is bracketed for the timeline: ResolveSuccessTitle and the ToolSucceeded
            // subscribers run afterwards, and recording a fault in either as "the tool failed" would be a
            // false audit statement.
            //
            // requestedAt/decidedAt are PARAMETERS, not captured locals, precisely because this local function
            // is shared by two different authorities: the AutoRun bypass (answered by the policy, askedAt /
            // resolvedAt) and the three accept arms of the card (answered by a person, cardShownAt /
            // cardDecidedAt). Captured mutable locals would have the bypass silently read whatever the card
            // path had not yet assigned, and a wrong-but-plausible timestamp survives a green suite.
            async Task<object?> ExecuteAndReport(
                ToolGateDecision decision, DateTime? requestedAt, DateTime? decidedAt)
            {
                var startedAt = Stopwatch.GetTimestamp();
                object? actionResult;
                try
                {
                    actionResult = await pendingAction.Execute();
                }
                catch
                {
                    timeline?.Emit(ToolGateSurface.Interactive, tool, toolClass, pluginId,
                        decision, AgentTimelineOutcome.Error,
                        toolCallId: toolCall.CallId, round: dispatch.Round,
                        requestedAt: requestedAt, decidedAt: decidedAt,
                        argsChars, resultChars: null,
                        durationMs: AgentTimelineScope.ElapsedMs(startedAt));
                    // Rethrow: what a throwing tool does to the turn is untouched by this batch.
                    throw;
                }

                timeline?.Emit(ToolGateSurface.Interactive, tool, toolClass, pluginId,
                    decision, AgentTimelineOutcome.Ok,
                    toolCallId: toolCall.CallId, round: dispatch.Round,
                    requestedAt: requestedAt, decidedAt: decidedAt,
                    argsChars,
                    resultChars: (actionResult as string)?.Length, durationMs: AgentTimelineScope.ElapsedMs(startedAt));

                _logger.LogInformation("Executed {ToolName} action successfully", tool);

                var snackbarTitle = _actionCardBuilder.ResolveSuccessTitle(pendingAction.PluginName);
                ToolSucceeded?.Invoke(this, new ToolSucceededEventArgs
                {
                    SuccessTitle = snackbarTitle,
                    Description = DetokenizeForDisplay(pendingAction.Description, tokenizationEnabled),
                });

                // Re-scan for new PII after memory write (this session's own map).
                if (tokenizationEnabled && pendingAction.PluginName == "memory")
                {
                    try { await TokenMap.InitializeAsync(); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to re-initialize token map after memory write"); }
                }

                return actionResult;
            }

            // Bypass: an authorized tool auto-executes. Render a resolved auto-approved card FIRST (audit
            // trace, never silent) and log only the non-sensitive tool name, the decision enum and the plugin
            // id — never the arguments (CLAUDE.md privacy).
            if (gate.Verdict.Outcome == ToolGateOutcome.AutoRun)
            {
                // The DECISION, not a bare `true`: the card's resolved line has to say which authority ran this
                // call, and the session tier is the one that must not be reported as a permanent grant.
                var autoCard = _actionCardBuilder.Build(
                    pendingAction, tokenizationEnabled, gate.Verdict.Decision, toolClass);
                // UI-affine loop: the continuation already runs on the UI thread.
                message.ActionCards.Add(autoCard);
                _logger.LogInformation("Auto-approved {ToolName} ({Decision}, plugin {PluginId})",
                    tool, gate.Verdict.Decision, pluginId);
                // The POLICY answered this one; no human was asked, so the card's pair would be meaningless.
                return await ExecuteAndReport(gate.Verdict.Decision, gate.AskedAt, gate.ResolvedAt);
            }

            var card = await ConfirmWithActionCardAsync(pendingAction, message, tokenizationEnabled, gate);

            switch (card.Decision)
            {
                case ToolDecision.AllowOnce:
                    _logger.LogInformation("User allowed {ToolName} action once", tool);
                    return await ExecuteAndReport(ToolGateDecision.ApprovedOnce, card.ShownAt, card.DecidedAt);

                // THE MIDDLE TIER. Execute now and remember for the rest of this app session — nothing reaches
                // AppSettings, so the grant dies with the process.
                case ToolDecision.AllowForSession:
                    _permissions.GrantForSession(pluginId, tool);
                    _logger.LogInformation(
                        "User granted session approval for {ToolName} (plugin {PluginId})", tool, pluginId);
                    return await ExecuteAndReport(ToolGateDecision.ApprovedForSession, card.ShownAt, card.DecidedAt);

                case ToolDecision.AlwaysAllow:
                    await _permissions.GrantAsync(pluginId, tool);
                    _logger.LogInformation("User granted standing approval for {ToolName} (plugin {PluginId})", tool, pluginId);
                    return await ExecuteAndReport(ToolGateDecision.ApprovedAlways, card.ShownAt, card.DecidedAt);

                default:
                    _logger.LogInformation("User declined {ToolName} action", tool);
                    timeline?.Emit(ToolGateSurface.Interactive, tool, toolClass, pluginId,
                        card.Cancelled ? ToolGateDecision.CardCancelled : ToolGateDecision.DeclinedByUser,
                        AgentTimelineOutcome.NotExecuted,
                        toolCallId: toolCall.CallId, round: dispatch.Round,
                        // BOTH stamps land on the cancelled path too, because the finally assigned the second
                        // one: a CardCancelled row therefore says how long the question had been open.
                        requestedAt: card.ShownAt, decidedAt: card.DecidedAt,
                        argsChars);
                    return $"User declined the {tool} operation. Do not retry. Ask the user what they would like to do instead.";
            }
        }

        return "Tool call handled.";
    }

    /// <summary>What the interactive gate decides before a card is shown or a bypass runs.</summary>
    private sealed record ToolGateResolution(
        Guid PluginId,
        string Tool,
        ToolClass ToolClass,
        DateTime AskedAt,
        ToolGateVerdict Verdict,
        DateTime ResolvedAt);

    /// <summary>The human's answer to one action card, and the two instants the audit row needs.</summary>
    /// <param name="Cancelled">A cancelled card (new chat / retry / scope dispose) maps to
    /// <see cref="ToolDecision.Decline"/>, and recording THAT as "the user declined" would be a false audit
    /// statement — this survives the mapping so the decline arm can tell the two apart.</param>
    private sealed record ActionCardOutcome(
        ToolDecision Decision, bool Cancelled, DateTime ShownAt, DateTime? DecidedAt);

    /// <summary>
    /// ONE resolver, shared with the unattended and voice gates — it used to be an expression here and an
    /// independent one in BackgroundAssistantTurnRunner, with no shared chokepoint. Grant lookups stay with
    /// their OWNERS and arrive as bools, because the sets involved use three different comparers.
    /// </summary>
    private ToolGateResolution ResolveToolGate(PluginToolCall pendingAction, RunAutonomyPolicy? policy)
    {
        var pluginId = pendingAction.PluginId;
        var tool = pendingAction.ToolName;
        // Resolve's allowlist arm fires on Voice only, so this is inert on THIS surface — passed anyway,
        // like the session-grant lookup below, so the input stays an honest fact rather than a hardcoded false.
        var allowlisted = _permissions.IsAutoApproveEligible(tool);
        var toolClass = ToolClassifier.Classify(pendingAction.PluginName, IsExternalTool(tool));
        // False for every built-in handler's pending action (there is no server to have declared anything),
        // true only where an MCP server sent ToolAnnotations.DestructiveHint.
        var serverDestructive = pendingAction.ServerDeclaredDestructive;
        // The policy question, bracketed. These two are RequestedAt/DecidedAt for the arm the policy
        // itself answered — the AutoRun bypass. They are usually EQUAL: Resolve is a few comparisons and
        // DateTime.UtcNow has ~1 ms resolution on Windows (the same reason Seq is not a timestamp), so nothing
        // may assert strict ordering on them. The prompted arm does NOT use them; it takes its own pair around
        // the card, because "when was the question posed" there means "when could the human see it", which is a
        // different instant by however long they took to look.
        var askedAt = DateTime.UtcNow;
        var verdict = ToolAutonomy.Resolve(new ToolGateInput(
            ToolGateSurface.Interactive, tool, toolClass,
            ServerDeclaredDestructive: serverDestructive,
            IsAllowlisted: allowlisted,
            // The lookup that makes the second call of a session-granted tool card-free; without it the tier
            // records a grant nothing ever reads.
            HasSessionGrant: _permissions.IsGrantedForSession(pluginId, tool),
            HasStandingGrant: _permissions.IsGranted(pluginId, tool),
            IsNamedGrant: false,
            // A run-scoped denial list is an UNATTENDED-envelope concept; the card this surface shows
            // IS the human decision, so there is nothing persisted to look up.
            HasNamedDenial: false,
            Policy: policy,
            // This surface already HAS a human — it shows the action card. Parking the whole
            // run to ask the same question through a Flow item would be strictly worse than the card.
            CanPark: false,
            // Only the park reads it, and this surface never parks. False keeps the input honest about
            // what it is answering rather than about what happens to be reachable from here.
            IsTopLevelUserRun: false));

        return new ToolGateResolution(
            pluginId, tool, toolClass, askedAt, verdict, DateTime.UtcNow);
    }

    /// <summary>
    /// Show the action card and wait for a person. Reached whenever the gate did not auto-run:
    /// <see cref="ToolGateOutcome.Refuse"/> is UNREACHABLE on the interactive surface (pinned by
    /// <c>ToolAutonomyTests.InteractiveSurface_NeverRefuses</c>) — a human is looking at the card — and it
    /// deliberately falls through to the card rather than throwing, since a throw would fail the whole turn and
    /// degrading toward the card is the safe direction if that ever changes.
    /// </summary>
    private async Task<ActionCardOutcome> ConfirmWithActionCardAsync(
        PluginToolCall pendingAction, AssistantMessage message, bool tokenizationEnabled, ToolGateResolution gate)
    {
        // The AUTHORITATIVE class goes to BOTH cards, not just the auto-approved one: the prompted card's
        // button set has to agree with the gate that just resolved it.
        var card = _actionCardBuilder.Build(pendingAction, tokenizationEnabled, toolClass: gate.ToolClass);
        // RequestedAt for the PROMPTED arm is the instant the question became visible to a person —
        // i.e. the instant the card joins the bound collection — not the instant the policy was consulted.
        // Taken immediately before the Add so the interval (DecidedAt - RequestedAt) is "how long the human was
        // asked for", which is the only reading of it that is useful.
        var shownAt = DateTime.UtcNow;
        message.ActionCards.Add(card);

        // Stamped in the `finally` below rather than after a successful await, so the CANCELLED path
        // (TaskCanceledException: LLM timeout, new chat, retry, scope dispose) carries it too. That is the
        // whole of "including timeout" for this tree: VERIFIED there is no approval timer —
        // ActionCardInfo.WaitForUserDecisionAsync() is `=> _tcs.Task` with no timeout, and no
        // ApprovalTimeout setting or constant exists anywhere in src. So no new ToolGateDecision member was
        // added (it would be a persisted, append-only int for a state that cannot occur); the way a gate
        // decision ends without an answer today is CardCancelled, and stamping here makes the row say how
        // long the question had been open when the turn died.
        DateTime? decidedAt = null;
        ToolDecision decision;
        var cancelled = false;
        SetState(ChatState.WaitingForTool);
        try
        {
            decision = await card.WaitForUserDecisionAsync();
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Tool action cancelled for {ToolName}", gate.Tool);
            cancelled = true;
            decision = ToolDecision.Decline;
        }
        finally
        {
            decidedAt = DateTime.UtcNow;

            // Back to Running for the next tool/segment (the turn is still in flight).
            if (State == ChatState.WaitingForTool)
                SetState(ChatState.Running);
        }

        // Carried as-is, with NO `?? DateTime.UtcNow` fallback. Every caller arm is reached only after the
        // finally ran, so in practice it is never null — but a fallback would MANUFACTURE an instant if that
        // ever stopped being true, and a fabricated "decided at" on an audit row is worse than an honest NULL.
        // It also keeps the stamp observable: neutralize the finally and the row goes null, which is what the
        // prompted-card tests watch.
        return new ActionCardOutcome(decision, cancelled, shownAt, decidedAt);
    }

    /// <summary>
    /// Is this an external/MCP tool? Re-derived from the plugin SERVICE at the gate — the same source the
    /// unattended gate uses — never from a name pattern and never from the pending action.
    /// </summary>
    /// <remarks>A fault answers <c>true</c>, which only ever narrows this surface: the settings preset does
    /// not cover External, so an auto-approval degrades to a card. The try/catch is not decoration — this
    /// call used to be bare, so a throw failed the whole turn.</remarks>
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

    private string DetokenizeForDisplay(string text, bool tokenizationEnabled) =>
        tokenizationEnabled ? TokenMap.Detokenize(text) : text;

    private static SourceRef ToSourceRef(SourceCitation citation) => new(
        Number: 0,
        Source: citation.Label,
        Meta: citation.Meta,
        Url: null,
        Kind: citation.Kind == SourceCitationKind.Chat ? SourceRefKind.Chat : SourceRefKind.VaultPage,
        Target: citation.Target);

    private void ApplyWebCitations(AssistantMessage message)
    {
        if (string.IsNullOrEmpty(message.Content)) return;

        var (cleaned, sources) = WebCitationExtractor.Extract(message.Content);
        if (sources.Count == 0) return;

        message.Content = cleaned;
        foreach (var s in sources)
            message.AddSource(s);

        _logger.LogInformation("Extracted {Count} web source(s) from assistant message", sources.Count);
    }

    private void ApplyStats(AssistantMessage message, Finished finished, AiProvider provider)
    {
        int? totalTokens = null;
        if (finished.Usage is { } usage)
        {
            var total = (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);
            if (total > 0) totalTokens = (int)total;
        }
        if (totalTokens is null)
            _logger.LogDebug("Stream finished without usable usage details (providerType={ProviderType})", provider.ProviderType);

        message.Stats = new AnswerStats(totalTokens, finished.Model, finished.Provider);
    }

    /// <summary>
    /// Window teardown / LRU retire. This cancel deliberately does <b>not</b> revoke a pending
    /// user-pause request (unlike Stop and clear-conversation, which do — <c>AssistantViewModel</c>). The
    /// reaper never retires a session with a turn in flight, so the reachable case is the window closing on a
    /// running Planned run, and there a pause request that is still unconsumed yielding a <c>Paused</c>,
    /// RESUMABLE run is strictly better than a <c>Cancelled</c> one. Same asymmetry, same reason, as
    /// <c>HeadlessRunLauncher.StopAsync</c>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
    }
}
