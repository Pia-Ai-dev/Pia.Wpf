namespace Pia.Services.Consent;

public sealed record ConsentEvidence(
    string TranscriptText,
    float ClassificationConfidence,
    DateTimeOffset Timestamp,
    string PromptVersionHash,
    string PromptTextPlayed,
    string SttModelId);
