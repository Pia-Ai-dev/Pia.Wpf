namespace Pia.Shared.E2EE;

/// <summary>
/// Lightweight response for checking account-level E2EE status.
/// Used by new devices to detect E2EE before attempting sync.
/// </summary>
public class E2EEStatusResponse
{
    public bool IsEnabled { get; set; }
    public int UmkVersion { get; set; }
    public bool HasRecoveryKey { get; set; }
}
