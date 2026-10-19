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

/// <summary>A refresh that cannot succeed ends the session with a reason instead of leaving the
/// app idle "signed in" with nothing to authenticate calls; a transient refusal keeps the
/// session.</summary>
public class AuthServiceRefreshSignOutTests
{
    [Fact]
    public async Task ARefresh401_ClearsTheStoredTokensSoNoLaterCycleRevivesThem()
    {
        var (sut, stored) = CreateSut(HttpStatusCode.Unauthorized);

        await sut.GetAccessTokenAsync();

        Assert.Null(stored.EncryptedRefreshToken);
        Assert.Null(stored.SyncUserEmail);
    }

    [Fact]
    public async Task ARefresh401_SignsOutWithTheSessionExpiredReason()
    {
        var (sut, _) = CreateSut(HttpStatusCode.Unauthorized);
        var reasons = new List<AuthSignOutReason>();
        sut.SignedOutUnexpectedly += (_, e) => reasons.Add(e.Reason);

        Assert.True(sut.IsLoggedIn);
        Assert.Null(await sut.GetAccessTokenAsync());

        Assert.False(sut.IsLoggedIn);
        Assert.Equal([AuthSignOutReason.SessionExpired], reasons);
    }

    [Fact]
    public async Task ARefresh403_SignsOutBecauseTheServerWillNotChangeItsMind()
    {
        var (sut, _) = CreateSut(HttpStatusCode.Forbidden);
        var reasons = new List<AuthSignOutReason>();
        sut.SignedOutUnexpectedly += (_, e) => reasons.Add(e.Reason);

        await sut.GetAccessTokenAsync();

        Assert.False(sut.IsLoggedIn);
        Assert.Equal([AuthSignOutReason.ServerRejectedSession], reasons);
    }

    // A server outage or a rate limit says nothing about the session's validity: stay signed in
    // and let the next call refresh again, rather than logging the user out of a hiccup.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ATransientRefreshFailure_KeepsTheSessionForTheNextAttempt(HttpStatusCode status)
    {
        var (sut, _) = CreateSut(status);
        var reasons = new List<AuthSignOutReason>();
        sut.SignedOutUnexpectedly += (_, e) => reasons.Add(e.Reason);

        await sut.GetAccessTokenAsync();

        Assert.True(sut.IsLoggedIn);
        Assert.Empty(reasons);
    }

    // A stored session that cannot be opened here can never be refreshed; that is not transient.
    [Fact]
    public async Task AnUndecryptableStoredSession_SignsOutWithItsOwnReason()
    {
        var (sut, _) = CreateSut(HttpStatusCode.OK, decryptsTo: "");
        var reasons = new List<AuthSignOutReason>();
        sut.SignedOutUnexpectedly += (_, e) => reasons.Add(e.Reason);

        Assert.True(sut.IsLoggedIn);
        Assert.Null(await sut.GetAccessTokenAsync());

        Assert.False(sut.IsLoggedIn);
        Assert.Equal([AuthSignOutReason.LocalSessionUnusable], reasons);
    }

    private static (AuthService Sut, AppSettings Settings) CreateSut(
        HttpStatusCode refreshStatus, string? decryptsTo = null)
    {
        var stored = new AppSettings
        {
            ServerUrl = "https://server.example",
            SyncEnabled = true,
            EncryptedRefreshToken = "rt",
            SyncUserEmail = "user@example.com",
        };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new HandlerFactory(refreshStatus), disposeHandler: true));

        var sut = new AuthService(
            settings, new FixedDpapi(NullLogger<DpapiHelper>.Instance, decryptsTo), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);

        return (sut, stored);
    }

    private sealed class FixedDpapi(ILogger<DpapiHelper> logger, string? decrypted) : DpapiHelper(logger)
    {
        public override string Decrypt(string encryptedText) => decrypted ?? encryptedText;

        public override string Encrypt(string plainText) => plainText;
    }

    private sealed class HandlerFactory(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath.EndsWith("/auth/refresh") == true && status == HttpStatusCode.OK
                ? """{"accessToken":"at","refreshToken":"rt","expiresIn":3600}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
