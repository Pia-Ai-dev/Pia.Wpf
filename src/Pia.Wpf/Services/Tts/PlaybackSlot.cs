namespace Pia.Services.Tts;

// One lock: Cancel runs on the UI thread while Start/End run on the playback worker, and a
// superseded owner must never change the playing state of the one that replaced it.
internal sealed class PlaybackSlot
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _isPlaying;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _isPlaying;
        }
    }

    public CancellationTokenSource Begin(CancellationToken linkedTo)
    {
        var mine = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        lock (_gate)
        {
            _current?.Cancel();
            _current = mine;
            _isPlaying = false;
        }
        return mine;
    }

    public bool Start(CancellationTokenSource mine)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, mine))
                return false;
            _isPlaying = true;
            return true;
        }
    }

    public bool End(CancellationTokenSource mine)
    {
        lock (_gate)
        {
            var wasCurrent = ReferenceEquals(_current, mine);
            if (wasCurrent)
            {
                _current = null;
                _isPlaying = false;
            }
            mine.Dispose();
            return wasCurrent;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _isPlaying = false;
        }
    }
}
