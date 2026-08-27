using System.Threading.Channels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.Tests.Services.LiveTranscription;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="MeetingAttendeeViewModel"/> logic with a faked
/// <see cref="IMeetingAttendeeService"/>: URL validation + consent gating of the Join command,
/// utterance→bubble mapping (ported behaviour), and state→status mapping. Bubble mapping is driven
/// through the internal <c>AddUtterance</c> seam and status through a raised <c>StateChanged</c> so the
/// tests are deterministic and never spin the background reader. Determinism comes from the injected
/// <c>InlineUiDispatcher</c>, not from the absence of a WPF <c>Application</c>: once
/// <c>AssistantViewParseTests</c> creates one (same batch), <c>Application.Current</c> is non-null for
/// the rest of the process, and the old null-static fallback that used to make <c>DispatchToUi</c> run
/// inline is gone. 31 of the 48 methods here (about 46 of the 67 xunit cases) have an assertion that
/// flips if a dispatched action is deferred; 5 more reach the seam but would pass either way.
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

    // ---- Display name (pre-fill + persist) -------------------------------------------------------

    [Fact]
    public async Task PrepareForDisplay_PrefillsPersistedName_WhenSet()
    {
        var (vm, _, _) = CreateSutFull(new AppSettings { MeetingAttendeeDisplayName = "Conference bot" });

        await vm.PrepareForDisplayAsync();

        Assert.Equal("Conference bot", vm.AssistantDisplayName);
    }

    [Theory]
    [InlineData("Alex", "Alex's assistant")]
    [InlineData(null, "Pia's assistant")]
    public async Task PrepareForDisplay_PrefillsBuiltDefault_WhenNoPersistedName(string? user, string expected)
    {
        var (vm, _, _) = CreateSutFull(new AppSettings { SyncUserDisplayName = user });

        await vm.PrepareForDisplayAsync();

        Assert.Equal(expected, vm.AssistantDisplayName);
    }

    [Theory]
    [InlineData("  Conference bot  ", "Conference bot")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public async Task Start_PersistsTrimmedDisplayName_BlankBecomesNull(string input, string? expected)
    {
        var (vm, _, settingsService) = CreateSutFull(new AppSettings());
        vm.MeetingUrl = ValidUrl;
        vm.ConsentAcknowledged = true;
        vm.AssistantDisplayName = input;

        await vm.StartCommand.ExecuteAsync(null);

        await settingsService.Received().SaveSettingsAsync(
            Arg.Is<AppSettings>(s => s.MeetingAttendeeDisplayName == expected));

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
    public void Utterances_NullLabelSegmentMidRun_InheritsTheRunsLabel()
    {
        // Too short to diarize ("uh") → no label → would render as the generic "meeting" placeholder
        // and cut the colored run in three. It inherits the run's label and merges instead.
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "alpha", t0, "Speaker 1"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "uh", t0.AddSeconds(1), null));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "beta", t0.AddSeconds(2), "Speaker 1"));

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 1", bubble.SpeakerLabel);
        Assert.Equal("alpha uh beta", bubble.Text);
    }

    [Fact]
    public void Utterances_NullLabelAtRunStart_KeepsTheGenericPlaceholder()
    {
        // Nothing labeled to inherit from — the segment stays honestly unlabeled, and the following
        // diarized utterance opens its own colored run.
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "uh", t0, null));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "alpha", t0.AddSeconds(1), "Speaker 1"));

        Assert.Equal(2, vm.Bubbles.Count);
        Assert.Null(vm.Bubbles[0].SpeakerLabel);
        Assert.Equal(0, vm.Bubbles[0].ColorIndex);
        Assert.Equal("Speaker 1", vm.Bubbles[1].SpeakerLabel);
    }

    [Fact]
    public void Utterances_InheritanceIsReDerived_OnAReassignmentRebuild()
    {
        // The journal keeps the truthful null, so a retro-correction that moves the neighbouring
        // labels re-derives which run the interjection belongs to.
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "alpha", t0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "uh", t0.AddSeconds(1), null));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "beta", t0.AddSeconds(2), "Speaker 2", SegmentId: 1));
        Assert.Equal(2, vm.Bubbles.Count);
        Assert.Equal("alpha uh", vm.Bubbles[0].Text);

        vm.ApplyReassignments(new[] { new SpeakerReassignment(0, "Speaker 2") });

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 2", bubble.SpeakerLabel);
        Assert.Equal("alpha uh beta", bubble.Text);
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

    // ---- ApplyReassignments (adaptive retro rebuild) ---------------------------------------------

    [Fact]
    public void ApplyReassignments_MergesBubbles_WhenTwoLabelsCollapse()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", t0.AddSeconds(5), "Speaker 2", SegmentId: 1));
        Assert.Equal(2, vm.Bubbles.Count);

        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 1") });

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 1", bubble.SpeakerLabel);
        Assert.Contains("hello", bubble.Text);
        Assert.Contains("world", bubble.Text);
    }

    [Fact]
    public void ApplyReassignments_SplitsABubble_WhenOneUtteranceMovesAway()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", t0.AddSeconds(5), "Speaker 1", SegmentId: 1));
        Assert.Single(vm.Bubbles);

        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 2") });

        Assert.Equal(2, vm.Bubbles.Count);
        Assert.Equal("Speaker 1", vm.Bubbles[0].SpeakerLabel);
        Assert.Equal("Speaker 2", vm.Bubbles[1].SpeakerLabel);
    }

    [Fact]
    public void ApplyReassignments_UnknownOrUnchangedSegments_LeaveBubblesUntouched()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 1", SegmentId: 0));
        var before = Assert.Single(vm.Bubbles);

        vm.ApplyReassignments(new[]
        {
            new SpeakerReassignment(0, "Speaker 1"),   // unchanged
            new SpeakerReassignment(99, "Speaker 3"),  // unknown id
        });

        Assert.Same(before, Assert.Single(vm.Bubbles)); // no rebuild happened
    }

    [Fact]
    public void ApplyReassignments_AfterRename_KeepsTheRenamedLabel()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", t0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "later", t0.AddSeconds(60), "Speaker 2", SegmentId: 1));

        vm.RelabelSpeakerForTest("Speaker 1", "Alice");

        // An unrelated reassignment triggers a rebuild — the rename must survive it.
        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 3") });

        Assert.Equal("Alice", vm.Bubbles[0].SpeakerLabel);
        Assert.Equal("Speaker 3", vm.Bubbles[1].SpeakerLabel);
    }

    [Fact]
    public void ApplyReassignments_ColorStaysWithTheSpeaker_AcrossRebuild()
    {
        var (vm, _) = CreateSut();
        var t0 = DateTimeOffset.Now;
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "a", t0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "b", t0.AddSeconds(60), "Speaker 2", SegmentId: 1));
        var color1 = vm.Bubbles[0].ColorIndex;
        var color2 = vm.Bubbles[1].ColorIndex;

        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 1") });
        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 2") }); // move it back

        Assert.Equal(color1, vm.Bubbles[0].ColorIndex);
        Assert.Equal(color2, vm.Bubbles[1].ColorIndex);
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

    // ---- Busy indicator (joining / leaving) ------------------------------------------------------

    [Theory]
    [InlineData(MeetingAttendeeState.Idle, false)]
    [InlineData(MeetingAttendeeState.ProvisioningBrowser, true)]
    [InlineData(MeetingAttendeeState.Joining, true)]
    [InlineData(MeetingAttendeeState.InLobby, true)]
    [InlineData(MeetingAttendeeState.Attending, false)]   // steady "transcribing" — not busy
    [InlineData(MeetingAttendeeState.Stopping, true)]
    [InlineData(MeetingAttendeeState.Error, false)]
    public void StateChanged_MapsToIsBusy(MeetingAttendeeState state, bool expectedBusy)
    {
        var (vm, service) = CreateSut();

        service.RaiseState(state);

        Assert.Equal(expectedBusy, vm.IsBusy);
    }

    // ---- Join setup visibility (post-meeting form removal) ---------------------------------------

    [Fact]
    public void IsJoinSetupVisible_TrueInitially()
    {
        var (vm, _) = CreateSut();

        Assert.True(vm.IsJoinSetupVisible);
    }

    [Fact]
    public void IsJoinSetupVisible_FalseWhileRunning()
    {
        var (vm, service) = CreateSut();

        service.RaiseState(MeetingAttendeeState.Joining);

        Assert.False(vm.IsJoinSetupVisible);
    }

    [Fact]
    public void IsJoinSetupVisible_FalseAfterAttendedThenLeft()
    {
        // Headline for requirement #4: after a meeting has been attended and left, the post-meeting page
        // shows the transcript alone — the join form must NOT reappear.
        var (vm, service) = CreateSut();

        service.RaiseState(MeetingAttendeeState.Attending);
        service.RaiseState(MeetingAttendeeState.Idle);

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsJoinSetupVisible);
    }

    [Fact]
    public void IsJoinSetupVisible_TrueAfterErrorBeforeAttending()
    {
        // A join that fails before admission keeps the form available for a retry.
        var (vm, service) = CreateSut();

        service.RaiseState(MeetingAttendeeState.Joining);
        service.RaiseState(MeetingAttendeeState.Error);

        Assert.True(vm.IsJoinSetupVisible);
    }

    [Fact]
    public async Task PrepareForDisplay_ResetsJoinSetupVisible_AfterPriorMeeting()
    {
        var (vm, service, _) = CreateSutFull(new AppSettings());
        service.RaiseState(MeetingAttendeeState.Attending);
        service.RaiseState(MeetingAttendeeState.Idle);
        Assert.False(vm.IsJoinSetupVisible);

        // Re-opening the overlay starts fresh: the join form is shown again.
        await vm.PrepareForDisplayAsync();

        Assert.True(vm.IsJoinSetupVisible);
    }

    [Fact]
    public async Task PrepareForDisplay_ClearsPriorTranscript_OnFreshOpen()
    {
        // A re-open after a prior meeting must not render the join form above a stale transcript: the
        // fresh open discards the previous bubbles, so the form sits alone with Save/Summarize disabled.
        var (vm, service, _) = CreateSutFull(new AppSettings());
        service.RaiseState(MeetingAttendeeState.Attending);
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "old meeting", DateTimeOffset.Now));
        service.RaiseState(MeetingAttendeeState.Idle);
        Assert.NotEmpty(vm.Bubbles);

        await vm.PrepareForDisplayAsync();

        Assert.Empty(vm.Bubbles);
        Assert.True(vm.IsJoinSetupVisible);
        Assert.False(vm.SaveTranscriptCommand.CanExecute(null));
        Assert.False(vm.SummarizeWithAssistantCommand.CanExecute(null));
    }

    // ---- Summarize with assistant ----------------------------------------------------------------

    [Fact]
    public void Summarize_CanExecute_FalseWhenEmpty()
    {
        var (vm, _) = CreateSut();

        Assert.False(vm.SummarizeWithAssistantCommand.CanExecute(null));
    }

    [Fact]
    public void Summarize_CanExecute_TrueWhenStoppedAndNonEmpty()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "x", DateTimeOffset.Now));

        Assert.False(vm.IsRunning);
        Assert.True(vm.SummarizeWithAssistantCommand.CanExecute(null));
    }

    [Fact]
    public void Summarize_CanExecute_FalseWhileRunning()
    {
        var (vm, service) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "x", DateTimeOffset.Now));

        service.RaiseState(MeetingAttendeeState.Attending);

        Assert.False(vm.SummarizeWithAssistantCommand.CanExecute(null));
    }

    [Fact]
    public void Summarize_RaisesSummarizeRequested_WithTranscriptInPrompt()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", DateTimeOffset.Now));

        string? captured = null;
        vm.SummarizeRequested += (_, prompt) => captured = prompt;

        vm.SummarizeWithAssistantCommand.Execute(null);

        Assert.NotNull(captured);
        // The prompt embeds the transcript Markdown (title heading + the utterance text).
        Assert.Contains("agenda item one", captured);
        Assert.Contains("MeetingAttendee_Title", captured);
    }

    [Fact]
    public void Summarize_IncludesObservedAttendees_InPrompt()
    {
        // The roster the service accumulated is injected into the summary prompt (after a localized
        // lead-in) so the assistant can attribute the diarized speakers to real names.
        var (vm, service) = CreateSut();
        service.ObservedAttendees = new[] { "Marco Altmann", "Jane Doe" };
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", DateTimeOffset.Now));

        string? captured = null;
        vm.SummarizeRequested += (_, prompt) => captured = prompt;

        vm.SummarizeWithAssistantCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Contains("Marco Altmann", captured);
        Assert.Contains("Jane Doe", captured);
        // The localized lead-in key is echoed back by the fake localization service.
        Assert.Contains("MeetingAttendee_SummaryPrompt_Attendees", captured);
    }

    [Fact]
    public void Summarize_OmitsAttendeesSection_WhenNoneObserved()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", DateTimeOffset.Now));

        string? captured = null;
        vm.SummarizeRequested += (_, prompt) => captured = prompt;

        vm.SummarizeWithAssistantCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.DoesNotContain("MeetingAttendee_SummaryPrompt_Attendees", captured);
    }

    [Fact]
    public void Summarize_NoOp_WhenEmpty()
    {
        var (vm, _) = CreateSut();
        var fired = false;
        vm.SummarizeRequested += (_, _) => fired = true;

        vm.SummarizeWithAssistantCommand.Execute(null);

        Assert.False(fired);
    }

    // ---- Open meeting settings -------------------------------------------------------------------

    [Fact]
    public void OpenMeetingSettings_RaisesOpenSettingsRequested()
    {
        var (vm, _) = CreateSut();
        var fired = false;
        vm.OpenSettingsRequested += (_, _) => fired = true;

        vm.OpenMeetingSettingsCommand.Execute(null);

        Assert.True(fired);
    }

    // ---- save to vault ----------------------------------------------------------------------------

    [Fact]
    public async Task SaveToVault_WhenTheDetailsDialogIsCancelled_WritesNothing()
    {
        var (vm, _, dialog, memory, ingest) = CreateSutWithVault();
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>()).Returns(false);
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", DateTimeOffset.Now));

        // Non-vacuity: the command must actually have been runnable, or "nothing written" proves nothing.
        Assert.True(vm.SaveToVaultCommand.CanExecute(null));
        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        await memory.DidNotReceive().CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>());
        await ingest.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveToVault_WritesASourceUnderSources_ThenIngestsThatSameRef()
    {
        var (vm, service, dialog, memory, ingest) = CreateSutWithVault();
        service.ObservedAttendees = new[] { "Marco Altmann", "Jane Doe" };
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>())
            .Returns(ci => { ci.Arg<MeetingSaveEditModel>().Title = "Q3 roadmap sync"; return Task.FromResult(true); });
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", DateTimeOffset.Now));

        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        var call = Assert.Single(
            memory.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IMemoryService.CreateSourceAsync));
        var reference = (string)call.GetArguments()[0]!;
        var markdown = (string)call.GetArguments()[1]!;

        Assert.StartsWith("sources/transcripts/meeting-", reference, StringComparison.Ordinal);
        Assert.EndsWith("-q3-roadmap-sync.md", reference, StringComparison.Ordinal);
        Assert.Contains("schema: pia-meeting/v1", markdown, StringComparison.Ordinal);
        Assert.Contains("title: Q3 roadmap sync", markdown, StringComparison.Ordinal);
        Assert.Contains("source: teams", markdown, StringComparison.Ordinal);
        Assert.Contains("attendees: [Marco Altmann, Jane Doe]", markdown, StringComparison.Ordinal);
        Assert.Contains("agenda item one", markdown, StringComparison.Ordinal);

        await ingest.Received(1).RunAsync(reference, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveToVault_WhenTheRefIsTaken_FallsBackToTheNextFreeName()
    {
        var (vm, _, dialog, memory, _) = CreateSutWithVault();
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>())
            .Returns(ci => { ci.Arg<MeetingSaveEditModel>().Title = "Standup"; return Task.FromResult(true); });
        memory.ResolveCreateSourceAsync(Arg.Any<string>()).Returns(ci =>
        {
            var reference = ci.Arg<string>();
            var free = !reference.EndsWith("-standup.md", StringComparison.Ordinal);
            return Task.FromResult(new SourceCreatePreview(free, reference, free ? null : "taken"));
        });
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "morning", DateTimeOffset.Now));

        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        await memory.Received(1).CreateSourceAsync(
            Arg.Is<string>(r => r.EndsWith("-standup-2.md", StringComparison.Ordinal)), Arg.Any<string>());
    }

    [Fact]
    public async Task SaveToVault_WhenTheConfirmedTitleIsBlank_WritesNothing()
    {
        // The dialog's disabled primary button is a binding, and a broken binding leaves it enabled.
        var (vm, _, dialog, memory, _) = CreateSutWithVault();
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>())
            .Returns(ci => { ci.Arg<MeetingSaveEditModel>().Title = "   "; return Task.FromResult(true); });
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "morning", DateTimeOffset.Now));

        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        await memory.DidNotReceive().CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SaveToVault_WhenTheVaultRefusesThePath_ReportsThatReasonRatherThanACollision()
    {
        // A refusal no name suffix can fix must not be reported as "that name is taken".
        var (vm, _, dialog, memory, ingest) = CreateSutWithVault();
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>()).Returns(true);
        memory.ResolveCreateSourceAsync(Arg.Any<string>()).Returns(ci =>
            Task.FromResult(new SourceCreatePreview(false, ci.Arg<string>(), "Error: reference is outside the memory vault.")));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "morning", DateTimeOffset.Now));

        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        await dialog.Received(1).ShowMessageDialogAsync(
            Arg.Any<string>(), Arg.Is<string>(m => m.Contains("outside the memory vault", StringComparison.Ordinal)));
        await memory.DidNotReceive().CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>());
        await ingest.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveToVault_WhenTheWriteFails_ReportsTheErrorAndDoesNotIngest()
    {
        var (vm, _, dialog, memory, ingest) = CreateSutWithVault();
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>()).Returns(true);
        memory.CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(new SourceWrite(false, "sources/x.md", "Error: nope.")));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "morning", DateTimeOffset.Now));

        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        await dialog.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
        await ingest.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveToVault_PrefillsAttendeesFromTheObservedRoster()
    {
        var (vm, service, dialog, _, _) = CreateSutWithVault();
        service.ObservedAttendees = new[] { "Marco Altmann", "Jane Doe" };
        MeetingSaveEditModel? seen = null;
        dialog.ShowMeetingSaveDialogAsync(Arg.Any<MeetingSaveEditModel>())
            .Returns(ci => { seen = ci.Arg<MeetingSaveEditModel>(); return Task.FromResult(false); });
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "agenda item one", DateTimeOffset.Now));

        await ((IAsyncRelayCommand)vm.SaveToVaultCommand).ExecuteAsync(null);

        Assert.NotNull(seen);
        Assert.Equal("Marco Altmann, Jane Doe", seen!.Attendees);
    }

    [Fact]
    public void SaveToVault_IsDisabled_WhileRunning_AndWithNoTranscript()
    {
        var (vm, service) = CreateSut();
        Assert.False(vm.SaveToVaultCommand.CanExecute(null));

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", DateTimeOffset.Now));
        Assert.True(vm.SaveToVaultCommand.CanExecute(null));

        service.RaiseState(MeetingAttendeeState.Attending);
        Assert.False(vm.SaveToVaultCommand.CanExecute(null));
    }

    // ---- service + VM pair: the label invariant ---------------------------------------------------

    /// <summary>
    /// No bubble may carry a speaker label that is absent from the diarizer's live label set.
    ///
    /// <para>Each side is separately correct, which is why the suite could stay green while the pair
    /// produced 11 labels for 4 clusters. The pair breaks because a re-cluster pass runs INSIDE the
    /// identify call for the segment that triggered it, so its correction is emitted before that
    /// segment's utterance — which only arrives once transcription finishes, seconds later. Order
    /// below is the production order: identify, then the reassignment event, then the utterance.</para>
    /// </summary>
    [Fact]
    public void Bubbles_NeverCarryALabelTheDiarizerHasDropped()
    {
        var (vm, _) = CreateSut();
        // Five voices 72° apart (cos 72° ≈ 0.31, under every threshold in the band) so each mints its
        // own label, then a scripted pass that merges the fifth voice into the first while leaving
        // every other cluster matched — so its label is dropped outright rather than recycled.
        var clusterer = new RecordingClusterer();
        clusterer.Scripted.Enqueue(new ClusterResult([0, 1, 2, 3, 4, 0], 5, 0.5f));
        clusterer.Scripted.Enqueue(new ClusterResult([0, 1, 2, 3, 0, 0, 1, 2, 3, 0, 0], 4, 0.5f));
        using var svc = new AdaptiveSpeakerIdentificationService(
            new DegreeEmbeddingExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance,
            now: null, AdaptiveSpeakerIdentificationService.DefaultMaxJournaledSegments, clusterer);
        svc.SpeakersReassigned += (_, changes) => vm.ApplyReassignments(changes);

        var t = new DateTimeOffset(2026, 8, 21, 14, 0, 0, TimeSpan.Zero);
        foreach (var degrees in new double[] { 0, 72, 144, 216, 288, 0, 72, 144, 216, 0, 288 })
        {
            var seg = svc.IdentifyOrRegisterSegment(SpeakerSegments.Seg(degrees), 16000);
            vm.AddUtterance(new TranscriptUtterance(
                TranscriptSpeaker.Them, $"{degrees}", t, seg.Label, seg.SegmentId));
            t = t.AddSeconds(30);       // outside the bubble window, so every utterance is its own bubble
        }

        var known = svc.KnownLabels;
        var stale = vm.Bubbles
            .Select(b => b.SpeakerLabel)
            .Where(label => label is not null && !known.Contains(label))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(stale);
    }

    [Fact]
    public void Reassignment_ForASegmentNotYetSeen_IsAppliedWhenItsUtteranceArrives()
    {
        var (vm, _) = CreateSut();

        // Production order: the pass corrects the segment it is running inside, seconds before that
        // segment finishes transcribing and becomes an utterance.
        vm.ApplyReassignments([new SpeakerReassignment(7, "Speaker 2")]);
        Utter(vm, "Speaker 1", "a", 0, segmentId: 7);

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 2", bubble.SpeakerLabel);
    }

    [Fact]
    public void Reassignment_ClearingALabel_LeavesTheBubbleUnlabelled()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 3", "a", 0, segmentId: 1);

        // A pass that drops the cluster a segment pointed at says so with no label at all, rather
        // than confidently moving it onto a different voice.
        vm.ApplyReassignments([new SpeakerReassignment(1, null)]);

        Assert.Null(Assert.Single(vm.Bubbles).SpeakerLabel);
    }

    // ---- display renumbering ----------------------------------------------------------------------

    [Fact]
    public void DisplayLabel_NumbersSpeakersInFirstAppearanceOrder()
    {
        var (vm, _) = CreateSut();

        Utter(vm, "Speaker 4", "a", 0);
        Utter(vm, "Speaker 1", "b", 30);
        Utter(vm, "Speaker 4", "c", 60);

        Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], vm.Bubbles.Select(b => b.DisplayLabel));
        // Identity is untouched: it keys the palette, the consent map and rename.
        Assert.Equal(["Speaker 4", "Speaker 1", "Speaker 4"], vm.Bubbles.Select(b => b.SpeakerLabel));
    }

    [Fact]
    public void DisplayLabel_ClosesTheGapsAMintCounterLeaves()
    {
        var (vm, _) = CreateSut();

        Utter(vm, "Speaker 1", "a", 0);
        Utter(vm, "Speaker 2", "b", 30);
        Utter(vm, "Speaker 17", "c", 60);

        Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 3"], vm.Bubbles.Select(b => b.DisplayLabel));
    }

    [Fact]
    public void DisplayLabel_SurvivesARebuildIdentically()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 9", "a", 0, segmentId: 1);
        Utter(vm, "Speaker 3", "b", 30, segmentId: 2);
        var before = vm.Bubbles.Select(b => b.DisplayLabel).ToArray();

        // A reassignment that changes nothing still has to leave the numbering where it was.
        vm.ApplyReassignments([new SpeakerReassignment(2, "Speaker 4")]);

        Assert.Equal(before, vm.Bubbles.Select(b => b.DisplayLabel));
        Assert.Equal(["Speaker 9", "Speaker 4"], vm.Bubbles.Select(b => b.SpeakerLabel));
    }

    /// <summary>
    /// Segment ids stay monotonic across the diarizer's Reset while its labels restart at "Speaker 1",
    /// so anything the ViewModel keeps keyed by segment id or by label has to go at meeting start —
    /// otherwise the next meeting's first speaker inherits the last one's number, or worse, its label.
    /// </summary>
    [Fact]
    public async Task StartingASecondMeeting_CarriesNoNumberingOrParkedCorrectionOver()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 4", "a", 0, segmentId: 10);
        Utter(vm, "Speaker 9", "b", 30, segmentId: 11);
        vm.ApplyReassignments([new SpeakerReassignment(99, "Speaker 9")]);   // parked, never claimed

        vm.MeetingUrl = ValidUrl;
        vm.ConsentAcknowledged = true;
        await vm.StartCommand.ExecuteAsync(null);

        Utter(vm, "Speaker 1", "c", 0, segmentId: 99);

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 1", bubble.SpeakerLabel);   // the stale parked correction did not apply
        Assert.Equal("Speaker 1", bubble.DisplayLabel);   // numbering restarted at 1
    }

    [Fact]
    public void DisplayLabel_LeavesARenamedSpeakerAlone()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 7", "a", 0);

        vm.RelabelSpeakerForTest("Speaker 7", "Andreas");

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Andreas", bubble.SpeakerLabel);
        Assert.Equal("Andreas", bubble.DisplayLabel);
    }

    [Fact]
    public void BuildMarkdown_EmitsDisplayLabels()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 12", "hello", 0);

        var markdown = vm.BuildMarkdown();

        Assert.Contains("**Speaker 1**", markdown);
        Assert.DoesNotContain("Speaker 12", markdown);
    }

    // ---- unlabelled transcript -------------------------------------------------------------------

    [Fact]
    public void SuppressSpeakerLabels_LeavesEveryBubbleUnlabelled()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 4", "a", 0);
        Utter(vm, "Speaker 1", "b", 30);

        vm.SuppressSpeakerLabels = true;

        Assert.All(vm.Bubbles, b => Assert.Null(b.DisplayLabel));
        // Identity survives, so rename, the palette and the consent map still work.
        Assert.Equal(["Speaker 4", "Speaker 1"], vm.Bubbles.Select(b => b.SpeakerLabel));
    }

    [Fact]
    public void SuppressSpeakerLabels_LeavesANewBubbleUnlabelled_WhenAlreadyOnBeforeItArrives()
    {
        // The production order: the setting is read when the reader starts, so every bubble in a real
        // session is created with suppression already on, not toggled on afterwards.
        var (vm, _) = CreateSut();
        vm.SuppressSpeakerLabels = true;

        Utter(vm, "Speaker 4", "a", 0);

        Assert.Null(Assert.Single(vm.Bubbles).DisplayLabel);
    }

    [Fact]
    public void SuppressSpeakerLabels_HidesARenamedSpeakerToo()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 7", "a", 0);
        vm.RelabelSpeakerForTest("Speaker 7", "Andreas");

        vm.SuppressSpeakerLabels = true;

        // A rename names a cluster the diarizer built, so it is no more verified than "Speaker 1".
        Assert.Null(Assert.Single(vm.Bubbles).DisplayLabel);
    }

    [Fact]
    public void SuppressSpeakerLabels_RestoresTheSameNumbering_WhenSwitchedBackOff()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 4", "a", 0);
        Utter(vm, "Speaker 1", "b", 30);
        var before = vm.Bubbles.Select(b => b.DisplayLabel).ToArray();

        vm.SuppressSpeakerLabels = true;
        vm.SuppressSpeakerLabels = false;

        Assert.Equal(before, vm.Bubbles.Select(b => b.DisplayLabel));
    }

    [Fact]
    public void BuildMarkdown_CarriesNoSpeakerLabel_WhenSuppressed()
    {
        var (vm, _) = CreateSut();
        Utter(vm, "Speaker 12", "hello", 0);

        vm.SuppressSpeakerLabels = true;
        var markdown = vm.BuildMarkdown();

        Assert.DoesNotContain("Speaker 1", markdown);
        Assert.DoesNotContain("Speaker 12", markdown);
        Assert.Contains("hello", markdown);
    }

    private static void Utter(
        MeetingAttendeeViewModel vm, string? label, string text, int atSeconds, long? segmentId = null)
        => vm.AddUtterance(new TranscriptUtterance(
            TranscriptSpeaker.Them, text,
            new DateTimeOffset(2026, 8, 21, 14, 0, 0, TimeSpan.Zero).AddSeconds(atSeconds),
            label, segmentId));

    // ---- helpers ----------------------------------------------------------------------------------

    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service) CreateSut()
    {
        var (vm, service, _) = CreateSutWithDialog();
        return (vm, service);
    }

    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service, IDialogService dialog) CreateSutWithDialog()
    {
        var (vm, service, dialog, _, _) = CreateSutWithVault();
        return (vm, service, dialog);
    }

    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service, IDialogService dialog,
        IMemoryService memory, IIngestScheduler ingest) CreateSutWithVault()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());

        // Echo the key back as its own value so status assertions can match by key without a real resx.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        // Key plus its arguments, so an assertion can see the substituted detail without a real resx.
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.Arg<string>()} {string.Join(" ", ci.ArgAt<object[]>(1))}");

        var files = Substitute.For<IFileDialogService>();
        var dialog = Substitute.For<IDialogService>();
        var memory = Substitute.For<IMemoryService>();
        memory.ResolveCreateSourceAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult(new SourceCreatePreview(true, ci.Arg<string>(), null)));
        memory.CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => Task.FromResult(new SourceWrite(true, ci.ArgAt<string>(0), null)));
        var ingest = Substitute.For<IIngestScheduler>();
        var service = new FakeMeetingAttendeeService();

        var vm = new MeetingAttendeeViewModel(
            service, settingsService, loc, files, dialog, memory, ingest,
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance, new InlineUiDispatcher());

        return (vm, service, dialog, memory, ingest);
    }

    // Variant that exposes the settings substitute and lets the caller seed the AppSettings instance,
    // for display-name pre-fill (PrepareForDisplayAsync) and persist-on-Start assertions.
    private static (MeetingAttendeeViewModel vm, FakeMeetingAttendeeService service, ISettingsService settingsService)
        CreateSutFull(AppSettings settings)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings);

        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());

        var files = Substitute.For<IFileDialogService>();
        var dialog = Substitute.For<IDialogService>();
        var service = new FakeMeetingAttendeeService();

        var vm = new MeetingAttendeeViewModel(
            service, settingsService, loc, files, dialog,
            Substitute.For<IMemoryService>(), Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance, new InlineUiDispatcher());

        return (vm, service, settingsService);
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
        public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }
        public ChannelReader<TranscriptUtterance> Utterances => _channel.Reader;

        public IReadOnlyCollection<string> ObservedAttendees { get; set; } = Array.Empty<string>();

        public string? LastStartUrl { get; private set; }
        public int StartCount { get; private set; }

        public (string Old, string New)? LastRename { get; private set; }
        public int RenameCount { get; private set; }

        public Task StartAsync(
            string meetingUrl,
            CancellationToken cancellationToken = default,
            IProgress<ModelDownloadProgress>? speakerModelProgress = null)
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
