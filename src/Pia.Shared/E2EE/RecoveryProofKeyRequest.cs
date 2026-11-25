namespace Pia.Shared.E2EE;

/// <summary>Body of <c>PUT api/e2ee/recovery/proof-key</c>; write-only, so never reuse it in a GET response.</summary>
public class RecoveryProofKeyRequest
{
    /// <summary>Base64 of the 32-byte <see cref="RecoveryActivationProof.DeriveProofKey"/> output.</summary>
    public required string ProofKey { get; set; }

    public int Version { get; set; } = RecoveryActivationProof.Current;
}
