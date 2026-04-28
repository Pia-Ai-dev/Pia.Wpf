using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent.Biometric;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

public sealed class EncryptedFileBiometricConsentStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public EncryptedFileBiometricConsentStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaBiometric_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "store.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private EncryptedFileBiometricConsentStore Make() => new(
        _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);

    private static float[] Emb(params float[] xs) => xs;

    [Fact]
    public async Task Add_Then_GetAll_RoundTrips_Entry()
    {
        var s = Make();
        var now = DateTimeOffset.UtcNow;
        var e = await s.AddAsync("Alice", Emb(0.1f, 0.2f, 0.3f), now, now.AddMonths(12), "ev.json", "h1");

        var all = await s.GetAllAsync();
        Assert.Single(all);
        Assert.Equal(e.Id, all[0].Id);
        Assert.Equal("Alice", all[0].DisplayName);
        Assert.Equal("h1", all[0].PromptVersionHash);
    }

    [Fact]
    public async Task DecryptEmbedding_Returns_OriginalEmbedding()
    {
        var s = Make();
        var orig = Emb(0.5f, -0.5f, 0.25f, 0.75f);
        var now = DateTimeOffset.UtcNow;
        var e = await s.AddAsync("Bob", orig, now, now.AddMonths(12), "ev", "h");

        var got = await s.DecryptEmbeddingAsync(e);
        Assert.Equal(orig, got);
    }

    [Fact]
    public async Task Remove_Removes_Entry()
    {
        var s = Make();
        var now = DateTimeOffset.UtcNow;
        var e = await s.AddAsync("Alice", Emb(0.1f), now, now.AddMonths(12), "ev", "h");

        Assert.True(await s.RemoveAsync(e.Id));
        Assert.Empty(await s.GetAllAsync());
        Assert.False(await s.RemoveAsync(e.Id));
    }

    [Fact]
    public async Task Rename_UpdatesDisplayName()
    {
        var s = Make();
        var now = DateTimeOffset.UtcNow;
        var e = await s.AddAsync("Alice", Emb(0.1f), now, now.AddMonths(12), "ev", "h");

        Assert.True(await s.RenameAsync(e.Id, "Alice Cooper"));
        var got = await s.GetAsync(e.Id);
        Assert.Equal("Alice Cooper", got!.DisplayName);
    }

    [Fact]
    public async Task File_OnDisk_IsNotPlaintext()
    {
        var s = Make();
        var now = DateTimeOffset.UtcNow;
        await s.AddAsync("UniqueDisplayName-XYZ", Emb(0.1f, 0.2f), now, now.AddMonths(12), "ev", "h");

        var bytes = await File.ReadAllBytesAsync(_filePath);
        // The display name must not appear as UTF-8 in the encrypted file.
        var marker = System.Text.Encoding.UTF8.GetBytes("UniqueDisplayName-XYZ");
        Assert.False(ContainsSubsequence(bytes, marker), "Plaintext display name found in encrypted file");
    }

    [Fact]
    public async Task File_Tampering_Causes_Read_To_Throw()
    {
        var s = Make();
        var now = DateTimeOffset.UtcNow;
        await s.AddAsync("Alice", Emb(0.1f), now, now.AddMonths(12), "ev", "h");

        // Flip a byte in the middle.
        var bytes = await File.ReadAllBytesAsync(_filePath);
        bytes[bytes.Length / 2] ^= 0x42;
        await File.WriteAllBytesAsync(_filePath, bytes);

        var s2 = Make();
        await Assert.ThrowsAsync<CryptographicException>(() => s2.GetAllAsync());
    }

    [Fact]
    public async Task Persistence_AcrossInstances_PreservesEntries()
    {
        var s1 = Make();
        var now = DateTimeOffset.UtcNow;
        var e = await s1.AddAsync("Alice", Emb(0.1f, 0.2f), now, now.AddMonths(12), "ev", "h");

        var s2 = Make();
        var got = await s2.GetAsync(e.Id);
        Assert.NotNull(got);
        var emb = await s2.DecryptEmbeddingAsync(got!);
        Assert.Equal(new[] { 0.1f, 0.2f }, emb);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}

