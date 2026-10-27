using System.IO;
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
using Pia.Shared.E2EE;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Once an account is known to need E2EE, a server answer of "off" must not make this device send plaintext.</summary>
public class E2EEDowngradeTests
{
    private const string Account = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private static string Token(string payloadJson) =>
        "eyJhbGciOiJIUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Theory]
    [InlineData("""{"sub":"x","e2ee":"1"}""", true)]
    [InlineData("""{"sub":"x","e2ee":"0"}""", false)]
    [InlineData("""{"sub":"x"}""", null)]
    [InlineData("""{"sub":"x","e2ee":true}""", null)]
    public void ReadE2EE_ReadsOnlyTheStringClaim(string payload, bool? expected) =>
        Assert.Equal(expected, AccessTokenClaims.ReadE2EE(Token(payload)));

    [Theory]
    [InlineData(null)]
    [InlineData("at")]
    [InlineData("a.!!!.c")]
    [InlineData("a.bm90IGpzb24.c")]
    public void ReadE2EE_MalformedToken_IsNull(string? token) =>
        Assert.Null(AccessTokenClaims.ReadE2EE(token));

    [Fact]
    public void Marker_SurvivesSignOutAndTheLocalDataWipe()
    {
        var settings = new AppSettings { SyncUserId = Account };
        Assert.True(settings.MarkE2EERequiredForAccount());

        settings.ClearAccountState();
        settings.ResetSyncedData();
        settings.SyncUserId = Account;

        Assert.True(settings.IsE2EERequiredForAccount());
    }

    [Fact]
    public void Marker_BelongsToOneAccount()
    {
        var settings = new AppSettings { SyncUserId = Account };
        settings.MarkE2EERequiredForAccount();

        settings.SyncUserId = "0f8fad5b-d9cb-469f-a165-70867728950e";
        Assert.False(settings.IsE2EERequiredForAccount());

        settings.SyncUserId = null;
        Assert.False(settings.MarkE2EERequiredForAccount());
    }

    [Fact]
    public void SignOut_OfAnEncryptedAccount_MarksItFirst()
    {
        var settings = new AppSettings { SyncUserId = Account, IsE2EEEnabled = true };

        settings.ClearAccountState();

        Assert.Equal([Account], settings.E2EERequiredAccountIds);
    }

    // A pushed policy could otherwise empty the list and reopen the downgrade.
    [Fact]
    public void Marker_IsOutOfPolicyReach() =>
        Assert.True(Pia.Shared.Policy.ClientPolicyContract.IsDenied(
            System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(nameof(AppSettings.E2EERequiredAccountIds))));

    [Fact]
    public void Unmark_ForgetsOnlyTheSignedInAccount()
    {
        var settings = new AppSettings { SyncUserId = Account, E2EERequiredAccountIds = [Account, "other"] };

        settings.UnmarkE2EERequiredForAccount();

        Assert.Equal(["other"], settings.E2EERequiredAccountIds);
    }

    // --- AuthService: every token is evidence ---

    [Fact]
    public async Task SignIn_WithTheE2EEClaim_MarksTheAccount()
    {
        var (auth, stored, server) = CreateAuth();
        server.LoginAccessToken = Token("""{"sub":"x","e2ee":"1"}""");

        await auth.LoginWithPasswordAsync("user@example.com", "pw");

        Assert.Equal([Account], stored.E2EERequiredAccountIds);
    }

    [Fact]
    public async Task SignIn_WithTheBodyFlag_MarksTheAccount()
    {
        var (auth, stored, server) = CreateAuth();
        server.LoginBodyE2EE = true;

        await auth.LoginWithPasswordAsync("user@example.com", "pw");

        Assert.Equal([Account], stored.E2EERequiredAccountIds);
    }

    [Fact]
    public async Task SignIn_WithE2EEOff_LeavesAnEarlierMarkInPlace()
    {
        var (auth, stored, server) = CreateAuth();
        stored.E2EERequiredAccountIds = [Account];
        server.LoginAccessToken = Token("""{"sub":"x","e2ee":"0"}""");

        await auth.LoginWithPasswordAsync("user@example.com", "pw");

        Assert.Equal([Account], stored.E2EERequiredAccountIds);
    }

    [Fact]
    public async Task SignIn_WithAnotherAccountsKeyLeftOver_MarksThatAccountNotTheNewOne()
    {
        const string previous = "0f8fad5b-d9cb-469f-a165-70867728950e";
        var (auth, stored, server) = CreateAuth();
        stored.SyncUserId = previous;
        stored.IsE2EEEnabled = true;
        server.LoginAccessToken = Token("""{"sub":"x","e2ee":"0"}""");

        await auth.LoginWithPasswordAsync("user@example.com", "pw");

        Assert.Equal([previous], stored.E2EERequiredAccountIds);
        Assert.False(stored.IsE2EEEnabled);
    }

    [Fact]
    public async Task Refresh_ReportingE2EE_MarksTheAccount()
    {
        var (auth, stored, server) = CreateAuth();
        await auth.LoginWithPasswordAsync("user@example.com", "pw");
        Assert.Empty(stored.E2EERequiredAccountIds);
        server.RefreshBodyE2EE = true;

        await auth.GetAccessTokenAsync(forceRefresh: true);

        Assert.Equal([Account], stored.E2EERequiredAccountIds);
    }

    // --- SyncClientService: the gate ---

    private readonly IAuthService _syncAuth = Substitute.For<IAuthService>();
    private readonly ISettingsService _syncSettings = Substitute.For<ISettingsService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IE2EEService _syncE2EE = Substitute.For<IE2EEService>();
    private readonly FailingHandler _syncServer = new();
    private readonly AppSettings _syncStored = new()
    {
        ServerUrl = "https://server.example",
        SyncEnabled = true,
        SyncUserId = Account,
    };

    private SyncClientService CreateSync()
    {
        _syncSettings.GetSettingsAsync().Returns(_syncStored);
        _syncAuth.IsLoggedIn.Returns(true);
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_syncServer, disposeHandler: false));
        var e2ee = _syncE2EE;
        return new SyncClientService(
            _syncAuth, _syncSettings, Substitute.For<ITemplateService>(),
            Substitute.For<IProviderService>(), Substitute.For<IHistoryService>(), Substitute.For<IMemoryService>(),
            new SyncMapper(dpapi, e2ee), factory,
            NullLogger<SyncClientService>.Instance,
            new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance),
            e2ee: e2ee,
            deviceMgmt: _deviceMgmt);
    }

    private void ServerSays(bool? enabled) =>
        _deviceMgmt.CheckE2EEStatusAsync().Returns(enabled is null ? null : new E2EEStatusResponse { IsEnabled = enabled.Value });

    [Fact]
    public async Task SyncNow_MarkedAccount_ServerSaysOff_SendsNothingAndReportsTheDowngrade()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        var raised = 0;
        sut.E2EEDowngradeChanged += (_, _) => raised++;

        Assert.Null(await sut.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.True(sut.IsE2EEDowngradeDetected);
        Assert.Equal(1, raised);
        await _syncAuth.DidNotReceive().GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SyncNow_MarkedAccount_StatusUnknown_SendsNothing()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(null);
        var sut = CreateSync();

        Assert.Null(await sut.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.False(sut.IsE2EEDowngradeDetected);
        await _syncAuth.DidNotReceive().GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SyncNow_MarkedAccount_ServerSaysOn_AsksForOnboarding()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(true);
        var sut = CreateSync();

        Assert.Null(await sut.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.True(sut.IsE2EEOnboardingRequired);
        Assert.False(sut.IsE2EEDowngradeDetected);
    }

    [Fact]
    public async Task FirstSyncMigration_MarkedAccount_ServerSaysOff_SendsNothing()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();

        await sut.PerformFirstSyncMigrationAsync();

        Assert.True(sut.IsE2EEDowngradeDetected);
        await _syncAuth.DidNotReceive().GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SyncNow_UnmarkedAccount_ServerSaysOff_GoesAhead()
    {
        ServerSays(false);
        var sut = CreateSync();

        await sut.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.False(sut.IsE2EEDowngradeDetected);
        await _syncAuth.Received().GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Downgrade_ClearsWhenTheServerReportsE2EEAgain()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.True(sut.IsE2EEDowngradeDetected);

        ServerSays(true);
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.False(sut.IsE2EEDowngradeDetected);
        Assert.True(sut.IsE2EEOnboardingRequired);
    }

    [Fact]
    public async Task Downgrade_ClearsOnceThisDeviceHoldsTheKey()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.True(sut.IsE2EEDowngradeDetected);

        _syncStored.IsE2EEEnabled = true;
        _syncE2EE.IsReady().Returns(true);
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.False(sut.IsE2EEDowngradeDetected);
    }

    [Fact]
    public async Task AllowUnencryptedSync_UnmarksTheAccountAndLetsSyncGoAhead()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.True(await sut.AllowUnencryptedSyncAsync(Account));
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.False(sut.IsE2EEDowngradeDetected);
        Assert.Empty(_syncStored.E2EERequiredAccountIds);
        await _syncAuth.Received().GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AllowUnencryptedSync_AfterTheDowngradeCleared_KeepsTheMark()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);
        ServerSays(true);
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);
        ServerSays(false);

        Assert.False(await sut.AllowUnencryptedSyncAsync(Account));

        Assert.Equal([Account], _syncStored.E2EERequiredAccountIds);
    }

    [Fact]
    public async Task AllowUnencryptedSync_WhenTheServerReportsE2EEAgain_KeepsTheMarkAndAsksForOnboarding()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);
        ServerSays(true);

        Assert.False(await sut.AllowUnencryptedSyncAsync(Account));

        Assert.Equal([Account], _syncStored.E2EERequiredAccountIds);
        Assert.False(sut.IsE2EEDowngradeDetected);
        Assert.True(sut.IsE2EEOnboardingRequired);
    }

    [Fact]
    public async Task AllowUnencryptedSync_ForAnAccountNoLongerSignedIn_KeepsTheMark()
    {
        _syncStored.E2EERequiredAccountIds = [Account];
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.False(await sut.AllowUnencryptedSyncAsync("0f8fad5b-d9cb-469f-a165-70867728950e"));

        Assert.Equal([Account], _syncStored.E2EERequiredAccountIds);
    }

    [Fact]
    public async Task SyncNow_WhenTheTokenRefreshMarksTheAccount_SendsNothing()
    {
        ServerSays(false);
        var sut = CreateSync();
        await sut.SyncNowAsync(TestContext.Current.CancellationToken);
        var before = _syncServer.Requests;
        _syncAuth.GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>()).Returns(_ =>
        {
            _syncStored.MarkE2EERequiredForAccount();
            return "token";
        });
        ServerSays(true);

        Assert.Null(await sut.SyncNowAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before, _syncServer.Requests);
        Assert.True(sut.IsE2EEOnboardingRequired);
    }

    [Fact]
    public async Task FirstSyncMigration_WhenTheTokenRefreshMarksTheAccount_SendsNothing()
    {
        ServerSays(false);
        _syncAuth.GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>()).Returns(_ =>
        {
            _syncStored.MarkE2EERequiredForAccount();
            return "token";
        });
        var sut = CreateSync();

        await sut.PerformFirstSyncMigrationAsync();

        Assert.Equal(0, _syncServer.Requests);
        Assert.True(sut.IsE2EEDowngradeDetected);
    }

    private static (AuthService Auth, AppSettings Stored, AuthHandler Server) CreateAuth()
    {
        var stored = new AppSettings { ServerUrl = "https://server.example" };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);
        var server = new AuthHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(server));
        var auth = new AuthService(
            settings, new PassthroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);
        return (auth, stored, server);
    }

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class AuthHandler : HttpMessageHandler
    {
        public string LoginAccessToken { get; set; } = "at";
        public bool LoginBodyE2EE { get; set; }
        public bool RefreshBodyE2EE { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh")
                ? $$$"""{"accessToken":"at2","refreshToken":"rt2","expiresIn":3600,"isE2EEEnabled":{{{Json(RefreshBodyE2EE)}}}}"""
                : $$$"""{"accessToken":"{{{LoginAccessToken}}}","refreshToken":"rt","expiresIn":3600,"isE2EEEnabled":{{{Json(LoginBodyE2EE)}}},"user":{"id":"{{{Account}}}","email":"user@example.com","displayName":"U","provider":"local"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static string Json(bool value) => value ? "true" : "false";
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
