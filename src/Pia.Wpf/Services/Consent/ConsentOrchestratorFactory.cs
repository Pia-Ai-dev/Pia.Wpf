using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services.Consent;

public sealed class ConsentOrchestratorFactory : IConsentOrchestratorFactory
{
    private readonly ISecurityModeProvider _modeProvider;
    private readonly IConsentStateManager _consentMgr;
    private readonly IConsentAuditLog _auditLog;
    private readonly IBlocklistFilter? _blocklist;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConsentOrchestratorFactory> _logger;

    public SecurityProfile CurrentProfile => _modeProvider.Current;

    public ConsentOrchestratorFactory(
        ISecurityModeProvider modeProvider,
        IConsentStateManager consentMgr,
        IConsentAuditLog auditLog,
        ILoggerFactory loggerFactory,
        IBlocklistFilter? blocklist = null)
    {
        _modeProvider = modeProvider;
        _consentMgr = consentMgr;
        _auditLog = auditLog;
        _blocklist = blocklist;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ConsentOrchestratorFactory>();

        _modeProvider.ProfileChanged += (_, e) =>
            _logger.LogWarning(
                "Security profile changed mid-application: {Old} -> {New}. The new strategy applies on the next session start; the active session keeps its current orchestrator.",
                e.OldProfile.Mode, e.NewProfile.Mode);
    }

    public IConsentOrchestrator CreateForCurrentProfile()
    {
        var profile = _modeProvider.Current;
        return profile.Strategy switch
        {
            NewSpeakerStrategy.PauseAndReConsent => new StrategyAOrchestrator(
                _consentMgr, _auditLog, _loggerFactory.CreateLogger<StrategyAOrchestrator>(), _blocklist),
            NewSpeakerStrategy.SelectiveRecording => new StrategyBOrchestrator(),
            _ => throw new InvalidOperationException($"Unknown strategy {profile.Strategy}"),
        };
    }
}
