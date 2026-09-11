using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Services.Screen;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The composer's screen-capture button: the picker, the clipboard-paste attachment path it
/// reuses, the audit line, and the provider gate that decides whether the button is offered at all.</summary>
public sealed class AssistantViewModelScreenCaptureTests
{
    private static readonly CaptureTarget Notepad =
        new(CaptureTargetKind.Window, 11, "", new PixelRect(0, 0, 800, 600), "notepad", "Quarterly numbers", false, false);

    private static readonly CaptureTarget Display =
        new(CaptureTargetKind.Monitor, 0, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), "", "", true, false);

    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly Wpf.Ui.ISnackbarService _snackbar = Substitute.For<Wpf.Ui.ISnackbarService>();
    private readonly IScreenCaptureService _screenCapture = Substitute.For<IScreenCaptureService>();
    private readonly IScreenCaptureAuditLog _audit = Substitute.For<IScreenCaptureAuditLog>();
    private readonly IScreenCaptureIndicator _indicator = Substitute.For<IScreenCaptureIndicator>();
    private readonly CapturingLogger<AssistantViewModel> _logger = new();

    private Func<CaptureTarget, Task<CaptureResult>> _capture;

    public AssistantViewModelScreenCaptureTests()
    {
        _capture = target => Task.FromResult(Ok(target, 64, 48));
        _screenCapture.EnumerateTargetsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CaptureTarget>>([Display, Notepad]);
        _screenCapture.CaptureAsync(Arg.Any<CaptureTarget>(), Arg.Any<CancellationToken>())
            .Returns(ci => _capture(ci.Arg<CaptureTarget>()));
        UsePiaCloud();
        PickTheWindowAndConfirm();
    }

    // ---- the capture itself --------------------------------------------------------------------

    [Fact]
    public async Task CaptureScreen_AttachesTheChosenWindow_ThroughTheImagePath()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PendingAttachment);
        Assert.Equal(64, vm.PendingAttachment!.Width);
        Assert.Equal(48, vm.PendingAttachment.Height);
        Assert.Equal("image/jpeg", vm.PendingAttachment.MimeType);
        await _screenCapture.Received().CaptureAsync(Notepad, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureScreen_WritesOneAuditLine_WithTheAttachmentDimensions_AndNoTitleInTheLog()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        _audit.Received(1).Record(Arg.Is<ScreenCaptureAuditEvent>(e =>
            e.Surface == ScreenCaptureSurfaces.Picker
            && e.TargetKind == ScreenCaptureTargetKinds.Window
            && e.ProcessName == "notepad"
            && e.WindowTitle == "Quarterly numbers"
            && e.Width == 64
            && e.Height == 48
            && e.Granter == null));
        _indicator.Received(1).NotifyCapture(Arg.Any<ScreenCaptureAuditEvent>());

        var releaseVisible = _logger.Entries.Where(e => e.Level > LogLevel.Debug).ToList();
        // Materialised and counted first: an empty set would pass the line below having read nothing.
        Assert.NotEmpty(releaseVisible);
        Assert.DoesNotContain(releaseVisible, e => e.Message.Contains("Quarterly numbers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureScreen_UsesTheActiveChatAsTaskId()
    {
        var chatId = Guid.NewGuid();
        var session = NewSession();
        session.Id = chatId;
        _manager.ActiveSession.Returns(session);
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        _audit.Received(1).Record(Arg.Is<ScreenCaptureAuditEvent>(e => e.TaskId == chatId));
    }

    [Fact]
    public async Task CaptureScreen_AMonitorTarget_IsRecordedAsAMonitor()
    {
        PickTheMonitorAndConfirm();
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        _audit.Received(1).Record(Arg.Is<ScreenCaptureAuditEvent>(e =>
            e.TargetKind == ScreenCaptureTargetKinds.Monitor && e.ProcessName == ""));
    }

    [Fact]
    public async Task CaptureScreen_WhenTheUserCancels_CapturesNothing_AttachesNothing_RecordsNothing()
    {
        // The preview loop parks on the first target, so the window is never even previewed and any
        // CaptureAsync on it would have to be the confirm capture.
        StallEveryCapture();
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>())
            .Returns(async ci =>
            {
                await ci.Arg<ScreenCapturePickerViewModel>().PendingInitialization;
                return false;
            });
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        Assert.Null(vm.PendingAttachment);
        await _screenCapture.DidNotReceive().CaptureAsync(Notepad, Arg.Any<CancellationToken>());
        _audit.DidNotReceive().Record(Arg.Any<ScreenCaptureAuditEvent>());
    }

    [Fact]
    public async Task CaptureScreen_ARefusedFrame_NamesTheReason_AndRecordsNothing()
    {
        _capture = target => Task.FromResult(CaptureResult.Failed(target, CaptureFailureReason.UniformFrame));
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        _ = _localization.Received()["Msg_Screen_UniformFrame"];
        _snackbar.ReceivedWithAnyArgs(1).Show(default!, default!, default, default, default);
        Assert.Null(vm.PendingAttachment);
        _audit.DidNotReceive().Record(Arg.Any<ScreenCaptureAuditEvent>());
    }

    [Fact]
    public async Task CaptureScreen_OnANonPiaCloudProvider_RefusesLikeAPaste_AndRecordsNothing()
    {
        UseProvider(AiProviderType.OpenAI);
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        // The button is disabled too, but the VM path has to hold on its own.
        await vm.CaptureScreenCommand.ExecuteAsync(null);

        _ = _localization.Received(1)["Msg_File_ImageProviderUnsupported"];
        Assert.Null(vm.PendingAttachment);
        _audit.DidNotReceive().Record(Arg.Any<ScreenCaptureAuditEvent>());
    }

    [Fact]
    public async Task CaptureScreen_ReplacesAnExistingImage_WithoutTheOneImageWarning()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        vm.PendingAttachment = Attachment();

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        Assert.Equal(64, vm.PendingAttachment!.Width);
        _localization.DidNotReceive().Format("Msg_File_OneImageOnly", Arg.Any<object[]>());
    }

    [Fact]
    public async Task CaptureScreen_WhileStreaming_StillAttaches()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        vm.IsStreaming = true;

        Assert.True(vm.CaptureScreenCommand.CanExecute(null));
        await vm.CaptureScreenCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
        Assert.NotNull(vm.PendingAttachment);
    }

    [Fact]
    public async Task CaptureScreen_ATurnThatStartedWhileThePickerWasOpen_KeepsTheFrame()
    {
        AssistantViewModel? vm = null;
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>())
            .Returns(async ci =>
            {
                var picker = ci.Arg<ScreenCapturePickerViewModel>();
                await picker.PendingInitialization;
                picker.WindowTargets[0].IsSelected = true;
                vm!.IsStreaming = true;
                return true;
            });
        vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PendingAttachment);
        _audit.Received(1).Record(Arg.Any<ScreenCaptureAuditEvent>());
    }

    [Fact]
    public async Task CaptureScreen_WhileStreaming_TheAttachmentSurvivesTheTurnEnding()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        vm.IsStreaming = true;

        await vm.CaptureScreenCommand.ExecuteAsync(null);
        vm.IsStreaming = false;

        Assert.NotNull(vm.PendingAttachment);
    }

    [Fact]
    public async Task CaptureScreen_WithoutACaptureService_IsNotOffered()
    {
        var vm = CreateSut(withScreenCapture: false);
        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.False(vm.CaptureScreenCommand.CanExecute(null));
        await vm.CaptureScreenCommand.ExecuteAsync(null);
        await _dialogs.DidNotReceive().ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
    }

    [Fact]
    public async Task CaptureScreen_ThePreviewLoopIsStopped_WhenTheDialogCloses()
    {
        var previewCancelled = StallEveryCapture();
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>())
            .Returns(async ci =>
            {
                await ci.Arg<ScreenCapturePickerViewModel>().PendingInitialization;
                return false;
            });
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await vm.CaptureScreenCommand.ExecuteAsync(null);

        Assert.True(previewCancelled.Task.IsCompleted);
    }

    // ---- the provider gate ---------------------------------------------------------------------

    [Theory]
    [InlineData(AiProviderType.PiaCloud, true)]
    [InlineData(AiProviderType.OpenAI, false)]
    public async Task Availability_FollowsTheDefaultAssistantProvider_AtConstruction(
        AiProviderType providerType, bool expected)
    {
        UseProvider(providerType);
        var vm = CreateSut();

        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.Equal(expected, vm.IsScreenCaptureAvailable);
        Assert.Equal(expected, vm.CaptureScreenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Availability_WithNoProviderAtAll_IsFalse()
    {
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns((AiProvider?)null);
        var vm = CreateSut();

        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.False(vm.IsScreenCaptureAvailable);
    }

    [Fact]
    public async Task Availability_RefreshesWhenTheProviderRowsChange()
    {
        UseProvider(AiProviderType.OpenAI);
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        Assert.False(vm.IsScreenCaptureAvailable);

        UsePiaCloud();
        _providers.ProvidersChanged += Raise.Event();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.True(vm.IsScreenCaptureAvailable);
        Assert.True(vm.CaptureScreenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Availability_RefreshesWhenTheSettingsChange()
    {
        UseProvider(AiProviderType.OpenAI);
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        UsePiaCloud();
        _settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(_settings, new AppSettings());
        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.True(vm.IsScreenCaptureAvailable);
    }

    [Fact]
    public async Task Availability_IsFalseWithoutACaptureService_AndReadsNoProvider()
    {
        var vm = CreateSut(withScreenCapture: false);

        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.False(vm.IsScreenCaptureAvailable);
        await _providers.DidNotReceive().GetDefaultProviderForModeAsync(Arg.Any<WindowMode>());
    }

    [Fact]
    public async Task Availability_AProviderReadThatThrows_DisablesTheButton_AndDoesNotPropagate()
    {
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns<Task<AiProvider?>>(_ => throw new InvalidOperationException("boom"));
        var vm = CreateSut();

        await vm.PendingScreenCaptureAvailabilityRefresh;

        Assert.False(vm.IsScreenCaptureAvailable);
    }

    [Fact]
    public async Task Dispose_StopsListening()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        vm.Dispose();
        _providers.ProvidersChanged += Raise.Event();
        _settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(_settings, new AppSettings());
        await vm.PendingScreenCaptureAvailabilityRefresh;

        await _providers.Received(1).GetDefaultProviderForModeAsync(WindowMode.Assistant);
    }

    // ---- the hotkey path -----------------------------------------------------------------------

    [Fact]
    public async Task HotkeyRequest_OpensThePicker_WhenAvailable()
    {
        var vm = CreateSut();

        await vm.OpenScreenCapturePickerFromHotkeyAsync();

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
        Assert.NotNull(vm.PendingAttachment);
    }

    [Fact]
    public async Task HotkeyRequest_OnANonPiaCloudProvider_SaysWhyNothingOpened()
    {
        UseProvider(AiProviderType.OpenAI);
        var vm = CreateSut();

        await vm.OpenScreenCapturePickerFromHotkeyAsync();

        _ = _localization.Received(1)["Msg_File_ImageProviderUnsupported"];
        await _dialogs.DidNotReceive().ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
    }

    [Fact]
    public async Task HotkeyRequest_WhileStreaming_StillOpensThePicker()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        vm.IsStreaming = true;

        await vm.OpenScreenCapturePickerFromHotkeyAsync();

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
        Assert.NotNull(vm.PendingAttachment);
    }

    [Fact]
    public async Task HotkeyRequest_WaitsForThePendingAvailabilityCheck()
    {
        var gate = new TaskCompletionSource<AiProvider?>();
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(_ => gate.Task);
        var vm = CreateSut();

        var pending = vm.OpenScreenCapturePickerFromHotkeyAsync();
        await _dialogs.DidNotReceive().ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());

        gate.SetResult(PiaCloud());
        await pending;

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
    }

    [Fact]
    public async Task HotkeyRequest_YieldsToACaptureThatStartedWhileItWaitedForTheAvailabilityCheck()
    {
        // Nothing was running when the press arrived, so only the guard placed AFTER the availability await
        // can stop it stacking a second picker on the one that opened meanwhile.
        var gate = new TaskCompletionSource<AiProvider?>();
        var open = new TaskCompletionSource<bool>();
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(_ => gate.Task);
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>()).Returns(_ => open.Task);
        var vm = CreateSut();

        var press = vm.OpenScreenCapturePickerFromHotkeyAsync();
        var running = vm.CaptureScreenCommand.ExecuteAsync(null);
        gate.SetResult(PiaCloud());
        await press;

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
        open.SetResult(false);
        await running;
    }

    [Fact]
    public async Task HotkeyRequest_WhileThePickerIsOpen_IsIgnored()
    {
        var open = new TaskCompletionSource<bool>();
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>())
            .Returns(_ => open.Task);
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;

        var first = vm.OpenScreenCapturePickerFromHotkeyAsync();
        await vm.OpenScreenCapturePickerFromHotkeyAsync();

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
        open.SetResult(false);
        await first;
    }

    [Fact]
    public async Task Navigation_WithAPickerRequest_OpensThePicker()
    {
        var vm = CreateSut();

        await vm.OnNavigatedToAsync(new ScreenCapturePickerRequest());

        await _dialogs.Received(1).ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
    }

    [Fact]
    public async Task Navigation_WithoutAPickerRequest_OpensNothing()
    {
        var vm = CreateSut();

        await vm.OnNavigatedToAsync(null);

        await _dialogs.DidNotReceive().ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>());
    }

    // ---- the provider gate at send time --------------------------------------------------------

    [Fact]
    public async Task Send_AfterTheProviderChanged_KeepsThePictureInTheComposer_AndSendsNothing()
    {
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        await vm.CaptureScreenCommand.ExecuteAsync(null);
        Assert.NotNull(vm.PendingAttachment);
        vm.InputText = "what is on my screen?";

        UseProvider(AiProviderType.OpenAI);
        await vm.SendMessageCommand.ExecuteAsync(null);

        await _manager.DidNotReceiveWithAnyArgs().StartTurnAsync(default!, default!, default);
        Assert.NotNull(vm.PendingAttachment);
        Assert.Equal("what is on my screen?", vm.InputText);
        _ = _localization.Received()["Msg_File_ImageProviderUnsupported"];
    }

    [Fact]
    public async Task Send_WithAPictureOnPiaCloud_GoesThrough()
    {
        _manager.StartTurnAsync(default!, default!, default).ReturnsForAnyArgs(true);
        var vm = CreateSut();
        await vm.PendingScreenCaptureAvailabilityRefresh;
        await vm.CaptureScreenCommand.ExecuteAsync(null);
        vm.InputText = "what is on my screen?";

        await vm.SendMessageCommand.ExecuteAsync(null);

        await _manager.ReceivedWithAnyArgs(1).StartTurnAsync(default!, default!, default);
        Assert.Null(vm.PendingAttachment);
    }

    [Fact]
    public async Task HotkeyRequest_AThrowingSnackbar_IsLoggedNotThrown()
    {
        UseProvider(AiProviderType.OpenAI);
        _snackbar.WhenForAnyArgs(s => s.Show(default!, default!, default, default, default))
            .Do(_ => throw new InvalidOperationException("boom"));
        var vm = CreateSut();

        await vm.OpenScreenCapturePickerFromHotkeyAsync();

        var errors = _logger.Entries.Where(e => e.Level == LogLevel.Error).ToArray();
        Assert.Single(errors);
        Assert.Contains("InvalidOperationException", errors[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", errors[0].Message, StringComparison.Ordinal);
    }

    // ---- fixture -------------------------------------------------------------------------------

    /// <summary>Makes every capture park until its token is cancelled; the returned task completes on the
    /// first cancellation.</summary>
    private TaskCompletionSource StallEveryCapture()
    {
        var cancelled = new TaskCompletionSource();
        _screenCapture.CaptureAsync(Arg.Any<CaptureTarget>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var pending = new TaskCompletionSource<CaptureResult>();
                ci.Arg<CancellationToken>().Register(() =>
                {
                    cancelled.TrySetResult();
                    pending.TrySetCanceled();
                });
                return pending.Task;
            });
        return cancelled;
    }

    private void UsePiaCloud() => _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(PiaCloud());

    private void UseProvider(AiProviderType providerType) =>
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(new AiProvider
        {
            Name = "Provider",
            Endpoint = "https://example.invalid",
            ProviderType = providerType,
        });

    private static AiProvider PiaCloud() => new()
    {
        Name = "Cloud",
        Endpoint = "https://example.invalid",
        ProviderType = AiProviderType.PiaCloud,
    };

    private void PickTheWindowAndConfirm() =>
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>())
            .Returns(async ci =>
            {
                var picker = ci.Arg<ScreenCapturePickerViewModel>();
                await picker.PendingInitialization;
                picker.WindowTargets[0].IsSelected = true;
                return true;
            });

    private void PickTheMonitorAndConfirm() =>
        _dialogs.ShowScreenCapturePickerDialogAsync(Arg.Any<ScreenCapturePickerViewModel>())
            .Returns(async ci =>
            {
                var picker = ci.Arg<ScreenCapturePickerViewModel>();
                await picker.PendingInitialization;
                picker.MonitorTargets[0].IsSelected = true;
                return true;
            });

    private static CaptureResult Ok(CaptureTarget target, int width, int height)
    {
        var stride = width * 4;
        var frame = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgr32, null, new byte[stride * height], stride);
        frame.Freeze();
        return CaptureResult.Success(target, frame);
    }

    private static ImageAttachment Attachment()
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        return new ImageAttachment
        {
            JpegBytes = [1, 2, 3],
            MimeType = "image/jpeg",
            Width = 1,
            Height = 1,
            Thumbnail = bitmap,
        };
    }

    private static ChatSession NewSession() => new(
        Substitute.For<ITokenMapService>(),
        Substitute.For<IAiClientService>(),
        Substitute.For<IPluginService>(),
        Substitute.For<IActionCardBuilder>(),
        Substitute.For<IToolPermissionService>(),
        Substitute.For<ILocalizationService>(),
        NullLogger.Instance,
        _ => false);

    private AssistantViewModel CreateSut(bool withScreenCapture = true)
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _manager.GetOrCreateActiveForNewChat().Returns(_ => NewSession());

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
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
            Substitute.For<IDirectTranscriptionService>(),
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
            _logger,
            Substitute.For<IAiClientService>(),
            _providers,
            Substitute.For<IPersonaService>(),
            _settings,
            Substitute.For<IOutputService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            _snackbar,
            _localization,
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
            _dialogs,
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>(),
            screenCapture: withScreenCapture ? _screenCapture : null,
            screenCaptureAudit: _audit,
            screenCaptureIndicator: _indicator);
    }
}
