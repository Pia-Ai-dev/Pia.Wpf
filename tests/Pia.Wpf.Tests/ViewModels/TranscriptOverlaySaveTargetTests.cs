using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Both overlays share one save-to-file flow, so each assertion is made against both: a regression that
/// reaches only one of them is the failure mode this file exists to catch.
/// </summary>
public sealed class TranscriptOverlaySaveTargetTests : IDisposable
{
    private readonly IFileDialogService _fileDialog = Substitute.For<IFileDialogService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly IWorkingDirectoryService _workingDir = Substitute.For<IWorkingDirectoryService>();
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    private readonly string _root;
    private readonly string _workdir;
    private readonly string _pinned;

    public TranscriptOverlaySaveTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-transcript-save-" + Guid.NewGuid().ToString("N"));
        _workdir = Path.Combine(_root, "workdir");
        _pinned = Path.Combine(_root, "pinned");
        Directory.CreateDirectory(_workdir);
        Directory.CreateDirectory(_pinned);

        _loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _settingsService.GetSettingsAsync().Returns(new AppSettings());
        // A real folder, so the flow's Directory.CreateDirectory is a no-op instead of creating a junk path.
        _workingDir.ResolveAbsolutePath(Arg.Any<string?>()).Returns(_workdir);
        _sessions.ActiveSession.Returns(NewSession("projects/app"));
    }

    public void Dispose() => TempPath.Remove(_root);

    [Fact]
    public async Task Save_Direct_PreselectsTheChatWorkingFolder_AndLeadsTheNameWithTheDate()
    {
        await SaveAsync(CreateDirect());

        AssertSaveDialog("direct-transcript", _workdir);
    }

    [Fact]
    public async Task Save_Meeting_PreselectsTheChatWorkingFolder_AndLeadsTheNameWithTheDate()
    {
        await SaveAsync(CreateMeeting());

        AssertSaveDialog("meeting", _workdir);
    }

    [Fact]
    public async Task Save_ResolvesTheActiveChatsOwnWorkingDirectory()
    {
        // Asserting the resolved folder alone would also pass if the chat's own subpath were never read.
        await SaveAsync(CreateDirect());

        _workingDir.Received(1).ResolveAbsolutePath("projects/app");
    }

    [Fact]
    public async Task Save_WhenATranscriptFolderIsPinned_PrefersItOverTheWorkingFolder()
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings { MeetingTranscriptFolder = _pinned });

        await SaveAsync(CreateMeeting());

        AssertSaveDialog("meeting", _pinned);
        _workingDir.DidNotReceiveWithAnyArgs().ResolveAbsolutePath(default);
    }

    // The name is matched by shape: _sessionStart is only assigned in the start path, so an overlay
    // driven through AddUtterance alone renders the default DateTimeOffset.
    private void AssertSaveDialog(string prefix, string expectedFolder) =>
        _fileDialog.Received(1).PromptSaveFile(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(name => Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}_" + prefix + @"\.md$")),
            expectedFolder);

    /// <summary>One utterance on a stopped overlay is what CanSaveTranscript asks for.</summary>
    private static async Task SaveAsync(TranscriptOverlayViewModel vm)
    {
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", DateTimeOffset.Now, "Speaker 1"));
        await ((IAsyncRelayCommand)vm.SaveTranscriptCommand).ExecuteAsync(null);
    }

    private TranscriptOverlayViewModel CreateDirect() => new DirectTranscriptionViewModel(
        Substitute.For<IDirectTranscriptionService>(), _settingsService, _loc, _fileDialog,
        Substitute.For<IDialogService>(), Substitute.For<IMemoryService>(),
        Substitute.For<IIngestScheduler>(), Substitute.For<Wpf.Ui.ISnackbarService>(),
        NullLogger<DirectTranscriptionViewModel>.Instance, new InlineUiDispatcher(),
        chatSessionManager: _sessions, workingDirectoryService: _workingDir);

    private TranscriptOverlayViewModel CreateMeeting()
    {
        var service = Substitute.For<IMeetingAttendeeService>();
        service.ObservedAttendees.Returns(Array.Empty<string>());
        return new MeetingAttendeeViewModel(
            service, _settingsService, _loc, _fileDialog,
            Substitute.For<IDialogService>(), Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(), Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance, new InlineUiDispatcher(),
            chatSessionManager: _sessions, workingDirectoryService: _workingDir);
    }

    private static ChatSession NewSession(string? workingDirectory)
    {
        var session = new ChatSession(
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAiClientService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IActionCardBuilder>(),
            Substitute.For<IToolPermissionService>(),
            Substitute.For<ILocalizationService>(),
            NullLogger.Instance,
            _ => false);
        session.SetWorkingDirectory(workingDirectory);
        return session;
    }
}

/// <summary>
/// Separate class because this fallback lands on PiaPaths.LocalDataDirectory, which the fixture redirects
/// to a throwaway profile — the non-parallel collection must not serialize the tests above with it.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class TranscriptOverlaySaveFallbackTests : IClassFixture<RedirectedProfileFixture>
{
    private readonly IFileDialogService _fileDialog = Substitute.For<IFileDialogService>();
    private readonly IWorkingDirectoryService _workingDir = Substitute.For<IWorkingDirectoryService>();

    public TranscriptOverlaySaveFallbackTests(RedirectedProfileFixture profile) => _ = profile;

    [Fact]
    public async Task Save_WhenTheWorkingFolderCannotBeResolved_FallsBackToTheMeetingsFolder()
    {
        _workingDir.ResolveAbsolutePath(Arg.Any<string?>()).Returns((string?)null);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());

        var vm = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(), settings, loc, _fileDialog,
            Substitute.For<IDialogService>(), Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(), Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<DirectTranscriptionViewModel>.Instance, new InlineUiDispatcher(),
            chatSessionManager: Substitute.For<IChatSessionManager>(),
            workingDirectoryService: _workingDir);

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", DateTimeOffset.Now, "Speaker 1"));
        await ((IAsyncRelayCommand)vm.SaveTranscriptCommand).ExecuteAsync(null);

        _fileDialog.Received(1).PromptSaveFile(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            MeetingTranscriptPaths.DefaultMeetingFolder);
    }
}
