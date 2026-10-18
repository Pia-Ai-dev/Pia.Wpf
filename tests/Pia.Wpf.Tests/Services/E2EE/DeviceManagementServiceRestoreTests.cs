namespace Pia.Tests.Services.E2EE;

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Tests.TestInfrastructure;
using Xunit;

/// <summary>Sign-out forgets the key, and the server still holds this device's wrapped copy, so signing back in
/// takes it back without asking the user to onboard a device the account already trusts.</summary>
public sealed class DeviceManagementServiceRestoreTests : IDisposable
{
    private const string ThisDevice = "dev-self";

    private readonly InMemoryDeviceKeys _keys = new(ThisDevice);
    private readonly PassthroughDpapi _dpapi = new(NullLogger<DpapiHelper>.Instance);
    private readonly AppSettings _stored = new() { ServerUrl = "https://sync.example" };
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ScriptedServer _server = new();
    private readonly E2EEService _e2ee;
    private readonly byte[] _umk = RandomNumberGenerator.GetBytes(32);

    public DeviceManagementServiceRestoreTests()
    {
        _settings.GetSettingsAsync().Returns(_stored);
        _e2ee = new E2EEService(new CryptoService(), _keys, _dpapi, _settings, NullLogger<E2EEService>.Instance);
    }

    [Fact]
    public async Task OnADeviceTheServerStillListsAsActive_TakesBackItsOwnCopy()
    {
        _server.Status = DeviceStatus.Active;
        _server.Wrapped = await SelfWrapAsync();

        Assert.True(await CreateSut().TryRestoreKeyAsync());

        Assert.Equal(_umk, _e2ee.LoadUmk());
        Assert.True(_stored.IsE2EEEnabled);
    }

    [Theory]
    [InlineData(DeviceStatus.Pending)]
    [InlineData(DeviceStatus.Revoked)]
    public async Task OnADeviceTheServerNoLongerTrusts_LeavesOnboardingToTheUser(DeviceStatus status)
    {
        _server.Status = status;
        _server.Wrapped = await SelfWrapAsync();

        Assert.False(await CreateSut().TryRestoreKeyAsync());

        Assert.Null(_e2ee.LoadUmk());
        Assert.False(_stored.IsE2EEEnabled);
        Assert.DoesNotContain(_server.Paths, p => p.EndsWith("/wrapped-umk"));
    }

    public void Dispose() => _keys.Dispose();

    // The copy the server keeps from this device's first activation, made with the same device keys.
    private async Task<WrappedUmkBlob> SelfWrapAsync()
    {
        var earlierSettings = Substitute.For<ISettingsService>();
        earlierSettings.GetSettingsAsync().Returns(new AppSettings());
        var earlier = new E2EEService(
            new CryptoService(), _keys, _dpapi, earlierSettings, NullLogger<E2EEService>.Instance);
        await earlier.StoreUmkAsync(_umk);
        var (ciphertext, salt) = earlier.WrapUmkForSelf();
        return new WrappedUmkBlob
        {
            DeviceId = ThisDevice,
            Ciphertext = ciphertext,
            HkdfSalt = salt,
            CreatedByDeviceId = ThisDevice,
        };
    }

    private DeviceManagementService CreateSut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_server, disposeHandler: false));
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token-1");
        _server.Device = new DeviceInfo
        {
            DeviceId = ThisDevice,
            DeviceName = "this",
            AgreementPublicKey = _keys.GetAgreementPublicKey(),
            SigningPublicKey = _keys.GetSigningPublicKey(),
        };

        return new DeviceManagementService(
            _e2ee, _keys, Substitute.For<IRecoveryCodeService>(), _settings, auth, factory,
            Substitute.For<IConfirmedApproverStore>(), NullLogger<DeviceManagementService>.Instance);
    }

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class ScriptedServer : HttpMessageHandler
    {
        public DeviceStatus Status { get; set; }
        public WrappedUmkBlob? Wrapped { get; set; }
        public DeviceInfo? Device { get; set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Device!.Status = Status;
            object? body = path switch
            {
                _ when path.EndsWith($"/devices/{ThisDevice}/status") =>
                    new DeviceStatusResponse { DeviceId = ThisDevice, Status = Status },
                _ when path.EndsWith($"/devices/{ThisDevice}/wrapped-umk") => Wrapped,
                _ when path.EndsWith("/devices") =>
                    new DeviceListResponse { Devices = [Device!], HasRecoveryKey = true, UmkVersion = 1 },
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(body, body.GetType(), JsonSerializerOptions.Web),
                        Encoding.UTF8, "application/json"),
                });
        }
    }
}
