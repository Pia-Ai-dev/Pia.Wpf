using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>Production <see cref="IEmbeddingExtractor"/> over sherpa-onnx.</summary>
public sealed class SherpaEmbeddingExtractor : IEmbeddingExtractor
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public SherpaEmbeddingExtractor(string modelPath)
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = modelPath;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    public int Dim => _extractor.Dim;

    public float[] Compute(float[] samples, int sampleRate)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    public void Dispose() => _extractor.Dispose();
}
