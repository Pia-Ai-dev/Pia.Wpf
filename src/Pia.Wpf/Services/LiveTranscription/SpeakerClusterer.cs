namespace Pia.Services.LiveTranscription;

/// <summary>
/// Result of one re-clustering pass over all journaled embeddings.
/// <see cref="AssignmentPerSegment"/>[i] is the cluster index (0..ClusterCount-1, numbered in
/// first-appearance order) of embeddings[i]. <see cref="CutDistance"/> is the cosine-DISTANCE cut
/// the pass used, clamped to the guardrail band — consumers derive the instant-match similarity
/// threshold as 1 − CutDistance.
/// </summary>
public sealed record ClusterResult(int[] AssignmentPerSegment, int ClusterCount, float CutDistance);

/// <summary>
/// Average-linkage agglomerative clustering (AHC) over L2-NORMALIZED speaker embeddings with a
/// data-derived cut: instead of a user-tuned similarity threshold, the cut falls into the largest
/// gap of the dendrogram's merge-distance sequence — the natural boundary between within-speaker
/// and between-speaker distance for THESE voices. Pure logic: no I/O, no native deps,
/// deterministic. O(n²) time (Lance–Williams + nearest-neighbor cache), O(n²) memory.
/// </summary>
public sealed class SpeakerClusterer
{
    // Guardrail band for the cut (cosine distance = 1 − cosine similarity). A cut outside this
    // band would mean an implausible speaker geometry — likely a degenerate gap — so we never cut
    // there. 0.50 distance == today's default manual threshold (sim 0.50).
    internal const float CutMin = 0.30f;
    internal const float CutMax = 0.70f;
    internal const float FallbackCut = 0.50f;
    // Two nearly-equal gaps (< this delta apart) are treated as ambiguous → prefer the cut that
    // keeps the previous pass's cluster count (label-churn dampening).
    internal const float HysteresisGapDelta = 0.03f;
    // Over-segmentation guard; matches the manual mode's max cap (12).
    internal const int MaxClusters = 12;

    /// <summary>
    /// Chooses the cut distance from the SORTED merge-distance sequence of a dendrogram with
    /// n = sortedMergeDistances.Length + 1 leaves. Candidate cuts sit between consecutive merges
    /// whose upper edge falls inside the guardrail band; the largest gap wins, with hysteresis
    /// toward <paramref name="previousClusterCount"/> on near-ties. Accepting all merges strictly
    /// below the returned cut yields the clustering.
    /// </summary>
    internal static float ChooseCut(float[] sortedMergeDistances, int previousClusterCount)
    {
        var seq = sortedMergeDistances;
        if (seq.Length == 0) return CutMin;
        if (seq[^1] < CutMin) return CutMin;            // everything is one speaker

        // Candidate i = cut between seq[i] and seq[i+1]. Accepting merges 0..i leaves
        // (seq.Length - i) clusters. Only consider candidates whose upper edge is in the band.
        List<(float Gap, float Cut, int ClusterCount)> candidates = new();
        for (int i = 0; i + 1 < seq.Length; i++)
        {
            var upper = seq[i + 1];
            if (upper < CutMin || upper > CutMax) continue;
            var cut = Math.Clamp((seq[i] + upper) / 2f, CutMin, CutMax);
            candidates.Add((upper - seq[i], cut, seq.Length - i));
        }
        if (candidates.Count == 0) return FallbackCut;

        candidates.Sort((x, y) => y.Gap.CompareTo(x.Gap));
        var best = candidates[0];
        if (previousClusterCount > 0)
        {
            foreach (var c in candidates)
            {
                if (best.Gap - c.Gap >= HysteresisGapDelta) break;   // sorted → rest are worse
                if (c.ClusterCount == previousClusterCount) return c.Cut;
            }
        }
        return best.Cut;
    }
}
