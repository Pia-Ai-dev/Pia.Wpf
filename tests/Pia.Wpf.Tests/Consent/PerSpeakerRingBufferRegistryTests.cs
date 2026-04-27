using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class PerSpeakerRingBufferRegistryTests
{
    [Fact]
    public void Append_DifferentSpeakers_CreatesSeparateBuffers()
    {
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 1024, totalCapacity: 4096);

        sut.Append("Speaker 1", new float[] { 1f, 2f, 3f });
        sut.Append("Speaker 2", new float[] { 4f, 5f });

        Assert.Equal(3, sut.Count("Speaker 1"));
        Assert.Equal(2, sut.Count("Speaker 2"));
    }

    [Fact]
    public void Drain_OnlyClearsRequestedSpeaker()
    {
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 1024, totalCapacity: 4096);
        sut.Append("Speaker 1", new float[] { 1f, 2f, 3f });
        sut.Append("Speaker 2", new float[] { 4f, 5f });

        var drained = sut.Drain("Speaker 1");

        Assert.Equal(new float[] { 1f, 2f, 3f }, drained);
        Assert.Equal(0, sut.Count("Speaker 1"));
        Assert.Equal(2, sut.Count("Speaker 2"));
    }

    [Fact]
    public void Drain_UnknownSpeaker_ReturnsEmpty()
    {
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 1024, totalCapacity: 4096);
        var drained = sut.Drain("Speaker X");
        Assert.Empty(drained);
    }

    [Fact]
    public void RemoveAll_ClearsEverything()
    {
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 1024, totalCapacity: 4096);
        sut.Append("Speaker 1", new float[] { 1f, 2f });
        sut.Append("Speaker 2", new float[] { 3f, 4f });

        sut.RemoveAll();

        Assert.Equal(0, sut.Count("Speaker 1"));
        Assert.Equal(0, sut.Count("Speaker 2"));
        Assert.Equal(0, sut.TotalSamples);
    }

    [Fact]
    public void Append_TotalCapExceeded_EvictsOldestFromLargestBuffer()
    {
        // Total cap = 10 samples. Speaker 1 fills 8, Speaker 2 fills 4 — total 12 > 10.
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 100, totalCapacity: 10);
        sut.Append("Speaker 1", new float[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        sut.Append("Speaker 2", new float[] { 9, 10, 11, 12 });

        Assert.True(sut.TotalSamples <= 10);
        // Oldest samples in the largest buffer (Speaker 1) should have been evicted.
        Assert.True(sut.Count("Speaker 1") < 8);
        Assert.Equal(4, sut.Count("Speaker 2"));
    }

    [Fact]
    public void Append_PerSpeakerCapExceeded_EvictsWithinThatBuffer()
    {
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 4, totalCapacity: 1024);
        sut.Append("Speaker 1", new float[] { 1, 2, 3, 4, 5, 6 });

        Assert.Equal(4, sut.Count("Speaker 1"));
        var snap = sut.Drain("Speaker 1");
        Assert.Equal(new float[] { 3f, 4f, 5f, 6f }, snap);
    }

    [Fact]
    public void TotalSamples_ReflectsAllSpeakers()
    {
        var sut = new PerSpeakerRingBufferRegistry(perSpeakerCapacity: 1024, totalCapacity: 4096);
        sut.Append("Speaker 1", new float[] { 1f, 2f, 3f });
        sut.Append("Speaker 2", new float[] { 4f, 5f });
        Assert.Equal(5, sut.TotalSamples);
    }
}
