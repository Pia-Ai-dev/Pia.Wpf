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
    /// hermes #9: the sink <see cref="HandleToolCall"/> writes an <c>emit_step_result</c> declaration into,
    /// or null when no step turn is in flight. Set at the top of <see cref="RunStepTurnAsync"/> and cleared
    /// in its <c>finally</c>; the verdict is read from the METHOD-LOCAL sink afterwards, not from this field,
    /// so the clear cannot lose the claim.
    /// <para>
    /// Non-null is the gate on the interception. It must be back to null before an ordinary
    /// <see cref="RunTurnAsync"/> chat turn runs, or a hallucinated <c>emit_step_result</c> on a chat turn
    /// would be silently swallowed instead of answered "Unknown tool.". A session runs at most one turn at a
    /// time (<see cref="IsStreaming"/> guards the entry points), so one field is enough.
    /// </para>
    /// </summary>
    private StepOutcomeStore? _stepOutcomeStore;

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
    /// answers it with the same <c>PersistAsync</c> the terminal path uses (E2). Deliberately NOT
    /// <see cref="TurnCompleted"/>: a mid-run step is not a finished turn, so raising that instead would
    /// settle terminal state, fire follow-ups/TTS and present a parked run as complete (guardrail 5).
    /// The single-turn <see cref="RunTurnAsync"/> path never raises this — its terminal
    /// <see cref="TurnCompleted"/> already persists, and its ordering must stay byte-stable (§16 R11).
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
    /// or null when the chat has no run to surface (§15.1). Set by the manager on the UI thread when a
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
    /// be garbled even if nothing were lost.
    /// <para>
    /// Set only for a re-attached (hydrated) run — a session that creates its own run has its own
    /// <see cref="IsStreaming"/> to block Send, and setting this for it would be an interactive regression.
    /// </para>
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
            })));

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
                    // Strip the @-command tokens (when present), then append any @Files content the
                    // manager read at setup and/or a styled-regeneration instruction so the model sees
                    // them inline. Injection is ephemeral — msg.Content (the persisted/displayed text)
                    // is unchanged, so history never bloats and the user's bubble stays clean.
                    var stripped = atCommands.Count > 0 ? AtCommandParser.StripCommands(msg.Content) : msg.Content;
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

            // Stream consumption + tool loop (§13.5 step 1) — extracted so the Planned
            // step path (RunStepTurnAsync) can reuse the identical exchange body. It throws
            // on every provider/exception type; RunTurnAsync keeps today's catches verbatim.
            // The returned usage is discarded here (the single-turn path has no step ledger).
            await RunModelExchangeAsync(assistantMessage, chatMessages, provider, tools,
                supportsTools, webSearchActive, tokenizationEnabled, token,
                personaId: turnSetup.PersonaId);

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
        catch (OperationCanceledException)
        {
            // User cancelled — not an error; settle to Idle and surface the cancelled snackbar.
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
            // Per-exchange cleanup (§13.5 step 2), shared with RunStepTurnAsync: empty-response
            // synthesis + IsStreaming=false + safety-net PII detokenize + ambient restore.
            var emptyResponse = CleanupPerExchange(assistantMessage, tokenizationEnabled,
                token.IsCancellationRequested, previousAmbient, previousTask);

            // Per-run terminal finalize (§13.5 step 2) — stays inline in RunTurnAsync only.
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

            if (emptyResponse)
            {
                RunFailed?.Invoke(this, new RunFailedEventArgs
                {
                    Kind = RunFailureKind.Empty,
                    Title = _localizationService["Msg_Warning"],
                    Message = _localizationService["Msg_Assistant_EmptyResponse"],
                });
            }

            // Empty-response synthesis means no real model content — not a success
            // for follow-up purposes (mirrors today: empty content skipped followups).
            TurnCompleted?.Invoke(this, new TurnCompletedEventArgs { Succeeded = succeeded && !emptyResponse });
        }
    }

    /// <summary>
    /// The shared model-exchange body (§13.5 step 1): stream consumption + the tool loop +
    /// reasoning-timer + web-citation post-process. Both <see cref="RunTurnAsync"/> and
    /// <see cref="RunStepTurnAsync"/> call it. It <b>throws</b> on every exception type the
    /// callers catch — the catch handlers are NOT part of this body (§16 R4). Returns the last
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
        Guid? personaId = null)
    {
        var rawBuffer = new StringBuilder();
        // Reasoning reaches us via two channels that never overlap for a given provider:
        // a separate ReasoningDelta stream (TextReasoningContent / OpenRouter `reasoning`)
        // and inline <think> tags parsed out of the visible text. Merge both into ThinkingContent.
        var reasoningBuffer = new StringBuilder();
        var tagThinking = string.Empty;

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

        // Time the "thinking" phase: from the first reasoning token to the first answer
        // token (or stream end), surfaced as a localized "Thought for Ns" chip.
        DateTime? reasoningStartedAt = null;
        var reasoningTimed = false;

        void StopReasoningTimer()
        {
            if (reasoningStartedAt is { } startedAt && !reasoningTimed)
            {
                reasoningTimed = true;
                var seconds = Math.Max(1, (int)Math.Round((DateTime.Now - startedAt).TotalSeconds));
                var duration = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
                assistantMessage.ReasoningDurationLabel =
                    _localizationService.Format("Assistant_ThoughtForDuration", duration);
            }
        }

        UsageDetails? usage = null;

        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            chatMessages, provider, tools,
            supportsTools ? toolCall => HandleToolCallWithStatus(toolCall, assistantMessage, tokenizationEnabled, policy, timeline) : null,
            nameof(WindowMode.Assistant),
            personaId,
            cancellationToken: token,
            contextBudget: contextBudget))
        {
            switch (item)
            {
                case TextDelta td:
                    rawBuffer.Append(td.Text);
                    var (visible, thinking) = StreamThinkTagParser.Parse(rawBuffer.ToString());

                    if (!string.IsNullOrEmpty(thinking))
                        reasoningStartedAt ??= DateTime.Now; // inline <think> reasoning
                    if (!string.IsNullOrEmpty(visible))
                        StopReasoningTimer(); // first answer token ends the thinking phase (set label first)
                    assistantMessage.Content = visible;
                    tagThinking = thinking;
                    UpdateThinking();
                    break;

                case ReasoningDelta rd:
                    reasoningStartedAt ??= DateTime.Now;
                    reasoningBuffer.Append(rd.Text);
                    UpdateThinking();
                    break;

                case Finished finished:
                    assistantMessage.IsProtectedRoute = finished.Protected;
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
    /// The shared per-exchange cleanup (§13.5 step 2): empty-response synthesis, IsStreaming=false,
    /// safety-net PII detokenization, and ambient restore. Both <see cref="RunTurnAsync"/> and
    /// <see cref="RunStepTurnAsync"/> run it. The per-run terminal finalize (Cts dispose / terminal
    /// state decision / empty snackbar / TurnCompleted) is NOT here — it stays inline in
    /// <see cref="RunTurnAsync"/> and is mirrored by the live executor's EndRunAsync (§16 R4).
    /// Returns whether the empty-response placeholder was synthesized.
    /// </summary>
    private bool CleanupPerExchange(
        AssistantMessage assistantMessage,
        bool tokenizationEnabled,
        bool cancelled,
        ITokenMapService? previousAmbient,
        TaskContext? previousTask)
    {
        var emptyResponse = false;
        // Don't fabricate empty-response text when the message pair was removed
        // (vision rejection) or the turn was cancelled (C1) — a cancelled turn must
        // not also report "empty" and raise a second snackbar over the Cancelled one.
        if (Messages.Contains(assistantMessage) && string.IsNullOrEmpty(assistantMessage.Content)
            && !cancelled)
        {
            _logger.LogWarning("SendMessage completed but assistant response content is empty — tool calls may not have been processed or streaming yielded no visible text");
            assistantMessage.Content = _localizationService["Msg_Assistant_EmptyResponse"];
            emptyResponse = true;
        }

        assistantMessage.IsStreaming = false;

        // Final full-pass de-tokenization as safety net (own map).
        if (tokenizationEnabled && !string.IsNullOrEmpty(assistantMessage.Content))
            assistantMessage.Content = TokenMap.Detokenize(assistantMessage.Content);

        // Restore the previous ambient map before the terminal decision (must be
        // restored on the same logical async flow — done synchronously here).
        TokenMapAmbient.Current = previousAmbient;
        TaskAmbient.Current = previousTask;

        return emptyResponse;
    }

    /// <summary>
    /// Runs one act step-turn of a <see cref="RunShape.Planned"/> run (§13.7, §16 R4/R9). Builds
    /// context from the visible transcript + an EPHEMERAL User-role step instruction (never added
    /// to <see cref="Messages"/> / persisted), creates a persona-attributed target
    /// <see cref="AssistantMessage"/>, runs <see cref="RunModelExchangeAsync"/> + the shared
    /// per-exchange cleanup, and returns the result. Exceptions become
    /// <c>StepTurnResult(Succeeded=false, …)</c> — no <see cref="ChatState.Error"/>, no RunFailed
    /// snackbar, and NO per-run finalize (the orchestrator's EndRunAsync owns that). The run stays
    /// <see cref="ChatState.Running"/> across steps; a mid-step tool-approval WaitingForTool flap
    /// inside <see cref="HandleToolCall"/> is the one exception and is R12-correct (§13.5.5).
    /// </summary>
    internal async Task<StepTurnResult> RunStepTurnAsync(StepTurnSpec spec, RunContext ctx, CancellationToken ct)
    {
        // Persona-attributed VISIBLE target message — one assistant message per step (§13.7).
        var assistantMessage = new AssistantMessage(ChatRole.Assistant)
        {
            IsStreaming = true,
            Persona = spec.Persona,
        };
        Messages.Add(assistantMessage);

        // Per-step ambients (§16 R9): TaskId = run-STABLE spec.RunId, but the TaskContext OBJECT is
        // re-set per step so the touch sink targets THIS step's message. Token map ambient re-set too.
        var previousAmbient = TokenMapAmbient.Current;
        TokenMapAmbient.Current = TokenMap;
        var previousTask = TaskAmbient.Current;
        TaskAmbient.Current = new TaskContext(
            spec.RunId,
            // Same one-narrowing rule as the run context (Batch 06 B6): an isolated run's workspace root
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
            // Batch 06 D4: confines this step's file tools to the run's workspace. The chip built just above
            // therefore carries a path inside runs\<runId>, which is why opening one resolves through
            // RunWorkspaceRedirects once the run's work is promoted out (plan D8).
            spec.WorkspaceRoot);

        // hermes #9. Armed IFF offered: the sink exists exactly when LiveTurnExecutor.BuildSpec put
        // emit_step_result in this step's tool list, derived from the list itself rather than from a second
        // flag so the two cannot drift. A step on a tool-less provider gets no sink and lands on the
        // unconfirmed fallback, which is right — it could never have declared anything.
        var outcomeStore = spec.SupportsTools && AgentStepTools.OffersStepResultTool(spec.Tools)
            ? new StepOutcomeStore()
            : null;
        _stepOutcomeStore = outcomeStore;

        var succeeded = false;
        var cancelled = false;
        // Distinct from `succeeded`, which the finally below rewrites: this stays true only if the exchange
        // itself returned without throwing, and it is what keeps a step's own declaration from overriding a
        // timeout, a truncation or a crash. The model gets a vote on its work, not on the transport.
        var exchangeCompleted = false;
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
                AgentContextBudget.From(spec.Provider), spec.Policy, spec.Timeline, spec.Persona.Id);
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
        catch (OperationCanceledException)
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
            // Shared per-exchange cleanup — clears IsStreaming + detokenizes PII + restores ambients (§16 R4).
            // NO per-run finalize (no Cts dispose, no terminal state decision, no snackbar, no TurnCompleted).
            var empty = CleanupPerExchange(assistantMessage, spec.TokenizationEnabled,
                ct.IsCancellationRequested, previousAmbient, previousTask);
            if (empty)
            {
                succeeded = false;
                error ??= _localizationService["Msg_Assistant_EmptyResponse"];
            }

            // Disarm before anything else can run a turn on this session — the verdict is read from the
            // method-local `outcomeStore` below, so this loses nothing.
            _stepOutcomeStore = null;
        }

        // ---- hermes #9: the step-success decision ----
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
            "Step outcome for run {RunId} step {StepOrdinal}: offered={Offered} confirmed={Confirmed} succeeded={Succeeded} declarations={Declarations}",
            spec.RunId, spec.Ordinal, outcomeStore is not null, claim is not null, succeeded && error is null,
            outcomeStore?.AcceptedCalls ?? 0);
        if (claim is not null)
            _logger.SensitiveDebug("Step outcome summary: {Summary} artifact: {Artifact}", claim.Summary, claim.ArtifactRef);

        // Stable Guid Id (AssistantMessage ctor self-assigns) → the R3 transcript slice.
        var id = assistantMessage.Id;
        return new StepTurnResult(
            Succeeded: succeeded && error is null,
            Cancelled: cancelled,
            Error: error,
            VisibleText: assistantMessage.Content ?? string.Empty,
            Usage: usage,
            FirstMessageId: id,
            LastMessageId: id,
            Outcome: claim);
    }

    /// <summary>
    /// Builds the model context for a step exchange: the system prompt + the full visible transcript
    /// so far (excluding the streaming target) + one trailing EPHEMERAL User-role step instruction.
    /// The instruction message is a local — it is never added to <see cref="Messages"/> / persisted (§13.7).
    /// <para>
    /// The finished list is compacted against the provider's context budget so a long run cannot
    /// overflow the window and fail a step. This is the LIVE half of executor parity — LiveTurnExecutor
    /// builds no message list of its own (it only posts to <see cref="RunStepTurnAsync"/>), so the
    /// parity seam lives here. Compaction returns a NEW list and never touches
    /// <see cref="Messages"/>, so the displayed and persisted transcript is unaffected.
    /// </para>
    /// </summary>
    private async Task<List<ChatMessage>> BuildStepChatMessagesAsync(StepTurnSpec spec, RunContext ctx, AssistantMessage assistantMessage, CancellationToken ct)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, spec.SystemPrompt),
        };

        foreach (var msg in Messages)
        {
            if (msg == assistantMessage)
                continue;
            chatMessages.Add(msg.ToChatMessage());
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
        }

        // Batch 08 D4: the ONLY place a user steering note may ride — a ChatRole.User message, never System.
        chatMessages.Add(new ChatMessage(ChatRole.User, ctx.AppendNudge(instruction)));

        // No ConfigureAwait(false) — this session is UI-thread-affine (see the class remarks), and the
        // caller resumes into code that touches Messages and the streaming target message.
        var compacted = await AgentContextCompactor.CompactAsync(chatMessages, AgentContextBudget.From(spec.Provider), _logger, ct);

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
        RunAutonomyPolicy? policy = null, AgentTimelineScope? timeline = null)
    {
        message.StatusText = _actionCardBuilder.ResolveStatusText(toolCall.Name);
        var result = await HandleToolCall(toolCall, message, tokenizationEnabled, policy, timeline);
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

    /// <param name="policy">The run's autonomy policy (Batch 04), from <c>StepTurnSpec.Policy</c>. Null on the
    /// ordinary interactive turn path (<see cref="RunTurnAsync"/>), which has no run — and null therefore
    /// means today's behaviour, byte for byte.</param>
    /// <param name="timeline">The step's audit sink (Batch 03), from <c>StepTurnSpec.Timeline</c>. Null on the
    /// ordinary interactive turn path, which has no run to attach a row to — so that path emits nothing.</param>
    private async Task<object?> HandleToolCall(
        FunctionCallContent toolCall, AssistantMessage message, bool tokenizationEnabled,
        RunAutonomyPolicy? policy = null, AgentTimelineScope? timeline = null)
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

        // suggest_agent_mode (R7): pre-route special-case. RouteToolCallAsync would return null for this
        // unknown tool and dead-end at "Unknown tool.", so intercept BEFORE routing. Records a typed chip
        // on the streaming message + returns a short ack; never gated, always succeeds. G1: every other
        // tool path is byte-for-byte unchanged because this short-circuits before RouteToolCallAsync.
        if (string.Equals(toolCall.Name, "suggest_agent_mode", StringComparison.Ordinal))
        {
            var reason = ExtractStringArg(toolCall.Arguments, "reason") ?? string.Empty;
            _logger.SensitiveDebug("suggest_agent_mode reason: {Reason}", reason); // user/model content
            // OQ2: idempotent — at most one chip per message even if the model calls twice.
            if (!message.HasAgentModeSuggestion)
            {
                var goal = Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
                // UI-affine loop: this handler runs on the UI thread, so the ObservableCollection add is safe.
                message.AgentModeSuggestions.Add(new AgentModeSuggestion(goal, reason));
            }
            return "Noted — offered Agent mode to the user.";
        }

        // emit_step_result (hermes #9): the second pre-route special case, for the same structural reason as
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

        // Length only, measured once and reused by every emit arm below (03 §3 — the serialized arguments
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
            timeline?.Emit(ToolGateSurface.Interactive, loggedName, ToolClass.Unknown, pluginId: null,
                ToolGateDecision.UnknownTool, AgentTimelineOutcome.NotExecuted, argsChars);
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
            var pluginId = pendingAction.PluginId;
            var tool = pendingAction.ToolName;
            // 04 D5: ONE resolver for both gates. The destructive-external FLOOR lives inside Resolve and is
            // evaluated before any policy or grant branch, so no policy value can reach an auto-approval past
            // it — it used to be this line and an independent expression in BackgroundAssistantTurnRunner,
            // with no shared chokepoint. Grant lookups stay with their OWNERS and arrive as bools (D7): the
            // three sets involved use three different comparers today and this batch changes none of them.
            // Eligibility still comes from the SERVICE, never the card, so a forged/stale grant on an
            // ineligible tool (write_file, a destructive MCP tool) cannot auto-bypass.
            var allowlisted = _permissions.IsAutoApproveEligible(tool);
            var toolClass = ToolClassifier.Classify(pendingAction.PluginName, IsExternalTool(tool));
            // Held as a local because the AlwaysAllow branch below needs the same answer the card's button set
            // was built from: an AlwaysAllow on a non-offerable tool executes once and persists NO grant.
            var offerable = ToolAutonomy.IsStandingGrantOfferable(toolClass, tool, allowlisted);
            var verdict = ToolAutonomy.Resolve(new ToolGateInput(
                ToolGateSurface.Interactive, tool, toolClass,
                IsAllowlisted: allowlisted,
                HasStandingGrant: _permissions.IsGranted(pluginId, tool),
                IsNamedGrant: false,
                Policy: policy));

            // The accepted/auto-approved success path: execute, fire ToolSucceeded, re-init the
            // memory token map, return the result. Shared by AllowOnce, AlwaysAllow, and bypass.
            //
            // <paramref name="decision"/> is the audit reason this call was authorized (Batch 03). Only the
            // Execute() call is bracketed for the timeline: ResolveSuccessTitle and the ToolSucceeded
            // subscribers run afterwards, and recording a fault in either as "the tool failed" would be a
            // false audit statement.
            async Task<object?> ExecuteAndReport(ToolGateDecision decision)
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
                        decision, AgentTimelineOutcome.Error, argsChars, resultChars: null,
                        durationMs: AgentTimelineScope.ElapsedMs(startedAt));
                    // Rethrow: what a throwing tool does to the turn is untouched by this batch.
                    throw;
                }

                timeline?.Emit(ToolGateSurface.Interactive, tool, toolClass, pluginId,
                    decision, AgentTimelineOutcome.Ok, argsChars,
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
            if (verdict.Outcome == ToolGateOutcome.AutoRun)
            {
                var autoCard = _actionCardBuilder.Build(pendingAction, tokenizationEnabled, autoApproved: true, toolClass);
                // UI-affine loop: the continuation already runs on the UI thread.
                message.ActionCards.Add(autoCard);
                _logger.LogInformation("Auto-approved {ToolName} ({Decision}, plugin {PluginId})",
                    tool, verdict.Decision, pluginId);
                return await ExecuteAndReport(verdict.Decision);
            }

            // ToolGateOutcome.Refuse is UNREACHABLE on the interactive surface (pinned by
            // ToolAutonomyTests.InteractiveSurface_NeverRefuses) — a human is looking at the card. It
            // deliberately falls through to the card rather than throwing: a throw here would fail the whole
            // turn, and degrading toward the card is the safe direction if that ever changes.
            // The AUTHORITATIVE class goes to BOTH cards, not just the auto-approved one: the prompted card's
            // button set has to agree with the gate that just resolved it (04 D4).
            var card = _actionCardBuilder.Build(pendingAction, tokenizationEnabled, toolClass: toolClass);
            message.ActionCards.Add(card);

            ToolDecision decision;
            // A cancelled card (new chat / retry / scope dispose) is mapped to ToolDecision.Decline below,
            // and recording THAT as "the user declined" would be a false audit statement. The flag survives
            // the mapping so the decline arm can tell the two apart.
            var cardCancelled = false;
            SetState(ChatState.WaitingForTool);
            try
            {
                decision = await card.WaitForUserDecisionAsync();
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Tool action cancelled for {ToolName}", tool);
                cardCancelled = true;
                decision = ToolDecision.Decline;
            }
            finally
            {
                // Back to Running for the next tool/segment (the turn is still in flight).
                if (State == ChatState.WaitingForTool)
                    SetState(ChatState.Running);
            }

            switch (decision)
            {
                case ToolDecision.AllowOnce:
                    _logger.LogInformation("User allowed {ToolName} action once", tool);
                    return await ExecuteAndReport(ToolGateDecision.ApprovedOnce);

                case ToolDecision.AlwaysAllow:
                    // Defensive: never grant a non-offerable tool even if its card somehow
                    // surfaced the option — AlwaysAllow on a non-offerable tool degrades to
                    // AllowOnce (execute once, persist no grant).
                    if (offerable)
                    {
                        await _permissions.GrantAsync(pluginId, tool);
                        _logger.LogInformation("User granted standing approval for {ToolName} (plugin {PluginId})", tool, pluginId);
                    }
                    return await ExecuteAndReport(ToolGateDecision.ApprovedAlways);

                default:
                    _logger.LogInformation("User declined {ToolName} action", tool);
                    timeline?.Emit(ToolGateSurface.Interactive, tool, toolClass, pluginId,
                        cardCancelled ? ToolGateDecision.CardCancelled : ToolGateDecision.DeclinedByUser,
                        AgentTimelineOutcome.NotExecuted, argsChars);
                    return $"User declined the {tool} operation. Do not retry. Ask the user what they would like to do instead.";
            }
        }

        return "Tool call handled.";
    }

    /// <summary>
    /// Is this an external/MCP tool? Re-derived from the plugin SERVICE at the gate — the same source the
    /// unattended gate uses — never from a name pattern and never from the pending action, so a renamed or
    /// spoofed tool cannot talk its way out of the destructive-external floor.
    /// <para>
    /// A derivation fault returns <c>true</c>, which is fail-CLOSED for the FLOOR (a delete-like tool is then
    /// refused/carded) and fail-OPEN for GRANTABILITY: <c>External</c> also makes a non-delete-like BUILT-IN
    /// pass <c>IsStandingGrantOfferable</c>, so a fault on <c>write_file</c> would let the card offer "Always
    /// allow" and let the gate persist a grant the allowlist deliberately excludes. Stated rather than fixed:
    /// the fix is a <c>routeKnown</c> signal threaded through <see cref="ToolGateInput"/>, and every
    /// <c>_toolNameRoutes</c> mutation in <c>PluginService</c> is inside <c>lock (_handlers)</c> with
    /// <c>IsMcpTool</c> a locked <c>TryGetValue</c>, so the only reachable throw is a null tool name — which
    /// cannot reach here (the pending action supplied the name). Adding a second condition beside the resolver
    /// in this file to close an unreachable path is the shape T-ARCH-1 exists to forbid.
    /// </para>
    /// <para>
    /// 04 D8: the call this wraps used to be BARE here while the headless twin
    /// (<c>BackgroundAssistantTurnRunner.IsExternalTool</c>) has had the guard since M3, so a throw
    /// propagated out of the tool loop and failed the whole turn. Failure-isolated bookkeeping: reading a
    /// classification must never fail a step.
    /// </para>
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

    private string DetokenizeForDisplay(string text, bool tokenizationEnabled) =>
        tokenizationEnabled ? TokenMap.Detokenize(text) : text;

    private void ApplyWebCitations(AssistantMessage message)
    {
        if (string.IsNullOrEmpty(message.Content)) return;

        var (cleaned, sources) = WebCitationExtractor.Extract(message.Content);
        if (sources.Count == 0) return;

        message.Content = cleaned;
        foreach (var s in sources)
            message.Sources.Add(s);

        _logger.LogInformation("Extracted {Count} web source(s) from assistant message", sources.Count);
    }

    private void ApplyStats(AssistantMessage message, Finished finished, AiProvider provider)
    {
        if (finished.Usage is not { } usage)
        {
            _logger.LogDebug("Stream finished without usage details (providerType={ProviderType})", provider.ProviderType);
            return;
        }

        var totalTokens = (int)((usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0));
        if (totalTokens <= 0)
        {
            _logger.LogDebug("Stream finished with zero tokens (providerType={ProviderType})", provider.ProviderType);
            return;
        }

        message.Stats = new AnswerStats(totalTokens, finished.Model);
    }

    /// <summary>
    /// Window teardown / LRU retire. Batch 08 D1: this cancel deliberately does <b>not</b> revoke a pending
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
