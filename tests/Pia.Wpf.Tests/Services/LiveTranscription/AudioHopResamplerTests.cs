using NAudio.Wave;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class AudioHopResamplerTests
{
    // A "known WAV" built in memory (matching the in-memory style of PcmConversionTests):
    // 48 kHz, stereo, 16-bit PCM — a typical render-device mix format — carrying a constant 0.5.
    private const int SourceRate = 48000;
    private const short HalfScale = 16384; // 0.5 * 32768

    [Fact]
    public void ProcessAvailable_DownmixesResamplesAndHops_To16kMonoFloat()
    {
        var sourceFormat = new WaveFormat(SourceRate, 16, 2);
        var resampler = new AudioHopResampler(sourceFormat);

        // ~500 ms of constant 0.5 on both channels.
        const int sourceFrames = SourceRate / 2;
        var pcm = BuildConstantStereoPcm16(sourceFrames, HalfScale);

        var hops = new List<float[]>();
        foreach (var hop in resampler.ProcessAvailable(pcm, pcm.Length))
            hops.Add(hop);

        Assert.NotEmpty(hops);

        // Every hop except possibly the last (a short drain remainder) is a full 800-sample hop.
        Assert.Equal(AudioHopResampler.SamplesPerHop, AudioHopResampler.TargetSampleRate / 20);
        for (int i = 0; i < hops.Count - 1; i++)
            Assert.Equal(AudioHopResampler.SamplesPerHop, hops[i].Length);
        Assert.True(hops[^1].Length <= AudioHopResampler.SamplesPerHop);

        var all = hops.SelectMany(h => h).ToArray();

        // All samples finite and in range.
        foreach (var s in all)
        {
            Assert.True(float.IsFinite(s));
            Assert.InRange(s, -1.0f, 1.0f);
        }

        // Rate correctness: 500 ms downsampled 48 k -> 16 k yields ~8000 samples (allow resampler
        // edge effects — do NOT assert an exact count).
        const int expected = AudioHopResampler.TargetSampleRate / 2;
        Assert.InRange(all.Length, (int)(expected * 0.9), (int)(expected * 1.1));

        // Downmix + value path: stereo 0.5/0.5 must average back to ~0.5. Skip the leading samples
        // where the resampler's filter is still ramping up from silence.
        var settled = all.Skip(AudioHopResampler.SamplesPerHop).ToArray();
        Assert.NotEmpty(settled);
        var mean = settled.Average();
        Assert.InRange(mean, 0.45f, 0.55f);
    }

    [Fact]
    public void ProcessAvailable_PassthroughWhenAlready16kMono()
    {
        var sourceFormat = new WaveFormat(AudioHopResampler.TargetSampleRate, 16, 1);
        var resampler = new AudioHopResampler(sourceFormat);

        // Exactly two hops worth of mono 16 kHz samples at constant 0.5.
        int frames = AudioHopResampler.SamplesPerHop * 2;
        var pcm = BuildConstantMonoPcm16(frames, HalfScale);

        var hops = resampler.ProcessAvailable(pcm, pcm.Length).ToList();

        Assert.NotEmpty(hops);
        var all = hops.SelectMany(h => h).ToArray();
        Assert.Equal(frames, all.Length);
        foreach (var s in all)
            Assert.Equal(0.5f, s, precision: 4);
    }

    [Fact]
    public void ProcessAvailable_ZeroCount_YieldsNothing()
    {
        var resampler = new AudioHopResampler(new WaveFormat(SourceRate, 16, 2));
        Assert.Empty(resampler.ProcessAvailable(new byte[16], 0));
    }

    private static byte[] BuildConstantStereoPcm16(int frames, short value)
    {
        var pcm = new byte[frames * 2 * sizeof(short)];
        for (int i = 0; i < frames; i++)
        {
            WriteLe(pcm, (i * 2 + 0) * sizeof(short), value);
            WriteLe(pcm, (i * 2 + 1) * sizeof(short), value);
        }
        return pcm;
    }

    private static byte[] BuildConstantMonoPcm16(int frames, short value)
    {
        var pcm = new byte[frames * sizeof(short)];
        for (int i = 0; i < frames; i++)
            WriteLe(pcm, i * sizeof(short), value);
        return pcm;
    }

    private static void WriteLe(byte[] dest, int offset, short value)
    {
        dest[offset] = (byte)(value & 0xFF);
        dest[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
