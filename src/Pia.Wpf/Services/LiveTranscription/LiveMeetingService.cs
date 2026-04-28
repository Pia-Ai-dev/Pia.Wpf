using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;

namespace Pia.Services.LiveTranscription;

public sealed class LiveMeetingService : ILiveMeetingService, IAsyncDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LiveMeetingService> _logger;

    private readonly Channel<TranscriptUtterance> _rawUtterances;
    private readonly Channel<TranscriptUtterance> _utterances;
    private Task? _forwardLoop;
    private CancellationTokenSource? _forwardCts;
    private PeriodicTimer? _timeoutSweepTimer;
    private Task? _timeoutSweepLoop;
    private readonly HashSet<string> _knownSpeakers = new(StringComparer.Ordinal);

    private readonly IConsentStateManager _consentMgr;
    private readonly IConsentClassifier _consentClassifier;
    private readonly IConsentGate _consentGate;
    private readonly IConsentAuditLog _auditLog;
    private readonly ITtsService _tts;
    private readonly PerSpeakerRingBufferRegistry _preConsentBuffers;
    private readonly IPostSttDefenseFilter _postSttFilter;
    private readonly IConsentOrchestratorFactory? _orchestratorFactory;
    private readonly IBlocklistFilter? _blocklistFilter;
    private readonly HashSet<string> _pendingConsentFlows = new(StringComparer.Ordinal);
    // Built per session in StartAsync from the security profile that was active at start.
    // Mid-session profile changes are deliberately ignored (would invalidate already-collected
    // consent) — the warning is logged in ConsentOrchestratorFactory.
    private IConsentOrchestrator? _orchestrator;

    private LiveMeetingState _state = LiveMeetingState.Idle;
    private readonly object _stateLock = new();

    private IAudioCaptureSource? _micSource;
    private IAudioCaptureSource? _loopbackSource;
    private LiveTranscriptionEngineService? _micEngine;
    private LiveTranscriptionEngineService? _loopbackEngine;
    private ITranscriptionEngine? _transcriptionEngine;
    private ISpeakerIdentificationService? _speakerId;
    private string? _vadModelPath;

    public LiveMeetingState State
    {
        get { lock (_stateLock) return _state; }
    }

    public event EventHandler<LiveMeetingState>? StateChanged;
    public event EventHandler<SpeakingChangedEventArgs>? SpeakingChanged;

    public ChannelReader<TranscriptUtterance> Utterances => _utterances.Reader;

    public bool RenameSpeaker(string oldLabel, string newLabel)
    {
        var renamed = _speakerId?.Rename(oldLabel, newLabel) ?? false;
        if (renamed) _consentMgr.Rename(oldLabel, newLabel);
        return renamed;
    }

    public LiveMeetingService(
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IConsentStateManager consentMgr,
        IConsentClassifier consentClassifier,
        IConsentGate consentGate,
        IConsentAuditLog auditLog,
        ITtsService tts,
        PerSpeakerRingBufferRegistry preConsentBuffers,
        IPostSttDefenseFilter postSttFilter,
        IConsentOrchestratorFactory? orchestratorFactory = null,
        IBlocklistFilter? blocklistFilter = null)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LiveMeetingService>();
        _rawUtterances = CreateUtterancesChannel();
        _utterances = CreateUtterancesChannel();
        _consentMgr = consentMgr;
        _consentClassifier = consentClassifier;
        _consentGate = consentGate;
        _auditLog = auditLog;
        _tts = tts;
        _preConsentBuffers = preConsentBuffers;
        _postSttFilter = postSttFilter;
        _orchestratorFactory = orchestratorFactory;
        _blocklistFilter = blocklistFilter;

        _consentMgr.StateChanged += OnConsentStateChanged;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            // Idempotent: if we're already prepared (or further along), return immediately.
            // Only transition Idle -> Preparing here; concurrent callers wait their turn.
            if (_state is not LiveMeetingState.Idle and not LiveMeetingState.Error) return;
        }
        TransitionState(LiveMeetingState.Preparing);

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);

            // Heavy model loads — these are the slow operations the disclaimer dialog hides
            // from the user. Source / engine / VAD detector construction happens in
            // StartAsync to keep the audio-pipeline runtime topology identical to the
            // pre-warmup behavior (i.e., engines and their VAD detectors are fresh per
            // session, not pre-instantiated and left idle through the disclaimer).
            _transcriptionEngine = await TranscriptionEngineFactory
                .CreateAsync(settings, _httpClientFactory, downloadProgress: null, _logger, cancellationToken)
                .ConfigureAwait(false);

            _vadModelPath = await LiveTranscriptionModels
                .EnsureSileroVadAsync(_httpClientFactory, progress: null, _logger, cancellationToken)
                .ConfigureAwait(false);

            if (settings.EnableLoopbackDiarization)
            {
                var speakerModelPath = await LiveTranscriptionModels
                    .EnsureSpeakerEmbeddingAsync(_httpClientFactory, progress: null, _logger, cancellationToken)
                    .ConfigureAwait(false);
                _speakerId = new SpeakerIdentificationService(
                    speakerModelPath,
                    settings.SpeakerEmbeddingThreshold,
                    _loggerFactory.CreateLogger<SpeakerIdentificationService>());
            }

            TransitionState(LiveMeetingState.Prepared);
            _logger.LogInformation("Live meeting transcription prepared ({Backend})", settings.SttBackend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare live meeting service");
            await DisposeAllAsync().ConfigureAwait(false);
            TransitionState(LiveMeetingState.Error);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state is LiveMeetingState.Running or LiveMeetingState.Starting)
                throw new InvalidOperationException($"Cannot start while {_state}");
        }

        // Allow callers that skipped the explicit warmup step to fall through.
        if (State is LiveMeetingState.Idle or LiveMeetingState.Error)
            await PrepareAsync(cancellationToken).ConfigureAwait(false);

        TransitionState(LiveMeetingState.Starting);

        try
        {
            // Construct sources + engines fresh for this session (matches original audio
            // pipeline lifecycle), then open the audio devices and start the reader loops.
            // This is the privacy boundary: nothing was capturing audio before the
            // source.StartAsync calls below.
            _micSource = new MicAudioCaptureService(_loggerFactory.CreateLogger<MicAudioCaptureService>());
            _loopbackSource = new LoopbackAudioCaptureService(_loggerFactory.CreateLogger<LoopbackAudioCaptureService>());

            await _micSource.StartAsync(cancellationToken).ConfigureAwait(false);
            await _loopbackSource.StartAsync(cancellationToken).ConfigureAwait(false);

            _micEngine = new LiveTranscriptionEngineService(
                TranscriptSpeaker.You,
                _micSource,
                _transcriptionEngine!,
                _vadModelPath!,
                _rawUtterances.Writer,
                _loggerFactory.CreateLogger<LiveTranscriptionEngineService>());

            _loopbackEngine = new LiveTranscriptionEngineService(
                TranscriptSpeaker.Them,
                _loopbackSource,
                _transcriptionEngine!,
                _vadModelPath!,
                _rawUtterances.Writer,
                _loggerFactory.CreateLogger<LiveTranscriptionEngineService>(),
                _speakerId,
                _consentGate,
                _preConsentBuffers,
                _consentMgr,
                _blocklistFilter);

            _micEngine.IsSpeakingChanged += OnEngineSpeakingChanged;
            _loopbackEngine.IsSpeakingChanged += OnEngineSpeakingChanged;

            // Build the orchestrator from the security profile in effect at session start.
            // The orchestrator is held for the whole session — profile changes mid-session
            // are logged but not applied.
            _orchestrator = _orchestratorFactory?.CreateForCurrentProfile();
            _orchestrator?.RegisterEngine(_micEngine);
            _orchestrator?.RegisterEngine(_loopbackEngine);

            _forwardCts = new CancellationTokenSource();
            _forwardLoop = Task.Run(() => RunForwardLoopAsync(_forwardCts.Token));

            _timeoutSweepTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            _timeoutSweepLoop = Task.Run(() => RunTimeoutSweepAsync(_timeoutSweepTimer));

            await _micEngine.StartAsync(cancellationToken).ConfigureAwait(false);
            await _loopbackEngine.StartAsync(cancellationToken).ConfigureAwait(false);

            TransitionState(LiveMeetingState.Running);
            _logger.LogInformation("Live meeting transcription started");
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
        bool wasRunning;
        lock (_stateLock)
        {
            if (_state is LiveMeetingState.Idle or LiveMeetingState.Stopping) return;
            wasRunning = _state is LiveMeetingState.Running or LiveMeetingState.Starting;
        }
        TransitionState(LiveMeetingState.Stopping);

        try
        {
            // Stop captures first so the reader loops drain naturally — but only if we
            // ever started capturing. From Prepared we can dispose without StopAsync calls.
            if (wasRunning)
            {
                if (_micSource is not null) await _micSource.StopAsync(cancellationToken).ConfigureAwait(false);
                if (_loopbackSource is not null) await _loopbackSource.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            await DisposeAllAsync().ConfigureAwait(false);

            TransitionState(LiveMeetingState.Idle);
            // Spec §6.6: any non-zero count indicates a pre-STT gate bug — surface it.
            var postSttDrops = _postSttFilter.DropCount;
            if (postSttDrops > 0)
                _logger.LogWarning("Live meeting stopped — post-STT defense drops: {Count} (gate-bug indicator)", postSttDrops);
            else
                _logger.LogInformation("Live meeting transcription stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping live meeting service");
            TransitionState(LiveMeetingState.Error);
            throw;
        }
    }

    private async Task RunForwardLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var utt in _rawUtterances.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await ProcessUtteranceAsync(utt, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Forward-loop processing threw"); }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.LogError(ex, "Forward loop failed"); }
    }

    internal async Task ProcessUtteranceAsync(TranscriptUtterance utt, CancellationToken cancellationToken)
    {
        // Loopback engine emits utterances with a SpeakerLabel; mic-side has none and is
        // always trusted (the local user is by definition consenting to their own recording).
        if (utt.SpeakerLabel is null)
        {
            await _utterances.Writer.WriteAsync(utt, cancellationToken).ConfigureAwait(false);
            return;
        }

        var label = utt.SpeakerLabel;
        var firstSeen = false;
        lock (_knownSpeakers) firstSeen = _knownSpeakers.Add(label);
        if (firstSeen)
        {
            // Strategy A pauses every running engine (in the orchestrator) before the
            // prompt flow runs; Strategy B is a no-op so the existing Granted speakers
            // keep producing utterances while the new speaker waits for consent.
            if (_orchestrator is not null)
            {
                try { await _orchestrator.OnNewSpeakerJoinedAsync(label, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Orchestrator OnNewSpeakerJoinedAsync threw for {Label}", label); }
            }
            StartConsentFlowAsync(label);
        }

        if (utt.Channel == TranscriptChannel.ConsentClassification)
        {
            await HandleConsentReplyAsync(label, utt.Text, cancellationToken).ConfigureAwait(false);
            return; // never forward consent dialog content to the user transcript
        }

        if (_postSttFilter.Evaluate(utt) == PostSttFilterDecision.DropAndAudit) return;

        await _utterances.Writer.WriteAsync(utt, cancellationToken).ConfigureAwait(false);
    }

    private void StartConsentFlowAsync(string label)
    {
        lock (_pendingConsentFlows)
        {
            if (!_pendingConsentFlows.Add(label)) return;
        }

        _consentMgr.GetOrCreate(label);
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "SPEAKER_JOINED", label, null));
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "STRATEGY_B_PENDING_CONSENT", label, null));

        _ = Task.Run(async () =>
        {
            try
            {
                var prompt = ConsentPromptTemplates.InitialConsentLocalOnlyDe;
                try { await _tts.SpeakAsync(prompt.Text).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "TTS playback failed for consent prompt"); }

                _consentMgr.MarkPrompted(label);
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, "CONSENT_PROMPTED", label,
                    new Dictionary<string, object?>
                    {
                        ["prompt_id"] = prompt.Id,
                        ["prompt_hash"] = prompt.VersionHash,
                        ["language"] = prompt.Language,
                    }));
            }
            catch (Exception ex) { _logger.LogError(ex, "Consent flow for {Label} failed", label); }
            finally
            {
                lock (_pendingConsentFlows) _pendingConsentFlows.Remove(label);
            }
        });
    }

    private async Task HandleConsentReplyAsync(string label, string transcriptText, CancellationToken cancellationToken)
    {
        var prompt = ConsentPromptTemplates.InitialConsentLocalOnlyDe;
        var classification = await _consentClassifier
            .ClassifyAsync(transcriptText, prompt.Text, cancellationToken)
            .ConfigureAwait(false);
        _consentMgr.RecordClassification(
            label, classification, transcriptText, prompt.VersionHash, prompt.Text, sttModelId: "live-engine");
    }

    private void OnConsentStateChanged(object? sender, ConsentStateChangedEventArgs e)
    {
        var eventType = e.NewState switch
        {
            ConsentState.Granted => "CONSENT_GRANTED",
            ConsentState.Denied => "CONSENT_DENIED",
            ConsentState.Ambiguous => "CONSENT_AMBIGUOUS",
            ConsentState.Timeout => "CONSENT_TIMEOUT",
            ConsentState.Revoked => "CONSENT_REVOKED",
            ConsentState.Prompted => "CONSENT_PROMPTED_TRANSITION",
            _ => null,
        };
        if (eventType is null) return;

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, eventType, e.SpeakerLabel,
            new Dictionary<string, object?> { ["from"] = e.OldState.ToString() }));

        // Spec §3.9 Strategy B point 5: pre-consent audio MUST be discarded on grant —
        // never replayed retroactively. Make the discard intent visible.
        if (e.NewState == ConsentState.Granted)
        {
            var discarded = _preConsentBuffers.Drain(e.SpeakerLabel);
            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(), DateTimeOffset.UtcNow, "PRE_CONSENT_BUFFER_DISCARDED", e.SpeakerLabel,
                new Dictionary<string, object?>
                {
                    ["sample_count"] = discarded.Length,
                    ["duration_ms"] = discarded.Length * 1000 / 16000,
                }));
        }

        // Spec §3 CLARIFICATION_AMBIGUOUS: re-prompt when a reply lands in the ambiguous band.
        if (e.NewState == ConsentState.Ambiguous)
        {
            _ = Task.Run(async () =>
            {
                var clar = ConsentPromptTemplates.ClarificationAmbiguousDe;
                try { await _tts.SpeakAsync(clar.Text).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "TTS clarification playback failed"); }
                _consentMgr.MarkPrompted(e.SpeakerLabel);
            });
        }
    }

    private async Task RunTimeoutSweepAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                _consentMgr.SweepTimeouts();
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "Timeout sweep loop failed"); }
    }

    private async Task DisposeAllAsync()
    {
        if (_timeoutSweepTimer is not null)
        {
            try { _timeoutSweepTimer.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Timeout sweep timer dispose threw"); }
            _timeoutSweepTimer = null;
        }
        if (_timeoutSweepLoop is not null)
        {
            try { await _timeoutSweepLoop.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Timeout sweep loop wait threw"); }
            _timeoutSweepLoop = null;
        }

        if (_micEngine is not null)
        {
            _micEngine.IsSpeakingChanged -= OnEngineSpeakingChanged;
            try { _orchestrator?.UnregisterEngine(_micEngine); } catch { /* swallow on shutdown */ }
            try { await _micEngine.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Mic engine dispose threw"); }
            _micEngine = null;
        }
        if (_loopbackEngine is not null)
        {
            _loopbackEngine.IsSpeakingChanged -= OnEngineSpeakingChanged;
            try { _orchestrator?.UnregisterEngine(_loopbackEngine); } catch { /* swallow on shutdown */ }
            try { await _loopbackEngine.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Loopback engine dispose threw"); }
            _loopbackEngine = null;
        }
        if (_orchestrator is IDisposable disposableOrch)
        {
            try { disposableOrch.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Orchestrator dispose threw"); }
        }
        _orchestrator = null;
        // Spec §3.9: blocklist is session-scoped (Art. 9 DSGVO; cross-session persistence is
        // Phase 5). Reset on every shutdown so a denied speaker is re-evaluated next meeting.
        if (_blocklistFilter is BlocklistFilter resettable)
        {
            try { resettable.Reset(); } catch (Exception ex) { _logger.LogWarning(ex, "Blocklist reset threw"); }
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
        if (_speakerId is not null)
        {
            try { _speakerId.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Speaker identification service dispose threw"); }
            _speakerId = null;
        }

        // Engines are gone, so the raw channel will receive no more writes — complete it
        // so the forward loop drains and exits. Public utterance writer is left open so
        // existing readers can continue consuming until DisposeAsync.
        _rawUtterances.Writer.TryComplete();
        if (_forwardLoop is not null)
        {
            try { await _forwardLoop.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Forward loop wait threw"); }
            _forwardLoop = null;
        }
        if (_forwardCts is not null)
        {
            _forwardCts.Dispose();
            _forwardCts = null;
        }
        lock (_knownSpeakers) _knownSpeakers.Clear();
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

    private static Channel<TranscriptUtterance> CreateUtterancesChannel()
        => Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _consentMgr.StateChanged -= OnConsentStateChanged;
        _utterances.Writer.TryComplete();
    }
}
