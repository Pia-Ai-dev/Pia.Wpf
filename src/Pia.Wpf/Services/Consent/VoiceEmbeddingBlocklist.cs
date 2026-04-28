namespace Pia.Services.Consent;

/// <summary>
/// Session-scoped pool of blocked voice embeddings. A new VAD segment is dropped if its
/// embedding is within <see cref="Threshold"/> cosine similarity of any blocked entry.
/// Default threshold is 0.85 per spec §3.9.
///
/// Holds <c>float[]</c> in memory only. Persistence across sessions is **not** allowed
/// in Phase 3 because biometric data falls under Art. 9 DSGVO and requires its own
/// consent — see Phase 5.
/// </summary>
public sealed class VoiceEmbeddingBlocklist
{
    public const float DefaultThreshold = 0.85f;

    private readonly float _threshold;
    private readonly object _lock = new();
    private readonly List<float[]> _entries = new();

    public VoiceEmbeddingBlocklist(float threshold = DefaultThreshold)
    {
        _threshold = threshold;
    }

    public float Threshold => _threshold;
    public int Count { get { lock (_lock) return _entries.Count; } }

    public void Add(float[] embedding)
    {
        if (embedding is null || embedding.Length == 0) return;
        var copy = (float[])embedding.Clone();
        lock (_lock) _entries.Add(copy);
    }

    public bool ShouldDrop(float[] embedding)
    {
        if (embedding is null || embedding.Length == 0) return false;
        lock (_lock)
        {
            foreach (var blocked in _entries)
            {
                if (CosineSimilarity(embedding, blocked) >= _threshold) return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0f : dot / denom;
    }
}
