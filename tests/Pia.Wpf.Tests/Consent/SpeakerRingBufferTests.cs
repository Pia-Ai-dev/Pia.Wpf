using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class SpeakerRingBufferTests
{
    [Fact]
    public void Append_BelowCapacity_RetainsAll()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 1000);
        sut.Append(new float[] { 1, 2, 3 });
        sut.Append(new float[] { 4, 5 });
        var snapshot = sut.Snapshot();
        Assert.Equal(new float[] { 1, 2, 3, 4, 5 }, snapshot);
    }

    [Fact]
    public void Append_OverCapacity_DropsOldest()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 4);
        sut.Append(new float[] { 1, 2, 3 });
        sut.Append(new float[] { 4, 5, 6 });
        var snapshot = sut.Snapshot();
        Assert.Equal(new float[] { 3, 4, 5, 6 }, snapshot);
    }

    [Fact]
    public void Drain_ReturnsAndClears()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 100);
        sut.Append(new float[] { 1, 2, 3 });
        var drained = sut.Drain();
        Assert.Equal(new float[] { 1, 2, 3 }, drained);
        Assert.Empty(sut.Snapshot());
    }

    [Fact]
    public void Clear_ZeroesUnderlyingStorage()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 4);
        sut.Append(new float[] { 1, 2, 3, 4 });
        sut.Clear();
        Assert.Empty(sut.Snapshot());
    }
}
