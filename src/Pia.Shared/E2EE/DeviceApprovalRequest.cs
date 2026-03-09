namespace Pia.Shared.E2EE;

/// <summary>
/// Sent by an Active device to approve a Pending device.
/// Contains UMK wrapped for the target device's agreement public key.
/// </summary>
public class DeviceApprovalRequest
{
    public required string OnboardingSessionId { get; set; }
    public required string TargetDeviceId { get; set; }
    /// <summary>Base64: AES-GCM(KEK, UMK) where KEK = HKDF(ECDH(approver, target)).</summary>
    public required string WrappedUmk { get; set; }
    /// <summary>Base64: HKDF salt used in KEK derivation.</summary>
    public required string HkdfSalt { get; set; }
    /// <summary>Base64: ECDSA signature from approving device over the approval payload.</summary>
    public string? ApproverSignature { get; set; }
    public required string ApproverDeviceId { get; set; }
}
