using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default);

    Task DownloadModelAsync(WhisperModelSize modelSize, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default);

    Task DownloadParakeetModelAsync(IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default);
}

public record ModelDownloadProgress(int PercentComplete, long TotalBytes, ModelDownloadPhase Phase = ModelDownloadPhase.Downloading);

public enum ModelDownloadPhase
{
    Downloading,
    Extracting,
}
