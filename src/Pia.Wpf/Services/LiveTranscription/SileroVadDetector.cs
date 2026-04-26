using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Voice-activity gate built on the Silero VAD ONNX model (v5+, 16 kHz, 512-sample windows).
///
/// Stateful: carries a 2x1x128 recurrent state across inferences and a small ring of
/// "preroll" windows so the start of an utterance is not lost when speech is detected.
/// Emits speech segments as <see cref="float"/> arrays via <see cref="OnSegment"/>.
///
/// The gate enforces a hard segment cap (default 30 s) — long monologues are flushed and
/// transcription resumes from the next sample, keeping latency predictable for long sessions.
/// </summary>
public sealed class SileroVadDetector : IDisposable
{
    private const int WindowSize = 512;          // 32 ms at 16 kHz
    private const int PrerollWindows = 16;        // ~512 ms preroll
    private const float SpeechStartThreshold = 0.4f;
    private const float SpeechEndThreshold = 0.35f;
    private const int SilenceWindowsToEnd = 16;   // ~512 ms of sub-threshold to close a segment
    private const int MinSegmentSamples = 8000;   // 0.5 s minimum to bother transcribing
    private const int MaxSegmentSamples = 30 * 16000; // 30 s flush cap
    private const int LogEveryNWindows = 100;     // ~3.2 s of audio

    private readonly ILogger _logger;
    private readonly InferenceSession _session;
    private readonly DenseTensor<long> _srTensor;
    private float[] _state = new float[2 * 1 * 128];

    // Capacity must accommodate the largest expected single Process() call. Loopback emits
    // ~50 ms hops at 16 kHz = 800 samples; mic emits the same. Plus one in-flight window.
    // 4 KiB of float storage is negligible.
    private readonly FloatRingBuffer _pendingChunk = new(capacity: WindowSize * 8);

    // Preroll ring (oldest → newest) of recently-seen non-speech windows.
    private readonly Queue<float[]> _preroll = new(PrerollWindows + 1);

    // Currently-accumulating speech segment.
    private List<float>? _segment;
    private int _silentRunWindows;

    // Diagnostics — sampled every LogEveryNWindows windows.
    private long _windowsProcessed;
    private float _maxProbInBatch;
    private float _maxWindowRmsInBatch;
    private float _maxWindowPeakInBatch;

    public event Action<float[]>? OnSegment;

    public SileroVadDetector(string modelPath, ILogger logger)
    {
        _logger = logger;
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        // Silero v5 declares 'sr' as a rank-0 scalar (shape []). Passing a rank-1 [1]-shaped
        // tensor does not throw, but the kernel reads it as 0 and the model collapses to ~0
        // probability for every window.
        _srTensor = new DenseTensor<long>(new[] { 16000L }, Array.Empty<int>());

        foreach (var kv in _session.InputMetadata)
            _logger.LogInformation(
                "Silero input '{Name}': type={Type} shape=[{Shape}]",
                kv.Key, kv.Value.ElementType, string.Join(",", kv.Value.Dimensions));
        foreach (var kv in _session.OutputMetadata)
            _logger.LogInformation(
                "Silero output '{Name}': type={Type} shape=[{Shape}]",
                kv.Key, kv.Value.ElementType, string.Join(",", kv.Value.Dimensions));
    }

    /// <summary>
    /// Feed a chunk of 16 kHz mono Float32 samples. Internally accumulates into 512-sample
    /// windows and runs Silero on each. Triggers <see cref="OnSegment"/> when a speech run ends.
    /// </summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        _pendingChunk.Write(samples);

        var window = new float[WindowSize];
        while (_pendingChunk.TryRead(window))
        {
            ProcessWindow(window);
            window = new float[WindowSize]; // fresh array per window — segments capture them
        }
    }

    private void ProcessWindow(float[] window)
    {
        var prob = RunSilero(window);
        _windowsProcessed++;
        if (prob > _maxProbInBatch) _maxProbInBatch = prob;

        // Window RMS — confirms audio is intact between capture and inference.
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
        if (rms > _maxWindowRmsInBatch) _maxWindowRmsInBatch = rms;
        if (peak > _maxWindowPeakInBatch) _maxWindowPeakInBatch = peak;

        if (_windowsProcessed == 1)
        {
            _logger.LogInformation(
                "VAD first window: rms={Rms:E3} peak={Peak:E3} samples[0..3]=[{S0:F4}, {S1:F4}, {S2:F4}, {S3:F4}]",
                rms, peak, window[0], window[1], window[2], window[3]);
        }

        if (_windowsProcessed % LogEveryNWindows == 0)
        {
            var rmsDb = _maxWindowRmsInBatch <= 1e-10f ? -200f : 20f * (float)Math.Log10(_maxWindowRmsInBatch);
            var peakDb = _maxWindowPeakInBatch <= 1e-10f ? -200f : 20f * (float)Math.Log10(_maxWindowPeakInBatch);
            float stateNorm = 0;
            for (int i = 0; i < _state.Length; i++) stateNorm += _state[i] * _state[i];
            _logger.LogDebug(
                "VAD windows={N} maxProb={P:F3} rmsDb={Rms:F1} peakDb={Peak:F1} stateL2={SL:F2} segOpen={Open} segSamples={S}",
                _windowsProcessed, _maxProbInBatch, rmsDb, peakDb, Math.Sqrt(stateNorm),
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
                // Open a new segment, prepending preroll so the leading consonant isn't lost.
                _segment = new List<float>(MaxSegmentSamples / 2);
                foreach (var pre in _preroll)
                    _segment.AddRange(pre);
                _segment.AddRange(window);
                _silentRunWindows = 0;
                _logger.LogDebug(
                    "VAD segment OPEN at prob={P:F2}, preroll={N} windows",
                    prob, _preroll.Count);
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
            _logger.LogInformation("Silero: 30s segment cap hit, flushing");
            FlushSegment();
        }
    }

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
        // Reset preroll between segments — preroll captures the leading edge of the next
        // utterance only, not trailing tails.
        _preroll.Clear();

        if (samples.Count >= MinSegmentSamples)
        {
            var arr = samples.ToArray();
            try { OnSegment?.Invoke(arr); }
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
            _logger.LogDebug(
                "VAD drain: nothing to flush (segmentSamples={S})",
                _segment?.Count ?? 0);
            _segment = null;
        }
        _preroll.Clear();
        _pendingChunk.Clear();
    }


    private bool _firstInferenceLogged;

    private float RunSilero(float[] window)
    {
        var input = new DenseTensor<float>(window, new[] { 1, WindowSize });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", _srTensor),
        });

        float prob = 0f;
        int stateElementsCopied = 0;
        bool sawOutput = false;
        foreach (var r in results)
        {
            if (!_firstInferenceLogged)
                _logger.LogInformation(
                    "Silero first inference output '{Name}' type={Type}",
                    r.Name, r.Value?.GetType().Name ?? "null");

            if (r.Name == "output")
            {
                var dt = (DenseTensor<float>)r.AsTensor<float>();
                if (dt.Buffer.Length > 0) prob = dt.Buffer.Span[0];
                sawOutput = true;
            }
            else if (r.Name == "stateN")
            {
                var dt = (DenseTensor<float>)r.AsTensor<float>();
                var span = dt.Buffer.Span;
                stateElementsCopied = Math.Min(span.Length, _state.Length);
                span.Slice(0, stateElementsCopied).CopyTo(_state.AsSpan());
            }
        }

        if (!_firstInferenceLogged)
        {
            _firstInferenceLogged = true;
            if (!sawOutput) _logger.LogWarning("Silero: 'output' tensor not found in results — prob will always be 0");
            float stateNorm = 0;
            for (int i = 0; i < _state.Length; i++) stateNorm += _state[i] * _state[i];
            _logger.LogInformation(
                "Silero first inference: prob={P:F3} stateCopied={N}/{Total} stateL2={L:F3} state[0..3]=[{S0:F4},{S1:F4},{S2:F4},{S3:F4}]",
                prob, stateElementsCopied, _state.Length, Math.Sqrt(stateNorm),
                _state[0], _state[1], _state[2], _state[3]);
        }
        return prob;
    }

    public void Dispose() => _session.Dispose();
}
