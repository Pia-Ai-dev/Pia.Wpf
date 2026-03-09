using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

public interface IRecoveryCodeService
{
    /// <summary>
    /// Generate a human-readable recovery code (128-bit entropy, Base32, groups of 4).
    /// </summary>
    string GenerateRecoveryCode();

    /// <summary>
    /// Derive a wrapping key from a recovery code using Argon2id.
    /// Returns (recoveryKek, kdfSalt) where kdfSalt is randomly generated.
    /// </summary>
    (byte[] RecoveryKek, byte[] KdfSalt) DeriveKeyFromRecoveryCode(string recoveryCode);

    /// <summary>
    /// Derive a wrapping key from a recovery code using Argon2id with a known salt.
    /// </summary>
    byte[] DeriveKeyFromRecoveryCode(string recoveryCode, byte[] kdfSalt);

    /// <summary>
    /// Wrap UMK with a key derived from the recovery code.
    /// </summary>
    RecoveryWrappedUmkBlob WrapUmkForRecovery(byte[] umk, string recoveryCode);

    /// <summary>
    /// Unwrap UMK from a recovery blob using the recovery code.
    /// </summary>
    byte[] UnwrapUmkFromRecovery(RecoveryWrappedUmkBlob blob, string recoveryCode);
}
