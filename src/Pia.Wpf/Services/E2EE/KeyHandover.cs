using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

/// <summary>A verified copy of the key that is not stored until <see cref="IDeviceManagementService.AcceptKeyHandoverAsync"/>.</summary>
public sealed record KeyHandover(
    WrappedUmkBlob Blob,
    string SenderAgreementPublicKey,
    int UmkVersion,
    string? ApproverDeviceName,
    string? ApproverFingerprint)
{
    /// <summary>A copy this device wrapped for itself, which no person has to confirm.</summary>
    public bool IsOwnCopy => ApproverFingerprint is null;
}
