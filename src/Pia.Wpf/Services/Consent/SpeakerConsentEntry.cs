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
    /// <summary>
    /// Per-speaker consent scope (spec §2.4). Null until set; when null, callers should
    /// derive a scope from the active <see cref="SecurityProfile"/> via
    /// <see cref="ConsentScope.FromProfile"/>. The pre-cloud pipeline must read scope from
    /// here for every utterance that touches a cloud provider.
    /// </summary>
    public ConsentScope? Scope { get; set; }

    public SpeakerConsentEntry(string speakerLabel, DateTimeOffset firstDetected)
    {
        SpeakerLabel = speakerLabel;
        FirstDetected = firstDetected;
    }
}
