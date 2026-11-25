namespace Pia.Shared.E2EE;

/// <summary>Lets a new device detect E2EE before it syncs.</summary>
public class E2EEStatusResponse
{
    public bool IsEnabled { get; set; }
    public int UmkVersion { get; set; }
    public bool HasRecoveryKey { get; set; }

    /// <summary>Only whether a proof key is on file; the key itself is never returned.</summary>
    public bool HasRecoveryProofKey { get; set; }
}
