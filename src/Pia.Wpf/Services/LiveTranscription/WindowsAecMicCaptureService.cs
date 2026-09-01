using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Dmo;
using NAudio.Wave;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// The microphone as Windows' own voice-capture stack hears it: the Voice Capture DSP in source mode
/// opens the default capture and render endpoints itself and subtracts what the speakers are playing,
/// so the far end never comes back in as local speech. Native output is 16 kHz mono 16-bit PCM, which
/// is what the rest of the pipeline wants anyway.
/// </summary>
public sealed class WindowsAecMicCaptureService : IAudioCaptureSource
{
    private const int TargetSampleRate = 16000;
    private const int ChannelCapacity = 50;

    /// <summary>Half a second of 16-bit mono — comfortably more than one <c>ProcessOutput</c> yields.</summary>
    private const int OutputBufferBytes = TargetSampleRate;

    private const int PollIntervalMs = 10;

    /// <summary>How long the first buffer may take before the caller should fall back to a plain mic.</summary>
    private static readonly TimeSpan FirstBufferTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<WindowsAecMicCaptureService> _logger;
    private readonly Channel<float[]> _channel;

    private Thread? _pump;
    private CancellationTokenSource? _cts;
    private IWavePlayer? _renderKeepAlive;
    private DateTimeOffset? _startedAt;
    private long _droppedFrames;
    private volatile bool _running;

    public int SampleRate => TargetSampleRate;
    public bool IsRunning => _running;
    public ChannelReader<float[]> Reader => _channel.Reader;
    public DateTimeOffset? StartedAt => _startedAt;

    public WindowsAecMicCaptureService(ILogger<WindowsAecMicCaptureService> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>
    /// Throws when the DSP is unavailable or silent, so the caller can fall back. Returns only once the
    /// first buffer has actually arrived — a DSP that starts but never produces is the failure mode that
    /// would otherwise cost a whole meeting.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running) throw new InvalidOperationException("Voice capture DSP already running");

        StartRenderKeepAlive();

        var firstBuffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _pump = new Thread(() => PumpLoop(firstBuffer, token))
        {
            IsBackground = true,
            Name = "Pia.VoiceCaptureDsp",
        };
        _pump.SetApartmentState(ApartmentState.MTA);
        _running = true;
        _pump.Start();

        try
        {
            await firstBuffer.Task.WaitAsync(FirstBufferTimeout, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Echo-cancelling mic capture started at {Rate} Hz mono", TargetSampleRate);
        }
        catch (Exception ex)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("The Windows voice capture DSP produced no audio.", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        try { _cts?.Cancel(); } catch { /* already disposed */ }

        if (_renderKeepAlive is not null)
        {
            try { _renderKeepAlive.Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Render keep-alive stop threw"); }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The DSP produces nothing while the render endpoint has no active stream, so a session with no
    /// call playing would capture silence. Holding an inaudible stream open removes that dependency.
    /// </summary>
    private void StartRenderKeepAlive()
    {
        try
        {
            var player = new WasapiOut();
            player.Init(new SilenceProvider(new WaveFormat(TargetSampleRate, 16, 1)));
            player.Play();
            _renderKeepAlive = player;
        }
        catch (Exception ex)
        {
            // Not fatal on its own: with a meeting already playing, the DSP has its reference anyway.
            _logger.LogWarning(ex, "Could not open a silent render stream for the echo canceller");
        }
    }

    private void PumpLoop(TaskCompletionSource firstBuffer, CancellationToken token)
    {
        object? dspObject = null;
        DmoOutputDataBuffer[]? buffers = null;
        IMediaObject? dsp = null;
        var allocated = false;

        try
        {
            dspObject = VoiceCaptureDsp.Create();
            dsp = (IMediaObject)dspObject;
            Configure((IPropertyStore)dspObject);
            SetOutputFormat(dsp);

            Check(dsp.AllocateStreamingResources(), nameof(IMediaObject.AllocateStreamingResources));
            allocated = true;

            buffers = [new DmoOutputDataBuffer(OutputBufferBytes)];
            var pcm = new byte[OutputBufferBytes];

            while (!token.IsCancellationRequested)
            {
                // Reset first: the DSP only calls SetLength when it wrote something, so a stale length
                // from the previous round would be re-published as duplicate audio.
                buffers[0].MediaBuffer.SetLength(0);

                Check(dsp.ProcessOutput(0, 1, buffers, out _), nameof(IMediaObject.ProcessOutput));

                var produced = buffers[0].Length;
                if (produced > 0)
                {
                    buffers[0].RetrieveData(pcm, 0);
                    Publish(PcmConversion.Pcm16LeToFloat(pcm.AsSpan(0, produced)));
                    firstBuffer.TrySetResult();
                }

                if (!buffers[0].MoreDataAvailable) Thread.Sleep(PollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            if (!firstBuffer.TrySetException(ex))
                _logger.LogError(ex, "Voice capture DSP pump failed");
        }
        finally
        {
            if (buffers is not null)
            {
                try { buffers[0].Dispose(); } catch { /* nothing left to salvage */ }
            }

            if (dsp is not null && allocated)
            {
                try { dsp.FreeStreamingResources(); } catch { /* nothing left to salvage */ }
            }

            if (dspObject is not null) Marshal.FinalReleaseComObject(dspObject);

            _channel.Writer.TryComplete();
            _logger.LogInformation("Echo-cancelling mic capture stopped: droppedFrames={Dropped}", _droppedFrames);
        }
    }

    private static void Configure(IPropertyStore properties)
    {
        var sourceMode = VoiceCaptureDsp.SourceMode;
        var sourceModeValue = PropVariant.FromBool(true);
        Check(properties.SetValue(ref sourceMode, ref sourceModeValue), "SetValue(DMO_SOURCE_MODE)");

        // The one property the DSP requires. Device selection is left at its (-1, -1) default, which is
        // the default capture and render endpoint — the same render endpoint the loopback side records.
        var systemMode = VoiceCaptureDsp.SystemMode;
        var systemModeValue = PropVariant.FromInt32(VoiceCaptureDsp.SingleChannelAec);
        Check(properties.SetValue(ref systemMode, ref systemModeValue), "SetValue(SYSTEM_MODE)");
    }

    private static void SetOutputFormat(IMediaObject dsp)
    {
        var waveFormat = new WaveFormat(TargetSampleRate, 16, 1);
        var formatSize = Marshal.SizeOf(waveFormat);
        var formatBlock = Marshal.AllocCoTaskMem(formatSize);
        try
        {
            Marshal.StructureToPtr(waveFormat, formatBlock, false);

            var mediaType = new DspMediaType
            {
                MajorType = VoiceCaptureDsp.MediaTypeAudio,
                SubType = VoiceCaptureDsp.MediaSubTypePcm,
                FixedSizeSamples = true,
                TemporalCompression = false,
                SampleSize = 0,
                FormatType = VoiceCaptureDsp.FormatWaveFormatEx,
                FormatSize = formatSize,
                Format = formatBlock,
            };

            Check(dsp.SetOutputType(0, ref mediaType, 0), nameof(IMediaObject.SetOutputType));
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatBlock);
        }
    }

    private void Publish(float[] samples)
    {
        _startedAt ??= DateTimeOffset.Now.AddSeconds(-samples.Length / (double)TargetSampleRate);

        if (_channel.Writer.TryWrite(samples)) return;

        _droppedFrames++;
        if (_droppedFrames % 50 == 1)
            _logger.LogWarning("Echo-cancelling mic source dropped frames (total: {Count})", _droppedFrames);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"Voice capture DSP {what} failed.", hr);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            _pump.Join(TimeSpan.FromSeconds(2));
            _pump = null;
        }

        _renderKeepAlive?.Dispose();
        _renderKeepAlive = null;

        _cts?.Dispose();
        _cts = null;

        _channel.Writer.TryComplete();
    }
}
