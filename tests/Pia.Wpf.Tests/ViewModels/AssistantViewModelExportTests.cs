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
/// Export asks where the answer goes before writing anything, so a cancelled dialog — at either step —
/// must leave nothing behind.
/// </summary>
public class AssistantViewModelExportTests
{
    private const string Answer = "The harvest peaked in week 34.";
    private const string ExternalPath = @"C:\pia-not-written\Week 34.html";
    private const string VaultPath = @"C:\pia-not-written\Week 34.md";

    private readonly IMarkdownExportService _export = Substitute.For<IMarkdownExportService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly IFileDialogService _fileDialog = Substitute.For<IFileDialogService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    /// <summary>Answers the dialog with <paramref name="destination"/>, applying the caller's edits first.</summary>
    private void AnswerDialogWith(AnswerExportDestination destination, Action<AnswerExportEditModel>? edit = null)
    {
        _dialog.ShowAnswerExportDialogAsync(Arg.Any<AnswerExportEditModel>()).Returns(ci =>
        {
            edit?.Invoke(ci.Arg<AnswerExportEditModel>());
            return destination;
        });
    }

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _export.SuggestFileName(Arg.Any<string>(), Arg.Any<string>()).Returns("Harvest report");
        _export.ExportToVaultAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(VaultPath);
        _fileDialog.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ExternalPath);

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
            Substitute.For<IChatSessionManager>(),
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            _export,
            _dialog,
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>(),
            fileDialogService: _fileDialog);
    }

    private static AssistantMessage NewAnswer(string content = Answer) => new(ChatRole.Assistant, content);

    [Fact]
    public async Task Export_OffersTheDerivedNameInTheDialog()
    {
        AnswerDialogWith(AnswerExportDestination.Cancel);

        var vm = CreateSut();
        await vm.ExportMessageCommand.ExecuteAsync(NewAnswer());

        await _dialog.Received(1).ShowAnswerExportDialogAsync(
            Arg.Is<AnswerExportEditModel>(m => m.FileName == "Harvest report" && m.OpenAfterStorage));
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing()
    {
        AnswerDialogWith(AnswerExportDestination.Cancel);

        var vm = CreateSut();
        var message = NewAnswer();
        await vm.ExportMessageCommand.ExecuteAsync(message);

        await _export.DidNotReceiveWithAnyArgs().ExportToVaultAsync(default!, default!, default!, Arg.Any<CancellationToken>());
        await _export.DidNotReceiveWithAnyArgs().ExportToPathAsync(default!, default!, default!, default, Arg.Any<CancellationToken>());
        Assert.Empty(message.FileRefs);
    }

    [Fact]
    public async Task Export_ToVault_WritesTheTypedNameAndChipsTheFile()
    {
        AnswerDialogWith(AnswerExportDestination.Vault, m => m.FileName = "Week 34");

        var vm = CreateSut();
        var message = NewAnswer();
        await vm.ExportMessageCommand.ExecuteAsync(message);

        await _export.Received(1).ExportToVaultAsync(Answer, "Week 34", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _export.DidNotReceiveWithAnyArgs().ExportToPathAsync(default!, default!, default!, default, Arg.Any<CancellationToken>());
        Assert.Equal(VaultPath, Assert.Single(message.FileRefs).AbsolutePath);
    }

    [Fact]
    public async Task Export_External_OffersTheNameWithAnHtmlExtension_AndWritesWhereThePickerLanded()
    {
        AnswerDialogWith(AnswerExportDestination.External, m => m.FileName = "Week 34");

        var vm = CreateSut();
        var message = NewAnswer();
        await vm.ExportMessageCommand.ExecuteAsync(message);

        _fileDialog.Received(1).PromptSaveFile(
            Arg.Any<string>(), Arg.Any<string>(), "Week 34.html", Arg.Any<string?>());
        await _export.Received(1).ExportToPathAsync(
            Answer, ExternalPath, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _export.DidNotReceiveWithAnyArgs().ExportToVaultAsync(default!, default!, default!, Arg.Any<CancellationToken>());
        Assert.Equal(ExternalPath, Assert.Single(message.FileRefs).AbsolutePath);
    }

    [Fact]
    public async Task Export_External_SavePickerCancelled_WritesNothing()
    {
        AnswerDialogWith(AnswerExportDestination.External);

        // After CreateSut, which stubs the picker with a path of its own.
        var vm = CreateSut();
        _fileDialog.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns((string?)null);

        var message = NewAnswer();
        await vm.ExportMessageCommand.ExecuteAsync(message);

        await _export.DidNotReceiveWithAnyArgs().ExportToPathAsync(default!, default!, default!, default, Arg.Any<CancellationToken>());
        Assert.Empty(message.FileRefs);
    }

    [Fact]
    public async Task Export_EmptyAnswer_NeverOpensTheDialog()
    {
        var vm = CreateSut();
        await vm.ExportMessageCommand.ExecuteAsync(NewAnswer(string.Empty));

        await _dialog.DidNotReceiveWithAnyArgs().ShowAnswerExportDialogAsync(default!);
    }

    /// <summary>The chip is added either way; only the launch is opt-out.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_ChipsTheFile_WhicheverWayTheOpenBoxIsTicked(bool openAfter)
    {
        AnswerDialogWith(AnswerExportDestination.Vault, m =>
        {
            m.FileName = "Week 34";
            m.OpenAfterStorage = openAfter;
        });

        var vm = CreateSut();
        var message = NewAnswer();
        await vm.ExportMessageCommand.ExecuteAsync(message);

        Assert.Equal(VaultPath, Assert.Single(message.FileRefs).AbsolutePath);
    }
}
