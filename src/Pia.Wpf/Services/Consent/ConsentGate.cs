using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

public sealed class ConsentGate : IConsentGate
{
    private readonly IConsentStateManager _mgr;
    private readonly ILogger<ConsentGate> _logger;

    public ConsentGate(IConsentStateManager mgr, ILogger<ConsentGate> logger)
    {
        _mgr = mgr;
        _logger = logger;
    }

    public GateDecision Evaluate(string speakerLabel)
    {
        var state = _mgr.CurrentState(speakerLabel);
        return state switch
        {
            ConsentState.Granted => GateDecision.PassToTranscript,
            ConsentState.Prompted => GateDecision.PassToConsentClassifier,
            _ => GateDecision.Drop
        };
    }
}
