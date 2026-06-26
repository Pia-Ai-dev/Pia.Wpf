using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Pia.Services.LiveTranscription;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// The <b>silent</b> audio source for the hidden meeting attendee: instead of capturing the system
/// render mix (audible <see cref="LoopbackAudioCaptureService"/>) or tapping the browser process with
/// WASAPI process loopback (which still plays to the speakers), it drives the in-page Web Audio tap
/// (<see cref="IMeetingSession.StartAudioCaptureAsync"/>). The page mutes the meeting from the
/// speakers and ships raw Float32 PCM here; this class downmixes/resamples it to the pipeline's
/// 16 kHz mono hops via the shared <see cref="AudioHopResampler"/> — identical downstream behaviour to
/// the other sources.
///
/// <para>If no audio arrives within <see cref="FirstAudioTimeout"/> of starting (the in-page hook
/// captured no remote track — e.g. Teams DOM/WebRTC drift, since this path cannot be run-verified in
/// CI), <see cref="StartAsync"/> throws so the orchestrator disposes this source (which unmutes the
/// meeting) and degrades to the audible endpoint loopback. That converts the worst case from
/// "silent and no transcript" back to today's "audible and transcribes".</para>
/// </summary>
public sealed class BrowserAudioCaptureService : IAudioCaptureSource
{
    private const int ChannelCapacity = 50;

    /// <summary>
    /// How long to wait for the first PCM frame after arming the in-page tap. The Web Audio graph
    /// starts pumping within ~100 ms of the first remote track connecting, and remote tracks exist as
    /// soon as the bot is admitted into a populated call, so this only elapses when capture genuinely
    /// fails. A meeting that is merely quiet still delivers (silent) PCM frames continuously.
    /// </summary>
    private static readonly TimeSpan FirstAudioTimeout = TimeSpan.FromSeconds(12);

    private readonly IMeetingSession _session;
    private readonly ILogger<BrowserAudioCaptureService> _logger;
    private readonly Channel<float[]> _channel;
    private readonly TaskCompletionSource _firstPcm = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AudioHopResampler? _resampler;
    private int _sourceSampleRate;
    private int _sourceChannels;
    private bool _started;
    private long _hopCount;
    private long _droppedFrames;
    private float _maxRmsInBatch;
    private bool _firstFrameLogged;

    public int SampleRate => AudioHopResampler.TargetSampleRate;
    public bool IsRunning => _started;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public BrowserAudioCaptureService(IMeetingSession session, ILogger<BrowserAudioCaptureService> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Browser audio capture already running");

        // Arm the in-page tap (exposes the PCM binding + starts the Web Audio graph). Throws on a hard
        // wiring failure, which the orchestrator degrades on.
        await _session.StartAudioCaptureAsync(OnFormat, OnPcm, cancellationToken).ConfigureAwait(false);

        // Wait for the first PCM frame, bounding the no-audio case so it degrades rather than running a
        // silent meeting forever.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(FirstAudioTimeout, linked.Token);
        var completed = await Task.WhenAny(_firstPcm.Task, timeout).ConfigureAwait(false);
        linked.Cancel(); // stop the pending timeout delay (its cancellation is not an error)

        // Running only if the first PCM actually arrived. _firstPcm can also complete via cancellation
        // (DisposeAsync racing a start), which must NOT be read as a healthy capture.
        if (completed == _firstPcm.Task && _firstPcm.Task.IsCompletedSuccessfully)
        {
            _started = true;
            _logger.LogInformation(
                "Browser audio capture running: source {Rate} Hz {Channels} ch -> {Target} Hz mono",
                _sourceSampleRate, _sourceChannels, AudioHopResampler.TargetSampleRate);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_firstPcm.Task.IsCanceled)
            throw new OperationCanceledException("Browser audio capture was disposed before any audio arrived.");
        throw new TimeoutException(
            $"No in-browser meeting audio within {FirstAudioTimeout.TotalSeconds:N0}s; "
            + "degrading to endpoint loopback.");
    }

    /// <summary>Invoked once (on the binding-dispatch thread) before any PCM, announcing its format.</summary>
    private void OnFormat(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0 || _resampler is not null) return;
        _sourceSampleRate = sampleRate;
        _sourceChannels = channels;
        _resampler = new AudioHopResampler(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        _logger.LogInformation("Browser audio format announced: {Rate} Hz {Channels} ch", sampleRate, channels);
    }

    /// <summary>Invoked repeatedly (on the binding-dispatch thread) with little-endian Float32 PCM bytes.</summary>
    private void OnPcm(byte[] pcm)
    {
        var resampler = _resampler;
        if (resampler is null || pcm.Length == 0) return;

        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            _logger.LogInformation("Browser audio first PCM frame: {Bytes} bytes", pcm.Length);
        }

        foreach (var hop in resampler.ProcessAvailable(pcm, pcm.Length))
            PublishHop(hop);

        _firstPcm.TrySetResult();
    }

    private void PublishHop(float[] hop)
    {
        var rms = ComputeRms(hop);
        if (rms > _maxRmsInBatch) _maxRmsInBatch = rms;
        _hopCount++;

        if (_hopCount % 100 == 0)
        {
            _logger.LogDebug(
                "Browser audio hops={Hops} maxRmsDb={Db:F1} droppedFrames={Dropped}",
                _hopCount, RmsToDb(_maxRmsInBatch), _droppedFrames);
            _maxRmsInBatch = 0f;
        }

        if (!_channel.Writer.TryWrite(hop))
        {
            _droppedFrames++;
            if (_droppedFrames % 50 == 1)
                _logger.LogWarning("Browser audio source dropped frames (total: {Count})", _droppedFrames);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _started = false;
        // Unmute the meeting and tear the in-page tap down. Best-effort: the session swallows failures
        // (the page may already be closing on a full teardown).
        await _session.StopAudioCaptureAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        // If StartAsync was still waiting on the first frame when we were disposed, release it.
        _firstPcm.TrySetCanceled();
        _resampler = null;
        _channel.Writer.TryComplete();
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
}
