using System.IO;
using Pia.Services.LiveTranscription;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>One speech segment as the bench saw it: exact stream position, and the labels the
/// diarizer gave it before and after every re-cluster pass.</summary>
internal sealed class BenchSegment
{
    public long StartSample { get; init; }
    public int SampleCount { get; init; }
    public float[] Samples { get; init; } = [];
    public float[]? Embedding { get; set; }
    public string? Label { get; set; }
    public string? FinalLabel { get; set; }

    public double StartSeconds => StartSample / 16000.0;
    public double DurationSeconds => SampleCount / 16000.0;
    public double MidSeconds => StartSeconds + DurationSeconds / 2;
}

/// <summary>
/// Embeddings keyed by the segment they came from, so a re-run of the same recording skips the only
/// expensive step. Keyed by stream position rather than call order: a changed VAD simply misses.
/// Biometric data — the caller keeps it under artifacts/, which is gitignored.
/// </summary>
internal sealed class EmbeddingCache
{
    private const string Magic = "PIABENCH1";

    private readonly Dictionary<(long Start, int Count), float[]> _entries = [];

    public int Dim { get; private set; }
    public bool Dirty { get; private set; }
    public int Count => _entries.Count;

    // Both sides copy. The identification service zeroes every embedding it holds when it disposes
    // (deliberate biometric hygiene), and it normalizes in place, so sharing an array with the cache
    // means the cache is wiped along with it — silently, after the run that filled it.
    public bool TryGet(long start, int count, out float[] embedding)
    {
        if (!_entries.TryGetValue((start, count), out var stored))
        {
            embedding = null!;
            return false;
        }
        embedding = (float[])stored.Clone();
        return true;
    }

    public void Put(long start, int count, float[] embedding)
    {
        if (Dim == 0) Dim = embedding.Length;
        _entries[(start, count)] = (float[])embedding.Clone();
        Dirty = true;
    }

    public static EmbeddingCache Load(string path)
    {
        var cache = new EmbeddingCache();
        if (!File.Exists(path)) return cache;

        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadString() != Magic) return cache;
        cache.Dim = reader.ReadInt32();
        var count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var start = reader.ReadInt64();
            var samples = reader.ReadInt32();
            var vector = new float[cache.Dim];
            for (int d = 0; d < cache.Dim; d++) vector[d] = reader.ReadSingle();
            cache._entries[(start, samples)] = vector;
        }
        return cache;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Magic);
        writer.Write(Dim);
        writer.Write(_entries.Count);
        foreach (var ((start, count), vector) in _entries)
        {
            writer.Write(start);
            writer.Write(count);
            foreach (var v in vector) writer.Write(v);
        }
        Dirty = false;
    }
}

/// <summary>
/// Serves the cache and falls back to the real extractor, which is only constructed on a miss — so a
/// warm run needs neither the ONNX model nor its native runtime. <see cref="Current"/> must be set to
/// the segment about to be identified; the service's own Compute call carries no position.
/// </summary>
internal sealed class CachedEmbeddingExtractor : IEmbeddingExtractor
{
    private readonly EmbeddingCache _cache;
    private readonly Lazy<IEmbeddingExtractor> _inner;

    public CachedEmbeddingExtractor(EmbeddingCache cache, Func<IEmbeddingExtractor> inner)
    {
        _cache = cache;
        _inner = new Lazy<IEmbeddingExtractor>(inner);
    }

    public (long Start, int Count) Current { get; set; }
    public int Misses { get; private set; }

    public int Dim => _cache.Dim > 0 ? _cache.Dim : _inner.Value.Dim;

    public float[] Compute(float[] samples, int sampleRate)
    {
        if (Current.Count != samples.Length)
            throw new InvalidOperationException(
                $"Current segment ({Current.Count} samples) does not match the Compute call ({samples.Length}).");

        if (_cache.TryGet(Current.Start, Current.Count, out var cached)) return cached;

        Misses++;
        var computed = _inner.Value.Compute(samples, sampleRate);
        _cache.Put(Current.Start, Current.Count, computed);
        return computed;
    }

    public void Dispose()
    {
        if (_inner.IsValueCreated) _inner.Value.Dispose();
    }
}
