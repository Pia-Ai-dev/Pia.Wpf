using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class AdaptiveSpeakerIdentificationServiceTests
{
    private static float[] Seg(double degrees, double seconds = 2.0)
        => SpeakerSegments.Seg(degrees, seconds);

    private static AdaptiveSpeakerIdentificationService Create(
        DegreeEmbeddingExtractor? extractor = null, Func<DateTimeOffset>? now = null)
        => new(extractor ?? new DegreeEmbeddingExtractor(),
            NullLogger<AdaptiveSpeakerIdentificationService>.Instance, now);

    private static AdaptiveSpeakerIdentificationService Create(
        RecordingClusterer clusterer, Func<DateTimeOffset>? now = null)
        => new(new DegreeEmbeddingExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance, now,
            AdaptiveSpeakerIdentificationService.DefaultMaxJournaledSegments, clusterer);

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
    public void ElapsedTime_TriggersACorrectingPass_EvenBelowTheSegmentStride()
    {
        var clock = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        using var svc = Create(now: () => clock);
        var events = new List<IReadOnlyList<SpeakerReassignment>>();
        svc.SpeakersReassigned += (_, e) => events.Add(e);

        // 6 tight segments (0°–5°) → clean pass at #6 → one cluster, and the adaptive threshold
        // tightens to the clamped maximum, sim ≥ 0.60 (cut = CutMin).
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);
        Assert.Empty(events);

        // A second voice at 50° sits in the blind spot that threshold leaves: close enough to the
        // ~2.5° centroid to be swallowed instantly (sim ≈ cos 47.5° ≈ 0.676 ≥ 0.60), far enough
        // that the dendrogram splits it (the group merge lands at ≈ 0.34, above the 0.30 cut).
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(50), 16000).Label);
        Assert.Empty(events); // stride (5) not reached, latency (30 s) not elapsed → no pass yet

        // Its second segment arrives 31 s later: the instant path swallows it again (sim ≈ 0.729 to
        // the now-8.8° centroid), the ≥30 s latency trigger fires, and the pass splits both out.
        clock += TimeSpan.FromSeconds(31);
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(52), 16000).Label);

        var change = Assert.Single(events);
        Assert.All(change, c => Assert.Equal("Speaker 2", c.NewLabel));
        Assert.Equal(new long[] { 6, 7 }, change.Select(c => c.SegmentId).Order().ToArray());
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
        var extractor = new DegreeEmbeddingExtractor();
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
            new DegreeEmbeddingExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance,
            now: null, maxJournaledSegments: 8);

        for (var i = 0; i < 20; i++)
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(i % 4), 16000).Label);
    }

    // ---- roster ceiling --------------------------------------------------------------------------

    [Fact]
    public void SetExpectedSpeakers_ReachesThePass()
    {
        var clusterer = new RecordingClusterer();
        using var svc = Create(clusterer);
        svc.SetExpectedSpeakers(4);

        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        var call = Assert.Single(clusterer.Calls);
        Assert.Equal(4, call.ExpectedSpeakers);
    }

    [Fact]
    public void ExpectedSpeakers_CapsTheDistinctLabels_LeavingTheUncappedRunAlone()
    {
        Assert.Equal(3, DistinctLabelsAcrossThreeVoices(expectedSpeakers: 0));
        Assert.Equal(2, DistinctLabelsAcrossThreeVoices(expectedSpeakers: 1));
    }

    /// <summary>Three well-separated voices (0°/60°/120°), five segments each plus one more so the
    /// last pass sees the whole meeting. Returns the number of distinct labels the transcript would
    /// end up showing — provisional labels corrected by every pass, exactly as the VM tracks them.</summary>
    private static int DistinctLabelsAcrossThreeVoices(int expectedSpeakers)
    {
        using var svc = Create();
        svc.SetExpectedSpeakers(expectedSpeakers);

        var labelBySegment = new Dictionary<long, string?>();
        svc.SpeakersReassigned += (_, changes) =>
        {
            foreach (var c in changes) labelBySegment[c.SegmentId] = c.NewLabel;
        };

        var degrees = new List<double>();
        foreach (var origin in new[] { 0, 60, 120 })
            for (var i = 0; i < 5; i++) degrees.Add(origin + 2 * i);
        degrees.Add(1);

        foreach (var deg in degrees)
        {
            var r = svc.IdentifyOrRegisterSegment(Seg(deg), 16000);
            // A pass inside this same call may already have corrected the label; the returned one
            // is the stale pre-pass provisional, so it must not overwrite the correction.
            if (!labelBySegment.ContainsKey(r.SegmentId)) labelBySegment[r.SegmentId] = r.Label;
        }
        return labelBySegment.Values.Distinct().Count();
    }

    // ---- adaptive-loop hygiene -------------------------------------------------------------------

    [Fact]
    public void Pass_SeedsHysteresisWithTheLastPassCount_NotTheProvisionalLiveCount()
    {
        var clusterer = new RecordingClusterer();
        clusterer.Scripted.Enqueue(new ClusterResult(new int[6], 1, SpeakerClusterer.CutMin));
        using var svc = Create(clusterer);

        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);
        // A distant voice registers provisionally between passes, so the LIVE cluster count is now
        // 2 while the last pass reported 1. Seeding with the live count is the upward-only ratchet.
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(80), 16000).Label);
        for (var i = 1; i < 5; i++) svc.IdentifyOrRegisterSegment(Seg(80 + i), 16000);

        Assert.Equal(2, clusterer.Calls.Count);
        Assert.Equal(1, clusterer.Calls[1].PreviousClusterCount);
    }

    [Fact]
    public void Pass_ClampsTheStrictSideOfTheDerivedThreshold()
    {
        using var svc = Create();
        // A one-voice pass reports CutMin, which unclamped demands sim ≥ 0.70 — the strictest the
        // band allows, exactly when the evidence says "one speaker".
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        // 49.5° off the centroid → sim ≈ 0.65: inside the clamped 0.60 gate, outside 0.70.
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(52), 16000).Label);
    }

    [Fact]
    public void Pass_ClampsTheGlueSideOfTheDerivedThreshold()
    {
        var clusterer = new RecordingClusterer();
        clusterer.Scripted.Enqueue(new ClusterResult(new int[6], 1, SpeakerClusterer.CutMax));
        using var svc = Create(clusterer);

        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        // A CutMax pass would derive a glue-everything sim ≥ 0.30; the clamp holds at 0.40, so a
        // genuinely distant voice (67.5° off the centroid → sim ≈ 0.38) still gets its own label.
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(70), 16000).Label);
    }

    [Fact]
    public void Pass_RecyclesAnOrphanedLabel_InsteadOfMintingAFreshNumber()
    {
        // A rebuild that strands one stable id while leaving two new clusters unmatched: the freed
        // "Speaker 2" is reused rather than the numbering running away to "Speaker 4".
        var clusterer = new RecordingClusterer();
        clusterer.Scripted.Enqueue(new ClusterResult(new[] { 0, 0, 1, 2, 2, 0 }, 3, 0.40f));
        using var svc = Create(clusterer);
        var events = new List<IReadOnlyList<SpeakerReassignment>>();
        svc.SpeakersReassigned += (_, e) => events.Add(e);

        foreach (var deg in new[] { 0.0, 1, 2, 3, 4 }) svc.IdentifyOrRegisterSegment(Seg(deg), 16000);
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(90), 16000).Label);

        var labels = Assert.Single(events).Select(c => c.NewLabel).Distinct().Order();
        Assert.Equal(new[] { "Speaker 1", "Speaker 2", "Speaker 3" }, labels);
    }

    // ---- duration gating -------------------------------------------------------------------------

    [Fact]
    public void Pass_SkippedWhileEveryEmbeddingIsSubFloor_KeepingProvisionalState()
    {
        var clusterer = new RecordingClusterer();
        using var svc = Create(clusterer);

        // Ten 1 s interjections clear the stride and the segment count but never the eligible
        // warm-up — a pass here would rebuild the label maps from an empty clustering. With no
        // cluster yet to match, they are unplaceable rather than a speaker of their own.
        for (var i = 0; i < 10; i++)
            Assert.Null(svc.IdentifyOrRegisterSegment(Seg(i, seconds: 1.0), 16000).Label);

        Assert.Empty(clusterer.Calls);
        Assert.Empty(svc.KnownLabels);
    }

    [Fact]
    public void Pass_ExcludesSubFloorSegments_ButTheyKeepTheirLabels()
    {
        var clusterer = new RecordingClusterer();
        using var svc = Create(clusterer);

        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);
        foreach (var deg in new[] { 0.0, 1, 2 })
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(deg, seconds: 1.0), 16000).Label);
        for (var i = 0; i < 5; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        Assert.Equal(2, clusterer.Calls.Count);
        // The second pass fires on the fifth segment since the first, i.e. 11 journaled — of which
        // 8 are eligible: the interjections took the label but stayed out of the dendrogram.
        Assert.Equal(8, clusterer.Calls[1].Inputs);
    }

    [Fact]
    public void SubFloorSegment_TakesBestMatch_WithoutMintingOrMovingTheCentroid()
    {
        using var svc = Create();
        // One voice at 0°, then a 1 s segment 50° off: sim ≈ cos 50° ≈ 0.64, over the 0.60 ceiling
        // the derived threshold is clamped to, so it matches rather than minting.
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(0), 16000);
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(50, seconds: 1.0), 16000).Label);
        Assert.Single(svc.KnownLabels);

        // The centroid did not follow it: a full-length segment 55° the other way still misses, which
        // it would not have if the centroid had been dragged toward 50°.
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(-55), 16000).Label);
    }

    [Fact]
    public void SubFloorSegment_MintsOnlyWhenNoClusterExists()
    {
        using var svc = Create();

        // Nothing to match against, and warm-up has not run: no label, and no cluster invented.
        Assert.Null(svc.IdentifyOrRegisterSegment(Seg(0, seconds: 1.0), 16000).Label);
        Assert.Empty(svc.KnownLabels);

        // A far-off sub-floor segment stays unplaceable even once a voice exists.
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(0), 16000).Label);
        Assert.Null(svc.IdentifyOrRegisterSegment(Seg(170, seconds: 1.0), 16000).Label);
        Assert.Single(svc.KnownLabels);
    }

    // ---- roster ceiling on the provisional path ---------------------------------------------------

    [Fact]
    public void ProvisionalPath_AtTheRosterCeiling_ForcesBestMatchInsteadOfMinting()
    {
        using var svc = Create();
        svc.SetExpectedSpeakers(2);   // ceiling = 2 + ExpectedSpeakerSlack = 3

        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(0), 16000).Label);
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(120), 16000).Label);
        Assert.Equal("Speaker 3", svc.IdentifyOrRegisterSegment(Seg(240), 16000).Label);

        // A fourth distinct voice would exceed the roster: it takes its nearest match instead.
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(60), 16000).Label);
        Assert.Equal(3, svc.KnownLabels.Count);
    }

    [Fact]
    public void ProvisionalPath_BelowTheCeiling_IsUnchanged()
    {
        using var svc = Create();
        svc.SetExpectedSpeakers(4);   // ceiling = 5

        foreach (var deg in new double[] { 0, 72, 144, 216 })
            svc.IdentifyOrRegisterSegment(Seg(deg), 16000);

        Assert.Equal(4, svc.KnownLabels.Count);
    }

    [Fact]
    public void ProvisionalPath_WithNoRoster_IsUnchanged()
    {
        using var svc = Create();

        foreach (var deg in new double[] { 0, 72, 144, 216, 288 })
            svc.IdentifyOrRegisterSegment(Seg(deg), 16000);

        Assert.Equal(5, svc.KnownLabels.Count);
    }

    [Fact]
    public void Pass_HasNoClusterDefinedOnlyBySubFloorSegments()
    {
        using var svc = Create();
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        // A participant who only ever produced a short interjection cannot define a cluster: it
        // would be excluded from every dendrogram and so unreachable by every correction.
        Assert.Null(svc.IdentifyOrRegisterSegment(Seg(90, seconds: 1.0), 16000).Label);
        Assert.Single(svc.KnownLabels);

        for (var i = 0; i < 5; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);
        Assert.Single(svc.KnownLabels);
    }
}
