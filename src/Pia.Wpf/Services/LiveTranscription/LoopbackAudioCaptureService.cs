using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Captures the default render device's mix (system audio "what other apps are playing")
/// via WASAPI loopback, downmixes to mono, and resamples to 16 kHz Float32.
/// </summary>
public sealed class LoopbackAudioCaptureService : IAudioCaptureSource
{
    private const int TargetSampleRate = 16000;
    private const int ChannelCapacity = 50;

    private readonly ILogger<LoopbackAudioCaptureService> _logger;
    private readonly Channel<float[]> _channel;

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _resampledMono;
    private float[]? _readBuffer;
    private int _samplesPerHop;
    private long _droppedFrames;

    public int SampleRate => TargetSampleRate;
    public bool IsRunning => _capture is not null;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public LoopbackAudioCaptureService(ILogger<LoopbackAudioCaptureService> logger)
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
        if (IsRunning) throw new InvalidOperationException("Loopback already running");

        _capture = new WasapiLoopbackCapture();
        var sourceFormat = _capture.WaveFormat;
        _logger.LogInformation(
            "Loopback source format: {Rate} Hz, {Channels} ch, {Encoding}",
            sourceFormat.SampleRate, sourceFormat.Channels, sourceFormat.Encoding);

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

        // Pull ~50 ms hops from the resampler to match the mic source cadence.
        _samplesPerHop = TargetSampleRate / 20;
        _readBuffer = new float[_samplesPerHop];

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is null) return Task.CompletedTask;
        try
        {
            _capture.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loopback StopRecording threw");
        }
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_buffer is null || _resampledMono is null || _readBuffer is null) return;

        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Drain everything currently available from the resampler in fixed hops.
        while (true)
        {
            int read = _resampledMono.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0) break;

            var hop = new float[read];
            Array.Copy(_readBuffer, hop, read);
            if (!_channel.Writer.TryWrite(hop))
            {
                _droppedFrames++;
                if (_droppedFrames % 50 == 1)
                    _logger.LogWarning("Loopback source dropped frames (total: {Count})", _droppedFrames);
            }

            if (read < _readBuffer.Length) break;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception, "Loopback capture stopped with error");

        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        _buffer = null;
        _resampledMono = null;
        _readBuffer = null;
        _channel.Writer.TryComplete();
    }
}
