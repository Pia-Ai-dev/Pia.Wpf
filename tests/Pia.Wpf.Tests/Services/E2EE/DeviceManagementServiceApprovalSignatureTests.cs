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

/// <summary>A joining device takes a key only from an active device that signed the hand-over. Version 1, which
/// clients up to 1.4.278 sign, covers session and target but not the wrapped key, and still counts.</summary>
public sealed class DeviceManagementServiceApprovalSignatureTests : IDisposable
{
    private const string Session = "session-1";
    private const string Target = "dev-target";
    private const string Approver = "dev-approver";

    private readonly InMemoryDeviceKeys _targetKeys = new(Target);
    private readonly InMemoryDeviceKeys _approverKeys = new(Approver);
    private readonly InMemoryDeviceKeys _strangerKeys = new("dev-stranger");
    private readonly PassthroughDpapi _dpapi = new(NullLogger<DpapiHelper>.Instance);
    private readonly byte[] _umk = RandomNumberGenerator.GetBytes(32);
    private readonly AppSettings _targetSettings = new() { ServerUrl = "https://sync.example" };
    private readonly ScriptedServer _server = new();
    private readonly E2EEService _targetE2ee;

    public DeviceManagementServiceApprovalSignatureTests()
    {
        _targetE2ee = E2EEFor(_targetKeys, _targetSettings);
        _server.Devices[Approver] = Listed(_approverKeys, Approver, DeviceStatus.Active);
        _server.Devices[Target] = Listed(_targetKeys, Target, DeviceStatus.Active);
    }

    [Fact]
    public async Task Approving_SignsTheSessionTheTargetAndTheWrappedKey()
    {
        var approverSettings = new AppSettings { ServerUrl = "https://sync.example" };
        var approverE2ee = E2EEFor(_approverKeys, approverSettings);
        await approverE2ee.StoreUmkAsync(_umk);

        await Sut(approverE2ee, _approverKeys, approverSettings)
            .ApproveDeviceAsync(Session, _server.Devices[Target]);

        var sent = JsonSerializer.Deserialize<DeviceApprovalRequest>(_server.ApproveBody!, JsonSerializerOptions.Web)!;
        Assert.Equal(2, sent.SignatureVersion);
        var payload = Encoding.UTF8.GetBytes(
            $"pia-approval-v2:{Session}:{Target}:{_targetKeys.GetAgreementPublicKey()}:{sent.WrappedUmk}:{sent.HkdfSalt}");
        Assert.True(_approverKeys.Verify(payload, sent.ApproverSignature!, _approverKeys.GetSigningPublicKey()));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task Joining_TakesAKeyTheActiveApproverSigned(int version)
    {
        _server.Wrapped = await HandoverAsync(version);

        await TargetSut().FetchAndUnwrapUmkAsync();

        Assert.Equal(_umk, _targetE2ee.LoadUmk());
        Assert.True(_targetSettings.IsE2EEEnabled);
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("signed-over-another-wrap")]
    [InlineData("signed-by-a-stranger")]
    [InlineData("unknown-version")]
    [InlineData("approver-pending")]
    [InlineData("approver-revoked")]
    public async Task Joining_RefusesAKeyWithoutAValidSignature(string defect)
    {
        var handover = await HandoverAsync(2, signer: defect == "signed-by-a-stranger" ? _strangerKeys : null);
        switch (defect)
        {
            case "unsigned":
                handover.ApproverSignature = null;
                break;
            case "signed-over-another-wrap":
                handover.ApproverSignature = Sign(_approverKeys, 2, (await HandoverAsync(2)).Ciphertext, handover.HkdfSalt);
                break;
            case "unknown-version":
                handover.SignatureVersion = 3;
                break;
            case "approver-pending":
                _server.Devices[Approver].Status = DeviceStatus.Pending;
                break;
            case "approver-revoked":
                _server.Devices[Approver].Status = DeviceStatus.Revoked;
                break;
        }
        _server.Wrapped = handover;

        await Assert.ThrowsAsync<UnverifiedApprovalException>(() => TargetSut().FetchAndUnwrapUmkAsync());

        Assert.Null(_targetE2ee.LoadUmk());
        Assert.False(_targetSettings.IsE2EEEnabled);
    }

    // A copy this device made for itself needs no signature, so it has to open with this device's own key alone.
    [Fact]
    public async Task JoiningFromItsOwnCopy_OpensItWithItsOwnKey_NotTheOneTheServerLists()
    {
        using var impostor = new InMemoryDeviceKeys(Target);
        var impostorSettings = new AppSettings();
        var impostorE2ee = E2EEFor(impostor, impostorSettings);
        await impostorE2ee.StoreUmkAsync(RandomNumberGenerator.GetBytes(32));
        var (ciphertext, salt) = impostorE2ee.WrapUmkForDevice(_targetKeys.GetAgreementPublicKey(), Target);
        _server.Devices[Target] = Listed(impostor, Target, DeviceStatus.Active);
        _server.Wrapped = new WrappedUmkBlob
        {
            DeviceId = Target, Ciphertext = ciphertext, HkdfSalt = salt, CreatedByDeviceId = Target,
        };

        await Assert.ThrowsAnyAsync<CryptographicException>(() => TargetSut().FetchAndUnwrapUmkAsync());

        Assert.Null(_targetE2ee.LoadUmk());
    }

    public void Dispose()
    {
        _targetKeys.Dispose();
        _approverKeys.Dispose();
        _strangerKeys.Dispose();
    }

    private async Task<WrappedUmkBlob> HandoverAsync(int version, InMemoryDeviceKeys? signer = null)
    {
        var approverE2ee = E2EEFor(_approverKeys, new AppSettings());
        await approverE2ee.StoreUmkAsync(_umk);
        var (ciphertext, salt) = approverE2ee.WrapUmkForDevice(_targetKeys.GetAgreementPublicKey(), Target);
        return new WrappedUmkBlob
        {
            DeviceId = Target,
            Ciphertext = ciphertext,
            HkdfSalt = salt,
            CreatedByDeviceId = Approver,
            OnboardingSessionId = Session,
            SignatureVersion = version,
            ApproverSignature = Sign(signer ?? _approverKeys, version, ciphertext, salt),
        };
    }

    private string Sign(InMemoryDeviceKeys signer, int version, string ciphertext, string salt)
    {
        var target = _targetKeys.GetAgreementPublicKey();
        var payload = version == 1
            ? $"{Session}:{Target}:{target}"
            : $"pia-approval-v2:{Session}:{Target}:{target}:{ciphertext}:{salt}";
        return signer.Sign(Encoding.UTF8.GetBytes(payload));
    }

    private static DeviceInfo Listed(InMemoryDeviceKeys keys, string deviceId, DeviceStatus status) => new()
    {
        DeviceId = deviceId,
        DeviceName = deviceId,
        Status = status,
        AgreementPublicKey = keys.GetAgreementPublicKey(),
        SigningPublicKey = keys.GetSigningPublicKey(),
    };

    private E2EEService E2EEFor(IDeviceKeyService keys, AppSettings stored)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);
        return new E2EEService(new CryptoService(), keys, _dpapi, settings, NullLogger<E2EEService>.Instance);
    }

    private DeviceManagementService TargetSut() => Sut(_targetE2ee, _targetKeys, _targetSettings);

    private DeviceManagementService Sut(IE2EEService e2ee, IDeviceKeyService keys, AppSettings stored)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_server, disposeHandler: false));
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token-1");

        return new DeviceManagementService(
            e2ee, keys, Substitute.For<IRecoveryCodeService>(), new CryptoService(), settings, auth, factory,
            NullLogger<DeviceManagementService>.Instance);
    }

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class ScriptedServer : HttpMessageHandler
    {
        public Dictionary<string, DeviceInfo> Devices { get; } = [];
        public WrappedUmkBlob? Wrapped { get; set; }
        public string? ApproveBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/devices/approve"))
            {
                ApproveBody = await request.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            object? body = null;
            if (path.EndsWith("/wrapped-umk"))
                body = Wrapped;
            else if (path.EndsWith("/status"))
            {
                var id = path.Split('/')[^2];
                body = new DeviceStatusResponse { DeviceId = id, Status = Devices[id].Status };
            }
            else if (path.EndsWith("/devices"))
                body = new DeviceListResponse { Devices = [.. Devices.Values], HasRecoveryKey = true, UmkVersion = 1 };

            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(body, body.GetType(), JsonSerializerOptions.Web),
                        Encoding.UTF8, "application/json"),
                };
        }
    }
}
