using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Resolves model file paths for the live transcription pipeline. Both Whisper ggml weights
/// and the Silero VAD ONNX file land under <c>%LOCALAPPDATA%\Pia\Models</c>, mirroring the
/// existing <see cref="TranscriptionService"/> behaviour. Models are downloaded on first
/// use and reused thereafter.
/// </summary>
public static class LiveTranscriptionModels
{
    private const string SileroVadFileName = "silero_vad.onnx";
    private const string SileroVadDownloadUrl =
        "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";

    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "Models");

    public static async Task<string> EnsureSileroVadAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = Path.Combine(ModelsDirectory, SileroVadFileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        logger.LogInformation("Downloading Silero VAD model to {Path}", path);
        var http = httpClientFactory.CreateClient();
        using var resp = await http.GetAsync(SileroVadDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var tmp = path + ".tmp";
        await using (var dst = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static async Task<string> EnsureWhisperGgmlAsync(
        WhisperModelSize modelSize,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var name = TranscriptionService.GetModelName(modelSize);
        var path = Path.Combine(ModelsDirectory, name);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        logger.LogInformation("Downloading Whisper model {Name} to {Path}", name, path);
        var http = httpClientFactory.CreateClient();
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{name}";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var tmp = path + ".tmp";
        await using (var dst = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
