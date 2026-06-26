namespace Pia.Services.MeetingAttendee;

/// <summary>
/// One automated browser session that joins a single meeting and stays until it ends.
///
/// Lifecycle: <see cref="JoinAsync"/> launches the browser and drives the join flow (through the
/// lobby, if any, until admitted); <see cref="WaitForEndAsync"/> blocks until the meeting ends
/// (the in-call UI disappears) or the supplied token cancels; <see cref="LeaveAsync"/> performs an
/// explicit hang-up; <see cref="IAsyncDisposable.DisposeAsync"/> tears the browser down. The
/// orchestrator (Unit 4) owns one instance for the duration of one attended meeting and disposes it
/// through its stop/dispose chain — mirroring how <c>LiveMeetingService</c> owns its audio sources.
/// </summary>
public interface IMeetingSession : IAsyncDisposable
{
    /// <summary>
    /// The OS process id of the launched browser's root process, or <c>null</c> if it could not be
    /// determined. Used by the per-process loopback audio source (Unit 3) to target this browser's
    /// audio render session via <c>INCLUDE_TARGET_PROCESS_TREE</c>. The default (endpoint loopback)
    /// audio path does not need it.
    /// </summary>
    int? BrowserProcessId { get; }

    /// <summary>
    /// Raised when the session reaches the meeting lobby (waiting for a host to admit the bot). The
    /// orchestrator surfaces this as an <c>InLobby</c> state. May never fire if the bot is admitted
    /// immediately.
    /// </summary>
    event EventHandler? EnteredLobby;

    /// <summary>
    /// Launches the browser and joins the meeting as <paramref name="displayName"/>. Completes once
    /// the bot is admitted into the call. Throws if the join flow cannot complete within its
    /// bounded timeout (e.g. never admitted, selectors not found).
    /// </summary>
    /// <param name="meetingUrl">The Teams meeting URL the user pasted.</param>
    /// <param name="displayName">The name shown to other participants (e.g. "Alex's assistant").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task JoinAsync(string meetingUrl, string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks until the meeting ends (the in-call hang-up control disappears / the call-ended state
    /// is observed) or <paramref name="cancellationToken"/> is cancelled. Meetings can run for
    /// hours, so this honours the token rather than imposing a fixed timeout.
    /// </summary>
    Task WaitForEndAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly leaves the meeting: clicks the hang-up control if present, then closes the
    /// browser. Safe to call once; further teardown is handled by <see cref="IAsyncDisposable"/>.
    /// </summary>
    Task LeaveAsync();

    /// <summary>
    /// Best-effort read of the participant names currently shown in the meeting's "People" roster.
    /// Returns an empty list on any failure (panel not open, selector miss, page navigating, not yet
    /// admitted) — reading the roster must NEVER fail the meeting. Safe to call repeatedly while
    /// attending; the orchestrator polls it on a cadence and accumulates the union of names seen.
    /// Implementations must serialize this against their own page polling (e.g.
    /// <see cref="WaitForEndAsync"/>), since the underlying browser page does not allow concurrent
    /// operations.
    /// </summary>
    Task<IReadOnlyList<string>> GetAttendeeNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the <b>silent in-browser audio capture</b>: taps the meeting's remote audio inside the
    /// page (Web Audio) and mutes it from the speakers, so the meeting is captured for transcription
    /// without being audible on the device. This is the mechanism behind the "hidden ⇒ silent"
    /// contract — the previous per-process WASAPI loopback only <i>captured</i> the browser audio (a
    /// tap) and never silenced the speakers.
    ///
    /// <para><paramref name="onFormat"/> is invoked once with the source PCM <c>(sampleRate, channels)</c>
    /// before any audio; <paramref name="onPcm"/> is then invoked repeatedly with little-endian
    /// interleaved Float32 PCM frames in that format. Both run on the implementation's binding-dispatch
    /// thread and must not block. Speaker muting is armed only once a remote track is actually captured,
    /// so a failed capture never leaves the meeting silent — and <see cref="StopAudioCaptureAsync"/>
    /// (also called on dispose) unmutes, which is what lets the orchestrator degrade to the audible
    /// endpoint loopback. Throws if the in-page tap cannot be armed (so the caller can degrade).</para>
    /// </summary>
    Task StartAudioCaptureAsync(
        Action<int, int> onFormat,
        Action<byte[]> onPcm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the in-browser audio tap started by <see cref="StartAudioCaptureAsync"/> and <b>unmutes</b>
    /// the page's media elements. Best-effort and safe to call when no capture is running or the page is
    /// already gone. Idempotent. The in-browser audio source calls this from its own dispose, which is
    /// what unmutes the meeting before the orchestrator degrades to the audible endpoint loopback (on a
    /// full session teardown the browser is closing anyway, so unmuting is moot).
    /// </summary>
    Task StopAudioCaptureAsync();
}
