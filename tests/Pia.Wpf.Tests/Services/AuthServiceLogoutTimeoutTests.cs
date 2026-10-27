using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class AuthServiceLogoutTimeoutTests
{
    [Fact]
    public async Task AHangingLogoutCall_GivesUpAtTheBound_AndStillClearsLocalState()
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
        var handler = new HangingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var sut = new AuthService(
            settings, new PassThroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance)
        {
            LogoutTimeout = TimeSpan.FromMilliseconds(200),
        };
        Assert.True(sut.IsLoggedIn);

        var elapsed = Stopwatch.StartNew();
        await sut.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(handler.WasCancelled);
        Assert.False(sut.IsLoggedIn);
        Assert.Null(stored.EncryptedRefreshToken);
        Assert.Null(stored.SyncUserEmail);
    }

    private sealed class PassThroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Decrypt(string encryptedText) => encryptedText;

        public override string Encrypt(string plainText) => plainText;
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public bool WasCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
