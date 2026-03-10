namespace Pia.Shared.E2EE;

public class E2EEStatusResponse
{
    public bool IsEnabled { get; set; }
    public int UmkVersion { get; set; }
    public bool HasRecoveryKey { get; set; }
}
