using System.IO;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// sherpa-onnx OfflineRecognizer wrapper for NVIDIA NeMo Parakeet TDT v3 (multilingual).
/// Transducer architecture: encoder + decoder + joiner. Language is auto-detected by the
/// model — no language code is passed in.
/// </summary>
public sealed class ParakeetSherpaEngine : ITranscriptionEngine
{
    private readonly OfflineRecognizer _recognizer;
    private readonly ILogger _logger;

    public ParakeetSherpaEngine(string modelDirectory, ILogger logger)
    {
        _logger = logger;

        var encoder = ResolveTransducerFile(modelDirectory, "encoder");
        var decoder = ResolveTransducerFile(modelDirectory, "decoder");
        var joiner = ResolveTransducerFile(modelDirectory, "joiner");
        var tokens = Path.Combine(modelDirectory, "tokens.txt");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        _logger.LogInformation(
            "Parakeet sherpa-onnx engine init: encoder='{Enc}' decoder='{Dec}' joiner='{Join}' tokens='{Tok}'",
            encoder, decoder, joiner, tokens);

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

    private static string ResolveTransducerFile(string dir, string role)
    {
        var direct = Path.Combine(dir, $"{role}.onnx");
        if (File.Exists(direct)) return direct;

        var match = Directory.EnumerateFiles(dir, $"{role}*.onnx").FirstOrDefault()
                    ?? Directory.EnumerateFiles(dir, $"*{role}*.onnx").FirstOrDefault();
        if (match is not null) return match;

        throw new FileNotFoundException(
            $"Parakeet sherpa-onnx model file '{role}' not found in '{dir}'. Re-download the model.");
    }

    public ValueTask DisposeAsync()
    {
        _recognizer.Dispose();
        return ValueTask.CompletedTask;
    }
}
