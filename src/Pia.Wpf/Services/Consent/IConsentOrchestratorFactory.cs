using Pia.Models;

namespace Pia.Services.Consent;

public interface IConsentOrchestratorFactory
{
    /// <summary>
    /// Build the orchestrator that should drive a session started right now. The chosen
    /// implementation is locked in for the session — callers must not swap orchestrators
    /// mid-meeting (would invalidate consent decisions already collected).
    /// </summary>
    IConsentOrchestrator CreateForCurrentProfile();

    SecurityProfile CurrentProfile { get; }
}
