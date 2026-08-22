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
/// Split-candidate pass. A global cut optimises the whole partition, so two voices that are close to
/// each other but far from everyone else are merged by a cut that is right for every other pair; this
/// re-asks the question one cluster at a time. <see cref="Margin"/> 0 leaves the partition alone, which
/// is the shipping behaviour.
/// </summary>
/// <param name="Margin">Required lead of the tighter half's self-similarity over the two halves'
/// cross-similarity — the same statistic the bench reports for a recording's closest pair.</param>
/// <param name="ExtraSlots">Clusters a split may add beyond the roster ceiling. At 0 a meeting already
/// at the ceiling can never be split, however strong the evidence.</param>
/// <param name="AbsorbBelow">Clusters holding fewer segments than this are folded into their nearest
/// neighbour before splits are considered, so a split can take a slot a fragment was holding instead
/// of needing a new one.</param>
/// <param name="AbsorbAfterSplit">Absorb after splitting rather than before, so a split pays for its
/// slot by evicting a fragment instead of being handed one. Absorbing first also folds the fragment
/// INTO the cluster that is about to be split, which can be what stops it splitting.</param>
public sealed record SpeakerSplitOptions(
    float Margin = 0f, int MinSegments = 8, int MinHalf = 3, int ExtraSlots = 0, int AbsorbBelow = 0,
    bool AbsorbAfterSplit = false)
{
    public static SpeakerSplitOptions Off { get; } = new();
}

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
    private readonly SpeakerSplitOptions _split;

    public SpeakerClusterer() : this(null)
    {
    }

    public SpeakerClusterer(SpeakerSplitOptions? split) => _split = split ?? SpeakerSplitOptions.Off;

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

        var count = indexByRoot.Count;
        if (_split.Margin > 0 || _split.AbsorbBelow > 0)
            count = ApplySplitCandidates(embeddings, assignment, count, cap);

        return new ClusterResult(assignment, count, Math.Clamp(cut, CutMin, CutMax));
    }

    /// <summary>
    /// Re-asks the 2-way question inside each cluster the global cut produced, keeping a split only
    /// when the halves separate by <see cref="SpeakerSplitOptions.Margin"/>. Splits are applied best
    /// margin first and stop at the roster ceiling, so a split competes for a slot rather than
    /// inflating the label count past what the roster allows. Mutates
    /// <paramref name="assignment"/> and returns the new cluster count.
    /// </summary>
    private int ApplySplitCandidates(
        IReadOnlyList<float[]> embeddings, int[] assignment, int clusterCount, int cap)
    {
        if (_split.AbsorbBelow > 0 && !_split.AbsorbAfterSplit)
            clusterCount = AbsorbFragments(embeddings, assignment, clusterCount);
        if (_split.Margin <= 0)
            return _split.AbsorbBelow > 0 && _split.AbsorbAfterSplit
                ? AbsorbFragments(embeddings, assignment, clusterCount)
                : clusterCount;

        var budget = Math.Min(cap + _split.ExtraSlots, MaxClusters) - clusterCount;
        if (budget <= 0)
            return _split.AbsorbBelow > 0 && _split.AbsorbAfterSplit
                ? AbsorbFragments(embeddings, assignment, clusterCount)
                : clusterCount;

        var members = new List<int>[clusterCount];
        for (int c = 0; c < clusterCount; c++) members[c] = new List<int>();
        for (int i = 0; i < assignment.Length; i++) members[assignment[i]].Add(i);

        var candidates = new List<(float Margin, int Cluster, List<int> Half)>();
        for (int c = 0; c < clusterCount; c++)
        {
            if (members[c].Count < _split.MinSegments) continue;
            var (a, b) = SplitInTwo(embeddings, members[c]);
            if (a.Count < _split.MinHalf || b.Count < _split.MinHalf) continue;

            var margin = Math.Min(SelfSimilarity(embeddings, a), SelfSimilarity(embeddings, b))
                         - CrossSimilarity(embeddings, a, b);
            if (margin >= _split.Margin) candidates.Add((margin, c, b));
        }

        candidates.Sort((x, y) => y.Margin.CompareTo(x.Margin));
        var next = clusterCount;
        foreach (var candidate in candidates)
        {
            if (budget-- <= 0) break;
            foreach (var i in candidate.Half) assignment[i] = next;
            next++;
        }
        // A split's half takes the next free id, which is not where it first appears.
        var split = Renumber(assignment);
        return _split.AbsorbBelow > 0 && _split.AbsorbAfterSplit
            ? AbsorbFragments(embeddings, assignment, split)
            : split;
    }

    /// <summary>
    /// Folds every cluster below the support floor into the nearest cluster that clears it. A meeting
    /// at the roster ceiling is usually holding a slot or two with a couple of seconds of speech, and
    /// a slot spent that way is a slot a real split cannot have. Renumbers to first-appearance order
    /// and returns the new cluster count.
    /// </summary>
    private int AbsorbFragments(IReadOnlyList<float[]> embeddings, int[] assignment, int clusterCount)
    {
        var members = new List<int>[clusterCount];
        for (int c = 0; c < clusterCount; c++) members[c] = new List<int>();
        for (int i = 0; i < assignment.Length; i++) members[assignment[i]].Add(i);

        var survivors = new List<int>();
        for (int c = 0; c < clusterCount; c++)
            if (members[c].Count >= _split.AbsorbBelow) survivors.Add(c);
        if (survivors.Count == 0 || survivors.Count == clusterCount) return clusterCount;

        var centroid = new Dictionary<int, float[]>(survivors.Count);
        foreach (var c in survivors) centroid[c] = Centroid(embeddings, members[c]);

        for (int c = 0; c < clusterCount; c++)
        {
            if (members[c].Count >= _split.AbsorbBelow) continue;
            var mean = Centroid(embeddings, members[c]);
            var best = survivors[0];
            var bestSim = float.NegativeInfinity;
            foreach (var s in survivors)
            {
                var sim = Dot(mean, centroid[s]);
                if (sim > bestSim) { bestSim = sim; best = s; }
            }
            foreach (var i in members[c]) assignment[i] = best;
        }
        return Renumber(assignment);
    }

    private static float[] Centroid(IReadOnlyList<float[]> embeddings, List<int> group)
    {
        var sum = new float[embeddings[group[0]].Length];
        foreach (var i in group)
            for (int d = 0; d < sum.Length; d++) sum[d] += embeddings[i][d];

        double norm = 0;
        for (int d = 0; d < sum.Length; d++) norm += sum[d] * (double)sum[d];
        var scale = norm > 1e-24 ? (float)(1.0 / Math.Sqrt(norm)) : 0f;
        for (int d = 0; d < sum.Length; d++) sum[d] *= scale;
        return sum;
    }

    private static int Renumber(int[] assignment)
    {
        var indexByOld = new Dictionary<int, int>();
        for (int i = 0; i < assignment.Length; i++)
        {
            if (!indexByOld.TryGetValue(assignment[i], out var idx))
            {
                idx = indexByOld.Count;
                indexByOld[assignment[i]] = idx;
            }
            assignment[i] = idx;
        }
        return indexByOld.Count;
    }

    /// <summary>Cuts a single cluster's own dendrogram one merge short of the root.</summary>
    private static (List<int> A, List<int> B) SplitInTwo(
        IReadOnlyList<float[]> embeddings, List<int> members)
    {
        var local = new float[members.Count][];
        for (int i = 0; i < members.Count; i++) local[i] = embeddings[members[i]];

        var parent = new int[members.Count];
        for (int i = 0; i < members.Count; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        var groups = members.Count;
        foreach (var m in BuildDendrogram(local))
        {
            if (groups <= 2) break;
            var (ra, rb) = (Find(m.A), Find(m.B));
            if (ra == rb) continue;
            parent[rb] = ra;
            groups--;
        }

        List<int> a = new(), b = new();
        var firstRoot = Find(0);
        for (int i = 0; i < members.Count; i++) (Find(i) == firstRoot ? a : b).Add(members[i]);
        return (a, b);
    }

    private static float SelfSimilarity(IReadOnlyList<float[]> embeddings, List<int> group)
    {
        if (group.Count < 2) return 1f;
        float sum = 0;
        var pairs = 0;
        for (int i = 0; i < group.Count; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                sum += Dot(embeddings[group[i]], embeddings[group[j]]);
                pairs++;
            }
        }
        return sum / pairs;
    }

    private static float CrossSimilarity(
        IReadOnlyList<float[]> embeddings, List<int> a, List<int> b)
    {
        float sum = 0;
        foreach (var i in a)
            foreach (var j in b) sum += Dot(embeddings[i], embeddings[j]);
        return sum / (a.Count * b.Count);
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
