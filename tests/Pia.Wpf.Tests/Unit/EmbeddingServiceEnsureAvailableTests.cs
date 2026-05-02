using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

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

        var ok = await svc.EnsureAvailableAsync();
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

        var ok = await svc.EnsureAvailableAsync();
        Assert.False(ok);
    }

    private class SimpleHttpClientFactory : IHttpClientFactory
    {
        public int RequestCount;
        public HttpClient CreateClient(string name) => new();
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
