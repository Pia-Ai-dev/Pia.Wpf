using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// The unattended half of meeting capture: nobody clicks Save, so everything the overlay does on a button
/// press has to happen on its own here.
/// </summary>
public sealed class ScheduledMeetingRecorderTests
{
    private const string Url = "https://teams.microsoft.com/l/meetup-join/x";

    private static readonly DateTimeOffset AcknowledgedAt = new(2026, 9, 28, 9, 15, 0, TimeSpan.FromHours(2));

    private readonly IConsentEvidenceStore _evidence = Substitute.For<IConsentEvidenceStore>();
    private readonly ConsentLiveSessions _live = new(NullLogger<ConsentLiveSessions>.Instance);
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    public ScheduledMeetingRecorderTests()
    {
        _localization.CurrentLanguage.Returns(TargetLanguage.DE);
    }

    private static ISettingsService NewSettings(AppSettings? settings = null)
    {
        var service = Substitute.For<ISettingsService>();
        service.GetSettingsAsync().Returns(settings ?? new AppSettings());
        return service;
    }

    private static IMemoryService NewMemory(bool writeSucceeds = true)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ResolveCreateSourceAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult(new SourceCreatePreview(true, (string)ci[0], null)));
        memory.CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => Task.FromResult(new SourceWrite(writeSucceeds, (string)ci[0], writeSucceeds ? null : "disk full")));
        return memory;
    }

    /// <summary>
    /// Stands in for the whole attendee: a channel the test writes utterances into, plus a state it can drive
    /// so "the meeting ended" is something the test decides rather than something it waits for.
    /// </summary>
    private sealed class FakeAttendee : IMeetingAttendeeService
    {
        private readonly Channel<TranscriptUtterance> _channel =
            Channel.CreateUnbounded<TranscriptUtterance>(new UnboundedChannelOptions { SingleReader = true });

        public MeetingAttendeeState State { get; private set; } = MeetingAttendeeState.Idle;
        public event EventHandler<MeetingAttendeeState>? StateChanged;
        public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;
        public ChannelReader<TranscriptUtterance> Utterances => _channel.Reader;
        public IReadOnlyCollection<string> ObservedAttendees { get; set; } = ["Marco Altmann", "Jane Doe"];

        public int StartCount { get; private set; }

        /// <summary>How many leading StartAsync calls throw an admission timeout before one succeeds.</summary>
        public int AdmissionTimeouts { get; set; }

        public Task StartAsync(string meetingUrl, CancellationToken cancellationToken = default,
            IProgress<ModelDownloadProgress>? speakerModelProgress = null)
        {
            StartCount++;
            if (StartCount <= AdmissionTimeouts)
            {
                Transition(MeetingAttendeeState.Error);
                throw new MeetingAdmissionTimeoutException("not admitted");
            }

            Transition(MeetingAttendeeState.Attending);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Transition(MeetingAttendeeState.Idle);
            return Task.CompletedTask;
        }

        public void RenameSpeaker(string oldLabel, string newLabel) { }

        public void Emit(TranscriptUtterance utterance) => _channel.Writer.TryWrite(utterance);

        public void Reassign(params SpeakerReassignment[] changes) =>
            SpeakersReassigned?.Invoke(this, changes);

        public void EndMeeting() => Transition(MeetingAttendeeState.Idle);

        private void Transition(MeetingAttendeeState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    private ScheduledMeetingRecorder NewRecorder(IMemoryService memory, AppSettings? settings = null) =>
        Quickened(new ScheduledMeetingRecorder(
            NewSettings(settings), memory, _evidence, _live, _localization, NullLogger<ScheduledMeetingRecorder>.Instance));

    /// <summary>Both waits exist for a real meeting's pace; a test should not sit out a real minute.</summary>
    private static ScheduledMeetingRecorder Quickened(ScheduledMeetingRecorder recorder)
    {
        recorder.LobbyRetryDelay = TimeSpan.Zero;
        recorder.DrainGrace = TimeSpan.FromMilliseconds(50);
        return recorder;
    }

    private static TranscriptUtterance Utterance(string text, int second, string? label, long segmentId) =>
        new(TranscriptSpeaker.Them, text,
            new DateTimeOffset(2026, 8, 27, 9, 0, second, TimeSpan.Zero), label, segmentId);

    /// <summary>
    /// Emits into the channel and ends the meeting once the recorder has actually joined — the recorder
    /// attaches its collector before joining, so writing earlier would still work, but ending earlier would
    /// race the join it is meant to follow.
    /// </summary>
    private static async Task<MeetingRecordingResult> RunAsync(
        ScheduledMeetingRecorder recorder, FakeAttendee attendee, Action<FakeAttendee> duringMeeting)
    {
        var recording = recorder.RecordAsync(attendee, Url, "Q3 roadmap sync", AcknowledgedAt);

        while (attendee.State != MeetingAttendeeState.Attending && !recording.IsCompleted)
            await Task.Delay(10);

        duringMeeting(attendee);
        attendee.EndMeeting();

        return await recording;
    }

    [Fact]
    public async Task RecordAsync_WithoutSpeakerNaming_SavesNoAttendees()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        await RunAsync(NewRecorder(memory, new AppSettings { MeetingSpeakerNaming = false }), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        var markdown = (string)memory.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMemoryService.CreateSourceAsync))
            .GetArguments()[1]!;
        Assert.DoesNotContain("attendees:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Marco Altmann", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_SavesTheTranscriptUnderTheTranscriptsFolder()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        var result = await RunAsync(NewRecorder(memory), attendee, a =>
        {
            a.Emit(Utterance("agenda item one", 0, "Speaker 1", 1));
            a.Emit(Utterance("agreed", 40, "Speaker 2", 2));
        });

        Assert.Equal(MeetingRecordingOutcome.Saved, result.Outcome);
        Assert.StartsWith("sources/transcripts/meeting-", result.Reference, StringComparison.Ordinal);

        var markdown = (string)memory.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMemoryService.CreateSourceAsync))
            .GetArguments()[1]!;

        Assert.Contains("schema: pia-meeting/v1", markdown, StringComparison.Ordinal);
        Assert.Contains("source: teams", markdown, StringComparison.Ordinal);
        // The roster is what lets a later summary put real names on the diarized labels.
        Assert.Contains("attendees: [Marco Altmann, Jane Doe]", markdown, StringComparison.Ordinal);
        Assert.Contains("agenda item one", markdown, StringComparison.Ordinal);
        Assert.Contains("agreed", markdown, StringComparison.Ordinal);
    }

    // Nobody was there to decide, so the language model does not evaluate the transcript until the user asks.
    [Fact]
    public async Task RecordAsync_LeavesTheEvaluationToTheUser()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        await RunAsync(NewRecorder(memory), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        var markdown = (string)memory.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMemoryService.CreateSourceAsync))
            .GetArguments()[1]!;
        Assert.Contains("\ningest: manual\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_AppliesRetroactiveSpeakerCorrections()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        await RunAsync(NewRecorder(memory), attendee, a =>
        {
            a.Emit(Utterance("first", 0, "Speaker 1", 1));
            a.Emit(Utterance("second", 40, "Speaker 7", 2));
            // The adaptive diarizer decides after the fact that both turns were one voice.
            a.Reassign(new SpeakerReassignment(2, "Speaker 1"));
        });

        var markdown = (string)memory.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMemoryService.CreateSourceAsync))
            .GetArguments()[1]!;

        // Renumbering runs over the CORRECTED labels, so the stale mint counter never reaches the file.
        Assert.DoesNotContain("Speaker 7", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker 2", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_RetriesOnceAfterAnAdmissionTimeout()
    {
        var attendee = new FakeAttendee { AdmissionTimeouts = 1 };
        var memory = NewMemory();

        var result = await RunAsync(NewRecorder(memory), attendee,
            a => a.Emit(Utterance("made it", 0, "Speaker 1", 1)));

        Assert.Equal(2, attendee.StartCount);
        Assert.Equal(MeetingRecordingOutcome.Saved, result.Outcome);
    }

    [Fact]
    public async Task RecordAsync_GivesUpAfterASecondAdmissionTimeout()
    {
        var attendee = new FakeAttendee { AdmissionTimeouts = 2 };

        var result = await NewRecorder(NewMemory()).RecordAsync(
            attendee, Url, "Standup", AcknowledgedAt, TestContext.Current.CancellationToken);

        Assert.Equal(2, attendee.StartCount);
        Assert.Equal(MeetingRecordingOutcome.JoinFailed, result.Outcome);
    }

    [Fact]
    public async Task RecordAsync_DoesNotRetryAJoinFailureThatIsNotALobbyTimeout()
    {
        var attendee = new ThrowingAttendee();

        var result = await NewRecorder(NewMemory()).RecordAsync(
            attendee, Url, "Standup", AcknowledgedAt, TestContext.Current.CancellationToken);

        Assert.Equal(1, attendee.StartCount);
        Assert.Equal(MeetingRecordingOutcome.JoinFailed, result.Outcome);
    }

    [Fact]
    public async Task RecordAsync_SavesNothing_WhenNobodySpoke()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        var result = await RunAsync(NewRecorder(memory), attendee, _ => { });

        Assert.Equal(MeetingRecordingOutcome.NothingCaptured, result.Outcome);
        await memory.DidNotReceive().CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RecordAsync_ReportsASaveFailure_WithoutLosingTheOutcome()
    {
        var attendee = new FakeAttendee();

        var result = await RunAsync(NewRecorder(NewMemory(writeSucceeds: false)), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        Assert.Equal(MeetingRecordingOutcome.SaveFailed, result.Outcome);
        Assert.Equal("disk full", result.Error);
    }

    [Fact]
    public async Task RecordAsync_SuffixesTheReference_WhenTheFirstNameIsTaken()
    {
        var attendee = new FakeAttendee();
        var memory = Substitute.For<IMemoryService>();
        var seen = 0;
        memory.ResolveCreateSourceAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult(new SourceCreatePreview(++seen > 1, (string)ci[0], "exists")));
        memory.CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => Task.FromResult(new SourceWrite(true, (string)ci[0], null)));

        var result = await RunAsync(NewRecorder(memory), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        // A second meeting in the same minute must not clobber the first.
        Assert.EndsWith("-2.md", result.Reference, StringComparison.Ordinal);
    }

    // ---- Consent evidence ------------------------------------------------------------------------

    private static string SavedMarkdown(IMemoryService memory) => (string)memory.ReceivedCalls()
        .Single(c => c.GetMethodInfo().Name == nameof(IMemoryService.CreateSourceAsync))
        .GetArguments()[1]!;

    private ConsentSessionMarker SavedMarker() => (ConsentSessionMarker)_evidence.ReceivedCalls()
        .Single(c => c.GetMethodInfo().Name == nameof(IConsentEvidenceStore.SaveHostAcknowledgementAsync))
        .GetArguments()[0]!;

    [Fact]
    public async Task RecordAsync_KeepsTheHostAcknowledgementAsTheSessionsEvidence_AndTheSessionLiveWhileItRecords()
    {
        var attendee = new FakeAttendee();
        IReadOnlyCollection<string> liveDuringMeeting = [];

        await RunAsync(NewRecorder(NewMemory()), attendee, a =>
        {
            liveDuringMeeting = _live.Snapshot();
            a.Emit(Utterance("hello", 0, "Speaker 1", 1));
        });

        await _evidence.Received(1).SaveHostAcknowledgementAsync(
            Arg.Any<ConsentSessionMarker>(), AcknowledgedAt, Arg.Any<CancellationToken>());
        var marker = SavedMarker();
        Assert.Equal(ConsentSessionMarker.TeamsKind, marker.Kind);
        Assert.Equal(ConsentNotice.TeamsVersion, marker.NoticeVersion);
        Assert.Equal(ConsentNotice.TeamsPurposes, marker.NoticePurposes);
        Assert.Equal("de", marker.NoticeLanguage);
        Assert.Equal([marker.SessionId], liveDuringMeeting);
        Assert.Empty(_live.Snapshot());
    }

    [Fact]
    public async Task RecordAsync_SavesTheConsentRecordIntoTheNote_AndLogsTheNoteAsTheSessionsCopy()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        var result = await RunAsync(NewRecorder(memory), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        var sessionId = SavedMarker().SessionId;
        var markdown = SavedMarkdown(memory);
        Assert.Contains($"\nconsentRecord: {ConsentFrontMatter.Schema}\n", markdown, StringComparison.Ordinal);
        Assert.Contains($"\nconsentSessions: [{sessionId}]\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nconsentNoticeVersion: 2\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nconsentNoticePurposes: [transcribe, store]\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nconsentNoticeLanguage: de\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nhostAcknowledgedAt: '2026-09-28T09:15:00+02:00'\n", markdown, StringComparison.Ordinal);
        // The host stands in for every speaker, so the block names none of them.
        Assert.DoesNotContain("consents:", markdown, StringComparison.Ordinal);
        await _evidence.Received(1).AppendCopyAsync(
            sessionId,
            Arg.Is<ConsentCopy>(c => c.Kind == ConsentCopy.VaultKind && c.Ref == result.Reference),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_EndsTheSession_OnlyOnceTheNoteAndItsCopyAreLogged()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();
        var order = new List<string>();
        memory.When(m => m.CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>())).Do(_ => order.Add("note"));
        _evidence.When(e => e.AppendCopyAsync(Arg.Any<string>(), Arg.Any<ConsentCopy>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("copy"));
        _live.SessionEnded += (_, _) => order.Add("ended");

        await RunAsync(NewRecorder(memory), attendee, a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        Assert.Equal(["note", "copy", "ended"], order);
    }

    [Fact]
    public async Task RecordAsync_EndsTheSession_WhenNothingWasCaptured()
    {
        var attendee = new FakeAttendee();
        var ended = new List<string>();
        _live.SessionEnded += (_, id) => ended.Add(id);

        var result = await RunAsync(NewRecorder(NewMemory()), attendee, _ => { });

        Assert.Equal(MeetingRecordingOutcome.NothingCaptured, result.Outcome);
        Assert.Equal([SavedMarker().SessionId], ended);
        await _evidence.DidNotReceive().AppendCopyAsync(
            Arg.Any<string>(), Arg.Any<ConsentCopy>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_WritesNoEvidence_ForAMeetingItNeverGotInto()
    {
        var attendee = new FakeAttendee { AdmissionTimeouts = 2 };
        var ended = new List<string>();
        _live.SessionEnded += (_, id) => ended.Add(id);

        var result = await NewRecorder(NewMemory()).RecordAsync(
            attendee, Url, "Standup", AcknowledgedAt, TestContext.Current.CancellationToken);

        Assert.Equal(MeetingRecordingOutcome.JoinFailed, result.Outcome);
        await _evidence.DidNotReceive().SaveHostAcknowledgementAsync(
            Arg.Any<ConsentSessionMarker>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        Assert.Empty(ended);
    }

    [Fact]
    public async Task RecordAsync_StillSavesTheNoteWithItsRecord_WhenTheEvidenceCannotBeWritten()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();
        _evidence.SaveHostAcknowledgementAsync(Arg.Any<ConsentSessionMarker>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new System.IO.IOException("disk full")));

        var result = await RunAsync(NewRecorder(memory), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        Assert.Equal(MeetingRecordingOutcome.Saved, result.Outcome);
        Assert.Contains($"consentSessions: [{SavedMarker().SessionId}]", SavedMarkdown(memory), StringComparison.Ordinal);
        Assert.Empty(_live.Snapshot());
    }



    /// <summary>Fails the join for a reason the retry must not treat as "try again in a minute".</summary>
    private sealed class ThrowingAttendee : IMeetingAttendeeService
    {
        private readonly Channel<TranscriptUtterance> _channel = Channel.CreateUnbounded<TranscriptUtterance>();

        public MeetingAttendeeState State => MeetingAttendeeState.Error;
        public event EventHandler<MeetingAttendeeState>? StateChanged { add { } remove { } }
        public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }
        public ChannelReader<TranscriptUtterance> Utterances => _channel.Reader;
        public IReadOnlyCollection<string> ObservedAttendees => [];

        public int StartCount { get; private set; }

        public Task StartAsync(string meetingUrl, CancellationToken cancellationToken = default,
            IProgress<ModelDownloadProgress>? speakerModelProgress = null)
        {
            StartCount++;
            throw new InvalidOperationException("the browser died");
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RenameSpeaker(string oldLabel, string newLabel) { }
    }
}
