namespace Pia.Services.Tts;

// Reads and delivers under one lock, so the last delivery always carries the latest state.
internal sealed class PlayingIndicator
{
    private readonly object _gate = new();
    private readonly Func<bool> _read;
    private readonly Action<bool> _deliver;
    private bool _lastDelivered;

    public PlayingIndicator(Func<bool> read, Action<bool> deliver)
    {
        _read = read;
        _deliver = deliver;
    }

    public void Notify()
    {
        lock (_gate)
        {
            var now = _read();
            if (now == _lastDelivered)
                return;
            _lastDelivered = now;
            _deliver(now);
        }
    }
}
