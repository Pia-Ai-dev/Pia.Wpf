namespace Pia.Shared.E2EE;

public class DeviceRegistrationResponse
{
    public required string OnboardingSessionId { get; set; }
    /// <summary>
    /// Random challenge bytes (base64) the device must sign or MAC
    /// to prove possession of keys during activation.
    /// </summary>
    public required string ServerChallenge { get; set; }
    /// <summary>
    /// True if this is the user's first device (no other Active devices).
    /// Client uses this to decide: generate UMK vs wait for approval.
    /// </summary>
    public bool IsFirstDevice { get; set; }
}
