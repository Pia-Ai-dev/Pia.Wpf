namespace Pia.Tests.E2EE;

using System.Security.Cryptography;
using Pia.Services.E2EE;
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
}
