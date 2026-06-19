using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.LiveTranscription;

public sealed class LiveMeetingService : ILiveMeetingService, IAsyncDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LiveMeetingService> _logger;

    private readonly Channel<TranscriptUtterance> _utterances;
    private LiveMeetingState _state = LiveMeetingState.Idle;
    private readonly object _stateLock = new();

    private IAudioCaptureSource? _micSource;
    private IAudioCaptureSource? _loopbackSource;
    private LiveTranscriptionEngineService? _micEngine;
    private LiveTranscriptionEngineService? _loopbackEngine;
    private ITranscriptionEngine? _transcriptionEngine;

    public LiveMeetingState State
    {
        get { lock (_stateLock) return _state; }
    }

    public event EventHandler<LiveMeetingState>? StateChanged;
    public event EventHandler<SpeakingChangedEventArgs>? SpeakingChanged;

    public ChannelReader<TranscriptUtterance> Utterances => _utterances.Reader;

    public LiveMeetingService(
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LiveMeetingService>();
        _utterances = UtteranceChannel.CreateBounded();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state is LiveMeetingState.Running or LiveMeetingState.Starting)
                throw new InvalidOperationException($"Cannot start while {_state}");
        }
        TransitionState(LiveMeetingState.Starting);

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);

            var sileroPath = await LiveTranscriptionModels
                .EnsureSileroVadAsync(_httpClientFactory, _logger, cancellationToken)
                .ConfigureAwait(false);

            _transcriptionEngine = await TranscriptionEngineFactory
                .CreateAsync(settings, _httpClientFactory, downloadProgress: null, _logger, cancellationToken)
                .ConfigureAwait(false);

            _micSource = new MicAudioCaptureService(_loggerFactory.CreateLogger<MicAudioCaptureService>());
            _loopbackSource = new LoopbackAudioCaptureService(_loggerFactory.CreateLogger<LoopbackAudioCaptureService>());

            await _micSource.StartAsync(cancellationToken).ConfigureAwait(false);
            await _loopbackSource.StartAsync(cancellationToken).ConfigureAwait(false);

            _micEngine = new LiveTranscriptionEngineService(
                TranscriptSpeaker.You,
                _micSource,
                sileroPath,
                _transcriptionEngine,
                _utterances.Writer,
                _loggerFactory.CreateLogger<LiveTranscriptionEngineService>());

            _loopbackEngine = new LiveTranscriptionEngineService(
                TranscriptSpeaker.Them,
                _loopbackSource,
                sileroPath,
                _transcriptionEngine,
                _utterances.Writer,
                _loggerFactory.CreateLogger<LiveTranscriptionEngineService>());

            _micEngine.IsSpeakingChanged += OnEngineSpeakingChanged;
            _loopbackEngine.IsSpeakingChanged += OnEngineSpeakingChanged;

            await _micEngine.StartAsync(cancellationToken).ConfigureAwait(false);
            await _loopbackEngine.StartAsync(cancellationToken).ConfigureAwait(false);

            TransitionState(LiveMeetingState.Running);
            _logger.LogInformation("Live meeting transcription started ({Backend})", settings.SttBackend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start live meeting service");
            await DisposeAllAsync().ConfigureAwait(false);
            TransitionState(LiveMeetingState.Error);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state is LiveMeetingState.Idle or LiveMeetingState.Stopping) return;
        }
        TransitionState(LiveMeetingState.Stopping);

        try
        {
            // Stop captures first so the reader loops drain naturally.
            if (_micSource is not null) await _micSource.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_loopbackSource is not null) await _loopbackSource.StopAsync(cancellationToken).ConfigureAwait(false);

            await DisposeAllAsync().ConfigureAwait(false);

            TransitionState(LiveMeetingState.Idle);
            _logger.LogInformation("Live meeting transcription stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping live meeting service");
            TransitionState(LiveMeetingState.Error);
            throw;
        }
    }

    private async Task DisposeAllAsync()
    {
        if (_micEngine is not null)
        {
            _micEngine.IsSpeakingChanged -= OnEngineSpeakingChanged;
            try { await _micEngine.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Mic engine dispose threw"); }
            _micEngine = null;
        }
        if (_loopbackEngine is not null)
        {
            _loopbackEngine.IsSpeakingChanged -= OnEngineSpeakingChanged;
            try { await _loopbackEngine.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Loopback engine dispose threw"); }
            _loopbackEngine = null;
        }
        if (_micSource is not null)
        {
            try { await _micSource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Mic source dispose threw"); }
            _micSource = null;
        }
        if (_loopbackSource is not null)
        {
            try { await _loopbackSource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Loopback source dispose threw"); }
            _loopbackSource = null;
        }
        if (_transcriptionEngine is not null)
        {
            try { await _transcriptionEngine.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Transcription engine dispose threw"); }
            _transcriptionEngine = null;
        }
    }

    private void OnEngineSpeakingChanged(object? sender, bool isSpeaking)
    {
        if (sender is not LiveTranscriptionEngineService engine) return;
        var handler = SpeakingChanged;
        if (handler is null) return;
        try { handler.Invoke(this, new SpeakingChangedEventArgs(engine.Speaker, isSpeaking)); }
        catch (Exception ex) { _logger.LogError(ex, "SpeakingChanged subscriber threw"); }
    }

    private void TransitionState(LiveMeetingState newState)
    {
        EventHandler<LiveMeetingState>? handler;
        lock (_stateLock)
        {
            if (_state == newState) return;
            _state = newState;
            handler = StateChanged;
        }
        handler?.Invoke(this, newState);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _utterances.Writer.TryComplete();
    }
}
