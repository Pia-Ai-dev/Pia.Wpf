using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Default <see cref="IBiometricConsentStore"/>. JSON manifest at
/// <c>%LOCALAPPDATA%\Pia\Biometric\store.bin</c>; the file as a whole is wrapped with
/// DPAPI <c>CurrentUser</c> scope so a copy under a different Windows profile cannot
/// be read (spec hard rule).
/// </summary>
public sealed class EncryptedFileBiometricConsentStore : IBiometricConsentStore
{
    private static readonly byte[] Entropy = "Pia.BiometricStore.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _filePath;
    private readonly ILogger<EncryptedFileBiometricConsentStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => _filePath;

    public EncryptedFileBiometricConsentStore(
        string filePath,
        ILogger<EncryptedFileBiometricConsentStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "Biometric", "store.bin");

    public async Task<BiometricConsentEntry> AddAsync(
        string displayName,
        float[] embedding,
        DateTimeOffset grantedAt,
        DateTimeOffset expiresAt,
        string consentEvidencePath,
        string promptVersionHash,
        CancellationToken ct = default)
    {
        if (embedding is null || embedding.Length == 0)
            throw new ArgumentException("Embedding required.", nameof(embedding));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (entries, perEntryKey) = LoadInternal();
            var cipher = EncryptEmbedding(embedding, perEntryKey);
            var entry = BiometricConsentEntry.Create(
                Guid.NewGuid(), displayName, cipher, grantedAt, expiresAt,
                consentEvidencePath, promptVersionHash);
            entries.Add(entry);
            SaveInternal(entries, perEntryKey);
            return entry;
        }
        finally { _gate.Release(); }
    }

    public async Task<BiometricConsentEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (entries, _) = LoadInternal();
            return entries.FirstOrDefault(e => e.Id == id);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BiometricConsentEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (entries, _) = LoadInternal();
            return entries.ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (entries, key) = LoadInternal();
            var idx = entries.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            entries.RemoveAt(idx);
            SaveInternal(entries, key);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RenameAsync(Guid id, string newDisplayName, CancellationToken ct = default)
    {
        if (newDisplayName is null) throw new ArgumentNullException(nameof(newDisplayName));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (entries, key) = LoadInternal();
            var idx = entries.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            entries[idx] = entries[idx] with { DisplayName = newDisplayName };
            SaveInternal(entries, key);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<float[]> DecryptEmbeddingAsync(BiometricConsentEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (_, key) = LoadInternal();
            return DecryptEmbedding(entry.EmbeddingCipherText, key);
        }
        finally { _gate.Release(); }
    }

    // ---------- Persistence helpers ----------

    private sealed class StoreFile
    {
        public byte[] PerEntryKey { get; set; } = Array.Empty<byte>();
        public List<BiometricConsentEntry> Entries { get; set; } = new();
    }

    private (List<BiometricConsentEntry> Entries, byte[] PerEntryKey) LoadInternal()
    {
        if (!File.Exists(_filePath))
        {
            var freshKey = RandomNumberGenerator.GetBytes(32);
            return (new List<BiometricConsentEntry>(), freshKey);
        }
        var encrypted = File.ReadAllBytes(_filePath);
        // ProtectedData throws CryptographicException if the blob was produced by another
        // user — that's the hard rule: the store is unreadable across user profiles.
        var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        var file = JsonSerializer.Deserialize<StoreFile>(json, JsonOpts)
                   ?? new StoreFile { PerEntryKey = RandomNumberGenerator.GetBytes(32) };
        if (file.PerEntryKey.Length != 32)
            file.PerEntryKey = RandomNumberGenerator.GetBytes(32);
        return (file.Entries, file.PerEntryKey);
    }

    private void SaveInternal(List<BiometricConsentEntry> entries, byte[] perEntryKey)
    {
        var file = new StoreFile { Entries = entries, PerEntryKey = perEntryKey };
        var json = JsonSerializer.SerializeToUtf8Bytes(file, JsonOpts);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        File.Move(tmp, _filePath, overwrite: true);
    }

    // ---------- Per-entry AES-GCM ----------
    // Each entry's embedding is encrypted with a fresh nonce under the store-wide
    // per-entry key. The per-entry key lives inside the DPAPI-wrapped manifest, so
    // the embedding ciphertext is doubly protected: AES-GCM + DPAPI envelope.
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[] EncryptEmbedding(float[] embedding, byte[] key)
    {
        var pt = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, pt, 0, pt.Length);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, pt, ct, tag);
        var output = new byte[NonceSize + TagSize + ct.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ct, 0, output, NonceSize + TagSize, ct.Length);
        return output;
    }

    private static float[] DecryptEmbedding(byte[] wire, byte[] key)
    {
        if (wire.Length < NonceSize + TagSize)
            throw new CryptographicException("Embedding ciphertext too short.");
        var nonce = wire.AsSpan(0, NonceSize);
        var tag = wire.AsSpan(NonceSize, TagSize);
        var ct = wire.AsSpan(NonceSize + TagSize);
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ct, tag, pt);
        if (pt.Length % sizeof(float) != 0)
            throw new CryptographicException("Decrypted embedding length not a multiple of float size.");
        var floats = new float[pt.Length / sizeof(float)];
        Buffer.BlockCopy(pt, 0, floats, 0, pt.Length);
        return floats;
    }
}
