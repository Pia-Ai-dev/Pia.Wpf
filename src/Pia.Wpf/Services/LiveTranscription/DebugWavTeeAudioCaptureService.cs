#if DEBUG
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Pia.Logging;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Dev-only decorator that writes every hop it forwards to a WAV file, so the exact stream the
/// pipeline hears can be replayed later through <c>DebugFileAudioCaptureService</c>. The recordings
/// in <c>artifacts/</c> are cloud-mixed Teams audio; this is the only way to capture the device
/// loopback Pia actually consumes, AGC and all. Wired only from a DEBUG-gated env-var branch in
/// <c>Bootstrapper</c>; never referenced from a Release build.
///
/// Privacy: the most sensitive artifact the app can produce. DEBUG only, off unless the env var is
/// set, written where the operator asked, and never mentioned above Debug level.
/// </summary>
public sealed class DebugWavTeeAudioCaptureService : IAudioCaptureSource
{
    // Never drop: a dropped hop would put audio in the WAV that the pipeline never saw, or the
    // reverse, and the file's whole purpose is to be the same stream. A 32 KB local write is
    // microseconds, so waiting for room cannot realistically stall capture.
    private const int ChannelCapacity = 200;

    private readonly IAudioCaptureSource _inner;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Channel<float[]> _channel;
    private CancellationTokenSource? _cts;
    private WaveFileWriter? _writer;
    private Task? _pump;
    private bool _stopped;

    public int SampleRate => _inner.SampleRate;
    public bool IsRunning => _inner.IsRunning;
    public DateTimeOffset? StartedAt => _inner.StartedAt;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public DebugWavTeeAudioCaptureService(IAudioCaptureSource inner, string path, ILogger logger)
    {
        _inner = inner;
        _path = path;
        _logger = logger;
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // 16-bit PCM, not IEEE float: MediaFoundationReader reads it back without question.
        _writer = new WaveFileWriter(_path, new WaveFormat(16000, 16, 1));
        _logger.SensitiveDebug("Audio dump active, writing {Path}", _path);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            await foreach (var hop in _inner.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                _writer?.WriteSamples(hop, 0, hop.Length);
                await _channel.Writer.WriteAsync(hop, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stop requested */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio dump tee failed");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        return _inner.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            return;
        }
        _stopped = true;

        _cts?.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Audio dump pump threw during dispose"); }
        }
        // Flush and close the file BEFORE the inner source goes away: disposing the inner first can
        // complete its reader and leave the tail of the stream unwritten.
        if (_writer is not null)
        {
            try { await _writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Audio dump writer threw on close"); }
            _writer = null;
        }
        await _inner.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
        _channel.Writer.TryComplete();
    }
}
#endif
