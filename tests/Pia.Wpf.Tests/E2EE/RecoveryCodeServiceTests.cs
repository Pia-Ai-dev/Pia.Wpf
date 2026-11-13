namespace Pia.Tests.E2EE;

using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Pia.Services.E2EE;
using Pia.Shared.E2EE;
using Xunit;

public class RecoveryCodeServiceTests
{
    private readonly CryptoService _crypto = new();
    private readonly RecoveryCodeService _sut;

    public RecoveryCodeServiceTests()
    {
        _sut = new RecoveryCodeService(_crypto);
    }

    [Fact]
    public void GenerateRecoveryCode_ShouldReturnGroupedBase32()
    {
        var code = _sut.GenerateRecoveryCode();

        // Should contain dashes and uppercase alphanumeric
        Assert.Contains("-", code);
        Assert.Matches(@"^[A-Z2-7]{4}(-[A-Z2-7]{2,4})+$", code);
        Assert.True(code.Replace("-", "").Length >= 24); // 128 bits in base32 >= 26 chars
    }

    [Fact]
    public void GenerateRecoveryCode_ShouldBeUnique()
    {
        var code1 = _sut.GenerateRecoveryCode();
        var code2 = _sut.GenerateRecoveryCode();
        Assert.NotEqual(code1, code2);
    }

    [Fact]
    public void WrapUmk_UnwrapUmk_RoundTrip()
    {
        var umk = RandomNumberGenerator.GetBytes(32);
        var code = _sut.GenerateRecoveryCode();

        var blob = _sut.WrapUmkForRecovery(umk, code);
        var unwrapped = _sut.UnwrapUmkFromRecovery(blob, code);

        Assert.Equal(umk, unwrapped);
    }

    [Fact]
    public void UnwrapUmk_WrongCode_ShouldThrow()
    {
        var umk = RandomNumberGenerator.GetBytes(32);
        var correctCode = _sut.GenerateRecoveryCode();
        var wrongCode = _sut.GenerateRecoveryCode();

        var blob = _sut.WrapUmkForRecovery(umk, correctCode);

        Assert.ThrowsAny<Exception>(() => _sut.UnwrapUmkFromRecovery(blob, wrongCode));
    }

    [Fact]
    public void DeriveKey_SameSalt_SameCode_ShouldBeReproducible()
    {
        var code = _sut.GenerateRecoveryCode();
        var (kek1, salt) = _sut.DeriveKeyFromRecoveryCode(code);
        var kek2 = _sut.DeriveKeyFromRecoveryCode(code, salt);
        Assert.Equal(kek1, kek2);
    }

    [Theory]
    [InlineData(262_145, 3, 4, "KdfMemoryCostKb")]
    [InlineData(int.MaxValue, 3, 4, "KdfMemoryCostKb")]
    [InlineData(65_536, 11, 4, "KdfTimeCost")]
    [InlineData(65_536, 3, 17, "KdfParallelism")]
    public void UnwrapUmk_RefusesCostsAboveTheCeiling(int memoryKb, int time, int parallelism, string field)
    {
        var blob = Blob(memoryKb, time, parallelism);

        var ex = Assert.Throws<RecoveryCopyRejectedException>(
            () => _sut.UnwrapUmkFromRecovery(blob, _sut.GenerateRecoveryCode()));

        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData(0, 3, 4, "KdfMemoryCostKb")]
    [InlineData(-1, 3, 4, "KdfMemoryCostKb")]
    [InlineData(int.MinValue, 3, 4, "KdfMemoryCostKb")]
    [InlineData(65_536, 0, 4, "KdfTimeCost")]
    [InlineData(65_536, -3, 4, "KdfTimeCost")]
    [InlineData(65_536, 3, 0, "KdfParallelism")]
    [InlineData(65_536, 3, -4, "KdfParallelism")]
    public void UnwrapUmk_RefusesCostsArgon2idCannotRunWith(int memoryKb, int time, int parallelism, string field)
    {
        var blob = Blob(memoryKb, time, parallelism);

        var ex = Assert.Throws<RecoveryCopyRejectedException>(
            () => _sut.UnwrapUmkFromRecovery(blob, _sut.GenerateRecoveryCode()));

        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("QUJD=")]
    public void UnwrapUmk_RefusesASaltThatIsNotBase64(string salt)
    {
        var blob = Blob(65_536, 3, 4, salt);

        var ex = Assert.Throws<RecoveryCopyRejectedException>(
            () => _sut.UnwrapUmkFromRecovery(blob, _sut.GenerateRecoveryCode()));

        Assert.Contains("KdfSalt", ex.Message);
    }

    // A JSON null gets past the required modifier.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnwrapUmk_RefusesAMissingSalt(string? salt)
    {
        var blob = Blob(65_536, 3, 4);
        blob.KdfSalt = salt!;

        var ex = Assert.Throws<RecoveryCopyRejectedException>(
            () => _sut.UnwrapUmkFromRecovery(blob, _sut.GenerateRecoveryCode()));

        Assert.Contains("KdfSalt", ex.Message);
    }

    [Fact]
    public void UnwrapUmk_ChecksTheCostsBeforeDecodingTheSalt()
    {
        var blob = Blob(int.MaxValue, 3, 4, "not base64!");

        var ex = Assert.Throws<RecoveryCopyRejectedException>(
            () => _sut.UnwrapUmkFromRecovery(blob, _sut.GenerateRecoveryCode()));

        Assert.Contains("KdfMemoryCostKb", ex.Message);
    }

    [Fact]
    public void CheckedKdfSalt_AcceptsTheCeilingItself()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var blob = Blob(
            RecoveryCodeService.MaxKdfMemoryCostKb,
            RecoveryCodeService.MaxKdfTimeCost,
            RecoveryCodeService.MaxKdfParallelism,
            Convert.ToBase64String(salt));

        Assert.Equal(salt, RecoveryCodeService.CheckedKdfSalt(blob));
    }

    [Fact]
    public void UnwrapUmk_OpensACopyWrappedBelowTheDefaults()
    {
        var umk = RandomNumberGenerator.GetBytes(32);
        var code = _sut.GenerateRecoveryCode();
        var salt = RandomNumberGenerator.GetBytes(32);
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(code.Replace("-", "")))
        {
            Salt = salt,
            MemorySize = 8192,
            Iterations = 1,
            DegreeOfParallelism = 1,
        };
        var kek = argon2.GetBytes(32);
        var blob = new RecoveryWrappedUmkBlob
        {
            Ciphertext = _crypto.Encrypt(kek, umk, Encoding.UTF8.GetBytes("pia-recovery-wrap-v1")),
            KdfSalt = Convert.ToBase64String(salt),
            KdfMemoryCostKb = 8192,
            KdfTimeCost = 1,
            KdfParallelism = 1,
        };

        Assert.Equal(umk, _sut.UnwrapUmkFromRecovery(blob, code));
    }

    private static RecoveryWrappedUmkBlob Blob(int memoryKb, int time, int parallelism, string? salt = null) => new()
    {
        Ciphertext = Convert.ToBase64String(new byte[60]),
        KdfSalt = salt ?? Convert.ToBase64String(new byte[32]),
        KdfMemoryCostKb = memoryKb,
        KdfTimeCost = time,
        KdfParallelism = parallelism,
    };
}
