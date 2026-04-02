using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Services.Interfaces;

namespace Pia.Services.E2EE;

public class E2EEService : IE2EEService
{
    private readonly ICryptoService _crypto;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly DpapiHelper _dpapi;
    private readonly ISettingsService _settings;
    private readonly ILogger<E2EEService> _logger;

    private byte[]? _cachedUmk;

    public E2EEService(
        ICryptoService crypto,
        IDeviceKeyService deviceKeys,
        DpapiHelper dpapi,
        ISettingsService settings,
        ILogger<E2EEService> logger)
    {
        _crypto = crypto;
        _deviceKeys = deviceKeys;
        _dpapi = dpapi;
        _settings = settings;
        _logger = logger;
    }

    public async Task<byte[]> GenerateAndStoreUmkAsync()
    {
        var umk = _crypto.GenerateRandomBytes(32);
        await StoreUmkAsync(umk);
        _logger.LogInformation("Generated and stored new UMK");
        return umk;
    }

    public byte[]? LoadUmk()
    {
        if (_cachedUmk is not null) return _cachedUmk;

        var settings = _settings.GetSettingsAsync().GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(settings.E2EEEncryptedUmk))
            return null;

        var decrypted = _dpapi.Decrypt(settings.E2EEEncryptedUmk);
        if (string.IsNullOrEmpty(decrypted))
            return null;

        _cachedUmk = Convert.FromBase64String(decrypted);
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(_cachedUmk)[..4]);
        _logger.LogInformation("UMK loaded from DPAPI (fingerprint: {Fingerprint})", fingerprint);
        return _cachedUmk;
    }

    public async Task StoreUmkAsync(byte[] umk)
    {
        var base64 = Convert.ToBase64String(umk);
        var encrypted = _dpapi.Encrypt(base64);
        var settings = await _settings.GetSettingsAsync();
        settings.E2EEEncryptedUmk = encrypted;
        await _settings.SaveSettingsAsync(settings);
        _cachedUmk = umk.ToArray();
    }

    public bool HasUmk() => LoadUmk() is not null;

    public (string EncryptedPayload, string WrappedDek) EncryptRecord(
        object record, string userId, string entityType, string entityId)
    {
        var umk = LoadUmk() ?? throw new InvalidOperationException("UMK not available");

        // Serialize record to JSON bytes
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record);

        // Compress before encryption — encrypted data is random and incompressible
        using var compressedStream = new MemoryStream();
        compressedStream.WriteByte(0x01); // Format marker: compressed
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlibStream.Write(plaintext);
        }
        var compressedPayload = compressedStream.ToArray();

        // Generate random DEK
        var dek = _crypto.GenerateRandomBytes(32);

        // Encrypt compressed payload with DEK
        var recordAad = Encoding.UTF8.GetBytes($"pia-e2ee-v1:{userId}:{entityType}:{entityId}");
        var encryptedPayload = _crypto.Encrypt(dek, compressedPayload, recordAad);

        // Wrap DEK with UMK
        var dekAad = Encoding.UTF8.GetBytes($"pia-dek-wrap-v1:{entityType}:{entityId}");
        var wrappedDek = _crypto.Encrypt(umk, dek, dekAad);

        // Clear DEK from memory (best effort)
        Array.Clear(dek);

        return (encryptedPayload, wrappedDek);
    }

    public T DecryptRecord<T>(
        string encryptedPayload, string wrappedDek,
        string userId, string entityType, string entityId)
    {
        var umk = LoadUmk() ?? throw new InvalidOperationException("UMK not available");

        // Unwrap DEK
        var dekAad = Encoding.UTF8.GetBytes($"pia-dek-wrap-v1:{entityType}:{entityId}");
        var dek = _crypto.Decrypt(umk, wrappedDek, dekAad);

        // Decrypt record
        var recordAad = Encoding.UTF8.GetBytes($"pia-e2ee-v1:{userId}:{entityType}:{entityId}");
        var decryptedBytes = _crypto.Decrypt(dek, encryptedPayload, recordAad);

        // Clear DEK
        Array.Clear(dek);

        // Handle both compressed (0x01 prefix) and legacy uncompressed formats
        byte[] jsonBytes;
        if (decryptedBytes.Length > 0 && decryptedBytes[0] == 0x01)
        {
            // Compressed format: skip marker byte, decompress rest
            using var input = new MemoryStream(decryptedBytes, 1, decryptedBytes.Length - 1);
            using var zlibStream = new ZLibStream(input, CompressionMode.Decompress);
            using var resultStream = new MemoryStream();
            zlibStream.CopyTo(resultStream);
            jsonBytes = resultStream.ToArray();
        }
        else
        {
            // Legacy uncompressed format (raw JSON starting with '{' or '[')
            jsonBytes = decryptedBytes;
        }

        return JsonSerializer.Deserialize<T>(jsonBytes)
            ?? throw new InvalidOperationException("Failed to deserialize decrypted record");
    }

    public (string Ciphertext, string HkdfSalt) WrapUmkForSelf()
    {
        var deviceId = _deviceKeys.GetDeviceId();
        var publicKey = _deviceKeys.GetAgreementPublicKey();
        return WrapUmkForDevice(publicKey, deviceId);
    }

    public (string Ciphertext, string HkdfSalt) WrapUmkForDevice(
        string targetAgreementPublicKeyBase64, string targetDeviceId)
    {
        var umk = LoadUmk() ?? throw new InvalidOperationException("UMK not available");

        // ECDH shared secret
        var sharedSecret = _deviceKeys.DeriveSharedSecret(targetAgreementPublicKeyBase64);

        // HKDF to derive KEK
        var salt = _crypto.GenerateRandomBytes(32);
        var kek = _crypto.DeriveKey(sharedSecret, salt, $"pia-umk-wrap-v1:{targetDeviceId}");

        // Wrap UMK with KEK
        var aad = Encoding.UTF8.GetBytes($"pia-umk-wrap-v1:{targetDeviceId}");
        var ciphertext = _crypto.Encrypt(kek, umk, aad);

        // Clear sensitive material
        Array.Clear(sharedSecret);
        Array.Clear(kek);

        return (ciphertext, Convert.ToBase64String(salt));
    }

    public byte[] UnwrapUmkForDevice(
        string ciphertextBase64, string hkdfSaltBase64,
        string senderAgreementPublicKeyBase64, string thisDeviceId)
    {
        // ECDH shared secret (using our private key + sender's public key)
        var sharedSecret = _deviceKeys.DeriveSharedSecret(senderAgreementPublicKeyBase64);

        // HKDF to derive KEK (same parameters as wrapping)
        var salt = Convert.FromBase64String(hkdfSaltBase64);
        var kek = _crypto.DeriveKey(sharedSecret, salt, $"pia-umk-wrap-v1:{thisDeviceId}");

        // Unwrap UMK
        var aad = Encoding.UTF8.GetBytes($"pia-umk-wrap-v1:{thisDeviceId}");
        var umk = _crypto.Decrypt(kek, ciphertextBase64, aad);

        // Clear sensitive material
        Array.Clear(sharedSecret);
        Array.Clear(kek);

        return umk;
    }

    public bool IsReady()
    {
        var settings = _settings.GetSettingsAsync().GetAwaiter().GetResult();
        return settings.IsE2EEEnabled && HasUmk();
    }
}
