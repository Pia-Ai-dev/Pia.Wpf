using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class SpeakerClustererTests
{
    // ---- ChooseCut (pure cut selection over sorted merge distances) ----------------------------

    [Fact]
    public void ChooseCut_AllMergesBelowBand_ReturnsCutMin_SingleCluster()
    {
        // Everything merges tightly → one speaker; cut reported as CutMin keeps the derived
        // instant-match threshold strict (sim ≥ 0.70), not degenerate.
        var cut = SpeakerClusterer.ChooseCut(new[] { 0.01f, 0.02f, 0.03f }, previousClusterCount: 0);
        Assert.Equal(SpeakerClusterer.CutMin, cut);
    }

    [Fact]
    public void ChooseCut_ClearGapIntoBand_CutsInsideGap()
    {
        // Within-speaker merges ~0.03, one between-speaker merge at 0.50 → cut lands strictly
        // between 0.04 and 0.50 (midpoint clamped up to CutMin).
        var cut = SpeakerClusterer.ChooseCut(new[] { 0.02f, 0.04f, 0.50f }, previousClusterCount: 0);
        Assert.InRange(cut, 0.04f + 0.001f, 0.50f - 0.001f);
        Assert.True(cut >= SpeakerClusterer.CutMin);
    }

    [Fact]
    public void ChooseCut_NoMergeInBand_FallsBackToDefault()
    {
        // Upper edges 0.90/0.95 are above CutMax → no candidate → today's default 0.50.
        var cut = SpeakerClusterer.ChooseCut(new[] { 0.02f, 0.90f, 0.95f }, previousClusterCount: 0);
        Assert.Equal(SpeakerClusterer.FallbackCut, cut);
    }

    [Fact]
    public void ChooseCut_AmbiguousGaps_PrefersPreviousClusterCount()
    {
        // Candidates: i=0 (upper 0.33, gap 0.31 → 4 clusters) and i=2 (upper 0.64, gap 0.29
        // → 2 clusters). Gap difference 0.02 < HysteresisGapDelta → with previousClusterCount=2
        // the 2-cluster cut wins; without a previous count the larger gap wins.
        var seq = new[] { 0.02f, 0.33f, 0.35f, 0.64f };

        var sticky = SpeakerClusterer.ChooseCut(seq, previousClusterCount: 2);
        Assert.InRange(sticky, 0.36f, 0.63f);   // between 0.35 and 0.64 → yields 2 clusters

        var fresh = SpeakerClusterer.ChooseCut(seq, previousClusterCount: 0);
        Assert.InRange(fresh, SpeakerClusterer.CutMin, 0.32f); // below 0.33 → yields 4 clusters
    }

    // ---- Cluster (geometric end-to-end) ---------------------------------------------------------

    private static float[] Vec(double degrees)
    {
        var r = Math.PI * degrees / 180.0;
        return new[] { (float)Math.Cos(r), (float)Math.Sin(r) };
    }

    [Fact]
    public void Cluster_TwoSpeakersSixtyDegreesApart_TwoClusters()
    {
        var e = new[] { Vec(0), Vec(2), Vec(4), Vec(60), Vec(62), Vec(64) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.AssignmentPerSegment[0], r.AssignmentPerSegment[1]);
        Assert.Equal(r.AssignmentPerSegment[0], r.AssignmentPerSegment[2]);
        Assert.Equal(r.AssignmentPerSegment[3], r.AssignmentPerSegment[4]);
        Assert.Equal(r.AssignmentPerSegment[3], r.AssignmentPerSegment[5]);
        Assert.NotEqual(r.AssignmentPerSegment[0], r.AssignmentPerSegment[3]);
        // First-appearance numbering: segment 0's cluster is 0.
        Assert.Equal(0, r.AssignmentPerSegment[0]);
    }

    [Fact]
    public void Cluster_SingleSpeaker_OneCluster_ReportsCutMin()
    {
        var e = new[] { Vec(0), Vec(1), Vec(2), Vec(3) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(1, r.ClusterCount);
        Assert.All(r.AssignmentPerSegment, a => Assert.Equal(0, a));
        Assert.Equal(SpeakerClusterer.CutMin, r.CutDistance);
    }

    [Fact]
    public void Cluster_ThreeSpeakers_ThreeClusters()
    {
        var e = new[] { Vec(0), Vec(2), Vec(55), Vec(57), Vec(115), Vec(117) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(3, r.ClusterCount);
        Assert.Equal(r.AssignmentPerSegment[0], r.AssignmentPerSegment[1]);
        Assert.Equal(r.AssignmentPerSegment[2], r.AssignmentPerSegment[3]);
        Assert.Equal(r.AssignmentPerSegment[4], r.AssignmentPerSegment[5]);
        Assert.Equal(3, r.AssignmentPerSegment.Distinct().Count());
    }

    [Fact]
    public void Cluster_OutlierFirstSegment_StillJoinsItsSpeaker()
    {
        // The "poisoned first impression": segment 0 is off-center for speaker A but far from B —
        // a full re-cluster puts it with A. This is the self-healing property the feature promises.
        var e = new[] { Vec(10), Vec(0), Vec(2), Vec(4), Vec(60), Vec(62) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.AssignmentPerSegment[1], r.AssignmentPerSegment[0]);
    }

    [Fact]
    public void Cluster_MoreClustersThanCap_MergedDownToTwelve()
    {
        // 14 mutually-orthogonal one-hot embeddings: every merge distance is 1.0 (out of band)
        // → fallback cut accepts none → 14 singletons → the cap merges down to 12.
        var e = Enumerable.Range(0, 14).Select(i =>
        {
            var v = new float[14];
            v[i] = 1f;
            return v;
        }).ToArray();
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(SpeakerClusterer.MaxClusters, r.ClusterCount);
        Assert.True(r.CutDistance <= SpeakerClusterer.CutMax); // reported cut stays in band
    }

    [Fact]
    public void Cluster_EdgeCases_EmptyAndSingle()
    {
        var empty = new SpeakerClusterer().Cluster(Array.Empty<float[]>());
        Assert.Equal(0, empty.ClusterCount);
        Assert.Empty(empty.AssignmentPerSegment);

        var one = new SpeakerClusterer().Cluster(new[] { Vec(0) });
        Assert.Equal(1, one.ClusterCount);
        Assert.Equal(new[] { 0 }, one.AssignmentPerSegment);
    }
}
