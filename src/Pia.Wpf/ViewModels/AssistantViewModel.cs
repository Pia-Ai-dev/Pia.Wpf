using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class AssistantViewModel : ObservableObject, INavigationAware, IDisposable
{
    private string BuildSystemPrompt(bool tokenizationEnabled)
    {
        var pluginPrompts = _pluginService.GetCombinedSystemPromptAdditions();
        return $"""
            You are Pia, a helpful personal assistant. Provide concise, accurate, and friendly responses.
            The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

            {pluginPrompts}

            TOOL SELECTION — follow this decision tree strictly:
            1. Does the request mention a specific TIME, DATE, or SCHEDULE for notification?
               YES → Use Reminder tools. NOT a reminder: "Remember I like coffee" (no time = memory).
               NO → Continue to step 2.
            2. Does the request involve a TASK, ACTION ITEM, or something to DO?
               YES → Use Todo tools. NOT a todo: "Remember my WiFi password" (information = memory).
               NO → Continue to step 3.
            3. Does the request involve STORING, RECALLING, or UPDATING personal information?
               YES → Use Memory tools (remember: query first, then create/update).
               NOT a memory: "Remind me at 3 PM to call Bob" (has time = reminder).
               NO → Respond conversationally without tools.

            Key principles:
            - When a user declines a proposed action, do NOT retry the same operation. Instead, acknowledge
              the decline and ask the user what they would like to do differently or if they want to adjust
              the details.
            {(tokenizationEnabled ? """

            When memory or contact data is returned, personal details (names, emails, phones, addresses,
            dates) are replaced with privacy tokens like [Person_1], [Email_1], etc. Use these tokens
            naturally in your responses — they will be resolved back to real values before the user sees
            your message. Never explain or call attention to the tokens. Treat [Person_1] as if it were
            the person's actual name.
            """ : "")}
            """;
    }

    private static string BuildAtCommandHint(IReadOnlyList<Pia.Models.AtCommand> commands)
    {
        if (commands.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("USER TOOL HINTS (the user explicitly requested these tools — prioritize them):");
        foreach (var cmd in commands)
        {
            var domainName = cmd.Domain switch
            {
                Pia.Models.AtCommandDomain.Memory => "Memory",
                Pia.Models.AtCommandDomain.Todo => "Todo",
                Pia.Models.AtCommandDomain.Reminder => "Reminder",
                _ => "Unknown"
            };

            if (cmd.ItemTitle is not null)
                sb.AppendLine($"- Use the {domainName} tools, specifically for item '{cmd.ItemTitle}'. Query for it first.");
            else
                sb.AppendLine($"- Use the {domainName} tools for this request.");
        }
        return sb.ToString();
    }

    private static string BuildSystemPromptNoTools() => $"""
        You are Pia, a helpful personal assistant. Provide concise, accurate, and friendly responses.
        The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).
        """;

    private readonly ILogger<AssistantViewModel> _logger;
    private readonly IAiClientService _aiClientService;
    private readonly IProviderService _providerService;
    private readonly ISettingsService _settingsService;
    private readonly IOutputService _outputService;
    private readonly IPluginService _pluginService;
    private readonly IVoiceInputService _voiceInputService;
    private readonly ITtsService _ttsService;
    private readonly IAudioRecordingService _audioRecordingService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly ITokenMapService _tokenMapService;
    private readonly IAutocompleteService _autocompleteService;
    private CancellationTokenSource? _streamingCts;
    private bool _disposed;
    private bool _tokenizationEnabled;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _hasMessages;

    [ObservableProperty]
    private bool _isTtsEnabled;

    [ObservableProperty]
    private bool _isTtsPlaying;

    [ObservableProperty]
    private VoiceModeViewModel? _voiceMode;

    [ObservableProperty]
    private bool _isVoiceModeActive;

    [ObservableProperty]
    private bool _isLiveTranscriptionVisible;

    public LiveTranscriptionViewModel LiveTranscription { get; }

    [ObservableProperty]
    private string _suggestionReminder = string.Empty;

    [ObservableProperty]
    private string _suggestionTodo = string.Empty;

    [ObservableProperty]
    private string _suggestionMemory = string.Empty;

    private static readonly string[] SuggestionReminderKeys =
    [
        "Assistant_Suggestion_Reminder1", "Assistant_Suggestion_Reminder2",
        "Assistant_Suggestion_Reminder3", "Assistant_Suggestion_Reminder4",
        "Assistant_Suggestion_Reminder5"
    ];

    private static readonly string[] SuggestionTodoKeys =
    [
        "Assistant_Suggestion_Todo1", "Assistant_Suggestion_Todo2",
        "Assistant_Suggestion_Todo3", "Assistant_Suggestion_Todo4",
        "Assistant_Suggestion_Todo5"
    ];

    private static readonly string[] SuggestionMemoryKeys =
    [
        "Assistant_Suggestion_Memory1", "Assistant_Suggestion_Memory2",
        "Assistant_Suggestion_Memory3", "Assistant_Suggestion_Memory4",
        "Assistant_Suggestion_Memory5"
    ];

    public IAutocompleteService AutocompleteService => _autocompleteService;

    public ObservableCollection<AssistantMessage> Messages { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand CancelStreamingCommand { get; }
    public IRelayCommand ClearConversationCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> CopyMessageCommand { get; }
    public IRelayCommand ToggleTtsCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> PlayMessageCommand { get; }
    public IAsyncRelayCommand EnterVoiceModeCommand { get; }
    public IRelayCommand<string> UseSuggestionCommand { get; }
    public IAsyncRelayCommand<PiiKeywordRequest> AddPiiKeywordCommand { get; }
    public IAsyncRelayCommand ToggleLiveTranscriptionCommand { get; }

    public AssistantViewModel(
        ILogger<AssistantViewModel> logger,
        IAiClientService aiClientService,
        IProviderService providerService,
        ISettingsService settingsService,
        IOutputService outputService,
        IPluginService pluginService,
        IVoiceInputService voiceInputService,
        ITtsService ttsService,
        IAudioRecordingService audioRecordingService,
        ITranscriptionService transcriptionService,
        ILoggerFactory loggerFactory,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        ITokenMapService tokenMapService,
        IAutocompleteService autocompleteService,
        LiveTranscriptionViewModel liveTranscription)
    {
        _logger = logger;
        _aiClientService = aiClientService;
        _providerService = providerService;
        _settingsService = settingsService;
        _outputService = outputService;
        _pluginService = pluginService;
        _voiceInputService = voiceInputService;
        _ttsService = ttsService;
        _audioRecordingService = audioRecordingService;
        _transcriptionService = transcriptionService;
        _loggerFactory = loggerFactory;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _tokenMapService = tokenMapService;
        _autocompleteService = autocompleteService;
        LiveTranscription = liveTranscription;

        SendMessageCommand = new AsyncRelayCommand(ExecuteSendMessage, CanExecuteSendMessage);
        ToggleRecordingCommand = new AsyncRelayCommand(ExecuteToggleRecording);
        CancelStreamingCommand = new RelayCommand(ExecuteCancelStreaming);
        ClearConversationCommand = new RelayCommand(ExecuteClearConversation);
        CopyMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteCopyMessage);
        ToggleTtsCommand = new RelayCommand(ExecuteToggleTts);
        PlayMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecutePlayMessage);
        EnterVoiceModeCommand = new AsyncRelayCommand(ExecuteEnterVoiceMode, CanEnterVoiceMode);
        UseSuggestionCommand = new RelayCommand<string>(ExecuteUseSuggestion);
        AddPiiKeywordCommand = new AsyncRelayCommand<PiiKeywordRequest>(ExecuteAddPiiKeyword);
        ToggleLiveTranscriptionCommand = new AsyncRelayCommand(ExecuteToggleLiveTranscription);

        _ttsService.IsPlayingChanged += OnTtsPlayingChanged;
        LiveTranscription.CloseRequested += OnLiveTranscriptionCloseRequested;
        LiveTranscription.SummarizeRequested += OnSummarizeRequested;
        PropertyChanged += OnPropertyChanged;
    }

    private async Task ExecuteToggleLiveTranscription()
    {
        if (IsLiveTranscriptionVisible)
        {
            // Closing the overlay also stops the underlying session.
            await LiveTranscription.StopAsync();
            IsLiveTranscriptionVisible = false;
            LiveTranscription.ResetForNewSession();
            return;
        }

        // Open the overlay; it shows the disclaimer first and warms up models in the
        // background so the eventual Start click is fast. Audio capture only begins when
        // the user accepts the disclaimer and clicks Start.
        LiveTranscription.ResetForNewSession();
        IsLiveTranscriptionVisible = true;
        LiveTranscription.BeginWarmup();
    }

    private void OnLiveTranscriptionCloseRequested(object? sender, EventArgs e)
    {
        // Fire-and-forget: stop the session and hide the overlay.
        _ = ExecuteToggleLiveTranscription();
    }

    private void OnSummarizeRequested(object? sender, MeetingSummarizationRequest e)
    {
        _ = HandleSummarizeRequestedAsync(e);
    }

    private async Task HandleSummarizeRequestedAsync(MeetingSummarizationRequest e)
    {
        await LiveTranscription.StopAsync();
        IsLiveTranscriptionVisible = false;
        LiveTranscription.ResetForNewSession();

        _snackbarService.Show(
            _localizationService["Msg_Assistant_TranscriptSaved"],
            _localizationService.Format("LiveTrans_SaveAndSummarize_Snackbar", e.DisplayPath),
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(4));

        InputText = _localizationService.Format("Assistant_Meeting_SummarizeRequest", e.DisplayPath);
        if (SendMessageCommand.CanExecute(null))
            await SendMessageCommand.ExecuteAsync(null);
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming))
        {
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(IsStreaming) or nameof(IsVoiceModeActive))
        {
            EnterVoiceModeCommand.NotifyCanExecuteChanged();
        }
    }

    private void ExecuteUseSuggestion(string? suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion))
            InputText = suggestion;
    }

    private bool CanExecuteSendMessage() =>
        !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    private async Task ExecuteSendMessage()
    {
        var userText = InputText.Trim();
        InputText = string.Empty;

        // Parse @-commands — keep full text for display (highlighted by view),
        // but strip commands from what the AI sees as the user message
        var atCommands = Pia.Services.AtCommandParser.ExtractAllCommands(userText);

        var userMessage = new AssistantMessage(ChatRole.User, userText);
        Messages.Add(userMessage);
        HasMessages = true;

        var assistantMessage = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        Messages.Add(assistantMessage);

        _streamingCts = new CancellationTokenSource();
        IsStreaming = true;

        try
        {
            var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
            if (provider is null)
            {
                assistantMessage.Content = _localizationService["Msg_Assistant_NoProviderInline"];
                assistantMessage.IsStreaming = false;
                IsStreaming = false;
                _snackbarService.Show(_localizationService["Msg_Error"], _localizationService["Msg_Assistant_NoProviderConfigured"], Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
                return;
            }

            // Determine if this provider supports tool calling
            var supportsTools = provider.SupportsToolCalling;

            // Build system prompt with memory context
            string fullSystemPrompt;
            IList<AITool>? tools;

            if (supportsTools)
            {
                fullSystemPrompt = BuildSystemPrompt(_tokenizationEnabled)
                    + BuildAtCommandHint(atCommands);
                tools = [.. _pluginService.GetAllTools()];
            }
            else
            {
                fullSystemPrompt = BuildSystemPromptNoTools();
                tools = null;
            }

            _logger.LogInformation("SendMessage: provider={ProviderName}, supportsTools={SupportsTools}, toolCount={ToolCount}",
                provider.Name, supportsTools, tools?.Count ?? 0);

            if (tools is { Count: > 0 })
                _logger.LogDebug("Tools being sent to AI: [{ToolNames}]",
                    string.Join(", ", tools.Select(t => t.Name)));

            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, fullSystemPrompt)
            };

            foreach (var msg in Messages)
            {
                if (msg == assistantMessage)
                    continue;

                // Strip @-commands from the latest user message sent to the AI
                // (the hint is already in the system prompt)
                if (msg == userMessage && atCommands.Count > 0)
                    chatMessages.Add(new ChatMessage(ChatRole.User,
                        Pia.Services.AtCommandParser.StripCommands(msg.Content)));
                else
                    chatMessages.Add(msg.ToChatMessage());
            }

            // Use tool-aware completion with think-tag parsing
            var rawBuffer = new StringBuilder();

            await foreach (var token in _aiClientService.GetChatCompletionWithToolsAsync(
                chatMessages, provider, tools,
                supportsTools ? toolCall => HandleToolCallWithStatus(toolCall, assistantMessage) : null,
                nameof(WindowMode.Assistant),
                _streamingCts.Token))
            {
                rawBuffer.Append(token);
                var (visible, thinking) = ParseStreamedContent(rawBuffer.ToString());

                assistantMessage.Content = visible;
                if (!string.IsNullOrEmpty(thinking))
                    assistantMessage.ThinkingContent = thinking;
            }
        }
        catch (OperationCanceledException)
        {
            _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Assistant_ResponseCancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get AI response");
            if (string.IsNullOrEmpty(assistantMessage.Content))
            {
                assistantMessage.Content = $"Error: {ex.Message}";
            }
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Assistant_ResponseFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
        finally
        {
            if (string.IsNullOrEmpty(assistantMessage.Content))
            {
                _logger.LogWarning("SendMessage completed but assistant response content is empty — tool calls may not have been processed or streaming yielded no visible text");
                assistantMessage.Content = _localizationService["Msg_Assistant_EmptyResponse"];
                _snackbarService.Show(
                    _localizationService["Msg_Warning"],
                    _localizationService["Msg_Assistant_EmptyResponse"],
                    Wpf.Ui.Controls.ControlAppearance.Caution,
                    null,
                    TimeSpan.FromSeconds(2));
            }

            assistantMessage.IsStreaming = false;
            IsStreaming = false;
            _streamingCts?.Dispose();
            _streamingCts = null;

            // Final full-pass de-tokenization as safety net
            if (_tokenizationEnabled && !string.IsNullOrEmpty(assistantMessage.Content))
            {
                assistantMessage.Content = _tokenMapService.Detokenize(assistantMessage.Content);
            }

            if (IsTtsEnabled && !string.IsNullOrEmpty(assistantMessage.Content)
                && !assistantMessage.Content.StartsWith("Error:"))
            {
                _ = SpeakMessageAsync(assistantMessage);
            }
        }
    }

    private async Task ExecuteToggleRecording()
    {
        var transcription = await _voiceInputService.CaptureVoiceInputAsync();
        if (!string.IsNullOrWhiteSpace(transcription))
        {
            InputText = string.IsNullOrWhiteSpace(InputText)
                ? transcription
                : $"{InputText.TrimEnd()} {transcription}";
            SendMessageCommand.NotifyCanExecuteChanged();
        }
    }

    private static (string visible, string thinking) ParseStreamedContent(string rawText)
    {
        var visible = new StringBuilder();
        var thinking = new StringBuilder();
        var remaining = rawText.AsSpan();

        while (remaining.Length > 0)
        {
            var thinkStart = remaining.IndexOf("<think>".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (thinkStart < 0)
            {
                visible.Append(remaining);
                break;
            }

            visible.Append(remaining[..thinkStart]);
            remaining = remaining[(thinkStart + 7)..]; // skip "<think>"

            var thinkEnd = remaining.IndexOf("</think>".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (thinkEnd < 0)
            {
                // Unclosed think block - all remaining is thinking content
                thinking.Append(remaining);
                break;
            }

            thinking.Append(remaining[..thinkEnd]);
            remaining = remaining[(thinkEnd + 8)..]; // skip "</think>"
        }

        return (visible.ToString().TrimStart(), thinking.ToString().Trim());
    }

    private async Task<object?> HandleToolCallWithStatus(FunctionCallContent toolCall, AssistantMessage message)
    {
        message.StatusText = toolCall.Name switch
        {
            "list_memories" => _localizationService["Msg_Assistant_StatusCheckingMemory"],
            "query_memory" => _localizationService["Msg_Assistant_StatusSearchingMemory"],
            "create_object" => _localizationService["Msg_Assistant_StatusCreatingMemory"],
            "update_object" => _localizationService["Msg_Assistant_StatusUpdatingMemory"],
            "append_to_list" => _localizationService["Msg_Assistant_StatusUpdatingMemory"],
            "delete_object" => _localizationService["Msg_Assistant_StatusDeletingMemory"],
            "create_reminder" => _localizationService["Msg_Assistant_StatusCreatingReminder"],
            "query_reminders" => _localizationService["Msg_Assistant_StatusCheckingReminders"],
            "update_reminder" => _localizationService["Msg_Assistant_StatusUpdatingReminder"],
            "delete_reminder" => _localizationService["Msg_Assistant_StatusDeletingReminder"],
            "create_todo" => _localizationService["Msg_Assistant_StatusCreatingTodo"],
            "query_todos" => _localizationService["Msg_Assistant_StatusCheckingTodos"],
            "complete_todo" => _localizationService["Msg_Assistant_StatusCompletingTodo"],
            "update_todo" => _localizationService["Msg_Assistant_StatusUpdatingTodo"],
            "delete_todo" => _localizationService["Msg_Assistant_StatusDeletingTodo"],
            _ => _localizationService["Msg_Assistant_StatusProcessing"]
        };

        var result = await HandleToolCall(toolCall, message);
        message.StatusText = _localizationService["Msg_Assistant_StatusThinking"];
        return result;
    }

    private async Task<object?> HandleToolCall(FunctionCallContent toolCall, AssistantMessage message)
    {
        _logger.LogInformation("Handling tool call: {ToolName}", toolCall.Name);
        _logger.LogDebug("Tool call {ToolName} with {ArgCount} arguments", toolCall.Name, toolCall.Arguments?.Count ?? 0);
#if DEBUG
        Debug.WriteLine($"[Tool Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif

        // Route through plugin service
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
            var preview = resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr;
            _logger.LogDebug("Tool {ToolName} result ({Length} chars): {Preview}",
                toolCall.Name, resultStr.Length, preview);
        }

        if (result is not null)
            return result;

        // For write operations, show inline action card
        if (pendingAction is not null)
        {
            var card = BuildPluginActionCard(pendingAction);
            await App.Current.Dispatcher.InvokeAsync(() => message.ActionCards.Add(card));

            string? chosenKey;
            try
            {
                chosenKey = await card.WaitForChoiceAsync();
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Tool action cancelled for {ToolName}", pendingAction.ToolName);
                chosenKey = null;
            }

            var confirmed = chosenKey is not null && chosenKey != "decline";

            if (confirmed)
            {
                _logger.LogInformation("User accepted {ToolName} action (choice={Choice})", pendingAction.ToolName, chosenKey);
                var actionResult = await pendingAction.Execute(chosenKey);
                _logger.LogInformation("Executed {ToolName} action successfully", pendingAction.ToolName);

                var snackbarTitle = pendingAction.PluginName switch
                {
                    "memory" => _localizationService["Msg_Assistant_MemoryUpdated"],
                    "todo" => _localizationService["Msg_Assistant_TodoUpdated"],
                    "reminder" => _localizationService["Msg_Assistant_ReminderUpdated"],
                    "meeting" => _localizationService["Msg_Assistant_MeetingSummarized"],
                    _ => _localizationService["Msg_Assistant_StatusProcessing"]
                };
                _snackbarService.Show(snackbarTitle,
                    DetokenizeForDisplay(pendingAction.Description),
                    Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));

                // Re-scan for new PII after memory write
                if (_tokenizationEnabled && pendingAction.PluginName == "memory")
                {
                    try { await _tokenMapService.InitializeAsync(); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to re-initialize token map after memory write"); }
                }

                return actionResult;
            }
            else
            {
                _logger.LogInformation("User declined {ToolName} action", pendingAction.ToolName);
                return $"User declined the {pendingAction.ToolName} operation. Do not retry. Ask the user what they would like to do instead.";
            }
        }

        return "Tool call handled.";
    }

    private ActionCardInfo BuildPluginActionCard(PluginToolCall pendingAction)
    {
        var category = pendingAction.PluginName switch
        {
            "memory" => ActionCardCategory.Memory,
            "todo" => ActionCardCategory.Todo,
            "reminder" => ActionCardCategory.Reminder,
            "meeting" => ActionCardCategory.Meeting,
            _ => ActionCardCategory.Memory
        };

        var isDelete = pendingAction.ToolName.Contains("delete");

        var warningText = isDelete ? pendingAction.PluginName switch
        {
            "memory" => _localizationService["Msg_Assistant_PermanentDeleteMemory"],
            "todo" => _localizationService["Msg_Assistant_PermanentDeleteTodo"],
            "reminder" => _localizationService["Msg_Assistant_PermanentDeleteReminder"],
            _ => null
        } : null;

        var details = pendingAction.Details is not null
            ? pendingAction.PluginName == "memory"
                ? new(DetokenizeDetails(JsonHelper.ParseToDetails(pendingAction.Details)))
                : new(DetokenizeDetails(JsonHelper.ParseKeyValueText(pendingAction.Details)))
            : new System.Collections.ObjectModel.ObservableCollection<ActionCardDetail>();

        return new ActionCardInfo
        {
            Title = FormatToolTitle(pendingAction.ToolName, category),
            Summary = DetokenizeForDisplay(pendingAction.Description),
            Category = category,
            ToolName = pendingAction.ToolName,
            IsDestructive = isDelete,
            WarningText = warningText,
            Details = details,
            Choices = pendingAction.Choices,
            AcceptedStatusText = _localizationService.Format("ActionCard_Status_Accepted", FormatToolTitle(pendingAction.ToolName, category)),
            DeclinedStatusText = _localizationService.Format("ActionCard_Status_Declined", FormatToolTitle(pendingAction.ToolName, category)),
        };
    }

    private string FormatToolTitle(string toolName, ActionCardCategory category)
    {
        var categoryKey = category switch
        {
            ActionCardCategory.Memory => "ActionCard_Category_Memory",
            ActionCardCategory.Todo => "ActionCard_Category_Todo",
            ActionCardCategory.Reminder => "ActionCard_Category_Reminder",
            ActionCardCategory.Meeting => "ActionCard_Category_Meeting",
            _ => "ActionCard_Category_Memory"
        };

        var actionKey = toolName switch
        {
            "create_object" or "create_todo" or "create_reminder" => "ActionCard_Action_Create",
            "update_object" or "append_to_list" or "update_todo" or "update_reminder" => "ActionCard_Action_Update",
            "delete_object" or "delete_todo" or "delete_reminder" => "ActionCard_Action_Delete",
            "complete_todo" => "ActionCard_Action_Complete",
            "summarize_meeting_transcript" => "ActionCard_Action_Summarize",
            "query_meeting_summaries" => "ActionCard_Action_Query",
            _ => "ActionCard_Action_Create"
        };

        return $"{_localizationService[actionKey]} {_localizationService[categoryKey]}";
    }

    private string DetokenizeForDisplay(string text) =>
        _tokenizationEnabled ? _tokenMapService.Detokenize(text) : text;

    private List<ActionCardDetail> DetokenizeDetails(List<ActionCardDetail> details)
    {
        if (!_tokenizationEnabled) return details;
        return details.Select(d => new ActionCardDetail(d.Label, _tokenMapService.Detokenize(d.Value))).ToList();
    }

    private void ExecuteCancelStreaming()
    {
        _streamingCts?.Cancel();
        CancelPendingActionCards(Messages.LastOrDefault());
    }

    private void ExecuteClearConversation()
    {
        _streamingCts?.Cancel();
        _ttsService.Stop();
        foreach (var msg in Messages)
            CancelPendingActionCards(msg);
        Messages.Clear();
        InputText = string.Empty;
        HasMessages = false;

        if (_tokenizationEnabled)
        {
            _tokenMapService.Clear();
            _ = Task.Run(async () =>
            {
                try { await _tokenMapService.InitializeAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to re-initialize token map after clear"); }
            });
        }
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

    private async Task ExecuteCopyMessage(AssistantMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Content))
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(message.Content);
            _snackbarService.Show(_localizationService["Msg_Assistant_Copied"], _localizationService["Msg_Assistant_MessageCopied"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy message");
        }
    }

    private async Task ExecuteAddPiiKeyword(PiiKeywordRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Keyword))
            return;

        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var exists = settings.Privacy.PiiKeywords.Any(k =>
                string.Equals(k.Keyword, request.Keyword, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                _snackbarService.Show(
                    _localizationService["Msg_PiiKeyword_Exists_Title"],
                    _localizationService.Format("Msg_PiiKeyword_Exists", request.Keyword),
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
                return;
            }

            settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry
            {
                Keyword = request.Keyword,
                Category = request.Category
            });

            await _settingsService.SaveSettingsAsync(settings);

            _snackbarService.Show(
                _localizationService["Msg_PiiKeyword_Added_Title"],
                _localizationService.Format("Msg_PiiKeyword_Added", request.Keyword, request.Category),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add PII keyword");
        }
    }

    private void RandomizeSuggestions()
    {
        SuggestionReminder = _localizationService[SuggestionReminderKeys[RandomNumberGenerator.GetInt32(SuggestionReminderKeys.Length)]];
        SuggestionTodo = _localizationService[SuggestionTodoKeys[RandomNumberGenerator.GetInt32(SuggestionTodoKeys.Length)]];
        SuggestionMemory = _localizationService[SuggestionMemoryKeys[RandomNumberGenerator.GetInt32(SuggestionMemoryKeys.Length)]];
    }

    public async void OnNavigatedTo(object? parameter)
    {
        RandomizeSuggestions();

        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            InputText = text;
        }

        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            IsTtsEnabled = settings.TtsEnabled;

            // Initialize TTS so HasVoiceLoaded becomes true for voice mode button
            if (!_ttsService.HasVoiceLoaded)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _ttsService.InitializeAsync();
                        App.Current.Dispatcher.Invoke(() =>
                            EnterVoiceModeCommand.NotifyCanExecuteChanged());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to initialize TTS on navigation");
                    }
                });
            }

            // Initialize PII tokenization
            _tokenizationEnabled = settings.Privacy.TokenizationEnabled;
            if (_tokenizationEnabled)
            {
                try
                {
                    await _tokenMapService.InitializeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize token map");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load TTS settings");
        }
    }

    public void OnNavigatedFrom() { }

    private void ExecuteToggleTts()
    {
        IsTtsEnabled = !IsTtsEnabled;

        if (!IsTtsEnabled)
        {
            _ttsService.Stop();
        }
        else
        {
            // Initialize TTS on first enable (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ttsService.InitializeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize TTS");
                }
            });
        }

        // Persist setting
        _ = SaveTtsSettingAsync();
    }

    private async Task SaveTtsSettingAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.TtsEnabled = IsTtsEnabled;
            await _settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save TTS setting");
        }
    }

    private async Task ExecutePlayMessage(AssistantMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Content))
            return;

        if (message.IsSpeaking)
        {
            _ttsService.Stop();
            return;
        }

        await SpeakMessageAsync(message);
    }

    private async Task SpeakMessageAsync(AssistantMessage message)
    {
        // Stop any currently speaking message
        foreach (var msg in Messages)
            msg.IsSpeaking = false;
        _ttsService.Stop();

        message.IsSpeaking = true;
        try
        {
            await _ttsService.SpeakAsync(message.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS playback failed");
        }
        finally
        {
            message.IsSpeaking = false;
        }
    }

    private void OnTtsPlayingChanged(object? sender, bool isPlaying)
    {
        IsTtsPlaying = isPlaying;
    }

    private bool CanEnterVoiceMode() =>
        !IsStreaming && !IsVoiceModeActive && _ttsService.HasVoiceLoaded;

    private async Task ExecuteEnterVoiceMode()
    {
        var voiceMode = new VoiceModeViewModel(
            _audioRecordingService,
            _transcriptionService,
            _ttsService,
            _loggerFactory.CreateLogger<VoiceModeViewModel>(),
            StreamVoiceModeResponse,
            AddVoiceModeConversation);

        VoiceMode = voiceMode;
        IsVoiceModeActive = true;

        voiceMode.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceModeViewModel.State) && voiceMode.State == VoiceModeState.Idle)
            {
                IsVoiceModeActive = false;
                VoiceMode = null;
                voiceMode.Dispose();
            }
        };

        await voiceMode.EnterAsync();
    }

    private async IAsyncEnumerable<string> StreamVoiceModeResponse(
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        if (provider is null)
        {
            yield return _localizationService["Msg_Assistant_NoProviderConfigured"];
            yield break;
        }

        var supportsTools = provider.SupportsToolCalling;

        string fullSystemPrompt;
        IList<AITool>? tools;

        if (supportsTools)
        {
            fullSystemPrompt = BuildSystemPrompt(_tokenizationEnabled);
            tools = [.. _pluginService.GetAllTools()];
        }
        else
        {
            fullSystemPrompt = BuildSystemPromptNoTools();
            tools = null;
        }

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, fullSystemPrompt)
        };

        // Include existing conversation history
        foreach (var msg in Messages)
        {
            chatMessages.Add(msg.ToChatMessage());
        }

        chatMessages.Add(new ChatMessage(ChatRole.User, userText));

        var rawBuffer = new StringBuilder();
        var lastVisibleLength = 0;

        await foreach (var token in _aiClientService.GetChatCompletionWithToolsAsync(
            chatMessages, provider, tools,
            supportsTools ? HandleVoiceModeToolCall : null,
            nameof(WindowMode.Assistant),
            cancellationToken))
        {
            rawBuffer.Append(token);
            var (visible, _) = ParseStreamedContent(rawBuffer.ToString());

            // Yield only newly added visible content (strips think tags)
            if (visible.Length > lastVisibleLength)
            {
                var newContent = visible[lastVisibleLength..];
                lastVisibleLength = visible.Length;
                yield return newContent;
            }
        }
    }

    private async Task<object?> HandleVoiceModeToolCall(FunctionCallContent toolCall)
    {
        _logger.LogInformation("Voice mode tool call: {ToolName}", toolCall.Name);

        var routeResult = await _pluginService.RouteToolCallAsync(toolCall);
        if (routeResult is null)
            return $"Unknown tool: {toolCall.Name}";

        var (result, pendingAction) = routeResult.Value;
        if (result is not null)
            return result;

        // Auto-approve write operations in voice mode (no dialog)
        if (pendingAction is not null)
        {
            var actionResult = await pendingAction.Execute(null);

            // Re-scan for new PII after memory write
            if (_tokenizationEnabled && pendingAction.PluginName == "memory")
            {
                try { await _tokenMapService.InitializeAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to re-initialize token map after voice mode memory write"); }
            }

            return actionResult;
        }

        return "Tool call handled.";
    }

    private void AddVoiceModeConversation(string userText, string assistantText)
    {
        Messages.Add(new AssistantMessage(ChatRole.User, userText));
        Messages.Add(new AssistantMessage(ChatRole.Assistant, assistantText));
        HasMessages = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        VoiceMode?.Dispose();
        VoiceMode = null;
        _ttsService.Stop();
        _ttsService.IsPlayingChanged -= OnTtsPlayingChanged;
        LiveTranscription.CloseRequested -= OnLiveTranscriptionCloseRequested;
        LiveTranscription.SummarizeRequested -= OnSummarizeRequested;
        PropertyChanged -= OnPropertyChanged;
        _streamingCts?.Cancel();
        _streamingCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
