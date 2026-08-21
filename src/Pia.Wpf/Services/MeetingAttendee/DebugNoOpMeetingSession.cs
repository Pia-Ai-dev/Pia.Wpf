#if DEBUG
namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Dev-only <see cref="IMeetingSession"/> stand-in for a real Teams join, used when
/// PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE is set: joins instantly, never enters a lobby, and reports
/// the names in PIA_DEBUG_MEETING_ATTENDEE_ROSTER (empty when unset, which leaves the diarizer's
/// roster ceiling off). Audio comes from <see cref="LiveTranscription.DebugFileAudioCaptureService"/>
/// instead of this session's (nonexistent) WebRTC tap. Wired only from a DEBUG-gated env-var branch
/// in <c>Bootstrapper</c>; never referenced from a Release build.
/// </summary>
internal sealed class DebugNoOpMeetingSession : IMeetingSession
{
    private readonly TaskCompletionSource _endSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<string> _roster;

    public DebugNoOpMeetingSession(IReadOnlyList<string>? roster = null)
        => _roster = roster ?? [];

    /// <summary>Splits a semicolon-separated replay roster; blank entries are dropped.</summary>
    internal static IReadOnlyList<string> ParseRoster(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int? BrowserProcessId => null;

    public event EventHandler? EnteredLobby { add { } remove { } }

    public Task JoinAsync(string meetingUrl, string displayName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task WaitForEndAsync(CancellationToken cancellationToken = default)
    {
        await using var registration = cancellationToken.Register(() => _endSignal.TrySetCanceled(cancellationToken));
        await _endSignal.Task.ConfigureAwait(false);
    }

    public Task LeaveAsync()
    {
        _endSignal.TrySetResult();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetAttendeeNamesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_roster);

    public Task StartAudioCaptureAsync(Action<int, int> onFormat, Action<byte[]> onPcm, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StopAudioCaptureAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _endSignal.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
#endif
