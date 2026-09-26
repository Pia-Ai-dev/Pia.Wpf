namespace Pia.Tests.Services;

using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.Interfaces;
using Xunit;
using Xunit.Sdk;

public class CreditStatusServiceTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    public CreditStatusServiceTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://pia.example/" });
        _auth.GetAccessTokenAsync().Returns("at");
    }

    [Fact]
    public async Task ALimitedAnswer_DeserializesEverySection()
    {
        var (sut, handler) = Create(HttpStatusCode.OK, """
            { "limited": true, "suspended": true,
              "hourly": { "limit": 50, "used": 3 },
              "weekly": { "limit": 1000, "used": 620, "resetsAt": "2026-09-27T22:00:00Z" },
              "pool": { "total": 3000, "used": 1200, "remaining": 1800, "resetsAt": "2026-09-27T22:00:00Z" },
              "topUp": { "granted": 25000, "consumed": 8300, "remaining": 16700, "isActive": true },
              "groupCap": { "weekly": { "limit": 20000, "used": 7400, "resetsAt": "2026-09-27T22:00:00Z" } } }
            """);

        var status = await sut.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.True(status.Limited);
        Assert.True(status.Suspended);
        Assert.Equal(new CreditWindowDto(50, 3, null), status.Hourly);
        Assert.Null(status.Daily);
        Assert.Equal(new DateTime(2026, 9, 27, 22, 0, 0, DateTimeKind.Utc), status.Weekly!.ResetsAt);
        Assert.Equal(1800, status.Pool!.Remaining);
        Assert.Equal(new CreditTopUpDto(25000, 8300, 16700), status.TopUp);
        Assert.Null(status.GroupCap!.Daily);
        Assert.Equal("https://pia.example/api/ai/credits", handler.LastUri);
        Assert.Equal("Bearer at", handler.LastAuthorization);
    }

    [Fact]
    public async Task AnUnlimitedAnswer_IsReturnedAsSuch()
    {
        var (sut, _) = Create(HttpStatusCode.OK, """{ "limited": false }""");

        var status = await sut.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.False(status.Limited);
        Assert.Null(status.Weekly);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusalOrAnOlderServer_AnswersNull(HttpStatusCode status)
    {
        var (sut, _) = Create(status, """{ "error": "x" }""");

        Assert.Null(await sut.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnreadableJson_AnswersNull()
    {
        var (sut, _) = Create(HttpStatusCode.OK, "<html>");

        Assert.Null(await sut.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANetworkFailure_AnswersNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ThrowingHandler(), disposeHandler: false));
        var sut = new CreditStatusService(_settings, _auth, factory, NullLogger<CreditStatusService>.Instance);

        Assert.Null(await sut.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoToken_AnswersNullWithoutARequest()
    {
        var (sut, handler) = Create(HttpStatusCode.OK, """{ "limited": true }""");
        _auth.GetAccessTokenAsync().Returns((string?)null);

        Assert.Null(await sut.GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task NoServerUrl_AnswersNullWithoutARequest()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = null });
        var (sut, handler) = Create(HttpStatusCode.OK, """{ "limited": true }""");

        Assert.Null(await sut.GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(handler.LastUri);
    }

    private (CreditStatusService Sut, ScriptedHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler(status, body);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return (new CreditStatusService(_settings, _auth, factory, NullLogger<CreditStatusService>.Instance), handler);
    }

    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("unreachable");
    }
}
