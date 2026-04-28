namespace Pia.Services.Consent.Biometric;

public sealed record BiometricMatchResult(BiometricConsentEntry Entry, float Similarity);

public interface IBiometricMatcher
{
    /// <summary>
    /// Find the stored entry whose embedding is most similar to <paramref name="embedding"/>
    /// above the given threshold. Returns <c>null</c> if no entry exceeds the threshold.
    /// </summary>
    Task<BiometricMatchResult?> MatchAsync(float[] embedding, float threshold = 0.85f, CancellationToken ct = default);
}
