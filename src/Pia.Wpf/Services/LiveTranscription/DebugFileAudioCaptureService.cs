#if DEBUG
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Dev-only <see cref="IAudioCaptureSource"/> that decodes a recorded meeting file (audio or video —
/// Media Foundation extracts the audio track either way) instead of a live mic/loopback device, so
/// Direct Transcription and Meeting Attendee can be exercised against a recording. Wired only from a
/// DEBUG-gated env-var branch in <c>Bootstrapper</c>; never referenced from a Release build.
/// </summary>
public sealed class DebugFileAudioCaptureService : IAudioCaptureSource
{
    private const int ChannelCapacity = 50;
    private const int HopDurationMs = 1000 * AudioHopResampler.SamplesPerHop / AudioHopResampler.TargetSampleRate;

    private readonly string _filePath;
    private readonly ILogger<DebugFileAudioCaptureService> _logger;
    private readonly Channel<float[]> _channel;

    private MediaFoundationReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;
    private volatile bool _isRunning;

    public int SampleRate => AudioHopResampler.TargetSampleRate;
    public bool IsRunning => _isRunning;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public DebugFileAudioCaptureService(string filePath, ILogger<DebugFileAudioCaptureService> logger)
    {
        _filePath = filePath;
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
        if (IsRunning) throw new InvalidOperationException("Debug file audio source already running");

        _reader = new MediaFoundationReader(_filePath);
        var resampler = new AudioHopResampler(_reader.WaveFormat);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        _logger.LogInformation("Debug file audio source playing {Path}", _filePath);
        _playbackTask = Task.Run(() => PlaybackLoopAsync(resampler, _cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task PlaybackLoopAsync(AudioHopResampler resampler, CancellationToken token)
    {
        var buffer = new byte[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = _reader!.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0) break; // EOF — mirrors a meeting recording ending on its own.

                foreach (var hop in resampler.ProcessAvailable(buffer, bytesRead))
                {
                    if (token.IsCancellationRequested) return;
                    if (!_channel.Writer.TryWrite(hop))
                        _logger.LogWarning("Debug file audio source dropped a hop (channel full)");

                    await Task.Delay(HopDurationMs, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync/DisposeAsync requested — not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debug file audio source playback failed for {Path}", _filePath);
        }
        finally
        {
            _isRunning = false;
            _channel.Writer.TryComplete();
            _logger.LogInformation("Debug file audio source finished playing {Path}", _filePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_playbackTask is not null)
        {
            try { await _playbackTask.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Debug file audio source playback task threw during dispose"); }
        }
        _reader?.Dispose();
        _reader = null;
        _cts?.Dispose();
        _cts = null;
        _channel.Writer.TryComplete();
    }
}
#endif
