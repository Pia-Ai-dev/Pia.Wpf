namespace Pia.Services.MeetingAttendee;

/// <summary>
/// A background meeting session, held for as long as the meeting runs. Disposing it tears the attendee
/// down and hands the slot back — the meeting is over when the lease ends, not before.
/// </summary>
public sealed class BackgroundMeetingLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _released;

    internal BackgroundMeetingLease(IMeetingAttendeeService attendee, Func<ValueTask> release)
    {
        Attendee = attendee;
        _release = release;
    }

    public IMeetingAttendeeService Attendee { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        await _release().ConfigureAwait(false);
    }
}

/// <summary>
/// Hands out the attendees that scheduled meetings run on, one per meeting, bounded by
/// <see cref="Pia.Models.AppSettings.MaxConcurrentBackgroundMeetings"/>.
///
/// <para>These are separate instances rather than the shared singleton the overlay drives, because that
/// singleton holds one session, one utterance channel and one end-watch loop and refuses a second start.
/// Every session it hands out is <c>SilentCaptureOnly</c>: the in-browser tap is per-page, so two hidden
/// meetings never contend, whereas the endpoint-loopback fallback records the system mix and would put
/// both meetings in both transcripts.</para>
///
/// <para>The bound is CPU and memory, not audio: each session runs its own VAD, STT and diarizer.</para>
/// </summary>
public interface IBackgroundMeetingSessions
{
    /// <summary>Meetings running right now.</summary>
    int Active { get; }

    /// <summary>
    /// Takes a slot and builds an attendee for it, or returns null when they are all busy. Acquiring is
    /// what reserves the slot, so a caller must acquire BEFORE it commits to running — a check followed
    /// by a later acquire would let two meetings coming due on the same tick both pass it.
    /// </summary>
    Task<BackgroundMeetingLease?> TryAcquireAsync(CancellationToken cancellationToken = default);
}
