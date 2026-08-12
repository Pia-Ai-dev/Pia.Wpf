using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The composer text is a draft for either destination, so opening the dialog copies it and leaves
/// the composer holding it.</summary>
public class AssistantViewModelAssignmentTests
{
    private const string Draft = "Summarise what we agreed on Tuesday";

    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly IAssignmentApiClient _assignments = Substitute.For<IAssignmentApiClient>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly AssignmentConsentViewModel _consent;

    public AssistantViewModelAssignmentTests()
    {
        _consent = new AssignmentConsentViewModel(
            Substitute.For<IAssignmentScopeResolver>(),
            Substitute.For<IAssignmentConsentStore>(),
            Substitute.For<IAssignmentRunOrchestrator>(),
            _localization,
            NullLogger<AssignmentConsentViewModel>.Instance);
    }

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _manager.GetOrCreateActiveForNewChat().Returns(NewSession());
        _dialog.ShowAssignmentConsentDialogAsync(Arg.Any<AssignmentConsentViewModel>()).Returns(true);

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
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
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
            Substitute.For<IToolPermissionService>(),
            assignmentApiClient: _assignments,
            assignmentConsentFactory: () => _consent);
    }

    private static ChatSession NewSession() => new(
        Substitute.For<ITokenMapService>(),
        Substitute.For<IAiClientService>(),
        Substitute.For<IPluginService>(),
        Substitute.For<IActionCardBuilder>(),
        Substitute.For<IToolPermissionService>(),
        Substitute.For<ILocalizationService>(),
        NullLogger.Instance,
        _ => true);

    private void SurfaceOffersAPromptOnlySkill() =>
        _assignments.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(
            new AssignmentSurface(true, [new AssignmentSkill("research", "Research", "research", [])]));

    [Fact]
    public async Task OpeningTheDialog_PrefillsTheComposerText_AndLeavesTheComposerHoldingIt()
    {
        SurfaceOffersAPromptOnlySkill();
        var vm = CreateSut();
        await vm.RefreshAssignmentSurfaceAsync();
        vm.InputText = Draft;

        await vm.RunAssignmentCommand.ExecuteAsync(null);

        await _dialog.Received(1).ShowAssignmentConsentDialogAsync(_consent);
        Assert.Equal(Draft, _consent.Prompt);
        Assert.Equal(Draft, vm.InputText);
    }

    [Fact]
    public async Task AnAvailableSurface_ShowsTheActionRowButton()
    {
        SurfaceOffersAPromptOnlySkill();
        var vm = CreateSut();

        await vm.RefreshAssignmentSurfaceAsync();

        Assert.True(vm.IsAssignmentSurfaceAvailable);
    }

    [Fact]
    public async Task AHiddenSurface_OpensNothing()
    {
        _assignments.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(AssignmentSurface.Hidden);
        var vm = CreateSut();
        await vm.RefreshAssignmentSurfaceAsync();
        vm.InputText = Draft;

        await vm.RunAssignmentCommand.ExecuteAsync(null);

        Assert.False(vm.IsAssignmentSurfaceAvailable);
        await _dialog.DidNotReceive().ShowAssignmentConsentDialogAsync(Arg.Any<AssignmentConsentViewModel>());
        Assert.Equal(Draft, vm.InputText);
    }
}
