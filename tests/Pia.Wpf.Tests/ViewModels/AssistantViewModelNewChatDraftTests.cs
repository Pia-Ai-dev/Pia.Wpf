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
using System.Threading;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>"New chat" throws the composer draft away, so a real draft has to be confirmed first.</summary>
public class AssistantViewModelNewChatDraftTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();

    private AssistantViewModel CreateSut(bool confirm)
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _manager.GetOrCreateActiveForNewChat().Returns(_ => NewSession());
        _dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(confirm);

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

        var vm = new AssistantViewModel(
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
            _dialog,
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());

        // Drop ctor calls (initial GetOrCreateActiveForNewChat) before counting.
        _manager.ClearReceivedCalls();
        return vm;
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData(" \n x \t")]
    public async Task NoRealDraft_StartsTheNewChatWithoutAsking(string input)
    {
        var vm = CreateSut(confirm: false);
        vm.InputText = input;

        await vm.NewChatCommand.ExecuteAsync(null);

        await _dialog.DidNotReceiveWithAnyArgs().ShowConfirmationDialogAsync(default!, default!);
        _manager.Received(1).GetOrCreateActiveForNewChat();
        Assert.Equal(string.Empty, vm.InputText);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData(" a b ")]
    public async Task Draft_Declined_KeepsTheDraftAndTheChat(string input)
    {
        var vm = CreateSut(confirm: false);
        vm.InputText = input;

        await vm.NewChatCommand.ExecuteAsync(null);

        await _dialog.ReceivedWithAnyArgs(1).ShowConfirmationDialogAsync(default!, default!);
        _manager.DidNotReceive().GetOrCreateActiveForNewChat();
        Assert.Equal(input, vm.InputText);
    }

    [Fact]
    public async Task Draft_Confirmed_StartsTheNewChat()
    {
        var vm = CreateSut(confirm: true);
        vm.InputText = "please summarize the attached report";

        await vm.NewChatCommand.ExecuteAsync(null);

        await _dialog.ReceivedWithAnyArgs(1).ShowConfirmationDialogAsync(default!, default!);
        _manager.Received(1).GetOrCreateActiveForNewChat();
        Assert.Equal(string.Empty, vm.InputText);
    }

    [Fact]
    public async Task ChipNewChat_WithDraft_AsksToo()
    {
        var vm = CreateSut(confirm: false);
        vm.InputText = "half-written question";

        vm.ChatTitleChip.NewChatCommand.Execute(null);

        await _dialog.ReceivedWithAnyArgs(1).ShowConfirmationDialogAsync(default!, default!);
        _manager.DidNotReceive().GetOrCreateActiveForNewChat();
        Assert.Equal("half-written question", vm.InputText);
    }
}
