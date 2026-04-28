namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Coordinates the cross-session biometric flow (spec §2.4 ConsentScope.biometric_persistence,
/// §8 Phase 5):
///   * <see cref="TryMatchExistingAsync"/> — on session start, try to short-circuit the regular
///     consent prompt with a stored, still-valid biometric grant.
///   * <see cref="OfferOptInAsync"/> — after a fresh regular GRANT, ask the speaker if they
///     want their voice persisted across sessions.
/// </summary>
public interface IBiometricConsentService
{
    Task<BiometricMatchOutcome> TryMatchExistingAsync(
        string speakerLabel, float[] embedding, CancellationToken ct = default);

    Task<BiometricOptInOutcome> OfferOptInAsync(
        string speakerLabel, float[] embedding, string consentEvidencePath, CancellationToken ct = default);
}

public enum BiometricMatchOutcome
{
    NoMatch,
    MatchedAndReused,    // fresh entry, consent state set to Granted via reuse
    MatchedButExpired,   // entry was stale; was deleted; caller continues with normal flow
}

public enum BiometricOptInOutcome
{
    Skipped,             // profile flag off
    Granted,             // user said yes; entry persisted
    Denied,              // user said no
    Ambiguous,           // could not classify; treat as Denied
}
