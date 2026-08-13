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

    // ---- ChooseCut: roster ceiling ---------------------------------------------------------------

    // Five competitive candidates (gaps 0.12/0.11/0.10, then two clearly out of the hysteresis
    // window) at cluster counts 6/5/4/3/2 — the ceiling picks among the competitive ones.
    private static readonly float[] LadderedGaps = { 0.18f, 0.30f, 0.41f, 0.51f, 0.595f, 0.66f };

    [Fact]
    public void ChooseCut_Ceiling_NeverInflatesTowardTheRoster()
    {
        // Best gap yields 2 clusters, a competitive noise gap yields 5. A 6-person roster (cap 7)
        // must leave the answer at 2 — silent attendees can never pull the count up.
        var seq = new[] { 0.02f, 0.05f, 0.32f, 0.34f, 0.35f, 0.64f };

        var capped = SpeakerClusterer.ChooseCut(seq, previousClusterCount: 0, expectedSpeakers: 6);

        Assert.Equal(SpeakerClusterer.ChooseCut(seq, previousClusterCount: 0), capped);
        Assert.InRange(capped, 0.36f, 0.63f);   // between 0.35 and 0.64 → yields 2 clusters
    }

    [Fact]
    public void ChooseCut_Ceiling_TakesTheLargestCompetitiveCountThatFits()
    {
        // Unconstrained the best gap gives 6 clusters. A 4-person roster (cap 5) steps down to the
        // competitive candidate at 5 — not all the way to 4.
        var uncapped = SpeakerClusterer.ChooseCut(LadderedGaps, previousClusterCount: 0);
        Assert.Equal(SpeakerClusterer.CutMin, uncapped);   // (0.18+0.30)/2 clamps up to CutMin

        Assert.Equal(0.355f, SpeakerClusterer.ChooseCut(LadderedGaps, 0, expectedSpeakers: 4), 3);
        Assert.Equal(0.46f, SpeakerClusterer.ChooseCut(LadderedGaps, 0, expectedSpeakers: 3), 3);
    }

    [Fact]
    public void ChooseCut_Ceiling_KeepsTheChoice_WhenNoCandidateFits()
    {
        // Cap 2 is below every competitive candidate (6/5/4) → the cut is left alone and Cluster's
        // force-merge guard is what finally enforces the cap.
        var cut = SpeakerClusterer.ChooseCut(LadderedGaps, previousClusterCount: 0, expectedSpeakers: 1);
        Assert.Equal(SpeakerClusterer.ChooseCut(LadderedGaps, previousClusterCount: 0), cut);
    }

    [Fact]
    public void ChooseCut_HysteresisWins_WhenItAlreadyFitsTheCeiling()
    {
        // Hysteresis runs before the ceiling: sticking at 4 is within a 5-person roster's cap (6),
        // so the ceiling leaves it untouched instead of re-deriving from the best gap.
        Assert.Equal(0.46f, SpeakerClusterer.ChooseCut(LadderedGaps, 4, expectedSpeakers: 5), 3);
        // And when hysteresis picks a count ABOVE the cap, the ceiling still pulls it down.
        Assert.Equal(0.355f, SpeakerClusterer.ChooseCut(LadderedGaps, 6, expectedSpeakers: 4), 3);
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

    private static float[][] Orthogonal(int n) => Enumerable.Range(0, n).Select(i =>
    {
        var v = new float[n];
        v[i] = 1f;
        return v;
    }).ToArray();

    [Fact]
    public void Cluster_MoreClustersThanCap_MergedDownToTwelve()
    {
        // 14 mutually-orthogonal one-hot embeddings: every merge distance is 1.0 (out of band)
        // → fallback cut accepts none → 14 singletons → the cap merges down to 12.
        var r = new SpeakerClusterer().Cluster(Orthogonal(14));

        Assert.Equal(SpeakerClusterer.MaxClusters, r.ClusterCount);
        // Force-merging must not raise the reported cut — it drives the caller's instant-match
        // threshold, which a cap merge has no business retuning.
        Assert.Equal(SpeakerClusterer.FallbackCut, r.CutDistance);
    }

    [Fact]
    public void Cluster_Ceiling_ForceMergesDownToTheRosterCap()
    {
        // Same pathological input, but a 3-person roster caps it at 4 instead of 12.
        var r = new SpeakerClusterer().Cluster(Orthogonal(14), previousClusterCount: 0, expectedSpeakers: 3);

        Assert.Equal(4, r.ClusterCount);
        Assert.Equal(SpeakerClusterer.FallbackCut, r.CutDistance);
    }

    [Fact]
    public void Cluster_Ceiling_LeavesAnUndercountedMeetingAlone()
    {
        // Three real voices under a 1-person roster (cap 2) collapse; the same input with the
        // ceiling off keeps all three. Ceiling = ceiling, and only that.
        var e = new[] { Vec(0), Vec(2), Vec(60), Vec(62), Vec(120), Vec(122) };

        Assert.Equal(3, new SpeakerClusterer().Cluster(e).ClusterCount);
        Assert.Equal(2, new SpeakerClusterer().Cluster(e, previousClusterCount: 0, expectedSpeakers: 1).ClusterCount);
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
