namespace Pia.Tests.E2EE;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Xunit;

public class DeviceKeyServiceTests : IDisposable
{
    private readonly ISettingsService _settingsMock;
    private readonly DeviceKeyService _sut;
    private readonly AppSettings _settings;

    public DeviceKeyServiceTests()
    {
        _settings = new AppSettings { SyncDeviceId = $"test-{Guid.NewGuid()}" };
        _settingsMock = Substitute.For<ISettingsService>();
        _settingsMock.GetSettingsAsync().Returns(_settings);
        _sut = new DeviceKeyService(_settingsMock, NullLogger<DeviceKeyService>.Instance);
    }

    public void Dispose()
    {
        // Clean up CNG keys created during test
        var deviceId = _settings.SyncDeviceId;
        TryDeleteKey($"Pia-Device-ECDH-{deviceId}");
        TryDeleteKey($"Pia-Device-ECDSA-{deviceId}");
    }

    private static void TryDeleteKey(string name)
    {
        try { CngKey.Open(name).Delete(); } catch { }
    }

    [Fact]
    public void GetAgreementPublicKey_ShouldReturn65ByteUncompressedPoint()
    {
        var publicKey = _sut.GetAgreementPublicKey();
        var bytes = Convert.FromBase64String(publicKey);
        Assert.Equal(65, bytes.Length);
        Assert.Equal(0x04, bytes[0]);
    }

    [Fact]
    public void GetAgreementPublicKey_CalledTwice_ShouldReturnSameKey()
    {
        var key1 = _sut.GetAgreementPublicKey();
        var key2 = _sut.GetAgreementPublicKey();
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveSharedSecret_WithRemoteKey_ShouldSucceed()
    {
        // Create a second ephemeral key pair to simulate remote device
        using var remote = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var remoteParams = remote.ExportParameters(false);
        var remotePoint = new byte[65];
        remotePoint[0] = 0x04;
        remoteParams.Q.X!.CopyTo(remotePoint, 1);
        remoteParams.Q.Y!.CopyTo(remotePoint, 33);
        var remoteBase64 = Convert.ToBase64String(remotePoint);

        var secret = _sut.DeriveSharedSecret(remoteBase64);
        Assert.Equal(32, secret.Length);
    }

    [Fact]
    public void Sign_Verify_RoundTrip_ShouldSucceed()
    {
        var data = "test data"u8.ToArray();
        var signature = _sut.Sign(data);
        var publicKey = _sut.GetSigningPublicKey();
        Assert.True(_sut.Verify(data, signature, publicKey));
    }

    [Fact]
    public void GetFingerprint_ShouldReturnExpectedFormat()
    {
        var fingerprint = _sut.GetFingerprint();
        Assert.Matches(@"^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", fingerprint);
    }
}
