using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public class EmbeddingServiceEnsureAvailableTests
{
    [Fact]
    public async Task EnsureAvailableAsync_ModelAlreadyAvailable_ReturnsTrueWithoutDownload()
    {
        var downloader = new StubAssetDownloader();
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, downloader);

        if (!svc.IsModelAvailable)
        {
            // Skip: this test only applies when the model is already on disk.
            return;
        }

        var ok = await svc.EnsureAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.Empty(downloader.Requested);
    }

    [Fact]
    public async Task EnsureAvailableAsync_DownloadFailure_ReturnsFalse()
    {
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, new StubAssetDownloader());

        if (svc.IsModelAvailable)
        {
            // Test only meaningful when model is missing.
            return;
        }

        var ok = await svc.EnsureAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(ok);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, new StubAssetDownloader());

        if (svc.IsModelAvailable)
        {
            // Test only meaningful when model is missing — cancellation can only happen during download.
            return;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            svc.GenerateEmbeddingAsync("test", cts.Token));
    }
}
