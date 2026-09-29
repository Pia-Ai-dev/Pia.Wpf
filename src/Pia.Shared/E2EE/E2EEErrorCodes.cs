namespace Pia.Shared.E2EE;

/// <summary>Values of the <c>error</c> field in an E2EE endpoint's error body.</summary>
public static class E2EEErrorCodes
{
    public const string InvalidProof = "invalid_proof";

    /// <summary>A proof key is on file, so the recovery-wrapped key can no longer be replaced.</summary>
    public const string RecoveryKeyLocked = "recovery_key_locked";

    /// <summary>A different proof key is already on file; the first write wins.</summary>
    public const string ProofKeyConflict = "proof_key_conflict";
}
