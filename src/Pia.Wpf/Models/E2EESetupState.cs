namespace Pia.Models;

/// <summary>
/// State of the first-run wizard's E2EE setup step.
/// </summary>
public enum E2EESetupState
{
    Choice,
    ConfirmingOptOut,
    Bootstrapping,
    SavingRecoveryCode,
    Completed,
}
