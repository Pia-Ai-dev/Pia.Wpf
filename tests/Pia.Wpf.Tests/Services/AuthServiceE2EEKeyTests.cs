using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The master key belongs to the signed-in account: signing out forgets it, so the next account signing
/// in on this device cannot encrypt under it.</summary>
public class AuthServiceE2EEKeyTests
{
    private const string AccountA = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string AccountB = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private readonly AppSettings _stored = new() { ServerUrl = "https://server.example" };
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly PassthroughDpapi _dpapi = new(NullLogger<DpapiHelper>.Instance);
    private readonly LoginHandler _server = new();
    private readonly AuthService _auth;
    private readonly E2EEService _e2ee;

    public AuthServiceE2EEKeyTests()
    {
        _settings.GetSettingsAsync().Returns(_stored);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_server));

        _auth = new AuthService(
            _settings, _dpapi, factory, Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);
        _e2ee = new E2EEService(
            new CryptoService(), Substitute.For<IDeviceKeyService>(), _dpapi, _settings,
            NullLogger<E2EEService>.Instance);
    }

    [Fact]
    public async Task SigningOut_LeavesNoKeyOnTheDevice()
    {
        await SignInAsync(AccountA);
        await EnableE2EEAsync();

        await _auth.LogoutAsync();

        Assert.False(_e2ee.IsReady());
        Assert.Null(_e2ee.LoadUmk());
        Assert.Null(_stored.E2EEEncryptedUmk);
        Assert.False(_stored.IsE2EEEnabled);
    }

    [Fact]
    public async Task AnotherAccountSigningIn_FindsNoKeyOfThePreviousAccount()
    {
        await SignInAsync(AccountA);
        await EnableE2EEAsync();
        Assert.True(_e2ee.IsReady());

        await _auth.LogoutAsync();
        await SignInAsync(AccountB);

        Assert.Equal(AccountB, _stored.SyncUserId);
        Assert.False(_e2ee.IsReady());
        Assert.Null(_e2ee.LoadUmk());
    }

    // A build that signed out without forgetting the key leaves it behind with no owner recorded, so a sign-in
    // cannot tell whose it is and must not keep it.
    [Fact]
    public async Task SigningIn_OnADeviceStillHoldingAnOwnerlessKey_DropsIt()
    {
        await EnableE2EEAsync();
        Assert.Null(_stored.SyncUserId);

        await SignInAsync(AccountB);

        Assert.False(_e2ee.IsReady());
        Assert.Null(_e2ee.LoadUmk());
    }

    private async Task SignInAsync(string userId)
    {
        _server.UserId = userId;
        var (success, error) = await _auth.LoginWithPasswordAsync("user@example.com", "pw");
        Assert.True(success, error);
    }

    // What BootstrapFirstDeviceAsync leaves behind locally.
    private async Task EnableE2EEAsync()
    {
        await _e2ee.GenerateAndStoreUmkAsync();
        _stored.IsE2EEEnabled = true;
        _stored.E2EEUmkVersion = 1;
        _stored.E2EERecoveryConfigured = true;
    }

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        public string UserId { get; set; } = AccountA;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$$"""{"accessToken":"at","refreshToken":"rt","expiresIn":3600,"user":{"id":"{{{UserId}}}","email":"user@example.com","displayName":"U","provider":"local"}}""",
                    Encoding.UTF8, "application/json")
            });
    }
}
