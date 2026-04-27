using System.Collections.Concurrent;

namespace Pia.Services.Consent;

/// <summary>
/// Keyed wrapper around per-speaker ring buffers with a global memory cap. When the sum of
/// samples across all speakers exceeds <see cref="TotalCapacity"/>, oldest samples are evicted
/// from the largest buffer. Disk spill is forbidden — eviction is in-memory only.
/// </summary>
public sealed class PerSpeakerRingBufferRegistry
{
    private readonly ConcurrentDictionary<string, SpeakerRingBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly int _perSpeakerCapacity;
    private readonly object _capLock = new();

    public int TotalCapacity { get; }

    public PerSpeakerRingBufferRegistry(int perSpeakerCapacity, int totalCapacity)
    {
        if (perSpeakerCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(perSpeakerCapacity));
        if (totalCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(totalCapacity));
        _perSpeakerCapacity = perSpeakerCapacity;
        TotalCapacity = totalCapacity;
    }

    public void Append(string speakerLabel, ReadOnlySpan<float> samples)
    {
        var buffer = _buffers.GetOrAdd(speakerLabel, _ => new SpeakerRingBuffer(_perSpeakerCapacity));
        buffer.Append(samples);
        EnforceTotalCap();
    }

    public int Count(string speakerLabel)
        => _buffers.TryGetValue(speakerLabel, out var buf) ? buf.Count : 0;

    public int TotalSamples
    {
        get
        {
            var total = 0;
            foreach (var b in _buffers.Values) total += b.Count;
            return total;
        }
    }

    public float[] Drain(string speakerLabel)
        => _buffers.TryGetValue(speakerLabel, out var buf) ? buf.Drain() : Array.Empty<float>();

    public void RemoveAll()
    {
        foreach (var b in _buffers.Values) b.Clear();
    }

    private void EnforceTotalCap()
    {
        // Serialize cap-enforcement so concurrent appends do not over-evict.
        lock (_capLock)
        {
            while (TotalSamples > TotalCapacity)
            {
                SpeakerRingBuffer? largest = null;
                foreach (var b in _buffers.Values)
                {
                    if (largest is null || b.Count > largest.Count) largest = b;
                }
                if (largest is null || largest.Count == 0) return;
                largest.EvictOldest(1024);
            }
        }
    }
}
