namespace Pia.Services.Consent;

public sealed class SpeakerConsentEntry
{
    public string SpeakerLabel { get; set; }
    public DateTimeOffset FirstDetected { get; }
    public ConsentState State { get; set; } = ConsentState.Unknown;
    public ConsentEvidence? Evidence { get; set; }
    public DateTimeOffset? PromptedAt { get; set; }

    public SpeakerConsentEntry(string speakerLabel, DateTimeOffset firstDetected)
    {
        SpeakerLabel = speakerLabel;
        FirstDetected = firstDetected;
    }
}
