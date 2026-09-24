using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Pia.Services.Assets;

/// <summary>
/// Download-and-extract for a sherpa-onnx <c>tar.bz2</c> release: the transcription models and the TTS
/// voices are published in the same shape, so they share the one extractor.
/// </summary>
public static class SherpaBundle
{
    /// <summary>
    /// Fetches and extracts <paramref name="asset"/> into <paramref name="targetDir"/> unless a model is
    /// already there. Returns the directory.
    /// </summary>
    public static async Task<string> EnsureAsync(
        RuntimeAsset asset,
        string targetDir,
        IAssetDownloader downloader,
        IProgress<ModelDownloadProgress>? progress,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(targetDir);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        if (Directory.Exists(targetDir) && Directory.EnumerateFiles(targetDir, "*.onnx").Any())
        {
            return targetDir;
        }

        var tmpArchive = targetDir + ".tar.bz2.tmp";
        var tmpExtract = targetDir + ".extract.tmp";

        try
        {
            logger.LogInformation("Downloading sherpa-onnx bundle {Key}", asset.MirrorKey);
            var archiveBytes = await downloader.DownloadAsync(asset, tmpArchive, progress, cancellationToken)
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

    internal static void ExtractTarBz2(string archivePath, string targetDir)
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
}
