using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class FloatRingBufferTests
{
    [Fact]
    public void Write_Then_TryRead_ReturnsExactWindow_InOrder()
    {
        var buf = new FloatRingBuffer(capacity: 16);
        buf.Write(new float[] { 1, 2, 3, 4, 5 });

        var window = new float[3];
        Assert.True(buf.TryRead(window));
        Assert.Equal(new float[] { 1, 2, 3 }, window);
    }

    [Fact]
    public void TryRead_WhenInsufficientSamples_ReturnsFalse_AndDoesNotConsume()
    {
        var buf = new FloatRingBuffer(capacity: 16);
        buf.Write(new float[] { 1, 2 });

        var window = new float[3];
        Assert.False(buf.TryRead(window));

        // After failed read, the next successful read must observe all samples.
        buf.Write(new float[] { 3 });
        Assert.True(buf.TryRead(window));
        Assert.Equal(new float[] { 1, 2, 3 }, window);
    }

    [Fact]
    public void Write_WrapsAround_WithoutLosingSamples()
    {
        var buf = new FloatRingBuffer(capacity: 8);
        buf.Write(new float[] { 1, 2, 3, 4, 5 });

        var firstWindow = new float[3];
        Assert.True(buf.TryRead(firstWindow));
        Assert.Equal(new float[] { 1, 2, 3 }, firstWindow);

        buf.Write(new float[] { 6, 7, 8, 9, 10 });
        var secondWindow = new float[5];
        Assert.True(buf.TryRead(secondWindow));
        Assert.Equal(new float[] { 4, 5, 6, 7, 8 }, secondWindow);

        var thirdWindow = new float[2];
        Assert.True(buf.TryRead(thirdWindow));
        Assert.Equal(new float[] { 9, 10 }, thirdWindow);
    }

    [Fact]
    public void Write_BeyondCapacity_Throws()
    {
        var buf = new FloatRingBuffer(capacity: 4);
        Assert.Throws<InvalidOperationException>(() => buf.Write(new float[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void Clear_ResetsState()
    {
        var buf = new FloatRingBuffer(capacity: 8);
        buf.Write(new float[] { 1, 2, 3 });
        buf.Clear();

        var window = new float[1];
        Assert.False(buf.TryRead(window));
    }
}
