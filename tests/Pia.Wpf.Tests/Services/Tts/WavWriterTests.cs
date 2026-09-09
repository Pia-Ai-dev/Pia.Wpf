using System.IO;
using System.Text;
using NAudio.Wave;
using Pia.Services.Tts;
using Xunit;

namespace Pia.Tests.Services.Tts;

/// <summary>
/// The container is what makes sherpa's floats playable: the round-trip below goes through the very
/// reader that playback and the filler cache use, so a header this file gets wrong fails here.
/// </summary>
public class WavWriterTests
{
    private static readonly float[] Ramp = [0f, 0.5f, -0.5f, 0.25f];

    [Fact]
    public void Writes_a_16_bit_mono_pcm_header_at_the_requested_rate()
    {
        var wav = WavWriter.FromSamples(Ramp, 22050);

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(22050, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
        Assert.Equal(Ramp.Length, reader.SampleCount);
    }

    [Fact]
    public void Chunk_tags_and_lengths_agree_with_the_payload()
    {
        var wav = WavWriter.FromSamples(Ramp, 16000);
        var dataBytes = Ramp.Length * sizeof(short);

        Assert.Equal(44 + dataBytes, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(36 + dataBytes, BitConverter.ToInt32(wav, 4));
        Assert.Equal(dataBytes, BitConverter.ToInt32(wav, 40));
        Assert.Equal(16000 * 2, BitConverter.ToInt32(wav, 28));
    }

    [Fact]
    public void Samples_survive_the_round_trip()
    {
        var wav = WavWriter.FromSamples(Ramp, 16000);

        using var reader = new WaveFileReader(new MemoryStream(wav));
        var read = new List<float>();
        while (reader.ReadNextSampleFrame() is { } frame) read.Add(frame[0]);

        Assert.Equal(Ramp.Length, read.Count);
        for (var i = 0; i < Ramp.Length; i++)
            Assert.Equal(Ramp[i], read[i], 0.001);
    }

    /// <summary>A model can overshoot unity; wrapping instead of clamping is audible as a click.</summary>
    [Fact]
    public void Out_of_range_samples_clamp_instead_of_wrapping()
    {
        var wav = WavWriter.FromSamples([2f, -2f], 16000);

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(wav, 44));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(wav, 46));
    }

    [Fact]
    public void An_empty_generation_is_still_a_readable_wav()
    {
        var wav = WavWriter.FromSamples([], 16000);

        Assert.Equal(44, wav.Length);

        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(0, reader.SampleCount);
    }
}
