namespace Pia.Services.Consent;

public enum ConsentState
{
    Unknown,
    Prompted,
    Granted,
    Denied,
    Revoked,
    Timeout,
    Ambiguous
}
