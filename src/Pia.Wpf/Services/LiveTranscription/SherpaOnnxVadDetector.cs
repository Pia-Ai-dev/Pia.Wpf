using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Voice-activity gate around sherpa-onnx's bundled Silero VAD. Public surface
/// (<see cref="Process"/> / <see cref="Drain"/> / <see cref="OnSegment"/>) mirrors
/// <see cref="SileroVadDetector"/> so the engine pipeline is unaware of which detector
/// is wired up.
///
/// Sherpa's <see cref="VoiceActivityDetector.AcceptWaveform"/> requires exactly
/// <see cref="WindowSize"/> samples per call; this wrapper accumulates partial input into
/// a small leftover buffer and chunks before forwarding.
/// </summary>
public sealed class SherpaOnnxVadDetector : IDisposable
{
    private const int WindowSize = 512;          // 32 ms at 16 kHz — Silero's expected hop.
    private const int SampleRate = 16000;
    private const float SegmentBufferSeconds = 60f;

    private readonly VoiceActivityDetector _vad;
    private readonly ILogger _logger;

    // Leftover samples that didn't fill a 512-sample window on the last Process call.
    // Capped well above WindowSize so the only allocations are window arrays.
    private readonly float[] _pending = new float[WindowSize * 2];
    private int _pendingCount;

    public event Action<float[]>? OnSegment;

    public SherpaOnnxVadDetector(string modelPath, ILogger logger)
    {
        _logger = logger;

        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = 0.5f;
        // 0.3 s gap is short enough to split most turn changes in fast back-and-forth
        // dialogue (so a single segment doesn't contain two speakers) while still bridging
        // intra-utterance breath/disfluency pauses.
        config.SileroVad.MinSilenceDuration = 0.3f;
        config.SileroVad.MinSpeechDuration = 0.5f;
        config.SileroVad.WindowSize = WindowSize;
        config.SileroVad.MaxSpeechDuration = 20.0f; // matches the legacy 20 s flush cap
        config.SampleRate = SampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;

        _vad = new VoiceActivityDetector(config, SegmentBufferSeconds);

        _logger.LogInformation(
            "Sherpa-onnx VAD active. model='{Model}' threshold=0.5 minSpeech=0.5s minSilence=0.3s maxSpeech=20s",
            modelPath);
    }

    /// <summary>
    /// Feed a chunk of 16 kHz mono Float32 samples. Buffers + chunks into 512-sample windows
    /// for sherpa's VAD, drains any segments completed by the new audio, raising
    /// <see cref="OnSegment"/> for each.
    /// </summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        int srcIdx = 0;
        while (srcIdx < samples.Length)
        {
            int take = Math.Min(WindowSize - _pendingCount, samples.Length - srcIdx);
            samples.Slice(srcIdx, take).CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            srcIdx += take;

            if (_pendingCount == WindowSize)
            {
                // Sherpa may retain a reference; allocate a fresh array per window.
                var window = new float[WindowSize];
                Array.Copy(_pending, 0, window, 0, WindowSize);
                _vad.AcceptWaveform(window);
                _pendingCount = 0;
            }
        }

        DrainSegments();
    }

    /// <summary>
    /// Force-flush any in-progress segment so the user sees trailing speech when the source
    /// stops mid-utterance.
    /// </summary>
    public void Drain()
    {
        _vad.Flush();
        DrainSegments();
        _pendingCount = 0;
    }

    private void DrainSegments()
    {
        while (!_vad.IsEmpty())
        {
            var seg = _vad.Front();
            _vad.Pop();
            if (seg.Samples is { Length: > 0 })
            {
                _logger.LogDebug("VAD segment: {Samples} samples ({Ms} ms)",
                    seg.Samples.Length, seg.Samples.Length * 1000 / SampleRate);
                try { OnSegment?.Invoke(seg.Samples); }
                catch (Exception ex) { _logger.LogError(ex, "VAD OnSegment subscriber threw"); }
            }
        }
    }

    public void Dispose()
    {
        _vad.Dispose();
    }
}
