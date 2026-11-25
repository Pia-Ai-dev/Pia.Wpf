namespace Pia.Services.Tts;

// One lock: Cancel runs on the UI thread while End runs on the playback worker.
internal sealed class PlaybackSlot
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;

    public CancellationTokenSource Begin(CancellationToken linkedTo)
    {
        var mine = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        lock (_gate)
        {
            _current?.Cancel();
            _current = mine;
        }
        return mine;
    }

    public bool End(CancellationTokenSource mine)
    {
        lock (_gate)
        {
            var wasCurrent = ReferenceEquals(_current, mine);
            if (wasCurrent)
                _current = null;
            mine.Dispose();
            return wasCurrent;
        }
    }

    public void Cancel()
    {
        lock (_gate)
            _current?.Cancel();
    }
}
