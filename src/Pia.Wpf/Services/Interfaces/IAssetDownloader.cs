using Pia.Services.Assets;

namespace Pia.Services.Interfaces;

/// <summary>
/// The one path every cached runtime artifact takes to disk: our mirror first, the upstream host if
/// that fails. Callers name the asset and the destination; which host answered is not their business.
/// </summary>
public interface IAssetDownloader
{
    /// <summary>
    /// Streams <paramref name="asset"/> to <paramref name="destinationPath"/>, overwriting it, and
    /// returns the bytes written. Throws on failure of both hosts; a cancelled token is never a
    /// reason to try the fallback.
    /// </summary>
    Task<long> DownloadAsync(
        RuntimeAsset asset,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
