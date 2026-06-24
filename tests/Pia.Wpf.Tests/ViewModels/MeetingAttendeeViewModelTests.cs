using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="MeetingAttendeeViewModel"/> logic with a faked
/// <see cref="IMeetingAttendeeService"/>: URL validation + consent gating of the Join command,
/// utterance→bubble mapping (ported behaviour), and state→status mapping. Bubble mapping is driven
/// through the internal <c>AddUtterance</c> seam and status through a raised <c>StateChanged</c> so the
/// tests are deterministic and never spin the background reader (DispatchToUi runs inline when there is
/// no WPF Application).
/// </summary>
public class MeetingAttendeeViewModelTests
{
    private const string ValidUrl = "https://teams.microsoft.com/l/meetup-join/abc";

    // ---- Start command gating --------------------------------------------------------------------

    [Fact]
    public void Start_CannotExecute_WhenUrlEmpty()
    {
        var (vm, _) = CreateSut();
        vm.ConsentAcknowledged = true;
        vm.MeetingUrl = "";

        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_CannotExecute_WhenUrlNotTeams()
    {
        var (vm, _) = CreateSut();
        vm.ConsentAcknowledged = true;
        vm.MeetingUrl = "https://example.com/meeting";

        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_CannotExecute_WhenConsentMissing()
    {
        var (vm, _) = CreateSut();
        vm.MeetingUrl = ValidUrl;
        vm.ConsentAcknowledged = false;

        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_CanExecute_WhenTeamsUrlAndConsent()
    {
        var (vm, _) = CreateSut();
        vm.MeetingUrl = ValidUrl;
        vm.ConsentAcknowledged = true;

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_CannotExecute_WhileRunning()
    {
        var (vm, service) = CreateSut();
        vm.MeetingUrl = ValidUrl;
        vm.ConsentAcknowledged = true;

        // Service transitions to an active state — Start must be disabled while attending.
        service.RaiseState(MeetingAttendeeState.Attending);

        Assert.True(vm.IsRunning);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_PassesUrlToService_WhenGatesPass()
    {
        var (vm, service) = CreateSut();
        vm.MeetingUrl = ValidUrl;
        vm.ConsentAcknowledged = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(ValidUrl, service.LastStartUrl);
        Assert.Equal(1, service.StartCount);

        vm.Dispose();
    }

    [Fact]
    public async Task Start_NoOp_WhenGatesFail()
    {
        var (vm, service) = CreateSut();
        vm.MeetingUrl = "not-a-url";
        vm.ConsentAcknowledged = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.StartCount);

        vm.Dispose();
    }

    // ---- State → status + IsRunning --------------------------------------------------------------

    [Theory]
    [InlineData(MeetingAttendeeState.Idle, "MeetingAttendee_Status_Idle", false)]
    [InlineData(MeetingAttendeeState.ProvisioningBrowser, "MeetingAttendee_Status_Provisioning", true)]
    [InlineData(MeetingAttendeeState.Joining, "MeetingAttendee_Status_Joining", true)]
    [InlineData(MeetingAttendeeState.InLobby, "MeetingAttendee_Status_InLobby", true)]
    [InlineData(MeetingAttendeeState.Attending, "MeetingAttendee_Status_Attending", true)]
    [InlineData(MeetingAttendeeState.Stopping, "MeetingAttendee_Status_Stopping", true)]
    [InlineData(MeetingAttendeeState.Error, "MeetingAttendee_Status_Error", false)]
    public void StateChanged_MapsToStatusText_AndIsRunning(
        MeetingAttendeeState state, string expectedKey, bool expectedRunning)
    {
        var (vm, service) = CreateSut();

        service.RaiseState(state);

        Assert.Equal(expectedKey, vm.StatusText);
        Assert.Equal(expectedRunning, vm.IsRunning);
    }

    // ---- Utterance → bubble mapping --------------------------------------------------------------

    [Fact]
    public void Utterances_SameSpeakerWithinWindow_Merge()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", t0.AddSeconds(5)));

        Assert.Single(vm.Bubbles);
        Assert.Equal("hello world", vm.Bubbles[0].Text);
    }

    [Fact]
    public void Utterances_BeyondWindow_StartNewBubble()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "first", t0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "second", t0.AddSeconds(40)));

        Assert.Equal(2, vm.Bubbles.Count);
    }

    // ---- Save gating + markdown ------------------------------------------------------------------

    [Fact]
    public void Save_CanExecute_FalseWhenEmpty()
    {
        var (vm, _) = CreateSut();

        Assert.False(vm.SaveTranscriptCommand.CanExecute(null));
    }

    [Fact]
    public void Save_CanExecute_TrueWhenStoppedAndNonEmpty()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "x", DateTimeOffset.Now));

        Assert.False(vm.IsRunning);
        Assert.True(vm.SaveTranscriptCommand.CanExecute(null));
    }

    [Fact]
    public void Save_CanExecute_FalseWhileRunning()
    {
        var (vm, service) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "x", DateTimeOffset.Now));

        service.RaiseState(MeetingAttendeeState.Attending);

        Assert.False(vm.SaveTranscriptCommand.CanExecute(null));
    }

    [Fact]
    public void BuildMarkdown_ContainsTitleAndUtterance()
    {
        var (vm, _) = CreateSut();
        var t0 = new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero).ToLocalTime();

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", t0));

        var md = vm.BuildMarkdown();

        Assert.Contains("# MeetingAttendee_Title", md);
        Assert.Contains("agenda item one", md);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service) CreateSut()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());

        // Echo the key back as its own value so status assertions can match by key without a real resx.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());

        var files = Substitute.For<IFileDialogService>();
        var service = new FakeMeetingAttendeeService();

        var vm = new MeetingAttendeeViewModel(
            service, settingsService, loc, files,
            NullLogger<MeetingAttendeeViewModel>.Instance);

        return (vm, service);
    }

    internal sealed class FakeMeetingAttendeeService : IMeetingAttendeeService
    {
        private readonly Channel<TranscriptUtterance> _channel =
            Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        public MeetingAttendeeState State { get; private set; } = MeetingAttendeeState.Idle;
        public event EventHandler<MeetingAttendeeState>? StateChanged;
        public ChannelReader<TranscriptUtterance> Utterances => _channel.Reader;

        public string? LastStartUrl { get; private set; }
        public int StartCount { get; private set; }

        public Task StartAsync(string meetingUrl, CancellationToken cancellationToken = default)
        {
            LastStartUrl = meetingUrl;
            StartCount++;
            State = MeetingAttendeeState.Attending;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = MeetingAttendeeState.Idle;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void RaiseState(MeetingAttendeeState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }
}
