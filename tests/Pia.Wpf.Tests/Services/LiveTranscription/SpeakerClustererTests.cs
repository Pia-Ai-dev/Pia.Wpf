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
}
