using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.E2EE;

public class DeviceKeyService : IDeviceKeyService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<DeviceKeyService> _logger;
    private ECDiffieHellman? _ecdh;
    private ECDsa? _ecdsa;
    private string? _cachedDeviceId;

    public DeviceKeyService(ISettingsService settings, ILogger<DeviceKeyService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string GetDeviceId()
    {
        if (_cachedDeviceId is not null)
            return _cachedDeviceId;

        var settings = _settings.GetSettingsAsync().GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(settings.SyncDeviceId))
        {
            settings.SyncDeviceId = Guid.NewGuid().ToString();
            _settings.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        _cachedDeviceId = settings.SyncDeviceId;
        return _cachedDeviceId;
    }

    public string GetAgreementPublicKey()
    {
        EnsureEcdhKey();
        var parameters = _ecdh!.ExportParameters(includePrivateParameters: false);
        return ExportUncompressedPoint(parameters);
    }

    public string GetSigningPublicKey()
    {
        EnsureEcdsaKey();
        var parameters = _ecdsa!.ExportParameters(includePrivateParameters: false);
        return ExportUncompressedPoint(parameters);
    }

    public byte[] DeriveSharedSecret(string remoteAgreementPublicKeyBase64)
    {
        EnsureEcdhKey();
        var remotePoint = Convert.FromBase64String(remoteAgreementPublicKeyBase64);
        var remoteParams = ImportPublicPoint(remotePoint);

        using var remoteEcdh = ECDiffieHellman.Create(remoteParams);
        return _ecdh!.DeriveRawSecretAgreement(remoteEcdh.PublicKey);
    }

    public string Sign(byte[] data)
    {
        EnsureEcdsaKey();
        var signature = _ecdsa!.SignData(data, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    public bool Verify(byte[] data, string signatureBase64, string signingPublicKeyBase64)
    {
        var publicPoint = Convert.FromBase64String(signingPublicKeyBase64);
        var parameters = ImportPublicPoint(publicPoint);
        using var ecdsa = ECDsa.Create(parameters);
        var signature = Convert.FromBase64String(signatureBase64);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public string GetFingerprint() => ComputeFingerprint(GetAgreementPublicKey());

    public string ComputeFingerprint(string agreementPublicKeyBase64)
    {
        var keyBytes = Convert.FromBase64String(agreementPublicKeyBase64);
        var hash = SHA256.HashData(keyBytes);
        // Take first 8 bytes (64 bits), format as XXXX-XXXX-XXXX-XXXX
        var hex = Convert.ToHexString(hash[..8]);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    public bool HasDeviceKeys()
    {
        var deviceId = GetDeviceId();
        try
        {
            using var key = CngKey.Open($"Pia-Device-ECDH-{deviceId}");
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private void EnsureEcdhKey()
    {
        if (_ecdh is not null) return;

        var deviceId = GetDeviceId();
        var keyName = $"Pia-Device-ECDH-{deviceId}";

        try
        {
            var existingKey = CngKey.Open(keyName);
            _ecdh = new ECDiffieHellmanCng(existingKey);
            _logger.LogDebug("Loaded existing ECDH key: {KeyName}", keyName);
        }
        catch (CryptographicException)
        {
            var creationParams = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.None,
            };

            var cngKey = CngKey.Create(
                CngAlgorithm.ECDiffieHellmanP256,
                keyName,
                creationParams);
            _ecdh = new ECDiffieHellmanCng(cngKey);
            _logger.LogInformation("Created new ECDH key: {KeyName}", keyName);
        }
    }

    private void EnsureEcdsaKey()
    {
        if (_ecdsa is not null) return;

        var deviceId = GetDeviceId();
        var keyName = $"Pia-Device-ECDSA-{deviceId}";

        try
        {
            var existingKey = CngKey.Open(keyName);
            _ecdsa = new ECDsaCng(existingKey);
        }
        catch (CryptographicException)
        {
            var creationParams = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.None,
            };

            var cngKey = CngKey.Create(
                CngAlgorithm.ECDsaP256,
                keyName,
                creationParams);
            _ecdsa = new ECDsaCng(cngKey);
            _logger.LogInformation("Created new ECDSA key: {KeyName}", keyName);
        }
    }

    private static string ExportUncompressedPoint(ECParameters parameters)
    {
        // Uncompressed point: 0x04 || X || Y
        var point = new byte[1 + parameters.Q.X!.Length + parameters.Q.Y!.Length];
        point[0] = 0x04;
        parameters.Q.X.CopyTo(point, 1);
        parameters.Q.Y.CopyTo(point, 1 + parameters.Q.X.Length);
        return Convert.ToBase64String(point);
    }

    private static ECParameters ImportPublicPoint(byte[] uncompressedPoint)
    {
        // Uncompressed point: 0x04 || X[32] || Y[32]
        if (uncompressedPoint.Length != 65 || uncompressedPoint[0] != 0x04)
            throw new CryptographicException("Invalid uncompressed EC point");

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = uncompressedPoint[1..33],
                Y = uncompressedPoint[33..65]
            }
        };
    }
}
