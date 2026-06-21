using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Imaging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.ViewModels;

public partial class AssistantViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly ILogger<AssistantViewModel> _logger;
    private readonly IAiClientService _aiClientService;
    private readonly IProviderService _providerService;
    private readonly IPersonaService _personaService;
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
    private readonly INavigationService _navigationService;
    private readonly ISuggestionService _suggestionService;
    private readonly IAssistantChatService _chatService;
    private readonly IAssistantPromptComposer _promptComposer;
    private readonly IChatTitleService _chatTitleService;
    private readonly IActionCardBuilder _actionCardBuilder;
    private CancellationTokenSource? _streamingCts;
    private bool _disposed;
    private bool _tokenizationEnabled;
    private bool _suggestionsEnabled = true;
    private bool _autoTitleEnabled;

    private Guid? _currentChatId;
    private DateTime _currentChatCreatedAt;
    private Guid? _currentChatProviderId;
    private bool _autoTitleApplied;

    public ChatTitleChipViewModel ChatTitleChip { get; }

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private ImageAttachment? _pendingAttachment;

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
    private string _suggestionReminder = string.Empty;

    [ObservableProperty]
    private string _suggestionTodo = string.Empty;

    [ObservableProperty]
    private string _suggestionMemory = string.Empty;

    /// <summary>The persona shown in the picker chip. Changing it persists the per-mode selection
    /// (synced via SyncSettings); the new persona applies from the next turn.</summary>
    [ObservableProperty]
    private Persona? _activePersona;

    private bool _isLoadingPersonas;

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

    public ObservableCollection<Persona> AvailablePersonas { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand CancelStreamingCommand { get; }
    public IRelayCommand ClearConversationCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> CopyMessageCommand { get; }
    public IRelayCommand ToggleTtsCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> PlayMessageCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> RegenerateMessageCommand { get; }
    public IAsyncRelayCommand EnterVoiceModeCommand { get; }
    public IRelayCommand<string> UseSuggestionCommand { get; }
    public IRelayCommand<string> UseFollowupCommand { get; }
    public IAsyncRelayCommand<PiiKeywordRequest> AddPiiKeywordCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<string>> HandleFilesDroppedCommand { get; }
    public IAsyncRelayCommand<string> HandleImageAttachedCommand { get; }
    public IAsyncRelayCommand<BitmapSource> HandleImagePastedCommand { get; }
    public IRelayCommand RemoveAttachmentCommand { get; }

    public AssistantViewModel(
        ILogger<AssistantViewModel> logger,
        IAiClientService aiClientService,
        IProviderService providerService,
        IPersonaService personaService,
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
        INavigationService navigationService,
        ISuggestionService suggestionService,
        IAssistantChatService chatService,
        IAssistantPromptComposer promptComposer,
        IChatTitleService chatTitleService,
        IActionCardBuilder actionCardBuilder)
    {
        _logger = logger;
        _aiClientService = aiClientService;
        _providerService = providerService;
        _personaService = personaService;
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
        _navigationService = navigationService;
        _suggestionService = suggestionService;
        _chatService = chatService;
        _promptComposer = promptComposer;
        _chatTitleService = chatTitleService;
        _actionCardBuilder = actionCardBuilder;

        SendMessageCommand = new AsyncRelayCommand(ExecuteSendMessage, CanExecuteSendMessage);
        ToggleRecordingCommand = new AsyncRelayCommand(ExecuteToggleRecording);
        CancelStreamingCommand = new RelayCommand(ExecuteCancelStreaming);
        ClearConversationCommand = new RelayCommand(ExecuteClearConversation);
        CopyMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteCopyMessage);
        ToggleTtsCommand = new RelayCommand(ExecuteToggleTts);
        PlayMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecutePlayMessage, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RegenerateMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteRegenerateMessage);
        EnterVoiceModeCommand = new AsyncRelayCommand(ExecuteEnterVoiceMode, CanEnterVoiceMode);
        UseSuggestionCommand = new RelayCommand<string>(ExecuteUseSuggestion);
        UseFollowupCommand = new RelayCommand<string>(ExecuteUseFollowup);
        AddPiiKeywordCommand = new AsyncRelayCommand<PiiKeywordRequest>(ExecuteAddPiiKeyword);
        HandleFilesDroppedCommand = new AsyncRelayCommand<IReadOnlyList<string>>(ExecuteHandleFilesDropped);
        HandleImageAttachedCommand = new AsyncRelayCommand<string>(ExecuteHandleImageAttached);
        HandleImagePastedCommand = new AsyncRelayCommand<BitmapSource>(ExecuteHandleImagePasted);
        RemoveAttachmentCommand = new RelayCommand(() => PendingAttachment = null);

        _ttsService.IsPlayingChanged += OnTtsPlayingChanged;
        _personaService.PersonasChanged += OnPersonasChanged;
        PropertyChanged += OnPropertyChanged;

        ChatTitleChip = new ChatTitleChipViewModel(
            _chatService,
            _localizationService,
            _loggerFactory.CreateLogger<ChatTitleChipViewModel>(),
            ResumeChatAsync,
            NewChat,
            NavigateToAssistantHistory);
    }

    private void OnPersonasChanged(object? sender, EventArgs e) =>
        LoadPersonasAsync().SafeFireAndForget(_logger);

    private async Task LoadPersonasAsync()
    {
        try
        {
            _isLoadingPersonas = true;
            var settings = await _settingsService.GetSettingsAsync();
            var personas = await _personaService.GetPersonasAsync();
            var active = await _personaService.ResolveActiveAsync(WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal);

            AvailablePersonas.Clear();
            foreach (var persona in personas)
                AvailablePersonas.Add(persona);

            ActivePersona = AvailablePersonas.FirstOrDefault(p => p.Id == active.Id) ?? active;
        }
        finally
        {
            _isLoadingPersonas = false;
        }
    }

    partial void OnActivePersonaChanged(Persona? value)
    {
        if (_isLoadingPersonas || value is null)
            return;
        PersistActivePersonaAsync(value.Id).SafeFireAndForget(_logger);
    }

    private async Task PersistActivePersonaAsync(Guid personaId)
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.SetPersonaForMode(WindowMode.Assistant, personaId);
        await _settingsService.SaveSettingsAsync(settings);
        _logger.LogInformation("Active persona for Assistant set to {PersonaId}", personaId);
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming) or nameof(PendingAttachment))
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

    private void ExecuteUseFollowup(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion) || IsStreaming) return;
        InputText = suggestion;
    }

    private bool CanExecuteSendMessage() =>
        !IsStreaming && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachment is not null);

    private async Task ExecuteSendMessage()
    {
        var userText = InputText.Trim();
        InputText = string.Empty;

        // Parse @-commands — keep full text for display (highlighted by view),
        // but strip commands from what the AI sees as the user message
        var atCommands = Pia.Services.AtCommandParser.ExtractAllCommands(userText);

        var userMessage = new AssistantMessage(ChatRole.User, userText)
        {
            Attachment = PendingAttachment
        };
        PendingAttachment = null;
        Messages.Add(userMessage);
        HasMessages = true;

        var assistantMessage = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        Messages.Add(assistantMessage);

        _streamingCts = new CancellationTokenSource();
        IsStreaming = true;

        try
        {
            // Resolve the active persona once for this turn (never null).
            var settings = await _settingsService.GetSettingsAsync();
            var persona = await _personaService.ResolveActiveAsync(WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal);
            assistantMessage.Persona = PersonaAttribution.From(persona);

            // Provider override (contract §6): the persona's PreferredProviderId wins when it resolves
            // to a usable provider; otherwise fall back to the Assistant-mode default.
            var provider = persona.PreferredProviderId.HasValue
                ? await _providerService.GetProviderAsync(persona.PreferredProviderId.Value)
                : null;
            provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
            if (provider is null)
            {
                assistantMessage.Content = _localizationService["Msg_Assistant_NoProviderInline"];
                assistantMessage.IsStreaming = false;
                IsStreaming = false;
                _snackbarService.Show(_localizationService["Msg_Error"], _localizationService["Msg_Assistant_NoProviderConfigured"], Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
                return;
            }

            // Reasoning-effort override (contract §6): apply on a shallow copy so the stored provider
            // is never mutated.
            if (persona.ReasoningEffort.HasValue)
            {
                provider = provider.Clone();
                provider.ReasoningEffort = persona.ReasoningEffort.Value;
            }

            _currentChatProviderId = provider.Id;

            _logger.LogInformation("SendMessage: resolved persona {PersonaId} (ToolScope {ToolScope})", persona.Id, persona.ToolScope);

            // Resolve the system prompt + tool set for this turn (gating, @-command hints,
            // privacy-token/web-search sections) — see AssistantPromptComposer.
            var turnSetup = _promptComposer.PrepareTurn(persona, provider, atCommands, _tokenizationEnabled);
            var supportsTools = turnSetup.SupportsTools;
            var webSearchActive = turnSetup.WebSearchActive;
            var fullSystemPrompt = turnSetup.SystemPrompt;
            var tools = turnSetup.Tools;

            _logger.LogInformation("SendMessage: provider={ProviderName}, supportsTools={SupportsTools}, toolCount={ToolCount}, atCommandCount={AtCommandCount}",
                provider.Name, supportsTools, tools?.Count ?? 0, atCommands.Count);

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

            await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
                chatMessages, provider, tools,
                supportsTools ? toolCall => HandleToolCallWithStatus(toolCall, assistantMessage) : null,
                nameof(WindowMode.Assistant),
                _streamingCts.Token))
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

            await GenerateFollowupsAsync(provider, userMessage.Content, assistantMessage, _streamingCts.Token);
        }
        catch (Pia.Services.Exceptions.LlmTimeoutException ex)
        {
            _logger.LogError(ex, "AI response timed out (provider={ProviderName}, seconds={Seconds})", ex.ProviderName, ex.TimeoutSeconds);
            var localizedMessage = _localizationService.Format("Msg_Assistant_ResponseTimedOut", ex.ProviderName, ex.TimeoutSeconds);
            if (string.IsNullOrEmpty(assistantMessage.Content))
            {
                assistantMessage.Content = localizedMessage;
            }
            _snackbarService.Show(_localizationService["Msg_Error"], localizedMessage, Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
        catch (Pia.Services.Exceptions.LlmTruncatedException ex)
        {
            _logger.LogWarning(ex, "AI response truncated by token cap (provider={ProviderName}, partialChars={PartialChars})", ex.ProviderName, ex.PartialLength);
            var localizedMessage = _localizationService.Format("Msg_Assistant_ResponseTruncated", ex.ProviderName);
            // Preserve any partial visible text so the user sees how far the model got;
            // append the hint underneath. If we have no text yet, show the hint alone.
            assistantMessage.Content = string.IsNullOrEmpty(assistantMessage.Content)
                ? localizedMessage
                : assistantMessage.Content + "\n\n" + localizedMessage;
            _snackbarService.Show(_localizationService["Msg_Warning"], localizedMessage, Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
        }
        catch (OperationCanceledException)
        {
            _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Assistant_ResponseCancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex) when (ex.Message.Contains("EnableVision is false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Provider rejected image attachment (vision disabled)");
            var localized = _localizationService["Msg_Assistant_ProviderNoVision"];
            assistantMessage.Content = localized;
            Messages.Remove(assistantMessage);
            Messages.Remove(userMessage);
            HasMessages = Messages.Count > 0;
            InputText = userMessage.Content;
            _snackbarService.Show(
                _localizationService["Msg_Warning"], localized,
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(5));
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

            await PersistCurrentChatAsync();
        }
    }

    private async Task PersistCurrentChatAsync()
    {
        if (Messages.Count == 0) return;

        var nowUtc = DateTime.UtcNow;

        if (_currentChatId is null)
        {
            _currentChatId = Guid.NewGuid();
            _currentChatCreatedAt = nowUtc;
        }

        var chat = new SyncAssistantChat
        {
            Id = _currentChatId.Value,
            SchemaVersion = 1,
            Title = DeriveChatTitle(),
            CreatedAt = _currentChatCreatedAt,
            UpdatedAt = nowUtc,
            LastAccessedAt = nowUtc,
            WindowMode = WindowMode.Assistant.ToString(),
            ProviderId = _currentChatProviderId,
            Messages = [.. Messages.Select(AssistantMessageMapper.ToDto)],
        };

        try
        {
            await _chatService.SaveAsync(chat);
            ChatTitleChip.SetTitle(chat.Title);
            _logger.LogInformation("Persisted assistant chat {ChatId} ({MessageCount} messages)",
                chat.Id, chat.Messages.Count);
            _logger.SensitiveDebug("Chat {ChatId} title: {Title}", chat.Id, chat.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist assistant chat {ChatId}", chat.Id);
        }

        TryStartAutoTitle(chat);
    }

    // Fire-and-forget: replaces the dumb fallback title with a model-generated
    // one once the first assistant reply is in. Guarded so it runs at most
    // once per chat session — the second SaveAsync inside RenameChatAsync would
    // otherwise loop us back through here.
    private void TryStartAutoTitle(SyncAssistantChat chat)
    {
        if (!_autoTitleEnabled || _autoTitleApplied) return;

        var userCount = chat.Messages.Count(m => m.Role == "user");
        var assistantReply = chat.Messages.FirstOrDefault(m => m.Role == "assistant"
            && !string.IsNullOrWhiteSpace(m.Content));
        if (userCount != 1 || assistantReply is null) return;

        var firstUser = chat.Messages.FirstOrDefault(m => m.Role == "user");
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content)) return;

        _autoTitleApplied = true;
        _logger.LogInformation("Auto-title generation starting for chat {ChatId}", chat.Id);
        _ = RenameChatAsync(chat.Id, firstUser.Content, assistantReply.Content);
    }

    private async Task RenameChatAsync(Guid chatId, string firstUserContent, string firstAssistantContent)
    {
        try
        {
            var title = await _chatTitleService.GenerateAsync(firstUserContent, firstAssistantContent);
            if (string.IsNullOrEmpty(title))
                return; // ChatTitleService logged the reason (no provider / empty model output)

            var existing = await _chatService.GetAsync(chatId);
            if (existing is null)
            {
                _logger.LogWarning("Auto-title: chat {ChatId} disappeared before rename", chatId);
                return;
            }

            existing.Title = title;
            existing.UpdatedAt = DateTime.UtcNow;
            await _chatService.SaveAsync(existing);

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _currentChatId != chatId) return;
                ChatTitleChip.SetTitle(title);
            });

            _logger.LogInformation("Auto-title applied for chat {ChatId}", chatId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-title generation failed for chat {ChatId}", chatId);
        }
    }

    private string? DeriveChatTitle()
    {
        var firstUser = Messages.FirstOrDefault(m => m.IsUser);
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content)) return null;

        var collapsed = TextFormatting.CollapseWhitespace(firstUser.Content);
        const int max = 40;
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
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

    private async Task<object?> HandleToolCallWithStatus(FunctionCallContent toolCall, AssistantMessage message)
    {
        message.StatusText = _actionCardBuilder.ResolveStatusText(toolCall.Name);

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
            _logger.SensitiveDebug("Tool {ToolName} result ({Length} chars): {Preview}",
                toolCall.Name,
                resultStr.Length,
                resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr);
        }

        if (result is not null)
            return result;

        // For write operations, show inline action card
        if (pendingAction is not null)
        {
            var card = _actionCardBuilder.Build(pendingAction, _tokenizationEnabled);
            await App.Current.Dispatcher.InvokeAsync(() => message.ActionCards.Add(card));

            bool confirmed;
            try
            {
                confirmed = await card.WaitForUserDecisionAsync();
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Tool action cancelled for {ToolName}", pendingAction.ToolName);
                confirmed = false;
            }

            if (confirmed)
            {
                _logger.LogInformation("User accepted {ToolName} action", pendingAction.ToolName);
                var actionResult = await pendingAction.Execute();
                _logger.LogInformation("Executed {ToolName} action successfully", pendingAction.ToolName);

                var snackbarTitle = _actionCardBuilder.ResolveSuccessTitle(pendingAction.PluginName);
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

    private string DetokenizeForDisplay(string text) =>
        _tokenizationEnabled ? _tokenMapService.Detokenize(text) : text;

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

        _currentChatId = null;
        _currentChatCreatedAt = default;
        _currentChatProviderId = null;
        _autoTitleApplied = false;
        ChatTitleChip.SetTitle(null);

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

    private void NewChat() => ExecuteClearConversation();

    private async Task ResumeChatAsync(Guid chatId)
    {
        _streamingCts?.Cancel();
        _ttsService.Stop();

        SyncAssistantChat? chat;
        try
        {
            chat = await _chatService.GetAsync(chatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load chat {ChatId}", chatId);
            return;
        }

        if (chat is null)
        {
            _logger.LogWarning("Chat {ChatId} not found during resume", chatId);
            return;
        }

        foreach (var msg in Messages)
            CancelPendingActionCards(msg);
        Messages.Clear();

        foreach (var dto in chat.Messages)
            Messages.Add(AssistantMessageMapper.FromDto(dto));

        HasMessages = Messages.Count > 0;
        _currentChatId = chat.Id;
        _currentChatCreatedAt = chat.CreatedAt;
        _currentChatProviderId = chat.ProviderId;
        _autoTitleApplied = true;
        ChatTitleChip.SetTitle(chat.Title);

        _logger.LogInformation("Resumed chat {ChatId} ({MessageCount} messages)", chat.Id, chat.Messages.Count);
        _logger.SensitiveDebug("Resumed chat {ChatId} title: {Title}", chat.Id, chat.Title);

        try
        {
            await _chatService.TouchLastAccessedAsync(chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to touch LastAccessedAt for {ChatId}", chat.Id);
        }
    }

    private void NavigateToAssistantHistory()
    {
        _navigationService.NavigateTo<AssistantHistoryViewModel>();
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

    private async Task ExecuteRegenerateMessage(AssistantMessage? message)
    {
        if (message is null || IsStreaming) return;

        var idx = Messages.IndexOf(message);
        if (idx <= 0) return;

        var prior = Messages[idx - 1];
        if (prior.Role != ChatRole.User) return;
        if (string.IsNullOrWhiteSpace(prior.Content) && prior.Attachment is null) return;

        CancelPendingActionCards(message);

        var prompt = prior.Content;
        var attachment = prior.Attachment;
        for (var i = Messages.Count - 1; i >= idx - 1; i--)
            Messages.RemoveAt(i);

        if (Messages.Count == 0) HasMessages = false;

        InputText = prompt;
        PendingAttachment = attachment;
        await SendMessageCommand.ExecuteAsync(null);
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

        if (parameter is Guid chatId && chatId != Guid.Empty)
        {
            await ResumeChatAsync(chatId);
        }
        else if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            InputText = text;
        }
        else if (parameter is CapturedSelectionPayload selection)
        {
            ApplyCapturedSelection(selection.Text);
        }

        try
        {
            await LoadPersonasAsync();

            var settings = await _settingsService.GetSettingsAsync();
            IsTtsEnabled = settings.TtsEnabled;
            _suggestionsEnabled = settings.AssistantSuggestionsEnabled;
            _autoTitleEnabled = settings.ChatAutoTitleEnabled;
            _logger.LogInformation("Assistant settings loaded: autoTitleEnabled={AutoTitleEnabled}", _autoTitleEnabled);

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

    private void ApplyCapturedSelection(string text)
    {
        if (string.IsNullOrEmpty(InputText))
        {
            InputText = text;
        }
        else
        {
            _snackbarService.Show(
                _localizationService["Msg_Warning"],
                _localizationService["Msg_SelectionNotPastedInputNotEmpty"],
                Wpf.Ui.Controls.ControlAppearance.Caution,
                null,
                TimeSpan.FromSeconds(3));
        }
    }

    private async Task ExecuteHandleFilesDropped(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return;
        if (IsStreaming) return;

        if (paths.Count == 1 && DroppedFileReader.Classify(paths[0]) == FileKind.Image)
        {
            await ExecuteHandleImageAttached(paths[0]);
            return;
        }

        var text = await DroppedFileImporter.TryImportAsync(
            paths, _logger, _snackbarService, _localizationService);
        if (text is not null)
            InsertOrPromptInsertAnyway(text);
    }

    private async Task ExecuteHandleImageAttached(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        await PrepareImageAttachmentAsync(() => ImageAttachmentProcessor.TryPrepare(filePath, _logger));
    }

    private async Task ExecuteHandleImagePasted(BitmapSource? source)
    {
        if (source is null) return;
        if (IsStreaming) return;

        // Freeze so the bitmap can be encoded on the background thread below without a
        // cross-thread access exception (Clipboard.GetImage runs on the UI thread).
        if (source.CanFreeze && !source.IsFrozen) source.Freeze();

        await PrepareImageAttachmentAsync(() => ImageAttachmentProcessor.TryPrepare(source, _logger));
    }

    private async Task PrepareImageAttachmentAsync(Func<ImageAttachment?> prepare)
    {
        var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        if (provider?.ProviderType != AiProviderType.PiaCloud)
        {
            _snackbarService.Show(
                _localizationService["Msg_Warning"],
                _localizationService["Msg_File_ImageProviderUnsupported"],
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
            return;
        }

        var attachment = await Task.Run(prepare);
        if (attachment is null)
        {
            _snackbarService.Show(
                _localizationService["Msg_Warning"],
                _localizationService["Msg_File_ImageTooLarge"],
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
            return;
        }

        PendingAttachment = attachment;
    }

    private void InsertOrPromptInsertAnyway(string text)
    {
        if (string.IsNullOrEmpty(InputText))
        {
            InputText = text;
            SendMessageCommand.NotifyCanExecuteChanged();
            return;
        }

        SnackbarActionHelper.ShowWithAction(
            _snackbarService,
            _localizationService["Msg_Warning"],
            _localizationService["Msg_SelectionNotPastedInputNotEmpty"],
            _localizationService["Msg_SelectionNotPasted_InsertAnyway"],
            () =>
            {
                InputText = text;
                SendMessageCommand.NotifyCanExecuteChanged();
            },
            Wpf.Ui.Controls.ControlAppearance.Caution,
            TimeSpan.FromSeconds(8));
    }

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

        if (!_ttsService.HasVoiceLoaded)
        {
            ShowNoVoiceLoadedSnackbar();
            return;
        }

        await SpeakMessageAsync(message);
    }

    private void ShowNoVoiceLoadedSnackbar()
    {
        // General tab (outer 4) → Speech inner tab (inner 2) hosts the TTS voice selection UI.
        SnackbarActionHelper.ShowWithAction(
            _snackbarService,
            _localizationService["Msg_Warning"],
            _localizationService["Msg_Tts_NoVoiceLoaded"],
            _localizationService["Msg_Tts_NoVoiceLoaded_OpenSettings"],
            () => _navigationService.NavigateTo<SettingsViewModel, (int, int)>((4, 2)),
            Wpf.Ui.Controls.ControlAppearance.Caution,
            TimeSpan.FromSeconds(8));
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

    /// <summary>Persona resolved for the most recent voice-mode turn, used to attribute the stored reply.</summary>
    private Persona? _lastVoiceModePersona;

    private async IAsyncEnumerable<string> StreamVoiceModeResponse(
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var persona = await _personaService.ResolveActiveAsync(WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal);
        _lastVoiceModePersona = persona;

        var provider = persona.PreferredProviderId.HasValue
            ? await _providerService.GetProviderAsync(persona.PreferredProviderId.Value)
            : null;
        provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        if (provider is null)
        {
            yield return _localizationService["Msg_Assistant_NoProviderConfigured"];
            yield break;
        }

        if (persona.ReasoningEffort.HasValue)
        {
            provider = provider.Clone();
            provider.ReasoningEffort = persona.ReasoningEffort.Value;
        }

        var turnSetup = _promptComposer.PrepareTurn(persona, provider, Array.Empty<AtCommand>(), _tokenizationEnabled);
        var supportsTools = turnSetup.SupportsTools;
        var fullSystemPrompt = turnSetup.SystemPrompt;
        var tools = turnSetup.Tools;

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

        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            chatMessages, provider, tools,
            supportsTools ? HandleVoiceModeToolCall : null,
            nameof(WindowMode.Assistant),
            cancellationToken))
        {
            if (item is not TextDelta td)
                continue;

            rawBuffer.Append(td.Text);
            var (visible, _) = StreamThinkTagParser.Parse(rawBuffer.ToString());

            // Yield only newly added visible content (strips think tags)
            if (visible.Length > lastVisibleLength)
            {
                var newContent = visible[lastVisibleLength..];
                lastVisibleLength = visible.Length;
                yield return newContent;
            }
        }
    }

    private async Task GenerateFollowupsAsync(
        AiProvider provider,
        string userText,
        AssistantMessage assistantMessage,
        CancellationToken cancellationToken)
    {
        if (!_suggestionsEnabled)
        {
            _logger.LogDebug("Follow-up suggestions skipped: disabled in settings");
            return;
        }
        if (!provider.SupportsStreaming)
        {
            _logger.LogDebug("Follow-up suggestions skipped: provider {ProviderName} does not support streaming", provider.Name);
            return;
        }
        if (string.IsNullOrWhiteSpace(assistantMessage.Content))
        {
            _logger.LogDebug("Follow-up suggestions skipped: assistant message has no content");
            return;
        }
        if (assistantMessage.Suggestions.Count > 0) return;

        _logger.LogInformation("Generating follow-up suggestions for provider {ProviderName}", provider.Name);

        IReadOnlyList<string> picks;
        try
        {
            picks = await _suggestionService.SuggestFollowupsAsync(
                provider, userText, assistantMessage.Content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Follow-up suggestion generation failed");
            return;
        }

        if (picks.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Follow-up suggestions: no picks ({Count}) or cancelled ({Cancelled})",
                picks.Count, cancellationToken.IsCancellationRequested);
            return;
        }

        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var s in picks)
                assistantMessage.Suggestions.Add(s);
        });
        _logger.LogInformation("Follow-up suggestions: added {Count} picks (HasSuggestions={Has})",
            picks.Count, assistantMessage.HasSuggestions);
    }

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
            var actionResult = await pendingAction.Execute();

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
        var assistantMessage = new AssistantMessage(ChatRole.Assistant, assistantText);
        if (_lastVoiceModePersona is { } persona)
            assistantMessage.Persona = PersonaAttribution.From(persona);

        Messages.Add(new AssistantMessage(ChatRole.User, userText));
        Messages.Add(assistantMessage);
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
        _personaService.PersonasChanged -= OnPersonasChanged;
        PropertyChanged -= OnPropertyChanged;
        _streamingCts?.Cancel();
        _streamingCts?.Dispose();
        ChatTitleChip.Dispose();

        GC.SuppressFinalize(this);
    }
}
