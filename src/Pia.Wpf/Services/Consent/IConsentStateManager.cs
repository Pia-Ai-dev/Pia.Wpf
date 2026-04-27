namespace Pia.Services.Consent;

public sealed record ConsentStateChangedEventArgs(string SpeakerLabel, ConsentState OldState, ConsentState NewState);

public interface IConsentStateManager
{
    event EventHandler<ConsentStateChangedEventArgs>? StateChanged;

    SpeakerConsentEntry GetOrCreate(string speakerLabel);
    bool TryGet(string speakerLabel, out SpeakerConsentEntry entry);
    ConsentState CurrentState(string speakerLabel);

    void MarkPrompted(string speakerLabel);
    void RecordClassification(
        string speakerLabel,
        ConsentClassification classification,
        string transcriptText,
        string promptHash,
        string promptText,
        string sttModelId);
    void Revoke(string speakerLabel);
    void Rename(string oldLabel, string newLabel);
    void SweepTimeouts();

    TimeSpan PromptTimeout { get; set; }
    float GrantConfidenceThreshold { get; set; }
}
