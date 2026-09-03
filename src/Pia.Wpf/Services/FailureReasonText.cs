using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// The failure vocabulary is OPEN: the app-owned tokens are localized, everything else is a model summary or an
/// exception message and passes through unchanged, because paraphrasing it would drop the only actionable part.
/// </summary>
public static class FailureReasonText
{
    public static string? Describe(string? reason, ILocalizationService localization) => reason switch
    {
        null or "" => null,
        AgentStepTools.EmptyResponseFailure => localization["Run_Failed_EmptyResponse"],
        AgentStepTools.UndetailedFailure => localization["Run_Failed_Undetailed"],
        HeadlessRunLauncher.WorkspaceSetupFailure => localization["Run_Failed_WorkspaceSetup"],
        HeadlessRunLauncher.ShutdownInterruptedFailure => localization["Run_Failed_Interrupted"],
        AgentRunOrchestrator.SupersededFailureReason => localization["Run_Failed_Superseded"],
        _ => reason,
    };
}
