using Pia.Models;

namespace Pia.Services.Consent;

/// <summary>
/// Per spec §7. Profiles are immutable presets that toggle behavior across the consent
/// pipeline (orchestrator strategy, cloud allowance, retention).
/// </summary>
public sealed record SecurityProfile(
    SecurityMode Mode,
    NewSpeakerStrategy Strategy,
    bool AllowEuCloud,
    bool AllowNonEuCloud,
    int TranscriptRetentionDays,
    int ConsentEvidenceRetentionDays,
    bool PersistConsentAudioSnippet,
    bool AllowBiometricPersistenceByDefault = false,
    int BiometricRetentionMonths = 12)
{
    public static readonly SecurityProfile Strict = new(
        SecurityMode.Strict,
        NewSpeakerStrategy.PauseAndReConsent,
        AllowEuCloud: false,
        AllowNonEuCloud: false,
        TranscriptRetentionDays: 7,
        ConsentEvidenceRetentionDays: 7,
        PersistConsentAudioSnippet: true,
        AllowBiometricPersistenceByDefault: false);

    public static readonly SecurityProfile Standard = new(
        SecurityMode.Standard,
        NewSpeakerStrategy.SelectiveRecording,
        AllowEuCloud: true,
        AllowNonEuCloud: false,
        TranscriptRetentionDays: 30,
        ConsentEvidenceRetentionDays: 30,
        PersistConsentAudioSnippet: false,
        AllowBiometricPersistenceByDefault: true);

    public static readonly SecurityProfile Permissive = new(
        SecurityMode.Permissive,
        NewSpeakerStrategy.SelectiveRecording,
        AllowEuCloud: true,
        AllowNonEuCloud: true,
        TranscriptRetentionDays: 90,
        ConsentEvidenceRetentionDays: 90,
        PersistConsentAudioSnippet: false,
        AllowBiometricPersistenceByDefault: true);

    public static SecurityProfile ForMode(SecurityMode mode) => mode switch
    {
        SecurityMode.Strict => Strict,
        SecurityMode.Standard => Standard,
        SecurityMode.Permissive => Permissive,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown security mode"),
    };
}
