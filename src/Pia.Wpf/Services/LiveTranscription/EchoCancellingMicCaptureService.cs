using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// The microphone the pipeline asks for: echo-cancelled where Windows can do it, plain otherwise.
/// A machine without the Voice Capture DSP, without a render endpoint, or with a driver the DSP will
/// not open must still transcribe — losing echo cancellation is a degradation, not a failure.
///
/// <para>Hands out the chosen source's own reader rather than re-publishing through a second channel:
/// another <c>DropOldest</c> stage would silently shorten the mic's sample-counted clock relative to the
/// loopback side, and that clock is what cross-channel echo detection compares.</para>
/// </summary>
public sealed class EchoCancellingMicCaptureService : IAudioCaptureSource
{
    private readonly Func<IAudioCaptureSource> _echoCancelling;
    private readonly Func<IAudioCaptureSource> _plain;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<EchoCancellingMicCaptureService> _logger;

    private IAudioCaptureSource? _active;

    public EchoCancellingMicCaptureService(
        Func<IAudioCaptureSource> echoCancelling,
        Func<IAudioCaptureSource> plain,
        ISettingsService settingsService,
        ILogger<EchoCancellingMicCaptureService> logger)
    {
        _echoCancelling = echoCancelling;
        _plain = plain;
        _settingsService = settingsService;
        _logger = logger;
    }

    public int SampleRate => _active?.SampleRate ?? 16000;
    public bool IsRunning => _active?.IsRunning ?? false;
    public DateTimeOffset? StartedAt => _active?.StartedAt;

    /// <summary>Which source won is only known once <see cref="StartAsync"/> has run.</summary>
    public ChannelReader<float[]> Reader =>
        _active?.Reader ?? throw new InvalidOperationException("Mic capture has not been started");

    /// <summary>True once started with the Windows echo canceller actually in the path.</summary>
    public bool IsEchoCancelled { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_active is not null) throw new InvalidOperationException("Mic capture already running");

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        if (!settings.MicEchoCancellation)
        {
            _logger.LogInformation("Echo cancellation is switched off; using the plain microphone");
            _active = _plain();
            await _active.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var candidate = _echoCancelling();
        try
        {
            await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
            _active = candidate;
            IsEchoCancelled = true;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogWarning(
                ex, "Echo-cancelling mic capture unavailable; falling back to the plain microphone. " +
                    "The far end may be re-recorded through the speakers");
            await SafeDisposeAsync(candidate).ConfigureAwait(false);

            _active = _plain();
            await _active.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _active?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    private async Task SafeDisposeAsync(IAudioCaptureSource source)
    {
        try { await source.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Discarding an unusable mic source threw"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_active is null) return;

        await SafeDisposeAsync(_active).ConfigureAwait(false);
        _active = null;
    }
}
