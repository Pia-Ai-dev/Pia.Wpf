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

    /// <summary>Raised on every real state transition (no-op on unchanged value).</summary>
    public event EventHandler<ChatStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a turn completes (any terminal state) — active VM persists / followups / TTS.</summary>
    public event EventHandler<TurnCompletedEventArgs>? TurnCompleted;

    /// <summary>Raised when an accepted write-action succeeded — active VM shows a snackbar.</summary>
    public event EventHandler<ToolSucceededEventArgs>? ToolSucceeded;

    /// <summary>Raised on a handled error (not cancellation) — active VM shows a snackbar / restores composer.</summary>
    public event EventHandler<RunFailedEventArgs>? RunFailed;

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
        TaskAmbient.Current = new TaskContext(Id, WorkingDirectory);

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

            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, fullSystemPrompt)
            };

            foreach (var msg in Messages)
            {
                if (msg == assistantMessage)
                    continue;

                if (msg == userMessage && atCommands.Count > 0)
                {
                    // Strip the @-command tokens, then append any @Files content the manager read at
                    // setup so the model sees the file inline (not only via a tool it may decline to
                    // call). Injection is ephemeral — msg.Content (the persisted/displayed text) keeps
                    // the original @Files token, so history never bloats with file dumps.
                    var stripped = AtCommandParser.StripCommands(msg.Content);
                    string visible;
                    if (string.IsNullOrEmpty(injectedFileContext))
                        visible = stripped;
                    else if (string.IsNullOrEmpty(stripped))
                        visible = injectedFileContext;
                    else
                        visible = $"{stripped}\n\n{injectedFileContext}";
                    chatMessages.Add(new ChatMessage(ChatRole.User, visible));
                }
                else
                    chatMessages.Add(msg.ToChatMessage());
            }

            var rawBuffer = new StringBuilder();

            await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
                chatMessages, provider, tools,
                supportsTools ? toolCall => HandleToolCallWithStatus(toolCall, assistantMessage, tokenizationEnabled) : null,
                nameof(WindowMode.Assistant),
                token))
            {
                switch (item)
                {
                    case TextDelta td:
                        rawBuffer.Append(td.Text);
                        var (visible, thinking) = StreamThinkTagParser.Parse(rawBuffer.ToString());

                        assistantMessage.Content = visible;
                        if (!string.IsNullOrEmpty(thinking))
                            assistantMessage.ThinkingContent = thinking;
                        break;

                    case Finished finished:
                        ApplyStats(assistantMessage, finished, provider);
                        break;
                }
            }

            if (webSearchActive)
                ApplyWebCitations(assistantMessage);

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
            var emptyResponse = false;
            // Don't fabricate empty-response text when the message pair was removed
            // (vision rejection) or the turn was cancelled (C1) — a cancelled turn must
            // not also report "empty" and raise a second snackbar over the Cancelled one.
            if (Messages.Contains(assistantMessage) && string.IsNullOrEmpty(assistantMessage.Content)
                && !token.IsCancellationRequested)
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

    private async Task<object?> HandleToolCallWithStatus(FunctionCallContent toolCall, AssistantMessage message, bool tokenizationEnabled)
    {
        message.StatusText = _actionCardBuilder.ResolveStatusText(toolCall.Name);
        var result = await HandleToolCall(toolCall, message, tokenizationEnabled);
        message.StatusText = _localizationService["Msg_Assistant_StatusThinking"];
        return result;
    }

    private async Task<object?> HandleToolCall(FunctionCallContent toolCall, AssistantMessage message, bool tokenizationEnabled)
    {
        _logger.LogInformation("Handling tool call: {ToolName}", toolCall.Name);
        _logger.LogDebug("Tool call {ToolName} with {ArgCount} arguments", toolCall.Name, toolCall.Arguments?.Count ?? 0);
#if DEBUG
        Debug.WriteLine($"[Tool Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif

        var routeResult = await _pluginService.RouteToolCallAsync(toolCall);
        if (routeResult is null)
        {
            _logger.LogWarning("No handler found for tool {ToolName}", toolCall.Name);
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
            // Eligibility comes from the SERVICE, never the card — the gate re-checks it so a
            // forged/stale grant on an ineligible tool (e.g. write_file) cannot auto-bypass.
            var eligible = _permissions.IsAutoApproveEligible(tool);

            // The accepted/auto-approved success path: execute, fire ToolSucceeded, re-init the
            // memory token map, return the result. Shared by AllowOnce, AlwaysAllow, and bypass.
            async Task<object?> ExecuteAndReport()
            {
                var actionResult = await pendingAction.Execute();
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

            // Bypass: an eligible tool the user has already granted auto-executes. Render a
            // resolved auto-approved card FIRST (audit trace, never silent) and log only the
            // non-sensitive tool name + plugin id — never the arguments (CLAUDE.md privacy).
            if (eligible && _permissions.IsGranted(pluginId, tool))
            {
                var autoCard = _actionCardBuilder.Build(pendingAction, tokenizationEnabled, autoApproved: true);
                // UI-affine loop: the continuation already runs on the UI thread.
                message.ActionCards.Add(autoCard);
                _logger.LogInformation("Auto-approved {ToolName} via standing grant (plugin {PluginId})", tool, pluginId);
                return await ExecuteAndReport();
            }

            var card = _actionCardBuilder.Build(pendingAction, tokenizationEnabled);
            message.ActionCards.Add(card);

            ToolDecision decision;
            SetState(ChatState.WaitingForTool);
            try
            {
                decision = await card.WaitForUserDecisionAsync();
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Tool action cancelled for {ToolName}", tool);
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
                    return await ExecuteAndReport();

                case ToolDecision.AlwaysAllow:
                    // Defensive: never grant an ineligible tool even if its card somehow
                    // surfaced the option — AlwaysAllow on an ineligible tool degrades to
                    // AllowOnce (execute once, persist no grant).
                    if (eligible)
                    {
                        await _permissions.GrantAsync(pluginId, tool);
                        _logger.LogInformation("User granted standing approval for {ToolName} (plugin {PluginId})", tool, pluginId);
                    }
                    return await ExecuteAndReport();

                default:
                    _logger.LogInformation("User declined {ToolName} action", tool);
                    return $"User declined the {tool} operation. Do not retry. Ask the user what they would like to do instead.";
            }
        }

        return "Tool call handled.";
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
    }
}
