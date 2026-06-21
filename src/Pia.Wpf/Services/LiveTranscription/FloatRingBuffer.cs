namespace Pia.Services.LiveTranscription;

/// <summary>
/// Fixed-capacity FIFO of float samples used by the VAD pre-windowing stage. Constant-time
/// append and constant-time fixed-size dequeue, which avoids the O(n) memmove that
/// <see cref="List{T}.RemoveRange"/> incurs on every 32 ms VAD window.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _data;
    private int _head;
    private int _count;

    public FloatRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _data = new float[capacity];
    }

    public int Count => _count;
    public int Capacity => _data.Length;

    public void Write(ReadOnlySpan<float> samples)
    {
        if (_count + samples.Length > _data.Length)
            throw new InvalidOperationException(
                $"FloatRingBuffer overflow: {_count}+{samples.Length} > {_data.Length}");

        var tail = (_head + _count) % _data.Length;
        var firstChunk = Math.Min(samples.Length, _data.Length - tail);
        samples.Slice(0, firstChunk).CopyTo(_data.AsSpan(tail));
        if (firstChunk < samples.Length)
            samples.Slice(firstChunk).CopyTo(_data.AsSpan(0));
        _count += samples.Length;
    }

    public bool TryRead(Span<float> destination)
    {
        if (destination.Length > _count) return false;

        var firstChunk = Math.Min(destination.Length, _data.Length - _head);
        _data.AsSpan(_head, firstChunk).CopyTo(destination);
        if (firstChunk < destination.Length)
            _data.AsSpan(0, destination.Length - firstChunk).CopyTo(destination.Slice(firstChunk));

        _head = (_head + destination.Length) % _data.Length;
        _count -= destination.Length;
        return true;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
