namespace Pia.Services.Consent;

/// <summary>
/// Bounded RAM-only circular sample queue. Phase 1: single-speaker, single buffer.
/// Disk spill is forbidden — when capacity is exceeded, oldest samples are overwritten.
/// </summary>
public sealed class SpeakerRingBuffer
{
    private readonly float[] _buffer;
    private int _start;
    private int _count;
    private readonly object _lock = new();

    public SpeakerRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _buffer = new float[capacitySamples];
    }

    public int Capacity => _buffer.Length;
    public int Count { get { lock (_lock) return _count; } }

    public void Append(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                var write = (_start + _count) % _buffer.Length;
                _buffer[write] = s;
                if (_count < _buffer.Length) _count++;
                else _start = (_start + 1) % _buffer.Length;
            }
        }
    }

    public float[] Snapshot()
    {
        lock (_lock)
        {
            var result = new float[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(_start + i) % _buffer.Length];
            return result;
        }
    }

    public float[] Drain()
    {
        lock (_lock)
        {
            var result = SnapshotNoLock();
            ClearNoLock();
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock) ClearNoLock();
    }

    /// <summary>
    /// Drops up to <paramref name="sampleCount"/> oldest samples without copying them out.
    /// Used by the registry's global-cap enforcer.
    /// </summary>
    public void EvictOldest(int sampleCount)
    {
        if (sampleCount <= 0) return;
        lock (_lock)
        {
            var drop = Math.Min(sampleCount, _count);
            _start = (_start + drop) % _buffer.Length;
            _count -= drop;
        }
    }

    private float[] SnapshotNoLock()
    {
        var result = new float[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(_start + i) % _buffer.Length];
        return result;
    }

    private void ClearNoLock()
    {
        Array.Clear(_buffer);
        _start = 0;
        _count = 0;
    }
}
