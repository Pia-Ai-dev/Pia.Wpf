using Pia.Services.Exceptions;
using Pia.Services.Interfaces;

namespace Pia.Tests.TestInfrastructure;

// A machine whose ONNX Runtime cannot load: the model is on disk, but every embedding fails.
internal sealed class UnavailableEmbeddingService : IEmbeddingService
{
    public bool IsModelAvailable => true;

    public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromException<float[]>(new EmbeddingUnavailableException(
            "ONNX Runtime could not be loaded.", new DllNotFoundException()));

    public byte[] FloatsToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
