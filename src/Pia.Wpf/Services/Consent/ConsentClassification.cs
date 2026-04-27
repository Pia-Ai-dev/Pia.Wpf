namespace Pia.Services.Consent;

public enum ConsentDecision { Grant, Deny, Ambiguous }

public sealed record ConsentClassification(ConsentDecision Decision, float Confidence);
