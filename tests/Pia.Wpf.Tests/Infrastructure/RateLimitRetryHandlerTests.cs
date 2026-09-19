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
    // _lastRequestTime is static, so a shared host name would pace these tests against each other.
    private static string UniqueHost() => $"{Guid.NewGuid():N}.invalid";

    private static HttpMessageInvoker CreateInvoker(HttpMessageHandler inner)
        => new(new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance) { InnerHandler = inner });

    [Fact]
    public async Task SendAsync_ConsecutiveRequests_PacesThemApart()
    {
        var host = UniqueHost();
        var invoker = CreateInvoker(new StubHandler(HttpStatusCode.OK));

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v1/a"), CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v1/b"), CancellationToken.None);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v1/c"), CancellationToken.None);
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
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{UniqueHost()}/api/v1/a"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SendAsync_RateLimited_StillRetriesHonouringRetryAfter()
    {
        var inner = new RateLimitedOnceHandler(TimeSpan.FromMilliseconds(200));
        var invoker = CreateInvoker(inner);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"https://{UniqueHost()}/api/v1/a"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Attempts);
    }

    /// <summary>The measured failure: a burst of background sync PUTs queued ahead of one interactive LLM
    /// call through a single per-host FIFO, and the call timed out before it was ever sent.</summary>
    [Fact]
    public async Task ABurstOnOneEndpoint_DoesNotDelayAnother()
    {
        var host = UniqueHost();
        var invoker = CreateInvoker(new StubHandler(HttpStatusCode.OK));
        for (var i = 0; i < 8; i++)
        {
            await invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, $"https://{host}/api/v1/chats/{i}"), CancellationToken.None);
        }

        var sw = Stopwatch.StartNew();
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"https://{host}/api/ai/chat"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"the AI call must not pace behind the sync burst, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TwoEndpointsUnderOnePrefix_StillPaceTogether()
    {
        var host = UniqueHost();
        var invoker = CreateInvoker(new StubHandler(HttpStatusCode.OK));

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"https://{host}/api/v1/chats/1"), CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"https://{host}/api/v1/personas/2"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 50,
            $"one lane covers the whole sync surface, took {sw.ElapsedMilliseconds}ms");
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
