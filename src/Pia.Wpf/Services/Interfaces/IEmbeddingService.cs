namespace Pia.Services.Interfaces;

public interface IEmbeddingService
{
    bool IsModelAvailable { get; }
    Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default);
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    byte[] FloatsToBytes(float[] embedding);
    float[] BytesToFloats(byte[] bytes);
}
