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

    [Fact]
    public async Task ConcurrentRefreshCallers_ReportOnlyTheOriginalSignOutReason()
    {
        using var handler = new BlockedRefreshHandler();
        var (sut, _) = CreateSut(HttpStatusCode.Unauthorized, handler: handler);
        var reasons = new List<AuthSignOutReason>();
        sut.SignedOutUnexpectedly += (_, e) => reasons.Add(e.Reason);

        var first = sut.GetAccessTokenAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queued = sut.GetAccessTokenAsync();
        handler.Release.SetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(sut.IsLoggedIn);
        Assert.Equal([AuthSignOutReason.SessionExpired], reasons);
        Assert.Equal(1, handler.RefreshCalls);
        Assert.Equal(1, handler.LogoutCalls);
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
        HttpStatusCode refreshStatus, string? decryptsTo = null, HttpMessageHandler? handler = null)
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
            .Returns(_ => new HttpClient(handler ?? new HandlerFactory(refreshStatus), disposeHandler: handler is null));

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

    private sealed class BlockedRefreshHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshCalls { get; private set; }
        public int LogoutCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh"))
            {
                RefreshCalls++;
                Entered.SetResult();
                await Release.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }

            LogoutCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
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
