using Pia.Models;

namespace Pia.Services.Consent;

public enum PostSttFilterDecision { Allow, DropAndAudit }

/// <summary>
/// Defense-in-depth filter: enforces consent state on post-STT utterances regardless of what
/// the pre-STT gate decided. Any non-Granted state on a <see cref="TranscriptChannel.Regular"/>
/// utterance is dropped and audited as a gate-bug indicator (spec §6.6).
/// </summary>
public interface IPostSttDefenseFilter
{
    PostSttFilterDecision Evaluate(TranscriptUtterance utterance);

    /// <summary>
    /// Number of post-STT drops observed since construction. Surface at session end —
    /// any non-zero value indicates a pre-STT gate bug per spec §6.6.
    /// </summary>
    int DropCount { get; }
}
