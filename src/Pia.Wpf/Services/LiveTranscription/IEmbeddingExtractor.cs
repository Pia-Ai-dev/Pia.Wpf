namespace Pia.Services.LiveTranscription;

/// <summary>
/// Seam over the native speaker-embedding extractor so the adaptive diarizer can be unit-tested
/// without an ONNX model. Production implementation: <see cref="SherpaEmbeddingExtractor"/>.
/// </summary>
public interface IEmbeddingExtractor : IDisposable
{
    /// <summary>Embedding dimensionality.</summary>
    int Dim { get; }

    /// <summary>Computes the voice embedding for a 16 kHz mono float32 segment.</summary>
    float[] Compute(float[] samples, int sampleRate);
}
