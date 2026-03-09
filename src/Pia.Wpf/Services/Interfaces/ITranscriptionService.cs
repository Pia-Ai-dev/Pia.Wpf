using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default);

    Task DownloadModelAsync(WhisperModelSize modelSize, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default);

    event EventHandler<(int Progress, int Total)>? ModelDownloadProgress;
}

public record ModelDownloadProgress(int PercentComplete, long TotalBytes);
