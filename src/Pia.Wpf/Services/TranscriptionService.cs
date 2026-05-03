using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TranscriptionService> _logger;

    public TranscriptionService(
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILogger<TranscriptionService> logger)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        Directory.CreateDirectory(LiveTranscriptionModels.ModelsDirectory);
    }

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();

        var samples = await Task.Run(() => DecodeTo16kMonoFloat(audioFilePath), cancellationToken).ConfigureAwait(false);

        await using var engine = await TranscriptionEngineFactory
            .CreateAsync(settings, _httpClientFactory, downloadProgress: null, _logger, cancellationToken)
            .ConfigureAwait(false);

        // Chunk long audio so the engine isn't asked to process minutes of speech in one shot.
        const int sampleRate = 16000;
        const int chunkSeconds = 25;
        const int chunkSize = chunkSeconds * sampleRate;

        if (samples.Length <= chunkSize)
        {
            return await engine.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
        }

        var pieces = new List<string>();
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, samples.Length - offset);
            var chunk = new float[len];
            Array.Copy(samples, offset, chunk, 0, len);
            var text = await engine.TranscribeAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text)) pieces.Add(text.Trim());
        }
        return string.Join(" ", pieces);
    }

    public static string GetModelName(WhisperModelSize modelSize)
    {
        return modelSize switch
        {
            WhisperModelSize.Tiny => "Whisper Tiny",
            WhisperModelSize.Base => "Whisper Base",
            WhisperModelSize.Small => "Whisper Small",
            WhisperModelSize.Medium => "Whisper Medium",
            WhisperModelSize.Large => "Whisper Large v3 Turbo",
            _ => "Whisper Base"
        };
    }

    public async Task DownloadModelAsync(WhisperModelSize modelSize, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        await LiveTranscriptionModels
            .EnsureWhisperOnnxAsync(modelSize, _httpClientFactory, progress, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DownloadParakeetModelAsync(IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        await LiveTranscriptionModels
            .EnsureParakeetOnnxAsync(_httpClientFactory, progress, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes any audio container that Media Foundation can open (wav, mp3, m4a, …) to a
    /// 16 kHz mono float32 buffer matching the live pipeline's expectations.
    /// </summary>
    private static float[] DecodeTo16kMonoFloat(string audioFilePath)
    {
        using var reader = new MediaFoundationReader(audioFilePath);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };

        using var pcmStream = new MemoryStream();
        var buffer = new byte[16000 * 2 * 4]; // ~4 s at 16 kHz mono 16-bit
        int bytesRead;
        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            pcmStream.Write(buffer, 0, bytesRead);
        }

        var pcm = pcmStream.GetBuffer();
        var byteCount = (int)pcmStream.Length;
        var samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, byteCount));
        var floats = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            floats[i] = samples[i] / 32768f;
        }
        return floats;
    }
}
