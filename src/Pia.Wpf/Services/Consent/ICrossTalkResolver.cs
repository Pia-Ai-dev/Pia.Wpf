namespace Pia.Services.Consent;

/// <summary>
/// Decides whether a VAD segment that overlaps multiple active speakers should be
/// transcribed. Per spec §3.10: when ≥ 2 speakers are active in the same segment, the
/// conservative default is to drop unless every active speaker is Granted.
/// </summary>
public interface ICrossTalkResolver
{
    GateDecision Resolve(IReadOnlyCollection<string> activeSpeakerLabels);
}
