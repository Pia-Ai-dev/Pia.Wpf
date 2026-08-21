using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Outcome assertions over the REAL <see cref="SpeakerClusterer"/> — "n voices in, at most k labels
/// out" — which is the one class of test the suite never had. Everything else verifies that a
/// mechanism does what it was written to do, which was never in doubt.
///
/// <para>What synthetic geometry can and cannot show: it proves BOUNDING and STABILITY of the label
/// set, never attribution accuracy. <see cref="DegreeEmbeddingExtractor"/> makes similarity exactly
/// cos(Δθ), so "who got which label" here is a property of the angles chosen, not of real voices.
/// Attribution is measured against the recorded fixture (scripts/Get-SpeakerReference.ps1 plus
/// scripts/Measure-SpeakerAttribution.ps1) and nowhere else. Do not tune a threshold against these
/// numbers.</para>
/// </summary>
public class AdaptiveSpeakerOutcomeTests
{
    private static float[] Seg(double degrees, double seconds = 2.0)
        => SpeakerSegments.Seg(degrees, seconds);

    // Four voices, 25° apart: cos 25° ≈ 0.91, well above every threshold the band allows, so the
    // instant path merges them and only the dendrogram can tell them apart.
    private static readonly double[] Voices = [0, 25, 50, 75];

    // Turn order over the four voices, and the duration mix the failing meeting showed: roughly
    // three quarters clear the 2 s clustering floor, a tenth land in the 1.5–2.0 s band that is
    // embedded but never clustered, and the rest are shorter still.
    private static readonly int[] TurnOrder =
        [0, 1, 0, 2, 0, 3, 1, 0, 2, 1, 0, 3, 0, 1, 2, 0, 3, 1, 0, 2,
         0, 1, 3, 0, 2, 0, 1, 0, 3, 2, 0, 1, 0, 2, 3, 0, 1, 0, 2, 1];
    private static readonly double[] Durations =
        [3.4, 1.7, 2.6, 4.1, 1.0, 2.2, 3.0, 2.8, 1.9, 2.4, 5.2, 1.2, 3.6, 2.1, 2.9, 1.8, 3.3, 2.5, 1.1, 2.7,
         4.4, 2.3, 1.6, 3.1, 2.6, 1.0, 3.8, 2.2, 2.4, 1.3, 3.5, 2.0, 2.9, 1.7, 4.0, 2.1, 3.2, 1.4, 2.6, 3.7];

    // A sub-floor segment is mostly silence, so its embedding sits nowhere near any speaker. In 192
    // dimensions that reads as near-orthogonality; on this circle the only way to be far from all
    // four voices at once is to leave their 0–75° arc, which needs a swing of roughly 100–170°
    // (cos 66° ≈ 0.41 is the loosest threshold the band allows). A gentler ±35° would land on a
    // neighbouring voice and match, which is why an unjittered short segment proves nothing.
    // Hand-written rather than random: the suite has to be deterministic.
    private static readonly double[] SubFloorJitter =
        [162, -118, 150, -131, 171, -104, 156, -125, 167, -112, 145, -137, 159, -121];

    private static AdaptiveSpeakerIdentificationService Create()
        => new(new DegreeEmbeddingExtractor(),
            NullLogger<AdaptiveSpeakerIdentificationService>.Instance, now: null);

    /// <summary>Drives one meeting and returns every label the run ever showed plus the final set.</summary>
    private static (HashSet<string> Ever, IReadOnlyCollection<string> Final) Run(
        int expectedSpeakers, bool includeSubFloor)
    {
        using var svc = Create();
        svc.SetExpectedSpeakers(expectedSpeakers);

        var ever = new HashSet<string>(StringComparer.Ordinal);
        var labelBySegment = new Dictionary<long, string>();
        svc.SpeakersReassigned += (_, changes) =>
        {
            foreach (var c in changes)
            {
                if (c.NewLabel is not null) { labelBySegment[c.SegmentId] = c.NewLabel; ever.Add(c.NewLabel); }
                else labelBySegment.Remove(c.SegmentId);
            }
        };

        var jitter = 0;
        for (var i = 0; i < TurnOrder.Length; i++)
        {
            var seconds = Durations[i];
            var subFloor = seconds < AdaptiveSpeakerIdentificationService.MinClusterSegmentSeconds;
            if (subFloor && !includeSubFloor) continue;

            var degrees = Voices[TurnOrder[i]];
            if (subFloor) degrees += SubFloorJitter[jitter++ % SubFloorJitter.Length];

            var r = svc.IdentifyOrRegisterSegment(Seg(degrees, seconds), 16000);
            if (r.Label is null) continue;
            // A pass inside this same call may already have corrected the label; the returned one is
            // the stale pre-pass provisional, so it must not overwrite the correction.
            if (!labelBySegment.ContainsKey(r.SegmentId)) labelBySegment[r.SegmentId] = r.Label;
            ever.Add(r.Label);
        }

        return (ever, svc.KnownLabels);
    }

    [Fact]
    public void FourVoices_ProduceAtMostFiveLabels()
    {
        var (ever, final) = Run(expectedSpeakers: 4, includeSubFloor: true);

        Assert.True(ever.Count <= 5, $"registered {ever.Count} labels: {string.Join(", ", ever.Order())}");
        Assert.True(final.Count <= 4, $"ended with {final.Count} labels: {string.Join(", ", final.Order())}");
    }

    /// <summary>
    /// The direct regression test for the 17-label failure: interjections too short to cluster must
    /// not be able to grow the label set at all.
    /// </summary>
    [Fact]
    public void FourVoices_ShortInterjections_DoNotAddLabels()
    {
        var withoutThem = Run(expectedSpeakers: 4, includeSubFloor: false);
        var withThem = Run(expectedSpeakers: 4, includeSubFloor: true);

        Assert.True(
            withThem.Ever.Count <= withoutThem.Ever.Count,
            $"interjections grew the label set from {withoutThem.Ever.Count} to {withThem.Ever.Count}: " +
            string.Join(", ", withThem.Ever.Order()));
    }

    [Fact]
    public void OneVoice_StaysOneLabel()
    {
        using var svc = Create();
        var ever = new HashSet<string>(StringComparer.Ordinal);
        svc.SpeakerRegistered += (_, label) => ever.Add(label);

        for (var i = 0; i < 20; i++) svc.IdentifyOrRegisterSegment(Seg(i % 5, 2.5), 16000);

        Assert.Single(ever);
        Assert.Single(svc.KnownLabels);
    }

    [Fact]
    public void RosterCeiling_DoesNotInflate()
    {
        using var svc = Create();
        svc.SetExpectedSpeakers(6);

        foreach (var origin in new double[] { 0, 120, 240 })
            for (var i = 0; i < 5; i++) svc.IdentifyOrRegisterSegment(Seg(origin + i, 2.5), 16000);

        Assert.Equal(3, svc.KnownLabels.Count);
    }
}
