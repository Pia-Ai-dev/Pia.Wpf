using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Live speaker identification for a single meeting. Uses sherpa-onnx's
/// <see cref="SpeakerEmbeddingExtractor"/> to compute per-segment embeddings and
/// matches them against an in-process pool of running per-speaker centroids.
///
/// Per-speaker state is kept under an internal id (<c>spk_1</c>, <c>spk_2</c>, …)
/// while the UI sees a display label ("Speaker 1", or whatever the user renames it
/// to). This split makes <see cref="Rename"/> a single dictionary update.
///
/// Each successful match folds the new embedding into the matched speaker's
/// centroid (running mean), so a noisy first segment doesn't permanently anchor
/// "Speaker 1".
/// </summary>
public sealed class SpeakerIdentificationService : ISpeakerIdentificationService
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly ILogger _logger;
    private readonly float _matchThreshold;

    private readonly object _lock = new();
    private readonly Dictionary<string, SpeakerCentroid> _speakers = new();
    private readonly Dictionary<string, string> _displayLabels = new(); // internalId → label
    private int _counter;

    public SpeakerIdentificationService(string modelPath, float matchThreshold, ILogger logger)
    {
        _logger = logger;
        _matchThreshold = matchThreshold;

        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = modelPath;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;

        _extractor = new SpeakerEmbeddingExtractor(config);

        _logger.LogInformation(
            "Speaker identification active. model='{Model}' dim={Dim} threshold={Threshold:F2}",
            modelPath, _extractor.Dim, _matchThreshold);
    }

    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate)
    {
        var durationSec = sampleRate > 0 ? segmentSamples.Length / (float)sampleRate : 0f;
        var embedding = ComputeEmbedding(segmentSamples, sampleRate);

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

            if (bestId is not null && bestSim >= _matchThreshold)
            {
                var matched = _speakers[bestId];
                matched.Update(embedding);
                var label = _displayLabels[bestId];

                _logger.LogInformation(
                    "Diarization match: {Label} sim={Sim:F3} dur={Dur:F2}s sims=[{Sims}]",
                    label, bestSim, durationSec, FormatSims(sims));

                return label;
            }

            _counter++;
            var internalId = $"spk_{_counter}";
            var newLabel = $"Speaker {_counter}";
            _speakers[internalId] = new SpeakerCentroid(embedding);
            _displayLabels[internalId] = newLabel;

            _logger.LogInformation(
                "Diarization new speaker: {Label} bestSim={BestSim:F3} threshold={Threshold:F2} dur={Dur:F2}s sims=[{Sims}]",
                newLabel,
                bestSim == float.NegativeInfinity ? 0f : bestSim,
                _matchThreshold,
                durationSec,
                FormatSims(sims));

            return newLabel;
        }
    }

    public bool Rename(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return false;

        lock (_lock)
        {
            string? internalId = null;
            foreach (var (id, label) in _displayLabels)
            {
                if (label == oldLabel) { internalId = id; break; }
            }
            if (internalId is null) return false;

            _displayLabels[internalId] = newLabel;
            _logger.LogInformation("Speaker renamed: '{Old}' → '{New}' (id={Id})", oldLabel, newLabel, internalId);
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _speakers.Clear();
            _displayLabels.Clear();
            _counter = 0;
            _logger.LogInformation("Speaker identification state reset");
        }
    }

    private float[] ComputeEmbedding(float[] samples, int sampleRate)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    public void Dispose()
    {
        lock (_lock)
        {
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
        public int Count;

        public SpeakerCentroid(float[] firstEmbedding)
        {
            Centroid = (float[])firstEmbedding.Clone();
            Count = 1;
        }

        public void Update(float[] embedding)
        {
            // Running mean: centroid = (count * centroid + embedding) / (count + 1)
            for (int i = 0; i < Centroid.Length; i++)
            {
                Centroid[i] = (Centroid[i] * Count + embedding[i]) / (Count + 1);
            }
            Count++;
        }
    }
}
