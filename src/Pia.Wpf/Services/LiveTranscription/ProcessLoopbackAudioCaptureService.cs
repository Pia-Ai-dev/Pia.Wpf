using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using static Pia.Services.LiveTranscription.ProcessLoopbackInterop;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Per-process WASAPI loopback capture isolated to a single target process (the attendee's browser)
/// and its child process tree, via <c>ActivateAudioInterfaceAsync</c> +
/// <c>AUDIOCLIENT_ACTIVATION_PARAMS</c> (<c>PROCESS_LOOPBACK</c> /
/// <c>INCLUDE_TARGET_PROCESS_TREE</c>). Unlike <see cref="LoopbackAudioCaptureService"/> it isolates
/// only the browser's audio (not the whole render-device mix).
///
/// <para><b>RETIRED / NOT SELECTED.</b> This was intended as the silent source, but per-process
/// loopback is only a capture <i>tap</i>: the browser still renders the meeting to the default
/// speakers while it is captured (confirmed in the field — the meeting was audible on the hidden
/// path). It does NOT silence output, so the silent path now uses the in-browser Web Audio tap
/// (<c>BrowserAudioCaptureSource</c>). This class is kept for reference and is no longer wired into
/// the audio-source selection. See <c>BrowserAudioCaptureService</c> for the live silent source.</para>
///
/// <para>The capture format is requested as 48 kHz stereo 16-bit PCM (the activated client converts
/// for us via <c>AUTOCONVERTPCM</c>); the shared <see cref="AudioHopResampler"/> then downmixes to
/// mono and resamples to 16 kHz Float32, yielding the same ~50 ms hops the rest of the pipeline
/// expects — identical to the endpoint service's chain.</para>
///
/// <para><b>UNVERIFIED / NON-DEFAULT:</b> selected when the meeting browser window is hidden
/// (<c>!AppSettings.MeetingAttendeeShowBrowserWindow</c>) and the browser PID is known. Requires
/// Windows 10 build 20348+ (guarded at <see cref="StartAsync"/>); on failure the orchestrator degrades
/// to the audible endpoint loopback. The interop is correct-by-construction against the Microsoft
/// ApplicationLoopback sample but cannot be run-verified in this environment (no live target render
/// stream). See <see cref="ProcessLoopbackInterop"/>.</para>
/// </summary>
public sealed class ProcessLoopbackAudioCaptureService : IAudioCaptureSource
{
    private const int TargetSampleRate = 16000;
    private const int ChannelCapacity = 50;

    // Format we ask the activated client to deliver; AudioHopResampler converts to 16 kHz mono.
    private const int CaptureSampleRate = 48000;
    private const int CaptureChannels = 2;

    // hns (100 ns) units. 0 lets WASAPI pick the default buffer in shared mode.
    private const int ActivationTimeoutSeconds = 5;

    private readonly int _targetProcessId;
    private readonly ILogger<ProcessLoopbackAudioCaptureService> _logger;
    private readonly Channel<float[]> _channel;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private AudioHopResampler? _resampler;
    private EventWaitHandle? _bufferReady;
    private CancellationTokenSource? _captureCts;
    private Thread? _captureThread;

    private long _droppedFrames;
    private long _hopCount;
    private bool _firstFrameLogged;
    private DateTimeOffset? _startedAt;

    public int SampleRate => TargetSampleRate;
    public bool IsRunning => _captureThread is not null;
    public ChannelReader<float[]> Reader => _channel.Reader;
    public DateTimeOffset? StartedAt => _startedAt;

    public ProcessLoopbackAudioCaptureService(int targetProcessId, ILogger<ProcessLoopbackAudioCaptureService> logger)
    {
        if (targetProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetProcessId), "A positive target process id is required.");

        _targetProcessId = targetProcessId;
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
        if (IsRunning) throw new InvalidOperationException("Process loopback already running");

        if (!IsSupportedOnThisWindows())
        {
            throw new PlatformNotSupportedException(
                "Per-process WASAPI loopback requires Windows 10 build 20348 or later. " +
                "Use the endpoint loopback source on older Windows.");
        }

        var captureFormat = CreatePcm16Format(CaptureSampleRate, CaptureChannels);
        ActivateProcessLoopbackClient(ref captureFormat);

        _resampler = new AudioHopResampler(new WaveFormat(CaptureSampleRate, 16, CaptureChannels));
        _bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        _audioClient!.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());

        var serviceGuid = IID_IAudioCaptureClient;
        _captureClient = (IAudioCaptureClient)_audioClient.GetService(ref serviceGuid);

        _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioClient.Start();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ProcessLoopbackCapture",
        };
        _captureThread.Start();

        _logger.LogInformation(
            "Process loopback started for pid {Pid}: {Rate} Hz {Channels} ch capture -> 16 kHz mono",
            _targetProcessId, CaptureSampleRate, CaptureChannels);
        return Task.CompletedTask;
    }

    private void ActivateProcessLoopbackClient(ref WAVEFORMATEX captureFormat)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.ProcessLoopback,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)_targetProcessId,
                ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.IncludeTargetProcessTree,
            },
        };

        // PROPVARIANT { vt = VT_BLOB; blob.cbSize; blob.pBlobData = &activationParams }.
        // Built by hand because the params blob must outlive the async activation call.
        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new PROPVARIANT
            {
                vt = VT_BLOB,
                blobSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                blobData = paramsPtr,
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new ActivationCompletionHandler();
            var riid = IID_IAudioClient;
            ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopbackPath,
                ref riid,
                propVariantPtr,
                handler,
                out _);

            if (!handler.Wait(TimeSpan.FromSeconds(ActivationTimeoutSeconds)) || handler.Operation is null)
                throw new TimeoutException("Process loopback activation did not complete in time.");

            handler.Operation.GetActivateResult(out int activateResult, out object? activatedInterface);
            Marshal.ThrowExceptionForHR(activateResult);

            _audioClient = (IAudioClient)(activatedInterface
                ?? throw new InvalidOperationException("Process loopback activation returned no interface."));

            var sessionGuid = Guid.Empty;
            _audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                hnsBufferDuration: 0,
                hnsPeriodicity: 0,
                ref captureFormat,
                ref sessionGuid);
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private void CaptureLoop()
    {
        var token = _captureCts?.Token ?? CancellationToken.None;
        int bytesPerFrame = CaptureChannels * sizeof(short);

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Wait for the next buffer-ready signal (or wake periodically to honour cancellation).
                if (_bufferReady is null || !_bufferReady.WaitOne(TimeSpan.FromMilliseconds(200)))
                    continue;
                if (token.IsCancellationRequested) break;
                if (_captureClient is null || _resampler is null) break;

                _captureClient.GetNextPacketSize(out uint packetFrames);
                while (packetFrames > 0)
                {
                    _captureClient.GetBuffer(
                        out IntPtr dataPtr,
                        out uint framesAvailable,
                        out uint flags,
                        out _,
                        out _);

                    int byteCount = checked((int)framesAvailable * bytesPerFrame);
                    var pcm = new byte[byteCount];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && dataPtr != IntPtr.Zero && byteCount > 0)
                        Marshal.Copy(dataPtr, pcm, 0, byteCount);
                    // else: leave the buffer zeroed (silent packet) — still advance timing.

                    _captureClient.ReleaseBuffer(framesAvailable);

                    if (!_firstFrameLogged)
                    {
                        _firstFrameLogged = true;
                        _logger.LogInformation(
                            "Process loopback first packet: {Frames} frames from pid {Pid}",
                            framesAvailable, _targetProcessId);
                    }

                    foreach (var hop in _resampler.ProcessAvailable(pcm, byteCount))
                        PublishHop(hop);

                    _captureClient.GetNextPacketSize(out packetFrames);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Process loopback capture loop failed for pid {Pid}", _targetProcessId);
        }
        finally
        {
            _channel.Writer.TryComplete();
            _logger.LogInformation(
                "Process loopback capture stopped: totalHops={Hops} droppedFrames={Dropped}",
                _hopCount, _droppedFrames);
        }
    }

    private void PublishHop(float[] hop)
    {
        _hopCount++;
        _startedAt ??= DateTimeOffset.Now.AddSeconds(-hop.Length / (double)TargetSampleRate);
        if (!_channel.Writer.TryWrite(hop))
        {
            _droppedFrames++;
            if (_droppedFrames % 50 == 1)
                _logger.LogWarning("Process loopback source dropped frames (total: {Count})", _droppedFrames);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return Task.CompletedTask;
        try
        {
            _captureCts?.Cancel();
            _audioClient?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process loopback Stop threw");
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        var thread = _captureThread;
        _captureThread = null;
        if (thread is not null && thread.IsAlive)
            thread.Join(TimeSpan.FromSeconds(2));

        try { _captureCts?.Dispose(); } catch { /* ignore */ }
        _captureCts = null;

        if (_captureClient is not null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }
        if (_audioClient is not null)
        {
            Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }

        _bufferReady?.Dispose();
        _bufferReady = null;
        _resampler = null;
        _channel.Writer.TryComplete();
    }
}
