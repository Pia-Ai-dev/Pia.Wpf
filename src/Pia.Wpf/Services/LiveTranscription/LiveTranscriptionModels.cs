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

    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "Models");

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
        using var reader = ReaderFactory.Open(fileStream);

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
