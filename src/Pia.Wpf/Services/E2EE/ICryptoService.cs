namespace Pia.Services.E2EE;

public interface ICryptoService
{
    /// <summary>
    /// AES-256-GCM encrypt. Returns base64(nonce[12] || ciphertext || tag[16]).
    /// </summary>
    string Encrypt(byte[] key, byte[] plaintext, byte[]? aad = null);

    /// <summary>
    /// AES-256-GCM decrypt. Input: base64(nonce[12] || ciphertext || tag[16]).
    /// </summary>
    byte[] Decrypt(byte[] key, string ciphertextBase64, byte[]? aad = null);

    /// <summary>
    /// HKDF-SHA256 key derivation.
    /// </summary>
    byte[] DeriveKey(byte[] ikm, byte[] salt, string info, int outputLength = 32);

    /// <summary>
    /// Generate cryptographically random bytes.
    /// </summary>
    byte[] GenerateRandomBytes(int length);
}
