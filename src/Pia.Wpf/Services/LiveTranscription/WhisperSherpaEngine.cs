using System.IO;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// sherpa-onnx OfflineRecognizer wrapper for Whisper ONNX models.
/// Thread-safe: a single recognizer can decode many streams concurrently.
/// </summary>
public sealed class WhisperSherpaEngine : ITranscriptionEngine
{
    private readonly OfflineRecognizer _recognizer;
    private readonly ILogger _logger;

    public WhisperSherpaEngine(string modelDirectory, string languageCode, ILogger logger)
    {
        _logger = logger;

        var encoder = ResolveModelFile(modelDirectory, "encoder");
        var decoder = ResolveModelFile(modelDirectory, "decoder");
        var tokens = ResolveTokensFile(modelDirectory);

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Whisper.Encoder = encoder;
        config.ModelConfig.Whisper.Decoder = decoder;
        config.ModelConfig.Whisper.Language = languageCode == "auto" ? "" : languageCode;
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        _logger.LogInformation(
            "Whisper sherpa-onnx engine init: encoder='{Enc}' decoder='{Dec}' tokens='{Tok}' lang='{Lang}'",
            encoder, decoder, tokens, languageCode);

        _recognizer = new OfflineRecognizer(config);
    }

    public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples16kMono);
            _recognizer.Decode(stream);
            return stream.Result.Text?.Trim() ?? string.Empty;
        }, cancellationToken);
    }

    private static string ResolveModelFile(string dir, string role)
    {
        // sherpa-onnx Whisper bundles ship files like <size>-encoder.int8.onnx
        // (e.g. base-encoder.int8.onnx). Pick whichever encoder/decoder file exists,
        // preferring int8 (smaller, fits the bundle size we ship).
        var direct = Path.Combine(dir, $"{role}.onnx");
        if (File.Exists(direct)) return direct;

        var match = Directory.EnumerateFiles(dir, $"*{role}*.int8.onnx").FirstOrDefault()
                    ?? Directory.EnumerateFiles(dir, $"*{role}*.onnx").FirstOrDefault();
        if (match is not null) return match;

        throw new FileNotFoundException(
            $"Whisper sherpa-onnx model file '{role}' not found in '{dir}'. Re-download the model.");
    }

    private static string ResolveTokensFile(string dir)
    {
        // sherpa-onnx Whisper bundles ship tokens as <size>-tokens.txt (e.g. base-tokens.txt).
        var direct = Path.Combine(dir, "tokens.txt");
        if (File.Exists(direct)) return direct;

        var match = Directory.EnumerateFiles(dir, "*tokens*.txt").FirstOrDefault();
        if (match is not null) return match;

        throw new FileNotFoundException(
            $"Whisper sherpa-onnx tokens file not found in '{dir}'. Re-download the model.");
    }

    public ValueTask DisposeAsync()
    {
        _recognizer.Dispose();
        return ValueTask.CompletedTask;
    }
}
