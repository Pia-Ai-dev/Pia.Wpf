namespace Pia.Services.MeetingAttendee;

/// <summary>Why an unattended meeting recording did not produce a transcript.</summary>
public enum MeetingRecordingOutcome
{
    Saved,

    /// <summary>Attended, but nobody said anything the pipeline could transcribe.</summary>
    NothingCaptured,

    /// <summary>The join never completed — including a second lobby timeout after the retry.</summary>
    JoinFailed,

    /// <summary>Attended and captured, but the vault write itself failed.</summary>
    SaveFailed
}

/// <param name="Reference">Vault ref the transcript was written to; null unless <see cref="MeetingRecordingOutcome.Saved"/>.</param>
public readonly record struct MeetingRecordingResult(
    MeetingRecordingOutcome Outcome, string? Reference, string? Error);

/// <summary>
/// Attends one meeting with nobody watching: joins, collects the transcript the attendee streams, and
/// files it in the vault when the meeting ends. The overlay does this same job interactively, but its
/// Save is a button — this is the path for a scheduled join, where nobody is there to click it.
/// </summary>
public interface IScheduledMeetingRecorder
{
    /// <param name="attendee">
    /// The session to run this meeting on, from <see cref="IBackgroundMeetingSessions"/>. Passed in rather
    /// than injected because concurrent meetings each need their own — the shared singleton holds one
    /// session and refuses a second start. The caller owns its lifetime; this never disposes it.
    /// </param>
    Task<MeetingRecordingResult> RecordAsync(
        IMeetingAttendeeService attendee, string meetingUrl, string title, CancellationToken cancellationToken = default);
}
