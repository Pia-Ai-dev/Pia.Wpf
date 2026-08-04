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

/// <summary>
/// Covers the host-side wiring of the two audio overlays that <see cref="AssistantViewModel"/> owns —
/// mutual exclusion and the summarize hand-off. Both are net-new logic with no analogue elsewhere in the
/// app, and both matter beyond tidiness: the direct-transcription overlay and the Teams meeting attendee
/// each own the LOCAL AUDIO STACK (microphone plus system loopback), so if both were visible at once both
/// would have a live capture pipeline. Nothing else in the suite would notice that regression.
///
/// <para>Measured through the real <see cref="AssistantViewModel"/> with substituted services; the two
/// overlay view models are real instances over substituted backing services, so the toggle really does
/// drive their <c>StopAsync</c>.</para>
/// </summary>
public class AssistantViewModelOverlayHostingTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMeetingAttendeeService _meetingService = Substitute.For<IMeetingAttendeeService>();
    private readonly IDirectTranscriptionService _directService = Substitute.For<IDirectTranscriptionService>();

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
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            _directService,
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
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
            Substitute.For<IToolPermissionService>());
    }
}
