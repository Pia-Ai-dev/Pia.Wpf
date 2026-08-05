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
/// 18 D1 layer 1 (spec §7 G1), at the <see cref="AssistantViewModel"/> composer boundary: the
/// <c>RunInBackgroundCommand</c>'s own gate now also refuses blatant junk (<see cref="GoalPreflight.IsRefused"/>),
/// and <see cref="AssistantViewModel.GoalTooShortHintVisible"/> is the reason the user sees for the dead
/// button — a copy of the shape <c>AssistantViewModelLeverTests</c>'s <c>ForeignRunActive</c> facts already
/// pin for the sibling composer hint (Assistant_BackgroundRunActive_Hint). Same builder shape: the VM is
/// constructed off any thread with a stub <see cref="System.Threading.SynchronizationContext"/>, and setting
/// <c>InputText</c> here is safe because no View is ever constructed in this file (the AtCommand autocomplete
/// hazard <c>AssistantViewParseTests</c> documents is a real-Dispatcher/behavior-attached-to-the-View concern).
/// </summary>
public class AssistantViewModelGoalPreflightTests
{
    private AssistantViewModel CreateSut()
    {
        if (System.Threading.SynchronizationContext.Current is null)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            settings,
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
            settings,
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
            Substitute.For<IChatSessionManager>(),
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }

    [Fact]
    public void RunInBackground_IsDisabled_ForBlatantJunk()
    {
        var vm = CreateSut();
        vm.InputText = "ggg";

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("Fix CI")]
    [InlineData("Ship it")]
    [InlineData("Fix the build")]
    [InlineData("Write the release notes for v2")]
    public void RunInBackground_StaysEnabled_ForALegitimatelyTerseGoal(string goal)
    {
        // §8.5's false-positive fact: layer 1 must never refuse a real, if short, goal — a dead button here
        // gives the user no recourse.
        var vm = CreateSut();
        vm.InputText = goal;

        Assert.True(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Fact]
    public void GoalTooShortHint_IsVisible_OnlyForBlatantJunk()
    {
        var vm = CreateSut();
        Assert.False(vm.GoalTooShortHintVisible);

        vm.InputText = "ggg";
        Assert.True(vm.GoalTooShortHintVisible);

        vm.InputText = "Fix CI";
        Assert.False(vm.GoalTooShortHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_NeverShows_ForTheEmptyComposerDisabledState()
    {
        // The exact mistake the ForeignRunActive precedent's comment warns against: an empty composer is
        // already a disabled RunInBackgroundCommand for an unrelated reason (no real text at all), so the
        // "goal too short" hint must not also render for it.
        var vm = CreateSut();
        vm.InputText = string.Empty;

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
        Assert.False(vm.GoalTooShortHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_NeverShows_WhileStreaming()
    {
        // Same mistake, the other disabled axis: streaming already explains the dead button.
        var vm = CreateSut();
        vm.InputText = "ggg";
        Assert.True(vm.GoalTooShortHintVisible);

        vm.IsStreaming = true;

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
        Assert.False(vm.GoalTooShortHintVisible);
    }
}
