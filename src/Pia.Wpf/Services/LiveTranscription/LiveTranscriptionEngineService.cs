using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Whisper.net;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Owns a single Whisper.net processor for the lifetime of a session and pipes audio from
/// an <see cref="IAudioCaptureSource"/> through a <see cref="SileroVadDetector"/>. Every
/// speech segment from the VAD is transcribed and the resulting <see cref="TranscriptUtterance"/>
/// is written to the supplied sink channel, tagged with the configured speaker.
/// </summary>
public sealed class LiveTranscriptionEngineService : IAsyncDisposable
{
    private readonly TranscriptSpeaker _speaker;
    private readonly IAudioCaptureSource _source;
    private readonly SileroVadDetector _vad;
    private readonly WhisperFactory _whisperFactory;
    private readonly WhisperProcessor _processor;
    private readonly ChannelWriter<TranscriptUtterance> _sink;
    private readonly ILogger _logger;

    private readonly Channel<float[]> _segmentQueue;
    private Task? _readerLoop;
    private Task? _segmentLoop;
    private CancellationTokenSource? _cts;

    public LiveTranscriptionEngineService(
        TranscriptSpeaker speaker,
        IAudioCaptureSource source,
        string sileroVadModelPath,
        string whisperGgmlPath,
        string languageCode,
        ChannelWriter<TranscriptUtterance> sink,
        ILogger logger)
    {
        _speaker = speaker;
        _source = source;
        _sink = sink;
        _logger = logger;

        _vad = new SileroVadDetector(sileroVadModelPath, logger);
        _vad.OnSegment += EnqueueSegmentForTranscription;

        _whisperFactory = WhisperFactory.FromPath(whisperGgmlPath);
        _processor = _whisperFactory.CreateBuilder()
            .WithLanguage(languageCode)
            .Build();

        _segmentQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) throw new InvalidOperationException("Engine already started");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _readerLoop = Task.Factory.StartNew(
            () => RunReaderLoopAsync(token),
            TaskCreationOptions.LongRunning).Unwrap();

        _segmentLoop = Task.Factory.StartNew(
            () => RunSegmentLoopAsync(token),
            TaskCreationOptions.LongRunning).Unwrap();

        return Task.CompletedTask;
    }

    private async Task RunReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _source.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _vad.Process(frame);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live transcription reader loop ({Speaker}) failed", _speaker);
        }
        finally
        {
            _vad.Drain();
            _segmentQueue.Writer.TryComplete();
        }
    }

    private async Task RunSegmentLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var samples in _segmentQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await TranscribeSegmentAsync(samples, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live transcription segment loop ({Speaker}) failed", _speaker);
        }
    }

    private void EnqueueSegmentForTranscription(float[] samples)
    {
        if (!_segmentQueue.Writer.TryWrite(samples))
            _logger.LogWarning("Dropped a segment from {Speaker} pipeline — transcription is falling behind", _speaker);
    }

    private async Task TranscribeSegmentAsync(float[] samples, CancellationToken cancellationToken)
    {
        try
        {
            var pieces = new List<string>();
            await foreach (var seg in _processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(seg.Text)) pieces.Add(seg.Text.Trim());
            }
            var text = string.Join(" ", pieces).Trim();
            if (text.Length == 0) return;

            var utt = new TranscriptUtterance(_speaker, text, DateTimeOffset.Now);
            await _sink.WriteAsync(utt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whisper segment transcription failed for {Speaker}", _speaker);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        try { if (_readerLoop is not null) await _readerLoop.ConfigureAwait(false); }
        catch { /* swallow on shutdown */ }
        try { if (_segmentLoop is not null) await _segmentLoop.ConfigureAwait(false); }
        catch { /* swallow on shutdown */ }

        _vad.OnSegment -= EnqueueSegmentForTranscription;
        _vad.Dispose();
        _processor.Dispose();
        _whisperFactory.Dispose();
        _cts?.Dispose();
    }
}
