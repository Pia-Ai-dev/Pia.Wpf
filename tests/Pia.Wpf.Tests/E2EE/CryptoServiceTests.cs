namespace Pia.Tests.E2EE;

using System.Security.Cryptography;
using System.Text;
using Pia.Services.E2EE;
using Xunit;

public class CryptoServiceTests
{
    private readonly CryptoService _sut = new();

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_ShouldReturnOriginal()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("Hello, E2EE!");

        var encrypted = _sut.Encrypt(key, plaintext);
        var decrypted = _sut.Decrypt(key, encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_WithAad_Decrypt_WithSameAad_ShouldSucceed()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("Secret data");
        var aad = Encoding.UTF8.GetBytes("pia-e2ee-v1:user123:template:abc");

        var encrypted = _sut.Encrypt(key, plaintext, aad);
        var decrypted = _sut.Decrypt(key, encrypted, aad);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WithWrongAad_ShouldThrow()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("Secret");
        var aad1 = Encoding.UTF8.GetBytes("correct-aad");
        var aad2 = Encoding.UTF8.GetBytes("wrong-aad");

        var encrypted = _sut.Encrypt(key, plaintext, aad1);

        Assert.ThrowsAny<CryptographicException>(() => _sut.Decrypt(key, encrypted, aad2));
    }

    [Fact]
    public void Decrypt_WithWrongKey_ShouldThrow()
    {
        var key1 = RandomNumberGenerator.GetBytes(32);
        var key2 = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("Secret");

        var encrypted = _sut.Encrypt(key1, plaintext);

        Assert.ThrowsAny<CryptographicException>(() => _sut.Decrypt(key2, encrypted));
    }

    [Fact]
    public void Encrypt_SameInput_ShouldProduceDifferentOutput()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("Same input");

        var encrypted1 = _sut.Encrypt(key, plaintext);
        var encrypted2 = _sut.Encrypt(key, plaintext);

        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void DeriveKey_SameInputs_ShouldProduceSameOutput()
    {
        var ikm = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);

        var key1 = _sut.DeriveKey(ikm, salt, "test-info");
        var key2 = _sut.DeriveKey(ikm, salt, "test-info");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveKey_DifferentInfo_ShouldProduceDifferentOutput()
    {
        var ikm = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);

        var key1 = _sut.DeriveKey(ikm, salt, "info-1");
        var key2 = _sut.DeriveKey(ikm, salt, "info-2");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void GenerateRandomBytes_ShouldReturnRequestedLength()
    {
        var bytes = _sut.GenerateRandomBytes(32);
        Assert.Equal(32, bytes.Length);
    }
}
