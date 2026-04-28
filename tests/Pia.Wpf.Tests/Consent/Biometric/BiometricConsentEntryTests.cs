using Pia.Services.Consent.Biometric;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

public sealed class BiometricConsentEntryTests
{
    private static BiometricConsentEntry Sample(
        DateTimeOffset? granted = null,
        DateTimeOffset? expires = null)
    {
        var g = granted ?? DateTimeOffset.UtcNow;
        var e = expires ?? g.AddMonths(12);
        return BiometricConsentEntry.Create(
            Guid.NewGuid(), "Alice", new byte[] { 1, 2, 3 }, g, e,
            "evidence/path.json", "abcd1234");
    }

    [Fact]
    public void Create_RoundTrips_AllFields()
    {
        var id = Guid.NewGuid();
        var g = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var e = g.AddMonths(12);
        var entry = BiometricConsentEntry.Create(id, "Bob", new byte[] { 9 }, g, e, "p.json", "h");

        Assert.Equal(id, entry.Id);
        Assert.Equal("Bob", entry.DisplayName);
        Assert.Equal(new byte[] { 9 }, entry.EmbeddingCipherText);
        Assert.Equal(g, entry.GrantedAt);
        Assert.Equal(e, entry.ExpiresAt);
        Assert.Equal("p.json", entry.ConsentEvidencePath);
        Assert.Equal("h", entry.PromptVersionHash);
    }

    [Fact]
    public void Create_NullDisplayName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BiometricConsentEntry.Create(
            Guid.NewGuid(), null!, new byte[] { 1 }, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1), "p", "h"));
    }

    [Fact]
    public void Create_EmptyCipherText_Throws()
    {
        Assert.Throws<ArgumentException>(() => BiometricConsentEntry.Create(
            Guid.NewGuid(), "X", Array.Empty<byte>(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1), "p", "h"));
    }

    [Fact]
    public void Create_ExpiryNotAfterGranted_Throws()
    {
        var t = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => BiometricConsentEntry.Create(
            Guid.NewGuid(), "X", new byte[] { 1 }, t, t, "p", "h"));
        Assert.Throws<ArgumentException>(() => BiometricConsentEntry.Create(
            Guid.NewGuid(), "X", new byte[] { 1 }, t, t.AddSeconds(-1), "p", "h"));
    }

    [Fact]
    public void Record_Equality_MatchesOnAllFields()
    {
        var a = Sample();
        var b = a with { };
        Assert.Equal(a, b);
    }
}
