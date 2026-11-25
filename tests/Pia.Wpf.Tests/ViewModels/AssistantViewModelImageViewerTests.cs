using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
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

public sealed class AssistantViewModelImageViewerTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly Wpf.Ui.ISnackbarService _snackbar = Substitute.For<Wpf.Ui.ISnackbarService>();
    private readonly CapturingLogger<AssistantViewModel> _logger = new();

    [Fact]
    public async Task OpenPendingImage_ShowsThePendingList_AtThatImage()
    {
        var vm = CreateSut();
        vm.PendingAttachments.Add(Attachment());
        vm.PendingAttachments.Add(Attachment());
        var third = Attachment();
        vm.PendingAttachments.Add(third);

        await vm.OpenPendingImageCommand.ExecuteAsync(third);

        await _dialogs.Received(1).ShowImageViewerAsync(
            Arg.Is<IReadOnlyList<ImageAttachment>>(l => l.SequenceEqual(vm.PendingAttachments)), 2);
    }

    [Fact]
    public async Task OpenSentImage_ShowsThatMessagesImages_AtThatImage()
    {
        var vm = CreateSut();
        var message = new AssistantMessage(ChatRole.User, "look");
        message.Attachments.Add(Attachment());
        var second = Attachment();
        message.Attachments.Add(second);
        vm.Messages.Add(new AssistantMessage(ChatRole.User, "first"));
        vm.Messages.Add(message);

        await vm.OpenSentImageCommand.ExecuteAsync(second);

        await _dialogs.Received(1).ShowImageViewerAsync(
            Arg.Is<IReadOnlyList<ImageAttachment>>(l => l.SequenceEqual(message.Attachments)), 1);
    }

    [Fact]
    public async Task OpenSentImage_ForAnImageNoMessageHolds_DoesNothing()
    {
        var vm = CreateSut();
        var message = new AssistantMessage(ChatRole.User, "look");
        message.Attachments.Add(Attachment());
        vm.Messages.Add(message);

        await vm.OpenSentImageCommand.ExecuteAsync(Attachment());

        await _dialogs.DidNotReceiveWithAnyArgs().ShowImageViewerAsync(default!, default);
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

    private AssistantViewModel CreateSut()
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
            _personas,
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
            Substitute.For<IToolPermissionService>());
    }
}
