using System.IO;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>The bench itself needs a recording, but the arithmetic that decides which defect is worth
/// fixing does not — and an untested diagnostic is not evidence.</summary>
public class DiarizationOracleTests
{
    private static float[] Voice(double degrees)
    {
        var r = Math.PI * degrees / 180.0;
        return [(float)Math.Cos(r), (float)Math.Sin(r)];
    }

    private static List<LabelledSegment> TwoSeparableVoices() =>
    [
        new("A", Voice(0), 2.0),
        new("B", Voice(88), 2.0),
        new("A", Voice(2), 2.0),
        new("B", Voice(90), 2.0),
        new("A", Voice(4), 2.0),
        new("B", Voice(92), 2.0),
    ];

    [Fact]
    public void Similarity_SeparatesSameVoiceFromDifferentVoices()
    {
        var stats = DiarizationOracle.Similarity(TwoSeparableVoices());

        Assert.True(stats.IntraMean > stats.InterMean, $"intra {stats.IntraMean} vs inter {stats.InterMean}");
        Assert.Equal(6, stats.IntraPairs);
        Assert.Equal(9, stats.InterPairs);
        // Orthogonal voices are perfectly separable, so some fixed threshold makes no pair errors.
        Assert.Equal(0.0, stats.PairErrorRate);
    }

    [Fact]
    public void NearestCentroid_ScoresOnlyWhatItDidNotEnrollOn()
    {
        var result = DiarizationOracle.NearestCentroid(TwoSeparableVoices(), enrollSeconds: 2.0);

        // One segment per speaker is spent on enrollment; the remaining four are the score.
        Assert.Equal(4, result.Total);
        Assert.Equal(1.0, result.BySegment);
        Assert.Equal(8.0, result.TotalSeconds);
    }

    [Fact]
    public void NearestCentroid_ReportsASpeakerTheEnrollmentBudgetSwallowed()
    {
        // B speaks for less than the enrollment budget, so none of B is ever scored — and a pooled
        // number that hides that is not a bound on B at all.
        List<LabelledSegment> lopsided =
        [
            new("A", Voice(0), 20.0),
            new("B", Voice(90), 2.0),
            new("A", Voice(2), 20.0),
            new("A", Voice(4), 20.0),
        ];

        var result = DiarizationOracle.NearestCentroid(lopsided, enrollSeconds: 30);

        var b = Assert.Single(result.PerSpeaker!, t => t.Speaker == "B");
        Assert.Equal(0, b.Scored);
        Assert.Equal(2.0, b.EnrolledSeconds);
        var a = Assert.Single(result.PerSpeaker!, t => t.Speaker == "A");
        Assert.Equal(1, a.Scored);
        Assert.Equal(40.0, a.EnrolledSeconds);
    }

    [Fact]
    public void NearestCentroid_MisattributesVoicesTheEmbeddingCannotSeparate()
    {
        // Two "speakers" one degree apart: perfect enrollment cannot fix an embedding that does not
        // distinguish them, which is the whole point of measuring this bound.
        List<LabelledSegment> confusable =
        [
            new("A", Voice(0), 2.0),
            new("B", Voice(1), 2.0),
            new("A", Voice(2), 2.0),
            new("B", Voice(3), 2.0),
        ];

        var result = DiarizationOracle.NearestCentroid(confusable, enrollSeconds: 2.0);

        Assert.Equal(2, result.Total);
        Assert.True(result.BySegment < 1.0, "confusable voices must not score perfectly");
    }

    [Fact]
    public void PinnedClusterer_SplitsSeparableVoices_AndScoresOneToOne()
    {
        var result = DiarizationOracle.PinnedClusterer(TwoSeparableVoices(), expectedSpeakers: 2);

        Assert.Equal(6, result.Total);
        Assert.Equal(1.0, result.BySegment);
    }

    [Fact]
    public void TruthAt_RefusesOverlapSilenceAndUnreadableRanges()
    {
        var reference = new SpeakerReference(
            DurationSeconds: 100,
            Speakers: ["A", "B"],
            Intervals:
            [
                new RefInterval(0, 10, ["A"]),
                new RefInterval(10, 12, ["A", "B"]),
                new RefInterval(20, 30, ["B"]),
            ],
            InvalidRanges: [new RefRange(5, 6)]);

        Assert.Equal("A", DiarizationOracle.TruthAt(reference, 3));
        Assert.Equal("B", DiarizationOracle.TruthAt(reference, 25));
        Assert.Null(DiarizationOracle.TruthAt(reference, 11));   // two people at once
        Assert.Null(DiarizationOracle.TruthAt(reference, 15));   // no interval at all
        Assert.Null(DiarizationOracle.TruthAt(reference, 5.5));  // the tile grid had moved
    }
}

public class BenchEmbeddingCacheTests
{
    [Fact]
    public void Cache_RoundTripsByStreamPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-bench-{Guid.NewGuid():N}.bin");
        try
        {
            var cache = new EmbeddingCache();
            cache.Put(0, 32000, [1f, 2f, 3f]);
            cache.Put(48000, 24000, [4f, 5f, 6f]);
            cache.Save(path);

            var reloaded = EmbeddingCache.Load(path);

            Assert.Equal(2, reloaded.Count);
            Assert.True(reloaded.TryGet(48000, 24000, out var vector));
            Assert.Equal([4f, 5f, 6f], vector);
            // A segment that moved is a miss, so changing the VAD cannot silently reuse stale vectors.
            Assert.False(reloaded.TryGet(48001, 24000, out _));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Cache_OwnsItsVectors_WhenTheCallerWipesThem()
    {
        var cache = new EmbeddingCache();
        var vector = new float[] { 1f, 2f, 3f };
        cache.Put(0, 32000, vector);

        // What the identification service does to every embedding it holds when it disposes.
        Array.Clear(vector);
        Assert.True(cache.TryGet(0, 32000, out var stored));
        Assert.Equal([1f, 2f, 3f], stored);

        Array.Clear(stored);
        Assert.True(cache.TryGet(0, 32000, out var again));
        Assert.Equal([1f, 2f, 3f], again);
    }

    [Fact]
    public void CachedExtractor_ComputesOnlyOnAMiss()
    {
        var cache = new EmbeddingCache();
        var inner = new CountingExtractor();
        using var extractor = new CachedEmbeddingExtractor(cache, () => inner);
        var samples = new float[24000];

        extractor.Current = (0, samples.Length);
        extractor.Compute(samples, 16000);
        extractor.Compute(samples, 16000);
        extractor.Current = (24000, samples.Length);
        extractor.Compute(samples, 16000);

        Assert.Equal(2, inner.Calls);
        Assert.Equal(2, extractor.Misses);
    }

    [Fact]
    public void CachedExtractor_RefusesAMismatchedCurrentSegment()
    {
        var extractor = new CachedEmbeddingExtractor(new EmbeddingCache(), () => new CountingExtractor())
        {
            Current = (0, 999),
        };

        // Silently caching under the wrong key would poison every later run.
        Assert.Throws<InvalidOperationException>(() => extractor.Compute(new float[24000], 16000));
    }

    private sealed class CountingExtractor : IEmbeddingExtractor
    {
        public int Calls { get; private set; }
        public int Dim => 3;

        public float[] Compute(float[] samples, int sampleRate)
        {
            Calls++;
            return [1f, 0f, 0f];
        }

        public void Dispose() { }
    }
}
