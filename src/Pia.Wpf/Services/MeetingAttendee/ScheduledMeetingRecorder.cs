using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services.MeetingAttendee;

/// <inheritdoc cref="IScheduledMeetingRecorder"/>
public sealed class ScheduledMeetingRecorder : IScheduledMeetingRecorder
{
    /// <summary>
    /// One retry, because the usual reason nobody admitted the attendee is that the organiser had not
    /// started the meeting yet. A second timeout is a different problem and is not worth a third wait.
    /// Settable only so a test does not have to sit out a real minute.
    /// </summary>
    internal TimeSpan LobbyRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the transcription tail is allowed to arrive after the meeting ends.</summary>
    internal TimeSpan DrainGrace { get; set; } = TimeSpan.FromSeconds(5);

    private const int MaxVaultReferenceAttempts = 50;

    private readonly ISettingsService _settingsService;
    private readonly IMemoryService _memoryService;
    private readonly IIngestScheduler _ingestScheduler;
    private readonly ILogger<ScheduledMeetingRecorder> _logger;

    public ScheduledMeetingRecorder(
        ISettingsService settingsService,
        IMemoryService memoryService,
        IIngestScheduler ingestScheduler,
        ILogger<ScheduledMeetingRecorder> logger)
    {
        _settingsService = settingsService;
        _memoryService = memoryService;
        _ingestScheduler = ingestScheduler;
        _logger = logger;
    }

    public async Task<MeetingRecordingResult> RecordAsync(
        IMeetingAttendeeService attendee, string meetingUrl, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attendee);
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingUrl);

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var journal = new MeetingJournal(settings.MeetingSuppressSpeakerLabels);
        var sessionStart = DateTimeOffset.Now;

        // Collect BEFORE joining, for the reason the overlay does the same: an utterance produced between
        // the join completing and a later subscribe would be lost, and nothing replays it.
        using var collectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnReassigned(object? _, IReadOnlyList<SpeakerReassignment> changes) => journal.ApplyReassignments(changes);
        attendee.SpeakersReassigned += OnReassigned;
        var collector = Task.Run(() => CollectAsync(attendee, journal, collectCts.Token), CancellationToken.None);

        try
        {
            if (!await TryJoinAsync(attendee, meetingUrl, cancellationToken).ConfigureAwait(false))
                return new MeetingRecordingResult(MeetingRecordingOutcome.JoinFailed, null, "The meeting attendee was never admitted.");

            await WaitForMeetingEndAsync(attendee, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled meeting attendance failed before the meeting ended");
            return new MeetingRecordingResult(MeetingRecordingOutcome.JoinFailed, null, ex.Message);
        }
        finally
        {
            // Give the transcription tail a moment to land, then stop collecting and take whatever is
            // still buffered — the channel completes only on the service's own disposal, so the loop
            // would otherwise never return.
            try { await Task.Delay(DrainGrace, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutting down; save what we have */ }

            await collectCts.CancelAsync().ConfigureAwait(false);
            try { await collector.ConfigureAwait(false); } catch { /* logged inside */ }
            attendee.SpeakersReassigned -= OnReassigned;
            journal.DrainRemaining(attendee.Utterances);
        }

        var bubbles = journal.Project();
        if (bubbles.Count == 0)
        {
            _logger.LogInformation("Scheduled meeting produced no transcript; nothing saved");
            return new MeetingRecordingResult(MeetingRecordingOutcome.NothingCaptured, null, null);
        }

        return await SaveAsync(attendee, bubbles, title, sessionStart, settings).ConfigureAwait(false);
    }

    /// <summary>
    /// Joins, retrying once after <see cref="LobbyRetryDelay"/> if the lobby timed out. Only that one
    /// failure is retried: the failed start already tore its browser down and left the service in
    /// <see cref="MeetingAttendeeState.Error"/>, which <c>StartAsync</c> accepts as a fresh start.
    /// </summary>
    private async Task<bool> TryJoinAsync(IMeetingAttendeeService attendee, string meetingUrl, CancellationToken ct)
    {
        try
        {
            await attendee.StartAsync(meetingUrl, ct).ConfigureAwait(false);
            return true;
        }
        catch (MeetingAdmissionTimeoutException ex)
        {
            _logger.LogWarning(ex, "Not admitted to the scheduled meeting; retrying once in {Seconds}s", LobbyRetryDelay.TotalSeconds);
        }

        await Task.Delay(LobbyRetryDelay, ct).ConfigureAwait(false);

        try
        {
            await attendee.StartAsync(meetingUrl, ct).ConfigureAwait(false);
            return true;
        }
        catch (MeetingAdmissionTimeoutException ex)
        {
            _logger.LogWarning(ex, "Still not admitted to the scheduled meeting after the retry; giving up");
            return false;
        }
    }

    /// <summary>
    /// Waits for the attendee to stop itself, which is what a meeting ending looks like from here. The
    /// state is re-read AFTER subscribing so a meeting that ended in between is not waited on forever.
    /// </summary>
    private static async Task WaitForMeetingEndAsync(IMeetingAttendeeService attendee, CancellationToken ct)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? _, MeetingAttendeeState state)
        {
            if (state is MeetingAttendeeState.Idle or MeetingAttendeeState.Error) ended.TrySetResult();
        }

        attendee.StateChanged += OnStateChanged;
        try
        {
            if (attendee.State is MeetingAttendeeState.Idle or MeetingAttendeeState.Error) return;
            await ended.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            attendee.StateChanged -= OnStateChanged;
        }
    }

    private async Task CollectAsync(IMeetingAttendeeService attendee, MeetingJournal journal, CancellationToken ct)
    {
        try
        {
            await foreach (var utterance in attendee.Utterances.ReadAllAsync(ct).ConfigureAwait(false))
                journal.Add(utterance);
        }
        catch (OperationCanceledException) { /* expected: the meeting ended */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collecting the scheduled meeting transcript failed");
        }
    }

    private async Task<MeetingRecordingResult> SaveAsync(
        IMeetingAttendeeService attendee, IReadOnlyList<TranscriptBubble> bubbles, string title,
        DateTimeOffset sessionStart, AppSettings settings)
    {
        var (reference, refusal) = await ResolveFreeReferenceAsync(sessionStart, title).ConfigureAwait(false);
        if (reference is null)
            return new MeetingRecordingResult(MeetingRecordingOutcome.SaveFailed, null, refusal);

        var metadata = new MeetingVaultMetadata(
            Title: title,
            Start: sessionStart,
            End: bubbles[^1].EndTimestamp,
            Source: "teams",
            Attendees: attendee.ObservedAttendees,
            Tags: [],
            Project: null,
            Notes: null);

        var markdown = MeetingVaultMarkdown.Render(
            metadata, DirectTranscriptMarkdown.RenderBody(title, bubbles, settings.LastCounterpartName));

        var write = await _memoryService.CreateSourceAsync(reference, markdown).ConfigureAwait(false);
        if (!write.Success)
        {
            _logger.LogWarning("Saving a scheduled meeting into the vault failed");
            return new MeetingRecordingResult(MeetingRecordingOutcome.SaveFailed, null, write.Error);
        }

        _logger.LogInformation("Saved a scheduled meeting into the vault ({Chars} chars)", markdown.Length);
        _logger.SensitiveDebug("Scheduled meeting saved as {Ref}", write.Ref);

        _ingestScheduler.RunAsync(write.Ref).SafeFireAndForget(_logger);
        return new MeetingRecordingResult(MeetingRecordingOutcome.Saved, write.Ref, null);
    }

    /// <summary>
    /// The first ref the title yields that is not taken, suffixed <c>-2</c>, <c>-3</c>, … so two meetings
    /// scheduled in the same minute cannot clobber each other.
    /// </summary>
    private async Task<(string? Reference, string? Error)> ResolveFreeReferenceAsync(DateTimeOffset start, string title)
    {
        var baseTitle = title.Trim();
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxVaultReferenceAttempts; attempt++)
        {
            var candidate = MeetingVaultMarkdown.BuildReference(
                start, attempt == 1 ? baseTitle : $"{baseTitle} {attempt}");

            var preview = await _memoryService.ResolveCreateSourceAsync(candidate).ConfigureAwait(false);
            if (preview.CanWrite) return (candidate, null);

            lastError = preview.Error;
        }

        return (null, lastError);
    }

    /// <summary>
    /// The unattended twin of the overlay's journal: the same utterance retention and the same retroactive
    /// relabelling, without the UI collection. Grouping and numbering come from the shared helpers, so the
    /// saved transcript is shaped the same either way.
    /// </summary>
    private sealed class MeetingJournal(bool suppressLabels)
    {
        private readonly Lock _gate = new();
        private readonly List<UtteranceEntry> _entries = [];
        private readonly Dictionary<long, string?> _pending = [];

        public void Add(TranscriptUtterance utterance)
        {
            lock (_gate)
            {
                var label = utterance.SpeakerLabel;
                if (utterance.SegmentId is long id && _pending.Remove(id, out var corrected))
                    label = corrected;

                _entries.Add(new UtteranceEntry
                {
                    Speaker = utterance.Speaker,
                    Text = utterance.Text,
                    Timestamp = utterance.Timestamp,
                    Label = label,
                    SegmentId = utterance.SegmentId,
                });
            }
        }

        /// <summary>
        /// A correction can arrive before the utterance it corrects — the re-cluster pass runs inside the
        /// diarizer call for a segment whose transcription is still seconds away — so an unmatched one is
        /// parked rather than dropped.
        /// </summary>
        public void ApplyReassignments(IReadOnlyList<SpeakerReassignment> changes)
        {
            if (changes.Count == 0) return;
            lock (_gate)
            {
                var bySegment = new Dictionary<long, string?>(changes.Count);
                foreach (var change in changes) bySegment[change.SegmentId] = change.NewLabel;

                var seen = new HashSet<long>();
                foreach (var entry in _entries)
                {
                    if (entry.SegmentId is not long id) continue;
                    if (!bySegment.TryGetValue(id, out var newLabel)) continue;
                    seen.Add(id);
                    entry.Label = newLabel;
                }

                foreach (var (id, newLabel) in bySegment)
                {
                    if (!seen.Contains(id)) _pending[id] = newLabel;
                }
            }
        }

        /// <summary>Takes whatever the collector loop left buffered when it was cancelled.</summary>
        public void DrainRemaining(ChannelReader<TranscriptUtterance> reader)
        {
            while (reader.TryRead(out var utterance)) Add(utterance);
        }

        public IReadOnlyList<TranscriptBubble> Project()
        {
            lock (_gate)
            {
                var numbering = new SpeakerDisplayNumbering();
                var bubbles = new List<TranscriptBubble>();

                foreach (var entry in _entries)
                {
                    var last = bubbles.Count > 0 ? bubbles[^1] : null;
                    if (!TranscriptGrouping.ShouldReuse(last, entry.Speaker, entry.Timestamp, entry.Label))
                    {
                        last = new TranscriptBubble(
                            entry.Speaker, entry.Timestamp,
                            speakerLabel: entry.Label,
                            displayLabel: numbering.Resolve(entry.Label, suppressLabels));
                        bubbles.Add(last);
                    }

                    last!.Append(entry.Text, entry.Timestamp);
                }

                return bubbles;
            }
        }
    }
}
