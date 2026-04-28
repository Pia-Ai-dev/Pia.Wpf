namespace Pia.Services.Consent;

public enum SecurityMode
{
    Strict,
    Standard,
    Permissive,
}

public enum NewSpeakerStrategy
{
    PauseAndReConsent,
    SelectiveRecording,
}

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
    bool PersistConsentAudioSnippet)
{
    public static readonly SecurityProfile Strict = new(
        SecurityMode.Strict,
        NewSpeakerStrategy.PauseAndReConsent,
        AllowEuCloud: false,
        AllowNonEuCloud: false,
        TranscriptRetentionDays: 7,
        ConsentEvidenceRetentionDays: 7,
        PersistConsentAudioSnippet: true);

    public static readonly SecurityProfile Standard = new(
        SecurityMode.Standard,
        NewSpeakerStrategy.SelectiveRecording,
        AllowEuCloud: true,
        AllowNonEuCloud: false,
        TranscriptRetentionDays: 30,
        ConsentEvidenceRetentionDays: 30,
        PersistConsentAudioSnippet: false);

    public static readonly SecurityProfile Permissive = new(
        SecurityMode.Permissive,
        NewSpeakerStrategy.SelectiveRecording,
        AllowEuCloud: true,
        AllowNonEuCloud: true,
        TranscriptRetentionDays: 90,
        ConsentEvidenceRetentionDays: 90,
        PersistConsentAudioSnippet: false);

    public static SecurityProfile ForMode(SecurityMode mode) => mode switch
    {
        SecurityMode.Strict => Strict,
        SecurityMode.Standard => Standard,
        SecurityMode.Permissive => Permissive,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown security mode"),
    };
}
