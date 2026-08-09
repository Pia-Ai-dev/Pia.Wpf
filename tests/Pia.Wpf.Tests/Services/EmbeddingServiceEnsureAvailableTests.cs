using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class EmbeddingServiceEnsureAvailableTests
{
    [Fact]
    public async Task EnsureAvailableAsync_ModelAlreadyAvailable_ReturnsTrueWithoutDownload()
    {
        var factory = new SimpleHttpClientFactory();
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, factory);

        if (!svc.IsModelAvailable)
        {
            // Skip: this test only applies when the model is already on disk.
            return;
        }

        var ok = await svc.EnsureAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.Equal(0, factory.RequestCount);
    }

    [Fact]
    public async Task EnsureAvailableAsync_DownloadFailure_ReturnsFalse()
    {
        var factory = new FailingHttpClientFactory();
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, factory);

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
        var factory = new FailingHttpClientFactory();
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, factory);

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

    private class SimpleHttpClientFactory : IHttpClientFactory
    {
        public int RequestCount;
        public HttpClient CreateClient(string name)
        {
            RequestCount++;
            return new HttpClient();
        }
    }

    private class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailingHandler());
    }

    private class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated failure");
    }
}
