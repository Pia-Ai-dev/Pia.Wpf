using Microsoft.Extensions.Logging;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Voice-activity gate for live meeting transcription. The class name is preserved for
/// engine compatibility, but the implementation is energy-based (RMS hysteresis), not
/// Silero ONNX.
///
/// Background: the Silero v5 ONNX path was implemented and debugged extensively. With both
/// <c>NamedOnnxValue</c>+<c>DenseTensor</c> and <c>FixedBufferOnnxValue.CreateFromMemory</c>
/// input bindings, the model returned <c>prob ≈ 0.0005</c> for every window regardless of
/// audio content, with hidden-state L2 diverging linearly at a rate independent of input —
/// strong evidence that audio bytes were not reaching the kernel (or the model file was
/// incompatible with Microsoft.ML.OnnxRuntime 1.24.4). Per systematic-debugging discipline,
/// 3+ failed fixes ⇒ architecture is suspect; the Silero path was abandoned in favour of
/// an energy detector.
///
/// Energy-VAD is sufficient for the clean-mic desktop-meeting use case: ~50 dB SNR between
/// speech (~-29 dBFS) and pauses (&lt;-80 dBFS). For noisy-room robustness, a future change
/// can drop in a WebRTC VAD wrapper behind the same <see cref="Process"/>/<see cref="OnSegment"/>
/// surface without touching the engine.
///
/// Stateful w.r.t. the speech segment (open/close hysteresis, preroll). Emits speech
/// segments via <see cref="OnSegment"/>.
/// </summary>
/// <summary>A closed speech segment plus where it starts in the capture stream, in samples.</summary>
public readonly record struct VadSegment(float[] Samples, long StartSample);

public sealed class SileroVadDetector : IDisposable
{
    private const int WindowSize = 512;          // 32 ms at 16 kHz
    private const int PrerollWindows = 16;        // ~512 ms preroll
    private const float SpeechStartThreshold = 0.4f;
    private const float SpeechEndThreshold = 0.35f;
    private const int SilenceWindowsToEnd = 16;   // ~512 ms of sub-threshold to close a segment
    private const int MinSegmentSamples = 8000;   // 0.5 s minimum to bother transcribing
    private const int MaxSegmentSamples = 20 * 16000; // 20 s flush cap
    private const int LogEveryNWindows = 100;     // ~3.2 s of audio

    // Energy thresholds. RMS above SpeechCertainRmsDb is unambiguously speech (prob=1);
    // below SilenceCertainRmsDb is unambiguously silence (prob=0); the band between
    // produces a linear pseudo-probability so the existing prob-threshold hysteresis
    // (SpeechStartThreshold / SpeechEndThreshold + SilenceWindowsToEnd) still applies.
    private const float SpeechCertainRmsDb = -35f;
    private const float SilenceCertainRmsDb = -50f;

    private readonly ILogger _logger;

    // Holds only the sub-window remainder between Process() calls. Process() writes in chunks
    // bounded by available room and drains complete windows after each chunk, so the buffer
    // never needs to fit a whole Process() call — capacity only has to be >= one window. The
    // generous sizing leaves headroom for the in-flight window plus accumulated remainder.
    private readonly FloatRingBuffer _pendingChunk = new(capacity: WindowSize * 8);

    // Preroll ring (oldest → newest) of recently-seen non-speech windows.
    private readonly Queue<float[]> _preroll = new(PrerollWindows + 1);

    // Currently-accumulating speech segment.
    private List<float>? _segment;
    private int _silentRunWindows;
    private long _segmentStartSample;

    // Diagnostics — sampled every LogEveryNWindows windows.
    private long _windowsProcessed;
    private float _maxProbInBatch;
    private float _maxWindowRmsInBatch;
    private float _maxWindowPeakInBatch;

    public event Action<VadSegment>? OnSegment;

    /// <summary>Fires once when a speech segment opens (transitions from silence to speech).</summary>
    public event Action? OnSpeechStarted;

    /// <summary>Fires once when a speech segment closes (silence run hits the threshold,
    /// the max-segment cap is reached, or <see cref="Drain"/> flushes a trailing segment).</summary>
    public event Action? OnSpeechEnded;

    public SileroVadDetector(string modelPath, ILogger logger)
    {
        _logger = logger;
        // modelPath is unused under the energy-VAD fallback. Kept in the signature so the
        // engine wiring stays untouched.
        _ = modelPath;
        _logger.LogInformation(
            "Energy-VAD active. Speech ≥ {Start:F0} dBFS, silence ≤ {End:F0} dBFS, hysteresis {N} windows ({Ms} ms).",
            SpeechCertainRmsDb, SilenceCertainRmsDb,
            SilenceWindowsToEnd, SilenceWindowsToEnd * WindowSize * 1000 / 16000);
    }

    /// <summary>
    /// Feed a chunk of 16 kHz mono Float32 samples. Internally accumulates into 512-sample
    /// windows and runs the energy detector on each. Triggers <see cref="OnSegment"/> when
    /// a speech run ends.
    /// </summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        // Write in chunks bounded by available room, draining complete windows after each
        // chunk. This decouples the ring-buffer capacity from the size of a single Process()
        // call: after every inner drain Count < WindowSize, so room stays positive and a chunk
        // of any size makes progress. Window order is preserved.
        var window = new float[WindowSize];
        while (!samples.IsEmpty)
        {
            var room = _pendingChunk.Capacity - _pendingChunk.Count;
            var take = Math.Min(room, samples.Length);
            _pendingChunk.Write(samples.Slice(0, take));
            samples = samples.Slice(take);

            while (_pendingChunk.TryRead(window))
            {
                ProcessWindow(window);
                window = new float[WindowSize]; // fresh array per window — segments capture them
            }
        }
    }

    private void ProcessWindow(float[] window)
    {
        // Compute RMS and peak from the window. These drive both the VAD decision and the
        // diagnostic logs.
        double sumSq = 0;
        float peak = 0;
        for (int i = 0; i < window.Length; i++)
        {
            var v = window[i];
            sumSq += v * v;
            var a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }
        var rms = (float)Math.Sqrt(sumSq / window.Length);
        var prob = ProbFromRms(rms);

        _windowsProcessed++;
        if (prob > _maxProbInBatch) _maxProbInBatch = prob;
        if (rms > _maxWindowRmsInBatch) _maxWindowRmsInBatch = rms;
        if (peak > _maxWindowPeakInBatch) _maxWindowPeakInBatch = peak;

        if (_windowsProcessed == 1)
        {
            _logger.LogInformation(
                "VAD first window: rms={Rms:E3} peak={Peak:E3} prob={P:F2} samples[0..3]=[{S0:F4}, {S1:F4}, {S2:F4}, {S3:F4}]",
                rms, peak, prob, window[0], window[1], window[2], window[3]);
        }

        if (_windowsProcessed % LogEveryNWindows == 0)
        {
            var rmsDb = RmsToDb(_maxWindowRmsInBatch);
            var peakDb = RmsToDb(_maxWindowPeakInBatch);
            _logger.LogDebug(
                "VAD windows={N} maxProb={P:F3} rmsDb={Rms:F1} peakDb={Peak:F1} segOpen={Open} segSamples={S}",
                _windowsProcessed, _maxProbInBatch, rmsDb, peakDb,
                _segment is not null, _segment?.Count ?? 0);
            _maxProbInBatch = 0f;
            _maxWindowRmsInBatch = 0f;
            _maxWindowPeakInBatch = 0f;
        }

        bool isSpeech = _segment is null
            ? prob >= SpeechStartThreshold
            : prob >= SpeechEndThreshold; // hysteresis: easier to stay in speech once started

        if (_segment is null)
        {
            if (isSpeech)
            {
                _segment = new List<float>(MaxSegmentSamples / 2);
                // Windows are contiguous and the preroll holds exactly the ones just before this
                // one, so the segment's stream position is exact rather than estimated.
                _segmentStartSample = (_windowsProcessed - 1 - _preroll.Count) * (long)WindowSize;
                foreach (var pre in _preroll)
                    _segment.AddRange(pre);
                _segment.AddRange(window);
                _silentRunWindows = 0;
                _logger.LogDebug(
                    "VAD segment OPEN at prob={P:F2} rmsDb={Rms:F1}, preroll={N} windows",
                    prob, RmsToDb(rms), _preroll.Count);
                try { OnSpeechStarted?.Invoke(); }
                catch (Exception ex) { _logger.LogError(ex, "VAD OnSpeechStarted subscriber threw"); }
            }
            else
            {
                EnqueuePreroll(window);
            }
            return;
        }

        _segment.AddRange(window);

        if (isSpeech)
        {
            _silentRunWindows = 0;
        }
        else
        {
            _silentRunWindows++;
            if (_silentRunWindows >= SilenceWindowsToEnd)
            {
                var samples = _segment.Count;
                if (samples >= MinSegmentSamples)
                    _logger.LogDebug(
                        "VAD segment CLOSED (silence) samples={S} duration={Ms}ms",
                        samples, samples * 1000 / 16000);
                else
                    _logger.LogDebug(
                        "VAD segment DROPPED (too short) samples={S}",
                        samples);
                FlushSegment();
                return;
            }
        }

        if (_segment.Count >= MaxSegmentSamples)
        {
            _logger.LogInformation("VAD: 20 s segment cap hit, flushing");
            FlushSegment();
        }
    }

    private static float ProbFromRms(float rms)
    {
        var rmsDb = RmsToDb(rms);
        if (rmsDb >= SpeechCertainRmsDb) return 1.0f;
        if (rmsDb <= SilenceCertainRmsDb) return 0.0f;
        return (rmsDb - SilenceCertainRmsDb) / (SpeechCertainRmsDb - SilenceCertainRmsDb);
    }

    private static float RmsToDb(float rms)
        => rms <= 1e-10f ? -200f : 20f * (float)Math.Log10(rms);

    private void EnqueuePreroll(float[] window)
    {
        _preroll.Enqueue(window);
        while (_preroll.Count > PrerollWindows)
            _preroll.Dequeue();
    }

    private void FlushSegment()
    {
        if (_segment is null) return;
        var samples = _segment;
        _segment = null;
        _silentRunWindows = 0;
        _preroll.Clear();

        try { OnSpeechEnded?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "VAD OnSpeechEnded subscriber threw"); }

        if (samples.Count >= MinSegmentSamples)
        {
            var arr = samples.ToArray();
            try { OnSegment?.Invoke(new VadSegment(arr, _segmentStartSample)); }
            catch (Exception ex) { _logger.LogError(ex, "VAD OnSegment subscriber threw"); }
        }
    }

    /// <summary>
    /// If a session ends mid-speech, force out whatever is buffered so the user sees the tail.
    /// </summary>
    public void Drain()
    {
        if (_segment is { Count: >= MinSegmentSamples })
        {
            _logger.LogDebug("VAD drain: flushing trailing segment samples={S}", _segment.Count);
            FlushSegment();
        }
        else
        {
            var hadOpenSegment = _segment is not null;
            _logger.LogDebug(
                "VAD drain: nothing to flush (segmentSamples={S})",
                _segment?.Count ?? 0);
            _segment = null;
            if (hadOpenSegment)
            {
                try { OnSpeechEnded?.Invoke(); }
                catch (Exception ex) { _logger.LogError(ex, "VAD OnSpeechEnded subscriber threw"); }
            }
        }
        _preroll.Clear();
        _pendingChunk.Clear();
    }

    public void Dispose()
    {
        // No native resources under the energy-VAD fallback.
    }
}
