using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Builds the right <see cref="ITranscriptionEngine"/> for the active <see cref="AppSettings.SttBackend"/>.
/// Model files must already be on disk — call <see cref="EnsureModelsAsync"/> first if a download
/// flow is needed (the settings ViewModel's "Download" button uses the same helpers directly).
/// </summary>
public static class TranscriptionEngineFactory
{
    public static async Task<ITranscriptionEngine> CreateAsync(
        AppSettings settings,
        IAssetDownloader downloader,
        IProgress<ModelDownloadProgress>? downloadProgress,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        switch (settings.SttBackend)
        {
            case SttBackend.Parakeet:
            {
                var dir = await LiveTranscriptionModels
                    .EnsureParakeetOnnxAsync(downloader, downloadProgress, logger, cancellationToken)
                    .ConfigureAwait(false);
                return new ParakeetSherpaEngine(dir, logger);
            }
            case SttBackend.Whisper:
            default:
            {
                var dir = await LiveTranscriptionModels
                    .EnsureWhisperOnnxAsync(settings.WhisperModel, downloader, downloadProgress, logger, cancellationToken)
                    .ConfigureAwait(false);
                return new WhisperSherpaEngine(dir, LanguageCode(settings.TargetSpeechLanguage), logger);
            }
        }
    }

    /// <summary>
    /// Downloads (if missing) and verifies model files for the given backend without constructing
    /// the engine. Used by the settings UI's "Download model" button.
    /// </summary>
    public static Task<string> EnsureModelsAsync(
        AppSettings settings,
        IAssetDownloader downloader,
        IProgress<ModelDownloadProgress>? downloadProgress,
        ILogger logger,
        CancellationToken cancellationToken)
        => settings.SttBackend == SttBackend.Parakeet
            ? LiveTranscriptionModels.EnsureParakeetOnnxAsync(downloader, downloadProgress, logger, cancellationToken)
            : LiveTranscriptionModels.EnsureWhisperOnnxAsync(settings.WhisperModel, downloader, downloadProgress, logger, cancellationToken);

    private static string LanguageCode(TargetSpeechLanguage language) => language switch
    {
        TargetSpeechLanguage.Auto => "auto",
        TargetSpeechLanguage.EN => "en",
        TargetSpeechLanguage.DE => "de",
        TargetSpeechLanguage.FR => "fr",
        _ => "auto",
    };
}
