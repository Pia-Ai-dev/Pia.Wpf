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

public class AuthServiceLoginPolicyTests
{
    private const string RefusedMessage = "refused by policy";

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accessToken":"at","refreshToken":"rt","expiresIn":3600,"user":{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","email":"a@b.c","displayName":"A","provider":"local"}}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private static AuthService CreateSut(
        AppSettings stored, RecordingHandler handler, IPolicyService? policy)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var localization = Substitute.For<ILocalizationService>();
        localization["Sync_LoginProviderDisabledByPolicy"].Returns(RefusedMessage);

        return new AuthService(
            settings, new PassthroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            localization, NullLogger<AuthService>.Instance, policy);
    }

    private static IPolicyService PolicyAllowing(params string[] providers)
    {
        var policy = Substitute.For<IPolicyService>();
        policy.IsLoginProviderAllowed(Arg.Any<string>())
            .Returns(call => providers.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase));
        return policy;
    }

    private static AppSettings SignedInWith(string? provider) => new()
    {
        ServerUrl = "https://server.example",
        SyncEnabled = true,
        EncryptedRefreshToken = "stored-rt",
        SyncProvider = provider,
        SyncUserEmail = "a@b.c",
    };

    [Fact]
    public async Task PasswordLogin_IsRefusedWithoutAServerCall_WhenLocalIsDisallowed()
    {
        var stored = new AppSettings { ServerUrl = "https://server.example" };
        var handler = new RecordingHandler();
        var sut = CreateSut(stored, handler, PolicyAllowing("entraid"));

        var (success, error) = await sut.LoginWithPasswordAsync("a@b.c", "pw");

        Assert.False(success);
        Assert.Equal(RefusedMessage, error);
        Assert.Empty(handler.Paths);
        Assert.False(stored.SyncEnabled);
    }

    [Fact]
    public async Task OAuthLogin_IsRefused_WhenTheProviderIsDisallowed()
    {
        var handler = new RecordingHandler();
        var sut = CreateSut(new AppSettings { ServerUrl = "https://server.example" }, handler, PolicyAllowing("entraid"));

        var (success, error) = await sut.LoginAsync("google");

        Assert.False(success);
        Assert.Equal(RefusedMessage, error);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task PasswordLogin_Proceeds_WhenLocalIsAllowed()
    {
        var stored = new AppSettings { ServerUrl = "https://server.example" };
        var sut = CreateSut(stored, new RecordingHandler(), PolicyAllowing("local"));

        var (success, error) = await sut.LoginWithPasswordAsync("a@b.c", "pw");

        Assert.True(success, error);
        Assert.True(stored.SyncEnabled);
    }

    [Fact]
    public async Task StoredSession_IsSignedOutAndRevoked_WhenItsProviderIsNoLongerAllowed()
    {
        var stored = SignedInWith("google");
        var handler = new RecordingHandler();
        var policy = PolicyAllowing("entraid");
        var sut = CreateSut(stored, handler, policy);

        Assert.Null(await sut.GetAccessTokenAsync());

        Assert.False(sut.IsLoggedIn);
        Assert.False(stored.SyncEnabled);
        Assert.Null(stored.EncryptedRefreshToken);
        Assert.Null(stored.SyncProvider);
        Assert.Contains("/auth/logout", handler.Paths);
        await policy.Received(1).ClearServerPolicyAsync();
    }

    [Fact]
    public async Task StoredSession_IsKept_WhenItsProviderIsAllowed()
    {
        var stored = SignedInWith("entraid");
        var sut = CreateSut(stored, new RecordingHandler(), PolicyAllowing("entraid"));

        await sut.GetAccessTokenAsync();

        Assert.True(sut.IsLoggedIn);
        Assert.True(stored.SyncEnabled);
    }

    [Fact]
    public async Task StoredSession_WithoutARecordedProvider_IsKept()
    {
        var stored = SignedInWith(null);
        var sut = CreateSut(stored, new RecordingHandler(), PolicyAllowing("entraid"));

        await sut.GetAccessTokenAsync();

        Assert.True(sut.IsLoggedIn);
    }
}
