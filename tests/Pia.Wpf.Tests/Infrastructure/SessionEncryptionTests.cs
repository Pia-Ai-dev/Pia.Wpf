using System.Security.Cryptography;
using System.Text;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Wpf.Tests.Infrastructure;

public sealed class SessionEncryptionTests
{
    [Fact]
    public void RoundTrip_RecoversPlaintext()
    {
        var sut = SessionEncryption.CreateSession();
        var data = Encoding.UTF8.GetBytes("the quick brown fox");
        var ct = sut.Encrypt(data);
        Assert.NotEqual(data, ct);
        var pt = sut.Decrypt(ct);
        Assert.Equal(data, pt);
    }

    [Fact]
    public void TamperedCiphertext_FailsAuthentication()
    {
        var sut = SessionEncryption.CreateSession();
        var ct = sut.Encrypt("hello"u8.ToArray());
        ct[ct.Length - 1] ^= 0x01;
        // AuthenticationTagMismatchException derives from CryptographicException.
        Assert.ThrowsAny<CryptographicException>(() => sut.Decrypt(ct));
    }

    [Fact]
    public void NoncesAreUnique_AcrossEncryptCalls()
    {
        var sut = SessionEncryption.CreateSession();
        var a = sut.Encrypt("same"u8.ToArray());
        var b = sut.Encrypt("same"u8.ToArray());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WrappedKey_RoundTrips()
    {
        var sut = SessionEncryption.CreateSession();
        var ct = sut.Encrypt("session-data"u8.ToArray());
        var wrapped = sut.WrapKey();

        var restored = SessionEncryption.FromWrappedKey(wrapped, sut.KeyId);
        Assert.Equal(sut.KeyId, restored.KeyId);
        var pt = restored.Decrypt(ct);
        Assert.Equal("session-data"u8.ToArray(), pt);
    }
}
