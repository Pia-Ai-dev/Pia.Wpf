using System.IO;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// sherpa-onnx OnlineRecognizer wrapper for NVIDIA nemotron-3.5 streaming (560 ms chunks).
/// Cache-aware streaming transducer; language is chosen per stream rather than baked into the config.
/// </summary>
public sealed class NemotronStreamingEngine : ITranscriptionEngine
{
    private const int SampleRate = 16000;

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
                stream.AcceptWaveform(SampleRate, samples16kMono);
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
