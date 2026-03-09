using System.Security.Cryptography;
using System.Text;

namespace Pia.Services.E2EE;

public class CryptoService : ICryptoService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Encrypt(byte[] key, byte[] plaintext, byte[]? aad = null)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, tagSizeInBytes: TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        // Format: nonce || ciphertext || tag
        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(combined, 0);
        ciphertext.CopyTo(combined, NonceSize);
        tag.CopyTo(combined, NonceSize + ciphertext.Length);

        return Convert.ToBase64String(combined);
    }

    public byte[] Decrypt(byte[] key, string ciphertextBase64, byte[]? aad = null)
    {
        var combined = Convert.FromBase64String(ciphertextBase64);

        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short");

        var nonce = combined.AsSpan(0, NonceSize);
        var tag = combined.AsSpan(combined.Length - TagSize);
        var ciphertext = combined.AsSpan(NonceSize, combined.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tagSizeInBytes: TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

        return plaintext;
    }

    public byte[] DeriveKey(byte[] ikm, byte[] salt, string info, int outputLength = 32)
    {
        var infoBytes = Encoding.UTF8.GetBytes(info);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength, salt, infoBytes);
    }

    public byte[] GenerateRandomBytes(int length) =>
        RandomNumberGenerator.GetBytes(length);
}
