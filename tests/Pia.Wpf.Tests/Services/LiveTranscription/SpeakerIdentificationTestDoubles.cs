using Pia.Services.LiveTranscription;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Maps a segment's first sample (read as degrees) to a unit vector on a circle, so the cosine
/// similarity between two "voices" is exactly cos(Δθ) — voice separation becomes a dial.
/// </summary>
internal sealed class DegreeEmbeddingExtractor : IEmbeddingExtractor
{
    public int Dim => 2;
    public bool Disposed { get; private set; }

    public float[] Compute(float[] samples, int sampleRate)
    {
        var r = Math.PI * samples[0] / 180.0;
        return [(float)Math.Cos(r), (float)Math.Sin(r)];
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Records what each pass asked for, and can answer from a script instead of clustering.</summary>
internal sealed class RecordingClusterer : SpeakerClusterer
{
    public List<(int Inputs, int PreviousClusterCount, int ExpectedSpeakers)> Calls { get; } = new();
    public Queue<ClusterResult> Scripted { get; } = new();

    public override ClusterResult Cluster(
        IReadOnlyList<float[]> embeddings, int previousClusterCount = 0, int expectedSpeakers = 0)
    {
        Calls.Add((embeddings.Count, previousClusterCount, expectedSpeakers));
        return Scripted.Count > 0
            ? Scripted.Dequeue()
            : base.Cluster(embeddings, previousClusterCount, expectedSpeakers);
    }
}

internal static class SpeakerSegments
{
    /// <summary>A "segment" whose first sample encodes the voice direction in degrees and whose
    /// length encodes its duration — 2 s by default, i.e. eligible for clustering.</summary>
    public static float[] Seg(double degrees, double seconds = 2.0)
    {
        var samples = new float[Math.Max(1, (int)(seconds * 16000))];
        samples[0] = (float)degrees;
        return samples;
    }
}
