namespace Pia.Tests.Services.E2EE;

using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Xunit;

public class DeviceManagementServiceUrlTests
{
    private readonly RecordingHandler _handler = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public static TheoryData<string, string> ServerUrls => new()
    {
        { "https://sync.example.com", "https://sync.example.com/" },
        { "https://corp.example/pia", "https://corp.example/pia/" },
        { "https://corp.example/pia/", "https://corp.example/pia/" },
    };

    [Theory]
    [MemberData(nameof(ServerUrls))]
    public async Task EveryRequestShape_KeepsThePathOfTheServerUrl(string serverUrl, string expectedBase)
    {
        UseServerUrl(serverUrl);
        var sut = CreateSut();

        await sut.CheckE2EEStatusAsync();
        await sut.GetDeviceStatusAsync("dev-1");
        await sut.GetDevicesAsync();
        await sut.RevokeDeviceAsync("dev-2");
        await sut.RegisterPendingDeviceAsync();

        Assert.Equal(
        [
            expectedBase + "api/e2ee/status",
            expectedBase + "api/e2ee/devices/dev-1/status",
            expectedBase + "api/e2ee/devices",
            expectedBase + "api/e2ee/devices/dev-2/revoke",
            expectedBase + "api/e2ee/devices/register",
        ], _handler.Uris);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public async Task WithoutAServerUrl_SaysItIsNotConfigured(string? serverUrl)
    {
        UseServerUrl(serverUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().GetDevicesAsync());
    }

    private void UseServerUrl(string? serverUrl) =>
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = serverUrl });

    private DeviceManagementService CreateSut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token-1");
        var deviceKeys = Substitute.For<IDeviceKeyService>();
        deviceKeys.GetDeviceId().Returns("dev-self");
        deviceKeys.GetAgreementPublicKey().Returns("agree");
        deviceKeys.GetSigningPublicKey().Returns("sign");

        return new DeviceManagementService(
            Substitute.For<IE2EEService>(), deviceKeys, Substitute.For<IRecoveryCodeService>(),
            Substitute.For<ICryptoService>(), _settings, auth, factory,
            NullLogger<DeviceManagementService>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Uris.Add(uri);
            var body = uri.EndsWith("/register", StringComparison.Ordinal)
                ? """{"onboardingSessionId":"s-1","serverChallenge":"c-1"}"""
                : uri.EndsWith("/dev-1/status", StringComparison.Ordinal)
                    ? """{"deviceId":"dev-1"}"""
                    : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
