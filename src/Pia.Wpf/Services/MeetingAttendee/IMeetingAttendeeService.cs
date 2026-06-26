using System.Threading.Channels;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Lifecycle states of the meeting attendee. Mirrors <see cref="Pia.Services.Interfaces.LiveMeetingState"/>
/// but adds the browser-join specific phases (<see cref="ProvisioningBrowser"/>, <see cref="Joining"/>,
/// <see cref="InLobby"/>, <see cref="Attending"/>).
/// </summary>
public enum MeetingAttendeeState
{
    /// <summary>Not attending; no browser, no capture.</summary>
    Idle,

    /// <summary>Ensuring the automated browser (Chromium) is on disk before joining.</summary>
    ProvisioningBrowser,

    /// <summary>Driving the join flow (navigating, entering the name, clicking "Join now").</summary>
    Joining,

    /// <summary>Admitted to the lobby; waiting for a host to let the bot in.</summary>
    InLobby,

    /// <summary>In the meeting; audio is being captured and transcribed.</summary>
    Attending,

    /// <summary>Leaving the meeting and tearing down capture + browser.</summary>
    Stopping,

    /// <summary>A start/join/capture step failed; resources have been cleaned up.</summary>
    Error
}

/// <summary>
/// Orchestrates one automated "meeting attendee": provisions a browser, joins a Teams meeting as the
/// user's assistant, captures the meeting audio, and feeds it through the existing live-transcription
/// pipeline. Exposes the resulting utterance stream the UI consumes — modelled on
/// <c>ILiveMeetingService</c>.
///
/// Transcript <b>saving is the ViewModel's responsibility</b> (it reuses the existing save flow); this
/// service only produces <see cref="Utterances"/>.
/// </summary>
public interface IMeetingAttendeeService
{
    MeetingAttendeeState State { get; }

    event EventHandler<MeetingAttendeeState>? StateChanged;

    /// <summary>
    /// Reader of the attendee's utterance stream. The reader instance is stable for the lifetime of
    /// the service; the channel is completed only on <see cref="IAsyncDisposable.DisposeAsync"/>
    /// (when the service is itself disposable).
    /// </summary>
    ChannelReader<TranscriptUtterance> Utterances { get; }

    /// <summary>
    /// The union of participant names observed in the Teams roster during the current (or most recent)
    /// meeting, in first-seen order and excluding the attendee's own display name. Accumulated from
    /// periodic roster snapshots (cadence from <see cref="AppSettings.MeetingAttendeeRosterSnapshotMinutes"/>)
    /// and surfaced as metadata for the post-meeting summary so the model can attribute the diarized
    /// "Speaker N" labels to real people. Empty when snapshots are disabled or none were captured;
    /// reset on each <see cref="StartAsync"/>, retained after stop until the next start.
    /// </summary>
    IReadOnlyCollection<string> ObservedAttendees { get; }

    /// <summary>
    /// Provisions the browser, joins <paramref name="meetingUrl"/> as the user's assistant, and starts
    /// capturing + transcribing the meeting audio. Returns once the bot is in the meeting
    /// (<see cref="MeetingAttendeeState.Attending"/>); the meeting then runs in the background until it
    /// ends or <see cref="StopAsync"/> is called.
    /// </summary>
    /// <param name="speakerModelProgress">
    /// Optional sink for the OPTIONAL speaker-embedding model download (per-speaker diarization). Receives
    /// <see cref="ModelDownloadPhase.Downloading"/> ticks while the model downloads and a terminal
    /// <see cref="ModelDownloadPhase.Completed"/> report on success, failure, or cancellation. A cached
    /// model (or disabled diarization) reports nothing until the terminal signal, so the UI can lazily
    /// show a dialog only when a download actually starts. Never affects whether the meeting is joined —
    /// a speaker-model failure degrades to single-bubble behavior.
    /// </param>
    Task StartAsync(
        string meetingUrl,
        CancellationToken cancellationToken = default,
        IProgress<ModelDownloadProgress>? speakerModelProgress = null);

    /// <summary>
    /// Leaves the meeting and tears down capture + browser. Idempotent: a no-op when already idle or
    /// stopping.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a diarized speaker label (e.g. <c>"Speaker 2"</c> → <c>"Marco"</c>) on the live diarizer
    /// for the current meeting only. In-session, in-memory, and discarded at meeting end (fresh service
    /// per meeting). A no-op when diarization is off (no underlying speaker-identification service); must
    /// not throw.
    /// </summary>
    void RenameSpeaker(string oldLabel, string newLabel);
}
