using System.Threading.Channels;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Pipeline-internal contract: a 16 kHz mono Float32 PCM source. Not a public DI service —
/// concrete instances are owned by <c>LiveMeetingService</c> for the duration of one
/// session and disposed via the orchestrator's stop/dispose chain.
/// </summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
    int SampleRate { get; }
    bool IsRunning { get; }
    ChannelReader<float[]> Reader { get; }

    /// <summary>
    /// Wall clock at which sample 0 was captured, so a segment's sample offset can be turned back into
    /// speech time. Null when the source does not track it — callers must fall back to arrival time.
    /// </summary>
    DateTimeOffset? StartedAt => null;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
