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

public sealed class DeviceManagementServiceProofKeyTests : IDisposable
{
    private const string ThisDevice = "dev-self";
    private const string ProofKeyPath = "/api/e2ee/recovery/proof-key";
    private const string RecoveryCopyPath = "/api/e2ee/recovery/wrapped-umk";

    private readonly InMemoryDeviceKeys _keys = new(ThisDevice);
    private readonly PassthroughDpapi _dpapi = new(NullLogger<DpapiHelper>.Instance);
    private readonly AppSettings _stored = new() { ServerUrl = "https://sync.example" };
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly CapturingLogger<DeviceManagementService> _log = new();
    private readonly ProofKeyServer _server = new();
    private readonly E2EEService _e2ee;
    private readonly byte[] _umk = RandomNumberGenerator.GetBytes(32);

    public DeviceManagementServiceProofKeyTests()
    {
        _settings.GetSettingsAsync().Returns(_stored);
        _auth.GetAccessTokenAsync().Returns("token-1");
        _auth.UserEmail.Returns("a@example.com");
        _e2ee = new E2EEService(new CryptoService(), _keys, _dpapi, _settings, NullLogger<E2EEService>.Instance);
    }

    [Fact]
    public async Task Bootstrap_SendsTheProofKeyOfTheNewKey_AfterTheRecoveryCopy()
    {
        await CreateSut(StubRecovery()).BootstrapFirstDeviceAsync();

        var recoveryAt = _server.Requests.IndexOf($"POST {RecoveryCopyPath}");
        var proofKeyAt = _server.Requests.IndexOf($"PUT {ProofKeyPath}");
        Assert.True(recoveryAt >= 0 && proofKeyAt > recoveryAt);
        Assert.Equal(Convert.ToBase64String(RecoveryActivationProof.DeriveProofKey(_e2ee.LoadUmk()!)), _server.StoredProofKey);
    }

    [Fact]
    public async Task Bootstrap_OnAnAccountTheServerReportsEnabled_StopsBeforeRegistering()
    {
        _server.IsEnabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut(StubRecovery()).BootstrapFirstDeviceAsync());

        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("POST") || r.StartsWith("PUT"));
        Assert.Null(_e2ee.LoadUmk());
    }

    [Fact]
    public async Task Bootstrap_WhenTheRecoveryCopyIsLocked_FailsBeforeACodeIsShown()
    {
        _server.RecoveryCopyLocked = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut(StubRecovery()).BootstrapFirstDeviceAsync());

        Assert.DoesNotContain($"PUT {ProofKeyPath}", _server.Requests);
        Assert.False(_stored.IsE2EEEnabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Bootstrap_WhenTheProofKeyIsRefused_StillFinishes(HttpStatusCode status)
    {
        _server.ProofKeyStatus = status;

        var code = await CreateSut(StubRecovery()).BootstrapFirstDeviceAsync();

        Assert.Equal("CODE-1", code);
        Assert.True(_stored.IsE2EEEnabled);
    }

    [Fact]
    public async Task Ensure_OnAnAccountWithoutAProofKey_UploadsItOnce()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        var sut = CreateSut();

        await sut.EnsureRecoveryProofKeyAsync();
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Single(_server.Requests, $"PUT {ProofKeyPath}");
        Assert.Equal(Convert.ToBase64String(RecoveryActivationProof.DeriveProofKey(_umk)), _server.StoredProofKey);
        Assert.Equal(_umk, _e2ee.LoadUmk());
    }

    [Fact]
    public async Task Ensure_WhenTheServerHasOne_SendsNothing()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.StoredProofKey = "already";

        await CreateSut().EnsureRecoveryProofKeyAsync();

        Assert.DoesNotContain($"PUT {ProofKeyPath}", _server.Requests);
    }

    [Theory]
    [InlineData(DeviceStatus.Pending)]
    [InlineData(DeviceStatus.Revoked)]
    public async Task Ensure_OnADeviceThatIsNotActive_SendsNothing(DeviceStatus status)
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.DeviceStatus = status;

        await CreateSut().EnsureRecoveryProofKeyAsync();

        Assert.DoesNotContain($"PUT {ProofKeyPath}", _server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Ensure_OnAServerThatPredatesTheEndpoint_StopsAskingForThisRun(HttpStatusCode status)
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.ProofKeyStatus = status;
        var sut = CreateSut();

        await sut.EnsureRecoveryProofKeyAsync();
        var afterFirst = _server.Requests.Count;
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Equal(afterFirst, _server.Requests.Count);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("does not accept"));
    }

    [Fact]
    public async Task Ensure_OnAConflict_WarnsWithoutTheValue_AndStops()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.ProofKeyConflict = true;
        var sut = CreateSut();

        await sut.EnsureRecoveryProofKeyAsync();
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Single(_server.Requests, $"PUT {ProofKeyPath}");
        var proofKey = Convert.ToBase64String(RecoveryActivationProof.DeriveProofKey(_umk));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("different recovery proof key"));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains(proofKey));
    }

    [Fact]
    public async Task Ensure_WhenTheServerIsNotReadyYet_TriesAgain()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.ProofKeyNotSetUp = true;
        var sut = CreateSut();

        await sut.EnsureRecoveryProofKeyAsync();
        _server.ProofKeyNotSetUp = false;
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Equal(2, _server.Requests.Count(r => r == $"PUT {ProofKeyPath}"));
        Assert.NotNull(_server.StoredProofKey);
    }

    [Fact]
    public async Task Ensure_WhenTheServerCallsTheKeyMalformed_StopsAskingForThisRun()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.ProofKeyStatus = HttpStatusCode.BadRequest;
        var sut = CreateSut();

        await sut.EnsureRecoveryProofKeyAsync();
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Single(_server.Requests, $"PUT {ProofKeyPath}");
    }

    [Fact]
    public async Task Ensure_AfterAServerError_TriesAgain()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.ProofKeyStatus = HttpStatusCode.InternalServerError;
        var sut = CreateSut();

        await sut.EnsureRecoveryProofKeyAsync();
        _server.ProofKeyStatus = null;
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Equal(2, _server.Requests.Count(r => r == $"PUT {ProofKeyPath}"));
        Assert.NotNull(_server.StoredProofKey);
    }

    [Fact]
    public async Task Ensure_AfterSigningInToAnotherAccount_ChecksAgain()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _server.ProofKeyStatus = HttpStatusCode.NotFound;
        var sut = CreateSut();
        await sut.EnsureRecoveryProofKeyAsync();

        _auth.UserEmail.Returns("b@example.com");
        await sut.EnsureRecoveryProofKeyAsync();

        Assert.Equal(2, _server.Requests.Count(r => r == $"PUT {ProofKeyPath}"));
    }

    [Fact]
    public async Task Ensure_WithoutAServer_DoesNotThrow()
    {
        await ActiveDeviceOnEnabledAccountAsync();
        _stored.ServerUrl = null;

        await CreateSut().EnsureRecoveryProofKeyAsync();

        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task RecoveryActivation_SendsAProofTheServerAcceptsUnderTheStoredProofKey()
    {
        var recovery = new RecoveryCodeService(new CryptoService());
        var code = recovery.GenerateRecoveryCode();
        _server.RecoveryCopy = recovery.WrapUmkForRecovery(_umk, code);
        _server.StoredProofKey = Convert.ToBase64String(RecoveryActivationProof.DeriveProofKey(_umk));
        _server.IsEnabled = true;
        _server.DeviceStatus = DeviceStatus.Pending;

        await CreateSut(recovery).ActivateViaRecoveryAsync(code, "c2Vzc2lvbi0x");

        Assert.Equal(DeviceStatus.Active, _server.DeviceStatus);
    }

    [Fact]
    public async Task RecoveryActivation_OnASpentSession_SaysSoInsteadOfFailingPlainly()
    {
        var recovery = new RecoveryCodeService(new CryptoService());
        var code = recovery.GenerateRecoveryCode();
        _server.RecoveryCopy = recovery.WrapUmkForRecovery(_umk, code);
        _server.IsEnabled = true;
        _server.DeviceStatus = DeviceStatus.Pending;
        _server.SessionSpent = true;

        await Assert.ThrowsAsync<OnboardingSessionExpiredException>(
            () => CreateSut(recovery).ActivateViaRecoveryAsync(code, "c2Vzc2lvbi0x"));
    }

    [Fact]
    public async Task RecoveryActivation_WithAProofTheServerRejects_IsNotTakenForASpentSession()
    {
        var recovery = new RecoveryCodeService(new CryptoService());
        var code = recovery.GenerateRecoveryCode();
        _server.RecoveryCopy = recovery.WrapUmkForRecovery(_umk, code);
        _server.StoredProofKey = Convert.ToBase64String(RecoveryActivationProof.DeriveProofKey(new byte[32]));
        _server.IsEnabled = true;
        _server.DeviceStatus = DeviceStatus.Pending;

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CreateSut(recovery).ActivateViaRecoveryAsync(code, "c2Vzc2lvbi0x"));

        Assert.IsType<HttpRequestException>(ex);
        Assert.Equal(DeviceStatus.Pending, _server.DeviceStatus);
    }

    public void Dispose() => _keys.Dispose();

    private async Task ActiveDeviceOnEnabledAccountAsync()
    {
        await _e2ee.StoreUmkAsync(_umk);
        _stored.IsE2EEEnabled = true;
        _server.IsEnabled = true;
        _server.DeviceStatus = DeviceStatus.Active;
    }

    private static IRecoveryCodeService StubRecovery()
    {
        var recovery = Substitute.For<IRecoveryCodeService>();
        recovery.GenerateRecoveryCode().Returns("CODE-1");
        recovery.WrapUmkForRecovery(Arg.Any<byte[]>(), Arg.Any<string>()).Returns(new RecoveryWrappedUmkBlob
        {
            Ciphertext = "recovery-ciphertext",
            KdfSalt = Convert.ToBase64String(new byte[32]),
        });
        return recovery;
    }

    private DeviceManagementService CreateSut(IRecoveryCodeService? recovery = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_server, disposeHandler: false));

        return new DeviceManagementService(
            _e2ee, _keys, recovery ?? Substitute.For<IRecoveryCodeService>(), _settings, _auth, factory, _log);
    }

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    // Answers like a server that stores the proof key write-only and checks recovery activation against it.
    private sealed class ProofKeyServer : HttpMessageHandler
    {
        public bool IsEnabled { get; set; }
        public DeviceStatus? DeviceStatus { get; set; }
        public string? StoredProofKey { get; set; }
        public RecoveryWrappedUmkBlob? RecoveryCopy { get; set; }
        public bool RecoveryCopyLocked { get; set; }
        public bool ProofKeyConflict { get; set; }
        public bool ProofKeyNotSetUp { get; set; }
        public bool SessionSpent { get; set; }
        public HttpStatusCode? ProofKeyStatus { get; set; }
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
                Requests.Add($"{request.Method} {path}");

            if (request.Method == HttpMethod.Get)
            {
                object? body = path switch
                {
                    "/api/e2ee/status" => new E2EEStatusResponse
                    {
                        IsEnabled = IsEnabled,
                        UmkVersion = 1,
                        HasRecoveryKey = IsEnabled,
                        HasRecoveryProofKey = StoredProofKey is not null,
                    },
                    $"/api/e2ee/devices/{ThisDevice}/status" when DeviceStatus is { } status =>
                        new DeviceStatusResponse { DeviceId = ThisDevice, Status = status },
                    RecoveryCopyPath => RecoveryCopy,
                    _ => null,
                };
                return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(body);
            }

            if (request.Method == HttpMethod.Put && path == ProofKeyPath)
            {
                if (ProofKeyStatus is { } forced)
                    return forced == HttpStatusCode.BadRequest
                        ? Error(forced, "invalid_proof_key")
                        : new HttpResponseMessage(forced);
                if (ProofKeyConflict)
                    return Error(HttpStatusCode.Conflict, E2EEErrorCodes.ProofKeyConflict);
                if (ProofKeyNotSetUp)
                    return Error(HttpStatusCode.Conflict, "recovery_not_set_up");
                StoredProofKey = (await request.Content!.ReadFromJsonAsync<RecoveryProofKeyRequest>(ct))!.ProofKey;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return path switch
            {
                "/api/e2ee/devices/register" => Json(new DeviceRegistrationResponse
                {
                    IsFirstDevice = true,
                    OnboardingSessionId = "session-1",
                    ServerChallenge = "unused",
                }),
                RecoveryCopyPath when RecoveryCopyLocked => Error(HttpStatusCode.Conflict, E2EEErrorCodes.RecoveryKeyLocked),
                "/api/e2ee/recovery/activate" => await ActivateAsync(request.Content!, ct),
                _ => new HttpResponseMessage(HttpStatusCode.OK),
            };
        }

        private async Task<HttpResponseMessage> ActivateAsync(HttpContent content, CancellationToken ct)
        {
            var activation = (await content.ReadFromJsonAsync<RecoveryActivationRequest>(ct))!;
            if (SessionSpent)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(""""Invalid or expired onboarding session"""", Encoding.UTF8, "application/json"),
                };
            if (StoredProofKey is not null && !RecoveryActivationProof.Verify(
                    Convert.FromBase64String(StoredProofKey), activation.OnboardingSessionId, activation.ProofOfPossession))
                return Error(HttpStatusCode.BadRequest, E2EEErrorCodes.InvalidProof);
            DeviceStatus = Shared.E2EE.DeviceStatus.Active;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
        {
            Content = new StringContent($$"""{"error":"{{code}}"}""", Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), JsonSerializerOptions.Web),
                Encoding.UTF8, "application/json"),
        };
    }
}
