using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Adaptive ("smart auto-detect") within-meeting speaker identification. Serves every segment an
/// INSTANT provisional label (nearest cluster centroid — same latency as the manual service) and
/// additionally journals every embedding; periodically it re-clusters the whole meeting with
/// <see cref="SpeakerClusterer"/> so early mistakes self-heal, emitting
/// <see cref="SpeakersReassigned"/> for retro-corrections. No user tuning: the cut (and thus the
/// instant-match threshold) is derived from the data each pass.
///
/// Privacy: journaled embeddings are biometric data — in-memory only, per meeting, actively
/// zeroed on <see cref="Reset"/>/<see cref="Dispose"/> (same discipline as the manual service's
/// WipeBiometricStateUnderLock).
/// </summary>
public sealed class AdaptiveSpeakerIdentificationService : ISpeakerIdentificationService
{
    // A pass needs enough evidence to beat the provisional path; below this we stay provisional.
    internal const int WarmupSegments = 6;
    // Pass cadence: every N new segments, or after this latency once at least one new segment
    // arrived. Passes are cheap (O(n²) Lance–Williams) but not free; the stride bounds churn.
    internal const int PassSegmentStride = 5;
    internal static readonly TimeSpan PassMaxLatency = TimeSpan.FromSeconds(30);
    internal const int DefaultMaxJournaledSegments = 2000;
    internal const float InitialMatchSimilarity = 0.50f;

    private readonly IEmbeddingExtractor _extractor;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _maxJournaledSegments;
    private readonly SpeakerClusterer _clusterer = new();

    private readonly object _lock = new();
    private readonly List<(long SegmentId, float[] Embedding)> _segments = new(); // oldest first
    private readonly Dictionary<long, int> _clusterBySegment = new();
    private readonly Dictionary<int, string> _labelByCluster = new();
    private readonly Dictionary<int, RunningCentroid> _centroidByCluster = new();
    private readonly HashSet<int> _renamedClusters = new();
    private long _nextSegmentId;
    private int _nextClusterId;
    private int _speakerCounter;
    private float _matchSimilarity = InitialMatchSimilarity;
    private int _segmentsSinceLastPass;
    private DateTimeOffset _lastPassAt;
    private bool _disposed;

    public event EventHandler<string>? SpeakerRegistered;
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;

    public AdaptiveSpeakerIdentificationService(
        IEmbeddingExtractor extractor, ILogger logger, Func<DateTimeOffset>? now = null)
        : this(extractor, logger, now, DefaultMaxJournaledSegments)
    {
    }

    /// <summary>Test ctor: caps sized down so cap behavior is exercisable.</summary>
    internal AdaptiveSpeakerIdentificationService(
        IEmbeddingExtractor extractor, ILogger logger, Func<DateTimeOffset>? now,
        int maxJournaledSegments)
    {
        _extractor = extractor;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _maxJournaledSegments = maxJournaledSegments;
        _lastPassAt = _now();
        _logger.LogInformation(
            "Adaptive speaker identification active. dim={Dim} warmup={Warmup} stride={Stride} maxJournal={MaxJournal}",
            extractor.Dim, WarmupSegments, PassSegmentStride, _maxJournaledSegments);
    }

    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate)
        => IdentifyOrRegisterSegment(segmentSamples, sampleRate).Label;

    public (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate)
    {
        var embedding = Normalize(_extractor.Compute(segmentSamples, sampleRate));
        var result = ProcessEmbedding(embedding);
        // The journal owns its copy; hand the caller an independent one so the biometric wipe
        // cannot zero a buffer the caller still holds (and vice versa).
        return (result.Label, (float[])embedding.Clone());
    }

    public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
    {
        var embedding = Normalize(_extractor.Compute(segmentSamples, sampleRate));
        return ProcessEmbedding(embedding);
    }

    private SpeakerSegmentResult ProcessEmbedding(float[] embedding)
    {
        string? newLabel = null;
        List<SpeakerReassignment>? reassignments = null;
        List<string>? passLabels = null;
        SpeakerSegmentResult result;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var segId = _nextSegmentId++;
            _segments.Add((segId, embedding));
            if (_segments.Count > _maxJournaledSegments)
            {
                // Oldest falls off: zero the biometric vector; its assignment stays frozen (the
                // VM's own journal is far smaller, so no rebuildable utterance references it).
                Array.Clear(_segments[0].Embedding);
                _clusterBySegment.Remove(_segments[0].SegmentId);
                _segments.RemoveAt(0);
            }

            // Instant provisional label: nearest centroid at the adaptive similarity threshold.
            var (bestCluster, bestSim) = BestClusterUnderLock(embedding);
            int cluster;
            if (bestCluster < 0 || bestSim < _matchSimilarity)
            {
                cluster = _nextClusterId++;
                var label = $"Speaker {++_speakerCounter}";
                _labelByCluster[cluster] = label;
                _centroidByCluster[cluster] = new RunningCentroid(embedding);
                newLabel = label;
            }
            else
            {
                cluster = bestCluster;
                _centroidByCluster[cluster].Add(embedding);
            }
            _clusterBySegment[segId] = cluster;
            _segmentsSinceLastPass++;
            result = new SpeakerSegmentResult(segId, _labelByCluster[cluster]);

            var due = _segmentsSinceLastPass >= PassSegmentStride
                      || (_segmentsSinceLastPass >= 1 && _now() - _lastPassAt >= PassMaxLatency);
            if (due && _segments.Count >= WarmupSegments)
            {
                try
                {
                    (reassignments, passLabels) = RunPassUnderLock();
                }
                catch (Exception ex)
                {
                    // A clustering bug must never take down transcription; keep the previous
                    // assignment and try again next time.
                    _logger.LogWarning(ex, "Adaptive re-cluster pass failed; keeping previous assignment");
                }
                _segmentsSinceLastPass = 0;
                _lastPassAt = _now();
            }
        }

        // Events outside the lock (same rationale as the manual service).
        if (newLabel is not null) RaiseSpeakerRegistered(newLabel);
        if (passLabels is not null)
            foreach (var label in passLabels) RaiseSpeakerRegistered(label);
        if (reassignments is { Count: > 0 })
        {
            try { SpeakersReassigned?.Invoke(this, reassignments); }
            catch (Exception ex) { _logger.LogError(ex, "SpeakersReassigned subscriber threw"); }
        }

        return result;
    }

    /// <summary>
    /// Re-clusters ALL journaled embeddings and maps the resulting clusters onto the existing
    /// stable cluster ids by greedy segment-overlap matching (ties: user-renamed label first,
    /// then earliest member segment — so "Speaker 1"/"Alice" stays on the earlier voice).
    /// Returns changed (segment → label) pairs and any labels newly created by the pass.
    /// </summary>
    private (List<SpeakerReassignment> Reassignments, List<string> NewLabels) RunPassUnderLock()
    {
        var embeddings = new float[_segments.Count][];
        for (int i = 0; i < _segments.Count; i++) embeddings[i] = _segments[i].Embedding;

        var previousCount = _labelByCluster.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cr = _clusterer.Cluster(embeddings, previousCount);
        sw.Stop();
        _matchSimilarity = 1f - cr.CutDistance;

        // Members per new cluster index (in journal order → element 0 is the earliest segment).
        var members = new List<long>[cr.ClusterCount];
        for (int c = 0; c < cr.ClusterCount; c++) members[c] = new List<long>();
        for (int i = 0; i < _segments.Count; i++)
            members[cr.AssignmentPerSegment[i]].Add(_segments[i].SegmentId);

        // Greedy overlap matching new-cluster ↔ previous stable cluster id.
        var candidates = new List<(int NewCluster, int PrevCluster, int Overlap, bool Renamed, long EarliestSeg)>();
        foreach (var (newCluster, segIds) in members.Index())
        {
            var overlapByPrev = new Dictionary<int, int>();
            foreach (var segId in segIds)
            {
                if (_clusterBySegment.TryGetValue(segId, out var prev))
                    overlapByPrev[prev] = overlapByPrev.GetValueOrDefault(prev) + 1;
            }
            foreach (var (prev, overlap) in overlapByPrev)
                candidates.Add((newCluster, prev, overlap, _renamedClusters.Contains(prev), segIds[0]));
        }
        candidates.Sort((x, y) =>
        {
            var byOverlap = y.Overlap.CompareTo(x.Overlap);
            if (byOverlap != 0) return byOverlap;
            var byRenamed = y.Renamed.CompareTo(x.Renamed);
            if (byRenamed != 0) return byRenamed;
            return x.EarliestSeg.CompareTo(y.EarliestSeg);
        });

        var stableByNew = new int[cr.ClusterCount];
        Array.Fill(stableByNew, -1);
        var takenPrev = new HashSet<int>();
        foreach (var c in candidates)
        {
            if (stableByNew[c.NewCluster] != -1 || takenPrev.Contains(c.PrevCluster)) continue;
            stableByNew[c.NewCluster] = c.PrevCluster;
            takenPrev.Add(c.PrevCluster);
        }

        // Unmatched new clusters get fresh stable ids + "Speaker N" labels.
        var newLabels = new List<string>();
        var newLabelByCluster = new Dictionary<int, string>();
        var newCentroidByCluster = new Dictionary<int, RunningCentroid>();
        var newRenamed = new HashSet<int>();
        for (int c = 0; c < cr.ClusterCount; c++)
        {
            if (stableByNew[c] == -1)
            {
                stableByNew[c] = _nextClusterId++;
                var label = $"Speaker {++_speakerCounter}";
                newLabelByCluster[stableByNew[c]] = label;
                newLabels.Add(label);
            }
            else
            {
                newLabelByCluster[stableByNew[c]] = _labelByCluster[stableByNew[c]];
                if (_renamedClusters.Contains(stableByNew[c])) newRenamed.Add(stableByNew[c]);
            }
        }

        // Apply: new assignment + per-cluster mean centroids; diff labels for the event.
        var reassignments = new List<SpeakerReassignment>();
        for (int i = 0; i < _segments.Count; i++)
        {
            var segId = _segments[i].SegmentId;
            var stable = stableByNew[cr.AssignmentPerSegment[i]];
            var oldLabel = _clusterBySegment.TryGetValue(segId, out var oldCluster)
                ? _labelByCluster.GetValueOrDefault(oldCluster)
                : null;
            _clusterBySegment[segId] = stable;

            var label = newLabelByCluster[stable];
            if (!string.Equals(oldLabel, label, StringComparison.Ordinal))
                reassignments.Add(new SpeakerReassignment(segId, label));

            if (!newCentroidByCluster.TryGetValue(stable, out var centroid))
                newCentroidByCluster[stable] = centroid = new RunningCentroid(_segments[i].Embedding);
            else
                centroid.Add(_segments[i].Embedding);
        }

        // Old centroids are biometric state too — zero them before swapping in the new set.
        foreach (var old in _centroidByCluster.Values) old.Wipe();
        _centroidByCluster.Clear();
        foreach (var (k, v) in newCentroidByCluster) _centroidByCluster[k] = v;
        _labelByCluster.Clear();
        foreach (var (k, v) in newLabelByCluster) _labelByCluster[k] = v;
        _renamedClusters.Clear();
        foreach (var k in newRenamed) _renamedClusters.Add(k);

        _logger.LogDebug(
            "Adaptive pass: {Segments} segments → {Clusters} clusters cut={Cut:F2} changed={Changed} ({Ms}ms)",
            _segments.Count, cr.ClusterCount, cr.CutDistance, reassignments.Count, sw.ElapsedMilliseconds);
        // Labels can carry user-typed names after a rename → DEBUG-only.
        _logger.SensitiveInformation("Adaptive pass labels: [{Labels}]",
            string.Join(", ", _labelByCluster.Values));

        return (reassignments, newLabels);
    }

    private (int Cluster, float Similarity) BestClusterUnderLock(float[] embedding)
    {
        var best = float.NegativeInfinity;
        var bestCluster = -1;
        foreach (var (cluster, centroid) in _centroidByCluster)
        {
            var sim = centroid.Similarity(embedding);
            if (sim > best) { best = sim; bestCluster = cluster; }
        }
        return (bestCluster, best);
    }

    public bool Rename(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return false;
        lock (_lock)
        {
            foreach (var (cluster, label) in _labelByCluster)
            {
                if (label != oldLabel) continue;
                _labelByCluster[cluster] = newLabel;
                _renamedClusters.Add(cluster);
                _logger.SensitiveInformation("Speaker renamed: '{Old}' → '{New}' (cluster={Cluster})",
                    oldLabel, newLabel, cluster);
                return true;
            }
            return false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            WipeBiometricStateUnderLock();
            _logger.LogInformation("Adaptive speaker identification state reset");
        }
    }

    /// <summary>
    /// Actively erase all in-memory biometric state: zero every journaled embedding and every
    /// centroid vector before dropping references. Segment ids stay monotonic across Reset so a
    /// stale reassignment held by the UI can never collide with a new segment.
    /// </summary>
    private void WipeBiometricStateUnderLock()
    {
        foreach (var (_, embedding) in _segments) Array.Clear(embedding);
        _segments.Clear();
        foreach (var centroid in _centroidByCluster.Values) centroid.Wipe();
        _centroidByCluster.Clear();
        _clusterBySegment.Clear();
        _labelByCluster.Clear();
        _renamedClusters.Clear();
        _nextClusterId = 0;
        _speakerCounter = 0;
        _matchSimilarity = InitialMatchSimilarity;
        _segmentsSinceLastPass = 0;
        // _lastPassAt is deliberately NOT reset: the warm-up gate already blocks a pass until
        // enough new segments exist, and _nextSegmentId must stay monotonic across Reset anyway.
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            WipeBiometricStateUnderLock();
            _extractor.Dispose();
        }
    }

    private void RaiseSpeakerRegistered(string label)
    {
        try { SpeakerRegistered?.Invoke(this, label); }
        catch (Exception ex) { _logger.LogError(ex, "SpeakerRegistered subscriber threw for {Label}", label); }
    }

    private static float[] Normalize(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = (float)Math.Sqrt(sumSq);
        if (norm > 1e-12f)
            for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    /// <summary>Running mean of unit vectors, renormalized for cosine matching by dot product.</summary>
    private sealed class RunningCentroid
    {
        private readonly float[] _sum;
        private int _count;

        public RunningCentroid(float[] first)
        {
            _sum = (float[])first.Clone();
            _count = 1;
        }

        public void Add(float[] embedding)
        {
            for (int i = 0; i < _sum.Length; i++) _sum[i] += embedding[i];
            _count++;
        }

        public float Similarity(float[] embedding)
        {
            if (embedding.Length != _sum.Length) return 0f;
            float dot = 0, norm = 0;
            for (int i = 0; i < _sum.Length; i++)
            {
                dot += _sum[i] * embedding[i];
                norm += _sum[i] * _sum[i];
            }
            var denom = MathF.Sqrt(norm);
            return denom <= 1e-12f ? 0f : dot / denom;   // embedding is already unit-norm
        }

        public void Wipe() => Array.Clear(_sum);
    }
}
