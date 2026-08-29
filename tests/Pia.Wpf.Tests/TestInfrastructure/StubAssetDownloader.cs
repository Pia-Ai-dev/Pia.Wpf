using Pia.Services.Assets;
using Pia.Services.Interfaces;

namespace Pia.Tests.TestInfrastructure;

/// <summary>
/// An <see cref="IAssetDownloader"/> for tests that must not dial out. Fails by default — the services
/// under test are expected to short-circuit on a cached file rather than to download.
/// </summary>
public sealed class StubAssetDownloader : IAssetDownloader
{
    private readonly Func<RuntimeAsset, string, Task<long>>? _handler;

    public StubAssetDownloader(Func<RuntimeAsset, string, Task<long>>? handler = null) => _handler = handler;

    public List<RuntimeAsset> Requested { get; } = [];

    public Task<long> DownloadAsync(
        RuntimeAsset asset,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requested.Add(asset);
        return _handler?.Invoke(asset, destinationPath)
               ?? Task.FromException<long>(new InvalidOperationException("no network"));
    }
}
