using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// sherpa-onnx OnlineRecognizer wrapper for NVIDIA nemotron-3.5 streaming (560 ms chunks).
/// Cache-aware streaming transducer; language is chosen per stream rather than baked into the config.
/// </summary>
public sealed class NemotronStreamingEngine : ITranscriptionEngine, IStreamingTranscriptionEngine
{
    private const int SampleRate = 16000;

    // Baked into the export, so this is the model rather than a setting.
    private const int ChunkMs = 560;
    private const int ChunkSamples = SampleRate * ChunkMs / 1000;

    private readonly OnlineRecognizer _recognizer;
    private readonly string _languageCode;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

    public NemotronStreamingEngine(string modelDirectory, string languageCode, ILogger logger)
    {
        _languageCode = string.IsNullOrWhiteSpace(languageCode) ? "auto" : languageCode;
        _logger = logger;

        var config = BuildConfig(modelDirectory);

        _logger.LogInformation(
            "Nemotron sherpa-onnx engine init: dir='{Dir}' language={Language}",
            modelDirectory, _languageCode);

        _recognizer = new OnlineRecognizer(config);
    }

    internal static OnlineRecognizerConfig BuildConfig(string modelDirectory)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = ResolveTransducerFile(modelDirectory, "encoder");
        config.ModelConfig.Transducer.Decoder = ResolveTransducerFile(modelDirectory, "decoder");
        config.ModelConfig.Transducer.Joiner = ResolveTransducerFile(modelDirectory, "joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        // sherpa's streaming NeMo implementations exit(-1) on any other method — a process kill from
        // native code, not a catchable exception.
        config.DecodingMethod = "greedy_search";

        // SileroVadDetector owns segment boundaries; sherpa's endpointer would compete with it.
        config.EnableEndpoint = 0;

        return config;
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The mic and loopback services share one recognizer and decode on their own threads.
        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var stream = _recognizer.CreateStream();
                stream.SetOption("language", _languageCode);
                stream.AcceptWaveform(SampleRate, PadForColdDecode(samples16kMono));
                stream.InputFinished();
                while (_recognizer.IsReady(stream)) _recognizer.Decode(stream);
                return _recognizer.GetResult(stream).Text?.Trim() ?? string.Empty;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public IStreamingSession BeginSession() => new Session(_recognizer, _decodeGate, _languageCode, _logger);

    /// <summary>One chunk of silence, fed after a reset so the next utterance does not decode cold.</summary>
    internal static float[] CacheWarmupSilence() => new float[ChunkSamples];

    private sealed class Session : IStreamingSession
    {
        // A frame handed over while the gate is held by a segment-final decode must not park the
        // caller: the caller is the audio capture reader, and a stalled reader overruns its buffer.
        private static readonly float[] ResetSentinel = [];

        private readonly OnlineRecognizer _recognizer;
        private readonly SemaphoreSlim _gate;
        private readonly OnlineStream _stream;
        private readonly Channel<float[]> _pending = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(256)
            {
                // Never DropOldest: a hole in the audio corrupts the running hypothesis. Let the
                // backlog drain and the preview lag instead.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        private readonly ILogger _logger;
        private readonly Task _drain;
        private string _partial = string.Empty;
        private long _dropped;
        private volatile bool _stopping;

        public Session(OnlineRecognizer recognizer, SemaphoreSlim gate, string languageCode, ILogger logger)
        {
            _recognizer = recognizer;
            _gate = gate;
            _logger = logger;
            _stream = recognizer.CreateStream();
            _stream.SetOption("language", languageCode);
            _drain = Task.Run(DrainAsync);
        }

        // Only the drain task writes _partial, so a plain read is enough for a preview.
        public string CurrentPartial => _partial;

        // TryWrite, not WriteAsync: the caller is the audio capture reader and must never park. A
        // full queue therefore drops the frame — 256 frames is ~7.7 s of backlog, so this only
        // happens when a segment-final decode has held the gate for longer than that. One preview
        // degrades; blocking the reader instead would lose committed audio.
        public void Feed(float[] samples16kMono)
        {
            if (!_pending.Writer.TryWrite(samples16kMono)) Interlocked.Increment(ref _dropped);
        }

        public void Reset()
        {
            if (!_pending.Writer.TryWrite(ResetSentinel)) Interlocked.Increment(ref _dropped);
        }

        private async Task DrainAsync()
        {
            await foreach (var frame in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Checked before the gate so disposal waits on at most one in-flight decode.
                if (_stopping) return;

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(frame, ResetSentinel))
                    {
                        _recognizer.Reset(_stream);
                        // Reset empties the encoder cache, so without this the next utterance loses
                        // its opening words exactly as a cold segment decode does.
                        _stream.AcceptWaveform(SampleRate, CacheWarmupSilence());
                        _partial = string.Empty;
                        continue;
                    }

                    _stream.AcceptWaveform(SampleRate, frame);
                    while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
                    _partial = _recognizer.GetResult(_stream).Text?.Trim() ?? string.Empty;
                }
                finally { _gate.Release(); }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _pending.Writer.TryComplete();

            var finished = false;
            try { finished = _drain.Wait(TimeSpan.FromSeconds(10)); }
            catch { /* the drain's own failure is not this method's problem */ }

            var dropped = Interlocked.Read(ref _dropped);
            if (dropped > 0)
                _logger.LogWarning("Streaming session dropped {Dropped} frames to preview backpressure", dropped);

            // Freeing the stream while the drain is still inside a native Decode is an access
            // violation, not an exception — it takes the whole process with it. A leaked native
            // stream on a pathological shutdown is the cheaper failure.
            if (finished) _stream.Dispose();
            else _logger.LogWarning("Streaming session drain did not stop; leaving its native stream to the finalizer");
        }
    }

    /// <summary>
    /// A VAD segment arrives silence-trimmed and hits an empty encoder cache. Without a chunk of
    /// lead-in the model loses the words before its first full chunk, and without a chunk of
    /// trailing silence it never flushes the last partial one — measured on the bundle's own de.wav
    /// as "hat ein Ende nur die Wurst hat" against "Alles hat ein Ende, nur die Wurst hat zwei".
    /// The streaming path needs none of this: there the cache is already warm.
    /// </summary>
    internal static float[] PadForColdDecode(float[] samples16kMono)
    {
        var padded = new float[ChunkSamples + samples16kMono.Length + ChunkSamples];
        samples16kMono.CopyTo(padded, ChunkSamples);
        return padded;
    }

    private static string ResolveTransducerFile(string dir, string role)
    {
        var direct = Path.Combine(dir, $"{role}.onnx");
        if (File.Exists(direct)) return direct;

        var match = Directory.EnumerateFiles(dir, $"{role}*.onnx").FirstOrDefault()
                    ?? Directory.EnumerateFiles(dir, $"*{role}*.onnx").FirstOrDefault();
        if (match is not null) return match;

        throw new FileNotFoundException(
            $"Nemotron sherpa-onnx model file '{role}' not found in '{dir}'. Re-download the model.");
    }

    public ValueTask DisposeAsync()
    {
        _recognizer.Dispose();
        _decodeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
