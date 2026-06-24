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

    [Fact]
    public void Utterances_DifferentSpeakerLabelWithinWindow_SplitIntoTwoBubbles()
    {
        // HEADLINE — the migration's core correctness gate. Two distinct diarizer labels inside the
        // 25s window must NOT merge into one bubble (they did under the old Speaker-only merge key).
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 1"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hi there", t0.AddSeconds(3), "Speaker 2"));

        Assert.Equal(2, vm.Bubbles.Count);
        Assert.Equal("Speaker 1", vm.Bubbles[0].SpeakerLabel);
        Assert.Equal("Speaker 2", vm.Bubbles[1].SpeakerLabel);
        // Distinct labels get distinct palette slots.
        Assert.NotEqual(vm.Bubbles[0].ColorIndex, vm.Bubbles[1].ColorIndex);
    }

    [Fact]
    public void Utterances_SameSpeakerLabelWithinWindow_Merge()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 1"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", t0.AddSeconds(3), "Speaker 1"));

        Assert.Single(vm.Bubbles);
        Assert.Equal("hello world", vm.Bubbles[0].Text);
    }

    [Fact]
    public void Utterances_NullLabelSegmentMidRun_SplitsTheColoredRun()
    {
        // Fragmentation-shape regression (risk #4): a sub-threshold null-label segment arriving mid-run
        // splits the colored speaker's run — null only merges with null. Pins the shipped SPLIT
        // behavior so a future "absorb-null" change is a deliberate, tested diff.
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "alpha", t0, "Speaker 1"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "uh", t0.AddSeconds(1), null));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "beta", t0.AddSeconds(2), "Speaker 1"));

        Assert.Equal(3, vm.Bubbles.Count);
        Assert.Equal("Speaker 1", vm.Bubbles[0].SpeakerLabel);
        Assert.Null(vm.Bubbles[1].SpeakerLabel);
        Assert.Equal("Speaker 1", vm.Bubbles[2].SpeakerLabel);
        // The null-label bubble lands in slot 0; the two "Speaker 1" bubbles keep its assigned slot.
        Assert.Equal(0, vm.Bubbles[1].ColorIndex);
        Assert.Equal(vm.Bubbles[0].ColorIndex, vm.Bubbles[2].ColorIndex);
    }

    [Fact]
    public void Utterances_NullLabelSameSpeaker_StillMerge()
    {
        // Existing null-label merge behavior must hold: two null-label Them utterances in-window merge.
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, null));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", t0.AddSeconds(5), null));

        Assert.Single(vm.Bubbles);
        Assert.Equal("hello world", vm.Bubbles[0].Text);
        Assert.Equal(0, vm.Bubbles[0].ColorIndex);
    }

    // ---- Rename speaker (in-session) -------------------------------------------------------------

    [Fact]
    public async Task Rename_RelabelsExistingInWindowBubbles_AndCallsService()
    {
        var (vm, service, dialog) = CreateSutWithDialog();
        var t0 = DateTimeOffset.Now;
        dialog.ShowInputDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("Marco");

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 2"));

        await vm.RenameSpeakerLabelCommand.ExecuteAsync("Speaker 2");

        Assert.Equal(("Speaker 2", "Marco"), service.LastRename);
        Assert.Equal("Marco", vm.Bubbles[0].SpeakerLabel);
    }

    [Fact]
    public async Task Rename_CarriesPaletteSlotOver_ToNewLabel()
    {
        // Black-box carry-over check: relabeling alone does NOT change ColorIndex (it's set at creation),
        // so asserting the existing bubble's index proves nothing. Instead, after the rename add a NEW
        // utterance under the new label (beyond the 25s window → fresh bubble) and assert it reuses the
        // renamed speaker's slot. Without the _speakerColorIndex re-key, "Marco" grabs the next free slot.
        // Two distinct speakers up front ensure the renamed one isn't slot 0 (so a no-carry path differs).
        var (vm, _, dialog) = CreateSutWithDialog();
        var t0 = DateTimeOffset.Now;
        dialog.ShowInputDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("Marco");

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "one", t0, "Speaker 1"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "two", t0.AddSeconds(3), "Speaker 2"));
        var renamedSlot = vm.Bubbles[1].ColorIndex;   // "Speaker 2"'s slot

        await vm.RenameSpeakerLabelCommand.ExecuteAsync("Speaker 2");

        // Fresh bubble (beyond window) under the new label must reuse the carried slot.
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "three", t0.AddSeconds(40), "Marco"));

        var newBubble = vm.Bubbles[^1];
        Assert.Equal("Marco", newBubble.SpeakerLabel);
        Assert.Equal(renamedSlot, newBubble.ColorIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_CannotExecute_OnNullOrBlankLabel(string? oldLabel)
    {
        var (vm, _) = CreateSut();

        Assert.False(vm.RenameSpeakerLabelCommand.CanExecute(oldLabel));
    }

    [Fact]
    public void Rename_CanExecute_OnNonBlankLabel()
    {
        var (vm, _) = CreateSut();

        Assert.True(vm.RenameSpeakerLabelCommand.CanExecute("Speaker 2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_NoOp_WhenDialogReturnsNullOrBlank(string? dialogResult)
    {
        var (vm, service, dialog) = CreateSutWithDialog();
        var t0 = DateTimeOffset.Now;
        dialog.ShowInputDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(dialogResult);

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 2"));

        await vm.RenameSpeakerLabelCommand.ExecuteAsync("Speaker 2");

        Assert.Equal(0, service.RenameCount);
        Assert.Equal("Speaker 2", vm.Bubbles[0].SpeakerLabel);
    }

    [Fact]
    public async Task Rename_NoOp_WhenNameUnchanged()
    {
        var (vm, service, dialog) = CreateSutWithDialog();
        var t0 = DateTimeOffset.Now;
        dialog.ShowInputDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("Speaker 2");

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 2"));

        await vm.RenameSpeakerLabelCommand.ExecuteAsync("Speaker 2");

        Assert.Equal(0, service.RenameCount);
        Assert.Equal("Speaker 2", vm.Bubbles[0].SpeakerLabel);
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

    [Fact]
    public void BuildMarkdown_DistinctSpeakerLabels_RenderDistinctHeadings()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "first point", t0, "Speaker 1"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "second point", t0.AddSeconds(3), "Speaker 2"));

        var md = vm.BuildMarkdown();

        Assert.Contains("**Speaker 1**", md);
        Assert.Contains("**Speaker 2**", md);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service) CreateSut()
    {
        var (vm, service, _) = CreateSutWithDialog();
        return (vm, service);
    }

    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service, IDialogService dialog) CreateSutWithDialog()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());

        // Echo the key back as its own value so status assertions can match by key without a real resx.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());

        var files = Substitute.For<IFileDialogService>();
        var dialog = Substitute.For<IDialogService>();
        var service = new FakeMeetingAttendeeService();

        var vm = new MeetingAttendeeViewModel(
            service, settingsService, loc, files, dialog,
            NullLogger<MeetingAttendeeViewModel>.Instance);

        return (vm, service, dialog);
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

        public (string Old, string New)? LastRename { get; private set; }
        public int RenameCount { get; private set; }

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

        public void RenameSpeaker(string oldLabel, string newLabel)
        {
            LastRename = (oldLabel, newLabel);
            RenameCount++;
        }

        public void RaiseState(MeetingAttendeeState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }
}
