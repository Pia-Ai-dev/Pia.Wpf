using System.IO;
using System.Media;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Two-tone rising chime, synthesized once into an in-memory WAV rather than shipped as an asset.
///
/// <para>Direct transcription's loopback source captures the default render device, so this tone comes
/// straight back in as far-end audio. It is deliberately short: at 180 ms it stays well under the
/// 1.5 s a segment needs before the diarizer will embed it, so it can never mint a speaker, and any
/// text an engine hallucinates from it arrives with no label and is dropped by the consent gate.</para>
/// </summary>
public sealed class ConsentSoundPlayer : IConsentSoundPlayer, IDisposable
{
    private const int SampleRate = 44100;
    private const double ToneSeconds = 0.09;
    private const double FadeSeconds = 0.006;
    private const double Amplitude = 0.22;
    private static readonly double[] ToneHz = [880.0, 1318.5];

    private readonly ILogger<ConsentSoundPlayer> _logger;
    private readonly Lazy<SoundPlayer?> _player;

    public ConsentSoundPlayer(ILogger<ConsentSoundPlayer> logger)
    {
        _logger = logger;
        _player = new Lazy<SoundPlayer?>(CreatePlayer);
    }

    public void PlayConsentGranted()
    {
        try
        {
            _player.Value?.Play();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play the consent confirmation tone");
        }
    }

    private SoundPlayer? CreatePlayer()
    {
        try
        {
            // Loaded up front so the first grant of a session does not pay for the decode.
            var player = new SoundPlayer(new MemoryStream(BuildWav()));
            player.Load();
            return player;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare the consent confirmation tone");
            return null;
        }
    }

    private static byte[] BuildWav()
    {
        var perTone = (int)(ToneSeconds * SampleRate);
        var samples = new short[perTone * ToneHz.Length];

        for (var t = 0; t < ToneHz.Length; t++)
        {
            var step = 2.0 * Math.PI * ToneHz[t] / SampleRate;
            for (var i = 0; i < perTone; i++)
                samples[t * perTone + i] = (short)(Math.Sin(step * i) * Envelope(i, perTone) * Amplitude * short.MaxValue);
        }

        return WrapPcm16Mono(samples);
    }

    /// <summary>Linear fade at both ends of a tone — a hard start or stop on a sine is an audible click.</summary>
    private static double Envelope(int index, int length)
    {
        var fade = Math.Max(1, (int)(FadeSeconds * SampleRate));
        if (index < fade) return (double)index / fade;
        var fromEnd = length - 1 - index;
        return fromEnd < fade ? (double)fromEnd / fade : 1.0;
    }

    private static byte[] WrapPcm16Mono(short[] samples)
    {
        var dataBytes = samples.Length * sizeof(short);
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                            // PCM chunk size
        writer.Write((short)1);                      // PCM
        writer.Write((short)1);                      // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));    // byte rate
        writer.Write((short)sizeof(short));          // block align
        writer.Write((short)16);                     // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
            writer.Write(sample);

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (_player.IsValueCreated) _player.Value?.Dispose();
    }
}
