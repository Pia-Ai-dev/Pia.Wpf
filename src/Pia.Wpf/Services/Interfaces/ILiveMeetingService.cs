using System.Threading.Channels;
using Pia.Models;

namespace Pia.Services.Interfaces;

public enum LiveMeetingState
{
    Idle,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>
/// Carries a per-speaker speech-activity transition. <see cref="IsSpeaking"/> is true on
/// VAD open and false on VAD close.
/// </summary>
public sealed class SpeakingChangedEventArgs : EventArgs
{
    public TranscriptSpeaker Speaker { get; }
    public bool IsSpeaking { get; }

    public SpeakingChangedEventArgs(TranscriptSpeaker speaker, bool isSpeaking)
    {
        Speaker = speaker;
        IsSpeaking = isSpeaking;
    }
}

/// <summary>
/// Orchestrates the live transcription session. Owns one mic pipeline + one loopback
/// pipeline; merges their utterance streams into a single reader the UI consumes.
/// </summary>
public interface ILiveMeetingService
{
    LiveMeetingState State { get; }
    event EventHandler<LiveMeetingState>? StateChanged;

    /// <summary>
    /// Raised when the VAD opens or closes a speech segment for either the mic ("you")
    /// or the loopback ("them") pipeline. Fired on the audio reader thread; subscribers
    /// must marshal to the UI thread themselves.
    /// </summary>
    event EventHandler<SpeakingChangedEventArgs>? SpeakingChanged;

    /// <summary>
    /// Reader of the merged utterance stream. The reader instance is stable for the
    /// lifetime of the service — engines write into the same channel across all
    /// start/stop cycles. The channel is completed only on <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    ChannelReader<TranscriptUtterance> Utterances { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
