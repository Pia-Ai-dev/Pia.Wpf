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
    private long _frameCount;
    private long _hopCount;
    private float _maxRmsInBatch;
    private bool _firstFrameLogged;

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

        LogDefaultRenderDevice();

        _capture = new WasapiLoopbackCapture();
        var sourceFormat = _capture.WaveFormat;
        _logger.LogInformation(
            "Loopback source format: {Rate} Hz, {Channels} ch, {Bits} bits, {Encoding}",
            sourceFormat.SampleRate, sourceFormat.Channels, sourceFormat.BitsPerSample, sourceFormat.Encoding);

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
        _frameCount++;

        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            _logger.LogInformation(
                "Loopback first frame received: {Bytes} bytes from render device",
                e.BytesRecorded);
        }

        // Drain everything currently available from the resampler in fixed hops.
        while (true)
        {
            int read = _resampledMono.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0) break;

            var hop = new float[read];
            Array.Copy(_readBuffer, hop, read);

            var rms = ComputeRms(hop);
            if (rms > _maxRmsInBatch) _maxRmsInBatch = rms;
            _hopCount++;

            if (_hopCount % 100 == 0)
            {
                _logger.LogDebug(
                    "Loopback hops={Hops} maxRmsDb={Db:F1} samplesPerHop={Samples}",
                    _hopCount, RmsToDb(_maxRmsInBatch), read);
                _maxRmsInBatch = 0f;
            }

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

        _logger.LogInformation(
            "Loopback capture stopped: totalFrames={Frames} totalHops={Hops} droppedFrames={Dropped}",
            _frameCount, _hopCount, _droppedFrames);

        _channel.Writer.TryComplete();
    }

    private void LogDefaultRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _logger.LogInformation(
                "Loopback selected default render device: '{Name}' state={State} id={Id}",
                device.FriendlyName, device.State, device.ID);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate default render device");
        }
    }

    private static float ComputeRms(float[] samples)
    {
        if (samples.Length == 0) return 0f;
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++) sumSq += samples[i] * samples[i];
        return (float)Math.Sqrt(sumSq / samples.Length);
    }

    private static float RmsToDb(float rms)
        => rms <= 1e-10f ? -200f : 20f * (float)Math.Log10(rms);

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
