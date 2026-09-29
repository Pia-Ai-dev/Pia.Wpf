using System.Security.Cryptography;
using System.Text;

namespace Pia.Shared.E2EE;

/// <summary>The recovery-activation proof, so the client and the server derive and check the same bytes.</summary>
public static class RecoveryActivationProof
{
    public const int Current = 1;
    public const string Salt = "activation";
    public const string Info = "pia-activation-proof-v1";
    public const int ProofKeyLength = 32;

    public static byte[] DeriveProofKey(byte[] umk) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256, umk, ProofKeyLength, Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes(Info));

    /// <summary>Hashes the session id's Base64 text as issued, not its decoded bytes.</summary>
    public static string Compute(byte[] proofKey, string onboardingSessionId) =>
        Convert.ToBase64String(HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(onboardingSessionId)));

    /// <summary>False for a proof that is not Base64, instead of throwing.</summary>
    public static bool Verify(byte[] proofKey, string onboardingSessionId, string proof)
    {
        var expected = HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(onboardingSessionId));
        var actual = new byte[expected.Length];
        return Convert.TryFromBase64String(proof, actual, out var written)
            && CryptographicOperations.FixedTimeEquals(actual.AsSpan(0, written), expected);
    }
}
