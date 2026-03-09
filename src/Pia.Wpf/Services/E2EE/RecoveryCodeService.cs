using System.Text;
using Konscious.Security.Cryptography;
using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

public class RecoveryCodeService : IRecoveryCodeService
{
    private readonly ICryptoService _crypto;

    // Argon2id parameters: 64 MB memory, 3 iterations, 4 parallelism
    private const int DefaultMemoryCostKb = 65536;
    private const int DefaultTimeCost = 3;
    private const int DefaultParallelism = 4;

    // Base32 alphabet (RFC 4648)
    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    public RecoveryCodeService(ICryptoService crypto)
    {
        _crypto = crypto;
    }

    public string GenerateRecoveryCode()
    {
        // 128 bits of entropy
        var randomBytes = _crypto.GenerateRandomBytes(16);
        var base32 = ToBase32(randomBytes);

        // Format in groups of 4
        var groups = new List<string>();
        for (var i = 0; i < base32.Length; i += 4)
        {
            var end = Math.Min(i + 4, base32.Length);
            groups.Add(base32[i..end]);
        }
        return string.Join("-", groups);
    }

    public (byte[] RecoveryKek, byte[] KdfSalt) DeriveKeyFromRecoveryCode(string recoveryCode)
    {
        var salt = _crypto.GenerateRandomBytes(32);
        var kek = DeriveKeyFromRecoveryCode(recoveryCode, salt);
        return (kek, salt);
    }

    public byte[] DeriveKeyFromRecoveryCode(string recoveryCode, byte[] kdfSalt)
    {
        // Normalize: remove dashes, uppercase
        var normalized = recoveryCode.Replace("-", "").ToUpperInvariant();
        var codeBytes = Encoding.UTF8.GetBytes(normalized);

        using var argon2 = new Argon2id(codeBytes);
        argon2.Salt = kdfSalt;
        argon2.MemorySize = DefaultMemoryCostKb;
        argon2.Iterations = DefaultTimeCost;
        argon2.DegreeOfParallelism = DefaultParallelism;

        return argon2.GetBytes(32);
    }

    public RecoveryWrappedUmkBlob WrapUmkForRecovery(byte[] umk, string recoveryCode)
    {
        var (recoveryKek, kdfSalt) = DeriveKeyFromRecoveryCode(recoveryCode);

        var aad = Encoding.UTF8.GetBytes("pia-recovery-wrap-v1");
        var ciphertext = _crypto.Encrypt(recoveryKek, umk, aad);

        Array.Clear(recoveryKek);

        return new RecoveryWrappedUmkBlob
        {
            Ciphertext = ciphertext,
            KdfSalt = Convert.ToBase64String(kdfSalt),
            KdfMemoryCostKb = DefaultMemoryCostKb,
            KdfTimeCost = DefaultTimeCost,
            KdfParallelism = DefaultParallelism,
            WrapVersion = 1,
            UmkVersion = 1,
            CreatedAt = DateTime.UtcNow
        };
    }

    public byte[] UnwrapUmkFromRecovery(RecoveryWrappedUmkBlob blob, string recoveryCode)
    {
        var kdfSalt = Convert.FromBase64String(blob.KdfSalt);

        // Use stored Argon2id parameters (allows parameter upgrades)
        var normalized = recoveryCode.Replace("-", "").ToUpperInvariant();
        var codeBytes = Encoding.UTF8.GetBytes(normalized);

        using var argon2 = new Argon2id(codeBytes);
        argon2.Salt = kdfSalt;
        argon2.MemorySize = blob.KdfMemoryCostKb;
        argon2.Iterations = blob.KdfTimeCost;
        argon2.DegreeOfParallelism = blob.KdfParallelism;

        var recoveryKek = argon2.GetBytes(32);

        var aad = Encoding.UTF8.GetBytes("pia-recovery-wrap-v1");
        var umk = _crypto.Decrypt(recoveryKek, blob.Ciphertext, aad);

        Array.Clear(recoveryKek);

        return umk;
    }

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);

        return sb.ToString();
    }
}
