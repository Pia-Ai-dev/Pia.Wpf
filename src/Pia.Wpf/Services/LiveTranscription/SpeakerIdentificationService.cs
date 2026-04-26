using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Live speaker identification for a single meeting. Wraps sherpa-onnx's
/// <see cref="SpeakerEmbeddingExtractor"/> + <see cref="SpeakerEmbeddingManager"/>.
///
/// The manager is keyed by an internal id (<c>spk_1</c>, <c>spk_2</c>, …) while the UI
/// sees a display label ("Speaker 1", or whatever the user renames it to). This split
/// makes <see cref="Rename"/> a single dictionary update rather than a remove/re-add
/// dance against the native manager.
/// </summary>
public sealed class SpeakerIdentificationService : ISpeakerIdentificationService
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly ILogger _logger;
    private readonly float _matchThreshold;

    private readonly object _lock = new();
    private SpeakerEmbeddingManager _manager;
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
        _manager = new SpeakerEmbeddingManager(_extractor.Dim);

        _logger.LogInformation(
            "Speaker identification active. model='{Model}' dim={Dim} threshold={Threshold:F2}",
            modelPath, _extractor.Dim, _matchThreshold);
    }

    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate)
    {
        var embedding = ComputeEmbedding(segmentSamples, sampleRate);

        lock (_lock)
        {
            var matchedId = _manager.Search(embedding, _matchThreshold);
            if (!string.IsNullOrEmpty(matchedId) && _displayLabels.TryGetValue(matchedId, out var existingLabel))
            {
                return existingLabel;
            }

            _counter++;
            var internalId = $"spk_{_counter}";
            var label = $"Speaker {_counter}";
            _manager.Add(internalId, embedding);
            _displayLabels[internalId] = label;

            _logger.LogInformation("New speaker registered: {Label} (id={Id})", label, internalId);
            return label;
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
            _manager.Dispose();
            _manager = new SpeakerEmbeddingManager(_extractor.Dim);
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
            _manager.Dispose();
            _extractor.Dispose();
        }
    }
}
