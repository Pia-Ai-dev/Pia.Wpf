using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class AdaptiveSpeakerIdentificationServiceTests
{
    private sealed class FakeExtractor : IEmbeddingExtractor
    {
        public int Dim => 2;
        public bool Disposed;
        public float[] Compute(float[] samples, int sampleRate)
        {
            var r = Math.PI * samples[0] / 180.0;
            return new[] { (float)Math.Cos(r), (float)Math.Sin(r) };
        }
        public void Dispose() => Disposed = true;
    }

    /// <summary>A "segment" whose first sample encodes the voice direction in degrees.</summary>
    private static float[] Seg(double degrees) => new[] { (float)degrees };

    private static AdaptiveSpeakerIdentificationService Create(
        FakeExtractor? extractor = null, Func<DateTimeOffset>? now = null)
        => new(extractor ?? new FakeExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance, now);

    [Fact]
    public void FirstSegment_RegistersSpeaker1_AndRaisesSpeakerRegistered()
    {
        using var svc = Create();
        var registered = new List<string>();
        svc.SpeakerRegistered += (_, label) => registered.Add(label);

        var r = svc.IdentifyOrRegisterSegment(Seg(0), 16000);

        Assert.Equal("Speaker 1", r.Label);
        Assert.Equal(0, r.SegmentId);
        Assert.Equal(new[] { "Speaker 1" }, registered);
    }

    [Fact]
    public void CloseSegments_ShareTheLabel_DistantSegmentGetsANewOne()
    {
        using var svc = Create();

        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(0), 16000).Label);
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(3), 16000).Label);
        // 80° apart → sim cos80 ≈ 0.17 < 0.50 initial threshold → new speaker.
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(80), 16000).Label);
    }

    [Fact]
    public void ReclusterPass_SplitsAProvisionallyMergedVoice_AndEmitsOnlyChangedSegments()
    {
        using var svc = Create();
        var events = new List<IReadOnlyList<SpeakerReassignment>>();
        svc.SpeakersReassigned += (_, e) => events.Add(e);

        // Speaker A: 3 segments around 0°. Speaker B: around 55° — similarity to A's ~2° CENTROID
        // ≈ cos 53° ≈ 0.60 ≥ 0.50 (initial threshold), so the instant path wrongly merges B into
        // "Speaker 1" (the exact first-impression failure).
        foreach (var deg in new[] { 0.0, 2, 4 })
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(deg), 16000).Label);
        foreach (var deg in new[] { 55.0, 57 })
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(deg), 16000).Label);

        // 6th segment reaches warm-up; the pass re-clusters and splits B out retroactively.
        // Discard the result: its label is the stale pre-pass provisional one by design.
        _ = svc.IdentifyOrRegisterSegment(Seg(59), 16000);

        var change = Assert.Single(events);
        Assert.All(change, c => Assert.Equal("Speaker 2", c.NewLabel));
        Assert.Equal(new long[] { 3, 4, 5 }, change.Select(c => c.SegmentId).OrderBy(x => x).ToArray());
        // Earliest-segment tie-break keeps "Speaker 1" on the earlier voice.
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(1), 16000).Label);
    }

    [Fact]
    public void ElapsedTime_TriggersAHealingPass_EvenBelowTheSegmentStride()
    {
        var clock = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        using var svc = Create(now: () => clock);
        var events = new List<IReadOnlyList<SpeakerReassignment>>();
        svc.SpeakersReassigned += (_, e) => events.Add(e);

        // 6 tight segments (0°–5°) → clean pass at #6 → one cluster, and the adaptive threshold
        // TIGHTENS to sim ≥ 0.70 (cut = CutMin). Note: after such a pass, a "wrong provisional
        // merge then split" scenario is unreachable by construction (a provisional merge needs
        // centroid sim ≥ 0.70 ⇒ cross-cluster distance < 0.30 ⇒ below the guardrail band), so the
        // healable error in this regime is the opposite one: a spurious over-SPLIT.
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);
        Assert.Empty(events);

        // Off-center segment of the SAME voice at 50°: sim to the ~2.5° centroid ≈ cos 47.5°
        // ≈ 0.676 < 0.70 → spuriously registers as provisional "Speaker 2" (segment id 6).
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(50), 16000).Label);
        Assert.Empty(events); // stride (5) not reached, latency (30 s) not elapsed → no pass yet

        // Bridging segment at 25° arrives 31 s later: the instant path joins "Speaker 1"
        // (sim cos 22.5° ≈ 0.924 beats the 50° centroid's cos 25° ≈ 0.906), the ≥30 s latency
        // trigger fires, and average linkage now chains 0–5° ∪ 25° ∪ 50° entirely below CutMin
        // (the 50° merge lands at ≈ 0.292) → one cluster → the spurious "Speaker 2" heals back.
        clock += TimeSpan.FromSeconds(31);
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(25), 16000).Label);

        var change = Assert.Single(events);
        var single = Assert.Single(change);
        Assert.Equal(6, single.SegmentId);
        Assert.Equal("Speaker 1", single.NewLabel);
    }

    [Fact]
    public void Rename_SurvivesReclusterPasses()
    {
        using var svc = Create();
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        Assert.True(svc.Rename("Speaker 1", "Alice"));

        // New distinct voice + enough segments for another pass.
        for (var i = 0; i < 5; i++) svc.IdentifyOrRegisterSegment(Seg(80 + i), 16000);
        Assert.Equal("Alice", svc.IdentifyOrRegisterSegment(Seg(2), 16000).Label);
    }

    [Fact]
    public void Reset_RestartsNumbering_AndStopsReassignments()
    {
        using var svc = Create();
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        svc.Reset();

        var r = svc.IdentifyOrRegisterSegment(Seg(0), 16000);
        Assert.Equal("Speaker 1", r.Label);
        // Segment ids stay monotonic across Reset so stale UI reassignments can never collide.
        Assert.Equal(6, r.SegmentId);
    }

    [Fact]
    public void Dispose_DisposesTheExtractor()
    {
        var extractor = new FakeExtractor();
        var svc = Create(extractor);
        svc.IdentifyOrRegisterSegment(Seg(0), 16000);
        svc.Dispose();
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public void IdentifyOrRegisterWithEmbedding_ReturnsAUnitEmbedding()
    {
        using var svc = Create();
        var (label, embedding) = svc.IdentifyOrRegisterWithEmbedding(Seg(0), 16000);
        Assert.Equal("Speaker 1", label);
        Assert.Equal(1f, embedding[0] * embedding[0] + embedding[1] * embedding[1], 3);
    }

    [Fact]
    public void JournalCap_DropsOldest_WithoutBreakingLabeling()
    {
        using var svc = new AdaptiveSpeakerIdentificationService(
            new FakeExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance,
            now: null, maxJournaledSegments: 8);

        for (var i = 0; i < 20; i++)
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(i % 4), 16000).Label);
    }
}
