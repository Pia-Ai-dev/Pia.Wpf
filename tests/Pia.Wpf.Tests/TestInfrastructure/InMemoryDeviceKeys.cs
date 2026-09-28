using System.Security.Cryptography;
using Pia.Services.E2EE;

namespace Pia.Tests.TestInfrastructure;

// Real P-256 keys in the wire format DeviceKeyService uses, held in memory so a test never writes to the CNG
// key store of the account running it.
internal sealed class InMemoryDeviceKeys(string deviceId) : IDeviceKeyService, IDisposable
{
    private readonly ECDiffieHellman _agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string GetDeviceId() => deviceId;

    public string GetAgreementPublicKey() => Export(_agreement.ExportParameters(false));

    public string GetSigningPublicKey() => Export(_signing.ExportParameters(false));

    public byte[] DeriveSharedSecret(string remoteAgreementPublicKeyBase64)
    {
        using var remote = ECDiffieHellman.Create(Import(remoteAgreementPublicKeyBase64));
        return _agreement.DeriveRawSecretAgreement(remote.PublicKey);
    }

    public string Sign(byte[] data) => Convert.ToBase64String(_signing.SignData(data, HashAlgorithmName.SHA256));

    public bool Verify(byte[] data, string signatureBase64, string signingPublicKeyBase64)
    {
        using var ecdsa = ECDsa.Create(Import(signingPublicKeyBase64));
        return ecdsa.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
    }

    public string GetFingerprint() => ComputeFingerprint(GetAgreementPublicKey());

    public string ComputeFingerprint(string agreementPublicKeyBase64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(agreementPublicKeyBase64))[..8]);

    public bool HasDeviceKeys() => true;

    public void Dispose()
    {
        _agreement.Dispose();
        _signing.Dispose();
    }

    private static string Export(ECParameters parameters) =>
        Convert.ToBase64String([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);

    private static ECParameters Import(string publicKeyBase64)
    {
        var point = Convert.FromBase64String(publicKeyBase64);
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        };
    }
}
