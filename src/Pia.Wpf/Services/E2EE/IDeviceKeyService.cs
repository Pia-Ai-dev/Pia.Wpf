namespace Pia.Services.E2EE;

public interface IDeviceKeyService
{
    /// <summary>
    /// Get or create a stable device identifier (GUID stored in settings).
    /// </summary>
    string GetDeviceId();

    /// <summary>
    /// Get or create the ECDH P-256 key pair for this device.
    /// Private key is non-exportable in CNG; public key is returned as base64.
    /// </summary>
    string GetAgreementPublicKey();

    /// <summary>
    /// Get or create the ECDSA P-256 key pair for this device.
    /// </summary>
    string GetSigningPublicKey();

    /// <summary>
    /// Perform ECDH key agreement with a remote device's public key.
    /// Returns the raw shared secret bytes.
    /// </summary>
    byte[] DeriveSharedSecret(string remoteAgreementPublicKeyBase64);

    /// <summary>
    /// Sign data with this device's ECDSA private key.
    /// Returns base64 signature.
    /// </summary>
    string Sign(byte[] data);

    /// <summary>
    /// Verify a signature from a remote device.
    /// </summary>
    bool Verify(byte[] data, string signatureBase64, string signingPublicKeyBase64);

    /// <summary>
    /// Compute a human-readable fingerprint of this device's agreement public key.
    /// Format: "XXXX-XXXX-XXXX-XXXX" (SHA-256 truncated to 64 bits, hex).
    /// </summary>
    string GetFingerprint();

    /// <summary>
    /// Compute fingerprint of any public key.
    /// </summary>
    string ComputeFingerprint(string agreementPublicKeyBase64);

    /// <summary>
    /// Check if device keys exist (have been initialized).
    /// </summary>
    bool HasDeviceKeys();
}
