using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class VoiceEmbeddingBlocklistTests
{
    [Fact]
    public void Empty_DoesNotDrop()
    {
        var sut = new VoiceEmbeddingBlocklist();
        Assert.False(sut.ShouldDrop(new[] { 1f, 0f, 0f }));
    }

    [Fact]
    public void ExactMatch_Drops()
    {
        var sut = new VoiceEmbeddingBlocklist();
        var v = new[] { 1f, 0f, 0f };
        sut.Add(v);
        Assert.True(sut.ShouldDrop((float[])v.Clone()));
    }

    [Fact]
    public void BelowThreshold_DoesNotDrop()
    {
        // Orthogonal vectors → cosine 0, well below 0.85.
        var sut = new VoiceEmbeddingBlocklist();
        sut.Add(new[] { 1f, 0f, 0f });
        Assert.False(sut.ShouldDrop(new[] { 0f, 1f, 0f }));
    }

    [Fact]
    public void AboveThreshold_Drops()
    {
        // sim ≈ 0.9 (cos of small angle in 2D between (1,0) and (0.9, sqrt(0.19)))
        var sut = new VoiceEmbeddingBlocklist(threshold: 0.85f);
        sut.Add(new[] { 1f, 0f });
        var close = new[] { 0.9f, MathF.Sqrt(1f - 0.81f) }; // unit-norm, sim with (1,0) = 0.9
        Assert.True(sut.ShouldDrop(close));
    }

    [Fact]
    public void Add_StoresCopy_NotReference()
    {
        var sut = new VoiceEmbeddingBlocklist();
        var v = new[] { 1f, 0f, 0f };
        sut.Add(v);
        v[0] = 0f; // mutate caller's vector
        Assert.True(sut.ShouldDrop(new[] { 1f, 0f, 0f }));
    }
}
