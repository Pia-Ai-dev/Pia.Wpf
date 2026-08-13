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
/// <remarks>Overridable so tests can observe what a re-cluster pass asks for.</remarks>
public class SpeakerClusterer
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
    // Roster head counts are display-name-deduped, so a meeting-room device undercounts its humans;
    // the ceiling sits one above the observed roster.
    internal const int ExpectedSpeakerSlack = 1;

    /// <summary>
    /// Chooses the cut distance from the SORTED merge-distance sequence of a dendrogram with
    /// n = sortedMergeDistances.Length + 1 leaves. Candidate cuts sit between consecutive merges
    /// whose upper edge falls inside the guardrail band; the largest gap wins, with hysteresis
    /// toward <paramref name="previousClusterCount"/> on near-ties, and
    /// <paramref name="expectedSpeakers"/> (0 = off) applied last as a downward-only ceiling.
    /// Accepting all merges strictly below the returned cut yields the clustering.
    /// </summary>
    internal static float ChooseCut(
        float[] sortedMergeDistances, int previousClusterCount, int expectedSpeakers = 0)
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
        var chosen = best;
        if (previousClusterCount > 0)
        {
            foreach (var c in candidates)
            {
                if (best.Gap - c.Gap >= HysteresisGapDelta) break;   // sorted → rest are worse
                if (c.ClusterCount == previousClusterCount) { chosen = c; break; }
            }
        }

        // Roster ceiling, downward only: at or below the cap the choice is untouched, so a meeting
        // with silent attendees can never be pulled UP toward the roster size. Above it, the most
        // finely-split competitive candidate that still fits wins; if none fits, the force-merge
        // guard in Cluster finishes the job.
        var cap = expectedSpeakers > 0 ? expectedSpeakers + ExpectedSpeakerSlack : 0;
        if (cap > 0 && chosen.ClusterCount > cap)
        {
            (float Gap, float Cut, int ClusterCount)? capped = null;
            foreach (var c in candidates)
            {
                if (best.Gap - c.Gap >= HysteresisGapDelta) break;
                if (c.ClusterCount > cap) continue;
                if (capped is null || c.ClusterCount > capped.Value.ClusterCount) capped = c;
            }
            if (capped is not null) chosen = capped.Value;
        }
        return chosen.Cut;
    }

    /// <summary>
    /// Clusters L2-normalized embeddings (caller's contract) by average-linkage AHC and the
    /// <see cref="ChooseCut"/> policy. <paramref name="previousClusterCount"/> (0 = none) feeds
    /// the hysteresis and must be the previous PASS's count, not a live cluster tally.
    /// Assignments are numbered 0..k-1 in first-appearance order so callers get stable,
    /// comparable indexes for the same input. <paramref name="expectedSpeakers"/> (0 = off) caps
    /// the result at <see cref="ExpectedSpeakerSlack"/> above the known roster size.
    /// </summary>
    public virtual ClusterResult Cluster(
        IReadOnlyList<float[]> embeddings, int previousClusterCount = 0, int expectedSpeakers = 0)
    {
        int n = embeddings.Count;
        if (n == 0) return new ClusterResult(Array.Empty<int>(), 0, CutMin);
        if (n == 1) return new ClusterResult(new[] { 0 }, 1, CutMin);

        var merges = BuildDendrogram(embeddings);

        // Average linkage is monotonic (reducible), so merge order == sorted order; sort
        // defensively anyway so ChooseCut's contract is honored under float noise.
        var sorted = new float[merges.Count];
        for (int i = 0; i < merges.Count; i++) sorted[i] = merges[i].Distance;
        Array.Sort(sorted);

        var cut = ChooseCut(sorted, previousClusterCount, expectedSpeakers);

        // Accept merges strictly below the cut via union-find over representative indexes.
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        int clusters = n;
        foreach (var m in merges)
        {
            if (m.Distance >= cut) continue;
            var (ra, rb) = (Find(m.A), Find(m.B));
            if (ra == rb) continue;
            parent[rb] = ra;
            clusters--;
        }

        // Over-segmentation guard: keep merging (cheapest remaining first — merges are already in
        // ascending order) until the cap is met. The reported cut deliberately does NOT follow
        // these merges — it drives the caller's instant-match threshold, which a cap merge must
        // not silently retune.
        var cap = expectedSpeakers > 0
            ? Math.Min(MaxClusters, expectedSpeakers + ExpectedSpeakerSlack)
            : MaxClusters;
        if (clusters > cap)
        {
            foreach (var m in merges)
            {
                if (clusters <= cap) break;
                var (ra, rb) = (Find(m.A), Find(m.B));
                if (ra == rb) continue;
                parent[rb] = ra;
                clusters--;
            }
        }

        // Root → 0..k-1 in first-appearance order.
        var assignment = new int[n];
        var indexByRoot = new Dictionary<int, int>(clusters);
        for (int i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!indexByRoot.TryGetValue(root, out var idx))
            {
                idx = indexByRoot.Count;
                indexByRoot[root] = idx;
            }
            assignment[i] = idx;
        }

        return new ClusterResult(assignment, indexByRoot.Count, Math.Clamp(cut, CutMin, CutMax));
    }

    /// <summary>
    /// Average-linkage dendrogram via Lance–Williams updates with a per-row nearest-neighbor
    /// cache (O(n²) average). Returns the n−1 merges in merge order; A/B are representative
    /// ORIGINAL segment indexes (B folds into A).
    /// </summary>
    private static List<(int A, int B, float Distance)> BuildDendrogram(IReadOnlyList<float[]> embeddings)
    {
        int n = embeddings.Count;
        var dist = new float[n][];
        for (int i = 0; i < n; i++) dist[i] = new float[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var d = 1f - Dot(embeddings[i], embeddings[j]);
                dist[i][j] = d;
                dist[j][i] = d;
            }
        }

        var active = new bool[n];
        Array.Fill(active, true);
        var size = new int[n];
        Array.Fill(size, 1);
        var nn = new int[n];
        var nnd = new float[n];

        void Refresh(int i)
        {
            var best = float.PositiveInfinity;
            var bi = -1;
            var row = dist[i];
            for (int j = 0; j < n; j++)
            {
                if (j == i || !active[j]) continue;
                if (row[j] < best) { best = row[j]; bi = j; }
            }
            nn[i] = bi;
            nnd[i] = best;
        }
        for (int i = 0; i < n; i++) Refresh(i);

        var merges = new List<(int A, int B, float Distance)>(n - 1);
        for (int step = 0; step < n - 1; step++)
        {
            var best = float.PositiveInfinity;
            int a = -1;
            for (int i = 0; i < n; i++)
            {
                if (active[i] && nnd[i] < best) { best = nnd[i]; a = i; }
            }
            var b = nn[a];
            merges.Add((a, b, best));

            // Lance–Williams average linkage: d(k, a∪b) = (|a|·d(k,a) + |b|·d(k,b)) / (|a|+|b|).
            var (sa, sb) = (size[a], size[b]);
            for (int k = 0; k < n; k++)
            {
                if (!active[k] || k == a || k == b) continue;
                var d = (sa * dist[a][k] + sb * dist[b][k]) / (sa + sb);
                dist[a][k] = d;
                dist[k][a] = d;
            }
            size[a] += size[b];
            active[b] = false;

            // The merged row changed and rows pointing at a or b went stale; averages can only
            // grow past cached minima, so other caches stay valid.
            Refresh(a);
            for (int k = 0; k < n; k++)
            {
                if (active[k] && k != a && (nn[k] == a || nn[k] == b)) Refresh(k);
            }
        }
        return merges;
    }

    private static float Dot(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}
