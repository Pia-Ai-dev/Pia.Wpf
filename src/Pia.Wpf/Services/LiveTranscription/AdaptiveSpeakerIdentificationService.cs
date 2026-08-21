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
    // Borderline-length embeddings are the dominant source of spurious dendrogram splits, so they
    // stay out of the clustering input — they keep their provisional label either way.
    internal const float MinClusterSegmentSeconds = 2f;
    // The cut a degenerate pass reports (one dominant voice → CutMin, or CutMax the other way) would
    // otherwise derive an unusable instant-match threshold exactly when the evidence is weakest.
    internal const float MatchSimilarityMin = 0.40f;
    internal const float MatchSimilarityMax = 0.60f;

    private readonly IEmbeddingExtractor _extractor;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _maxJournaledSegments;
    private readonly SpeakerClusterer _clusterer;

    private readonly object _lock = new();
    private readonly List<(long SegmentId, float[] Embedding, float DurationSeconds)> _segments = new(); // oldest first
    private readonly Dictionary<long, int> _clusterBySegment = new();
    private readonly Dictionary<int, string> _labelByCluster = new();
    private readonly Dictionary<int, RunningCentroid> _centroidByCluster = new();
    private readonly HashSet<int> _renamedClusters = new();
    private long _nextSegmentId;
    private int _nextClusterId;
    private int _speakerCounter;
    private float _matchSimilarity = InitialMatchSimilarity;
    private int _segmentsSinceLastPass;
    private int _lastPassClusterCount;
    private int _expectedSpeakers;
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
        int maxJournaledSegments, SpeakerClusterer? clusterer = null)
    {
        _extractor = extractor;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _maxJournaledSegments = maxJournaledSegments;
        _clusterer = clusterer ?? new SpeakerClusterer();
        _lastPassAt = _now();
        _logger.LogInformation(
            "Adaptive speaker identification active. dim={Dim} warmup={Warmup} stride={Stride} maxJournal={MaxJournal}",
            extractor.Dim, WarmupSegments, PassSegmentStride, _maxJournaledSegments);
    }

    // These two promise a label, so an unplaceable segment collapses to blank rather than null.
    // Only IdentifyOrRegisterSegment models "no speaker" properly, and it is what the engine calls.
    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate)
        => IdentifyOrRegisterSegment(segmentSamples, sampleRate).Label ?? string.Empty;

    public (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate)
    {
        var embedding = Normalize(_extractor.Compute(segmentSamples, sampleRate));
        var result = ProcessEmbedding(embedding, DurationSeconds(segmentSamples, sampleRate));
        // The journal owns its copy; hand the caller an independent one so the biometric wipe
        // cannot zero a buffer the caller still holds (and vice versa).
        return (result.Label ?? string.Empty, (float[])embedding.Clone());
    }

    public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
    {
        var embedding = Normalize(_extractor.Compute(segmentSamples, sampleRate));
        return ProcessEmbedding(embedding, DurationSeconds(segmentSamples, sampleRate));
    }

    public void SetExpectedSpeakers(int count)
    {
        lock (_lock) _expectedSpeakers = Math.Max(0, count);
    }

    /// <summary>Snapshot of the live label set — a copy, so it cannot mutate after the lock releases.</summary>
    internal IReadOnlyCollection<string> KnownLabels
    {
        get { lock (_lock) return [.. _labelByCluster.Values]; }
    }

    private static float DurationSeconds(float[] samples, int sampleRate)
        => sampleRate > 0 ? (float)samples.Length / sampleRate : 0f;

    private SpeakerSegmentResult ProcessEmbedding(float[] embedding, float durationSeconds)
    {
        string? newLabel = null;
        List<SpeakerReassignment>? reassignments = null;
        List<string>? passLabels = null;
        SpeakerSegmentResult result;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var segId = _nextSegmentId++;
            _segments.Add((segId, embedding, durationSeconds));
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
            var matched = bestCluster >= 0 && bestSim >= _matchSimilarity;
            int? cluster = null;
            if (durationSeconds >= MinClusterSegmentSeconds)
            {
                if (matched)
                {
                    cluster = bestCluster;
                    _centroidByCluster[bestCluster].Add(embedding);
                }
                else if (_expectedSpeakers > 0 && bestCluster >= 0
                         && _labelByCluster.Count >= _expectedSpeakers + SpeakerClusterer.ExpectedSpeakerSlack)
                {
                    // At the roster ceiling. Take the nearest voice instead of minting one the roster
                    // says cannot exist; no centroid update, because the match was forced not earned.
                    cluster = bestCluster;
                }
                else
                {
                    cluster = _nextClusterId++;
                    var label = $"Speaker {++_speakerCounter}";
                    _labelByCluster[cluster.Value] = label;
                    _centroidByCluster[cluster.Value] = new RunningCentroid(embedding);
                    newLabel = label;
                }
            }
            else if (matched)
            {
                // A sub-floor segment may take a label but must never move a centroid: it is mostly
                // silence, and no pass will ever see it to undo the drift.
                cluster = bestCluster;
            }
            // Sub-floor and matching nothing: no label. Minting here would create a speaker that the
            // 2 s clustering floor keeps out of reach of every correction mechanism.

            if (cluster is int assigned) _clusterBySegment[segId] = assigned;
            _segmentsSinceLastPass++;
            result = new SpeakerSegmentResult(
                segId, cluster is int c ? _labelByCluster[c] : null);

            // Warm-up counts ELIGIBLE embeddings: a pass over a handful of short interjections would
            // rebuild the label/centroid maps from near-empty output and wipe every known speaker.
            var due = _segmentsSinceLastPass >= PassSegmentStride
                      || (_segmentsSinceLastPass >= 1 && _now() - _lastPassAt >= PassMaxLatency);
            if (due && EligibleCountUnderLock() >= WarmupSegments)
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

    private int EligibleCountUnderLock()
    {
        var eligible = 0;
        foreach (var segment in _segments)
            if (segment.DurationSeconds >= MinClusterSegmentSeconds) eligible++;
        return eligible;
    }

    /// <summary>
    /// Re-clusters the journaled embeddings that clear <see cref="MinClusterSegmentSeconds"/> and
    /// maps the resulting clusters onto the existing stable cluster ids by greedy segment-overlap
    /// matching (ties: user-renamed label first, then earliest member segment — so "Speaker 1"/
    /// "Alice" stays on the earlier voice). Returns changed (segment → label) pairs and any labels
    /// newly created by the pass.
    /// </summary>
    private (List<SpeakerReassignment> Reassignments, List<string> NewLabels) RunPassUnderLock()
    {
        // Eligible → journal index. Sub-floor segments keep their provisional label but never enter
        // the dendrogram, so every site below indexes the journal through this map.
        var journalIndex = new List<int>(_segments.Count);
        for (int i = 0; i < _segments.Count; i++)
            if (_segments[i].DurationSeconds >= MinClusterSegmentSeconds) journalIndex.Add(i);

        var embeddings = new float[journalIndex.Count][];
        for (int i = 0; i < journalIndex.Count; i++) embeddings[i] = _segments[journalIndex[i]].Embedding;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Seed hysteresis with the last PASS's count: _labelByCluster also holds the provisional
        // registrations made since, which would ratchet the count upward pass after pass.
        var cr = _clusterer.Cluster(embeddings, _lastPassClusterCount, _expectedSpeakers);
        sw.Stop();
        _matchSimilarity = Math.Clamp(1f - cr.CutDistance, MatchSimilarityMin, MatchSimilarityMax);
        _lastPassClusterCount = cr.ClusterCount;

        // Members per new cluster index (in journal order → element 0 is the earliest segment).
        var members = new List<long>[cr.ClusterCount];
        for (int c = 0; c < cr.ClusterCount; c++) members[c] = new List<long>();
        for (int i = 0; i < journalIndex.Count; i++)
            members[cr.AssignmentPerSegment[i]].Add(_segments[journalIndex[i]].SegmentId);

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

        // Labels this rebuild orphaned — nothing references them any more. Recycling them before
        // minting keeps the numbering close to the distinct voices instead of only ever growing;
        // user-renamed labels are never handed to a different voice.
        var orphans = new List<int>();
        foreach (var cluster in _labelByCluster.Keys)
        {
            if (takenPrev.Contains(cluster) || _renamedClusters.Contains(cluster)) continue;
            orphans.Add(cluster);
        }
        orphans.Sort();
        var nextOrphan = 0;

        var newLabels = new List<string>();
        var newLabelByCluster = new Dictionary<int, string>();
        var newCentroidByCluster = new Dictionary<int, RunningCentroid>();
        var newRenamed = new HashSet<int>();
        for (int c = 0; c < cr.ClusterCount; c++)
        {
            if (stableByNew[c] != -1)
            {
                newLabelByCluster[stableByNew[c]] = _labelByCluster[stableByNew[c]];
                if (_renamedClusters.Contains(stableByNew[c])) newRenamed.Add(stableByNew[c]);
            }
            else if (nextOrphan < orphans.Count)
            {
                var recycled = orphans[nextOrphan++];
                stableByNew[c] = recycled;
                newLabelByCluster[recycled] = _labelByCluster[recycled];
            }
            else
            {
                stableByNew[c] = _nextClusterId++;
                var label = $"Speaker {++_speakerCounter}";
                newLabelByCluster[stableByNew[c]] = label;
                newLabels.Add(label);
            }
        }
        // Apply: new assignment + per-cluster mean centroids; diff labels for the event.
        var reassignments = new List<SpeakerReassignment>();
        for (int i = 0; i < journalIndex.Count; i++)
        {
            var (segId, embedding, _) = _segments[journalIndex[i]];
            var stable = stableByNew[cr.AssignmentPerSegment[i]];
            var oldLabel = _clusterBySegment.TryGetValue(segId, out var oldCluster)
                ? _labelByCluster.GetValueOrDefault(oldCluster)
                : null;
            _clusterBySegment[segId] = stable;

            var label = newLabelByCluster[stable];
            if (!string.Equals(oldLabel, label, StringComparison.Ordinal))
                reassignments.Add(new SpeakerReassignment(segId, label));

            if (!newCentroidByCluster.TryGetValue(stable, out var centroid))
                newCentroidByCluster[stable] = centroid = new RunningCentroid(embedding);
            else
                centroid.Add(embedding);
        }

        // Old centroids are biometric state too — zero every one the rebuild did not carry over.
        foreach (var (_, old) in _centroidByCluster) old.Wipe();
        _centroidByCluster.Clear();
        foreach (var (k, v) in newCentroidByCluster) _centroidByCluster[k] = v;
        _labelByCluster.Clear();
        foreach (var (k, v) in newLabelByCluster) _labelByCluster[k] = v;
        _renamedClusters.Clear();
        foreach (var k in newRenamed) _renamedClusters.Add(k);

        // A pass must not leave a segment pointing at a cluster it just dropped, or the bubble keeps a
        // label the service no longer knows. Only segments the pass did not iterate can be left
        // dangling — the sub-floor ones — and "no label" is the honest answer for a segment no
        // clustering ever saw.
        foreach (var segId in _clusterBySegment.Keys.ToArray())
        {
            if (_labelByCluster.ContainsKey(_clusterBySegment[segId])) continue;
            _clusterBySegment.Remove(segId);
            reassignments.Add(new SpeakerReassignment(segId, null));
        }

        _logger.LogDebug(
            "Adaptive pass: {Eligible}/{Segments} segments → {Clusters} clusters cut={Cut:F2} expected={Expected} changed={Changed} ({Ms}ms)",
            journalIndex.Count, _segments.Count, cr.ClusterCount, cr.CutDistance, _expectedSpeakers,
            reassignments.Count, sw.ElapsedMilliseconds);
        // Labels can carry user-typed names after a rename → DEBUG-only.
        _logger.SensitiveInformation("Adaptive pass labels: [{Labels}]",
            string.Join(", ", _labelByCluster.Values));
        // A cleared label renders as a bare "123=" — the log's spelling of "this segment has no
        // speaker any more". Measure-SpeakerAttribution.ps1 relies on that shape.
        if (reassignments.Count > 0)
            _logger.SensitiveDebug("Adaptive pass reassigned: [{Pairs}]",
                string.Join(", ", reassignments.Select(r => $"{r.SegmentId}={r.NewLabel}")));

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
        foreach (var (_, embedding, _) in _segments) Array.Clear(embedding);
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
        _lastPassClusterCount = 0;
        _expectedSpeakers = 0;
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

        public RunningCentroid(float[] first) => _sum = (float[])first.Clone();

        public void Add(float[] embedding)
        {
            for (int i = 0; i < _sum.Length; i++) _sum[i] += embedding[i];
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
