using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Pipes audio from an <see cref="IAudioCaptureSource"/> through a <see cref="SherpaOnnxVadDetector"/>
/// and forwards every speech segment to a shared <see cref="ITranscriptionEngine"/>. When an
/// <see cref="ISpeakerIdentificationService"/> is supplied (loopback channel only), each
/// segment also gets a speaker label attached to the emitted <see cref="TranscriptUtterance"/>.
///
/// The engine and (optional) speaker-id service are owned by the caller (typically
/// <see cref="LiveMeetingService"/>) and shared across mic + loopback engine services for one
/// meeting. This service does not dispose them.
/// </summary>
public sealed class LiveTranscriptionEngineService : IAsyncDisposable
{
    // Speaker embeddings need ~1.5 s of phonetic content to be stable; shorter segments
    // (throat clears, single-word "äh"s) produce noisy vectors that poison the centroid pool.
    private const int MinDiarizationSamples = 16000 * 3 / 2; // 1.5 s at 16 kHz

    private readonly TranscriptSpeaker _speaker;
    private readonly IAudioCaptureSource _source;
    private readonly SherpaOnnxVadDetector _vad;
    private readonly ITranscriptionEngine _engine;
    private readonly ISpeakerIdentificationService? _speakerId;
    private readonly ChannelWriter<TranscriptUtterance> _sink;
    private readonly ILogger _logger;

    private readonly Channel<float[]> _segmentQueue;
    private Task? _readerLoop;
    private Task? _segmentLoop;
    private CancellationTokenSource? _readerCts;
    private CancellationTokenSource? _segmentCts;

    public LiveTranscriptionEngineService(
        TranscriptSpeaker speaker,
        IAudioCaptureSource source,
        ITranscriptionEngine engine,
        string vadModelPath,
        ChannelWriter<TranscriptUtterance> sink,
        ILogger logger,
        ISpeakerIdentificationService? speakerId = null)
    {
        _speaker = speaker;
        _source = source;
        _engine = engine;
        _speakerId = speakerId;
        _sink = sink;
        _logger = logger;

        _logger.LogInformation("Engine init: speaker={Speaker} diarization={Diarization}",
            speaker, speakerId is not null);

        _vad = new SherpaOnnxVadDetector(vadModelPath, logger);
        _vad.OnSegment += EnqueueSegmentForTranscription;
        _vad.OnSpeechStarted += OnVadSpeechStarted;
        _vad.OnSpeechEnded += OnVadSpeechEnded;

        _segmentQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_readerCts is not null) throw new InvalidOperationException("Engine already started");
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _segmentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _readerLoop = Task.Factory.StartNew(
            () => RunReaderLoopAsync(_readerCts.Token),
            TaskCreationOptions.LongRunning).Unwrap();

        _segmentLoop = Task.Run(() => RunSegmentLoopAsync(_segmentCts.Token));

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
        if (_segmentQueue.Writer.TryWrite(samples))
            _logger.LogDebug("Segment queued for {Speaker}: {Samples} samples", _speaker, samples.Length);
        else
            _logger.LogWarning("Dropped a segment from {Speaker} pipeline — transcription is falling behind", _speaker);
    }

    /// <summary>
    /// Raised on the audio reader-loop thread when VAD detects start/end of speech.
    /// Subscribers MUST marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler<bool>? IsSpeakingChanged;

    private void OnVadSpeechStarted() => RaiseSpeakingChanged(true);
    private void OnVadSpeechEnded() => RaiseSpeakingChanged(false);

    private void RaiseSpeakingChanged(bool isSpeaking)
    {
        try { IsSpeakingChanged?.Invoke(this, isSpeaking); }
        catch (Exception ex) { _logger.LogError(ex, "IsSpeakingChanged subscriber threw for {Speaker}", _speaker); }
    }

    public TranscriptSpeaker Speaker => _speaker;

    private async Task TranscribeSegmentAsync(float[] samples, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("Engine start: {Speaker} {Samples} samples", _speaker, samples.Length);
        try
        {
            var text = await _engine.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Engine produced empty result for {Speaker} ({Ms}ms)", _speaker, sw.ElapsedMilliseconds);
                return;
            }

            _logger.LogDebug(
                "Engine done: {Speaker} {Ms}ms text='{Text}' (len={Len})",
                _speaker, sw.ElapsedMilliseconds, Truncate(text, 60), text.Length);

            string? speakerLabel = null;
            if (_speakerId is not null)
            {
                if (samples.Length >= MinDiarizationSamples)
                {
                    try { speakerLabel = _speakerId.IdentifyOrRegister(samples, 16000); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Speaker identification failed for {Speaker}", _speaker); }
                }
                else
                {
                    _logger.LogDebug(
                        "Skipping diarization for {Speaker}: segment too short ({Ms} ms)",
                        _speaker, samples.Length * 1000 / 16000);
                }
            }

            var utt = new TranscriptUtterance(_speaker, text, DateTimeOffset.Now, speakerLabel);
            await _sink.WriteAsync(utt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engine segment transcription failed for {Speaker}", _speaker);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 1. Stop accepting new audio: cancel the reader, which will drain the VAD,
        //    enqueue any trailing segment, and complete the segment-queue writer.
        try { _readerCts?.Cancel(); } catch { /* ignore */ }
        try { if (_readerLoop is not null) await _readerLoop.ConfigureAwait(false); }
        catch { /* swallow on shutdown */ }

        // 2. Wait for the segment loop to finish processing whatever is left in the
        //    queue (it observes writer-completion via ReadAllAsync). We do not cancel
        //    its token — the writer being completed is what stops the loop.
        try { if (_segmentLoop is not null) await _segmentLoop.ConfigureAwait(false); }
        catch { /* swallow on shutdown */ }

        _vad.OnSegment -= EnqueueSegmentForTranscription;
        _vad.OnSpeechStarted -= OnVadSpeechStarted;
        _vad.OnSpeechEnded -= OnVadSpeechEnded;
        _vad.Dispose();
        _readerCts?.Dispose();
        _segmentCts?.Dispose();
        // _engine is owned by the caller — do not dispose here.
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max) + "…";
}
