namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Persistent biometric-consent entry (spec §2.4 ConsentScope.biometric_persistence,
/// §4.1 storage table). The embedding bytes are AES-GCM ciphertext produced by the
/// store; callers do not see plaintext embeddings. Every entry has an explicit
/// <see cref="ExpiresAt"/> (spec §4.6 retention policy, default 12 months).
/// </summary>
public sealed record BiometricConsentEntry(
    Guid Id,
    string DisplayName,
    byte[] EmbeddingCipherText,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    string ConsentEvidencePath,
    string PromptVersionHash)
{
    public static BiometricConsentEntry Create(
        Guid id,
        string displayName,
        byte[] embeddingCipherText,
        DateTimeOffset grantedAt,
        DateTimeOffset expiresAt,
        string consentEvidencePath,
        string promptVersionHash)
    {
        if (displayName is null) throw new ArgumentNullException(nameof(displayName));
        if (embeddingCipherText is null || embeddingCipherText.Length == 0)
            throw new ArgumentException("Embedding ciphertext required.", nameof(embeddingCipherText));
        if (consentEvidencePath is null) throw new ArgumentNullException(nameof(consentEvidencePath));
        if (promptVersionHash is null) throw new ArgumentNullException(nameof(promptVersionHash));
        if (expiresAt <= grantedAt)
            throw new ArgumentException("ExpiresAt must be strictly after GrantedAt.", nameof(expiresAt));
        return new BiometricConsentEntry(
            id, displayName, embeddingCipherText, grantedAt, expiresAt,
            consentEvidencePath, promptVersionHash);
    }
}
