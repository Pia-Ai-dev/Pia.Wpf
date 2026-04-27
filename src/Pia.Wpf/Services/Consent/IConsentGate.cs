namespace Pia.Services.Consent;

public enum GateDecision { Drop, PassToConsentClassifier, PassToTranscript }

public interface IConsentGate
{
    GateDecision Evaluate(string speakerLabel);
}
