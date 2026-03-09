namespace Pia.Shared.E2EE;

/// <summary>
/// Sent by a device activating via recovery code.
/// Proves UMK possession via HMAC over server challenge.
/// </summary>
public class RecoveryActivationRequest
{
    public required string DeviceId { get; set; }
    /// <summary>Base64: UMK self-wrapped for this device's agreement public key.</summary>
    public required string SelfWrappedUmk { get; set; }
    /// <summary>Base64: HKDF salt for the self-wrap.</summary>
    public required string HkdfSalt { get; set; }
    /// <summary>
    /// Base64: HMAC-SHA256(HKDF(UMK, "activation"), serverChallenge).
    /// Proves the device actually possesses UMK without revealing it.
    /// </summary>
    public required string ProofOfPossession { get; set; }
    public required string OnboardingSessionId { get; set; }
}
