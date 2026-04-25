using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Pia.Services.LiveTranscription;

public static class PcmConversion
{
    /// <summary>Converts a little-endian 16-bit PCM byte buffer to Float32 samples in [-1, 1].</summary>
    public static float[] Pcm16LeToFloat(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        var output = new float[sampleCount];
        var shorts = MemoryMarshal.Cast<byte, short>(pcm[..(sampleCount * 2)]);
        for (int i = 0; i < shorts.Length; i++)
            output[i] = shorts[i] / 32768f;
        return output;
    }
}

/// <summary>
/// Captures the system default microphone at 16 kHz mono 16-bit PCM via NAudio's
/// <see cref="WaveInEvent"/>, converts to Float32, and publishes to a bounded channel.
/// </summary>
public sealed class MicAudioCaptureService : IAudioCaptureSource
{
    private const int TargetSampleRate = 16000;
    private const int ChannelCapacity = 50;

    private readonly ILogger<MicAudioCaptureService> _logger;
    private readonly Channel<float[]> _channel;
    private WaveInEvent? _waveIn;
    private long _droppedFrames;

    public int SampleRate => TargetSampleRate;
    public bool IsRunning => _waveIn is not null;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public MicAudioCaptureService(ILogger<MicAudioCaptureService> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Mic capture already running");

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(TargetSampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            _waveIn.StartRecording();
            _logger.LogInformation("Mic capture started at {Rate} Hz mono", TargetSampleRate);
        }
        catch
        {
            _waveIn.Dispose();
            _waveIn = null;
            throw;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_waveIn is null) return Task.CompletedTask;
        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mic StopRecording threw");
        }
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded < 2) return;
        var samples = PcmConversion.Pcm16LeToFloat(e.Buffer.AsSpan(0, e.BytesRecorded));

        if (!_channel.Writer.TryWrite(samples))
        {
            // BoundedChannel dropped the oldest frame to make room for this one.
            _droppedFrames++;
            if (_droppedFrames % 50 == 1)
                _logger.LogWarning("Mic source dropped frames (total: {Count}) — VAD/engine cannot keep up", _droppedFrames);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception, "Mic capture stopped with error");

        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
        _channel.Writer.TryComplete();
    }
}
