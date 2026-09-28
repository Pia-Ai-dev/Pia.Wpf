using System.Text;

namespace Pia.Shared.E2EE;

/// <summary>The bytes an approving device signs, so the server and the joining device verify the same thing.</summary>
public static class DeviceApprovalSignature
{
    public const int Current = 2;

    /// <summary>Null for a version this build does not know. Version 1, which clients up to 1.4.278 sign, does
    /// not cover the wrapped key.</summary>
    public static byte[]? Payload(
        int version, string onboardingSessionId, string targetDeviceId, string targetAgreementPublicKey,
        string wrappedUmk, string hkdfSalt) => version switch
    {
        1 => Encoding.UTF8.GetBytes($"{onboardingSessionId}:{targetDeviceId}:{targetAgreementPublicKey}"),
        2 => Encoding.UTF8.GetBytes(
            $"pia-approval-v2:{onboardingSessionId}:{targetDeviceId}:{targetAgreementPublicKey}:{wrappedUmk}:{hkdfSalt}"),
        _ => null,
    };
}
