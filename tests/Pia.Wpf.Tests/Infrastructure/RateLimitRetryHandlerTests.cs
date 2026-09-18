using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class RateLimitRetryHandlerTests
{
    // _lastRequestTime is static and keyed by host, so a shared host name would pace these tests
    // against each other.
    private static string UniqueHost() => $"{Guid.NewGuid():N}.invalid";

    private static HttpMessageInvoker CreateInvoker(HttpMessageHandler inner)
        => new(new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance) { InnerHandler = inner });

    [Fact]
    public async Task SendAsync_ConsecutiveRequests_PacesThemApart()
    {
        var host = UniqueHost();
        var invoker = CreateInvoker(new StubHandler(HttpStatusCode.OK));

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/a"), CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/b"), CancellationToken.None);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/c"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"two gaps at 100ms should cost at least 150ms, took {sw.ElapsedMilliseconds}ms");
        // At the former 500ms this was over a second; the ceiling is what proves the pre-delay was lowered
        // rather than just still present.
        Assert.True(sw.ElapsedMilliseconds < 600,
            $"two gaps should stay well under the former 500ms-per-round cost, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SendAsync_FirstRequestToHost_IsNotDelayed()
    {
        var invoker = CreateInvoker(new StubHandler(HttpStatusCode.OK));

        var sw = Stopwatch.StartNew();
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{UniqueHost()}/a"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SendAsync_RateLimited_StillRetriesHonouringRetryAfter()
    {
        var inner = new RateLimitedOnceHandler(TimeSpan.FromMilliseconds(200));
        var invoker = CreateInvoker(inner);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"https://{UniqueHost()}/a"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Attempts);
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class RateLimitedOnceHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts > 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return Task.FromResult(response);
        }
    }
}
