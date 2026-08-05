using System;
using System.Threading;
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

/// <summary><see cref="AssistantViewModel.RunInBackgroundCommand"/>'s gate refuses blatant junk goals (<see cref="GoalPreflight.IsRefused"/>), with <see cref="AssistantViewModel.GoalTooShortHintVisible"/> as the reason shown for the dead button.</summary>
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
        // Layer 1 must never refuse a real, if short, goal — a dead button here gives the user no recourse.
        var vm = CreateSut();
        vm.InputText = goal;

        Assert.True(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Fact]
    public void GoalTooShortHint_IsVisible_OnlyForBlatantJunk()
    {
        var vm = CreateSut();
        vm.GoalTooShortHintDebounce = TimeSpan.Zero;
        vm.AgentModeEnabled = true;
        Assert.False(vm.GoalTooShortHintVisible);

        vm.InputText = "ggg";
        WaitUntilTrue(() => vm.GoalTooShortHintVisible);

        vm.InputText = "Fix CI";
        Assert.False(vm.GoalTooShortHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_NeverShows_ForTheEmptyComposerDisabledState()
    {
        // An empty composer is already disabled for an unrelated reason, so the goal-too-short hint must not also render.
        var vm = CreateSut();
        vm.GoalTooShortHintDebounce = TimeSpan.Zero;
        vm.AgentModeEnabled = true;
        vm.InputText = string.Empty;

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
        Assert.False(vm.GoalTooShortHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_NeverShows_WhileStreaming()
    {
        // Same idea, the other disabled axis: streaming already explains the dead button.
        var vm = CreateSut();
        vm.GoalTooShortHintDebounce = TimeSpan.Zero;
        vm.AgentModeEnabled = true;
        vm.InputText = "ggg";
        WaitUntilTrue(() => vm.GoalTooShortHintVisible);

        vm.IsStreaming = true;

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
        Assert.False(vm.GoalTooShortHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_NeverShows_InChatMode()
    {
        // The hint explains the dead Run-in-background button, which only exists in agent mode.
        var vm = CreateSut();
        vm.GoalTooShortHintDebounce = TimeSpan.Zero;
        vm.InputText = "ggg";

        Thread.Sleep(50);
        Assert.False(vm.GoalTooShortHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_IsDebounced_OneSecondAfterTheLastKeystroke()
    {
        var vm = CreateSut();
        vm.AgentModeEnabled = true;
        vm.InputText = "ggg";

        // Showing is deferred, so the hint must not pop the moment typing starts.
        Assert.False(vm.GoalTooShortHintVisible);

        WaitUntilTrue(() => vm.GoalTooShortHintVisible);
    }

    private static void WaitUntilTrue(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(() => condition(), TimeSpan.FromSeconds(3)));
}
