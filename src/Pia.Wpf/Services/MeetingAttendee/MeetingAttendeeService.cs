using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Orchestrates the meeting attendee. Modelled closely on
/// <c>LiveMeetingService</c>: it owns the browser session, the audio source, and one
/// <see cref="LiveTranscriptionEngineService"/>, exposes a stable merged utterance reader, and tears
/// everything down in the same defensive order on stop/dispose.
///
/// <para>Start sequence: read settings → provision Chromium (<see cref="MeetingAttendeeState.ProvisioningBrowser"/>)
/// → ensure Silero VAD + build the sherpa engine → join the meeting (<see cref="MeetingAttendeeState.Joining"/>,
/// possibly via <see cref="MeetingAttendeeState.InLobby"/>) → create + start the audio source and the
/// transcription engine (<see cref="MeetingAttendeeState.Attending"/>). A background task then awaits
/// <see cref="IMeetingSession.WaitForEndAsync"/> and calls <see cref="StopAsync"/> when the meeting ends.</para>
///
/// <para>The attendee's audio is tagged <see cref="TranscriptSpeaker.Them"/> (it is the room, not the
/// local mic). Transcript <b>saving is the ViewModel's job</b>; this service only produces
/// <see cref="Utterances"/>.</para>
///
/// <para><b>Testability:</b> every network/disk/IO construction in the start path sits behind an
/// injectable delegate (provisioning, model setup, session factory, audio-source factory, engine
/// factory) so the state machine can be exercised with substitutes. The public constructor wires
/// production defaults; the internal constructor (visible to the test assembly) accepts the seams.</para>
/// </summary>
public sealed class MeetingAttendeeService : IMeetingAttendeeService, IAsyncDisposable
{
    // The bot's display name is "{user}'s assistant". The localized format string formally belongs to
    // Unit 5's resources; until that key exists this fallback keeps the orchestrator compiling and
    // self-contained. Unit 5 should replace this with a CommonStrings key. (See assumptions/handover.)
    private const string DisplayNameFormat = "{0}'s assistant";
    private const string DefaultUserName = "Pia";

    private readonly ISettingsService _settingsService;
    private readonly ILogger<MeetingAttendeeService> _logger;

    // ---- Injected seams ---------------------------------------------------------------------------
    private readonly Func<IProgress<ChromiumDownloadProgress>?, CancellationToken, Task<string>> _provisionChromium;
    // Takes an optional IProgress<ModelDownloadProgress> threaded down to the speaker-embedding model
    // download so the VM can surface a progress dialog — mirrors the _provisionChromium IProgress seam
    // above. Silero VAD + the sherpa engine remain silent (no progress); only the OPTIONAL speaker model
    // reports, and only when it actually downloads.
    private readonly Func<IProgress<ModelDownloadProgress>?, CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>> _createTranscription;
    private readonly Func<string, IMeetingSession> _sessionFactory;
    // (session, usePerProcessLoopback) → source. usePerProcess is already resolved against the
    // settings flag + PID availability by the orchestrator, so the factory just builds the right one.
    private readonly Func<IMeetingSession, bool, IAudioCaptureSource> _audioSourceFactory;
    // Builds AND starts the transcription engine service, returning it as IAsyncDisposable (the only
    // surface the orchestrator needs). Folding start into the factory keeps the engine service a clean
    // seam — tests substitute an observable IAsyncDisposable instead of spinning real reader loops.
    private readonly Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, CancellationToken, Task<IAsyncDisposable>> _engineServiceFactory;

    private readonly Channel<TranscriptUtterance> _utterances;
    private readonly object _stateLock = new();
    // Serializes DisposeAllAsync only (NOT the whole start/stop body — gating the 120s join would just
    // move the hang to StopAsync). With teardown single-threaded, the read-then-null of each owned field
    // is atomic between the two callers (StopAsync and StartAsync's catch), so a resource — including the
    // per-process WASAPI RCWs whose Marshal.ReleaseComObject over-releases on a double dispose — is torn
    // down exactly once even when Stop races an in-flight Start.
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private MeetingAttendeeState _state = MeetingAttendeeState.Idle;

    private IMeetingSession? _session;
    private IAudioCaptureSource? _audioSource;
    private IAsyncDisposable? _engineService;
    private ITranscriptionEngine? _transcriptionEngine;

    // Per-session speaker diarization. Owned by this orchestrator (constructed fresh per start so
    // "Speaker N" numbering resets per meeting). Null when diarization is disabled or the speaker
    // model failed to download/construct — that degrade-to-null path keeps meeting join non-fatal.
    // Wraps native ONNX resources, so it is disposed strictly AFTER the engine service drains its
    // segment loop (see DisposeAllAsync); disposing it earlier would crash an in-flight identify.
    private ISpeakerIdentificationService? _speakerId;

    // The background loop that awaits the meeting's natural end then stops us. Owns its own CTS so
    // StopAsync can cancel WaitForEndAsync without awaiting (and thus deadlocking) the loop itself.
    private Task? _watchLoop;
    private CancellationTokenSource? _watchCts;

    // Cancels the active StartAsync. Linked to the caller's token so Stop can abort the join even when
    // the caller passed CancellationToken.None (the VM and tests do). Without this, the long-lived join
    // (WaitForAdmissionAsync polls up to 120s) would race a concurrent Stop/teardown.
    private CancellationTokenSource? _startCts;

    public MeetingAttendeeState State
    {
        get { lock (_stateLock) return _state; }
    }

    public event EventHandler<MeetingAttendeeState>? StateChanged;

    public ChannelReader<TranscriptUtterance> Utterances => _utterances.Reader;

    /// <summary>Production constructor (used by DI). Wires default seams over the real dependencies.</summary>
    public MeetingAttendeeService(
        ISettingsService settingsService,
        IBrowserProvisioner browserProvisioner,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
        : this(
            settingsService,
            loggerFactory,
            provisionChromium: (progress, ct) => browserProvisioner.EnsureChromiumAsync(progress, ct),
            createTranscription: async (speakerProgress, ct) =>
            {
                var settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                var log = loggerFactory.CreateLogger<MeetingAttendeeService>();
                // Silero VAD + the sherpa engine are REQUIRED for transcription: build them outside any
                // speaker try/catch so a failure here still propagates fatally (StartAsync → Error), as today.
                var sileroPath = await LiveTranscriptionModels
                    .EnsureSileroVadAsync(httpClientFactory, log, ct).ConfigureAwait(false);
                var engine = await TranscriptionEngineFactory
                    .CreateAsync(settings, httpClientFactory, downloadProgress: null, log, ct).ConfigureAwait(false);
                // Diarization is an OPTIONAL enhancement: a missing/corrupt/404 speaker model degrades to
                // null inside the helper (single-bubble behavior) and must NEVER fail meeting join. The
                // progress is threaded ONLY here (the optional speaker model), surfacing the download dialog.
                var speakerId = await TryCreateSpeakerIdentificationAsync(
                    httpClientFactory, loggerFactory, settings, log, ct, speakerProgress).ConfigureAwait(false);
                return (sileroPath, engine, speakerId);
            },
            sessionFactory: chromiumPath => new TeamsMeetingSession(
                chromiumPath,
                httpClientFactory,
                loggerFactory.CreateLogger<TeamsMeetingSession>()),
            audioSourceFactory: null,
            engineServiceFactory: async (source, sileroPath, engine, sink, speakerId, ct) =>
            {
                var svc = new LiveTranscriptionEngineService(
                    TranscriptSpeaker.Them,
                    source,
                    sileroPath,
                    engine,
                    sink,
                    loggerFactory.CreateLogger<LiveTranscriptionEngineService>(),
                    speakerId);
                await svc.StartAsync(ct).ConfigureAwait(false);
                return svc;
            })
    {
    }

    /// <summary>
    /// Seam constructor used by tests. Any null factory falls back to the production default that
    /// closes over the supplied dependencies, so a test can override only the seams it cares about.
    /// </summary>
    internal MeetingAttendeeService(
        ISettingsService settingsService,
        ILoggerFactory loggerFactory,
        Func<IProgress<ChromiumDownloadProgress>?, CancellationToken, Task<string>> provisionChromium,
        Func<IProgress<ModelDownloadProgress>?, CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>> createTranscription,
        Func<string, IMeetingSession> sessionFactory,
        Func<IMeetingSession, bool, IAudioCaptureSource>? audioSourceFactory,
        Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, CancellationToken, Task<IAsyncDisposable>> engineServiceFactory)
    {
        _settingsService = settingsService;
        _logger = loggerFactory.CreateLogger<MeetingAttendeeService>();

        _provisionChromium = provisionChromium;
        _createTranscription = createTranscription;
        _sessionFactory = sessionFactory;
        _audioSourceFactory = audioSourceFactory
            ?? ((session, usePerProcess) => CreateDefaultAudioSource(session, usePerProcess, loggerFactory));
        _engineServiceFactory = engineServiceFactory;

        _utterances = UtteranceChannel.CreateBounded();
    }

    public async Task StartAsync(
        string meetingUrl,
        CancellationToken cancellationToken = default,
        IProgress<ModelDownloadProgress>? speakerModelProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingUrl);

        lock (_stateLock)
        {
            if (_state is not (MeetingAttendeeState.Idle or MeetingAttendeeState.Error))
                throw new InvalidOperationException($"Cannot start while {_state}");
        }

        // Only after the guard passed (a rejected second start must not touch the first start's CTS):
        // dispose any CTS left over from a prior cycle and link a fresh one to the caller's token, so
        // StopAsync can abort this start even when the caller passed CancellationToken.None.
        _startCts?.Dispose();
        _startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startToken = _startCts.Token;

        TransitionState(MeetingAttendeeState.ProvisioningBrowser);

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            var displayName = BuildDisplayName(settings.SyncUserDisplayName);

            // 1) Browser on disk (idempotent; skips fast when cached).
            var chromiumPath = await _provisionChromium(null, startToken).ConfigureAwait(false);

            // 2) Models — Silero VAD + the sherpa engine — before we join, mirroring LiveMeetingService.
            //    The speaker-ID service degrades to null INSIDE the closure, so this await cannot throw on a
            //    speaker-model failure — StartAsync still reaches Attending; only a Silero/engine failure is fatal.
            var (sileroPath, engine, speakerId) = await _createTranscription(speakerModelProgress, startToken).ConfigureAwait(false);
            startToken.ThrowIfCancellationRequested();
            _transcriptionEngine = engine;
            _speakerId = speakerId;

            // 3) Join. Subscribe to the lobby signal BEFORE joining so InLobby is observable even if it
            //    fires during JoinAsync. Admitted-immediately meetings skip InLobby (Joining → Attending).
            var session = _sessionFactory(chromiumPath);
            _session = session;
            session.EnteredLobby += OnEnteredLobby;

            TransitionState(MeetingAttendeeState.Joining);
            await session.JoinAsync(meetingUrl, displayName, startToken).ConfigureAwait(false);

            // Re-check after the long-lived join (WaitForAdmissionAsync polls up to 120s): if Stop ran
            // while we were joining it cancelled startToken and already owns teardown — bail before we
            // hand the now-disposed source/engine onward or clobber the Idle state Stop set.
            startToken.ThrowIfCancellationRequested();

            // 4) Audio source + transcription engine. Default = endpoint loopback; per-process only when
            //    the AppSettings flag is set AND the browser PID is known.
            var source = ResolveAudioSource(session, settings);
            _audioSource = source;
            await source.StartAsync(startToken).ConfigureAwait(false);

            startToken.ThrowIfCancellationRequested();
            _engineService = await _engineServiceFactory(source, sileroPath, engine, _utterances.Writer, _speakerId, startToken)
                .ConfigureAwait(false);

            startToken.ThrowIfCancellationRequested();
            TransitionState(MeetingAttendeeState.Attending);
            _logger.LogInformation("Meeting attendee is now attending");

            // 5) Background watch: when the meeting ends naturally, stop ourselves. Owns a dedicated CTS
            //    so StopAsync can cancel the wait without awaiting this loop (which would deadlock, since
            //    the loop calls StopAsync). DisposeAsync awaits it after StopAsync.
            //    Dispose any CTS left over from a prior start/stop cycle here (not in StopAsync, where the
            //    not-yet-awaited loop may still read the token) to avoid leaking a wait handle on restart.
            _watchCts?.Dispose();
            _watchCts = new CancellationTokenSource();
            _watchLoop = Task.Run(() => WatchForEndAsync(session, _watchCts.Token));
        }
        catch (OperationCanceledException) when (startToken.IsCancellationRequested)
        {
            // StopAsync (or the caller's token) cancelled this start. Stop already owns teardown and the
            // Idle transition, so we must NOT run a competing DisposeAllAsync race or clobber Stop's state
            // with Error. DisposeAllAsync is idempotent under _disposeGate, so we still call it to clean up
            // any resource Stop had not yet observed, but we leave the state to whoever is stopping.
            _logger.LogInformation("Meeting attendee start was cancelled");
            await DisposeAllAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start meeting attendee");
            await DisposeAllAsync().ConfigureAwait(false);
            TransitionState(MeetingAttendeeState.Error);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Atomic check-and-set: capture the guard and the Stopping transition under the SAME lock so
        // two concurrent callers (the background end-watch loop, the user clicking Stop, and dispose)
        // cannot both pass the guard before either sets Stopping. Only the winner proceeds into
        // DisposeAllAsync, so each owned resource — including the per-process WASAPI RCWs whose
        // Marshal.ReleaseComObject would over-release on a double dispose — is torn down exactly once.
        EventHandler<MeetingAttendeeState>? handler;
        lock (_stateLock)
        {
            if (_state is MeetingAttendeeState.Idle or MeetingAttendeeState.Stopping) return;
            _state = MeetingAttendeeState.Stopping;
            handler = StateChanged;
        }
        handler?.Invoke(this, MeetingAttendeeState.Stopping);

        try
        {
            // Cancel an in-flight StartAsync FIRST so its long-lived join (WaitForAdmissionAsync polls up
            // to 120s) aborts promptly and Start's cancellation-aware catch yields teardown ownership to
            // us instead of racing its own DisposeAllAsync / clobbering state. The linked CTS gives us a
            // handle even when Start's caller passed CancellationToken.None.
            _startCts?.Cancel();

            // Cancel the background watch loop so it does not re-enter StopAsync. We do NOT await it
            // here: the loop may itself be the caller, and awaiting would deadlock. DisposeAsync awaits.
            _watchCts?.Cancel();

            // Stop capture first so the engine's reader loop drains naturally, then leave the meeting.
            if (_audioSource is not null)
            {
                try { await _audioSource.StopAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Audio source stop threw"); }
            }

            if (_session is not null)
            {
                try { await _session.LeaveAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Session leave threw"); }
            }

            await DisposeAllAsync().ConfigureAwait(false);

            TransitionState(MeetingAttendeeState.Idle);
            _logger.LogInformation("Meeting attendee stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping meeting attendee");
            TransitionState(MeetingAttendeeState.Error);
            throw;
        }
    }

    /// <summary>
    /// Renames a diarized speaker label on the live diarizer (in-memory, current meeting only). A no-op
    /// when <see cref="_speakerId"/> is null (diarization off, or degraded-to-null on model failure) — it
    /// must not throw. Thread-safe against a concurrent in-flight identify: the rename takes the same
    /// <c>_lock</c> the speaker-identification service holds.
    /// </summary>
    public void RenameSpeaker(string oldLabel, string newLabel) => _speakerId?.Rename(oldLabel, newLabel);

    private async Task WatchForEndAsync(IMeetingSession session, CancellationToken token)
    {
        try
        {
            await session.WaitForEndAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // StopAsync cancelled us — it is already tearing everything down.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meeting end-watch loop threw");
        }

        if (token.IsCancellationRequested) return;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-stop after meeting end threw");
        }
    }

    /// <summary>
    /// Disposes every owned resource in the same defensive order as
    /// <c>LiveMeetingService</c>'s teardown: engine service → audio source → meeting session →
    /// transcription engine. Each step is null-guarded and its exception swallowed so one failure does
    /// not abort the rest. Called both on the error path (where only the session/engine may exist) and
    /// on normal stop.
    /// </summary>
    private async Task DisposeAllAsync()
    {
        // Single-thread teardown across its two callers (StopAsync and StartAsync's cancellation catch)
        // so the read-then-null of each owned field is atomic and no resource is disposed twice — the
        // race that would double-release the per-process WASAPI RCWs. The two callers are on separate,
        // non-nested stacks, so this non-reentrant gate cannot self-deadlock.
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engineService is not null)
            {
                try { await _engineService.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Engine service dispose threw"); }
                _engineService = null;
            }

            if (_audioSource is not null)
            {
                try { await _audioSource.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Audio source dispose threw"); }
                _audioSource = null;
            }

            if (_session is not null)
            {
                _session.EnteredLobby -= OnEnteredLobby;
                try { await _session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Meeting session dispose threw"); }
                _session = null;
            }

            if (_transcriptionEngine is not null)
            {
                try { await _transcriptionEngine.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Transcription engine dispose threw"); }
                _transcriptionEngine = null;
            }

            // Speaker-ID LAST: it wraps native ONNX resources and must be disposed only after the engine
            // service above drained its segment loop — disposing it while an IdentifyOrRegister is in flight
            // would crash natively. Swallow any throw so one failure does not abort the rest of teardown
            // (an uncaught native throw would propagate into StopAsync's catch and flip state to Error).
            if (_speakerId is not null)
            {
                try { _speakerId.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Speaker identification dispose threw"); }
                _speakerId = null;
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private IAudioCaptureSource ResolveAudioSource(IMeetingSession session, AppSettings settings)
    {
        // Default: endpoint loopback (captures the whole render mix, audible). Per-process loopback —
        // isolated to the browser PID, inaudible — is opt-in via the flag AND requires a known PID.
        var usePerProcess = UsePerProcessLoopback(settings, session);
        if (usePerProcess)
        {
            _logger.LogInformation("Meeting attendee using per-process loopback (browser PID known)");
        }
        return _audioSourceFactory(session, usePerProcess);
    }

    /// <summary>
    /// Pure decision: use the per-process loopback source only when opted in via
    /// <see cref="AppSettings.MeetingAttendeeUseProcessLoopback"/> AND the browser process id is known.
    /// Otherwise fall back to the default endpoint loopback.
    /// </summary>
    internal static bool UsePerProcessLoopback(AppSettings settings, IMeetingSession session)
        => settings.MeetingAttendeeUseProcessLoopback && session.BrowserProcessId is int;

    private static IAudioCaptureSource CreateDefaultAudioSource(
        IMeetingSession session, bool usePerProcess, ILoggerFactory loggerFactory)
    {
        // Per-process loopback is selected only when the orchestrator already decided so (flag on AND a
        // PID is known); otherwise the proven endpoint loopback is the default. The PID is re-checked
        // here purely as a defensive guard before constructing the per-process source.
        if (usePerProcess && session.BrowserProcessId is int pid)
        {
            return new ProcessLoopbackAudioCaptureService(
                pid, loggerFactory.CreateLogger<ProcessLoopbackAudioCaptureService>());
        }
        return new LoopbackAudioCaptureService(loggerFactory.CreateLogger<LoopbackAudioCaptureService>());
    }

    /// <summary>
    /// Builds the per-session speaker diarizer, DEGRADING TO null on any failure. Diarization is an
    /// optional enhancement to an already-working feature, so a missing/corrupt/404 speaker model or a
    /// native <c>SpeakerEmbeddingExtractor</c> construction failure must never fail meeting join — it
    /// downgrades to single-bubble behavior. Returns null when diarization is disabled, and null (not a
    /// throw) on any ensure/construct exception. A fresh service is built per start so "Speaker N"
    /// numbering resets per meeting. Extracted as an internal static for unit-testing the catch.
    /// </summary>
    internal static async Task<ISpeakerIdentificationService?> TryCreateSpeakerIdentificationAsync(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        AppSettings settings,
        ILogger logger,
        CancellationToken cancellationToken,
        IProgress<ModelDownloadProgress>? progress = null)
    {
        if (!settings.EnableMeetingDiarization) return null;

        try
        {
            var speakerModelPath = await LiveTranscriptionModels
                .EnsureSpeakerEmbeddingAsync(httpClientFactory, logger, cancellationToken, progress).ConfigureAwait(false);
            return new SpeakerIdentificationService(
                speakerModelPath,
                settings.SpeakerEmbeddingThreshold,
                loggerFactory.CreateLogger<SpeakerIdentificationService>());
        }
        catch (Exception ex)
        {
            // A CDN hiccup, a 404 (e.g. if the misspelled `recongition` release tag is "fixed"), a corrupt
            // download, or a native extractor construction failure must NOT regress meeting join.
            logger.LogWarning(ex, "Speaker diarization unavailable; continuing without per-speaker bubbles.");
            return null;
        }
        finally
        {
            // Terminal dismissal signal: emit on success, failure→null, AND cancellation so the
            // progress dialog is NEVER left stuck. Distinguishable from a mid-download tick (the VM
            // dismisses only on Completed), and a cached model — which produced no Downloading report —
            // emits only this, so the dialog never flashed. Progress<T>.Report never throws.
            progress?.Report(new ModelDownloadProgress(100, 0, ModelDownloadPhase.Completed));
        }
    }

    private void OnEnteredLobby(object? sender, EventArgs e)
    {
        // Only meaningful while joining; ignore late/duplicate signals.
        lock (_stateLock)
        {
            if (_state != MeetingAttendeeState.Joining) return;
        }
        TransitionState(MeetingAttendeeState.InLobby);
    }

    internal static string BuildDisplayName(string? userDisplayName)
    {
        var name = string.IsNullOrWhiteSpace(userDisplayName) ? DefaultUserName : userDisplayName.Trim();
        return string.Format(DisplayNameFormat, name);
    }

    private void TransitionState(MeetingAttendeeState newState)
    {
        EventHandler<MeetingAttendeeState>? handler;
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

        // Now it is safe to await the background loop: StopAsync cancelled its CTS, so it has either
        // returned already or will observe the cancellation and exit without re-entering StopAsync.
        var loop = _watchLoop;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "End-watch loop fault on dispose"); }
        }

        _watchCts?.Dispose();
        _watchCts = null;
        _watchLoop = null;

        // Dispose the start CTS here (not in StopAsync, where a still-running StartAsync may read its
        // token) and the teardown gate now that no further DisposeAllAsync can run.
        _startCts?.Dispose();
        _startCts = null;
        _disposeGate.Dispose();

        _utterances.Writer.TryComplete();
    }
}
