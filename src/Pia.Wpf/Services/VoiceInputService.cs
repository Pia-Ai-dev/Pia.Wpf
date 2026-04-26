using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services;

public class VoiceInputService : IVoiceInputService
{
    private readonly IAudioRecordingService _audioRecordingService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILogger<VoiceInputService> _logger;

    public VoiceInputService(
        IAudioRecordingService audioRecordingService,
        ITranscriptionService transcriptionService,
        IDialogService dialogService,
        ISettingsService settingsService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILogger<VoiceInputService> logger)
    {
        _audioRecordingService = audioRecordingService;
        _transcriptionService = transcriptionService;
        _dialogService = dialogService;
        _settingsService = settingsService;
        _snackbarService = snackbarService;
        _logger = logger;
    }

    public async Task<string?> CaptureVoiceInputAsync()
    {
        if (_audioRecordingService.IsRecording)
        {
            _snackbarService.Show(
                "Recording Busy",
                "Audio recording is in use by another window",
                Wpf.Ui.Controls.ControlAppearance.Caution,
                null,
                TimeSpan.FromSeconds(4));
            return null;
        }

        var recordingCts = new CancellationTokenSource();
        string? audioFilePath = null;

        try
        {
            await _audioRecordingService.StartRecordingAsync();

            await _dialogService.ShowRecordingDialogAsync(recordingCts.Token);

            var wasCancelled = recordingCts.Token.IsCancellationRequested;
            audioFilePath = await _audioRecordingService.StopRecordingAsync();

            if (wasCancelled)
            {
                _snackbarService.Show("Cancelled", "Recording was cancelled",
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                return null;
            }

            if (!_audioRecordingService.HasAudioContent(audioFilePath))
            {
                _snackbarService.Show(
                    "No Audio Detected",
                    "No speech was detected. Please check your microphone settings.",
                    Wpf.Ui.Controls.ControlAppearance.Caution,
                    null,
                    TimeSpan.FromSeconds(4));
                return null;
            }

            return await TranscribeAsync(audioFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice input failed");
            _snackbarService.Show("Error", $"Recording failed: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
            return null;
        }
        finally
        {
            recordingCts.Dispose();
            CleanupAudioFile(audioFilePath);
        }
    }

    private async Task<string?> TranscribeAsync(string audioFilePath)
    {
        if (!await EnsureModelDownloadedAsync())
            return null;

        var transcriptionCts = new CancellationTokenSource();
        var dialogCts = new CancellationTokenSource();

        try
        {
            var transcriptionTask = RunTranscriptionAsync(audioFilePath, transcriptionCts.Token, dialogCts);
            var dialogTask = _dialogService.ShowTranscribingDialogAsync(dialogCts.Token);

            var completedTask = await Task.WhenAny(transcriptionTask, dialogTask);

            if (completedTask == dialogTask)
            {
                var dialogCancelled = await dialogTask;
                if (dialogCancelled)
                {
                    transcriptionCts.Cancel();
                    _snackbarService.Show("Cancelled", "Transcription was cancelled",
                        Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                    return null;
                }
            }

            return await transcriptionTask;
        }
        finally
        {
            transcriptionCts.Dispose();
            dialogCts.Dispose();
        }
    }

    private async Task<string?> RunTranscriptionAsync(
        string audioFilePath,
        CancellationToken cancellationToken,
        CancellationTokenSource dialogCancellation)
    {
        try
        {
            var transcription = await _transcriptionService.TranscribeAsync(audioFilePath, cancellationToken);
            dialogCancellation.Cancel();

            if (string.IsNullOrWhiteSpace(transcription))
            {
                _snackbarService.Show("No Speech", "No speech could be transcribed from the recording.",
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                return null;
            }

            return transcription;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            dialogCancellation.Cancel();
            _logger.LogError(ex, "Transcription failed");
            _snackbarService.Show("Error", $"Transcription failed: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
            return null;
        }
    }

    private async Task<bool> EnsureModelDownloadedAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        var alreadyDownloaded = settings.SttBackend switch
        {
            SttBackend.Parakeet => LiveTranscriptionModels.IsParakeetOnnxAvailable(),
            _ => LiveTranscriptionModels.IsWhisperOnnxAvailable(settings.WhisperModel),
        };
        if (alreadyDownloaded) return true;

        var modelDisplayName = settings.SttBackend == SttBackend.Parakeet
            ? "Parakeet TDT v3"
            : TranscriptionService.GetModelName(settings.WhisperModel);

        var userCancelCts = new CancellationTokenSource();
        var dialogCloseCts = CancellationTokenSource.CreateLinkedTokenSource(userCancelCts.Token);
        var progress = new Progress<ModelDownloadProgress>();

        try
        {
            var downloadTask = settings.SttBackend == SttBackend.Parakeet
                ? _transcriptionService.DownloadParakeetModelAsync(progress, userCancelCts.Token)
                : _transcriptionService.DownloadModelAsync(settings.WhisperModel, progress, userCancelCts.Token);

            var dialogTask = _dialogService.ShowModelDownloadDialogAsync(
                modelDisplayName, progress, dialogCloseCts.Token);

            try
            {
                await downloadTask.ConfigureAwait(true);
                return true;
            }
            catch (OperationCanceledException) when (userCancelCts.IsCancellationRequested)
            {
                _snackbarService.Show("Cancelled", "Model download was cancelled",
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                return false;
            }
            finally
            {
                dialogCloseCts.Cancel();
                try { await dialogTask.ConfigureAwait(true); } catch { /* dialog already hidden */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model download failed");
            _snackbarService.Show("Error", $"Model download failed: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
            return false;
        }
        finally
        {
            dialogCloseCts.Dispose();
            userCancelCts.Dispose();
        }
    }

    private void CleanupAudioFile(string? audioFilePath)
    {
        if (audioFilePath is null || !File.Exists(audioFilePath))
            return;

        try
        {
            File.Delete(audioFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary audio file: {FilePath}", audioFilePath);
        }
    }
}
