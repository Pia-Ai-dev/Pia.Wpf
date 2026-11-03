namespace Pia.Shared.E2EE;

/// <summary>Values of the <c>error</c> field in an E2EE endpoint's error body.</summary>
public static class E2EEErrorCodes
{
    public const string InvalidProof = "invalid_proof";

    /// <summary>A proof key is on file, so the recovery-wrapped key can no longer be replaced.</summary>
    public const string RecoveryKeyLocked = "recovery_key_locked";

    /// <summary>A different proof key is already on file; the first write wins.</summary>
    public const string ProofKeyConflict = "proof_key_conflict";

    /// <summary>The calling device has been revoked; its session is refused on sync and the E2EE write routes.</summary>
    public const string DeviceRevoked = "device_revoked";

    /// <summary>The session is bound to a different device than the one being registered or re-keyed.</summary>
    public const string DeviceMismatch = "device_mismatch";

    /// <summary>The account hit its cap of devices waiting for approval.</summary>
    public const string TooManyPendingDevices = "too_many_pending_devices";

    /// <summary>A malformed registration field; the server states the problem in the message.</summary>
    public const string InvalidField = "invalid_field";

    /// <summary>The device row changed while it was being registered; the caller should retry.</summary>
    public const string DeviceChanged = "device_changed";

    /// <summary>The account is E2EE-enabled, so plaintext writes are refused.</summary>
    public const string E2EERequired = "e2ee_required";

    /// <summary>The onboarding session is spent, invalid or expired.</summary>
    public const string InvalidOnboardingSession = "invalid_onboarding_session";

    /// <summary>The session behind the bearer token is gone on the server; only a new sign-in helps.</summary>
    public const string SessionEnded = "session_ended";

    /// <summary>The route needs a device signature and the request carried none.</summary>
    public const string SignatureRequired = "signature_required";

    /// <summary>The device signature does not verify, or its challenge is expired or spent.</summary>
    public const string InvalidSignature = "invalid_signature";

    /// <summary>The device asking for a challenge is unknown, pending or revoked.</summary>
    public const string DeviceNotActive = "device_not_active";
}
