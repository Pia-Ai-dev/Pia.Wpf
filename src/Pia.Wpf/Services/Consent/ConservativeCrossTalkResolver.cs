using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Drops the segment unless every active speaker is in <see cref="ConsentState.Granted"/>.
/// Conservative because true source separation is not available in the current pipeline —
/// dropping is the only way to honour a non-Granted speaker's status when their voice is
/// mixed into the same audio frame.
/// </summary>
public sealed class ConservativeCrossTalkResolver : ICrossTalkResolver
{
    private readonly IConsentStateManager _consentMgr;
    private readonly ILogger<ConservativeCrossTalkResolver> _logger;

    public ConservativeCrossTalkResolver(
        IConsentStateManager consentMgr,
        ILogger<ConservativeCrossTalkResolver> logger)
    {
        _consentMgr = consentMgr;
        _logger = logger;
    }

    public GateDecision Resolve(IReadOnlyCollection<string> activeSpeakerLabels)
    {
        if (activeSpeakerLabels.Count == 0) return GateDecision.Drop;

        foreach (var label in activeSpeakerLabels)
        {
            var state = _consentMgr.CurrentState(label);
            if (state != ConsentState.Granted)
            {
                _logger.LogDebug(
                    "Cross-talk drop: speaker {Label} state={State} (need Granted from all {Count})",
                    label, state, activeSpeakerLabels.Count);
                return GateDecision.Drop;
            }
        }
        return GateDecision.PassToTranscript;
    }
}
