using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class LiveTranscriptionViewModelBubbleTests
{
    [Fact]
    public void Utterances_WithinWindow_AndSameSpeaker_MergeIntoSameBubble()
    {
        var (vm, _, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", t0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "world", t0.AddSeconds(10)));

        Assert.Single(vm.Bubbles);
        Assert.Equal("hello world", vm.Bubbles[0].Text);
    }

    [Fact]
    public void Utterances_BeyondWindow_StartNewBubble()
    {
        var (vm, _, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "first", t0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "second", t0.AddSeconds(40)));

        Assert.Equal(2, vm.Bubbles.Count);
        Assert.Equal("first", vm.Bubbles[0].Text);
        Assert.Equal("second", vm.Bubbles[1].Text);
    }

    [Fact]
    public void Utterances_DifferentSpeakers_AlwaysSeparateBubbles()
    {
        var (vm, _, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hi", t0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0.AddSeconds(2)));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "how are you", t0.AddSeconds(4)));

        Assert.Equal(3, vm.Bubbles.Count);
        Assert.Equal(TranscriptSpeaker.You, vm.Bubbles[0].Speaker);
        Assert.Equal(TranscriptSpeaker.Them, vm.Bubbles[1].Speaker);
        Assert.Equal(TranscriptSpeaker.You, vm.Bubbles[2].Speaker);
    }

    [Fact]
    public void SpeakingChanged_True_CreatesEmptyBubble_AndSetsListening()
    {
        var (vm, service, _) = CreateSut();

        service.RaiseSpeaking(TranscriptSpeaker.You, true);

        Assert.Single(vm.Bubbles);
        Assert.True(vm.Bubbles[0].IsListening);
        Assert.Equal(string.Empty, vm.Bubbles[0].Text);
    }

    [Fact]
    public void SpeakingChanged_False_ClearsListening_KeepsBubble()
    {
        var (vm, service, _) = CreateSut();

        service.RaiseSpeaking(TranscriptSpeaker.You, true);
        service.RaiseSpeaking(TranscriptSpeaker.You, false);

        Assert.Single(vm.Bubbles);
        Assert.False(vm.Bubbles[0].IsListening);
    }

    [Fact]
    public void SpeakingChanged_DoesNotCreateBubble_WhenSpeakingFalse()
    {
        var (vm, service, _) = CreateSut();

        // No prior bubble; an end-of-speech event must not synthesise one.
        service.RaiseSpeaking(TranscriptSpeaker.Them, false);

        Assert.Empty(vm.Bubbles);
    }

    [Fact]
    public void SpeakingChanged_LiveBubble_AbsorbsSubsequentUtterance()
    {
        var (vm, service, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        // The user starts talking; the indicator-bubble appears.
        service.RaiseSpeaking(TranscriptSpeaker.You, true);
        // The transcription arrives while the bubble is still in-window — it should
        // populate the same bubble, not create a second one.
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", t0));

        Assert.Single(vm.Bubbles);
        Assert.Equal("hello", vm.Bubbles[0].Text);
    }

    [Fact]
    public void SaveTranscript_CanExecute_FalseWhenEmpty()
    {
        var (vm, _, _) = CreateSut();

        Assert.False(vm.SaveTranscriptCommand.CanExecute(null));
    }

    [Fact]
    public void SaveTranscript_CanExecute_TrueWhenStoppedAndNonEmpty()
    {
        var (vm, _, _) = CreateSut();

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "x", DateTimeOffset.Now));

        Assert.False(vm.IsRunning);
        Assert.True(vm.SaveTranscriptCommand.CanExecute(null));
    }

    [Fact]
    public void BuildMarkdown_ContainsHeader_SpeakerLabels_AndTimestamps()
    {
        var (vm, _, _) = CreateSut();
        var t0 = new DateTimeOffset(2026, 4, 26, 14, 0, 0, TimeSpan.Zero).ToLocalTime();

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hi alice", t0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hi there", t0.AddSeconds(40), SpeakerLabel: "Speaker 1"));

        var md = MeetingTranscriptWriter.Render(
            vm.Bubbles, sessionStart: t0, originalFilename: "x.md", title: "Live transcription");

        Assert.Contains("# Live transcription", md);
        Assert.Contains("**you**", md);
        Assert.Contains("**Speaker 1**", md);
        Assert.Contains("hi alice", md);
        Assert.Contains("hi there", md);
        Assert.Contains(t0.LocalDateTime.ToString("HH:mm:ss"), md);
    }

    [Fact]
    public void ListeningBubble_AdoptsLabel_WhenDiarizedUtteranceArrives()
    {
        // Reproduces the empty-bubble bug from the saved markdown: the listening dot opens
        // a bubble for "Them" with no SpeakerLabel; when the utterance arrives carrying
        // SpeakerLabel="Speaker 1" the existing empty bubble must be reused (label adopted)
        // rather than leaving an empty stub and creating a second bubble.
        var (vm, service, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        service.RaiseSpeaking(TranscriptSpeaker.Them, true);
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0.AddSeconds(2), SpeakerLabel: "Speaker 1"));

        Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 1", vm.Bubbles[0].SpeakerLabel);
        Assert.Equal("hello", vm.Bubbles[0].Text);
    }

    private static (LiveTranscriptionViewModel vm, FakeLiveMeetingService service, RecordingFileDialogService files)
        CreateSut(AppSettings? settings = null)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings ?? new AppSettings());

        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>() switch
        {
            "LiveTrans_Title" => "Live transcription",
            "LiveTrans_OtherSpeaker_Placeholder" => "them",
            _ => ci.Arg<string>(),
        });

        var service = new FakeLiveMeetingService();
        var files = new RecordingFileDialogService();
        var dialogs = Substitute.For<IDialogService>();

        var consentMgr = new Pia.Services.Consent.ConsentStateManager(
            NullLogger<Pia.Services.Consent.ConsentStateManager>.Instance,
            TimeProvider.System);

        var vm = new LiveTranscriptionViewModel(
            service, settingsService, loc, dialogs, files, consentMgr,
            NullLogger<LiveTranscriptionViewModel>.Instance);

        return (vm, service, files);
    }

    internal sealed class FakeLiveMeetingService : ILiveMeetingService
    {
        private readonly Channel<TranscriptUtterance> _channel =
            Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        public LiveMeetingState State { get; private set; } = LiveMeetingState.Idle;

        public event EventHandler<LiveMeetingState>? StateChanged;
        public event EventHandler<SpeakingChangedEventArgs>? SpeakingChanged;

        public ChannelReader<TranscriptUtterance> Utterances => _channel.Reader;

        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            State = LiveMeetingState.Prepared;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            State = LiveMeetingState.Running;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = LiveMeetingState.Idle;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public bool RenameSpeaker(string oldLabel, string newLabel) => false;

        public void RaiseSpeaking(TranscriptSpeaker speaker, bool isSpeaking)
            => SpeakingChanged?.Invoke(this, new SpeakingChangedEventArgs(speaker, isSpeaking));
    }

    internal sealed class RecordingFileDialogService : IFileDialogService
    {
        public string? LastInitialDirectory { get; private set; }
        public string? NextSavePath { get; set; }
        public string? NextFolderPath { get; set; }

        public string? PromptSaveFile(string title, string filter, string defaultFileName, string? initialDirectory)
        {
            LastInitialDirectory = initialDirectory;
            return NextSavePath;
        }

        public string? PromptSelectFolder(string title, string? initialDirectory)
        {
            LastInitialDirectory = initialDirectory;
            return NextFolderPath;
        }
    }
}
