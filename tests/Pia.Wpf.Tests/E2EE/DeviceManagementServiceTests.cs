namespace Pia.Tests.E2EE;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Xunit;

public class DeviceManagementServiceTests
{
    private readonly IE2EEService _e2eeMock;
    private readonly IDeviceKeyService _deviceKeysMock;
    private readonly IRecoveryCodeService _recoveryMock;
    private readonly ICryptoService _cryptoMock;
    private readonly ISettingsService _settingsMock;
    private readonly IAuthService _authMock;
    private readonly AppSettings _settings;

    public DeviceManagementServiceTests()
    {
        _e2eeMock = Substitute.For<IE2EEService>();
        _deviceKeysMock = Substitute.For<IDeviceKeyService>();
        _recoveryMock = Substitute.For<IRecoveryCodeService>();
        _cryptoMock = Substitute.For<ICryptoService>();
        _settingsMock = Substitute.For<ISettingsService>();
        _authMock = Substitute.For<IAuthService>();

        _settings = new AppSettings { ServerUrl = "https://test.example.com" };
        _settingsMock.GetSettingsAsync().Returns(_settings);
        _authMock.GetAccessTokenAsync().Returns("test-token");

        _deviceKeysMock.GetDeviceId().Returns("device-001");
        _deviceKeysMock.GetAgreementPublicKey().Returns(Convert.ToBase64String(new byte[65]));
        _deviceKeysMock.GetSigningPublicKey().Returns(Convert.ToBase64String(new byte[65]));
        _deviceKeysMock.HasDeviceKeys().Returns(true);
    }

    [Fact]
    public void IsInitialized_WithE2EEReadyAndDeviceKeys_ShouldReturnTrue()
    {
        _e2eeMock.IsReady().Returns(true);
        _deviceKeysMock.HasDeviceKeys().Returns(true);

        var sut = CreateService();

        Assert.True(sut.IsInitialized());
    }

    [Fact]
    public void IsInitialized_WithoutUmk_ShouldReturnFalse()
    {
        _e2eeMock.IsReady().Returns(false);
        _deviceKeysMock.HasDeviceKeys().Returns(true);

        var sut = CreateService();

        Assert.False(sut.IsInitialized());
    }

    [Fact]
    public void IsInitialized_WithoutDeviceKeys_ShouldReturnFalse()
    {
        _e2eeMock.IsReady().Returns(true);
        _deviceKeysMock.HasDeviceKeys().Returns(false);

        var sut = CreateService();

        Assert.False(sut.IsInitialized());
    }

    [Fact]
    public async Task BootstrapFirstDevice_ShouldGenerateUmkAndReturnRecoveryCode()
    {
        var fakeUmk = new byte[32];
        _e2eeMock.GenerateAndStoreUmkAsync().Returns(fakeUmk);
        _e2eeMock.WrapUmkForSelf().Returns(("wrapped-ciphertext", "hkdf-salt"));
        _recoveryMock.GenerateRecoveryCode().Returns("ABCD-EFGH-IJKL-MNOP");
        _recoveryMock.WrapUmkForRecovery(Arg.Any<byte[]>(), Arg.Any<string>())
            .Returns(new RecoveryWrappedUmkBlob
            {
                Ciphertext = "recovery-ciphertext",
                KdfSalt = Convert.ToBase64String(new byte[32]),
                KdfMemoryCostKb = 65536,
                KdfTimeCost = 3,
                KdfParallelism = 4,
                WrapVersion = 1,
                UmkVersion = 1,
                CreatedAt = DateTime.UtcNow
            });

        var registrationResponse = new DeviceRegistrationResponse
        {
            IsFirstDevice = true,
            OnboardingSessionId = "session-123",
            ServerChallenge = Convert.ToBase64String(new byte[32])
        };

        var handler = new MockHttpMessageHandler();
        // Register device -> returns IsFirstDevice=true
        handler.AddResponse("/api/e2ee/devices/register", HttpStatusCode.OK,
            JsonSerializer.Serialize(registrationResponse));
        // Upload wrapped UMK
        handler.AddResponse("/api/e2ee/devices/device-001/wrapped-umk", HttpStatusCode.OK, "{}");
        // Upload recovery wrapped UMK
        handler.AddResponse("/api/e2ee/recovery/wrapped-umk", HttpStatusCode.OK, "{}");

        var sut = CreateService(handler);

        var recoveryCode = await sut.BootstrapFirstDeviceAsync();

        Assert.Equal("ABCD-EFGH-IJKL-MNOP", recoveryCode);
        await _e2eeMock.Received(1).GenerateAndStoreUmkAsync();
        _e2eeMock.Received(1).WrapUmkForSelf();
        _recoveryMock.Received(1).GenerateRecoveryCode();
        _recoveryMock.Received(1).WrapUmkForRecovery(Arg.Any<byte[]>(), "ABCD-EFGH-IJKL-MNOP");

        // Verify settings were updated
        await _settingsMock.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s =>
            s.IsE2EEEnabled && s.E2EEUmkVersion == 1 && s.E2EERecoveryConfigured));
    }

    [Fact]
    public async Task BootstrapFirstDevice_NotFirstDevice_ShouldThrow()
    {
        var registrationResponse = new DeviceRegistrationResponse
        {
            IsFirstDevice = false,
            OnboardingSessionId = "session-123",
            ServerChallenge = Convert.ToBase64String(new byte[32])
        };

        var handler = new MockHttpMessageHandler();
        handler.AddResponse("/api/e2ee/devices/register", HttpStatusCode.OK,
            JsonSerializer.Serialize(registrationResponse));

        var sut = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.BootstrapFirstDeviceAsync());
    }

    private DeviceManagementService CreateService(MockHttpMessageHandler? handler = null)
    {
        handler ??= new MockHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        // Return a new HttpClient each time since DeviceManagementService disposes clients after use
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new DeviceManagementService(
            _e2eeMock, _deviceKeysMock, _recoveryMock, _cryptoMock,
            _settingsMock, _authMock, factory,
            NullLogger<DeviceManagementService>.Instance);
    }

    /// <summary>
    /// Simple HTTP message handler mock that returns predefined responses based on URL path.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Content)> _responses = new();

        public void AddResponse(string pathPrefix, HttpStatusCode status, string content)
        {
            _responses[pathPrefix] = (status, content);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            foreach (var (pathPrefix, response) in _responses)
            {
                if (path.Contains(pathPrefix))
                {
                    return Task.FromResult(new HttpResponseMessage(response.Status)
                    {
                        Content = new StringContent(response.Content, System.Text.Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
