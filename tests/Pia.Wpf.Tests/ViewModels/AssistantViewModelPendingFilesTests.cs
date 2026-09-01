using System.IO;
using System.Threading;
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

/// <summary>The composer half of file attachments: the chip strip drives Send, blocks a background run,
/// and survives (or does not) a send, a refusal and a new chat.</summary>
public sealed class AssistantViewModelPendingFilesTests : IDisposable
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly string _dir;

    public AssistantViewModelPendingFilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-vmattach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());

        // Five Arg.Any<> would leave the sixth parameter at its default, miss the call and hand the
        // awaited send a null Task.
        _manager.StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<string?>()).Returns(true);

        // StartFreshChat calls SetWorkingDirectory on whatever this returns.
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
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }

    private static PendingFileAttachment Chip(string fileName = "notes.txt") => new()
    {
        FullPath = @"C:\work\" + fileName,
        FileName = fileName,
        Kind = PendingFileKind.Text,
        Text = "the quarterly numbers",
        Truncated = false,
        OriginalCharCount = 21,
    };

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WritePng(string name)
    {
        var path = Path.Combine(_dir, name);
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[4], 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixel));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static void WaitUntilTrue(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(() => condition(), TimeSpan.FromSeconds(3)));

    // ---- Send availability -----------------------------------------------------------------------

    [Fact]
    public void AddingAPendingFile_EnablesSend()
    {
        var vm = CreateSut();
        Assert.False(vm.SendMessageCommand.CanExecute(null));
        var requeries = 0;
        vm.SendMessageCommand.CanExecuteChanged += (_, _) => requeries++;

        vm.PendingFiles.Add(Chip());

        Assert.True(vm.SendMessageCommand.CanExecute(null));
        // A collection change notifies the collection, never the command: without the explicit re-query
        // the button stays greyed out until the next keystroke, and CanExecute alone cannot see that.
        Assert.Equal(1, requeries);
    }

    [Fact]
    public void RemovingTheLastPendingFile_DisablesSend()
    {
        var vm = CreateSut();
        var chip = Chip();
        vm.PendingFiles.Add(chip);

        vm.PendingFiles.Remove(chip);

        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void AddingAPendingFile_RaisesHasPendingFiles()
    {
        var vm = CreateSut();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is { } n) raised.Add(n); };

        vm.PendingFiles.Add(Chip());

        Assert.True(vm.HasPendingFiles);
        Assert.Contains(nameof(AssistantViewModel.HasPendingFiles), raised);
    }

    // ---- Run in background -----------------------------------------------------------------------

    [Fact]
    public void TextPlusPendingFile_DoesNotEnableRunInBackground()
    {
        // Text AND a file: a file on its own leaves the command disabled anyway, so that case would
        // prove nothing.
        var vm = CreateSut();
        vm.InputText = "summarize the attached report for the team";
        Assert.True(vm.RunInBackgroundCommand.CanExecute(null));
        var requeries = 0;
        vm.RunInBackgroundCommand.CanExecuteChanged += (_, _) => requeries++;

        vm.PendingFiles.Add(Chip());

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
        Assert.Equal(1, requeries);
    }

    [Fact]
    public void RemovingTheLastPendingFile_ReEnablesRunInBackground()
    {
        var vm = CreateSut();
        vm.InputText = "summarize the attached report for the team";
        var chip = Chip();
        vm.PendingFiles.Add(chip);

        vm.PendingFiles.Remove(chip);

        Assert.True(vm.RunInBackgroundCommand.CanExecute(null));
    }

    // ---- The composer hint -----------------------------------------------------------------------

    [Fact]
    public void AddingAPendingFile_ShowsTheBlockRunHint()
    {
        // Asserted with no keystroke and no wait after the collection change: routed through the
        // debounced goal hint instead, the dead button would sit unexplained for a second.
        var vm = CreateSut();
        vm.AgentModeEnabled = true;
        Assert.False(vm.PendingFilesBlockRunHintVisible);

        vm.PendingFiles.Add(Chip());

        Assert.True(vm.PendingFilesBlockRunHintVisible);
    }

    [Fact]
    public void RemovingTheLastPendingFile_HidesTheBlockRunHint()
    {
        var vm = CreateSut();
        vm.AgentModeEnabled = true;
        var chip = Chip();
        vm.PendingFiles.Add(chip);

        vm.PendingFiles.Remove(chip);

        Assert.False(vm.PendingFilesBlockRunHintVisible);
    }

    [Fact]
    public void ChatModeWithTypedText_ShowsNoBlockRunHint()
    {
        var vm = CreateSut();
        vm.AgentModeEnabled = false;

        vm.InputText = "summarise the attached report";
        vm.PendingFiles.Add(Chip());

        // Run in background is hidden outside Agent mode and a chat turn was never going to be planned,
        // so both halves of the sentence would be false here.
        Assert.False(vm.PendingFilesBlockRunHintVisible);
    }

    [Fact]
    public void FlippingToAgentModeWithAChipAlreadyStaged_ShowsTheBlockRunHint()
    {
        var vm = CreateSut();
        vm.AgentModeEnabled = false;
        vm.PendingFiles.Add(Chip());

        vm.AgentModeEnabled = true;

        // The lever, not the chip, is the change that makes the hint true here — every other hint test
        // stages the chip last, so only this one covers the property-change route.
        Assert.True(vm.PendingFilesBlockRunHintVisible);
        Assert.False(vm.AgentModeHintVisible);
    }

    [Fact]
    public void GoalTooShortHint_WinsOverTheBlockRunHint()
    {
        var vm = CreateSut();
        vm.GoalTooShortHintDebounce = TimeSpan.Zero;
        vm.AgentModeEnabled = true;
        vm.InputText = "ggg";
        WaitUntilTrue(() => vm.GoalTooShortHintVisible);

        vm.PendingFiles.Add(Chip());

        Assert.True(vm.GoalTooShortHintVisible);
        Assert.False(vm.PendingFilesBlockRunHintVisible);
    }

    // ---- Send ------------------------------------------------------------------------------------

    [Fact]
    public async Task AgentModeSendWithAPendingFile_IsNotPlanned()
    {
        var vm = CreateSut();
        vm.ActivePersona = new Persona
        {
            Name = "Tester",
            SystemPrompt = "be helpful",
            ToolScope = PersonaToolScope.Full,
        };
        vm.AgentModeEnabled = true;
        vm.InputText = "summarize the attached report for the team";
        vm.PendingFiles.Add(Chip());

        await vm.SendMessageCommand.ExecuteAsync(null);

        await _manager.Received(1).StartTurnAsync(
            Arg.Any<ChatSession>(), "summarize the attached report for the team",
            Arg.Any<ImageAttachment?>(), Arg.Any<string?>(),
            planned: false,
            attachedFileContext: Arg.Is<string?>(s => s != null && s.Contains("notes.txt")));
        // The downgrade is per-turn: the lever stays where the user put it.
        Assert.True(vm.AgentModeEnabled);
    }

    [Fact]
    public async Task Send_ClearsPendingFiles()
    {
        var vm = CreateSut();
        vm.InputText = "summarize this";
        vm.PendingFiles.Add(Chip());

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Empty(vm.PendingFiles);
    }

    [Fact]
    public async Task RefusedSend_RestoresPendingFiles()
    {
        var vm = CreateSut();
        _manager.StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<string?>()).Returns(false);
        var chip = Chip();
        vm.InputText = "summarize this";
        vm.PendingFiles.Add(chip);

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Same(chip, Assert.Single(vm.PendingFiles));
        Assert.Equal("summarize this", vm.InputText);
    }

    [Fact]
    public void StartFreshChat_ClearsPendingFiles()
    {
        var vm = CreateSut();
        vm.PendingFiles.Add(Chip());

        vm.NewChatCommand.Execute(null);

        Assert.Empty(vm.PendingFiles);
    }

    [Fact]
    public void SwitchingChat_ClearsTheChipsAndTheImage()
    {
        var vm = CreateSut();
        vm.PendingFiles.Add(Chip());
        vm.PendingAttachment = Attachment();

        Activate(Session());
        Activate(Session());

        // Staged for the chat the user left; carrying it over would send it into the wrong conversation.
        Assert.Empty(vm.PendingFiles);
        Assert.Null(vm.PendingAttachment);
    }

    [Fact]
    public void ReselectingTheOpenChat_LeavesTheComposerAlone()
    {
        var vm = CreateSut();
        var session = Session();
        Activate(session);

        vm.PendingFiles.Add(Chip());
        vm.PendingAttachment = Attachment();
        Activate(session);

        Assert.Single(vm.PendingFiles);
        Assert.NotNull(vm.PendingAttachment);
    }

    private void Activate(ChatSession session) =>
        _manager.ActiveChanged += Raise.Event<EventHandler<ChatSession?>>(_manager, session);

    private static ChatSession Session() => new(
        Substitute.For<ITokenMapService>(),
        Substitute.For<IAiClientService>(),
        Substitute.For<IPluginService>(),
        Substitute.For<IActionCardBuilder>(),
        Substitute.For<IToolPermissionService>(),
        Substitute.For<ILocalizationService>(),
        NullLogger.Instance,
        _ => false);

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

    [Fact]
    public void RemovePendingFileCommand_RemovesOnlyThatFile()
    {
        var vm = CreateSut();
        var first = Chip("first.txt");
        var second = Chip("second.txt");
        vm.PendingFiles.Add(first);
        vm.PendingFiles.Add(second);

        vm.RemovePendingFileCommand.Execute(first);

        Assert.Same(second, Assert.Single(vm.PendingFiles));
    }

    // ---- The drop itself -------------------------------------------------------------------------

    [Fact]
    public async Task HandleFilesDropped_WhileStreaming_StagesNothing()
    {
        var vm = CreateSut();
        vm.IsStreaming = true;
        var path = Write("notes.txt", "hello");

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { path });

        Assert.Empty(vm.PendingFiles);
    }

    [Fact]
    public async Task HandleFilesDropped_RoutesImagesAndTextSeparately()
    {
        var vm = CreateSut();
        var text = Write("notes.txt", "hello");
        var image = Path.Combine(_dir, "shot.png");

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { image, text });

        Assert.Equal("notes.txt", Assert.Single(vm.PendingFiles).FileName);
        // The image never becomes a chip; it goes down the image-attachment path, whose first step is
        // the vision-provider check.
        await _providers.Received(1).GetDefaultProviderForModeAsync(WindowMode.Assistant);
    }

    [Fact]
    public async Task TwoImagesRefusedByTheProvider_DoNotClaimOneWasKept()
    {
        var vm = CreateSut();
        var first = WritePng("a.png");
        var second = WritePng("b.png");

        // No provider at all, so the vision gate refuses before anything is staged.
        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { first, second });

        Assert.Null(vm.PendingAttachment);
        _ = _localization.Received(1)["Msg_File_ImageProviderUnsupported"];
        _localization.DidNotReceive().Format("Msg_File_OneImageOnly", Arg.Any<object[]>());
    }

    [Fact]
    public async Task TwoImagesKeptByAVisionProvider_NameTheOneThatWasKept()
    {
        var vm = CreateSut();
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(new AiProvider
        {
            Name = "Cloud",
            Endpoint = "https://example.invalid",
            ProviderType = AiProviderType.PiaCloud,
        });
        var first = WritePng("a.png");
        var second = WritePng("b.png");

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { first, second });

        Assert.NotNull(vm.PendingAttachment);
        _localization.Received(1).Format(
            "Msg_File_OneImageOnly",
            Arg.Is<object[]>(args => args.Length == 1 && (string)args[0] == "a.png"));
    }

    /// <summary>A drop that produced no chip has to say so where the user is looking. The corner snackbar is
    /// not enough on its own: a drop leaves the source app in the foreground, so the toast can render behind
    /// the window the mail was dragged from.</summary>
    [Fact]
    public void AFailedDrop_PutsItsReasonInTheComposer()
    {
        var vm = CreateSut();
        _localization["Msg_File_DropNoFile"].Returns("no file to take");

        vm.HandleDropFailedCommand.Execute(null);

        Assert.Equal("no file to take", vm.DropFailureMessage);
    }

    /// <summary>A source that named the item it would not hand over gets the named wording instead.</summary>
    [Fact]
    public void AFailedDropWithAName_NamesTheItem()
    {
        var vm = CreateSut();
        _localization.Format("Msg_File_DropFailed", Arg.Any<object[]>()).Returns("could not take Angebot.msg");

        vm.HandleDropFailedCommand.Execute("Angebot.msg");

        Assert.Equal("could not take Angebot.msg", vm.DropFailureMessage);
        _localization.Received(1).Format(
            "Msg_File_DropFailed",
            Arg.Is<object[]>(args => args.Length == 1 && (string)args[0] == "Angebot.msg"));
        _localization.DidNotReceive()["Msg_File_DropNoFile"].ToString();
    }

    [Fact]
    public void TypingClearsAFailedDropNotice()
    {
        var vm = CreateSut();
        _localization["Msg_File_DropNoFile"].Returns("no file to take");
        vm.HandleDropFailedCommand.Execute(null);

        vm.InputText = "what was in that mail?";

        Assert.Null(vm.DropFailureMessage);
    }

    /// <summary>A drop that works after one that did not must not leave the stale reason on screen.</summary>
    [Fact]
    public async Task ASuccessfulDropClearsAFailedDropNotice()
    {
        var vm = CreateSut();
        _localization["Msg_File_DropNoFile"].Returns("no file to take");
        vm.HandleDropFailedCommand.Execute(null);

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { Write("notes.txt", "hello") });

        Assert.Single(vm.PendingFiles);
        Assert.Null(vm.DropFailureMessage);
    }
}
