using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class PcmConversionTests
{
    [Fact]
    public void Pcm16LeToFloat_DecodesKnownSamples()
    {
        // 0x0000 → 0.0; 0x7FFF → ~0.9999; 0x8000 (-32768) → -1.0; 0xFFFF (-1) → ~-0.0000305
        var input = new byte[]
        {
            0x00, 0x00,
            0xFF, 0x7F,
            0x00, 0x80,
            0xFF, 0xFF,
        };

        var result = PcmConversion.Pcm16LeToFloat(input);

        Assert.Equal(0f, result[0]);
        Assert.Equal(32767f / 32768f, result[1], precision: 6);
        Assert.Equal(-1f, result[2]);
        Assert.Equal(-1f / 32768f, result[3], precision: 6);
    }

    [Fact]
    public void Pcm16LeToFloat_TruncatesOddByte()
    {
        var input = new byte[] { 0x00, 0x00, 0xFF };
        var result = PcmConversion.Pcm16LeToFloat(input);
        Assert.Single(result);
        Assert.Equal(0f, result[0]);
    }
}
