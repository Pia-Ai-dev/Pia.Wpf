using Pia.Services.Interfaces;

namespace Pia.Tests.TestInfrastructure;

// Deterministic stand-in for the real model: identical text always yields an identical vector, and
// distinct text yields a near-orthogonal one, so cosine still discriminates. Pass pins to force two
// texts onto the same basis vector when a test needs a chosen similarity band.
internal sealed class StubEmbeddingService : IEmbeddingService
{
    private const int Dim = 16;

    private readonly Dictionary<string, int> _pins;

    public StubEmbeddingService(Dictionary<string, int>? pins = null)
        => _pins = pins ?? new Dictionary<string, int>();

    public bool IsModelAvailable => true;

    public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_pins.TryGetValue(text, out var pinned))
        {
            var pinnedVec = new float[Dim];
            pinnedVec[pinned % Dim] = 1f;
            return Task.FromResult(pinnedVec);
        }

        // Fills every dimension from the hash, so an unpinned vector stays near-orthogonal to the
        // axis-aligned pinned ones instead of colliding with whichever axis it landed on.
        var vec = new float[Dim];
        var h = Fnv1a(text);
        for (var i = 0; i < Dim; i++)
        {
            h = (h ^ (uint)(i * 0x9e3779b9)) * 16777619u;
            vec[i] = ((h & 0xffff) / 32767.5f) - 1f;
        }
        return Task.FromResult(vec);
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261u;
        foreach (var c in s)
        {
            h = (h ^ c) * 16777619u;
        }
        return h;
    }

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
