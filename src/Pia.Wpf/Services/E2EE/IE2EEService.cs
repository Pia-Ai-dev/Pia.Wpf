namespace Pia.Services.E2EE;

/// <summary>
/// High-level E2EE operations: UMK lifecycle, record encrypt/decrypt, device wrapping.
/// </summary>
public interface IE2EEService
{
    /// <summary>
    /// Generate a new 32-byte UMK and store it locally (DPAPI-protected).
    /// Called only on first-device bootstrap.
    /// </summary>
    Task<byte[]> GenerateAndStoreUmkAsync();

    /// <summary>
    /// Load UMK from local secure storage. Returns null if not stored.
    /// </summary>
    byte[]? LoadUmk();

    /// <summary>
    /// Store UMK in local secure storage (DPAPI-protected).
    /// </summary>
    Task StoreUmkAsync(byte[] umk);

    /// <summary>
    /// Check if UMK is available locally.
    /// </summary>
    bool HasUmk();

    /// <summary>
    /// Encrypt a sync entity record (serialize to JSON, encrypt with random DEK, wrap DEK with UMK).
    /// Returns (encryptedPayload, wrappedDek) as base64 strings.
    /// </summary>
    (string EncryptedPayload, string WrappedDek) EncryptRecord(
        object record, string userId, string entityType, string entityId);

    /// <summary>
    /// Decrypt a sync entity record.
    /// Returns the deserialized object.
    /// </summary>
    T DecryptRecord<T>(
        string encryptedPayload, string wrappedDek,
        string userId, string entityType, string entityId);

    /// <summary>
    /// Wrap UMK for this device (self-wrap using ECDH with own public key).
    /// Returns (wrappedUmkCiphertext, hkdfSalt) as base64 strings.
    /// </summary>
    (string Ciphertext, string HkdfSalt) WrapUmkForSelf();

    /// <summary>
    /// Wrap UMK for another device using ECDH key agreement.
    /// </summary>
    (string Ciphertext, string HkdfSalt) WrapUmkForDevice(string targetAgreementPublicKeyBase64, string targetDeviceId);

    /// <summary>
    /// Unwrap UMK that was wrapped for this device.
    /// </summary>
    byte[] UnwrapUmkForDevice(string ciphertextBase64, string hkdfSaltBase64, string senderAgreementPublicKeyBase64, string thisDeviceId);

    /// <summary>
    /// Check if E2EE is enabled and UMK is available for sync operations.
    /// </summary>
    bool IsReady();
}
