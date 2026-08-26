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

/// <summary>The confirm-then-detach path behind "Run in background": who gets asked, what the opt-out tick is
/// allowed to suppress, and the notice that says the detach happened.</summary>
public class AssistantViewModelBackgroundRunTests
{
    private const string Goal = "Write the release notes for v2";

    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly Wpf.Ui.ISnackbarService _snackbar = Substitute.For<Wpf.Ui.ISnackbarService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly AppSettings _appSettings = new();

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (System.Threading.SynchronizationContext.Current is null)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());

        _settings.GetSettingsAsync().Returns(_appSettings);
        // The loc key is what the assertions below identify a notice by, so echo it back verbatim.
        _localization[Arg.Any<string>()].Returns(call => call.Arg<string>());

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
            _dialog,
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }

    private void ConfirmWith(bool confirmed, bool dontAskAgain) =>
        _dialog.ShowOptOutConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OptOutConfirmation(confirmed, dontAskAgain));

    [Fact]
    public async Task AConfirmedRun_IsDetached_AndTheComposerIsCleared()
    {
        var vm = CreateSut();
        ConfirmWith(confirmed: true, dontAskAgain: false);
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        await _manager.Received(1).StartBackgroundRunAsync(Goal, Arg.Any<string?>());
        Assert.Equal(string.Empty, vm.InputText);
    }

    [Fact]
    public async Task ADeclinedRun_StartsNothing_AndKeepsTheGoal()
    {
        var vm = CreateSut();
        ConfirmWith(confirmed: false, dontAskAgain: false);
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        await _manager.DidNotReceive().StartBackgroundRunAsync(Arg.Any<string>(), Arg.Any<string?>());
        Assert.Equal(Goal, vm.InputText);
    }

    [Fact]
    public async Task AConfirmedRun_PublishesTheQueuedNotice()
    {
        var vm = CreateSut();
        ConfirmWith(confirmed: true, dontAskAgain: false);
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        _snackbar.Received(1).Show(
            "Assistant_RunInBackground_Queued",
            "Assistant_RunInBackground_Queued_Body",
            Wpf.Ui.Controls.ControlAppearance.Success,
            null,
            Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task TheOptOutTick_OnAYes_SuppressesTheNextAsk()
    {
        var vm = CreateSut();
        ConfirmWith(confirmed: true, dontAskAgain: true);
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        Assert.True(_appSettings.AssistantBackgroundRunConfirmSuppressed);
        await _settings.Received().SaveSettingsAsync(_appSettings);
    }

    [Fact]
    public async Task TheOptOutTick_OnANo_SuppressesNothing()
    {
        // Declining means this run was not wanted — not that the next one may start unasked.
        var vm = CreateSut();
        ConfirmWith(confirmed: false, dontAskAgain: true);
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        Assert.False(_appSettings.AssistantBackgroundRunConfirmSuppressed);
    }

    [Fact]
    public async Task ASuppressedConfirm_DetachesWithoutAsking()
    {
        var vm = CreateSut();
        _appSettings.AssistantBackgroundRunConfirmSuppressed = true;
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        await _dialog.DidNotReceive().ShowOptOutConfirmationDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _manager.Received(1).StartBackgroundRunAsync(Goal, Arg.Any<string?>());
    }

    [Fact]
    public async Task ADialogThatCannotBeShown_CountsAsUnasked_NotAsApproved()
    {
        var vm = CreateSut();
        _dialog.ShowOptOutConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<OptOutConfirmation>(_ => throw new InvalidOperationException("No dialog host available"));
        vm.InputText = Goal;

        await vm.RunInBackgroundCommand.ExecuteAsync(null);

        await _manager.DidNotReceive().StartBackgroundRunAsync(Arg.Any<string>(), Arg.Any<string?>());
        Assert.Equal(Goal, vm.InputText);
        // A dialog that never appeared is not a launch failure, so that notice must not be what the user reads.
        _snackbar.DidNotReceive().Show(
            Arg.Any<string>(), Arg.Any<string>(), Wpf.Ui.Controls.ControlAppearance.Danger,
            Arg.Any<Wpf.Ui.Controls.IconElement?>(), Arg.Any<TimeSpan>());
    }
}
