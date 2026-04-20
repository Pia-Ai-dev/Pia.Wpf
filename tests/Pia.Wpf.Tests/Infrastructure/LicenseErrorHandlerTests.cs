using System.Net;
using System.Net.Http;
using System.Text;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Shared.Licensing;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class LicenseErrorHandlerTests
{
    private readonly ILicenseErrorBus _bus = Substitute.For<ILicenseErrorBus>();

    private LicenseErrorHandler CreateHandler(HttpMessageHandler innerHandler)
        => new(_bus) { InnerHandler = innerHandler };

    private static HttpMessageInvoker CreateInvoker(LicenseErrorHandler handler) => new(handler);

    private static HttpRequestMessage CreateRequest()
        => new(HttpMethod.Get, "https://example.com/api/test");

    [Fact]
    public async Task SendAsync_403WithKnownError_PublishesOnce()
    {
        var json = """{"error":"no_license","setupUrl":"/admin/setup"}""";
        var handler = CreateHandler(new StubHandler(HttpStatusCode.Forbidden, json, "application/json"));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _bus.Received(1).Publish(Arg.Is<LicenseErrorResponse>(e => e.Error == "no_license"));
    }

    [Fact]
    public async Task SendAsync_403WithFeatureNotLicensed_PublishesFeature()
    {
        var json = """{"error":"feature_not_licensed","feature":"Sync"}""";
        var handler = CreateHandler(new StubHandler(HttpStatusCode.Forbidden, json, "application/json"));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _bus.Received(1).Publish(Arg.Is<LicenseErrorResponse>(e =>
            e.Error == "feature_not_licensed" && e.Feature == "Sync"));
    }

    [Fact]
    public async Task SendAsync_200_DoesNotPublish()
    {
        var handler = CreateHandler(new StubHandler(HttpStatusCode.OK, null, null));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _bus.DidNotReceive().Publish(Arg.Any<LicenseErrorResponse>());
    }

    [Fact]
    public async Task SendAsync_403WithUnknownErrorKey_DoesNotPublish()
    {
        var json = """{"error":"something_else"}""";
        var handler = CreateHandler(new StubHandler(HttpStatusCode.Forbidden, json, "application/json"));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _bus.DidNotReceive().Publish(Arg.Any<LicenseErrorResponse>());
    }

    [Fact]
    public async Task SendAsync_403WithNonJsonBody_DoesNotPublish()
    {
        var handler = CreateHandler(new StubHandler(HttpStatusCode.Forbidden, "forbidden", "text/plain"));
        var invoker = CreateInvoker(handler);

        await invoker.SendAsync(CreateRequest(), CancellationToken.None);

        _bus.DidNotReceive().Publish(Arg.Any<LicenseErrorResponse>());
    }

    [Fact]
    public async Task SendAsync_ResponseFlowsThroughToCaller()
    {
        var json = """{"error":"no_license"}""";
        var handler = CreateHandler(new StubHandler(HttpStatusCode.Forbidden, json, "application/json"));
        var invoker = CreateInvoker(handler);

        var response = await invoker.SendAsync(CreateRequest(), CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(json, body);
    }

    [Fact]
    public async Task SendAsync_InnerThrows_DoesNotPublishAndRethrows()
    {
        var expected = new HttpRequestException("boom");
        var handler = CreateHandler(new ThrowingHandler(expected));
        var invoker = CreateInvoker(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(CreateRequest(), CancellationToken.None));
        _bus.DidNotReceive().Publish(Arg.Any<LicenseErrorResponse>());
    }

    private class StubHandler(HttpStatusCode statusCode, string? body, string? contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, contentType ?? "text/plain");
            }
            return Task.FromResult(response);
        }
    }

    private class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw exception;
    }
}
