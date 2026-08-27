using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.MeetingAttendee;

/// <inheritdoc cref="IBackgroundMeetingSessions"/>
public sealed class BackgroundMeetingSessions : IBackgroundMeetingSessions
{
    private readonly Func<MeetingAttendeeService> _create;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<BackgroundMeetingSessions> _logger;

    private readonly Lock _gate = new();
    private int _active;

    public BackgroundMeetingSessions(
        Func<MeetingAttendeeService> create,
        ISettingsService settingsService,
        ILogger<BackgroundMeetingSessions> logger)
    {
        _create = create;
        _settingsService = settingsService;
        _logger = logger;
    }

    public int Active { get { lock (_gate) return _active; } }

    public async Task<BackgroundMeetingLease?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        // A misconfigured 0 or a negative would switch scheduled meetings off silently, which is a worse
        // answer than the floor of one.
        var capacity = Math.Max(1, settings.MaxConcurrentBackgroundMeetings);

        lock (_gate)
        {
            if (_active >= capacity)
            {
                _logger.LogInformation(
                    "No background meeting slot free ({Active}/{Capacity} busy)", _active, capacity);
                return null;
            }

            _active++;
        }

        // Built after the slot is taken, and released again if construction throws — a slot leaked here
        // would shrink the pool for the rest of the process.
        try
        {
            var attendee = _create();
            attendee.SilentCaptureOnly = true;
            _logger.LogInformation("Background meeting slot taken ({Active}/{Capacity})", Active, capacity);
            return new BackgroundMeetingLease(attendee, () => ReleaseAsync(attendee));
        }
        catch
        {
            lock (_gate) _active--;
            throw;
        }
    }

    private async ValueTask ReleaseAsync(IMeetingAttendeeService attendee)
    {
        // Disposed before the slot is handed back, so a fresh acquire can never race a browser and a set
        // of models that are still being torn down.
        try
        {
            if (attendee is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing a background meeting session threw");
        }
        finally
        {
            lock (_gate) _active--;
            _logger.LogInformation("Background meeting slot released ({Active} still running)", Active);
        }
    }
}
