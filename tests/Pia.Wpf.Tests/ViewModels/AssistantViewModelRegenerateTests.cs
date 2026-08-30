using Microsoft.Extensions.AI;
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

/// <summary>
/// A styled Regenerate drops the answer it is meant to transform, so the instruction has to carry it.
/// </summary>
public class AssistantViewModelRegenerateTests
{
    private const string Prompt = "Search the web for what is new about the Sonnenblume harvest.";
    private const string PreviousAnswer = "AVGO rose 3.71% to $368.79 on 2026-08-29.";

    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ChatSession _session = NewSession();

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _manager.GetOrCreateActiveForNewChat().Returns(_session);
        _manager.ActiveSession.Returns(_session);
        _manager.StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(),
            Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);

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

    private static ChatSession NewSession() => new(
        Substitute.For<ITokenMapService>(),
        Substitute.For<IAiClientService>(),
        Substitute.For<IPluginService>(),
        Substitute.For<IActionCardBuilder>(),
        Substitute.For<IToolPermissionService>(),
        Substitute.For<ILocalizationService>(),
        NullLogger.Instance,
        _ => true);

    private AssistantMessage SeedExchange(AssistantViewModel vm)
    {
        var answer = new AssistantMessage(ChatRole.Assistant, PreviousAnswer);
        vm.Messages.Add(new AssistantMessage(ChatRole.User, Prompt));
        vm.Messages.Add(answer);
        return answer;
    }

    private async Task<string?> RegenerateAndCaptureInstruction(RegenerateStyle style)
    {
        var vm = CreateSut();
        var answer = SeedExchange(vm);

        await vm.RegenerateStyledCommand.ExecuteAsync(new RegenerateRequest(answer, style));

        var call = _manager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IChatSessionManager.StartTurnAsync));
        var args = call.GetArguments();
        // The re-sent user bubble must stay the original prompt — the instruction rides beside it.
        Assert.Equal(Prompt, (string?)args[1]);
        return (string?)args[3];
    }

    [Theory]
    [InlineData(RegenerateStyle.Shorten)]
    [InlineData(RegenerateStyle.Detailed)]
    [InlineData(RegenerateStyle.Exportable)]
    public async Task StyledRegenerate_HandsTheDroppedAnswerToTheTurn(RegenerateStyle style)
    {
        var instruction = await RegenerateAndCaptureInstruction(style);

        Assert.Contains(PreviousAnswer, instruction!, StringComparison.Ordinal);
        Assert.Contains("<previous_answer>", instruction!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainRegenerate_SendsNoInstruction_SoTheTaskIsRerun()
    {
        Assert.Null(await RegenerateAndCaptureInstruction(RegenerateStyle.Default));
    }

    [Fact]
    public async Task StyledRegenerate_RemovesTheOldPairFromTheTranscript()
    {
        var vm = CreateSut();
        var answer = SeedExchange(vm);

        await vm.RegenerateStyledCommand.ExecuteAsync(new RegenerateRequest(answer, RegenerateStyle.Exportable));

        Assert.Empty(vm.Messages);
        Assert.False(vm.HasMessages);
    }
}
