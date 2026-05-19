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
using Pia.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.ViewModels;

public partial class AssistantViewModel : ObservableObject, INavigationAware, IDisposable
{
    private static string GetLanguageName(TargetLanguage language) => language switch
    {
        TargetLanguage.DE => "German",
        TargetLanguage.FR => "French",
        _ => "English"
    };

    private string BuildLanguageInstruction()
    {
        var languageName = GetLanguageName(_localizationService.CurrentLanguage);
        return $"Always respond to the user in '{languageName}' unless the user asks you to switch.";
    }

    private string BuildSystemPrompt(bool tokenizationEnabled, bool skipToolSelectionTree = false)
    {
        var pluginPrompts = _pluginService.GetCombinedSystemPromptAdditions();
        var pluginSection = string.IsNullOrWhiteSpace(pluginPrompts)
            ? string.Empty
            : $"## Plugins\n\n{pluginPrompts}\n\n";
        var tokenSection = tokenizationEnabled
            ? "\n## Privacy Tokens\n\nWhen memory or contact data is returned, personal details (names, emails, phones, addresses, dates) are replaced with privacy tokens like [Person_1], [Email_1], etc. Use these tokens naturally in your responses — they will be resolved back to real values before the user sees your message. Never explain or call attention to the tokens. Treat [Person_1] as if it were the person's actual name.\n"
            : string.Empty;

        var toolSelectionSection = skipToolSelectionTree
            ? string.Empty
            : """
              ## Tool Selection

              Follow this decision tree strictly:

              1. Does the request mention a specific TIME, DATE, or SCHEDULE for notification?
                 - YES → Use Reminder tools. NOT a reminder: "Remember I like coffee" (no time = memory).
                 - NO → Continue to step 2.
              2. Does the request involve a TASK, ACTION ITEM, or something to DO?
                 - YES → Use Todo tools. NOT a todo: "Remember my WiFi password" (information = memory).
                 - NO → Continue to step 3.
              3. Does the request involve STORING, RECALLING, or UPDATING personal information?
                 - YES → Use Memory tools (remember: query first, then create/update). NOT a memory: "Remind me at 3 PM to call Bob" (has time = reminder).
                 - NO → Respond conversationally without tools.

              """;

        return $"""
            ## Identity

            You are Pia, a helpful personal assistant. Provide concise, accurate, and friendly responses.
            The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

            ## Language

            {BuildLanguageInstruction()}

            {pluginSection}{toolSelectionSection}## Principles

            - Keep replies short. Default to 1–3 sentences; expand only when the user explicitly asks for detail, steps, or code.
            - Write plain prose. Do not use headings or italics. Avoid bold; reserve **bold** only for safety-critical warnings (e.g. confirming a destructive action).
            - Use bullet lists only for 3+ discrete items. Use code blocks only for code, commands, or file paths.
            - Do not restate the user's question and do not summarize what you just said at the end of a reply.
            - When a user declines a proposed action, do NOT retry the same operation. Instead, acknowledge the decline and ask the user what they would like to do differently or if they want to adjust the details.
            {tokenSection}
            """;
    }

    private static (string CategoryLabel, string QueryTool, IReadOnlyList<string> ToolNames) GetAtCommandToolMapping(Pia.Models.AtCommandDomain domain) => domain switch
    {
        Pia.Models.AtCommandDomain.Memory => (
            "memory entry",
            "query_memory",
            (IReadOnlyList<string>)["query_memory", "list_memories", "create_object", "update_object", "append_to_list", "delete_object"]),
        Pia.Models.AtCommandDomain.Todo => (
            "todo",
            "query_todos",
            (IReadOnlyList<string>)["query_todos", "create_todo", "complete_todo", "update_todo", "delete_todo"]),
        Pia.Models.AtCommandDomain.Reminder => (
            "reminder",
            "query_reminders",
            (IReadOnlyList<string>)["query_reminders", "create_reminder", "update_reminder", "delete_reminder"]),
        Pia.Models.AtCommandDomain.Research => (
            "scheduled research job",
            "query_scheduled_research",
            (IReadOnlyList<string>)["query_scheduled_research", "create_scheduled_research", "update_scheduled_research", "delete_scheduled_research"]),
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain,
            $"No tool mapping registered for at-command domain {domain}. Add a row to GetAtCommandToolMapping.")
    };

    private static IReadOnlySet<string> GetAllowedToolNames(IReadOnlyList<Pia.Models.AtCommand> commands)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in commands)
        {
            foreach (var name in GetAtCommandToolMapping(cmd.Domain).ToolNames)
                allowed.Add(name);
        }
        return allowed;
    }

    private static string BuildAtCommandHint(IReadOnlyList<Pia.Models.AtCommand> commands)
    {
        if (commands.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## User Tool Hints decision");
        sb.AppendLine();
        sb.AppendLine("The user explicitly tagged this request with @-commands. These tags identify the item category and target — they are not ambiguous. Only the tools listed below will be loaded for this turn. Do NOT ask the user to clarify which kind of item they mean. Treat the rest of the user's message as the intended action on the tagged item.");
        sb.AppendLine();
        foreach (var cmd in commands)
        {
            var (categoryLabel, queryTool, toolNames) = GetAtCommandToolMapping(cmd.Domain);
            var toolFamily = $"{categoryLabel} tools ({string.Join(", ", toolNames)})";

            if (cmd.ItemTitle is not null)
                sb.AppendLine($"- The user's request targets a {categoryLabel} titled \"{cmd.ItemTitle}\". Call {queryTool} first to obtain its ID, then perform the action described in the rest of the user's message (e.g. delete, update, complete). Available {toolFamily}.");
            else
                sb.AppendLine($"- The user's request is about {categoryLabel}s — use the {toolFamily}.");
        }
        return sb.ToString();
    }

    private string BuildSystemPromptNoTools() => $"""
        ## Identity

        You are Pia, a helpful personal assistant. Provide concise, accurate, and friendly responses.
        The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

        ## Language

        {BuildLanguageInstruction()}

        ## Principles

        - Keep replies short. Default to 1–3 sentences; expand only when the user explicitly asks for detail, steps, or code.
        - Write plain prose. Use formatting elements rare. Avoid bold, italics; reserve **bold** only for safety-critical warnings.
        - Use bullet lists only for 3+ discrete items. Use code blocks only for code, commands, or file paths.
        - Do not restate the user's question and do not summarize what you just said at the end of a reply.
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
    private readonly INavigationService _navigationService;
    private readonly ISuggestionService _suggestionService;
    private readonly IAssistantChatService _chatService;
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
    public IAsyncRelayCommand<AssistantMessage> RegenerateMessageCommand { get; }
    public IAsyncRelayCommand EnterVoiceModeCommand { get; }
    public IRelayCommand<string> UseSuggestionCommand { get; }
    public IRelayCommand<string> UseFollowupCommand { get; }
    public IAsyncRelayCommand<PiiKeywordRequest> AddPiiKeywordCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<string>> HandleFilesDroppedCommand { get; }

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
        INavigationService navigationService,
        ISuggestionService suggestionService,
        IAssistantChatService chatService)
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
        _navigationService = navigationService;
        _suggestionService = suggestionService;
        _chatService = chatService;

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

        _ttsService.IsPlayingChanged += OnTtsPlayingChanged;
        PropertyChanged += OnPropertyChanged;

        ChatTitleChip = new ChatTitleChipViewModel(
            _chatService,
            _localizationService,
            _loggerFactory.CreateLogger<ChatTitleChipViewModel>(),
            ResumeChatAsync,
            NewChat,
            NavigateToAssistantHistory);
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

    private void ExecuteUseFollowup(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion) || IsStreaming) return;
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

            _currentChatProviderId = provider.Id;

            // Determine if this provider supports tool calling
            var supportsTools = provider.SupportsToolCalling;

            // Build system prompt with memory context
            string fullSystemPrompt;
            IList<AITool>? tools;

            if (supportsTools)
            {
                var hasAtCommands = atCommands.Count > 0;
                fullSystemPrompt = BuildSystemPrompt(_tokenizationEnabled, skipToolSelectionTree: hasAtCommands)
                    + BuildAtCommandHint(atCommands);

                var allTools = _pluginService.GetAllTools();
                if (hasAtCommands)
                {
                    var allowed = GetAllowedToolNames(atCommands);
                    tools = [.. allTools.Where(t => allowed.Contains(t.Name))];
                }
                else
                {
                    tools = [.. allTools];
                }
            }
            else
            {
                fullSystemPrompt = BuildSystemPromptNoTools();
                tools = null;
            }

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
                        var (visible, thinking) = ParseStreamedContent(rawBuffer.ToString());

                        assistantMessage.Content = visible;
                        if (!string.IsNullOrEmpty(thinking))
                            assistantMessage.ThinkingContent = thinking;
                        break;

                    case Finished finished:
                        ApplyStats(assistantMessage, finished, provider);
                        break;
                }
            }

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
            Messages = [.. Messages.Select(MapToDto)],
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
            var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
            if (provider is null)
            {
                _logger.LogWarning("Auto-title skipped for chat {ChatId}: no provider", chatId);
                return;
            }

            const int snippetMax = 1000;
            var userSnippet = firstUserContent.Length > snippetMax ? firstUserContent[..snippetMax] : firstUserContent;
            var assistantSnippet = firstAssistantContent.Length > snippetMax ? firstAssistantContent[..snippetMax] : firstAssistantContent;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You write very short chat titles (3-8 words, no quotes, no trailing punctuation). Respond with only the title."),
                new(ChatRole.User,
                    $"Summarize this conversation in 3-8 words:\nUser: {userSnippet}\nAssistant: {assistantSnippet}"),
            };

            _logger.SensitiveDebug("Auto-title prompt for chat {ChatId}: user={User} assistant={Assistant}",
                chatId, userSnippet, assistantSnippet);

            var response = await _aiClientService.GetChatResponseAsync(
                messages, provider, tools: null, mode: nameof(WindowMode.Assistant));

            var rawTitle = response.Text ?? string.Empty;
            var title = SanitizeGeneratedTitle(rawTitle);
            if (string.IsNullOrEmpty(title))
            {
                _logger.LogWarning("Auto-title generation returned empty title for chat {ChatId}", chatId);
                return;
            }

            _logger.SensitiveDebug("Auto-title result for chat {ChatId}: {Title}", chatId, title);

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

    private static string SanitizeGeneratedTitle(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return text;

        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
        {
            text = text[1..^1].Trim();
        }

        text = text.TrimEnd('.', '!', '?').TrimEnd();

        const int max = 80;
        if (text.Length > max) text = text[..max].TrimEnd() + "…";
        return CollapseWhitespace(text);
    }

    private string? DeriveChatTitle()
    {
        var firstUser = Messages.FirstOrDefault(m => m.IsUser);
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content)) return null;

        var collapsed = CollapseWhitespace(firstUser.Content);
        const int max = 40;
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    private static SyncAssistantChatMessage MapToDto(AssistantMessage m) => new()
    {
        Id = m.Id,
        Role = m.IsUser ? "user" : "assistant",
        Content = m.Content,
        ThinkingContent = string.IsNullOrEmpty(m.ThinkingContent) ? null : m.ThinkingContent,
        Timestamp = m.Timestamp.ToUniversalTime(),
        Tokens = m.Stats?.Tokens,
        ModelName = m.Stats?.Model,
    };

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
            var card = BuildPluginActionCard(pendingAction);
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

                var snackbarTitle = pendingAction.PluginName switch
                {
                    "memory" => _localizationService["Msg_Assistant_MemoryUpdated"],
                    "todo" => _localizationService["Msg_Assistant_TodoUpdated"],
                    "reminder" => _localizationService["Msg_Assistant_ReminderUpdated"],
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
            "files" => ActionCardCategory.Files,
            _ => ActionCardCategory.Memory
        };

        var isDelete = pendingAction.ToolName.Contains("delete");

        var warningText = isDelete ? pendingAction.PluginName switch
        {
            "memory" => _localizationService["Msg_Assistant_PermanentDeleteMemory"],
            "todo" => _localizationService["Msg_Assistant_PermanentDeleteTodo"],
            "reminder" => _localizationService["Msg_Assistant_PermanentDeleteReminder"],
            "files" => _localizationService["Msg_Assistant_PermanentDeleteFile"],
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
            ActionCardCategory.Files => "ActionCard_Category_File",
            _ => "ActionCard_Category_Memory"
        };

        var actionKey = toolName switch
        {
            "create_object" or "create_todo" or "create_reminder" => "ActionCard_Action_Create",
            "update_object" or "append_to_list" or "update_todo" or "update_reminder" => "ActionCard_Action_Update",
            "delete_object" or "delete_todo" or "delete_reminder" or "delete_file" => "ActionCard_Action_Delete",
            "complete_todo" => "ActionCard_Action_Complete",
            "write_file" => "ActionCard_Action_Write",
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
            Messages.Add(MapFromDto(dto));

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

    private static AssistantMessage MapFromDto(SyncAssistantChatMessage dto)
    {
        var role = dto.Role == "user" ? ChatRole.User : ChatRole.Assistant;
        var message = new AssistantMessage(dto.Id, role, dto.Content, dto.Timestamp.ToLocalTime());
        if (!string.IsNullOrEmpty(dto.ThinkingContent))
            message.ThinkingContent = dto.ThinkingContent;
        if (dto.Tokens is { } tokens && !string.IsNullOrEmpty(dto.ModelName))
            message.Stats = new AnswerStats(tokens, dto.ModelName);
        return message;
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
        if (prior.Role != ChatRole.User || string.IsNullOrWhiteSpace(prior.Content)) return;

        CancelPendingActionCards(message);

        var prompt = prior.Content;
        for (var i = Messages.Count - 1; i >= idx - 1; i--)
            Messages.RemoveAt(i);

        if (Messages.Count == 0) HasMessages = false;

        InputText = prompt;
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

        var text = await DroppedFileImporter.TryImportAsync(
            paths, _logger, _snackbarService, _localizationService);
        if (text is not null)
            InsertOrPromptInsertAnyway(text);
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

        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            chatMessages, provider, tools,
            supportsTools ? HandleVoiceModeToolCall : null,
            nameof(WindowMode.Assistant),
            cancellationToken))
        {
            if (item is not TextDelta td)
                continue;

            rawBuffer.Append(td.Text);
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
        PropertyChanged -= OnPropertyChanged;
        _streamingCts?.Cancel();
        _streamingCts?.Dispose();
        ChatTitleChip.Dispose();

        GC.SuppressFinalize(this);
    }
}
