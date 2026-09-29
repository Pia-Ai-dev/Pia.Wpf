namespace Pia.Tests.Services.E2EE;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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

/// <summary>The server takes recovery activation only from a pending device, so an active device that cannot open
/// its server copy repairs that copy with the recovery code instead.</summary>
public sealed class DeviceManagementServiceRecoveryTests : IDisposable
{
    private const string ThisDevice = "dev-self";
    private const string OtherDevice = "dev-other";

    private readonly InMemoryDeviceKeys _keys = new(ThisDevice);
    private readonly InMemoryDeviceKeys _otherKeys = new(OtherDevice);
    private readonly PassthroughDpapi _dpapi = new(NullLogger<DpapiHelper>.Instance);
    private readonly AppSettings _stored = new() { ServerUrl = "https://sync.example" };
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly RecoveryCodeService _recovery = new(new CryptoService());
    private readonly ScriptedServer _server;
    private readonly E2EEService _e2ee;
    private readonly byte[] _umk = RandomNumberGenerator.GetBytes(32);
    private readonly string _code;

    public DeviceManagementServiceRecoveryTests()
    {
        _settings.GetSettingsAsync().Returns(_stored);
        _e2ee = new E2EEService(new CryptoService(), _keys, _dpapi, _settings, NullLogger<E2EEService>.Instance);
        _code = _recovery.GenerateRecoveryCode();
        _server = new ScriptedServer(_keys, _otherKeys) { Recovery = _recovery.WrapUmkForRecovery(_umk, _code) };
    }

    // Every hand-over a server stored before it checked approval signatures is unsigned.
    [Fact]
    public async Task AnActiveDeviceHoldingAnUnsignedHandOver_WrapsTheKeyForItselfWithTheRecoveryCode()
    {
        _server.Status = DeviceStatus.Active;
        _server.Wrapped = await HandOverFromOtherDeviceAsync();
        var sut = CreateSut();
        Assert.False(await sut.TryRestoreKeyAsync());

        await sut.ActivateViaRecoveryAsync(_code, "session-1");

        Assert.Equal(_umk, _e2ee.LoadUmk());
        Assert.True(_stored.IsE2EEEnabled);
        Assert.Equal(ThisDevice, _server.Wrapped!.CreatedByDeviceId);
        Assert.DoesNotContain(_server.Posts, p => p.EndsWith("/recovery/activate"));

        _stored.E2EEEncryptedUmk = null;
        Assert.True(await sut.TryRestoreKeyAsync());
        Assert.Equal(_umk, _e2ee.LoadUmk());
    }

    [Fact]
    public async Task APendingDevice_StillActivatesThroughTheRecoveryEndpoint()
    {
        _server.Status = DeviceStatus.Pending;

        await CreateSut().ActivateViaRecoveryAsync(_code, "session-1");

        Assert.Equal(_umk, _e2ee.LoadUmk());
        Assert.Equal(DeviceStatus.Active, _server.Status);
        Assert.Contains(_server.Posts, p => p.EndsWith("/recovery/activate"));
        Assert.DoesNotContain(_server.Posts, p => p.EndsWith($"/devices/{ThisDevice}/wrapped-umk"));
    }

    [Fact]
    public async Task AWrongCode_LeavesTheServerCopyAlone()
    {
        _server.Status = DeviceStatus.Active;
        var handOver = await HandOverFromOtherDeviceAsync();
        _server.Wrapped = handOver;

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => CreateSut().ActivateViaRecoveryAsync(_recovery.GenerateRecoveryCode(), "session-1"));

        Assert.Null(_e2ee.LoadUmk());
        Assert.Same(handOver, _server.Wrapped);
        Assert.Empty(_server.Posts);
    }

    public void Dispose()
    {
        _keys.Dispose();
        _otherKeys.Dispose();
    }

    private async Task<WrappedUmkBlob> HandOverFromOtherDeviceAsync()
    {
        var otherSettings = Substitute.For<ISettingsService>();
        otherSettings.GetSettingsAsync().Returns(new AppSettings());
        var other = new E2EEService(
            new CryptoService(), _otherKeys, _dpapi, otherSettings, NullLogger<E2EEService>.Instance);
        await other.StoreUmkAsync(_umk);
        var (ciphertext, salt) = other.WrapUmkForDevice(_keys.GetAgreementPublicKey(), ThisDevice);
        return new WrappedUmkBlob
        {
            DeviceId = ThisDevice,
            Ciphertext = ciphertext,
            HkdfSalt = salt,
            CreatedByDeviceId = OtherDevice,
        };
    }

    private DeviceManagementService CreateSut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_server, disposeHandler: false));
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token-1");

        return new DeviceManagementService(
            _e2ee, _keys, _recovery, _settings, auth, factory,
            NullLogger<DeviceManagementService>.Instance);
    }

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    // Answers the way the server does: its own copy only for an active device, recovery activation only for a
    // pending one.
    private sealed class ScriptedServer(InMemoryDeviceKeys self, InMemoryDeviceKeys other) : HttpMessageHandler
    {
        public DeviceStatus Status { get; set; }
        public WrappedUmkBlob? Wrapped { get; set; }
        public RecoveryWrappedUmkBlob? Recovery { get; set; }
        public List<string> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Posts.Add(path);
                return await PostAsync(path, request.Content!, ct);
            }

            object? body = path switch
            {
                _ when path.EndsWith($"/devices/{ThisDevice}/status") =>
                    new DeviceStatusResponse { DeviceId = ThisDevice, Status = Status },
                _ when path.EndsWith($"/devices/{ThisDevice}/wrapped-umk") =>
                    Status == DeviceStatus.Active ? Wrapped : null,
                _ when path.EndsWith("/recovery/wrapped-umk") => Recovery,
                _ when path.EndsWith("/devices") => new DeviceListResponse
                {
                    Devices = [Device(self, Status), Device(other, DeviceStatus.Active)],
                    HasRecoveryKey = true,
                    UmkVersion = 1,
                },
                _ => null,
            };
            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(body);
        }

        private async Task<HttpResponseMessage> PostAsync(string path, HttpContent content, CancellationToken ct)
        {
            if (path.EndsWith($"/devices/{ThisDevice}/wrapped-umk"))
            {
                if (Status != DeviceStatus.Active) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var blob = (await content.ReadFromJsonAsync<WrappedUmkBlob>(ct))!;
                blob.ApproverSignature = null;
                blob.OnboardingSessionId = null;
                Wrapped = blob;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.EndsWith("/recovery/activate"))
            {
                if (Status != DeviceStatus.Pending) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var activation = (await content.ReadFromJsonAsync<RecoveryActivationRequest>(ct))!;
                Wrapped = new WrappedUmkBlob
                {
                    DeviceId = ThisDevice,
                    Ciphertext = activation.SelfWrappedUmk,
                    HkdfSalt = activation.HkdfSalt,
                    CreatedByDeviceId = ThisDevice,
                };
                Status = DeviceStatus.Active;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static DeviceInfo Device(InMemoryDeviceKeys keys, DeviceStatus status) => new()
        {
            DeviceId = keys.GetDeviceId(),
            DeviceName = keys.GetDeviceId(),
            Status = status,
            AgreementPublicKey = keys.GetAgreementPublicKey(),
            SigningPublicKey = keys.GetSigningPublicKey(),
        };

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), JsonSerializerOptions.Web),
                Encoding.UTF8, "application/json"),
        };
    }
}
