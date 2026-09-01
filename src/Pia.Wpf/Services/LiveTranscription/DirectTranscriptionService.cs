using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Orchestrates a direct (microphone + system-audio) transcription session: two
/// <see cref="LiveTranscriptionEngineService"/> instances (mic = "me", no diarizer; loopback = manual
/// diarizer, never adaptive — see design §3.4) writing into a private, per-start raw channel, whose
/// sole reader is a session-scoped <see cref="ConsentForwardLoop"/> — THE privacy boundary. Only
/// speech that the forward loop emits ever reaches <see cref="Utterances"/>.
///
/// <para>Modelled on <c>MeetingAttendeeService</c>: every IO/native construction sits behind an
/// injectable delegate so the state machine can be exercised with substitutes (see the internal seam
/// constructor). The public constructor wires production defaults.</para>
/// </summary>
public sealed class DirectTranscriptionService : IDirectTranscriptionService
{
    private readonly ISettingsService _settingsService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DirectTranscriptionService> _logger;
    private readonly IConsentStateManager _consentStateManager;
    private readonly INamedConsentClassifier _consentClassifier;
    private readonly IConsentAuditLog _auditLog;
    private readonly IConsentEvidenceStore _evidenceStore;

    // ---- Injected seams ---------------------------------------------------------------------------
    private readonly Func<CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService SpeakerId, string SttModelId)>> _createTranscription;
    private readonly Func<IAudioCaptureSource> _micSourceFactory;
    private readonly Func<IAudioCaptureSource> _loopbackSourceFactory;
    private readonly Func<TranscriptSpeaker, IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, CancellationToken, Task<IAsyncDisposable>> _engineServiceFactory;

    // Public channel: stable for the whole service lifetime, completed only in DisposeAsync so a
    // consumer's reader survives every Stop/Start cycle unchanged.
    private readonly Channel<TranscriptUtterance> _publicChannel;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _prepareGate = new(1, 1);

    // Serializes teardown only (not the whole Start/Stop body), mirroring MeetingAttendeeService: a
    // failed StartAsync and a racing StopAsync both call teardown, and each owned resource must be
    // disposed exactly once.
    private readonly SemaphoreSlim _disposeGate = new(1, 1);

    /// <summary>
    /// Serializes the whole of <see cref="StartAsync"/>, <see cref="StopAsync"/> and the session teardown
    /// in <see cref="EndSessionAsync"/> against each other. A plain state check is not enough, and every
    /// one of these three overlaps was a real failure mode:
    /// <list type="bullet">
    /// <item>Two overlapping starts each built a full run; the loser's assignments overwrote the winner's
    /// fields, orphaning an already-recording microphone that no teardown path could ever reach.</item>
    /// <item>A stop that arrived while a start was still constructing tore down the sources and completed
    /// the raw channel, after which the start unconditionally reported <c>Running</c> over a dead
    /// pipeline.</item>
    /// <item><see cref="EndSessionAsync"/> read "already Stopping" as "already torn down" and disposed the
    /// shared sherpa engine and the native ONNX diarizer while the engines were still draining their
    /// trailing segments through them — a native use-after-free, not a managed exception.</item>
    /// </list>
    /// </summary>
    private readonly SemaphoreSlim _startStopGate = new(1, 1);

    /// <summary>Cancels an in-flight <see cref="StartAsync"/> so a stop does not have to wait out a model
    /// download before it can claim the gate. Linked to the start caller's own token.</summary>
    private CancellationTokenSource? _startCts;

    /// <summary>Cancels an in-flight <see cref="PrepareAsync"/> (typically a background warmup) so a session
    /// end does not have to wait out a first-run model download before it can dispose the natives.</summary>
    private CancellationTokenSource? _prepareCts;

    private DirectTranscriptionState _state = DirectTranscriptionState.Idle;
    private bool _disposed;

    // ---- Session-scoped (survive a Stop/Start pause, cleared only by EndSessionAsync) --------------
    private string _sessionId = string.Empty;
    private string _sttModelId = string.Empty;
    private string? _vadModelPath;
    private ITranscriptionEngine? _transcriptionEngine;
    private ISpeakerIdentificationService? _speakerId;
    private ConsentForwardLoop? _forwardLoop;
    private EchoDetector? _echoDetector;

    // ---- Run-scoped (fresh every StartAsync, torn down every StopAsync) ----------------------------
    private Channel<TranscriptUtterance>? _rawChannel;
    private IAudioCaptureSource? _micSource;
    private IAudioCaptureSource? _loopbackSource;
    private IAsyncDisposable? _micEngine;
    private IAsyncDisposable? _loopbackEngine;
    private Task? _forwardLoopTask;
    private CancellationTokenSource? _forwardCts;

    public DirectTranscriptionState State
    {
        get { lock (_stateLock) return _state; }
    }

    public ChannelReader<TranscriptUtterance> Utterances => _publicChannel.Reader;

    public event EventHandler<DirectTranscriptionState>? StateChanged;
    public event EventHandler<SpeakerConsentChangedEventArgs>? SpeakerConsentChanged;
    public event EventHandler<string>? SpeakerRegistered;
    public event EventHandler<TranscriptionSpeakingChangedEventArgs>? SpeakingChanged;
    public event EventHandler? ConsentSessionReset;

    /// <summary>Production constructor (used by DI). Wires default seams over the real dependencies.</summary>
    public DirectTranscriptionService(
        ISettingsService settingsService,
        IAssetDownloader assetDownloader,
        ILoggerFactory loggerFactory,
        IConsentStateManager consentStateManager,
        INamedConsentClassifier consentClassifier,
        IConsentAuditLog auditLog,
        IConsentEvidenceStore evidenceStore)
        : this(
            settingsService,
            loggerFactory,
            consentStateManager,
            consentClassifier,
            auditLog,
            evidenceStore,
            createTranscription: CreateProductionTranscriptionFactory(settingsService, assetDownloader, loggerFactory),
            micSourceFactory: () => CreateMicSource(settingsService, loggerFactory),
            loopbackSourceFactory: () => new LoopbackAudioCaptureService(loggerFactory.CreateLogger<LoopbackAudioCaptureService>()),
            engineServiceFactory: CreateEngineServiceFactory(loggerFactory))
    {
    }

    /// <summary>
    /// The production microphone: Windows' echo canceller where it works, the plain capture otherwise.
    /// Shared with the DEBUG audio-dump composition in <c>Bootstrapper</c> so a dumped session records
    /// the same mic signal the real one does.
    /// </summary>
    internal static IAudioCaptureSource CreateMicSource(ISettingsService settingsService, ILoggerFactory loggerFactory)
        => new EchoCancellingMicCaptureService(
            () => new WindowsAecMicCaptureService(loggerFactory.CreateLogger<WindowsAecMicCaptureService>()),
            () => new MicAudioCaptureService(loggerFactory.CreateLogger<MicAudioCaptureService>()),
            settingsService,
            loggerFactory.CreateLogger<EchoCancellingMicCaptureService>());

    /// <summary>
    /// Builds the real model/engine bootstrapper. Extracted so the DEBUG-only file-audio composition
    /// in <c>Bootstrapper</c> can reuse the exact same Silero/STT/diarizer construction instead of a
    /// second, divergent copy.
    /// </summary>
    internal static Func<CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService SpeakerId, string SttModelId)>> CreateProductionTranscriptionFactory(
        ISettingsService settingsService, IAssetDownloader assetDownloader, ILoggerFactory loggerFactory)
    {
        return async ct =>
        {
            var settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
            var log = loggerFactory.CreateLogger<DirectTranscriptionService>();

            var sileroPath = await LiveTranscriptionModels
                .EnsureSileroVadAsync(assetDownloader, log, ct).ConfigureAwait(false);
            var engine = await TranscriptionEngineFactory
                .CreateAsync(settings, assetDownloader, downloadProgress: null, log, ct).ConfigureAwait(false);

            // Speaker-model failure is FATAL here (unlike the Teams attendee's degrade-to-null):
            // without diarization there is no per-speaker consent gate, so a consent-gated session
            // must not silently degrade to "one anonymous speaker". Let this throw.
            var speakerModelPath = await LiveTranscriptionModels
                .EnsureSpeakerEmbeddingAsync(assetDownloader, log, ct).ConfigureAwait(false);

            // Always the MANUAL diarizer, regardless of settings.MeetingSmartSpeakerDetection: the
            // adaptive diarizer retroactively reassigns labels, which is unsound under a consent
            // gate (design §3.4) — a Granted label could be retroactively handed to an unconsented
            // speaker, or vice versa, and neither direction can be undone once text has been emitted.
            // ShouldUseAdaptiveDiarizer is a hardcoded `false` (see its doc); the guard below is
            // unreachable by construction and exists only so a future edit cannot silently wire the
            // adaptive diarizer back in without also touching (and breaking) its dedicated test.
            if (ShouldUseAdaptiveDiarizer(settings))
                throw new NotSupportedException("Direct transcription requires the manual speaker diarizer.");

            // maxSpeakers is deliberately 0 (unlimited) and NOT settings.MeetingMaxSpeakers: at the cap
            // the diarizer FORCE-ASSIGNS a new speaker's segment to its best existing match with no
            // similarity floor at all, so with the cap reached an unconsented speaker would come back
            // wearing a Granted label and the gate would emit their speech. That trade (bounded label
            // growth vs. a consent transfer) is acceptable for the Teams attendee, which has no
            // per-speaker consent gate; it is not acceptable here. Over-splitting is the accepted
            // limitation instead (design §5.4) — it fails closed.
            var speakerId = new SpeakerIdentificationService(
                speakerModelPath,
                settings.SpeakerEmbeddingThreshold,
                maxSpeakers: 0,
                loggerFactory.CreateLogger<SpeakerIdentificationService>());

            return (sileroPath, engine, (ISpeakerIdentificationService)speakerId, ComputeSttModelId(settings));
        };
    }

    /// <summary>Builds the real engine-service factory. Extracted for the same reason as
    /// <see cref="CreateProductionTranscriptionFactory"/>.</summary>
    internal static Func<TranscriptSpeaker, IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, CancellationToken, Task<IAsyncDisposable>> CreateEngineServiceFactory(
        ILoggerFactory loggerFactory)
    {
        return async (speaker, source, sileroPath, engine, sink, speakerId, ct) =>
        {
            var svc = new LiveTranscriptionEngineService(
                speaker,
                source,
                sileroPath,
                engine,
                sink,
                loggerFactory.CreateLogger<LiveTranscriptionEngineService>(),
                speakerId);
            await svc.StartAsync(ct).ConfigureAwait(false);
            return svc;
        };
    }

    /// <summary>
    /// Seam constructor used by tests. Every network/native construction sits behind a delegate so the
    /// state machine can be exercised with substitutes.
    /// </summary>
    internal DirectTranscriptionService(
        ISettingsService settingsService,
        ILoggerFactory loggerFactory,
        IConsentStateManager consentStateManager,
        INamedConsentClassifier consentClassifier,
        IConsentAuditLog auditLog,
        IConsentEvidenceStore evidenceStore,
        Func<CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService SpeakerId, string SttModelId)>> createTranscription,
        Func<IAudioCaptureSource> micSourceFactory,
        Func<IAudioCaptureSource> loopbackSourceFactory,
        Func<TranscriptSpeaker, IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, CancellationToken, Task<IAsyncDisposable>> engineServiceFactory)
    {
        _settingsService = settingsService;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DirectTranscriptionService>();
        _consentStateManager = consentStateManager;
        _consentClassifier = consentClassifier;
        _auditLog = auditLog;
        _evidenceStore = evidenceStore;

        _createTranscription = createTranscription;
        _micSourceFactory = micSourceFactory;
        _loopbackSourceFactory = loopbackSourceFactory;
        _engineServiceFactory = engineServiceFactory;

        _publicChannel = UtteranceChannel.CreateBounded();
    }

    // -------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state is DirectTranscriptionState.Prepared or DirectTranscriptionState.Running) return;
        }

        await _prepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Published so a session end can ABORT this prepare instead of merely waiting it out. The wait is
        // mandatory (a completing prepare must not assign live native handles into a session that is being
        // torn down), and on the first run it would otherwise be a whole model download long — with
        // EndSessionAsync reached from a synchronous Dispose on the UI thread, that is a frozen window.
        var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock) { _prepareCts = prepareCts; }

        try
        {
            // Re-check under the gate: a second caller that queued behind an in-flight prepare must
            // not repeat the work (or resubscribe the diarizer's SpeakerRegistered event).
            lock (_stateLock)
            {
                if (_state is DirectTranscriptionState.Prepared or DirectTranscriptionState.Running) return;
            }

            TransitionState(DirectTranscriptionState.Preparing);

            try
            {
                // Re-preparing (the Error/retry path) must release the PREVIOUS session's natives before
                // provisioning new ones. Without this, a failed StartAsync — an absent or exclusively-held
                // microphone is enough — left the session's sherpa recognizer and native diarizer alive in
                // state Error, and the retry assigned straight over the fields, leaking one native model
                // pair per retry with no teardown path able to reach them again.
                if (_transcriptionEngine is not null || _speakerId is not null || _forwardLoop is not null)
                    await TeardownSessionAsync().ConfigureAwait(false);

                var (sileroPath, engine, speakerId, sttModelId) = await _createTranscription(prepareCts.Token)
                    .ConfigureAwait(false);

                _vadModelPath = sileroPath;
                _transcriptionEngine = engine;
                _speakerId = speakerId;
                _sttModelId = sttModelId;
                _speakerId.SpeakerRegistered += OnSpeakerRegistered;

                _sessionId = Guid.NewGuid().ToString("N");

                // The consent map MUST be cleared here: the diarizer built above is brand new, so its
                // "Speaker 1" is a different voice from the previous one's, and carrying a grant over
                // would hand one person's consent to another. But clearing it silently was its own defect
                // on the Error-retry path — the UI kept showing speakers as consented while the gate had
                // reverted them to Unknown and was dropping their speech. Announce it.
                _consentStateManager.ResetSession();
                RaiseConsentSessionReset();

                // Per session, like the diarizer: it remembers the far end's recent speech, and carrying
                // that across sessions would let one meeting's audio explain away the next one's.
                _echoDetector = new EchoDetector();

                _forwardLoop = new ConsentForwardLoop(
                    _consentStateManager,
                    _consentClassifier,
                    _auditLog,
                    _evidenceStore,
                    _loggerFactory.CreateLogger<ConsentForwardLoop>(),
                    _echoDetector);
                _forwardLoop.SpeakerConsentChanged += OnForwardLoopSpeakerConsentChanged;

                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.SessionStarted, null, null));

                TransitionState(DirectTranscriptionState.Prepared);
                _logger.LogInformation("Direct transcription prepared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare direct transcription session");
                await TeardownSessionAsync().ConfigureAwait(false);
                TransitionState(DirectTranscriptionState.Error);
                throw;
            }
        }
        finally
        {
            lock (_stateLock) { _prepareCts = null; }
            prepareCts.Dispose();
            _prepareGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Claim the gate FIRST, then check the state. Checking first and claiming later is what let two
        // overlapping starts both pass (the guard released the state lock before the seconds-long
        // PrepareAsync, and the Resume button is on screen during it).
        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            // Starting/Stopping are impossible while the gate is held; Preparing means a background
            // warmup is mid-flight, and PrepareAsync's own gate will serialize us behind it.
            if (_state is DirectTranscriptionState.Running)
                throw new InvalidOperationException($"Cannot start while {_state}");
        }

        // A stop that arrives while this start is still building can cancel it here rather than blocking
        // behind a model download. Linked to the caller's token so an external cancel still works.
        var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock) { _startCts = startCts; }
        var startToken = startCts.Token;

        try
        {
            if (State is DirectTranscriptionState.Idle or DirectTranscriptionState.Error)
                await PrepareAsync(startToken).ConfigureAwait(false);

            TransitionState(DirectTranscriptionState.Starting);

            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            var context = new ConsentSessionContext(_sessionId, _sttModelId, settings.TargetSpeechLanguage);

            // Every resource below is assigned to its instance field IMMEDIATELY after creation (not
            // batched at the end): if a later step throws, the catch below calls TeardownRunAsync,
            // which only tears down what it finds in the instance fields. Batching the assignments
            // until "everything succeeded" would leak a mid-start failure's already-created sources,
            // engines, or forward-loop task, because teardown would see nothing to dispose.

            // Fresh raw channel every start (fixes D2: the old branch's raw channel was a readonly
            // field completed inside the same teardown that StopAsync called, so a second start wrote
            // into an already-closed writer and produced nothing).
            var rawChannel = UtteranceChannel.CreateBounded();
            _rawChannel = rawChannel;

            var forwardCts = new CancellationTokenSource();
            _forwardCts = forwardCts;
            _forwardLoopTask = Task.Run(
                () => _forwardLoop!.RunAsync(context, rawChannel.Reader, _publicChannel.Writer, RenameSpeaker, forwardCts.Token));

            // Sources are single-use (LoopbackAudioCaptureService.StartAsync throws while IsRunning,
            // which stays true after StopAsync until DisposeAsync) — build fresh instances every start.
            var micSource = _micSourceFactory();
            _micSource = micSource;
            var loopbackSource = _loopbackSourceFactory();
            _loopbackSource = loopbackSource;

            // Privacy boundary: audio capture opens here, after every model is already warm and the
            // forward loop is already listening — never before.
            await micSource.StartAsync(startToken).ConfigureAwait(false);
            await loopbackSource.StartAsync(startToken).ConfigureAwait(false);

            var vadModelPath = _vadModelPath!;
            var transcriptionEngine = _transcriptionEngine!;

            var micEngine = await _engineServiceFactory(
                TranscriptSpeaker.You, micSource, vadModelPath, transcriptionEngine, rawChannel.Writer, null, startToken)
                .ConfigureAwait(false);
            _micEngine = micEngine;
            WireSpeakingChanged(micEngine, TranscriptSpeaker.You);

            var loopbackEngine = await _engineServiceFactory(
                TranscriptSpeaker.Them, loopbackSource, vadModelPath, transcriptionEngine, rawChannel.Writer, _speakerId, startToken)
                .ConfigureAwait(false);
            _loopbackEngine = loopbackEngine;
            WireSpeakingChanged(loopbackEngine, TranscriptSpeaker.Them);

            // Only claimed once the whole run is genuinely up. Transitioning unconditionally is what let a
            // torn-down run still report "Listening".
            startToken.ThrowIfCancellationRequested();
            TransitionState(DirectTranscriptionState.Running);
            _logger.LogInformation("Direct transcription started");
        }
        catch (OperationCanceledException)
        {
            // A cancelled start is not a failure: unwind the half-built run and fall back to the state
            // the session was already in (Prepared — the models and consent map are untouched).
            _logger.LogInformation("Direct transcription start was cancelled; run torn down");
            await TeardownRunAsync().ConfigureAwait(false);
            TransitionState(DirectTranscriptionState.Prepared);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start direct transcription service");
            await TeardownRunAsync().ConfigureAwait(false);
            TransitionState(DirectTranscriptionState.Error);
            throw;
        }
        finally
        {
            lock (_stateLock) { _startCts = null; }
            startCts.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Signal an in-flight start to abort BEFORE queueing behind it, so a stop never has to wait out a
        // model download, and the start can never resurrect resources this stop is about to tear down.
        CancelInFlightStart();

        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        // Atomic check-and-set: guard and transition under the SAME lock so two concurrent callers
        // cannot both pass before either sets Stopping — otherwise each owned resource could be
        // disposed twice (the old branch's bug; see MeetingAttendeeService.StopAsync for the pattern).
        // Preparing is excluded too: a background warmup has no run to stop, and flipping it to
        // Stopping -> Prepared would both lie about the session and emit a SessionStopped audit line for
        // a session that never started.
        EventHandler<DirectTranscriptionState>? handler;
        lock (_stateLock)
        {
            if (_state is DirectTranscriptionState.Idle
                or DirectTranscriptionState.Preparing
                or DirectTranscriptionState.Prepared
                or DirectTranscriptionState.Stopping)
                return;
            _state = DirectTranscriptionState.Stopping;
            handler = StateChanged;
        }
        handler?.Invoke(this, DirectTranscriptionState.Stopping);

        try
        {
            await TeardownRunAsync().ConfigureAwait(false);

            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                ConsentAuditEventTypes.SessionStopped,
                null,
                new Dictionary<string, object?>
                {
                    ["droppedUnlabeledLoopback"] = _forwardLoop?.DroppedUnlabeledCount ?? 0,
                    ["droppedUnconsented"] = _forwardLoop?.DroppedUnconsentedCount ?? 0,
                    ["droppedMicEcho"] = _forwardLoop?.DroppedEchoCount ?? 0,
                }));

            TransitionState(DirectTranscriptionState.Prepared);
            _logger.LogInformation("Direct transcription stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping direct transcription service");
            TransitionState(DirectTranscriptionState.Error);
            throw;
        }
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        if (State is DirectTranscriptionState.Idle) return;

        CancelInFlightStart();

        if (State is DirectTranscriptionState.Running or DirectTranscriptionState.Starting or DirectTranscriptionState.Stopping)
        {
            // Never treat "already Stopping" as "already torn down": StopAsync queues on the same gate as
            // the in-flight stop, so by the time it returns both engines really have been disposed and
            // drained. Skipping this wait disposed the shared sherpa recognizer and the native ONNX
            // diarizer while a trailing segment was still being decoded through them.
            try { await StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Stop while ending the session threw; continuing teardown"); }
        }

        // Barrier on any in-flight PrepareAsync (including a background warmup): it must finish assigning
        // — or failing — before this method disposes the session's native models, otherwise it writes live
        // sherpa/ONNX handles into a session that nothing will ever tear down again. Cancelled first so the
        // barrier resolves promptly instead of waiting out a whole first-run model download.
        CancelInFlightPrepare();
        await WaitForPrepareIdleAsync(cancellationToken).ConfigureAwait(false);

        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TeardownSessionAsync().ConfigureAwait(false);

            _consentStateManager.ResetSession();
            _sessionId = string.Empty;
            _sttModelId = string.Empty;

            // Drain whatever the forward loop emitted after the UI's consumer was cancelled. The public
            // channel outlives every session (its reader must stay stable), so an undrained trailing
            // utterance would be delivered into the NEXT session's transcript — carrying the previous
            // session's speaker label, after consent had already been reset.
            var dropped = 0;
            while (_publicChannel.Reader.TryRead(out _)) dropped++;
            if (dropped > 0)
                _logger.LogInformation("Discarded {Count} undelivered utterances at session end", dropped);

            TransitionState(DirectTranscriptionState.Idle);
            _logger.LogInformation("Direct transcription session ended");
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    /// <summary>Cancels an in-flight <see cref="StartAsync"/>, if any. Safe to call at any time.</summary>
    private void CancelInFlightStart()
    {
        CancellationTokenSource? startCts;
        lock (_stateLock) { startCts = _startCts; }
        try { startCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the start already finished and disposed it */ }
    }

    /// <summary>Cancels an in-flight <see cref="PrepareAsync"/>, if any. Safe to call at any time.</summary>
    private void CancelInFlightPrepare()
    {
        CancellationTokenSource? prepareCts;
        lock (_stateLock) { prepareCts = _prepareCts; }
        try { prepareCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the prepare already finished and disposed it */ }
    }

    /// <summary>
    /// Waits until no <see cref="PrepareAsync"/> is in flight, then releases immediately — a barrier, not a
    /// held lock, so it cannot deadlock against PrepareAsync's own use of the same gate.
    /// </summary>
    private async Task WaitForPrepareIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _prepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _prepareGate.Release();
        }
        catch (ObjectDisposedException) { /* disposed concurrently — nothing left to wait for */ }
        catch (OperationCanceledException) { /* caller gave up waiting; teardown proceeds regardless */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await EndSessionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EndSessionAsync during dispose threw");
        }

        _publicChannel.Writer.TryComplete();

        try
        {
            await _auditLog.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consent audit log dispose threw");
        }

        // Disposed only after EndSessionAsync above has barriered on the prepare gate, so no PrepareAsync
        // can still be holding one of these.
        _prepareGate.Dispose();
        _disposeGate.Dispose();
        _startStopGate.Dispose();
    }

    // -------------------------------------------------------------------------------------------
    // Rename / revoke / stats
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Renames a speaker across the diarizer, the consent map and the recorded voice samples — all three,
    /// or none of them.
    ///
    /// <para>Order and atomicity are a privacy boundary, not tidiness. The previous version renamed the
    /// DIARIZER first and the consent map second, with no rollback, and neither side checked for a label
    /// collision. Renaming an unconsented cluster onto an already-granted label therefore succeeded in the
    /// diarizer and failed in the consent map: from then on every segment of the UNCONSENTED person came
    /// back carrying the granted label, the gate read <see cref="ConsentState.Granted"/> for it, and their
    /// speech was transcribed into the visible transcript, the saved Markdown and the voice statistics
    /// under someone else's consent record.</para>
    /// </summary>
    public bool RenameSpeaker(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(oldLabel) || string.IsNullOrWhiteSpace(newLabel))
            return false;
        if (string.Equals(oldLabel, newLabel, StringComparison.Ordinal))
            return false;

        // Refuse up front if the target label is already known to the consent map. Checked before ANY
        // mutation: a rename onto an existing key can only ever alias two voices onto one consent record.
        if (_consentStateManager.TryGet(newLabel, out _))
        {
            _logger.LogInformation("Speaker rename refused: the target label already has a consent entry");
            return false;
        }

        if (_speakerId is null)
            return false;

        // Consent map first — it owns the authoritative key the gate reads, and its rename is cheap and
        // exactly reversible. The diarizer second, with a rollback if it refuses (its own collision guard
        // can also say no), so the two can never diverge.
        if (!_consentStateManager.Rename(oldLabel, newLabel))
            return false;

        if (!_speakerId.Rename(oldLabel, newLabel))
        {
            if (!_consentStateManager.Rename(newLabel, oldLabel))
                _logger.LogError("Consent rename rollback failed after the diarizer refused a rename");
            return false;
        }

        // Only now that the rename is known to have fully succeeded: re-key the already-measured samples,
        // or the statistics would report one person as two rows with split totals and halved shares.
        _forwardLoop?.RenameSamples(oldLabel, newLabel);
        return true;
    }

    public void RevokeSpeaker(string speakerLabel)
    {
        // The evidence label must be read BEFORE the revoke, while the entry is still Granted, and it is
        // deliberately not `speakerLabel`: after a grant-time rename the caller's label IS the extracted
        // personal name, and both the plaintext JSONL audit trail and the evidence FILENAME must stay
        // name-free (the DPAPI envelope protects the contents, not the file name). Using the grant's own
        // label also keeps the revocation record correlatable with the grant it revokes.
        _consentStateManager.TryGet(speakerLabel, out var priorEntry);
        var evidenceLabel = priorEntry?.Evidence?.SpeakerLabel ?? speakerLabel;
        var extractedName = priorEntry?.ExtractedName;

        // Single lock acquisition decides whether a revoke actually happened. Probing CurrentState first
        // and branching on the probe left a window in which a concurrent grant landed between the two
        // calls: the speaker was really revoked in-session, yet the audit event and the persisted
        // revocation record were both skipped — the exact Nachweispflicht gap the evidence store exists
        // to close.
        if (!_consentStateManager.Revoke(speakerLabel)) return;

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.ConsentRevoked, evidenceLabel, null));

        // Revocation removes this speaker's text from the transcript, so their measured speech must go
        // too — otherwise their name, utterance count and speaking time survive in the voice-stats flyout
        // and in the YAML front matter of the file the user saves and shares.
        _forwardLoop?.RemoveSamplesFor(speakerLabel);

        var revokedAt = DateTimeOffset.UtcNow;
        var sessionId = _sessionId;
        _ = SaveRevocationBestEffortAsync(sessionId, evidenceLabel, revokedAt);

        RaiseSpeakerConsentChanged(new SpeakerConsentChangedEventArgs(
            speakerLabel, ConsentState.Granted, ConsentState.Revoked, extractedName, evidenceLabel));
    }

    private async Task SaveRevocationBestEffortAsync(string sessionId, string speakerLabel, DateTimeOffset revokedAt)
    {
        try
        {
            await _evidenceStore.SaveRevocationAsync(sessionId, speakerLabel, revokedAt, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never throw into a UI command — the revoke itself already took effect in-session.
            _logger.LogWarning(ex, "Failed to persist a consent revocation");
        }
    }

    public IReadOnlyList<SpeakerVoiceStats> GetVoiceStats()
        => _forwardLoop is null
            ? Array.Empty<SpeakerVoiceStats>()
            : VoiceStatsCalculator.Compute(_forwardLoop.VoiceSamples);

    // -------------------------------------------------------------------------------------------
    // Event plumbing
    // -------------------------------------------------------------------------------------------

    private void OnSpeakerRegistered(object? sender, string label)
    {
        // Fires on the engine's segment loop, before the utterance reaches the raw channel — keep
        // this lock-safe and non-blocking, never await disk I/O here.
        _consentStateManager.GetOrCreate(label);
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.SpeakerDetected, label, null));

        var handler = SpeakerRegistered;
        if (handler is null) return;
        try
        {
            handler.Invoke(this, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeakerRegistered subscriber threw");
        }
    }

    private void OnForwardLoopSpeakerConsentChanged(object? sender, ConsentStateChangedEventArgs e)
        => RaiseSpeakerConsentChanged(new SpeakerConsentChangedEventArgs(e.SpeakerLabel, e.OldState, e.NewState, e.ExtractedName));

    private void RaiseConsentSessionReset()
    {
        var handler = ConsentSessionReset;
        if (handler is null) return;
        try
        {
            handler.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConsentSessionReset subscriber threw");
        }
    }

    private void RaiseSpeakerConsentChanged(SpeakerConsentChangedEventArgs args)
    {
        var handler = SpeakerConsentChanged;
        if (handler is null) return;
        try
        {
            handler.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeakerConsentChanged subscriber threw");
        }
    }

    private void WireSpeakingChanged(IAsyncDisposable engine, TranscriptSpeaker speaker)
    {
        if (engine is not LiveTranscriptionEngineService concrete) return;

        concrete.IsSpeakingChanged += (_, isSpeaking) =>
        {
            // The far end's voice activity is known here, long before its text is recognised — which is
            // what lets the detector spot a suspect microphone segment without delaying every caption.
            if (speaker == TranscriptSpeaker.Them)
                _echoDetector?.NoteRemoteSpeaking(isSpeaking, DateTimeOffset.UtcNow);

            RaiseSpeakingChanged(speaker, isSpeaking);
        };
    }

    private void RaiseSpeakingChanged(TranscriptSpeaker speaker, bool isSpeaking)
    {
        var handler = SpeakingChanged;
        if (handler is null) return;
        try
        {
            handler.Invoke(this, new TranscriptionSpeakingChangedEventArgs(speaker, isSpeaking));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeakingChanged subscriber threw");
        }
    }

    private void TransitionState(DirectTranscriptionState newState)
    {
        EventHandler<DirectTranscriptionState>? handler;
        lock (_stateLock)
        {
            if (_state == newState) return;
            _state = newState;
            handler = StateChanged;
        }
        handler?.Invoke(this, newState);
    }

    // -------------------------------------------------------------------------------------------
    // Teardown
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Pauses the pipeline: sources → engines (awaited) → raw channel completed → forward-loop task
    /// awaited. Order is mandatory — the engine has no <c>StopAsync</c>; VAD drain, the trailing
    /// segment and its final (awaited) sink write all happen inside <c>DisposeAsync</c>, and because
    /// that write is <c>WriteAsync</c> (not <c>TryWrite</c>), completing the raw channel first would
    /// throw <see cref="ChannelClosedException"/> and silently lose the trailing utterance. Does NOT
    /// touch the diarizer, the shared engine or the consent map — those survive so a resume is fast.
    /// </summary>
    private async Task TeardownRunAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SafeStopAsync(_micSource, "Mic source stop threw").ConfigureAwait(false);
            await SafeStopAsync(_loopbackSource, "Loopback source stop threw").ConfigureAwait(false);

            await SafeDisposeAsync(_micEngine, "Mic engine dispose threw").ConfigureAwait(false);
            _micEngine = null;
            await SafeDisposeAsync(_loopbackEngine, "Loopback engine dispose threw").ConfigureAwait(false);
            _loopbackEngine = null;

            await SafeDisposeAsync(_micSource, "Mic source dispose threw").ConfigureAwait(false);
            _micSource = null;
            await SafeDisposeAsync(_loopbackSource, "Loopback source dispose threw").ConfigureAwait(false);
            _loopbackSource = null;

            // Only now: engines have finished draining into it, so nothing more will ever be written.
            _rawChannel?.Writer.TryComplete();
            _rawChannel = null;

            if (_forwardLoopTask is not null)
            {
                try { await _forwardLoopTask.ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Forward loop task wait threw"); }
                _forwardLoopTask = null;
            }

            _forwardCts?.Dispose();
            _forwardCts = null;
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    /// <summary>Null-guarded, exception-swallowed <see cref="IAudioCaptureSource.StopAsync"/> — every
    /// teardown step here must log-and-continue rather than let one owned resource's failure stop the
    /// rest of the teardown from running.</summary>
    private async Task SafeStopAsync(IAudioCaptureSource? source, string failureMessage)
    {
        if (source is null) return;
        try { await source.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, failureMessage); }
    }

    /// <summary>Null-guarded, exception-swallowed <see cref="IAsyncDisposable.DisposeAsync"/> — same
    /// log-and-continue contract as <see cref="SafeStopAsync"/>.</summary>
    private async Task SafeDisposeAsync(IAsyncDisposable? resource, string failureMessage)
    {
        if (resource is null) return;
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, failureMessage); }
    }

    /// <summary>
    /// Ends the session: disposes the shared <see cref="ITranscriptionEngine"/>, then the diarizer
    /// LAST (native ONNX — must not be disposed while a segment could still be in flight), and drops
    /// the session's <see cref="ConsentForwardLoop"/>. Assumes <see cref="TeardownRunAsync"/> already
    /// ran (via <see cref="StopAsync"/>) — run-scoped resources are null by the time this executes.
    /// </summary>
    private async Task TeardownSessionAsync()
    {
        await SafeDisposeAsync(_transcriptionEngine, "Shared transcription engine dispose threw").ConfigureAwait(false);
        _transcriptionEngine = null;

        if (_speakerId is not null)
        {
            _speakerId.SpeakerRegistered -= OnSpeakerRegistered;
            try { _speakerId.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Speaker identification dispose threw"); }
            _speakerId = null;
        }

        if (_forwardLoop is not null)
        {
            _forwardLoop.SpeakerConsentChanged -= OnForwardLoopSpeakerConsentChanged;
            _forwardLoop = null;
        }

        _echoDetector = null;

        _vadModelPath = null;
    }

    /// <summary>
    /// Whether direct transcription should build the adaptive (re-clustering) diarizer instead of the
    /// manual one. ALWAYS <c>false</c> — design §3.4: the adaptive diarizer retroactively reassigns
    /// already-emitted segments, which is unsound under a consent gate in both directions (a Granted
    /// label could be handed to an unconsented speaker after the fact, or vice versa, and neither can
    /// be undone once text has left the process). Deliberately ignores
    /// <see cref="AppSettings.MeetingSmartSpeakerDetection"/>, which continues to govern only the Teams
    /// meeting attendee. A pure function (not a literal inlined at the call site) so the invariant is
    /// independently testable without constructing a native diarizer.
    /// </summary>
    internal static bool ShouldUseAdaptiveDiarizer(AppSettings settings) => false;

    private static string ComputeSttModelId(AppSettings settings)
        => settings.SttBackend == SttBackend.Parakeet
            ? "parakeet-tdt-v3"
            : $"whisper-{settings.WhisperModel}".ToLowerInvariant();
}
