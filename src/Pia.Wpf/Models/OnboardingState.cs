namespace Pia.Models;

public enum OnboardingState
{
    Initial,
    WaitingForApproval,
    EnteringRecoveryCode,
    Activating,
    Success,
    Error
}
