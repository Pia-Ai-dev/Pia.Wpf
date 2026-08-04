namespace Pia.Services.Consent;

/// <summary>
/// Session-scoped consent state of one diarizer speaker label. v1 drives exactly these three:
/// the old prompt machine (Prompted/Timeout/Ambiguous/Denied) has no producer here, because consent
/// is speaker-initiated — nothing prompts, so nothing can time out or come back ambiguous.
///
/// <para><c>default(ConsentState)</c> is <see cref="Unknown"/>, and the forward loop's gate relies on
/// that for its fail-closed path: an unseen label reads as Unknown and its speech is dropped.</para>
/// </summary>
public enum ConsentState
{
    /// <summary>No consent sentence has been recognised for this label. Speech is dropped.</summary>
    Unknown = 0,

    /// <summary>A consent sentence was recognised and evidence recorded. Speech is transcribed.</summary>
    Granted = 1,

    /// <summary>Consent was withdrawn in-session. Speech is dropped; the grant evidence is preserved.</summary>
    Revoked = 2
}
