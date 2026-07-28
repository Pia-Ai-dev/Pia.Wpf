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
using Pia.ViewModels.Models;

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
    private readonly IProviderCapabilityService _providerCapabilityService;
    private readonly IAgentRunService _agentRunService;
    private readonly IAgentRunResumeService _resumeService;
    private readonly IChatSessionManager _chatSessionManager;
    private readonly IWorkingDirectoryService _workingDirectoryService;
    private readonly IFilesToolHandler _filesToolHandler;
    private readonly IMarkdownExportService _markdownExportService;
    private readonly IDialogService _dialogService;
    private bool _disposed;
    private bool _tokenizationEnabled;
    private bool _suggestionsEnabled = true;

    /// <summary>The session whose events this VM is currently subscribed to.</summary>
    private ChatSession? _subscribedSession;

    public ChatTitleChipViewModel ChatTitleChip { get; }

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private ImageAttachment? _pendingAttachment;

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>
    /// W2: a run attached to this chat is executing under an executor this session does not own (headlessly),
    /// so it is a second full-chat writer and Send must stay disabled until it stops. No explanatory string in
    /// this batch on purpose: the embedded <see cref="ActiveRunProgress"/> panel is already on screen for the
    /// re-attached run and already shows it executing.
    /// </summary>
    [ObservableProperty]
    private bool _foreignRunActive;

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

    public MeetingAttendeeViewModel MeetingAttendee { get; }

    [ObservableProperty]
    private bool _isMeetingAttendeeVisible;

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

    /// <summary>The Chat/Agent lever state (R15). false = Chat, true = Agent (Planned run). Persisted
    /// as a global last-used default in <see cref="AppSettings.AssistantAgentModeDefault"/>.</summary>
    [ObservableProperty]
    private bool _agentModeEnabled;

    /// <summary>Guards the settings-seed of <see cref="AgentModeEnabled"/> so seeding never re-persists
    /// (mirrors <see cref="_isLoadingPersonas"/> for the persona seed).</summary>
    private bool _isLoadingAgentMode;

    /// <summary>The run-progress view-model for the active session's live/selected run (§15.1); null when
    /// the active chat has no run to surface. New'd on the UI thread, disposed on session swap (not DI'd).</summary>
    private RunProgressViewModel? _runProgress;

    [ObservableProperty]
    private RunProgressViewModel? _activeRunProgress;

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

    /// <summary>Points at the active session's message list; re-pointed on active-session swap.</summary>
    [ObservableProperty]
    private ObservableCollection<AssistantMessage> _messages = new();

    /// <summary>Proxied from the active session's <see cref="ChatState"/> (drives the chip badge).</summary>
    [ObservableProperty]
    private ChatState _activeState = ChatState.Idle;

    public ObservableCollection<Persona> AvailablePersonas { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }
    public IAsyncRelayCommand RunInBackgroundCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand CancelStreamingCommand { get; }
    public IRelayCommand ClearConversationCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> CopyMessageCommand { get; }
    public IRelayCommand ToggleTtsCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> PlayMessageCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> RegenerateMessageCommand { get; }
    public IAsyncRelayCommand<RegenerateRequest> RegenerateStyledCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> ExportMessageHtmlCommand { get; }
    public IAsyncRelayCommand EnterVoiceModeCommand { get; }
    public IRelayCommand<string> UseSuggestionCommand { get; }
    public IRelayCommand<string> UseFollowupCommand { get; }
    public IAsyncRelayCommand<PiiKeywordRequest> AddPiiKeywordCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<string>> HandleFilesDroppedCommand { get; }
    public IAsyncRelayCommand<string> HandleImageAttachedCommand { get; }
    public IAsyncRelayCommand<BitmapSource> HandleImagePastedCommand { get; }
    public IRelayCommand RemoveAttachmentCommand { get; }
    public IAsyncRelayCommand ToggleMeetingAttendeeCommand { get; }

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
        MeetingAttendeeViewModel meetingAttendee,
        IAssistantPromptComposer promptComposer,
        IProviderCapabilityService providerCapabilityService,
        IAgentRunService agentRunService,
        IAgentRunResumeService resumeService,
        IChatSessionManager chatSessionManager,
        IWorkingDirectoryService workingDirectoryService,
        IFilesToolHandler filesToolHandler,
        IMarkdownExportService markdownExportService,
        IDialogService dialogService)
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
        MeetingAttendee = meetingAttendee;
        _promptComposer = promptComposer;
        _providerCapabilityService = providerCapabilityService;
        _agentRunService = agentRunService;
        _resumeService = resumeService;
        _chatSessionManager = chatSessionManager;
        _workingDirectoryService = workingDirectoryService;
        _filesToolHandler = filesToolHandler;
        _markdownExportService = markdownExportService;
        _dialogService = dialogService;

        SendMessageCommand = new AsyncRelayCommand(ExecuteSendMessage, CanExecuteSendMessage);
        RunInBackgroundCommand = new AsyncRelayCommand(ExecuteRunInBackground, CanExecuteRunInBackground);
        ToggleRecordingCommand = new AsyncRelayCommand(ExecuteToggleRecording);
        CancelStreamingCommand = new RelayCommand(ExecuteCancelStreaming);
        ClearConversationCommand = new RelayCommand(ExecuteClearConversation);
        CopyMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteCopyMessage);
        ToggleTtsCommand = new RelayCommand(ExecuteToggleTts);
        PlayMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecutePlayMessage, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RegenerateMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteRegenerateMessage);
        RegenerateStyledCommand = new AsyncRelayCommand<RegenerateRequest>(ExecuteRegenerateStyled);
        ExportMessageHtmlCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteExportMessageHtml);
        EnterVoiceModeCommand = new AsyncRelayCommand(ExecuteEnterVoiceMode, CanEnterVoiceMode);
        UseSuggestionCommand = new RelayCommand<string>(ExecuteUseSuggestion);
        UseFollowupCommand = new RelayCommand<string>(ExecuteUseFollowup);
        AddPiiKeywordCommand = new AsyncRelayCommand<PiiKeywordRequest>(ExecuteAddPiiKeyword);
        HandleFilesDroppedCommand = new AsyncRelayCommand<IReadOnlyList<string>>(ExecuteHandleFilesDropped);
        HandleImageAttachedCommand = new AsyncRelayCommand<string>(ExecuteHandleImageAttached);
        HandleImagePastedCommand = new AsyncRelayCommand<BitmapSource>(ExecuteHandleImagePasted);
        RemoveAttachmentCommand = new RelayCommand(() => PendingAttachment = null);
        ToggleMeetingAttendeeCommand = new AsyncRelayCommand(ExecuteToggleMeetingAttendee);

        _ttsService.IsPlayingChanged += OnTtsPlayingChanged;
        _personaService.PersonasChanged += OnPersonasChanged;
        PropertyChanged += OnPropertyChanged;
        MeetingAttendee.CloseRequested += OnMeetingAttendeeCloseRequested;
        MeetingAttendee.SummarizeRequested += OnMeetingAttendeeSummarizeRequested;
        MeetingAttendee.OpenSettingsRequested += OnMeetingAttendeeOpenSettingsRequested;

        ChatTitleChip = new ChatTitleChipViewModel(
            _chatService,
            _localizationService,
            _loggerFactory.CreateLogger<ChatTitleChipViewModel>(),
            ResumeChatAsync,
            DeleteChatFromChipAsync,
            NewChat,
            NavigateToAssistantHistory,
            _chatSessionManager.GetState,
            _workingDirectoryService,
            SetActiveWorkingDirectory,
            GetActiveWorkingDirectory);

        _chatSessionManager.ActiveChanged += OnActiveSessionChanged;
        _chatSessionManager.SessionTitleChanged += OnSessionTitleChanged;
        _chatSessionManager.SessionStateChanged += OnManagerSessionStateChanged;

        // Always mirror a live session so send/cancel never null-ref on a fresh
        // window. GetOrCreateActiveForNewChat raises ActiveChanged → AttachToActiveSession.
        _chatSessionManager.GetOrCreateActiveForNewChat();

        // Pin the initial chat to the configured default working directory (e.g. "Playground").
        // Done off the ctor (fire-and-forget, mirroring LoadPersonasAsync) so the synchronous
        // settings-load + folder-create never stalls window-open on the UI thread. It cascades to
        // "+ New chat" and Clear for free — the chip re-seeds its pending folder from the active
        // chat's dir on flyout-open.
        ApplyDefaultWorkingDirectoryAsync().SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Resolves the configured default working directory (creating the folder if needed) and pins
    /// it onto the initial, still-empty chat. Skips the pin if the user has already started a turn,
    /// re-pointed the folder, or a resumed chat became active in the interim — so it never clobbers
    /// a chat that already has a working directory of its own.
    /// </summary>
    private async Task ApplyDefaultWorkingDirectoryAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        var dir = _workingDirectoryService.EnsureSubfolder(settings.AssistantDefaultWorkingDirectory);
        if (string.IsNullOrEmpty(dir)) return; // unusable or root — leave the chat at root

        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            var session = _chatSessionManager.ActiveSession;
            if (session is null || session.Messages.Count > 0 || !string.IsNullOrEmpty(session.WorkingDirectory))
                return;

            session.SetWorkingDirectory(dir);
            ChatTitleChip.SetWorkingDirectory(session.WorkingDirectory);
            _filesToolHandler.ActiveUiWorkingSubpath = session.WorkingDirectory;
        });
    }

    private void OnActiveSessionChanged(object? sender, ChatSession? session)
    {
        if (session is not null)
            AttachToActiveSession(session);
    }

    /// <summary>Re-points Messages + proxies and moves the session-event subscriptions to <paramref name="session"/>.</summary>
    private void AttachToActiveSession(ChatSession session)
    {
        if (_subscribedSession is { } prev)
        {
            prev.StateChanged -= OnActiveSessionStateChanged;
            prev.TurnCompleted -= OnActiveSessionTurnCompleted;
            prev.ToolSucceeded -= OnActiveSessionToolSucceeded;
            prev.RunFailed -= OnActiveSessionRunFailed;
            prev.ActiveRunChanged -= OnActiveRunChanged;
            prev.ForeignRunActiveChanged -= OnForeignRunActiveChanged;
        }

        _subscribedSession = session;
        session.StateChanged += OnActiveSessionStateChanged;
        session.TurnCompleted += OnActiveSessionTurnCompleted;
        session.ToolSucceeded += OnActiveSessionToolSucceeded;
        session.RunFailed += OnActiveSessionRunFailed;
        session.ActiveRunChanged += OnActiveRunChanged;
        session.ForeignRunActiveChanged += OnForeignRunActiveChanged;
        SyncRunProgress(session.ActiveRunId); // embed the panel if this session already has a run
        ForeignRunActive = session.ForeignRunActive; // late attach: read the flag the manager already seeded

        Messages = session.Messages;            // re-points the ItemsControl (OnMessagesChanged swaps CollectionChanged)
        HasMessages = session.Messages.Count > 0;
        IsStreaming = session.IsStreaming;
        ActiveState = session.State;
        ChatTitleChip.SetTitle(session.Title);
        ChatTitleChip.SetWorkingDirectory(session.WorkingDirectory);
        // Scope the @Files autocomplete to this chat's dir (it runs outside any turn).
        _filesToolHandler.ActiveUiWorkingSubpath = session.WorkingDirectory;
    }

    // The session raises ActiveRunChanged on the UI thread (its Planned branch runs there), but marshal
    // defensively so a future off-thread caller can't touch the bound RunProgressViewModel cross-thread.
    private void OnActiveRunChanged(object? sender, Guid? runId) =>
        App.Current.Dispatcher.InvokeAsync(() => SyncRunProgress(runId));

    // The manager already marshals the flip to the UI thread (G3); marshal defensively anyway, for the same
    // reason OnActiveRunChanged does — this sets a bound property and re-evaluates a command.
    private void OnForeignRunActiveChanged(object? sender, bool active) =>
        App.Current.Dispatcher.InvokeAsync(() => ForeignRunActive = active);

    private void SyncRunProgress(Guid? runId)
    {
        if (_runProgress?.RunId == runId)
            return;
        _runProgress?.Dispose(); // unsubscribes the prior RunChanged handler
        _runProgress = runId is { } id
            ? new RunProgressViewModel(_agentRunService, id, _localizationService, _resumeService, _logger)
            : null;
        ActiveRunProgress = _runProgress;
    }

    partial void OnMessagesChanged(ObservableCollection<AssistantMessage>? oldValue, ObservableCollection<AssistantMessage> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnMessagesCollectionChanged;
        newValue.CollectionChanged += OnMessagesCollectionChanged;
    }

    private void OnMessagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        HasMessages = Messages.Count > 0;
    }

    private void OnActiveSessionStateChanged(object? sender, ChatStateChangedEventArgs e)
    {
        ActiveState = e.NewState;
        IsStreaming = _subscribedSession?.IsStreaming ?? false;
    }

    // Mirror the active state onto the chip badge (single sink — both the attach
    // path and live transitions set ActiveState, so this stays in sync).
    partial void OnActiveStateChanged(ChatState value) => ChatTitleChip.SetState(value);

    // Sync-void fire-and-forget: followups + TTS for the active session only.
    private void OnActiveSessionTurnCompleted(object? sender, TurnCompletedEventArgs e)
    {
        if (sender is ChatSession session)
            RunActiveTurnSideEffectsAsync(session, e.Succeeded).SafeFireAndForget(_logger);
    }

    private void OnActiveSessionToolSucceeded(object? sender, ToolSucceededEventArgs e)
    {
        _snackbarService.Show(e.SuccessTitle, e.Description,
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    private void OnActiveSessionRunFailed(object? sender, RunFailedEventArgs e)
    {
        var (appearance, seconds) = e.Kind switch
        {
            RunFailureKind.Timeout => (Wpf.Ui.Controls.ControlAppearance.Danger, 6),
            RunFailureKind.Truncated => (Wpf.Ui.Controls.ControlAppearance.Caution, 6),
            RunFailureKind.VisionRejected => (Wpf.Ui.Controls.ControlAppearance.Caution, 5),
            RunFailureKind.Empty => (Wpf.Ui.Controls.ControlAppearance.Caution, 2),
            _ => (Wpf.Ui.Controls.ControlAppearance.Danger, 4),
        };

        _snackbarService.Show(e.Title, e.Message, appearance, null, TimeSpan.FromSeconds(seconds));

        // Vision rejection restores the composer (active VM only — the message pair
        // was already dropped session-locally).
        if (e.Kind == RunFailureKind.VisionRejected && e.RestoreInputText is not null)
            InputText = e.RestoreInputText;
    }

    private void OnSessionTitleChanged(object? sender, SessionTitleChangedEventArgs e)
    {
        // Only the active session's title may update the chip.
        if (e.IsActive)
            ChatTitleChip.SetTitle(e.Title);
    }

    // Keep the quick-switcher badge for this chat live as its state changes.
    private void OnManagerSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.ChatId is { } chatId)
            ChatTitleChip.RefreshMatchState(chatId, e.NewState);
    }

    private async Task RunActiveTurnSideEffectsAsync(ChatSession session, bool succeeded)
    {
        var assistantMessage = session.Messages.LastOrDefault(m => !m.IsUser);
        if (assistantMessage is null || string.IsNullOrEmpty(assistantMessage.Content)) return;
        if (assistantMessage.Content.StartsWith("Error:", StringComparison.Ordinal)) return;

        // Follow-ups only on a clean turn (matches today: GenerateFollowupsAsync was
        // the last line of try, skipped on any exception / empty content).
        if (succeeded)
        {
            var userMessage = session.Messages.LastOrDefault(m => m.IsUser);
            if (session.ProviderId is { } providerId && userMessage is not null)
            {
                var provider = await _providerService.GetProviderAsync(providerId);
                if (provider is not null)
                    await GenerateFollowupsAsync(provider, userMessage.Content, assistantMessage, CancellationToken.None);
            }
        }

        // TTS mirrors today's finally gate: non-empty, non-"Error:" content — runs even
        // on a cancelled turn that produced partial visible text.
        if (IsTtsEnabled)
            await SpeakMessageAsync(assistantMessage);
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

            // PersonasChanged can arrive on a background thread (the sync pull loop calls
            // Add/Update/DeletePersonaAsync), so marshal the bound-collection mutation to the UI
            // thread. Awaited before the finally so _isLoadingPersonas is still set when the lambda
            // assigns ActivePersona (OnActivePersonaChanged relies on that guard).
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                AvailablePersonas.Clear();
                foreach (var persona in personas)
                    AvailablePersonas.Add(persona);

                ActivePersona = AvailablePersonas.FirstOrDefault(p => p.Id == active.Id) ?? active;

                // Seed the Chat/Agent lever from the persisted global default (R15).
                SeedAgentModeFromSettings(settings);
            });
        }
        finally
        {
            _isLoadingPersonas = false;
        }
    }

    /// <summary>Seeds the Chat/Agent lever from the persisted global default (R15), guarded so the
    /// seed itself never re-persists via <see cref="OnAgentModeEnabledChanged"/>. Internal seam so the
    /// seed-guard + reopen-restore can be exercised without spinning the whole persona-load path.</summary>
    internal void SeedAgentModeFromSettings(AppSettings settings)
    {
        _isLoadingAgentMode = true;
        try { AgentModeEnabled = settings.AssistantAgentModeDefault; }
        finally { _isLoadingAgentMode = false; }
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

    partial void OnAgentModeEnabledChanged(bool value)
    {
        if (_isLoadingAgentMode)
            return;
        PersistAgentModeDefaultAsync(value).SafeFireAndForget(_logger);
        // Warning-first (§14.4): surface the subtle Weak-provider adorner when flipping to Agent.
        if (value)
            EvaluateProviderWarningAsync().SafeFireAndForget(_logger);
        else
            WeakProviderWarningVisible = false;
    }

    // Weak-provider warning surface (§14.4). Set true when the active provider is not Capable of tool
    // calling for Agent planning; drives the subtle adorner on the Agent segment + the composer banner.
    // Never blocks a Planned send (R10). Populated by EvaluateProviderWarningAsync (Commit Group 2).
    [ObservableProperty]
    private bool _weakProviderWarningVisible;

    private async Task PersistAgentModeDefaultAsync(bool enabled)
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.AssistantAgentModeDefault = enabled;
        await _settingsService.SaveSettingsAsync(settings);
        _logger.LogInformation("Assistant agent-mode default set to {Enabled}", enabled);
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming) or nameof(PendingAttachment)
            or nameof(ForeignRunActive))
        {
            SendMessageCommand.NotifyCanExecuteChanged();
            RunInBackgroundCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(IsStreaming) or nameof(IsVoiceModeActive))
        {
            EnterVoiceModeCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task ExecuteToggleMeetingAttendee()
    {
        // Unlike voice mode, the attendee cannot auto-start on reveal: it needs a meeting URL and
        // consent first, collected by the overlay's own Join command. So the toggle only shows or
        // hides the overlay; hiding it also stops any in-progress session.
        if (IsMeetingAttendeeVisible)
        {
            await MeetingAttendee.StopAsync();
            IsMeetingAttendeeVisible = false;
            return;
        }

        // Pre-fill the editable assistant display name from settings (or the auto-built default) before
        // the overlay appears, so the join form shows the right value the moment it opens.
        await MeetingAttendee.PrepareForDisplayAsync();
        IsMeetingAttendeeVisible = true;
    }

    private void OnMeetingAttendeeCloseRequested(object? sender, EventArgs e)
    {
        // The overlay's close (X) button raises CloseRequested; stop the session and hide the overlay.
        _ = ExecuteToggleMeetingAttendee();
    }

    private void OnMeetingAttendeeSummarizeRequested(object? sender, string prompt)
    {
        // "Summarize with assistant" on the post-meeting transcript: hide the overlay so the chat (where
        // the summary streams) is revealed, open a fresh chat so the summary stands on its own, then send
        // the prompt. StartFreshChat clears InputText as its last step, so set the prompt afterwards.
        // Sync fire-and-forget mirrors OnMeetingAttendeeCloseRequested. Do NOT log the prompt — it carries
        // the (sensitive) meeting transcript.
        IsMeetingAttendeeVisible = false;
        StartFreshChat();
        // Drop any image left pending in the composer behind the overlay so the summary turn carries only
        // the prompt (StartFreshChat clears InputText but not PendingAttachment).
        PendingAttachment = null;
        InputText = prompt;
        SendMessageCommand.Execute(null);
    }

    private void OnMeetingAttendeeOpenSettingsRequested(object? sender, EventArgs e)
    {
        // "Meeting settings" link on the join setup page: deep-link to the Assistant settings tab
        // → Meeting inner tab — mirrors NavigateToToolPermissions. The overlay is left visible: it's
        // a child of this page, so navigating swaps it out of the frame, and returning to the
        // assistant restores the join form. The link is only reachable before a meeting runs, so
        // there is no live session to stop.
        _navigationService.NavigateTo<SettingsViewModel, (int, int)>(
            ((int)SettingsTab.Assistant, (int)AssistantSettingsInnerTab.Meeting));
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

    // W2: ForeignRunActive blocks Send because a live turn would be a SECOND full-chat writer against a
    // headless executor that is mid-run — the live full replace deletes the run's step rows, and the run's
    // own model context never sees the typed message, so the transcript would be garbled even without loss.
    private bool CanExecuteSendMessage() =>
        !IsStreaming && !ForeignRunActive
        && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachment is not null);

    // "Run in background" detaches the typed goal and (this milestone) ignores attachments, so unlike
    // Send it requires real text — never enable on an attachment-only composer. Deliberately NOT gated on
    // ForeignRunActive: it launches into a NEW chat id and never writes this chat.
    private bool CanExecuteRunInBackground() =>
        !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    private async Task ExecuteSendMessage()
    {
        var userText = InputText.Trim();
        InputText = string.Empty;
        var attachment = PendingAttachment;
        PendingAttachment = null;

        var session = _chatSessionManager.ActiveSession
            ?? _chatSessionManager.GetOrCreateActiveForNewChat();

        // The Chat/Agent lever decides the run shape. Defence in depth: a no-tools persona can never
        // plan (the lever UI already disables), so force Chat regardless of a stale lever value.
        var planned = AgentModeEnabled && ActivePersona?.ToolScope != PersonaToolScope.None;

        // Awaited so the AsyncRelayCommand's running-state blocks re-entry; StartTurnAsync
        // returns once the turn is fire-and-forgotten (Step 4-compatible).
        await _chatSessionManager.StartTurnAsync(session, userText, attachment, planned: planned);
    }

    /// <summary>
    /// "Run in background": detach the current input as an unattended headless Planned run instead of
    /// starting a live turn. Additive to <see cref="ExecuteSendMessage"/> — no live session is created
    /// (G-6); the run notifies via Flow on completion.
    /// </summary>
    private async Task ExecuteRunInBackground()
    {
        var userText = InputText.Trim();
        if (string.IsNullOrWhiteSpace(userText)) return;
        InputText = string.Empty;
        try
        {
            await _chatSessionManager.StartBackgroundRunAsync(userText);
        }
        catch (Exception ex)
        {
            // A detached run has no live session to surface a failure through, and the goal is not persisted
            // until the run row exists — so a pre-dispatch failure (e.g. no provider configured) would silently
            // swallow the user's input. Restore it and tell them. ex.Message is diagnostic text, not user content.
            _logger.LogError(ex, "Failed to start a background run");
            InputText = userText;
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                _localizationService.Format("Assistant_RunInBackground_Failed", ex.Message),
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
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
            RunInBackgroundCommand.NotifyCanExecuteChanged();
        }
    }

    private void ExecuteCancelStreaming()
    {
        _chatSessionManager.ActiveSession?.Cancel();
    }

    /// <summary>
    /// "Clear conversation" (destructive): abandon the CURRENT conversation by cancelling
    /// its in-flight turn + pending action cards, then open a fresh chat. No-op cancel when
    /// nothing is running. The dedicated Stop button (<see cref="ExecuteCancelStreaming"/>)
    /// is the other place a turn is intentionally cancelled. Contrast <see cref="NewChat"/>,
    /// which is additive and leaves the running turn alive in the background.
    /// </summary>
    private void ExecuteClearConversation()
    {
        // Inherit the cleared chat's working dir so clearing keeps you in the same folder
        // (capture before the swap, since GetOrCreateActiveForNewChat re-points ActiveSession).
        var inheritedDir = _chatSessionManager.ActiveSession?.WorkingDirectory;
        // Destructive: cancel this conversation's in-flight turn + pending cards. Other
        // live sessions are untouched (the manager owns their lifetime).
        _chatSessionManager.ActiveSession?.Cancel();
        StartFreshChat(inheritedDir);
    }

    /// <summary>
    /// "New chat" (additive): open a fresh chat WITHOUT cancelling the current turn — the
    /// running turn keeps streaming in the background and notifies on completion (the
    /// background-chats contract: opening or switching a chat never kills an in-flight
    /// turn). Cancelling here would abort the in-flight HTTP request — surfacing a
    /// SocketException in the support log — and discard the background result.
    /// </summary>
    private void NewChat(string? workingDirectory) => StartFreshChat(workingDirectory);

    /// <summary>Opens a new, empty active chat and resets the composer. Shared by the
    /// additive "New chat" (pinned to the folder shown on the pill) and the destructive
    /// "Clear conversation" entry point (which inherits the cleared chat's folder).</summary>
    /// <param name="workingDirectory">Relative working dir to pin (forward slashes;
    /// null/empty = sandbox root).</param>
    private void StartFreshChat(string? workingDirectory = null)
    {
        _ttsService.Stop();

        // The new session is created with its OWN freshly-initialized token map, so
        // the new chat starts with a clean PII namespace automatically — no global
        // Clear() (which would poison a still-running background chat's map).
        // GetOrCreateActiveForNewChat raises ActiveChanged → AttachToActiveSession, which
        // pushes the new (root) dir to the chip; pin + re-push afterwards so the pill
        // reflects the folder this chat was started in.
        var session = _chatSessionManager.GetOrCreateActiveForNewChat();
        session.SetWorkingDirectory(workingDirectory);
        ChatTitleChip.SetWorkingDirectory(session.WorkingDirectory);
        _filesToolHandler.ActiveUiWorkingSubpath = session.WorkingDirectory;
        InputText = string.Empty;
    }

    private async Task ResumeChatAsync(Guid chatId)
    {
        _ttsService.Stop();
        await _chatSessionManager.ActivateAsync(chatId);
    }

    /// <summary>Quick-delete a chat from the title-chip flyout: confirm, then delete (ChatsChanged
    /// refreshes the flyout). Mirrors the history view's confirmation wording.</summary>
    private async Task DeleteChatFromChipAsync(Guid chatId)
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_History_ConfirmDeleteTitle"],
            _localizationService["Msg_History_ConfirmDeleteMessage"]);
        if (!confirmed) return;

        await _chatService.DeleteAsync(chatId);
        _logger.LogInformation("Deleted assistant chat {ChatId} from title-chip flyout", chatId);
    }

    private void NavigateToAssistantHistory()
    {
        _navigationService.NavigateTo<AssistantHistoryViewModel>();
    }

    /// <summary>Reads the active chat's working dir (relative, forward slashes; null = root) for the chip.</summary>
    private string? GetActiveWorkingDirectory() => _chatSessionManager.ActiveSession?.WorkingDirectory;

    /// <summary>
    /// Re-points the active chat's working dir from the picker — but ONLY while that chat is
    /// un-started (no messages yet). Once a chat has begun a turn its folder is fixed; the
    /// picker then only chooses where the next "+ New Chat" opens. Unlike
    /// <see cref="ChatSession.ProviderId"/> (which persists only as a turn side-effect), a
    /// working-dir change can happen with no turn, so this triggers an explicit persist.
    /// </summary>
    private void SetActiveWorkingDirectory(string? relativePath)
    {
        var session = _chatSessionManager.ActiveSession;
        if (session is null) return;

        // A started chat (turn in progress or with history) keeps its folder. The pill still
        // reflects the pick for the next new chat; we just don't re-point this one.
        if (session.Messages.Count > 0) return;

        session.SetWorkingDirectory(relativePath);
        // Keep the @Files autocomplete scoped to the re-pointed dir immediately.
        _filesToolHandler.ActiveUiWorkingSubpath = session.WorkingDirectory;
        // PersistAsync is a no-op for a brand-new empty chat (no row yet); the first turn
        // will persist with the current dir. For an existing chat this saves the re-point.
        _chatSessionManager.PersistAsync(session).SafeFireAndForget(_logger);
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

    private Task ExecuteRegenerateMessage(AssistantMessage? message) =>
        RegenerateCore(message, RegenerateStyle.Default);

    private Task ExecuteRegenerateStyled(RegenerateRequest? request) =>
        request is null ? Task.CompletedTask : RegenerateCore(request.Message, request.Style);

    /// <summary>
    /// Re-runs the user prompt that produced <paramref name="message"/>, optionally appending a style
    /// instruction (Shorter / More detailed / Export-ready). Removes the prior answer and anything after
    /// it, then starts a fresh turn directly via the session manager — bypassing the composer round-trip
    /// so it never clobbers whatever the user has typed. The style instruction is injected AI-side only,
    /// so the re-sent user bubble stays the original prompt.
    /// </summary>
    private async Task RegenerateCore(AssistantMessage? message, RegenerateStyle style)
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

        var session = _chatSessionManager.ActiveSession
            ?? _chatSessionManager.GetOrCreateActiveForNewChat();

        await _chatSessionManager.StartTurnAsync(session, prompt, attachment, RegenerateInstructions.For(style));
    }

    private async Task ExecuteExportMessageHtml(AssistantMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Content))
            return;

        try
        {
            var fallbackTitle = _localizationService["Msg_Assistant_ExportDefaultTitle"];
            var path = await _markdownExportService.ExportAsync(
                message.Content, title: null, fallbackTitle, _chatSessionManager.ActiveSession?.WorkingDirectory);

            // Surface the generated file as an open-file/open-folder chip, and open it in the browser.
            message.AddOrUpgradeFileRef(new FileRef(path, FileRefKind.Exported));
            ShellLauncher.OpenFile(path);

            _snackbarService.Show(
                _localizationService["Msg_Assistant_Exported"],
                _localizationService.Format("Msg_Assistant_ExportedTo", System.IO.Path.GetFileName(path)),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export message to HTML");
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                _localizationService["Msg_Assistant_ExportFailed"],
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
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

    public void OnNavigatedTo(object? parameter)
    {
        RandomizeSuggestions();

        // Non-Guid synchronous setup (string / selection params). Guid-activation
        // is awaited in OnNavigatedToAsync so its exceptions are observed.
        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            InputText = text;
        }
        else if (parameter is CapturedSelectionPayload selection)
        {
            ApplyCapturedSelection(selection.Text);
        }
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is Guid chatId && chatId != Guid.Empty)
        {
            await ResumeChatAsync(chatId);
        }

        try
        {
            await LoadPersonasAsync();

            var settings = await _settingsService.GetSettingsAsync();
            IsTtsEnabled = settings.TtsEnabled;
            _suggestionsEnabled = settings.AssistantSuggestionsEnabled;

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
        // General tab → Speech inner tab hosts the TTS voice selection UI.
        SnackbarActionHelper.ShowWithAction(
            _snackbarService,
            _localizationService["Msg_Warning"],
            _localizationService["Msg_Tts_NoVoiceLoaded"],
            _localizationService["Msg_Tts_NoVoiceLoaded_OpenSettings"],
            () => _navigationService.NavigateTo<SettingsViewModel, (int, int)>(
                ((int)SettingsTab.General, (int)GeneralSettingsInnerTab.Speech)),
            Wpf.Ui.Controls.ControlAppearance.Caution,
            TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Deep-link from an auto-approved action card's "Manage" affordance to the
    /// tool-permissions revocation surface: Assistant tab → Tool access inner tab.
    /// SettingsViewModel.OnNavigatedTo maps (Assistant, inner).
    /// </summary>
    [RelayCommand]
    private void NavigateToToolPermissions()
        => _navigationService.NavigateTo<SettingsViewModel, (int, int)>(
            ((int)SettingsTab.Assistant, (int)AssistantSettingsInnerTab.ToolAccess));

    /// <summary>
    /// Accepts a model-offered <see cref="AgentModeSuggestion"/> (R8): flips the lever to Agent and
    /// re-dispatches the goal as a Planned run. Modeled on <see cref="ExecuteSendMessage"/> (NOT
    /// RegenerateCore) — the prior Chat answer carrying the chip stays in the transcript, and the
    /// composer round-trip is untouched (InputText/attachment are never read or cleared).
    /// </summary>
    [RelayCommand]
    private async Task SwitchToAgent(AgentModeSuggestion? suggestion)
    {
        if (suggestion is null || string.IsNullOrWhiteSpace(suggestion.Goal))
            return;

        AgentModeEnabled = true; // persists + evaluates the warning via OnAgentModeEnabledChanged
        var session = _chatSessionManager.ActiveSession
            ?? _chatSessionManager.GetOrCreateActiveForNewChat();
        await _chatSessionManager.StartTurnAsync(session, suggestion.Goal, attachment: null, planned: true);
    }

    /// <summary>Warning-first evaluation (§14.4): shows the subtle adorner/banner when the active provider
    /// is not Capable of tool calling. Non-blocking — a Weak provider still runs Planned (R10).</summary>
    internal async Task EvaluateProviderWarningAsync()
    {
        var provider = await ResolveActiveProviderAsync();
        if (provider is null)
        {
            WeakProviderWarningVisible = false;
            return;
        }
        var capability = await _providerCapabilityService.GetPlanningCapabilityAsync(provider);
        // Treat Unknown (transient probe failure) conservatively like Weak (OQ5).
        WeakProviderWarningVisible = capability != PlanningCapability.Capable;
    }

    /// <summary>Resolves the provider for the active persona (persona preference, else the mode default).</summary>
    private async Task<AiProvider?> ResolveActiveProviderAsync()
    {
        var persona = ActivePersona;
        if (persona?.PreferredProviderId is { } preferred)
        {
            var p = await _providerService.GetProviderAsync(preferred);
            if (p is not null)
                return p;
        }
        return await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
    }

    [RelayCommand]
    private void DismissWeakWarning() => WeakProviderWarningVisible = false;

    [RelayCommand]
    private void OpenProviderSettings()
    {
        WeakProviderWarningVisible = false;
        // Link to existing provider settings (no reassignment UI — out of scope this pass).
        _navigationService.NavigateTo<SettingsViewModel, (int, int)>(
            ((int)SettingsTab.Providers, 0));
    }

    [RelayCommand]
    private void StayInChat() => AgentModeEnabled = false;

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

        // Voice mode has no chip-render surface and no Planned concept (F1) → never eligible.
        var turnSetup = _promptComposer.PrepareTurn(persona, provider, Array.Empty<AtCommand>(), _tokenizationEnabled,
            suggestAgentModeEligible: false);
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
        MeetingAttendee.CloseRequested -= OnMeetingAttendeeCloseRequested;
        MeetingAttendee.SummarizeRequested -= OnMeetingAttendeeSummarizeRequested;
        MeetingAttendee.OpenSettingsRequested -= OnMeetingAttendeeOpenSettingsRequested;
        MeetingAttendee.Dispose();
        _ttsService.Stop();
        _ttsService.IsPlayingChanged -= OnTtsPlayingChanged;
        _personaService.PersonasChanged -= OnPersonasChanged;
        PropertyChanged -= OnPropertyChanged;

        // Unsubscribe only — the manager owns session lifetime and tears them down
        // (cancelling each Cts + pending action cards) when the window scope disposes.
        // This is what lets Assistant → History → Assistant not kill a running turn.
        _chatSessionManager.ActiveChanged -= OnActiveSessionChanged;
        _chatSessionManager.SessionTitleChanged -= OnSessionTitleChanged;
        _chatSessionManager.SessionStateChanged -= OnManagerSessionStateChanged;
        if (_subscribedSession is { } session)
        {
            session.StateChanged -= OnActiveSessionStateChanged;
            session.TurnCompleted -= OnActiveSessionTurnCompleted;
            session.ToolSucceeded -= OnActiveSessionToolSucceeded;
            session.RunFailed -= OnActiveSessionRunFailed;
            session.ActiveRunChanged -= OnActiveRunChanged;
        }
        _runProgress?.Dispose(); // unsubscribes the last RunChanged handler off the singleton
        Messages.CollectionChanged -= OnMessagesCollectionChanged;

        ChatTitleChip.Dispose();

        GC.SuppressFinalize(this);
    }
}
