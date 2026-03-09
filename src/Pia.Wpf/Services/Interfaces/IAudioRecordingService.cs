namespace Pia.Services.Interfaces;

public interface IAudioRecordingService
{
    bool IsRecording { get; }

    Task StartRecordingAsync(CancellationToken cancellationToken = default);
    Task<string> StopRecordingAsync(CancellationToken cancellationToken = default);

    bool HasAudioContent(string audioFilePath, float silenceThreshold = 0.01f);

    event EventHandler<string>? RecordingCompleted;
    event EventHandler<float>? AudioLevelChanged;
}
