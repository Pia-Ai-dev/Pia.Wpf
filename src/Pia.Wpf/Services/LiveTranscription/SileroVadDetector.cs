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
    private const float SpeechStartThreshold = 0.5f;
    private const float SpeechEndThreshold = 0.35f;
    private const int SilenceWindowsToEnd = 16;   // ~512 ms of sub-threshold to close a segment
    private const int MinSegmentSamples = 16000;  // 1 s minimum to bother transcribing
    private const int MaxSegmentSamples = 30 * 16000; // 30 s flush cap

    private readonly ILogger _logger;
    private readonly InferenceSession _session;
    private readonly DenseTensor<long> _srTensor;
    private float[] _state = new float[2 * 1 * 128];

    // Rolling pending samples that have not yet been gathered into the current 512 window.
    private readonly List<float> _pendingChunk = new(WindowSize * 2);

    // Preroll ring (oldest → newest) of recently-seen non-speech windows.
    private readonly Queue<float[]> _preroll = new(PrerollWindows + 1);

    // Currently-accumulating speech segment.
    private List<float>? _segment;
    private int _silentRunWindows;

    public event Action<float[]>? OnSegment;

    public SileroVadDetector(string modelPath, ILogger logger)
    {
        _logger = logger;
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        _srTensor = new DenseTensor<long>(new[] { 16000L }, new[] { 1 });
    }

    /// <summary>
    /// Feed a chunk of 16 kHz mono Float32 samples. Internally accumulates into 512-sample
    /// windows and runs Silero on each. Triggers <see cref="OnSegment"/> when a speech run ends.
    /// </summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        // Fast path: append to pending and consume in 512-sample windows.
        for (int i = 0; i < samples.Length; i++)
            _pendingChunk.Add(samples[i]);

        while (_pendingChunk.Count >= WindowSize)
        {
            var window = new float[WindowSize];
            _pendingChunk.CopyTo(0, window, 0, WindowSize);
            _pendingChunk.RemoveRange(0, WindowSize);
            ProcessWindow(window);
        }

        // Compact when pending list grows abnormally (shouldn't happen, but defensive).
        if (_pendingChunk.Capacity > WindowSize * 16 && _pendingChunk.Count < WindowSize)
            _pendingChunk.TrimExcess();
    }

    private void ProcessWindow(float[] window)
    {
        var prob = RunSilero(window);
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
                FlushSegment();
                return;
            }
        }

        if (_segment.Count >= MaxSegmentSamples)
        {
            _logger.LogDebug("Silero: 30s segment cap hit, flushing");
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
        if (_segment is { Count: >= MinSegmentSamples }) FlushSegment();
        else _segment = null;
        _preroll.Clear();
        _pendingChunk.Clear();
    }

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
        foreach (var r in results)
        {
            if (r.Name == "output")
            {
                var t = r.AsTensor<float>();
                prob = t.GetValue(0);
            }
            else if (r.Name == "stateN")
            {
                var t = r.AsTensor<float>();
                // 2*1*128 = 256 floats
                for (int i = 0; i < _state.Length; i++)
                    _state[i] = t.GetValue(i);
            }
        }
        return prob;
    }

    public void Dispose() => _session.Dispose();
}
