using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>Each audio overlay owns the local audio stack (microphone plus system loopback), so two visible at
/// once would mean two live capture pipelines.</summary>
public class AssistantViewModelOverlayHostingTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMeetingAttendeeService _meetingService = Substitute.For<IMeetingAttendeeService>();
    private readonly IDirectTranscriptionService _directService = Substitute.For<IDirectTranscriptionService>();
    private readonly VolatileWorkStore _work = new();

    [Fact]
    public async Task OpeningDirectTranscription_ClosesTheMeetingAttendee()
    {
        var vm = CreateSut();
        await vm.ToggleMeetingAttendeeCommand.ExecuteAsync(null);
        Assert.True(vm.IsMeetingAttendeeVisible);

        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);

        Assert.True(vm.IsDirectTranscriptionVisible);
        Assert.False(vm.IsMeetingAttendeeVisible, "both overlays own the local audio stack; only one may be open");
        await _meetingService.Received().StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpeningTheMeetingAttendee_ClosesDirectTranscription()
    {
        var vm = CreateSut();
        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        Assert.True(vm.IsDirectTranscriptionVisible);

        await vm.ToggleMeetingAttendeeCommand.ExecuteAsync(null);

        Assert.True(vm.IsMeetingAttendeeVisible);
        Assert.False(vm.IsDirectTranscriptionVisible, "both overlays own the local audio stack; only one may be open");
        await _directService.Received().StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TogglingDirectTranscriptionClosed_StopsTheSession()
    {
        var vm = CreateSut();
        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        _directService.ClearReceivedCalls();

        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirectTranscriptionVisible);
        await _directService.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DirectTranscriptionSummarizeRequested_HidesTheOverlay_AndClearsAnyPendingAttachment()
    {
        var vm = CreateSut();
        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        vm.ActivePersona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        vm.PendingAttachment = NewAttachment();

        // A transcript must exist for the command to be enabled (CanSummarize: not running + bubbles > 0).
        vm.DirectTranscription.AddUtterance(
            new TranscriptUtterance(TranscriptSpeaker.You, "agenda item one", DateTimeOffset.Now));
        Assert.True(vm.DirectTranscription.SummarizeWithAssistantCommand.CanExecute(null));

        vm.DirectTranscription.SummarizeWithAssistantCommand.Execute(null);

        // The chat is where the summary streams, so the overlay must get out of the way, and the summary
        // must not carry over an unrelated screenshot the user had queued.
        Assert.False(vm.IsDirectTranscriptionVisible);
        Assert.Null(vm.PendingAttachment);
    }

    [Fact]
    public async Task DirectTranscriptionCloseRequested_HidesTheOverlay_ExactlyOnce()
    {
        // Routed through the command rather than the method so AsyncRelayCommand's no-concurrent-execution
        // guard applies: the X button and the toolbar toggle cannot start two overlapping hide bodies.
        var vm = CreateSut();
        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        _directService.ClearReceivedCalls();

        vm.DirectTranscription.CloseCommand.Execute(null);

        Assert.False(vm.IsDirectTranscriptionVisible);
        await _directService.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The policy-restart overlay covers the whole window including these overlays, and Save only
    /// lights up once the session has stopped — so an open transcript overlay has to hold it off.</summary>
    [Fact]
    public async Task AnOpenTranscriptOverlay_IsReportedAsWorkARestartWouldDestroy()
    {
        var vm = CreateSut();
        Assert.False(_work.HasVolatileWork);

        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        Assert.True(_work.HasVolatileWork);

        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        Assert.False(_work.HasVolatileWork);
    }

    [Fact]
    public async Task AnOpenMeetingOverlay_IsReportedAsWorkARestartWouldDestroy()
    {
        var vm = CreateSut();

        await vm.ToggleMeetingAttendeeCommand.ExecuteAsync(null);

        Assert.True(_work.HasVolatileWork);
    }

    /// <summary>Published cross-window so an Optimize window cannot offer Restart mid-turn.</summary>
    [Fact]
    public void AStreamingTurn_IsReportedAsWorkARestartWouldDestroy()
    {
        var vm = CreateSut();
        _manager.IsAnyStreaming.Returns(true);

        _manager.SessionStateChanged += Raise.Event<EventHandler<SessionStateChangedEventArgs>>(
            _manager,
            new SessionStateChangedEventArgs
            {
                ChatId = Guid.NewGuid(),
                OldState = ChatState.Idle,
                NewState = ChatState.Running,
                IsActive = true,
            });

        Assert.True(_work.HasVolatileWork);
        Assert.NotNull(vm);
    }

    /// <summary>Voice mode streams straight through the AI client and never creates a session, so
    /// <c>IsAnyStreaming</c> is false for the whole conversation while the scrim covers its only exit.</summary>
    [Fact]
    public void AnActiveVoiceMode_IsReportedAsWorkARestartWouldDestroy()
    {
        var vm = CreateSut();
        Assert.False(_work.HasVolatileWork);

        vm.IsVoiceModeActive = true;
        Assert.True(_work.HasVolatileWork);

        vm.IsVoiceModeActive = false;
        Assert.False(_work.HasVolatileWork);
    }

    /// <summary>A report left behind by a closed window would defer the overlay for the whole process.</summary>
    [Fact]
    public async Task Dispose_DropsTheReport()
    {
        var vm = CreateSut();
        await vm.ToggleDirectTranscriptionCommand.ExecuteAsync(null);
        Assert.True(_work.HasVolatileWork);

        vm.Dispose();

        Assert.False(_work.HasVolatileWork);
    }

    /// <summary>Minimal 1x1 attachment, mirroring <c>AssistantMessageFileRefsTests.NewAttachment</c> —
    /// <see cref="ImageAttachment"/> requires a real <see cref="BitmapSource"/>.</summary>
    private static ImageAttachment NewAttachment()
    {
        var thumb = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        return new ImageAttachment
        {
            JpegBytes = [1, 2, 3, 4],
            MimeType = "image/jpeg",
            Width = 1,
            Height = 1,
            Thumbnail = thumb,
        };
    }

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (System.Threading.SynchronizationContext.Current is null)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _directService.GetVoiceStats().Returns(Array.Empty<SpeakerVoiceStats>());
        // StartFreshChat (hit by the summarize hand-off) calls SetWorkingDirectory on whatever
        // this returns, so it must be a real ChatSession rather than the substitute default null.
        _manager.GetOrCreateActiveForNewChat().Returns(_ => new ChatSession(
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAiClientService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IActionCardBuilder>(),
            Substitute.For<IToolPermissionService>(),
            Substitute.For<ILocalizationService>(),
            NullLogger.Instance,
            _ => false));

        var meeting = new MeetingAttendeeViewModel(
            _meetingService,
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            _directService,
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<DirectTranscriptionViewModel>.Instance,
            new InlineUiDispatcher());

        return new AssistantViewModel(
            NullLogger<AssistantViewModel>.Instance,
            Substitute.For<IAiClientService>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IPersonaService>(),
            _settings,
            Substitute.For<IOutputService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAutocompleteService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ISuggestionService>(),
            Substitute.For<IAssistantChatService>(),
            meeting,
            directTranscription,
            Substitute.For<IAssistantPromptComposer>(),
            Substitute.For<IProviderCapabilityService>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IAgentRunResumeService>(),
            _manager,
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>(),
            volatileWork: _work);
    }
}
