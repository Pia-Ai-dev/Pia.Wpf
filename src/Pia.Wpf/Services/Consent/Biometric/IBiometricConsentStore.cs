namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Persistent biometric-consent store (spec §4.1 "Voice-Embeddings (über Sessions)
/// — nur mit Extra-Consent"). Implementations MUST encrypt at rest and bind the
/// encryption to the current Windows user (DPAPI CurrentUser scope).
/// </summary>
public interface IBiometricConsentStore
{
    /// <summary>Inserts a new entry. Returns the persisted entry.</summary>
    Task<BiometricConsentEntry> AddAsync(
        string displayName,
        float[] embedding,
        DateTimeOffset grantedAt,
        DateTimeOffset expiresAt,
        string consentEvidencePath,
        string promptVersionHash,
        CancellationToken ct = default);

    Task<BiometricConsentEntry?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<BiometricConsentEntry>> GetAllAsync(CancellationToken ct = default);

    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Decrypt a single entry's embedding. Throws if the file was tampered with.</summary>
    Task<float[]> DecryptEmbeddingAsync(BiometricConsentEntry entry, CancellationToken ct = default);

    /// <summary>Update display name (user-editable in settings UI).</summary>
    Task<bool> RenameAsync(Guid id, string newDisplayName, CancellationToken ct = default);
}
