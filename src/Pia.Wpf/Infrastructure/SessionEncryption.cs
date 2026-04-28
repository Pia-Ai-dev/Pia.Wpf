using System.Security.Cryptography;

namespace Pia.Infrastructure;

/// <summary>
/// AES-256-GCM helpers for at-rest encryption of meeting artefacts (transcripts, snippets,
/// evidence). The session key is generated per session and wrapped via DPAPI; manifests
/// store only the wrapped key blob plus the key id.
///
/// File format: [12 bytes nonce][16 bytes tag][ciphertext...]
/// </summary>
public sealed class SessionEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;
    public string KeyId { get; }

    private SessionEncryption(byte[] key, string keyId)
    {
        _key = key;
        KeyId = keyId;
    }

    /// <summary>Creates a fresh per-session key. Use <see cref="WrapKey"/> to persist it.</summary>
    public static SessionEncryption CreateSession()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        return new SessionEncryption(key, keyId);
    }

    /// <summary>Restores a session from a previously DPAPI-wrapped key blob.</summary>
    public static SessionEncryption FromWrappedKey(byte[] wrapped, string keyId)
    {
        var key = ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
        if (key.Length != KeySize)
            throw new CryptographicException("Wrapped key has wrong length.");
        return new SessionEncryption(key, keyId);
    }

    public byte[] WrapKey() => ProtectedData.Protect(_key, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> wireBytes)
    {
        if (wireBytes.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short.");

        var nonce = wireBytes.Slice(0, NonceSize);
        var tag = wireBytes.Slice(NonceSize, TagSize);
        var ciphertext = wireBytes.Slice(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static readonly byte[] Entropy = "Pia.SessionEncryption.v1"u8.ToArray();
}
