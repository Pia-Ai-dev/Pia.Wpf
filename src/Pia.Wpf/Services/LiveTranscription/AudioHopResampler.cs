using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Device-free, unit-testable core of the loopback audio path: given raw PCM bytes in some source
/// <see cref="WaveFormat"/>, downmixes to mono and resamples to 16 kHz Float32, then yields fixed
/// ~50 ms hops (800 samples each) suitable for the live-transcription pipeline.
///
/// <para>This is the same NAudio chain <see cref="LoopbackAudioCaptureService"/> wires inline
/// (<c>BufferedWaveProvider</c> → <c>ToMono()</c> → <c>WdlResamplingSampleProvider</c>); it is
/// extracted here so the new <see cref="ProcessLoopbackAudioCaptureService"/> can reuse it and so the
/// resample/downmix/hop behaviour can be verified by a unit test without a real audio device.</para>
///
/// <para>It is <b>stateful</b> (streaming resamplers must carry state across buffer boundaries) but
/// device-free: construct it with the source format and feed buffers as they arrive. Not
/// thread-safe — drive it from a single producer, exactly as the WASAPI <c>DataAvailable</c> /
/// sample-ready callback does.</para>
/// </summary>
public sealed class AudioHopResampler
{
    /// <summary>Target sample rate of the live-transcription pipeline.</summary>
    public const int TargetSampleRate = 16000;

    /// <summary>Samples per hop (~50 ms at 16 kHz), matching the mic/loopback cadence.</summary>
    public const int SamplesPerHop = TargetSampleRate / 20;

    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _resampledMono;
    private readonly float[] _readBuffer;

    public AudioHopResampler(WaveFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        _buffer = new BufferedWaveProvider(sourceFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            ReadFully = false,
        };

        ISampleProvider sourceSamples = _buffer.ToSampleProvider();
        if (sourceFormat.Channels > 1)
            sourceSamples = sourceSamples.ToMono();

        _resampledMono = sourceFormat.SampleRate == TargetSampleRate
            ? sourceSamples
            : new WdlResamplingSampleProvider(sourceSamples, TargetSampleRate);

        _readBuffer = new float[SamplesPerHop];
    }

    /// <summary>
    /// Pushes one source buffer (raw bytes in the constructor's <see cref="WaveFormat"/>) into the
    /// chain and drains everything currently available from the resampler as fixed-size hops.
    /// Mirrors the drain loop in <see cref="LoopbackAudioCaptureService.OnDataAvailable"/>: each
    /// returned array is a freshly-allocated copy safe to hand off to the channel. A final, short
    /// (&lt; <see cref="SamplesPerHop"/>) hop may be returned when the resampler has no more to give.
    /// </summary>
    public IEnumerable<float[]> ProcessAvailable(byte[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) yield break;

        _buffer.AddSamples(buffer, 0, count);

        while (true)
        {
            int read = _resampledMono.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0) break;

            var hop = new float[read];
            Array.Copy(_readBuffer, hop, read);
            yield return hop;

            // A short read means the resampler has drained everything currently buffered.
            if (read < _readBuffer.Length) break;
        }
    }
}
