namespace Pia.Services.Consent;

public sealed class SpeakerConsentEntry
{
    public string SpeakerLabel { get; set; }
    public DateTimeOffset FirstDetected { get; }
    public ConsentState State { get; set; } = ConsentState.Unknown;
    public ConsentEvidence? Evidence { get; set; }
    public DateTimeOffset? PromptedAt { get; set; }
    /// <summary>
    /// Latest voice embedding observed for this speaker. Snapshotted into the session
    /// blocklist on Deny/Timeout/Revoke so the speaker's voice is dropped for the rest of
    /// the session (spec §3.9 blocklist filter). Session-only — never persisted.
    /// </summary>
    public float[]? Embedding { get; set; }

    public SpeakerConsentEntry(string speakerLabel, DateTimeOffset firstDetected)
    {
        SpeakerLabel = speakerLabel;
        FirstDetected = firstDetected;
    }
}
