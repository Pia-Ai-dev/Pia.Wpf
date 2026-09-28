using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.LiveTranscription;

/// <summary>Matches each segment against running per-speaker centroids: above the threshold it joins the
/// speaker, just below it takes the nearest label without moving the centroid, further below it is new.</summary>
public sealed class SpeakerIdentificationService : ISpeakerIdentificationService
{
    // Width of the band below the threshold that still takes the nearest label but never moves its centroid.
    private const float BorderlineMargin = 0.07f;

    private readonly IEmbeddingExtractor _extractor;
    private readonly ILogger _logger;
    private readonly float _matchThreshold;
    // Maximum number of distinct speakers to register in one meeting; 0 = unlimited. When the cap is
    // reached a new segment is forced onto its best existing match instead of registering a new speaker.
    private readonly int _maxSpeakers;

    private readonly object _lock = new();
    private readonly Dictionary<string, SpeakerCentroid> _speakers = new();
    private readonly Dictionary<string, string> _displayLabels = new(); // internalId → label
    private int _counter;
    private long _nextSegmentId;
    private bool _disposed;

    public SpeakerIdentificationService(string modelPath, float matchThreshold, int maxSpeakers, ILogger logger)
        : this(new SherpaEmbeddingExtractor(modelPath), matchThreshold, maxSpeakers, logger)
    {
        _logger.LogInformation(
            "Speaker identification active. model='{Model}' dim={Dim} threshold={Threshold:F2} maxSpeakers={MaxSpeakers}",
            modelPath, _extractor.Dim, _matchThreshold, _maxSpeakers);
    }

    internal SpeakerIdentificationService(IEmbeddingExtractor extractor, float matchThreshold, int maxSpeakers, ILogger logger)
    {
        _extractor = extractor;
        _logger = logger;
        _matchThreshold = matchThreshold;
        _maxSpeakers = maxSpeakers;
    }

    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate)
        => Identify(segmentSamples, sampleRate).Label;

    public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
    {
        var (label, _, belowThreshold) = Identify(segmentSamples, sampleRate);
        var id = Interlocked.Increment(ref _nextSegmentId) - 1;
        return new SpeakerSegmentResult(id, label, belowThreshold);
    }

    public event EventHandler<string>? SpeakerRegistered;

    // Manual mode never revisits a decision, so the event can never fire. Explicit empty
    // accessors instead of a field avoid the CS0067 unused-event warning.
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }

    public (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate)
    {
        var (label, embedding, _) = Identify(segmentSamples, sampleRate);
        return (label, embedding);
    }

    private (string Label, float[] Embedding, bool BelowThreshold) Identify(float[] segmentSamples, int sampleRate)
    {
        var durationSec = sampleRate > 0 ? segmentSamples.Length / (float)sampleRate : 0f;
        var embedding = _extractor.Compute(segmentSamples, sampleRate);

        string? newlyRegisteredLabel = null;
        (string Label, float[] Embedding, bool BelowThreshold) result;

        lock (_lock)
        {
            string? bestId = null;
            float bestSim = float.NegativeInfinity;
            var sims = new List<(string label, float sim)>(_speakers.Count);

            foreach (var (id, centroid) in _speakers)
            {
                var sim = CosineSimilarity(embedding, centroid.Centroid);
                sims.Add((_displayLabels[id], sim));
                if (sim > bestSim) { bestSim = sim; bestId = id; }
            }

            // Confident match; the weight grows with similarity so a near-threshold one barely moves the centroid.
            if (bestId is not null && bestSim >= _matchThreshold)
            {
                var matched = _speakers[bestId];
                var weight = (bestSim - _matchThreshold) / MathF.Max(1f - _matchThreshold, 1e-6f);
                matched.Update(embedding, weight);
                var label = _displayLabels[bestId];

                // Label/sims can carry a user-typed name once the speaker is renamed → sensitive.
                _logger.SensitiveInformation(
                    "Diarization match: {Label} sim={Sim:F3} w={Weight:F2} dur={Dur:F2}s sims=[{Sims}]",
                    label, bestSim, weight, durationSec, FormatSims(sims));

                result = (label, embedding, false);
            }
            // Borderline: the nearest label for the UI, but no centroid update and flagged below the threshold.
            else if (bestId is not null && bestSim >= _matchThreshold - BorderlineMargin)
            {
                var label = _displayLabels[bestId];
                // Label/sims can carry a user-typed name once the speaker is renamed → sensitive.
                _logger.SensitiveInformation(
                    "Diarization borderline (no centroid update): {Label} sim={Sim:F3} threshold={Threshold:F2} margin={Margin:F2} dur={Dur:F2}s sims=[{Sims}]",
                    label, bestSim, _matchThreshold, BorderlineMargin, durationSec, FormatSims(sims));
                result = (label, embedding, true);
            }
            // At the speaker cap a would-be new speaker is forced onto its nearest match, like a borderline one.
            else if (_maxSpeakers > 0 && _speakers.Count >= _maxSpeakers && bestId is not null)
            {
                var label = _displayLabels[bestId];
                // Label/sims can carry a user-typed name once the speaker is renamed → sensitive.
                _logger.SensitiveInformation(
                    "Diarization cap reached ({Count}/{Max}); forcing best match: {Label} sim={Sim:F3} dur={Dur:F2}s sims=[{Sims}]",
                    _speakers.Count, _maxSpeakers, label, bestSim, durationSec, FormatSims(sims));
                result = (label, embedding, true);
            }
            else
            {
                _counter++;
                var internalId = $"spk_{_counter}";
                var newLabel = $"Speaker {_counter}";
                _speakers[internalId] = new SpeakerCentroid(embedding);
                _displayLabels[internalId] = newLabel;

                // {Label} is the auto-assigned "Speaker N", but {Sims} carries other labels that may
                // already be user-typed (renamed) → sensitive.
                _logger.SensitiveInformation(
                    "Diarization new speaker: {Label} bestSim={BestSim:F3} threshold={Threshold:F2} margin={Margin:F2} dur={Dur:F2}s sims=[{Sims}]",
                    newLabel,
                    bestSim == float.NegativeInfinity ? 0f : bestSim,
                    _matchThreshold,
                    BorderlineMargin,
                    durationSec,
                    FormatSims(sims));

                newlyRegisteredLabel = newLabel;
                result = (newLabel, embedding, false);
            }
        }

        // Raise outside the lock to avoid deadlocks if a subscriber calls back into the
        // service or another component that takes its own locks (e.g. ConsentStateManager).
        if (newlyRegisteredLabel is not null)
        {
            try { SpeakerRegistered?.Invoke(this, newlyRegisteredLabel); }
            catch (Exception ex) { _logger.LogError(ex, "SpeakerRegistered subscriber threw for {Label}", newlyRegisteredLabel); }
        }

        return result;
    }

    public bool Rename(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return false;

        lock (_lock)
        {
            string? internalId = null;
            var targetTaken = false;
            foreach (var (id, label) in _displayLabels)
            {
                if (label == oldLabel && internalId is null) internalId = id;
                else if (label == newLabel) targetTaken = true;
            }
            if (internalId is null) return false;

            // Collision guard: two internal ids must NEVER share one display label. Without this, a
            // rename onto a label that another cluster already carries silently aliased two different
            // voices onto one identity — and because the consent map is keyed by display label, the
            // second voice inherited the first one's consent state.
            if (targetTaken)
            {
                _logger.LogWarning("Speaker rename refused: the target display label is already in use");
                return false;
            }

            _displayLabels[internalId] = newLabel;
            // The new label is a user-typed name → sensitive.
            _logger.SensitiveInformation("Speaker renamed: '{Old}' → '{New}' (id={Id})", oldLabel, newLabel, internalId);
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            WipeBiometricStateUnderLock();
            _logger.LogInformation("Speaker identification state reset");
        }
    }

    /// <summary>
    /// Actively erase all in-memory biometric state: zero each centroid's float[] vector
    /// (so the embedding bytes don't linger on the managed heap waiting for GC), then drop
    /// the centroid store, the display-label map (which may hold user-typed renamed names),
    /// and reset the speaker counter. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private void WipeBiometricStateUnderLock()
    {
        foreach (var centroid in _speakers.Values)
        {
            Array.Clear(centroid.Centroid);
        }
        _speakers.Clear();
        _displayLabels.Clear();
        _counter = 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            // Actively erase voice embeddings/centroids and any user-typed labels before the
            // native extractor goes — so when a meeting ends no biometric data lingers in
            // managed memory waiting for GC. Idempotent + thread-safe (under _lock).
            WipeBiometricStateUnderLock();
            _extractor.Dispose();
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0f : dot / denominator;
    }

    private static string FormatSims(List<(string label, float sim)> sims)
    {
        if (sims.Count == 0) return "(none)";
        sims.Sort((x, y) => y.sim.CompareTo(x.sim));
        return string.Join(", ", sims.Select(s => $"{s.label}={s.sim:F3}"));
    }

    private sealed class SpeakerCentroid
    {
        public float[] Centroid;
        public float CumulativeWeight;

        public SpeakerCentroid(float[] firstEmbedding)
        {
            Centroid = (float[])firstEmbedding.Clone();
            NormalizeInPlace(Centroid);
            CumulativeWeight = 1f;
        }

        public void Update(float[] embedding, float weight)
        {
            if (weight <= 0f) return;

            // Weighted running mean: c_new = (c * cw + e * w) / (cw + w)
            var newCw = CumulativeWeight + weight;
            for (int i = 0; i < Centroid.Length; i++)
            {
                Centroid[i] = (Centroid[i] * CumulativeWeight + embedding[i] * weight) / newCw;
            }
            // Re-project onto the unit sphere so cosine geometry stays well-conditioned
            // and any future weighting variant (EMA, decay) doesn't silently drift.
            NormalizeInPlace(Centroid);
            CumulativeWeight = newCw;
        }

        private static void NormalizeInPlace(float[] v)
        {
            double sumSq = 0;
            for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
            var norm = (float)Math.Sqrt(sumSq);
            if (norm <= 1e-12f) return;
            for (int i = 0; i < v.Length; i++) v[i] /= norm;
        }
    }
}
