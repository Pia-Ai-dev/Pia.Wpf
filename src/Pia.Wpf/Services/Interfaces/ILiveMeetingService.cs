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
/// Orchestrates the live transcription session. Owns one mic pipeline + one loopback
/// pipeline; merges their utterance streams into a single reader the UI consumes.
/// </summary>
public interface ILiveMeetingService
{
    LiveMeetingState State { get; }
    event EventHandler<LiveMeetingState>? StateChanged;

    /// <summary>
    /// Reader of the merged utterance stream. The reader instance is stable for the
    /// lifetime of the service — engines write into the same channel across all
    /// start/stop cycles. The channel is completed only on <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    ChannelReader<TranscriptUtterance> Utterances { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename a speaker label so future utterances from the same voice carry
    /// <paramref name="newLabel"/>. Returns true on success, false if no matching speaker
    /// exists or diarization isn't running.
    /// </summary>
    bool RenameSpeaker(string oldLabel, string newLabel);
}
