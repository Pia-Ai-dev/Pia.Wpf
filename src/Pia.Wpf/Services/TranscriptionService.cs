using System.IO;
using System.Net.Http;
using Pia.Models;
using Pia.Services.Interfaces;
using Whisper.net;
using Whisper.net.Ggml;

namespace Pia.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _modelsDirectory;

    public TranscriptionService(ISettingsService settingsService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _modelsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pia",
            "Models");

        Directory.CreateDirectory(_modelsDirectory);
    }

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var modelName = GetModelName(settings.WhisperModel);
        var modelPath = Path.Combine(_modelsDirectory, modelName);

        if (!File.Exists(modelPath))
        {
            await DownloadModelAsync(modelName, modelPath, cancellationToken, progress: null);
        }

        using var whisperFactory = WhisperFactory.FromPath(modelPath);
        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage(GetLanguageCode(settings.TargetSpeechLanguage))
            .Build();

        using var fileStream = File.OpenRead(audioFilePath);
        var result = processor.ProcessAsync(fileStream, cancellationToken);

        var segments = new List<string>();
        await foreach (var segment in result)
        {
            segments.Add(segment.Text);
        }

        var transcribedText = string.Join(" ", segments);
        return transcribedText;
    }

    public static string GetModelName(WhisperModelSize modelSize)
    {
        return modelSize switch
        {
            WhisperModelSize.Tiny => "ggml-tiny.bin",
            WhisperModelSize.Base => "ggml-base.bin",
            WhisperModelSize.Small => "ggml-small.bin",
            WhisperModelSize.Medium => "ggml-medium.bin",
            WhisperModelSize.Large => "ggml-large-v3-turbo.bin",
            _ => "ggml-base.bin"
        };
    }

    private static string GetLanguageCode(TargetSpeechLanguage language)
    {
        return language switch
        {
            TargetSpeechLanguage.Auto => "auto",
            TargetSpeechLanguage.EN => "en",
            TargetSpeechLanguage.DE => "de",
            TargetSpeechLanguage.FR => "fr",
            _ => "auto"
        };
    }

    private async Task DownloadModelAsync(string modelName, string modelPath, CancellationToken cancellationToken, IProgress<Services.Interfaces.ModelDownloadProgress>? progress = null)
    {
        var downloadUrl = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{modelName}";

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var buffer = new byte[8192];
        var bytesRead = 0L;

        await using var fileStream = File.Create(modelPath);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        int read;
        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
            bytesRead += read;

            if (totalBytes > 0)
            {
                var progressPercent = (int)(bytesRead * 100 / totalBytes);
                ModelDownloadProgress?.Invoke(this, (progressPercent, (int)totalBytes));
                progress?.Report(new Services.Interfaces.ModelDownloadProgress(progressPercent, totalBytes));
            }
        }
    }

    public async Task DownloadModelAsync(WhisperModelSize modelSize, IProgress<Services.Interfaces.ModelDownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        var modelName = GetModelName(modelSize);
        var modelPath = Path.Combine(_modelsDirectory, modelName);
        await DownloadModelAsync(modelName, modelPath, cancellationToken, progress);
    }

    public event EventHandler<(int Progress, int Total)>? ModelDownloadProgress;
}
