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
/// Only a logout clears the chat backfill marker, so a stale one would suppress this account's whole
/// history upload for good.
/// </summary>
public class AuthServiceChatBackfillMarkerTests
{
    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accessToken":"at","refreshToken":"rt","expiresIn":3600,"user":{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","email":"a@b.c","displayName":"A","provider":"local"}}""",
                    Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task ASuccessfulLogin_ReopensTheChatBackfillGate()
    {
        var stored = new AppSettings
        {
            ServerUrl = "https://server.example",
            AssistantChatsBackfilledAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new LoginHandler()));

        var sut = new AuthService(
            settings, new PassthroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);

        var (success, error) = await sut.LoginWithPasswordAsync("a@b.c", "pw");

        Assert.True(success, error);
        Assert.True(stored.SyncEnabled);
        Assert.Null(stored.AssistantChatsBackfilledAt);
    }
}
