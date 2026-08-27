using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
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

    private static ScheduledMeetingRecorder NewRecorder(
        FakeAttendee attendee, IMemoryService memory, IIngestScheduler? ingest = null, AppSettings? settings = null) =>
        Quickened(new ScheduledMeetingRecorder(
            attendee, NewSettings(settings), memory, ingest ?? Substitute.For<IIngestScheduler>(),
            NullLogger<ScheduledMeetingRecorder>.Instance));

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
        var recording = recorder.RecordAsync(Url, "Q3 roadmap sync");

        while (attendee.State != MeetingAttendeeState.Attending && !recording.IsCompleted)
            await Task.Delay(10);

        duringMeeting(attendee);
        attendee.EndMeeting();

        return await recording;
    }

    [Fact]
    public async Task RecordAsync_SavesTheTranscriptUnderTheTranscriptsFolder()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();
        var ingest = Substitute.For<IIngestScheduler>();

        var result = await RunAsync(NewRecorder(attendee, memory, ingest), attendee, a =>
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

    [Fact]
    public async Task RecordAsync_TriggersIngest_SoRecallCanReachTheTranscript()
    {
        var attendee = new FakeAttendee();
        var ingest = Substitute.For<IIngestScheduler>();

        var result = await RunAsync(NewRecorder(attendee, NewMemory(), ingest), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        await ingest.Received(1).RunAsync(result.Reference!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_AppliesRetroactiveSpeakerCorrections()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        await RunAsync(NewRecorder(attendee, memory), attendee, a =>
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

        var result = await RunAsync(NewRecorder(attendee, memory), attendee,
            a => a.Emit(Utterance("made it", 0, "Speaker 1", 1)));

        Assert.Equal(2, attendee.StartCount);
        Assert.Equal(MeetingRecordingOutcome.Saved, result.Outcome);
    }

    [Fact]
    public async Task RecordAsync_GivesUpAfterASecondAdmissionTimeout()
    {
        var attendee = new FakeAttendee { AdmissionTimeouts = 2 };

        var result = await NewRecorder(attendee, NewMemory()).RecordAsync(Url, "Standup", TestContext.Current.CancellationToken);

        Assert.Equal(2, attendee.StartCount);
        Assert.Equal(MeetingRecordingOutcome.JoinFailed, result.Outcome);
    }

    [Fact]
    public async Task RecordAsync_DoesNotRetryAJoinFailureThatIsNotALobbyTimeout()
    {
        var attendee = new ThrowingAttendee();

        var result = await NewRecorder2(attendee).RecordAsync(Url, "Standup", TestContext.Current.CancellationToken);

        Assert.Equal(1, attendee.StartCount);
        Assert.Equal(MeetingRecordingOutcome.JoinFailed, result.Outcome);
    }

    [Fact]
    public async Task RecordAsync_SavesNothing_WhenNobodySpoke()
    {
        var attendee = new FakeAttendee();
        var memory = NewMemory();

        var result = await RunAsync(NewRecorder(attendee, memory), attendee, _ => { });

        Assert.Equal(MeetingRecordingOutcome.NothingCaptured, result.Outcome);
        await memory.DidNotReceive().CreateSourceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RecordAsync_ReportsASaveFailure_WithoutLosingTheOutcome()
    {
        var attendee = new FakeAttendee();

        var result = await RunAsync(NewRecorder(attendee, NewMemory(writeSucceeds: false)), attendee,
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

        var result = await RunAsync(NewRecorder(attendee, memory), attendee,
            a => a.Emit(Utterance("hello", 0, "Speaker 1", 1)));

        // A second meeting in the same minute must not clobber the first.
        Assert.EndsWith("-2.md", result.Reference, StringComparison.Ordinal);
    }

    private static ScheduledMeetingRecorder NewRecorder2(ThrowingAttendee attendee) =>
        Quickened(new ScheduledMeetingRecorder(
            attendee, NewSettings(), NewMemory(), Substitute.For<IIngestScheduler>(),
            NullLogger<ScheduledMeetingRecorder>.Instance));

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
