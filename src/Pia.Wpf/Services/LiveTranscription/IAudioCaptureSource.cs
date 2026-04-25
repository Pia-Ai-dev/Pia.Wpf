using System.Threading.Channels;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Pipeline-internal contract: a 16 kHz mono Float32 PCM source. Not a public DI service —
/// concrete instances are owned by <see cref="LiveMeetingService"/> for the duration of one
/// session and disposed via the orchestrator's stop/dispose chain.
/// </summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
    int SampleRate { get; }
    bool IsRunning { get; }
    ChannelReader<float[]> Reader { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
