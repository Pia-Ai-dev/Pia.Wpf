using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
using Pia.Services.Operators;
using Pia.Services.Screen;
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
    private readonly IAgentTimelineService? _agentTimelineService;
    private readonly IRunWorkspaceService? _runWorkspaces;
    private readonly IAgentRunResumeService _resumeService;
    private readonly IChatSessionManager _chatSessionManager;
    private readonly IWorkingDirectoryService _workingDirectoryService;
    private readonly IAttachedFileStore? _attachedFileStore;
    private readonly IFilesToolHandler _filesToolHandler;
    private readonly IMarkdownExportService _markdownExportService;
    private readonly IDialogService _dialogService;
    private readonly IFileDialogService? _fileDialogService;
    private readonly IAgentToolExchangeStore? _toolCalls;
    private readonly IAiFeedbackService? _aiFeedback;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IVolatileWorkStore? _volatileWork;
    private readonly IStarterSuggestionService? _starterSuggestions;

    /// <summary>Tool-permission state consulted by the voice-mode gate.</summary>
    private readonly IToolPermissionService _permissions;

    /// <summary>
    /// Batch 08 D1: the steering registry, held here for its TERMINAL-INTENT revocations only (§5.3 sites 1
    /// and 2 — Stop and clear-conversation). This VM never records a pause request; that is the run panel's
    /// job, through <c>IAgentRunSteeringService</c>. Null ⇒ nothing to revoke, i.e. the pre-Batch-08 behaviour.
    /// </summary>
    private readonly IRunSteeringStore? _runSteering;

    /// <summary>Batch 08 G8: handed on to the hand-constructed <see cref="RunProgressViewModel"/> so its Pause
    /// button can request a user pause. Null ⇒ <c>CanPause</c> is always false, i.e. the panel renders exactly
    /// as it did before this batch.</summary>
    private readonly IAgentRunSteeringService? _steering;
    private readonly IThemeService? _themeService;
    private readonly ITimelineWatcher? _timelineWatcher;
    private readonly IAssignmentSurfaceCache? _assignmentSurfaceCache;
    private readonly Func<AssignmentConsentViewModel>? _assignmentConsentFactory;
    private readonly IScreenCaptureService? _screenCapture;
    private readonly IScreenCaptureAuditLog? _screenCaptureAudit;
    private readonly IScreenCaptureIndicator? _screenCaptureIndicator;
    private AssignmentSurface _assignmentSurface = AssignmentSurface.Hidden;
    private bool _disposed;
    private bool _tokenizationEnabled;
    private bool _suggestionsEnabled = true;

    /// <summary>The session whose events this VM is currently subscribed to.</summary>
    private ChatSession? _subscribedSession;

    public ChatTitleChipViewModel ChatTitleChip { get; }

    [ObservableProperty]
    private string _inputText = string.Empty;

    public ObservableCollection<ImageAttachment> PendingAttachments { get; } = new();

    public const int MaxPendingImages = 4;

    /// <summary>The real gate: four images at ImageAttachmentProcessor.ThresholdBytes each is 14 MB.</summary>
    public const long MaxPendingImageBytes = 12L * 1024 * 1024;

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    /// <summary>Text and mail files staged as chips, kept apart from the image attachments.</summary>
    public ObservableCollection<PendingFileAttachment> PendingFiles { get; } = new();

    public bool HasPendingFiles => PendingFiles.Count > 0;

    /// <summary>Whether the composer can offer "save to working directory" at all — false leaves the
    /// chip with only its remove button rather than a button that cannot work.</summary>
    public bool CanSavePendingFiles => _attachedFileStore is not null;

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>
    /// W2: a run attached to this chat is executing under an executor this session does not own (headlessly),
    /// so it is a second full-chat writer and Send must stay disabled until it stops. Also drives the composer
    /// hint line (Assistant_RunActive_Hint): the embedded <see cref="ActiveRunProgress"/> panel does show the
    /// run executing, but nothing there attributes the dead Send to it, so the hint says it in words. The hint
    /// does NOT say "background": approving a plan hands a foreground run to the headless executor, so this is
    /// what the person who pressed Send sees, and naming a button they did not press reads as their mistake.
    /// </summary>
    [ObservableProperty]
    private bool _foreignRunActive;

    /// <summary>Narrower than <see cref="ForeignRunActive"/>: the run is parked on a proposed plan, so the only
    /// answers are Approve and Reject in the run panel. Also drives its own composer hint line.</summary>
    [ObservableProperty]
    private bool _planApprovalParkActive;

    /// <summary>Gates the action row's background-assignment button: the server offers the surface.</summary>
    [ObservableProperty]
    private bool _isAssignmentSurfaceAvailable;

    /// <summary>True only when, in agent mode, <see cref="GoalPreflight.IsRefused"/> refuses the typed goal; appears one idle second after typing, hides immediately.</summary>
    [ObservableProperty]
    private bool _goalTooShortHintVisible;

    /// <summary>Idle time before the goal-too-short hint may appear; internal so tests can zero it.</summary>
    internal TimeSpan GoalTooShortHintDebounce { get; set; } = TimeSpan.FromSeconds(1);

    private int _goalHintGeneration;

    /// <summary>Says what agent mode changes about a send; shown when the lever is flipped to Agent and
    /// yields to the goal-too-short hint, which shares this spot in the composer.</summary>
    [ObservableProperty]
    private bool _agentModeHintVisible;

    /// <summary>How long that hint stays; internal so tests can zero it.</summary>
    internal TimeSpan AgentModeHintDuration { get; set; } = TimeSpan.FromSeconds(8);

    private int _agentHintGeneration;

    /// <summary>Says why an attached file turns Run in background off and keeps the turn unplanned.</summary>
    [ObservableProperty]
    private bool _pendingFilesBlockRunHintVisible;

    /// <summary>
    /// Why a drop produced no chip, shown in the composer rather than only as a snackbar. A drop leaves the
    /// source app in the foreground, so a corner toast can land behind the window the user just dragged from —
    /// the one place they are certainly looking is where the chip would have been.
    /// </summary>
    [ObservableProperty]
    private string? _dropFailureMessage;

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

    /// <summary>False hides the toolbar toggle; policy can remove either meeting feature on its own.</summary>
    [ObservableProperty]
    private bool _isMeetingAttendeeAvailable = true;

    public DirectTranscriptionViewModel DirectTranscription { get; }

    [ObservableProperty]
    private bool _isDirectTranscriptionVisible;

    [ObservableProperty]
    private bool _isDirectTranscriptionAvailable = true;

    /// <summary>Empty-state chips. Drawn per visit, so a chat that already has messages never pays for them.</summary>
    public ObservableCollection<StarterSuggestion> Suggestions { get; } = new();

    /// <summary>The persona shown in the picker chip. Changing it persists the per-mode selection
    /// (synced via SyncSettings); the new persona applies from the next turn.</summary>
    [ObservableProperty]
    private Persona? _activePersona;

    private bool _isLoadingPersonas;

    /// <summary>The Chat/Agent lever state. false = Chat, true = Agent (Planned run). Per-chat: it is held
    /// on the active <see cref="ChatSession"/>, never written back to settings.</summary>
    [ObservableProperty]
    private bool _agentModeEnabled;

    /// <summary>Guards the seed of <see cref="AgentModeEnabled"/> so activating a chat is not mistaken for
    /// the user flipping the lever (mirrors <see cref="_isLoadingPersonas"/> for the persona seed).</summary>
    private bool _isLoadingAgentMode;

    /// <summary>The active chat's recorded agent-context choice; null means never asked.</summary>
    [ObservableProperty]
    private AgentContextMode? _activeAgentContextMode;

    /// <summary>The context banner is up and unanswered, which blocks Send and Run-in-background — <c>Off</c>
    /// has to be a click. Flipping the lever to Chat clears it, so the banner needs no button for that.</summary>
    [ObservableProperty]
    private bool _agentContextChoicePending;

    /// <summary>The one-line statement of what a settled choice is doing, so the state never acts invisibly.</summary>
    [ObservableProperty]
    private bool _agentContextSettledVisible;

    [ObservableProperty]
    private string _agentContextSettledLabel = string.Empty;

    /// <summary>The run-progress view-model for the active session's live/selected run (§15.1); null when
    /// the active chat has no run to surface. New'd on the UI thread, disposed on session swap (not DI'd).</summary>
    private RunProgressViewModel? _runProgress;

    [ObservableProperty]
    private RunProgressViewModel? _activeRunProgress;

    private const int VisibleSuggestionCount = 3;

    public IAutocompleteService AutocompleteService => _autocompleteService;

    /// <summary>Points at the active session's message list; re-pointed on active-session swap.</summary>
    [ObservableProperty]
    private ObservableCollection<AssistantMessage> _messages = new();

    /// <summary>What the transcript list is bound to — the tail of <see cref="Messages"/>, which stays whole
    /// because it is also the model's context, the export and in-chat search.</summary>
    public ObservableCollection<AssistantMessage> VisibleMessages { get; } = [];

    private bool _hasOlderMessages;

    public bool HasOlderMessages
    {
        get => _hasOlderMessages;
        private set => SetProperty(ref _hasOlderMessages, value);
    }

    private int _olderMessageCount;

    public int OlderMessageCount
    {
        get => _olderMessageCount;
        private set => SetProperty(ref _olderMessageCount, value);
    }

    /// <summary>Proxied from the active session's <see cref="ChatState"/> (drives the chip badge).</summary>
    [ObservableProperty]
    private ChatState _activeState = ChatState.Idle;

    public ObservableCollection<Persona> AvailablePersonas { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }
    public IAsyncRelayCommand RunInBackgroundCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand CancelStreamingCommand { get; }
    public IRelayCommand NewChatCommand { get; }
    public IAsyncRelayCommand DeleteCurrentChatCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> CopyMessageCommand { get; }
    public IRelayCommand ToggleTtsCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> PlayMessageCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> RegenerateMessageCommand { get; }
    public IAsyncRelayCommand<RegenerateRequest> RegenerateStyledCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> ExportMessageCommand { get; }
    public IAsyncRelayCommand<AnswerRatingRequest> RateMessageCommand { get; }
    public IAsyncRelayCommand EnterVoiceModeCommand { get; }
    public IRelayCommand<string> UseSuggestionCommand { get; }
    public IRelayCommand<string> UseFollowupCommand { get; }
    public IAsyncRelayCommand<PiiKeywordRequest> AddPiiKeywordCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<string>> HandleFilesDroppedCommand { get; }
    public IRelayCommand<string> HandleDropFailedCommand { get; }
    public IAsyncRelayCommand<string> HandleImageAttachedCommand { get; }
    public IAsyncRelayCommand<BitmapSource> HandleImagePastedCommand { get; }
    public IRelayCommand RemoveAttachmentCommand { get; }
    public IRelayCommand<PendingFileAttachment> RemovePendingFileCommand { get; }
    public IRelayCommand<PendingFileAttachment> SavePendingFileCommand { get; }
    public IRelayCommand<AttachedFileRef> OpenAttachedFileCommand { get; }
    public IRelayCommand<AttachedFileRef> RevealAttachedFileCommand { get; }
    public IAsyncRelayCommand ToggleMeetingAttendeeCommand { get; }
    public IAsyncRelayCommand ToggleDirectTranscriptionCommand { get; }
    public IAsyncRelayCommand CaptureScreenCommand { get; }

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
        DirectTranscriptionViewModel directTranscription,
        IAssistantPromptComposer promptComposer,
        IProviderCapabilityService providerCapabilityService,
        IAgentRunService agentRunService,
        IAgentRunResumeService resumeService,
        IChatSessionManager chatSessionManager,
        IWorkingDirectoryService workingDirectoryService,
        IFilesToolHandler filesToolHandler,
        IMarkdownExportService markdownExportService,
        IDialogService dialogService,
        IUiDispatcher uiDispatcher,
        IToolPermissionService permissions,
        // Batch 03: handed to the hand-constructed RunProgressViewModel so the run panel can show the
        // tool-decision trace. Trailing and defaulted so the existing test constructions keep compiling.
        IAgentTimelineService? agentTimelineService = null,
        // Batch 06 G4: handed on to the same hand-constructed panel VM so a settled run can offer to publish
        // what is still in its workspace, and so a worktree run can say which branch its output is on. Same
        // trailing-and-defaulted discipline, same reason.
        IRunWorkspaceService? runWorkspaces = null,
        // Batch 08 D1: read by the two TERMINAL-intent commands below (Stop, clear conversation) so a cancel
        // the user meant as "stop this run" can never be consumed as a pause. Trailing and defaulted for the
        // same reason as the two above; null ⇒ nothing is ever revoked, which is today's behaviour.
        IRunSteeringStore? runSteering = null,
        // Batch 08 G8: handed on to the run panel VM only. Trailing and defaulted, same discipline; null ⇒ no
        // Pause button anywhere this VM constructs the panel.
        IAgentRunSteeringService? steering = null,
        // Handed on to the run panel VM only, so a theme switch reaches the colours its converters resolved by
        // key. Trailing and defaulted, same discipline as the four above; null means the panel keeps the
        // pre-fix behaviour and simply does not re-resolve them.
        IThemeService? themeService = null,
        // Handed on to the run panel VM only, so its tool-activity section live-updates. Trailing and
        // defaulted, same discipline; null means the panel reads its trace on expand and at settle only.
        ITimelineWatcher? timelineWatcher = null,
        // Trailing and defaulted for the same reason as the five above; null ⇒ the background-assignment
        // action never appears, which is also what an unavailable surface looks like.
        IAssignmentSurfaceCache? assignmentSurfaceCache = null,
        Func<AssignmentConsentViewModel>? assignmentConsentFactory = null,
        // Where this window publishes "a restart would destroy something here" so the policy-restart overlay
        // in EVERY window defers. Trailing and defaulted; null ⇒ nothing is published.
        IVolatileWorkStore? volatileWork = null,
        // Trailing and defaulted, same discipline as the ones above; null ⇒ the empty state shows no chips.
        IStarterSuggestionService? starterSuggestions = null,
        // Trailing and defaulted, same discipline; null ⇒ thumbs on Pia Cloud answers do nothing.
        IAiFeedbackService? aiFeedback = null,
        // Trailing and defaulted, same discipline; null ⇒ the export dialog's External button has no picker,
        // so it does nothing rather than writing somewhere the user did not choose.
        IFileDialogService? fileDialogService = null,
        // Handed on to the run panel VM only, so a parked run can show the whole call it asked to make.
        // Trailing and defaulted, same discipline; null ⇒ the panel offers no detail.
        IAgentToolExchangeStore? toolCalls = null,
        // Trailing and defaulted, same discipline; null => the composer offers no save-to-working-directory
        // button and an attachment chip stays name-only.
        IAttachedFileStore? attachedFileStore = null,
        // Trailing and defaulted, same discipline; null ⇒ the composer offers no screen-capture button and
        // no provider read is made for one.
        IScreenCaptureService? screenCapture = null,
        IScreenCaptureAuditLog? screenCaptureAudit = null,
        IScreenCaptureIndicator? screenCaptureIndicator = null)
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
        DirectTranscription = directTranscription;
        _promptComposer = promptComposer;
        _providerCapabilityService = providerCapabilityService;
        _agentRunService = agentRunService;
        _agentTimelineService = agentTimelineService;
        _runWorkspaces = runWorkspaces;
        _resumeService = resumeService;
        _chatSessionManager = chatSessionManager;
        _workingDirectoryService = workingDirectoryService;
        _attachedFileStore = attachedFileStore;
        _filesToolHandler = filesToolHandler;
        _markdownExportService = markdownExportService;
        _dialogService = dialogService;
        _fileDialogService = fileDialogService;
        _toolCalls = toolCalls;
        _uiDispatcher = uiDispatcher;
        _permissions = permissions;
        _runSteering = runSteering;
        _steering = steering;
        _themeService = themeService;
        _timelineWatcher = timelineWatcher;
        _assignmentSurfaceCache = assignmentSurfaceCache;
        _assignmentConsentFactory = assignmentConsentFactory;
        _volatileWork = volatileWork;
        _starterSuggestions = starterSuggestions;
        _aiFeedback = aiFeedback;
        _screenCapture = screenCapture;
        _screenCaptureAudit = screenCaptureAudit;
        _screenCaptureIndicator = screenCaptureIndicator;

        SendMessageCommand = new AsyncRelayCommand(ExecuteSendMessage, CanExecuteSendMessage);
        RunInBackgroundCommand = new AsyncRelayCommand(ExecuteRunInBackground, CanExecuteRunInBackground);
        ToggleRecordingCommand = new AsyncRelayCommand(ExecuteToggleRecording);
        CancelStreamingCommand = new RelayCommand(ExecuteCancelStreaming);
        NewChatCommand = new RelayCommand(ExecuteNewChat);
        DeleteCurrentChatCommand = new AsyncRelayCommand(ExecuteDeleteCurrentChat, CanDeleteCurrentChat);
        CopyMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteCopyMessage);
        ToggleTtsCommand = new RelayCommand(ExecuteToggleTts);
        PlayMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecutePlayMessage, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RegenerateMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteRegenerateMessage);
        RegenerateStyledCommand = new AsyncRelayCommand<RegenerateRequest>(ExecuteRegenerateStyled);
        ExportMessageCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteExportMessage);
        RateMessageCommand = new AsyncRelayCommand<AnswerRatingRequest>(ExecuteRateMessage);
        EnterVoiceModeCommand = new AsyncRelayCommand(ExecuteEnterVoiceMode, CanEnterVoiceMode);
        UseSuggestionCommand = new RelayCommand<string>(ExecuteUseSuggestion);
        UseFollowupCommand = new RelayCommand<string>(ExecuteUseFollowup);
        AddPiiKeywordCommand = new AsyncRelayCommand<PiiKeywordRequest>(ExecuteAddPiiKeyword);
        HandleFilesDroppedCommand = new AsyncRelayCommand<IReadOnlyList<string>>(ExecuteHandleFilesDropped);
        HandleDropFailedCommand = new RelayCommand<string>(ExecuteHandleDropFailed);
        HandleImageAttachedCommand = new AsyncRelayCommand<string>(ExecuteHandleImageAttached);
        HandleImagePastedCommand = new AsyncRelayCommand<BitmapSource>(ExecuteHandleImagePasted);
        RemoveAttachmentCommand = new RelayCommand<ImageAttachment>(attachment =>
        {
            if (attachment is not null) PendingAttachments.Remove(attachment);
        });
        RemovePendingFileCommand = new RelayCommand<PendingFileAttachment>(file =>
        {
            if (file is not null) PendingFiles.Remove(file);
        });
        SavePendingFileCommand = new RelayCommand<PendingFileAttachment>(ExecuteSavePendingFile);
        OpenAttachedFileCommand = new RelayCommand<AttachedFileRef>(
            file => AttachedFileOpener.Open(file, _attachedFileStore));
        RevealAttachedFileCommand = new RelayCommand<AttachedFileRef>(
            file => AttachedFileOpener.Reveal(file, _attachedFileStore));
        ToggleMeetingAttendeeCommand = new AsyncRelayCommand(ExecuteToggleMeetingAttendee);
        ToggleDirectTranscriptionCommand = new AsyncRelayCommand(ExecuteToggleDirectTranscription);
        CaptureScreenCommand = new AsyncRelayCommand(ExecuteCaptureScreen, CanCaptureScreen);

        _ttsService.IsPlayingChanged += OnTtsPlayingChanged;
        _personaService.PersonasChanged += OnPersonasChanged;
        _personaService.ManagedPersonaWithdrawn += OnManagedPersonaWithdrawn;
        PropertyChanged += OnPropertyChanged;
        // The field initializer's collection never goes through OnMessagesChanged, so an un-started chat
        // would mutate outside the window until the first session attach re-points it.
        Messages.CollectionChanged += OnMessagesCollectionChanged;
        PendingFiles.CollectionChanged += OnPendingFilesChanged;
        PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
        MeetingAttendee.CloseRequested += OnMeetingAttendeeCloseRequested;
        MeetingAttendee.SummarizeRequested += OnMeetingAttendeeSummarizeRequested;
        MeetingAttendee.OpenSettingsRequested += OnMeetingAttendeeOpenSettingsRequested;
        DirectTranscription.CloseRequested += OnDirectTranscriptionCloseRequested;
        DirectTranscription.SummarizeRequested += OnDirectTranscriptionSummarizeRequested;

        ChatTitleChip = new ChatTitleChipViewModel(
            _chatService,
            _localizationService,
            _loggerFactory.CreateLogger<ChatTitleChipViewModel>(),
            ResumeChatAsync,
            DeleteChatFromChipAsync,
            RenameChatFromChipAsync,
            NewChat,
            NavigateToAssistantHistory,
            _chatSessionManager.GetState,
            _workingDirectoryService,
            SetActiveWorkingDirectory,
            GetActiveWorkingDirectory);

        _chatSessionManager.ActiveChanged += OnActiveSessionChanged;
        _chatSessionManager.SessionTitleChanged += OnSessionTitleChanged;
        _chatSessionManager.SessionStateChanged += OnManagerSessionStateChanged;

        _providerService.ProvidersChanged += OnProvidersChangedForScreenCapture;
        _settingsService.SettingsChanged += OnSettingsChangedForScreenCapture;
        StartScreenCaptureAvailabilityRefresh();

        // Always mirror a live session so send/cancel never null-ref on a fresh
        // window. GetOrCreateActiveForNewChat raises ActiveChanged → AttachToActiveSession.
        _chatSessionManager.GetOrCreateActiveForNewChat();

        // Pin the initial chat to the configured default working directory (e.g. "Playground").
        // Done off the ctor (fire-and-forget, mirroring LoadPersonasAsync) so the synchronous
        // settings-load + folder-create never stalls window-open on the UI thread. It cascades to
        // "+ New chat" and Clear for free — the chip re-seeds its pending folder from the active
        // chat's dir on flyout-open.
        ApplyDefaultWorkingDirectoryAsync().SafeFireAndForget(_logger);
        ApplyMeetingFeaturePolicyAsync().SafeFireAndForget(_logger);
    }

    private async Task ApplyMeetingFeaturePolicyAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        await _uiDispatcher.PostAsync(() =>
        {
            IsMeetingAttendeeAvailable = settings.MeetingAttendeeEnabled;
            IsDirectTranscriptionAvailable = settings.DirectTranscriptionEnabled;
        });
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

        await _uiDispatcher.PostAsync(() =>
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
        // Only a real move between chats, so the first attach and a re-select of the open chat leave the
        // composer alone.
        var switchedChat = _subscribedSession is not null && !ReferenceEquals(_subscribedSession, session);

        if (_subscribedSession is { } prev)
        {
            prev.StateChanged -= OnActiveSessionStateChanged;
            prev.TurnCompleted -= OnActiveSessionTurnCompleted;
            prev.ToolSucceeded -= OnActiveSessionToolSucceeded;
            prev.RunFailed -= OnActiveSessionRunFailed;
            prev.ActiveRunChanged -= OnActiveRunChanged;
            prev.ForeignRunActiveChanged -= OnForeignRunActiveChanged;
            prev.AgentModeChanged -= OnSessionAgentModeChanged;
            prev.PlanApprovalParkActiveChanged -= OnPlanApprovalParkActiveChanged;
        }

        _subscribedSession = session;
        session.StateChanged += OnActiveSessionStateChanged;
        session.TurnCompleted += OnActiveSessionTurnCompleted;
        session.ToolSucceeded += OnActiveSessionToolSucceeded;
        session.RunFailed += OnActiveSessionRunFailed;
        session.ActiveRunChanged += OnActiveRunChanged;
        session.ForeignRunActiveChanged += OnForeignRunActiveChanged;
        session.AgentModeChanged += OnSessionAgentModeChanged;
        session.PlanApprovalParkActiveChanged += OnPlanApprovalParkActiveChanged;
        SyncRunProgress(session.ActiveRunId); // embed the panel if this session already has a run
        ForeignRunActive = session.ForeignRunActive; // late attach: read the flag the manager already seeded
        PlanApprovalParkActive = session.PlanApprovalParkActive;

        // An attachment was staged for the chat the user was in, so it does not follow them to another one —
        // sending it into the wrong conversation is the worse failure, and it is a file they can re-drop.
        if (switchedChat)
        {
            PendingAttachments.Clear();
            PendingFiles.Clear();
            SeedAgentModeOnChatLoadAsync(session).SafeFireAndForget(_logger);
        }

        ActiveAgentContextMode = session.AgentContextMode;
        Messages = session.Messages;            // OnMessagesChanged swaps CollectionChanged and rebuilds the window
        HasMessages = session.Messages.Count > 0;
        IsStreaming = session.IsStreaming;
        ActiveState = session.State;
        ChatTitleChip.SetTitle(session.Title);
        ChatTitleChip.SetWorkingDirectory(session.WorkingDirectory);
        // Scope the @Files autocomplete to this chat's dir (it runs outside any turn).
        _filesToolHandler.ActiveUiWorkingSubpath = session.WorkingDirectory;
        // After HasMessages, which is half of what the trigger reads.
        RefreshAgentContextBanner();
    }

    // The session raises ActiveRunChanged on the UI thread (its Planned branch runs there), but marshal
    // defensively so a future off-thread caller can't touch the bound RunProgressViewModel cross-thread.
    private void OnActiveRunChanged(object? sender, Guid? runId) =>
        _uiDispatcher.Post(() => SyncRunProgress(runId));

    // The manager already marshals the flip to the UI thread (G3); marshal defensively anyway, for the same
    // reason OnActiveRunChanged does — this sets a bound property and re-evaluates a command.
    private void OnForeignRunActiveChanged(object? sender, bool active) =>
        _uiDispatcher.Post(() => ForeignRunActive = active);

    private void OnPlanApprovalParkActiveChanged(object? sender, bool active) =>
        _uiDispatcher.Post(() => PlanApprovalParkActive = active);

    // Unguarded, like the run-settled fall-back: the write back to the session is a no-op at the same value,
    // and the rest of the handler is what clears the Agent-mode hint and the weak-provider adorner for a
    // mode the composer has just left.
    private void OnSessionAgentModeChanged(object? sender, bool enabled) =>
        _uiDispatcher.Post(() => AgentModeEnabled = enabled);

    // Internal so the lever facts can attach the panel to a stubbed run without a whole ChatSession.
    internal void SyncRunProgress(Guid? runId)
    {
        if (_runProgress?.RunId == runId)
            return;
        if (_runProgress is not null)
        {
            _runProgress.RunSettled -= OnRunProgressSettled;
            _runProgress.PropertyChanged -= OnRunProgressPropertyChanged;
            _runProgress.Dispose(); // unsubscribes the prior RunChanged handler
        }
        _runProgress = runId is { } id
            ? new RunProgressViewModel(_agentRunService, id, _localizationService, _resumeService, _logger,
                _agentTimelineService, _runWorkspaces, _personaService, _steering, _themeService, _timelineWatcher,
                _navigationService, _toolCalls)
            : null;
        if (_runProgress is not null)
        {
            _runProgress.RunSettled += OnRunProgressSettled;
            _runProgress.PropertyChanged += OnRunProgressPropertyChanged;
        }
        ActiveRunProgress = _runProgress;
        RefreshAgentContextBanner();
    }

    // Not RunSettled: that event arms itself on a non-terminal State CHANGE, and Planning is the field's
    // default — so a run that fails while planning never raises it, and the offer the gate below suppressed
    // would stay suppressed for the rest of the chat.
    private void OnRunProgressPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunProgressViewModel.State))
            RefreshAgentContextBanner();
    }

    // A finished run must not silently arm the NEXT send as a fresh run: the lever falls back to Chat so a
    // follow-up message lands in the conversation instead of replacing the settled header with a new one.
    private void OnRunProgressSettled() => AgentModeEnabled = false;

    partial void OnMessagesChanged(ObservableCollection<AssistantMessage>? oldValue, ObservableCollection<AssistantMessage> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnMessagesCollectionChanged;
        newValue.CollectionChanged += OnMessagesCollectionChanged;
        RebuildMessageWindow();
    }

    private void OnMessagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        HasMessages = Messages.Count > 0;

        switch (e.Action)
        {
            // The tail is always in the window, so an append needs no index arithmetic; anything else
            // landing mid-transcript is rare enough to be worth a rebuild rather than a second code path.
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add
                when e.NewItems is not null && e.NewStartingIndex + e.NewItems.Count == Messages.Count:
                foreach (AssistantMessage message in e.NewItems)
                    VisibleMessages.Add(message);
                break;

            // A removal below the window leaves it alone; one inside it — regenerating truncates back to the
            // prompt — would otherwise leave a near-empty transcript with the rest hidden behind the button.
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (AssistantMessage message in e.OldItems)
                    VisibleMessages.Remove(message);
                MessageWindow.TopUp(VisibleMessages, Messages);
                break;

            default:
                RebuildMessageWindow();
                return;
        }

        UpdateOlderMessageCount();
    }

    private void RebuildMessageWindow()
    {
        MessageWindow.Reset(VisibleMessages, Messages);
        UpdateOlderMessageCount();
    }

    private void UpdateOlderMessageCount()
    {
        OlderMessageCount = Messages.Count - VisibleMessages.Count;
        HasOlderMessages = OlderMessageCount > 0;
    }

    [RelayCommand]
    private void LoadOlderMessages()
    {
        MessageWindow.PrependOlder(VisibleMessages, Messages);
        UpdateOlderMessageCount();
    }

    private void OnActiveSessionStateChanged(object? sender, ChatStateChangedEventArgs e)
    {
        ActiveState = e.NewState;
        IsStreaming = _subscribedSession?.IsStreaming ?? false;
    }

    // Mirror the active state onto the chip badge (single sink — both the attach
    // path and live transitions set ActiveState, so this stays in sync).
    partial void OnActiveStateChanged(ChatState value) => ChatTitleChip.SetState(value);

    partial void OnHasMessagesChanged(bool value)
    {
        DeleteCurrentChatCommand.NotifyCanExecuteChanged();
        RefreshAgentContextBanner();
    }

    partial void OnIsStreamingChanged(bool value) => RefreshAgentContextBanner();

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

        ReportVolatileWork();
    }

    partial void OnIsMeetingAttendeeVisibleChanged(bool value) => ReportVolatileWork();

    partial void OnIsDirectTranscriptionVisibleChanged(bool value) => ReportVolatileWork();

    partial void OnIsVoiceModeActiveChanged(bool value) => ReportVolatileWork();

    // An open transcript overlay counts whatever the capture state is, because Save only lights up after
    // Stop; voice mode counts because it streams without ever creating a session.
    private void ReportVolatileWork() => _volatileWork?.Report(
        this,
        IsDirectTranscriptionVisible || IsMeetingAttendeeVisible || IsVoiceModeActive
            || _chatSessionManager.IsAnyStreaming);

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

    // Deliberately does NOT re-seed the lever: the sync pull loop raises this on every cycle, and a re-seed
    // there undoes the run-settled fall-back to Chat minutes after it happened.
    private void OnPersonasChanged(object? sender, EventArgs e) =>
        LoadPersonasAsync().SafeFireAndForget(_logger);

    // Set by the withdrawal event, consumed by the very next LoadPersonasAsync. Deliberately not
    // persisted: clearing the dangling per-mode selection inside
    // PersonaService.ReplaceManagedPersonasAsync is what makes the notice one-shot — the next replace can
    // no longer find the withdrawn id in ModePersonaDefaults, so the same withdrawal cannot be detected
    // twice and no "already told them" flag is needed.
    private ManagedPersonaWithdrawnEventArgs? _pendingWithdrawnPersona;

    // Stash, don't show. The message names the fallback persona, which is only known once
    // LoadPersonasAsync has resolved ActivePersona — and PersonaService raises this BEFORE
    // PersonasChanged precisely so the reload it triggers can pick the stash up.
    private void OnManagedPersonaWithdrawn(object? sender, ManagedPersonaWithdrawnEventArgs e) =>
        _pendingWithdrawnPersona = e;

    private async Task LoadPersonasAsync(bool seedAgentMode = false)
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
            await _uiDispatcher.PostAsync(() =>
            {
                AvailablePersonas.Clear();
                foreach (var persona in personas)
                    AvailablePersonas.Add(persona);

                ActivePersona = AvailablePersonas.FirstOrDefault(p => p.Id == active.Id) ?? active;

                // The lever is per-chat, so the startup seed needs the chat as well as the default.
                if (seedAgentMode && _chatSessionManager.ActiveSession is { } leverSession)
                    SeedAgentMode(leverSession, settings);

                // Inside the posted lambda so the snackbar is raised on the UI thread, and after
                // ActivePersona so the notice names the fallback the user is actually now on.
                ShowPendingWithdrawnPersonaNotice();
            });
        }
        finally
        {
            _isLoadingPersonas = false;
        }
    }

    /// <summary>
    /// Surfaces the one-shot notice for a selected managed persona that the org withdrew (§5.1), if this
    /// reload is the one that followed the withdrawal. Informational and non-blocking on purpose: no modal,
    /// and nothing is cancelled — the server freezes resource scope per activation, so a turn already
    /// streaming completes under the old scope.
    /// </summary>
    private void ShowPendingWithdrawnPersonaNotice()
    {
        if (_pendingWithdrawnPersona is not { } withdrawn)
            return;

        // A reload that STARTED before the withdrawal can reach this point holding pre-replace data: the
        // pull raises PersonasChanged once per applied user persona, well before it applies the managed
        // snapshot, and LoadPersonasAsync is fire-and-forget, so its posted lambda can land after the stash
        // was set. Showing the notice then would name the withdrawn persona as its own fallback and burn the
        // one shot. Leave it pending instead — ReplaceManagedPersonasAsync always raises PersonasChanged
        // after the withdrawal event, so a reload that sees the real fallback is guaranteed to follow.
        if (ActivePersona is { } stillActive && stillActive.Id == withdrawn.PersonaId)
            return;

        // Clear before showing: a throw out of Show must not leave the notice pending and re-fire it on
        // every later persona reload.
        _pendingWithdrawnPersona = null;

        var fallbackName = ActivePersona?.Name ?? string.Empty;
        _logger.LogInformation(
            "Selected managed persona {PersonaId} was withdrawn; fell back to the resolved persona",
            withdrawn.PersonaId);
        // Persona names are admin-authored user content — fine in the snackbar, never in the log file.
        _logger.SensitiveDebug(
            "Withdrawn managed persona {PersonaId} name: {Name}, fallback: {Fallback}",
            withdrawn.PersonaId, withdrawn.PersonaName, fallbackName);

        _snackbarService.Show(
            _localizationService["Settings_Tab_Personas"],
            _localizationService.Format(
                "Msg_Settings_ManagedPersonaWithdrawn", withdrawn.PersonaName, fallbackName),
            Wpf.Ui.Controls.ControlAppearance.Info, null, TimeSpan.FromSeconds(6));
    }

    /// <summary>Adopts the lever of the chat being activated, falling back to the new-chat default for a
    /// chat whose lever was never touched. Guarded so the switch does not read as the user flipping it.
    /// Internal seam so the guard can be exercised without the whole activation path.</summary>
    internal void SeedAgentMode(ChatSession session, AppSettings settings)
    {
        _isLoadingAgentMode = true;
        try { AgentModeEnabled = session.AgentModeEnabled ?? settings.AssistantNewChatAgentMode; }
        finally { _isLoadingAgentMode = false; }
    }

    private async Task SeedAgentModeOnChatLoadAsync(ChatSession session)
    {
        var settings = await _settingsService.GetSettingsAsync();
        await _uiDispatcher.PostAsync(() => SeedAgentMode(session, settings));
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
        // ABOVE the seed guard: a regular agent user never toggles the lever — the seed is what turns the
        // mode on for them — so a trigger that only fired on a toggle would never fire at all.
        RefreshAgentContextBanner();
        if (_isLoadingAgentMode)
            return;
        // The lever belongs to THIS chat, so nothing here reaches settings: one curious flip must not arm
        // every chat the user opens afterwards.
        if (_chatSessionManager.ActiveSession is { } session)
            session.AgentModeEnabled = value;
        // Warning-first (§14.4): surface the subtle Weak-provider adorner when flipping to Agent.
        if (value)
        {
            EvaluateProviderWarningAsync().SafeFireAndForget(_logger);
            ShowAgentModeHint();
        }
        else
        {
            WeakProviderWarningVisible = false;
            HideAgentModeHint();
        }
    }

    // Seeding the lever from settings returns before this (the first guard above), so the hint marks a switch
    // the user made rather than every app start. The settle fall-back does NOT return: it reaches
    // HideAgentModeHint below, which is the arm it needs.
    private void ShowAgentModeHint()
    {
        var generation = ++_agentHintGeneration;
        AgentModeHintVisible = true;
        _ = HideAgentModeHintAfterDelayAsync(generation);
    }

    // Bumps the generation so a pending expiry can never hide a hint shown after it.
    private void HideAgentModeHint()
    {
        _agentHintGeneration++;
        AgentModeHintVisible = false;
    }

    private async Task HideAgentModeHintAfterDelayAsync(int generation)
    {
        await Task.Delay(AgentModeHintDuration);
        _uiDispatcher.PostOrRun(() =>
        {
            if (generation == _agentHintGeneration)
                AgentModeHintVisible = false;
        });
    }

    // One line in the composer, three claimants, most concrete first: "this goal won't run" beats "an
    // attached file blocks a run", which beats the general explanation of the mode.
    partial void OnGoalTooShortHintVisibleChanged(bool value)
    {
        if (value)
        {
            AgentModeHintVisible = false;
            PendingFilesBlockRunHintVisible = false;
        }
        else
        {
            RefreshPendingFilesHint();
        }
    }

    // Weak-provider warning surface (§14.4). Set true when the active provider is not Capable of tool
    // calling for Agent planning; drives the subtle adorner on the Agent segment + the composer banner.
    // Never blocks a Planned send (R10). Populated by EvaluateProviderWarningAsync (Commit Group 2).
    [ObservableProperty]
    private bool _weakProviderWarningVisible;

    // A collection mutation notifies the collection, never this VM, so the name filter below cannot see
    // one: without this Send stays disabled and no hint explains it.
    private void OnPendingFilesChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingFiles));
        SendMessageCommand.NotifyCanExecuteChanged();
        RunInBackgroundCommand.NotifyCanExecuteChanged();
        RefreshPendingFilesHint();
        DropFailureMessage = null;
    }

    // Same blind spot as OnPendingFilesChanged: the [ObservableProperty] this collection replaced re-raised
    // CanExecute for free, so without this Send stays disabled with an image attached.
    private void OnPendingAttachmentsChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendMessageCommand.NotifyCanExecuteChanged();
        RunInBackgroundCommand.NotifyCanExecuteChanged();
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InputText)) DropFailureMessage = null;

        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming)
            or nameof(ForeignRunActive) or nameof(PlanApprovalParkActive) or nameof(AgentContextChoicePending))
        {
            SendMessageCommand.NotifyCanExecuteChanged();
            RunInBackgroundCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming) or nameof(AgentModeEnabled))
        {
            RefreshGoalTooShortHint();
            RefreshPendingFilesHint();
        }

        if (e.PropertyName is nameof(IsStreaming) or nameof(IsVoiceModeActive))
        {
            EnterVoiceModeCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(IsScreenCaptureAvailable))
        {
            CaptureScreenCommand.NotifyCanExecuteChanged();
        }

        // A turn is under way, so the explanation of what a send would do has been answered.
        if (e.PropertyName is nameof(IsStreaming) && IsStreaming)
        {
            HideAgentModeHint();
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

        if (!IsMeetingAttendeeAvailable)
            return;

        // Direct transcription and meeting-attendee both own the local audio stack, so only one may be
        // open at a time — opening this one closes the other first.
        if (IsDirectTranscriptionVisible)
        {
            await DirectTranscription.StopAsync();
            IsDirectTranscriptionVisible = false;
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

    private async Task ExecuteToggleDirectTranscription()
    {
        // Mirrors ExecuteToggleMeetingAttendee: hiding stops any in-progress session, showing warms up
        // the service first. Direct transcription and the meeting attendee share the local audio stack
        // (mic + system loopback), so only one overlay may be open at a time.
        if (IsDirectTranscriptionVisible)
        {
            await DirectTranscription.StopAsync();
            IsDirectTranscriptionVisible = false;
            return;
        }

        if (!IsDirectTranscriptionAvailable)
            return;

        if (IsMeetingAttendeeVisible)
        {
            await MeetingAttendee.StopAsync();
            IsMeetingAttendeeVisible = false;
        }

        await DirectTranscription.PrepareForDisplayAsync();
        IsDirectTranscriptionVisible = true;
    }

    private void OnDirectTranscriptionCloseRequested(object? sender, EventArgs e)
    {
        // The overlay's close (X) button raises CloseRequested; stop the session and hide the overlay.
        // Routed through the COMMAND, not the method: AsyncRelayCommand refuses to run while a previous
        // execution is still in flight, so clicking X and then the toolbar toggle cannot start two
        // overlapping hide bodies (two StopAsync calls racing each other inside StopReaderAsync, which
        // nulls its reader CTS/task without synchronization).
        if (ToggleDirectTranscriptionCommand.CanExecute(null))
            ToggleDirectTranscriptionCommand.Execute(null);
    }

    private void OnDirectTranscriptionSummarizeRequested(object? sender, string prompt)
    {
        // "Summarize with assistant" on the post-session transcript: hide the overlay so the chat (where
        // the summary streams) is revealed, open a fresh chat so the summary stands on its own, then send
        // the prompt. Mirrors OnMeetingAttendeeSummarizeRequested; do NOT log the prompt — it carries the
        // (sensitive) transcript.
        IsDirectTranscriptionVisible = false;
        StartFreshChat();
        PendingAttachments.Clear();
        InputText = prompt;
        SendMessageCommand.Execute(null);
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
        // the prompt (StartFreshChat clears InputText but not the staged images).
        PendingAttachments.Clear();
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
        !IsStreaming && !ForeignRunActive && !PlanApprovalParkActive && !AgentContextChoicePending
        && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0 || PendingFiles.Count > 0);

    /// <summary>Nothing typed, attached or in flight — the hotkey may tuck the window away.</summary>
    public bool CanDismissWithHotkey =>
        string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0 && PendingFiles.Count == 0
        && !IsStreaming && !ForeignRunActive && !PlanApprovalParkActive;

    // Factored out so the gate below and the hint that explains it cannot drift out of sync.
    private bool HasCandidateGoalText() => !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    private bool GoalTooShortHolds() =>
        AgentModeEnabled && HasCandidateGoalText() && GoalPreflight.IsRefused(InputText);

    // The hint appears one idle second after the last keystroke so it never pops mid-typing, but hides
    // immediately. A generation counter supersedes the pending delay: cancelling a Task.Delay per keystroke
    // would throw a first-chance TaskCanceledException into the debug output on every keypress.
    private void RefreshGoalTooShortHint()
    {
        var generation = ++_goalHintGeneration;
        if (!GoalTooShortHolds())
        {
            GoalTooShortHintVisible = false;
            return;
        }

        if (GoalTooShortHintVisible)
            return;

        _ = ShowGoalTooShortHintDebouncedAsync(generation);
    }

    private async Task ShowGoalTooShortHintDebouncedAsync(int generation)
    {
        await Task.Delay(GoalTooShortHintDebounce);
        _uiDispatcher.PostOrRun(() =>
        {
            if (generation == _goalHintGeneration)
                GoalTooShortHintVisible = GoalTooShortHolds();
        });
    }

    // Agent mode only: in Chat the Run-in-background button is hidden and no turn was going to be
    // planned, so both halves of the sentence it renders would be false.
    private bool PendingFilesBlockRunHolds() =>
        PendingFiles.Count > 0 && !IsStreaming && AgentModeEnabled;

    // Assigned here rather than from an On...Changed hook: [ObservableProperty] short-circuits on equality,
    // so an already-visible hint would never re-clear the agent-mode hint a lever flip had just raised.
    private void RefreshPendingFilesHint()
    {
        PendingFilesBlockRunHintVisible = !GoalTooShortHintVisible && PendingFilesBlockRunHolds();
        if (PendingFilesBlockRunHintVisible)
            AgentModeHintVisible = false;
    }

    // Never offers more than Send: the same availability gate, plus real text (it ignores the image, unlike
    // Send), a non-refused goal, and no attached file — a detached run has no way to carry one.
    private bool CanExecuteRunInBackground() =>
        CanExecuteSendMessage() && HasCandidateGoalText() && PendingFiles.Count == 0
        && !GoalPreflight.IsRefused(InputText);

    private async Task ExecuteSendMessage()
    {
        // The provider is read again here because it can change between attaching a picture and sending it,
        // and the picture may be a grab of the whole screen.
        if (PendingAttachments.Count > 0 && !await AssistantProviderTakesImagesAsync())
        {
            WarnImageProviderUnsupported();
            return;
        }

        var userText = InputText.Trim();
        InputText = string.Empty;
        var attachments = PendingAttachments.ToArray();
        PendingAttachments.Clear();
        // Captured above the Clear, and read again at the planned line below.
        var files = PendingFiles.ToArray();
        var attachedFileContext = files.Length > 0
            ? AssistantPromptComposer.BuildAttachedFileBlock(files)
            : null;
        // Names (and the sandbox path of the ones that were saved) outlive the send as chips under the
        // bubble; the extracted text above does not, so a resumed chat shows what was attached but no
        // longer feeds it to the model.
        var attachedFiles = files.Length == 0
            ? null
            : files.Select(f => new AttachedFileRef(f.FileName, f.SavedRelativePath)).ToArray();
        PendingFiles.Clear();

        var session = _chatSessionManager.ActiveSession
            ?? _chatSessionManager.GetOrCreateActiveForNewChat();

        // Defence in depth on the lever: a no-tools persona can never plan, and neither can an attached
        // file — the planner sees only the goal string, and an approved plan resumes headless without it.
        var planned = files.Length == 0
            && AgentModeEnabled && ActivePersona?.ToolScope != PersonaToolScope.None;

        // Awaited so the AsyncRelayCommand's running-state blocks re-entry; StartTurnAsync
        // returns once the turn is fire-and-forgotten (Step 4-compatible).
        var accepted = await _chatSessionManager.StartTurnAsync(
            session, userText, attachments, planned: planned,
            attachedFileContext: attachedFileContext, attachedFiles: attachedFiles);

        // A refused send consumed nothing, so put the composer back rather than dropping what was typed —
        // reachable in the window between a plan-approval park releasing the session and the flag landing.
        if (!accepted)
        {
            InputText = userText;
            foreach (var attachment in attachments) PendingAttachments.Add(attachment);
            foreach (var file in files) PendingFiles.Add(file);
        }
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

        // Asked before the composer is cleared, so backing out of the dialog leaves the goal where it was.
        if (!await ConfirmBackgroundRunAsync()) return;

        InputText = string.Empty;
        try
        {
            // The detached run inherits the composer chat's working directory, mirroring the live turn path.
            await _chatSessionManager.StartBackgroundRunAsync(
                userText, _chatSessionManager.ActiveSession?.WorkingDirectory);

            // Nothing else marks the detach: no live session appears and the run's own Flow item is only
            // published when it settles, which is what made the button look dead.
            _snackbarService.Show(
                _localizationService["Assistant_RunInBackground_Queued"],
                _localizationService["Assistant_RunInBackground_Queued_Body"],
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(4));
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

    /// <summary>
    /// Puts the unattended run to the user once, unless they ticked "don't ask again" — the tick is honoured
    /// only on a Yes, since a No means they did not want this run, not that they want the next one to start
    /// unasked. Stored in <see cref="AppSettings.AssistantBackgroundRunConfirmSuppressed"/>, which is outside
    /// the settings sync projection, so the decision stays on this device.
    /// </summary>
    private async Task<bool> ConfirmBackgroundRunAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (settings.AssistantBackgroundRunConfirmSuppressed) return true;

        OptOutConfirmation answer;
        try
        {
            answer = await _dialogService.ShowOptOutConfirmationDialogAsync(
                _localizationService["BackgroundRunConfirm_Title"],
                _localizationService["BackgroundRunConfirm_Body"],
                _localizationService["BackgroundRunConfirm_Start"]);
        }
        catch (Exception ex)
        {
            // Never shown is not the same as approved (and not a launch failure either — reporting it as one
            // would blame the run for a missing dialog host).
            _logger.LogWarning(ex, "The background-run confirmation could not be shown");
            return false;
        }

        if (!answer.Confirmed) return false;

        if (answer.DontAskAgain)
        {
            settings.AssistantBackgroundRunConfirmSuppressed = true;
            await _settingsService.SaveSettingsAsync(settings);
        }

        return true;
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
        RevokeAnyPendingPause();
        _chatSessionManager.ActiveSession?.Cancel();
    }

    /// <summary>
    /// Both callers cancel the active session with TERMINAL intent, and <c>ChatSession.Cancel()</c> is also
    /// the sink a user PAUSE fires — so an unconsumed pause request sitting behind this cancel would be read
    /// by the run's loop as "the user asked to pause", and a run the user pressed Stop on would come back
    /// <c>Paused</c> and resumable instead of settling <c>Cancelled</c>. Revoke FIRST, always: after the
    /// cancel the step may already have unwound and consumed it. The direction matters — a lost pause is
    /// recoverable (press Pause again), a Stop read as a pause is not what the user asked for.
    /// <para>
    /// No-op when the session carries no run (an ordinary chat turn) or when no request is pending, and it
    /// deliberately does NOT touch the sink registration: the dispatch owns that and releases it itself.
    /// </para>
    /// </summary>
    private void RevokeAnyPendingPause()
    {
        if (_chatSessionManager.ActiveSession?.ActiveRunId is { } runId)
            _runSteering?.RevokePauseRequest(runId);
    }

    /// <summary>
    /// "New chat" (additive): open a fresh chat WITHOUT cancelling the current turn — the
    /// running turn keeps streaming in the background and notifies on completion (the
    /// background-chats contract: opening or switching a chat never kills an in-flight
    /// turn). Cancelling here would abort the in-flight HTTP request — surfacing a
    /// SocketException in the support log — and discard the background result.
    /// </summary>
    private void NewChat(string? workingDirectory) => StartFreshChat(workingDirectory);

    /// <summary>The composer's "+" — same additive contract as <see cref="NewChat"/>, opening in the
    /// folder this chat works in.</summary>
    private void ExecuteNewChat() => NewChat(_chatSessionManager.ActiveSession?.WorkingDirectory);

    /// <summary>Opens a new, empty active chat and resets the composer. Shared by the chip's
    /// "+ New Chat" (its pinned folder), the composer's "+" (this chat's folder) and the delete
    /// path (the deleted chat's folder).</summary>
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
        PendingFiles.Clear();

        // The empty state is back, and the stores may have moved since it was last on screen.
        RefreshSuggestionsAsync().SafeFireAndForget(_logger);
    }

    private async Task ResumeChatAsync(Guid chatId)
    {
        _ttsService.Stop();
        await _chatSessionManager.ActivateAsync(chatId);
    }

    /// <summary>Deletes the chat the composer is on, then opens a fresh one.</summary>
    private async Task ExecuteDeleteCurrentChat()
    {
        var session = _chatSessionManager.ActiveSession;
        if (session?.Id is { } chatId)
        {
            await DeleteChatFromChipAsync(chatId);
            return;
        }

        // No row to delete: the id is assigned by the first persist, which a chat mid-first-turn has not
        // reached yet. Discarding it locally is the whole of "delete" for such a chat.
        if (!await ConfirmChatDeleteAsync()) return;
        AbandonActiveChat(session?.WorkingDirectory);
    }

    /// <summary>Nothing to delete while the chat is still empty — a fresh one is what deleting would leave.</summary>
    private bool CanDeleteCurrentChat() => HasMessages;

    /// <summary>Quick-delete of one chat (title-chip flyout or the composer's delete button);
    /// deleting the open chat moves to a fresh one.</summary>
    private async Task DeleteChatFromChipAsync(Guid chatId)
    {
        if (!await ConfirmChatDeleteAsync()) return;

        // Capture before the swap: StartFreshChat re-points ActiveSession.
        var deletesOpenChat = _chatSessionManager.ActiveSession?.Id == chatId;
        var inheritedDir = deletesOpenChat ? _chatSessionManager.ActiveSession?.WorkingDirectory : null;

        await _chatService.DeleteAsync(chatId);
        _logger.LogInformation("Deleted assistant chat {ChatId}", chatId);

        if (!deletesOpenChat) return;
        AbandonActiveChat(inheritedDir);
    }

    /// <summary>Persists an inline rename from the flyout and puts the name on the live session too — a
    /// later save from that session would otherwise write the old one back.</summary>
    private async Task<bool> RenameChatFromChipAsync(Guid chatId, string title)
    {
        try
        {
            if (!await _chatService.SetTitleAsync(chatId, title)) return false;

            _chatSessionManager.ApplyExternalTitle(chatId, title);
            _logger.LogInformation("Renamed assistant chat {ChatId} from the flyout", chatId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename chat {ChatId} from the flyout", chatId);
            return false;
        }
    }

    private Task<bool> ConfirmChatDeleteAsync() => _dialogService.ShowConfirmationDialogAsync(
        _localizationService["Msg_History_ConfirmDeleteTitle"],
        _localizationService["Msg_History_ConfirmDeleteMessage"]);

    /// <summary>Drops the open chat and swaps a fresh one in its folder. The cancel is not optional: the
    /// abandoned turn's terminal persist would resurrect the row that was just deleted.</summary>
    private void AbandonActiveChat(string? inheritedDir)
    {
        RevokeAnyPendingPause();
        _chatSessionManager.ActiveSession?.Cancel();
        StartFreshChat(inheritedDir);
    }

    private void NavigateToAssistantHistory()
    {
        _navigationService.NavigateTo<AssistantHistoryViewModel>();
    }

    /// <summary>Reads the active chat's working dir (relative, forward slashes; null = root) for the chip.</summary>
    private string? GetActiveWorkingDirectory() => _chatSessionManager.ActiveSession?.WorkingDirectory;

    /// <summary>
    /// Re-points the active chat's working dir from the empty state's picker — the only one that
    /// aims this chat, and it is gone by the first message. The guard stays anyway: a started chat
    /// keeps the folder its turns ran in. Unlike <see cref="ChatSession.ProviderId"/> (which persists
    /// only as a turn side-effect), a working-dir change can happen with no turn, so this triggers an
    /// explicit persist.
    /// </summary>
    private void SetActiveWorkingDirectory(string? relativePath)
    {
        var session = _chatSessionManager.ActiveSession;
        if (session is null) return;

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
        // W2c: ForeignRunActive blocks this for the same reason it blocks Send — the turn below writes THIS
        // chat, and the persist at the end of it is a full replace built from session.Messages, so it would
        // delete every row the headless run has appended since. Regenerate is worse than Send, in fact: it
        // also truncates the transcript the run is still extending.
        if (message is null || IsStreaming || ForeignRunActive || PlanApprovalParkActive) return;

        var idx = Messages.IndexOf(message);
        if (idx <= 0) return;

        var prior = Messages[idx - 1];
        if (prior.Role != ChatRole.User) return;
        if (string.IsNullOrWhiteSpace(prior.Content) && prior.Attachments.Count == 0
            && string.IsNullOrEmpty(prior.AttachedFileContext)) return;

        CancelPendingActionCards(message);

        var prompt = prior.Content;
        var attachments = prior.Attachments.ToArray();
        var attachedFileContext = prior.AttachedFileContext;
        // Captured before the removal below, which takes the answer a styled instruction has to quote.
        var previousAnswer = message.Content;
        for (var i = Messages.Count - 1; i >= idx - 1; i--)
            Messages.RemoveAt(i);

        if (Messages.Count == 0) HasMessages = false;

        var session = _chatSessionManager.ActiveSession
            ?? _chatSessionManager.GetOrCreateActiveForNewChat();

        await _chatSessionManager.StartTurnAsync(
            session, prompt, attachments, RegenerateInstructions.For(style, previousAnswer),
            attachedFileContext: attachedFileContext);
    }

    /// <summary>
    /// Asks where the answer should go before writing anything. "Store in vault" files it as Markdown under
    /// the vault's Exports folder — where it is indexed for recall; "External" renders the HTML document and
    /// writes it wherever the Save dialog lands.
    /// </summary>
    private async Task ExecuteExportMessage(AssistantMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Content))
            return;

        var fallbackTitle = _localizationService["Msg_Assistant_ExportDefaultTitle"];
        var edit = new AnswerExportEditModel(_markdownExportService.SuggestFileName(message.Content, fallbackTitle));

        var destination = await _dialogService.ShowAnswerExportDialogAsync(edit);
        if (destination == AnswerExportDestination.Cancel)
            return;

        try
        {
            var path = destination == AnswerExportDestination.Vault
                ? await _markdownExportService.ExportToVaultAsync(message.Content, edit.FileName, fallbackTitle)
                : await ExportExternallyAsync(message, edit.FileName, fallbackTitle);

            // Null only from a cancelled Save dialog — the user backed out, so say nothing.
            if (path is null)
                return;

            // Surface the written file as an open-file/open-folder chip.
            message.AddOrUpgradeFileRef(new FileRef(path, FileRefKind.Exported));
            if (edit.OpenAfterStorage)
                ShellLauncher.OpenFile(path);

            _snackbarService.Show(
                _localizationService["Msg_Assistant_Exported"],
                _localizationService.Format("Msg_Assistant_ExportedTo", System.IO.Path.GetFileName(path)),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export message ({Destination})", destination);
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                _localizationService["Msg_Assistant_ExportFailed"],
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Save-As, then the HTML render. Returns null when the picker was cancelled.</summary>
    private async Task<string?> ExportExternallyAsync(AssistantMessage message, string fileName, string fallbackTitle)
    {
        var path = DebugPresetPath("PIA_DEBUG_ANSWER_EXPORT_FILE")
            ?? _fileDialogService?.PromptSaveFile(
                title: _localizationService["AnswerExport_SaveDialogTitle"],
                filter: _localizationService["AnswerExport_SaveDialogFilter"],
                defaultFileName: fileName + ".html",
                initialDirectory: null);
        if (string.IsNullOrEmpty(path))
            return null;

        await _markdownExportService.ExportToPathAsync(
            message.Content, path, fallbackTitle, message.Stats?.ProvenanceLabel);
        return path;
    }

    /// <summary>
    /// Dev-only: a preset path that stands in for the file picker, so a UI script can drive the real Export
    /// button without automating a native dialog. Always null in release.
    /// </summary>
    private static string? DebugPresetPath(string environmentVariable)
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } path ? path : null;
#else
        return null;
#endif
    }

    /// <summary>
    /// Thumbs-up sends a bare rating; thumbs-down opens the report dialog first. Both go to the connected
    /// Pia Cloud server — BYOK answers never reach here because the message hides the buttons.
    /// </summary>
    private async Task ExecuteRateMessage(AnswerRatingRequest? request)
    {
        if (request is null || _aiFeedback is null || !request.Message.IsRateable)
            return;

        try
        {
            var chatId = _chatSessionManager.ActiveSession?.Id;
            Shared.Models.AiFeedbackRequest report;
            if (request.Positive)
            {
                report = await _aiFeedback.BuildRequestAsync(
                    request.Message, chatId, Shared.Models.AiFeedbackRequest.RatingUp, comment: null, includeAnswer: false);
            }
            else
            {
                var settings = await _settingsService.GetSettingsAsync();
                var edit = new AiFeedbackEditModel(settings.Privacy.TokenizationEnabled);
                if (!await _dialogService.ShowAiFeedbackDialogAsync(edit))
                    return;

                report = await _aiFeedback.BuildRequestAsync(
                    request.Message, chatId, Shared.Models.AiFeedbackRequest.RatingDown, edit.Comment, edit.IncludeAnswer);
            }

            var sent = await _aiFeedback.SendAsync(report);
            _snackbarService.Show(
                _localizationService[sent ? "Msg_Assistant_FeedbackSent_Title" : "Msg_Error"],
                _localizationService[sent ? "Msg_Assistant_FeedbackSent" : "Msg_Assistant_FeedbackFailed"],
                sent ? Wpf.Ui.Controls.ControlAppearance.Success : Wpf.Ui.Controls.ControlAppearance.Danger,
                null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send AI feedback for message {MessageId}", request.Message.Id);
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

    /// <summary>The draw probes the local stores, so it is skipped whenever the empty state is hidden anyway.</summary>
    private async Task RefreshSuggestionsAsync()
    {
        if (_starterSuggestions is null || HasMessages) return;

        try
        {
            var drawn = await _starterSuggestions.DrawAsync(VisibleSuggestionCount);
            await _uiDispatcher.PostAsync(() =>
            {
                Suggestions.Clear();
                foreach (var suggestion in drawn) Suggestions.Add(suggestion);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to draw the empty-state suggestions");
        }
    }

    internal void ReportActivated(TimeSpan elapsed) =>
        _logger.LogInformation(
            "Assistant view activated in {ElapsedMs} ms with {MessageCount} messages",
            (long)elapsed.TotalMilliseconds, Messages.Count);

    internal void ReportTranscriptBuilt(TimeSpan elapsed) =>
        _logger.LogInformation(
            "Assistant transcript built in {ElapsedMs} ms showing {VisibleCount} of {MessageCount} messages",
            (long)elapsed.TotalMilliseconds, VisibleMessages.Count, Messages.Count);

    public void OnNavigatedTo(object? parameter)
    {
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
        // First, so nothing that throws below can decide whether this action row button appears.
        RefreshAssignmentSurfaceAsync().SafeFireAndForget(_logger);

        if (parameter is Guid chatId && chatId != Guid.Empty)
        {
            await ResumeChatAsync(chatId);
        }

        // After the activation above, which is what settles HasMessages for the resumed chat.
        await RefreshSuggestionsAsync();

        try
        {
            await LoadPersonasAsync(seedAgentMode: true);

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
                        await _uiDispatcher.PostAsync(() =>
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

        // Last, so a window the hotkey just created has settled before a dialog opens on top of it.
        if (parameter is ScreenCapturePickerRequest)
        {
            await OpenScreenCapturePickerFromHotkeyAsync();
        }
    }

    public void OnNavigatedFrom() { }

    internal async Task RefreshAssignmentSurfaceAsync()
    {
        if (_assignmentSurfaceCache is null) return;

        _assignmentSurface = await _assignmentSurfaceCache.RefreshAsync();

        await _uiDispatcher.PostAsync(() => IsAssignmentSurfaceAvailable = _assignmentSurface.Available);
    }

    /// <summary>Prefills the dialog from the composer and leaves the composer alone: what is typed here is a
    /// draft for either destination until the user affirms one.</summary>
    [RelayCommand]
    private async Task RunAssignmentAsync()
    {
        if (_assignmentConsentFactory is null || !_assignmentSurface.Available) return;

        var consent = _assignmentConsentFactory();

        try
        {
            await consent.InitializeAsync(_assignmentSurface, InputText);
            if (!await _dialogService.ShowAssignmentConsentDialogAsync(consent)) return;

            var status = await consent.SendAsync();
            _snackbarService.Show(
                _localizationService["Assignments_Title"],
                consent.ResultMessage,
                status == AssignmentStartStatus.Started
                    ? Wpf.Ui.Controls.ControlAppearance.Success
                    : Wpf.Ui.Controls.ControlAppearance.Caution,
                null,
                TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The background-assignment dialog could not be completed");
        }
    }

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

        var result = await DroppedFileAttachmentImporter.TryStageAsync(
            paths, PendingFiles, _logger, _snackbarService, _localizationService);

        foreach (var file in result.Staged)
            PendingFiles.Add(file);

        if (result.ImagePaths.Count > 0)
            await AttachImagesAsync(result.ImagePaths);
    }

    /// <summary>Copies a staged file into the chat's working directory so it survives the send and stays
    /// openable from history. Idempotent — an already-saved chip hides the button.</summary>
    private void ExecuteSavePendingFile(PendingFileAttachment? file)
    {
        if (file is null || file.IsSaved) return;

        var saved = _attachedFileStore?.SaveIntoWorkingDirectory(
            file.FullPath, _chatSessionManager.ActiveSession?.WorkingDirectory);

        if (saved is null)
        {
            _logger.LogWarning("Could not save a pending attachment into the working directory");
            _snackbarService.Show(
                _localizationService["Msg_Warning"],
                _localizationService.Format("Msg_File_SaveFailed", file.FileName),
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
            return;
        }

        file.SavedRelativePath = saved;
        _snackbarService.Show(
            _localizationService["Msg_Success"],
            _localizationService.Format("Msg_File_Saved", file.FileName, saved),
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(4));
    }

    /// <summary>An item the source app offered but would not hand over. A null name means it offered no file
    /// at all — new Outlook drags mailbox row keys, not a message.</summary>
    private void ExecuteHandleDropFailed(string? fileName)
    {
        _logger.LogWarning("A dragged item could not be taken from its source app (named={Named})",
            !string.IsNullOrWhiteSpace(fileName));
        _logger.SensitiveDebug("Drag materialization failed for {FileName}", fileName);

        var message = string.IsNullOrWhiteSpace(fileName)
            ? _localizationService["Msg_File_DropNoFile"]
            : _localizationService.Format("Msg_File_DropFailed", fileName);

        DropFailureMessage = message;

        _snackbarService.Show(
            _localizationService["Msg_Warning"], message,
            Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
    }

    private async Task AttachImagesAsync(IReadOnlyList<string> imagePaths)
    {
        // The provider gate answers for the whole drop, so it is read once and says so once; every refusal
        // inside the loop is per-file and names the file it left out.
        if (!await AssistantProviderTakesImagesAsync())
        {
            WarnImageProviderUnsupported();
            return;
        }

        foreach (var path in imagePaths)
            await AdmitImageAsync(
                () => ImageAttachmentProcessor.TryPrepare(path, _logger),
                System.IO.Path.GetFileName(path), path);
    }

    private async Task<bool> ExecuteHandleImageAttached(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        if (IsStreaming) return false;
        return await PrepareImageAttachmentAsync(
            () => ImageAttachmentProcessor.TryPrepare(filePath, _logger),
            System.IO.Path.GetFileName(filePath), filePath) is not null;
    }

    private async Task ExecuteHandleImagePasted(BitmapSource? source)
    {
        if (source is null) return;
        if (IsStreaming) return;

        // Freeze so the bitmap can be encoded on the background thread below without a
        // cross-thread access exception (Clipboard.GetImage runs on the UI thread).
        if (source.CanFreeze && !source.IsFrozen) source.Freeze();

        await PrepareImageAttachmentAsync(
            () => ImageAttachmentProcessor.TryPrepare(source, _logger),
            _localizationService["Msg_File_PastedImageName"]);
    }

    /// <summary>Refused rather than assumed on a failed read: the picture may be the user's whole desktop.</summary>
    private async Task<bool> AssistantProviderTakesImagesAsync()
    {
        try
        {
            var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
            return provider?.ProviderType == AiProviderType.PiaCloud;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not resolve the Assistant provider for an image ({Type})", ex.GetType().Name);
            return false;
        }
    }

    private void WarnImageProviderUnsupported() =>
        _snackbarService.Show(
            _localizationService["Msg_Warning"],
            _localizationService["Msg_File_ImageProviderUnsupported"],
            Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));

    /// <summary>Returns the admitted attachment so a caller cannot read back "whatever is last" — with a
    /// collection, the one just added is not identifiable any other way.</summary>
    private async Task<ImageAttachment?> PrepareImageAttachmentAsync(
        Func<ImageAttachment?> prepare, string displayName, string? sourcePath = null)
    {
        if (!await AssistantProviderTakesImagesAsync())
        {
            WarnImageProviderUnsupported();
            return null;
        }

        return await AdmitImageAsync(prepare, displayName, sourcePath);
    }

    private async Task<ImageAttachment?> AdmitImageAsync(
        Func<ImageAttachment?> prepare, string displayName, string? sourcePath)
    {
        // Count and dedup are checked before the encode so a refused image costs no JPEG work; the byte
        // budget cannot be, because the size is not known until the quality ladder settles.
        if (PendingAttachments.Count >= MaxPendingImages)
        {
            WarnImageAttach(_localizationService.Format("Msg_File_ImageLimit", MaxPendingImages, displayName));
            return null;
        }

        if (sourcePath is not null && PendingAttachments.Any(
                a => string.Equals(a.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            WarnImageAttach(_localizationService.Format("Msg_File_DuplicateAttachment", displayName));
            return null;
        }

        var attachment = await Task.Run(prepare);
        if (attachment is null)
        {
            WarnImageAttach(_localizationService["Msg_File_ImageTooLarge"]);
            return null;
        }

        var staged = PendingAttachments.Sum(a => (long)a.JpegBytes.Length);
        if (staged + attachment.JpegBytes.Length > MaxPendingImageBytes)
        {
            WarnImageAttach(_localizationService.Format("Msg_File_ImageBudget", displayName));
            return null;
        }

        PendingAttachments.Add(attachment);
        return attachment;
    }

    private void WarnImageAttach(string message) =>
        _snackbarService.Show(
            _localizationService["Msg_Warning"], message,
            Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));

    /// <summary>False when the default Assistant provider cannot read a picture, so the button says why
    /// instead of refusing after the user has chosen a window.</summary>
    [ObservableProperty]
    private bool _isScreenCaptureAvailable;

    /// <summary>Completes when the last provider check has landed; tests await it instead of racing the ctor.</summary>
    internal Task PendingScreenCaptureAvailabilityRefresh { get; private set; } = Task.CompletedTask;

    // Deliberately not gated on IsStreaming: the grab lands in the composer for the next turn, which is
    // typed ahead the same way, and the send gate still holds it back.
    private bool CanCaptureScreen() => _screenCapture is not null && IsScreenCaptureAvailable;

    private void OnProvidersChangedForScreenCapture(object? sender, EventArgs e) =>
        StartScreenCaptureAvailabilityRefresh();

    private void OnSettingsChangedForScreenCapture(object? sender, AppSettings settings) =>
        StartScreenCaptureAvailabilityRefresh();

    private void StartScreenCaptureAvailabilityRefresh()
    {
        PendingScreenCaptureAvailabilityRefresh = RefreshScreenCaptureAvailabilityAsync();
        PendingScreenCaptureAvailabilityRefresh.SafeFireAndForget(_logger);
    }

    private async Task RefreshScreenCaptureAvailabilityAsync()
    {
        // Without a capture service there is no button to enable, and no provider read to account for.
        if (_screenCapture is null) return;

        var available = await AssistantProviderTakesImagesAsync();

        await _uiDispatcher.PostAsync(() =>
        {
            if (_disposed) return;
            IsScreenCaptureAvailable = available;
            CaptureScreenCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task ExecuteCaptureScreen()
    {
        if (_screenCapture is null) return;

        var picker = new ScreenCapturePickerViewModel(
            _screenCapture, _localizationService, _loggerFactory.CreateLogger<ScreenCapturePickerViewModel>());
        try
        {
            picker.InitializeAsync().SafeFireAndForget(_logger);
            if (!await _dialogService.ShowScreenCapturePickerDialogAsync(picker)) return;

            var result = await picker.CaptureSelectedAsync();
            if (result is null) return;

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Screen capture from the picker refused: {Kind} {Process} {Reason}",
                    result.Target.Kind, result.Target.ProcessName, result.Reason);
                _snackbarService.Show(
                    _localizationService["Msg_Warning"],
                    _localizationService[ScreenCaptureFailureText.KeyFor(result.Reason)!],
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
                return;
            }

            var attached = await PrepareImageAttachmentAsync(
                () => ImageAttachmentProcessor.TryPrepare(result.Bitmap!, _logger),
                _localizationService["Msg_File_ScreenCaptureName"]);
            if (attached is null) return;

            RecordScreenCapture(result, attached);
        }
        catch (Exception ex)
        {
            _logger.LogError("Screen capture from the picker failed ({Type})", ex.GetType().Name);
            _logger.SensitiveDebug("Screen capture failure: {Error}", ex);
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                _localizationService["Msg_Screen_NativeError"],
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
        finally
        {
            picker.Cancel();
        }
    }

    /// <summary>The hotkey has to say why nothing opened; the availability check may still be in flight on a
    /// window it just created.</summary>
    internal async Task OpenScreenCapturePickerFromHotkeyAsync()
    {
        if (_screenCapture is null || CaptureScreenCommand.IsRunning) return;

        try
        {
            await PendingScreenCaptureAvailabilityRefresh;

            if (!IsScreenCaptureAvailable)
            {
                _snackbarService.Show(
                    _localizationService["Msg_Warning"],
                    _localizationService["Msg_File_ImageProviderUnsupported"],
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                return;
            }

            // ExecuteAsync does not consult CanExecute, and two presses can both park on the await above.
            if (CaptureScreenCommand.IsRunning) return;
            await CaptureScreenCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // The navigation task is discarded upstream, so nothing else would ever observe this.
            _logger.LogError("Screen capture from the hotkey failed ({Type})", ex.GetType().Name);
        }
    }

    private void RecordScreenCapture(CaptureResult result, ImageAttachment attachment)
    {
        var target = result.Target;
        var kind = target.Kind == CaptureTargetKind.Monitor
            ? ScreenCaptureTargetKinds.Monitor
            : ScreenCaptureTargetKinds.Window;

        var evt = new ScreenCaptureAuditEvent(
            ScreenCaptureSurfaces.Picker, _chatSessionManager.ActiveSession?.Id, null,
            kind, target.ProcessName, target.Title, attachment.Width, attachment.Height);

        _screenCaptureAudit?.Record(evt);
        _screenCaptureIndicator?.NotifyCapture(evt);
        _logger.LogInformation("Screen capture attached from the picker: {Kind} {Process} {Width}x{Height}",
            kind, target.ProcessName, attachment.Width, attachment.Height);
        _logger.SensitiveDebug("Picker capture window title: {Title}", target.Title);
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
    /// Opens what a source chip points at. A web chip never reaches here — it opens its own URL in the
    /// browser; this is the in-app half (vault page, past conversation).
    /// </summary>
    [RelayCommand]
    private async Task OpenSourceAsync(SourceRef? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Target)) return;

        switch (source.Kind)
        {
            case SourceRefKind.VaultPage:
                _navigationService.NavigateTo<VaultViewModel, string>(source.Target);
                break;

            case SourceRefKind.Chat when Guid.TryParse(source.Target, out var chatId):
                await ResumeChatAsync(chatId);
                break;
        }
    }

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

        // W2c: same lever as Send (this is modeled on ExecuteSendMessage). It starts a live turn against the
        // ACTIVE chat, so with a foreign headless run mid-flight it would be a second full-chat writer — and
        // it would additionally create a SECOND Planned run in a chat that already has one.
        if (IsStreaming || ForeignRunActive || PlanApprovalParkActive)
            return;

        var session = _chatSessionManager.ActiveSession
            ?? _chatSessionManager.GetOrCreateActiveForNewChat();

        // The documented exception to the banner: the chip is offered BECAUSE of the conversation, so the
        // conversation is its context by construction. Recorded BEFORE the lever flips, or the trigger arms
        // and the banner flashes behind a run that is already starting. StartTurnAsync awaits its own persist
        // before creating the run, so this rides that write.
        session.AgentContextMode = AgentContextMode.Summary;
        ActiveAgentContextMode = AgentContextMode.Summary;

        AgentModeEnabled = true; // persists + evaluates the warning via OnAgentModeEnabledChanged
        await _chatSessionManager.StartTurnAsync(session, suggestion.Goal, attachments: null, planned: true);
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

    /// <summary>Persisted before any run can be created, so the orchestrator reads the choice off the chat
    /// row rather than from this view model.</summary>
    [RelayCommand]
    private void SetAgentContext(string? mode)
    {
        if (AgentContextModes.Parse(mode) is not { } parsed)
            return;

        var session = _chatSessionManager.ActiveSession;
        if (session is null)
            return;

        session.AgentContextMode = parsed;
        ActiveAgentContextMode = parsed;
        RefreshAgentContextBanner();
        _chatSessionManager.PersistAsync(session).SafeFireAndForget(_logger);
        _logger.LogInformation("Chat {ChatId} agent context set to {Mode}", session.Id, parsed);
    }

    /// <summary>Re-opens the offer. Blocks sending again by design — changing the answer means choosing one.</summary>
    [RelayCommand]
    private void ChangeAgentContext()
    {
        var session = _chatSessionManager.ActiveSession;
        if (session is null)
            return;

        session.AgentContextMode = null;
        ActiveAgentContextMode = null;
        RefreshAgentContextBanner();
        _chatSessionManager.PersistAsync(session).SafeFireAndForget(_logger);
    }

    // A run already attached to this chat is past the point the offer decides, and a parked one is waiting
    // for an answer the composer types — which the offer's send gate would refuse. Terminal states let the
    // offer return, so the NEXT run in the chat still gets asked.
    private bool RunNotYetSettled =>
        _runProgress is { State: not (RunProgressState.Completed or RunProgressState.TruncatedCompleted
            or RunProgressState.Failed) };

    /// <summary>Evaluated on the lever toggle AND on chat load: agent mode is frequently already on without
    /// a toggle, so a toggle-only trigger would never fire for a regular agent user.</summary>
    private void RefreshAgentContextBanner()
    {
        var applicable = AgentModeEnabled && HasMessages && !IsStreaming && !RunNotYetSettled;
        AgentContextChoicePending = applicable && ActiveAgentContextMode is null;
        AgentContextSettledVisible = applicable && ActiveAgentContextMode is not null;

        if (ActiveAgentContextMode is { } settled)
        {
            AgentContextSettledLabel = _localizationService[settled switch
            {
                AgentContextMode.Summary => "Agent_Context_Settled_Summary",
                AgentContextMode.Verbatim => "Agent_Context_Settled_Verbatim",
                _ => "Agent_Context_Settled_Off",
            }];
        }
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
            AddVoiceModeConversation,
            _uiDispatcher);

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
            suggestAgentModeEligible: false,
            // Un-narrowed on purpose: a voice turn establishes no TaskContext, so its tool calls resolve
            // at the sandbox root no matter which chat is on screen.
            environmentRoot: _filesToolHandler.DescribeEffectiveRoot(null));
        var supportsTools = turnSetup.SupportsTools;
        var fullSystemPrompt = turnSetup.SystemPrompt;
        var tools = turnSetup.Tools;

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, fullSystemPrompt)
        };

        chatMessages.AddRange(Messages.Select(m => m.ToChatMessage()));

        chatMessages.Add(new ChatMessage(ChatRole.User, AssistantPromptComposer.AppendTimeNote(userText)));

        var rawBuffer = new StringBuilder();
        var lastVisibleLength = 0;
        // A tool round just completed; the next TextDelta starts a fresh model turn built on the
        // tool result, not a continuation of whatever is already in rawBuffer.
        var pendingRoundBreak = false;

        // Any selected persona travels as X-Pia-Persona, managed or not — the server maps an id it does
        // not know to null, so no IsManaged check belongs here.
        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            chatMessages, provider, tools,
            supportsTools ? HandleVoiceModeToolCall : null,
            nameof(WindowMode.Assistant),
            persona.Id,
            persona.ModelType,
            cancellationToken: cancellationToken))
        {
            if (item is ToolRoundCompleted)
            {
                pendingRoundBreak = true;
                continue;
            }

            if (item is not TextDelta td)
                continue;

            if (pendingRoundBreak && rawBuffer.Length > 0)
                rawBuffer.Append("\n\n");
            pendingRoundBreak = false;

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

        _logger.LogInformation("Generating follow-up suggestions for provider {ProviderType}", provider.ProviderType);

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

        await _uiDispatcher.PostAsync(() =>
        {
            foreach (var s in picks)
                assistantMessage.Suggestions.Add(s);
        });
        _logger.LogInformation("Follow-up suggestions: added {Count} picks (HasSuggestions={Has})",
            picks.Count, assistantMessage.HasSuggestions);
    }

    /// <summary>
    /// Voice-mode tool dispatch. Reads always run; writes go through the SAME resolver as the chat gate and
    /// the unattended gate, on <see cref="ToolGateSurface.Voice"/>.
    /// </summary>
    /// <remarks>
    /// This path used to execute EVERY pending write unchecked, so <c>write_file</c> and <c>delete_file</c> ran
    /// silently while the user was talking. Now the allowlisted create tools run, anything the user has
    /// "always allowed" runs, the settings preset's classes run, and everything else is refused with a remedy.
    /// There is no run here, so the policy comes from settings; <c>internal</c> so the gate can be tested
    /// without standing up a whole voice turn.
    /// </remarks>
    /// <param name="context">Unused, and that is the design: this method is a <c>ToolCallHandler</c> so the
    /// loop can dispatch to it, but a voice turn belongs to no run and emits no timeline row (see
    /// <c>IAgentTimelineService</c>'s scope remarks), so there is nowhere for the round to be recorded.</param>
    internal async Task<object?> HandleVoiceModeToolCall(FunctionCallContent toolCall, ToolDispatchContext context)
    {
        _ = context;
        _logger.LogInformation("Voice mode tool call: {ToolName}", toolCall.Name);

        var routeResult = await _pluginService.RouteToolCallAsync(toolCall);
        if (routeResult is null)
            return $"Unknown tool: {toolCall.Name}";

        var (result, pendingAction) = routeResult.Value;
        if (result is not null)
            return result;

        if (pendingAction is not null)
        {
            var tool = pendingAction.ToolName;
            var settings = await _settingsService.GetSettingsAsync();
            var toolClass = ToolClassifier.Classify(pendingAction.PluginName, IsExternalTool(tool));
            // Hoisted like ChatSession's, so this file reads the allowlist in exactly one place that
            // ToolAutonomyRuleTests can name.
            var allowlisted = _permissions.IsAutoApproveEligible(tool);
            var verdict = ToolAutonomy.Resolve(new ToolGateInput(
                ToolGateSurface.Voice, tool, toolClass,
                ServerDeclaredDestructive: pendingAction.ServerDeclaredDestructive,
                IsAllowlisted: allowlisted,
                // The honest lookup, even though the resolver does not honour a session grant on THIS surface:
                // the input stays a fact and the reason voice is excluded stays in Resolve's session arm.
                HasSessionGrant: _permissions.IsGrantedForSession(pendingAction.PluginId, tool),
                HasStandingGrant: _permissions.IsGranted(pendingAction.PluginId, tool),
                // Voice has no per-job grant list and no run envelope; the policy is the settings preset.
                IsNamedGrant: false,
                // The denial list lives in a run envelope; a voice turn has no run.
                HasNamedDenial: false,
                Policy: RunAutonomyPolicy.FromSettings(settings),
                // A voice turn is not a run — there is no row to park, no Continue card that would
                // reach the speaker, and the refusal below is already spoken back as a remedy.
                CanPark: false,
                // A voice turn belongs to no run, so there is no run row to answer this from.
                IsTopLevelUserRun: false));

            if (verdict.Outcome != ToolGateOutcome.AutoRun)
            {
                // An English literal, like both other gate refusals: it goes to the MODEL, not the UI, so it
                // needs no resx key.
                _logger.LogInformation("Voice mode denied ungranted write tool {ToolName}", tool);
                return $"Denied: '{tool}' needs your confirmation and voice mode cannot show an approval card. "
                       + "Ask me again in the chat window.";
            }

            _logger.LogInformation("Voice mode executing {ToolName} ({Decision})", tool, verdict.Decision);
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

    /// <summary>
    /// Is this an external/MCP tool? A route-lookup fault returns <c>true</c> like both run gates, and only
    /// ever narrows: it costs the call the allowlist arm and keeps the settings preset from covering it, so
    /// it adds friction instead of failing the voice turn.
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
        // Before anything else: a report left behind would defer the restart overlay for the whole process.
        _volatileWork?.Forget(this);
        VoiceMode?.Dispose();
        VoiceMode = null;
        MeetingAttendee.CloseRequested -= OnMeetingAttendeeCloseRequested;
        MeetingAttendee.SummarizeRequested -= OnMeetingAttendeeSummarizeRequested;
        MeetingAttendee.OpenSettingsRequested -= OnMeetingAttendeeOpenSettingsRequested;
        MeetingAttendee.Dispose();
        DirectTranscription.CloseRequested -= OnDirectTranscriptionCloseRequested;
        DirectTranscription.SummarizeRequested -= OnDirectTranscriptionSummarizeRequested;
        DirectTranscription.Dispose();
        _ttsService.Stop();
        _ttsService.IsPlayingChanged -= OnTtsPlayingChanged;
        _personaService.PersonasChanged -= OnPersonasChanged;
        _personaService.ManagedPersonaWithdrawn -= OnManagedPersonaWithdrawn;
        _providerService.ProvidersChanged -= OnProvidersChangedForScreenCapture;
        _settingsService.SettingsChanged -= OnSettingsChangedForScreenCapture;
        PropertyChanged -= OnPropertyChanged;
        _goalHintGeneration++;
        _agentHintGeneration++;

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
            session.ForeignRunActiveChanged -= OnForeignRunActiveChanged;
            session.AgentModeChanged -= OnSessionAgentModeChanged;
            session.PlanApprovalParkActiveChanged -= OnPlanApprovalParkActiveChanged;
        }
        if (_runProgress is not null)
        {
            _runProgress.RunSettled -= OnRunProgressSettled;
            _runProgress.PropertyChanged -= OnRunProgressPropertyChanged;
            _runProgress.Dispose(); // unsubscribes the last RunChanged handler off the singleton
        }
        Messages.CollectionChanged -= OnMessagesCollectionChanged;
        PendingFiles.CollectionChanged -= OnPendingFilesChanged;
        PendingAttachments.CollectionChanged -= OnPendingAttachmentsChanged;

        ChatTitleChip.Dispose();

        GC.SuppressFinalize(this);
    }
}
