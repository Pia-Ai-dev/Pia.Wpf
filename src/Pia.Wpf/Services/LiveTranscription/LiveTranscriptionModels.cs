using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Resolves model file paths for the transcription pipeline. All models land under
/// <c>%LOCALAPPDATA%\Pia\Models</c> and are downloaded on first use.
///
/// Whisper and Parakeet bundles are sherpa-onnx tar.bz2 archives extracted into
/// per-model directories.
/// </summary>
public static class LiveTranscriptionModels
{
    private const string SherpaReleasesBase =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models";

    // NOTE: the "recongition" misspelling is part of the actual GitHub release tag — do NOT
    // "correct" it. The misspelled tag is the one that serves the model (Content-Length
    // 28,281,164); the correctly-spelled URL 404s.
    private const string SherpaSpeakerReleasesBase =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models";

    private const string SileroVadFileName = "silero_vad.onnx";
    private const string SileroVadDownloadUrl =
        "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";

    private const string SpeakerEmbeddingFileName =
        "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";

    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "Models");

    /// <summary>
    /// Flat path of the speaker-embedding model (mirrors the Silero VAD layout — the model is
    /// a single <c>.onnx</c>, not a sherpa-onnx bundle, so it is not nested under a sub-directory).
    /// </summary>
    public static string SpeakerEmbeddingModelPath { get; } =
        Path.Combine(ModelsDirectory, SpeakerEmbeddingFileName);

    public static bool IsSpeakerEmbeddingAvailable() =>
        File.Exists(SpeakerEmbeddingModelPath) && new FileInfo(SpeakerEmbeddingModelPath).Length > 0;

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

    /// <summary>
    /// Downloads (if missing) the 3D-Speaker CAM++ speaker-embedding model to
    /// <c>%LOCALAPPDATA%\Pia\Models\</c> and returns its path. Mirrors
    /// <see cref="EnsureSileroVadAsync"/>: a single <c>.onnx</c> fetched straight to a
    /// <c>.tmp</c> file and atomically moved into place. The optional <paramref name="progress"/>
    /// receives per-chunk <see cref="ModelDownloadProgress"/> in the <see cref="ModelDownloadPhase.Downloading"/>
    /// phase (no extract phase — the model is a single <c>.onnx</c>); a cached model returns early
    /// with no report at all.
    /// </summary>
    public static async Task<string> EnsureSpeakerEmbeddingAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken = default,
        IProgress<ModelDownloadProgress>? progress = null)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = SpeakerEmbeddingModelPath;
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        var url = $"{SherpaSpeakerReleasesBase}/{SpeakerEmbeddingFileName}";
        logger.LogInformation("Downloading speaker-embedding model to {Path}", path);

        var tmp = path + ".tmp";
        await DownloadWithProgressAsync(url, tmp, httpClientFactory, progress, cancellationToken)
            .ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Downloads (if missing) and extracts the sherpa-onnx Whisper bundle for the requested
    /// size into <c>%LOCALAPPDATA%\Pia\Models\sherpa-whisper-{size}\</c>. Returns the directory.
    /// </summary>
    public static Task<string> EnsureWhisperOnnxAsync(
        WhisperModelSize modelSize,
        IHttpClientFactory httpClientFactory,
        IProgress<ModelDownloadProgress>? progress,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var sizeSlug = WhisperSherpaSlug(modelSize);
        var bundleName = $"sherpa-onnx-whisper-{sizeSlug}";
        var url = $"{SherpaReleasesBase}/{bundleName}.tar.bz2";
        var targetDir = Path.Combine(ModelsDirectory, $"sherpa-whisper-{sizeSlug}");
        return EnsureBundleAsync(url, targetDir, httpClientFactory, progress, logger, cancellationToken);
    }

    public static bool IsWhisperOnnxAvailable(WhisperModelSize modelSize)
    {
        var dir = Path.Combine(ModelsDirectory, $"sherpa-whisper-{WhisperSherpaSlug(modelSize)}");
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.onnx").Any();
    }

    public static bool IsParakeetOnnxAvailable()
    {
        var dir = Path.Combine(ModelsDirectory, "sherpa-parakeet-tdt-v3");
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.onnx").Any();
    }

    /// <summary>
    /// Downloads (if missing) and extracts the sherpa-onnx Parakeet TDT v3 multilingual bundle
    /// into <c>%LOCALAPPDATA%\Pia\Models\sherpa-parakeet-tdt-v3\</c>. Returns the directory.
    /// </summary>
    public static Task<string> EnsureParakeetOnnxAsync(
        IHttpClientFactory httpClientFactory,
        IProgress<ModelDownloadProgress>? progress,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        const string bundleName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
        var url = $"{SherpaReleasesBase}/{bundleName}.tar.bz2";
        var targetDir = Path.Combine(ModelsDirectory, "sherpa-parakeet-tdt-v3");
        return EnsureBundleAsync(url, targetDir, httpClientFactory, progress, logger, cancellationToken);
    }

    private static async Task<string> EnsureBundleAsync(
        string url,
        string targetDir,
        IHttpClientFactory httpClientFactory,
        IProgress<ModelDownloadProgress>? progress,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);

        if (Directory.Exists(targetDir) && Directory.EnumerateFiles(targetDir, "*.onnx").Any())
        {
            return targetDir;
        }

        var tmpArchive = targetDir + ".tar.bz2.tmp";
        var tmpExtract = targetDir + ".extract.tmp";

        try
        {
            logger.LogInformation("Downloading sherpa-onnx bundle {Url}", url);
            var archiveBytes = await DownloadWithProgressAsync(url, tmpArchive, httpClientFactory, progress, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Extracting sherpa-onnx bundle to {Dir}", tmpExtract);
            if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, recursive: true);
            Directory.CreateDirectory(tmpExtract);

            // Switch the dialog into "extracting" mode (indeterminate spinner) before the
            // potentially-long BZip2 decompression starts. Real percentages here would be
            // misleading since no network traffic is happening anymore.
            progress?.Report(new ModelDownloadProgress(0, archiveBytes, ModelDownloadPhase.Extracting));

            await Task.Run(() => ExtractTarBz2(tmpArchive, tmpExtract), cancellationToken)
                .ConfigureAwait(false);

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.Move(tmpExtract, targetDir);

            return targetDir;
        }
        finally
        {
            try { if (File.Exists(tmpArchive)) File.Delete(tmpArchive); } catch { /* ignore */ }
            try { if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task<long> DownloadWithProgressAsync(
        string url,
        string destinationPath,
        IHttpClientFactory httpClientFactory,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? 0L;
        var buffer = new byte[16 * 1024];
        var bytesRead = 0L;

        await using (var dst = File.Create(destinationPath))
        await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            int read;
            while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesRead += read;
                if (totalBytes > 0)
                {
                    var pct = (int)(bytesRead * 100 / totalBytes);
                    progress?.Report(new ModelDownloadProgress(pct, totalBytes));
                }
            }
        }
        return bytesRead;
    }

    private static void ExtractTarBz2(string archivePath, string targetDir)
    {
        using var fileStream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(fileStream);

        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory) continue;

            // sherpa-onnx archives are wrapped in a top-level directory matching the bundle
            // name (e.g. "sherpa-onnx-whisper-base/encoder.onnx"). Strip that leading folder
            // so files land flat in the target directory.
            var key = reader.Entry.Key ?? string.Empty;
            key = key.Replace('\\', '/');
            var sep = key.IndexOf('/');
            var rel = sep >= 0 ? key[(sep + 1)..] : key;
            if (string.IsNullOrEmpty(rel)) continue;

            var dest = Path.Combine(targetDir, rel);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            reader.WriteEntryToFile(dest, new ExtractionOptions { Overwrite = true });
        }
    }

    private static string WhisperSherpaSlug(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => "tiny",
        WhisperModelSize.Base => "base",
        WhisperModelSize.Small => "small",
        WhisperModelSize.Medium => "medium",
        WhisperModelSize.Large => "large-v3-turbo",
        _ => "base",
    };
}
