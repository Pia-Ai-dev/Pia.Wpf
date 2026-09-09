using System.IO;
using System.Text;

namespace Pia.Services.Tts;

/// <summary>
/// sherpa returns float samples; the on-disk filler cache and NAudio's <c>WaveFileReader</c> both want
/// a container, so the header goes on here rather than adding a second playback path.
/// </summary>
internal static class WavWriter
{
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    public static byte[] FromSamples(ReadOnlySpan<float> samples, int sampleRate)
    {
        var dataBytes = samples.Length * sizeof(short);

        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        // u8 literals, not strings: BinaryWriter.Write(string) would length-prefix each tag.
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)Channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * Channels * BitsPerSample / 8);
        writer.Write((short)(Channels * BitsPerSample / 8));
        writer.Write((short)BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
            writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));

        writer.Flush();
        return stream.ToArray();
    }
}
