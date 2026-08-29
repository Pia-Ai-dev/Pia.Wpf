using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A server that cannot answer is not a server saying "nothing owed" — reading it as the latter clears a
/// declaration the account still owes, and the user never sees the form again.
/// </summary>
public class AuthServiceBusinessProfileProbeTests
{
    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class ScriptedHandler(HttpStatusCode meStatus, string meBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isRefresh = request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal);
            return Task.FromResult(isRefresh
                ? Json(HttpStatusCode.OK, """{"accessToken":"at","refreshToken":"rt","expiresIn":3600}""")
                : Json(meStatus, meBody));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static AuthService CreateSut(HttpStatusCode meStatus, string meBody = "{}")
    {
        var stored = new AppSettings
        {
            ServerUrl = "https://server.example",
            SyncEnabled = true,
            EncryptedRefreshToken = "rt"
        };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);

        var handler = new ScriptedHandler(meStatus, meBody);
        var factory = Substitute.For<IHttpClientFactory>();
        // A fresh client per call: AuthService disposes each one, so a shared instance dies on the second probe.
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new AuthService(
            settings, new PassthroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);
    }

    [Fact]
    public async Task AServerThatCannotAnswer_ProbesToNull()
    {
        var sut = CreateSut(HttpStatusCode.BadGateway);

        Assert.Null(await sut.RequiresBusinessProfileAsync());
    }

    [Fact]
    public async Task AnUnauthorizedProbe_IsNotAnAnswer()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized);

        Assert.Null(await sut.RequiresBusinessProfileAsync());
    }

    /// <summary>Non-vacuity for both nulls: the same wiring does carry a real answer through.</summary>
    [Fact]
    public async Task AServerSayingTheDeclarationIsOwed_ProbesToTrue()
    {
        var sut = CreateSut(HttpStatusCode.OK, """{"requiresBusinessProfile":true}""");

        Assert.True(await sut.RequiresBusinessProfileAsync());
    }

    [Fact]
    public async Task AServerSayingNothingIsOwed_ProbesToFalse()
    {
        var sut = CreateSut(HttpStatusCode.OK, """{"requiresBusinessProfile":false}""");

        Assert.False(await sut.RequiresBusinessProfileAsync());
    }

    /// <summary>Both ViewModels re-read the flag straight after submitting, so a stale true traps the user.</summary>
    [Fact]
    public async Task SubmittingTheDeclaration_ClearsTheFlagTheLoginSet()
    {
        var sut = CreateLoginSut();

        Assert.True((await sut.LoginWithPasswordAsync("a@example.com", "pw")).Success);
        Assert.True(sut.RequiresBusinessProfile);

        Assert.True((await sut.SubmitBusinessProfileAsync("Contoso GmbH")).Success);
        Assert.False(sut.RequiresBusinessProfile);
    }

    private sealed class LoginThenSubmitHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/auth/login/local", StringComparison.Ordinal)
                ? """
                  {"accessToken":"at","refreshToken":"rt","expiresIn":3600,
                   "user":{"id":"0195c0de-0000-7000-8000-000000000001","email":"a@example.com",
                           "provider":"local","requiresBusinessProfile":true}}
                  """
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static AuthService CreateLoginSut()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://server.example" });

        var handler = new LoginThenSubmitHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new AuthService(
            settings, new PassthroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);
    }
}
