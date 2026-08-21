using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Exceptions;
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
    private readonly IDefaultBrowserResolver _defaultBrowserResolver;

    // ---- Injected seams ---------------------------------------------------------------------------
    private readonly Func<IProgress<ChromiumDownloadProgress>?, CancellationToken, Task<string>> _provisionChromium;
    // Takes an optional IProgress<ModelDownloadProgress> threaded down to the speaker-embedding model
    // download so the VM can surface a progress dialog — mirrors the _provisionChromium IProgress seam
    // above. Silero VAD + the sherpa engine remain silent (no progress); only the OPTIONAL speaker model
    // reports, and only when it actually downloads.
    private readonly Func<IProgress<ModelDownloadProgress>?, CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>> _createTranscription;
    private readonly Func<BrowserLaunchSpec, IMeetingSession> _sessionFactory;
    // (session, usePerProcessLoopback) → source. usePerProcess is already resolved against the
    // settings flag + PID availability by the orchestrator, so the factory just builds the right one.
    private readonly Func<IMeetingSession, bool, IAudioCaptureSource> _audioSourceFactory;
    // Builds AND starts the transcription engine service, returning it as IAsyncDisposable (the only
    // surface the orchestrator needs). Folding start into the factory keeps the engine service a clean
    // seam — tests substitute an observable IAsyncDisposable instead of spinning real reader loops.
    private readonly Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, int, CancellationToken, Task<IAsyncDisposable>> _engineServiceFactory;

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

    // The background loop that periodically snapshots the Teams roster and accumulates the union of
    // names into _attendees (read back by the ViewModel for the summary prompt). Own CTS, cancelled by
    // StopAsync and awaited by DisposeAsync, mirroring the end-watch loop. Null when roster snapshots
    // are disabled (interval <= 0) or before a meeting starts.
    private Task? _rosterLoop;
    private CancellationTokenSource? _rosterCts;

    // Union of roster names seen this meeting, first-seen order, bot's own name excluded. Guarded by its
    // own lock (mutated on the roster loop, read on the UI thread via ObservedAttendees). Cleared per start.
    private readonly object _attendeesLock = new();
    private readonly List<string> _attendees = new();

    // Cancels the active StartAsync. Linked to the caller's token so Stop can abort the join even when
    // the caller passed CancellationToken.None (the VM and tests do). Without this, the long-lived join
    // (WaitForAdmissionAsync polls up to 120s) would race a concurrent Stop/teardown.
    private CancellationTokenSource? _startCts;

    public MeetingAttendeeState State
    {
        get { lock (_stateLock) return _state; }
    }

    public event EventHandler<MeetingAttendeeState>? StateChanged;

    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;

    private void OnSpeakersReassigned(object? sender, IReadOnlyList<SpeakerReassignment> changes)
        => SpeakersReassigned?.Invoke(this, changes);

    public ChannelReader<TranscriptUtterance> Utterances => _utterances.Reader;

    public IReadOnlyCollection<string> ObservedAttendees
    {
        get { lock (_attendeesLock) return _attendees.ToArray(); }
    }

    /// <summary>Production constructor (used by DI). Wires default seams over the real dependencies.</summary>
    public MeetingAttendeeService(
        ISettingsService settingsService,
        IBrowserProvisioner browserProvisioner,
        IHttpClientFactory httpClientFactory,
        IDefaultBrowserResolver defaultBrowserResolver,
        ILoggerFactory loggerFactory)
        : this(
            settingsService,
            loggerFactory,
            provisionChromium: (progress, ct) => browserProvisioner.EnsureChromiumAsync(progress, ct),
            createTranscription: CreateProductionTranscriptionFactory(settingsService, httpClientFactory, loggerFactory),
            sessionFactory: spec => new TeamsMeetingSession(
                spec,
                httpClientFactory,
                loggerFactory.CreateLogger<TeamsMeetingSession>()),
            audioSourceFactory: null,
            engineServiceFactory: CreateEngineServiceFactory(loggerFactory),
            defaultBrowserResolver: defaultBrowserResolver)
    {
    }

    /// <summary>
    /// Builds the real model/engine bootstrapper. Extracted so the DEBUG-only file-audio composition
    /// in <c>Bootstrapper</c> can reuse the exact same Silero/STT/diarizer construction instead of a
    /// second, divergent copy.
    /// </summary>
    internal static Func<IProgress<ModelDownloadProgress>?, CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>> CreateProductionTranscriptionFactory(
        ISettingsService settingsService, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        return async (speakerProgress, ct) =>
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
        };
    }

    /// <summary>Builds the real engine-service factory. Extracted for the same reason as
    /// <see cref="CreateProductionTranscriptionFactory"/>.</summary>
    internal static Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, int, CancellationToken, Task<IAsyncDisposable>> CreateEngineServiceFactory(
        ILoggerFactory loggerFactory)
    {
        return async (source, sileroPath, engine, sink, speakerId, minDiarizationSamples, ct) =>
        {
            var svc = new LiveTranscriptionEngineService(
                TranscriptSpeaker.Them,
                source,
                sileroPath,
                engine,
                sink,
                loggerFactory.CreateLogger<LiveTranscriptionEngineService>(),
                speakerId,
                minDiarizationSamples);
            await svc.StartAsync(ct).ConfigureAwait(false);
            return svc;
        };
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
        Func<BrowserLaunchSpec, IMeetingSession> sessionFactory,
        Func<IMeetingSession, bool, IAudioCaptureSource>? audioSourceFactory,
        Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, int, CancellationToken, Task<IAsyncDisposable>> engineServiceFactory,
        IDefaultBrowserResolver? defaultBrowserResolver = null)
    {
        _settingsService = settingsService;
        _logger = loggerFactory.CreateLogger<MeetingAttendeeService>();

        _provisionChromium = provisionChromium;
        _createTranscription = createTranscription;
        _sessionFactory = sessionFactory;
        _audioSourceFactory = audioSourceFactory
            ?? ((session, usePerProcess) => CreateDefaultAudioSource(session, usePerProcess, loggerFactory));
        _engineServiceFactory = engineServiceFactory;
        // Tests that don't exercise SystemDefault can omit the resolver; default to "always bundled".
        _defaultBrowserResolver = defaultBrowserResolver ?? new AlwaysBundledBrowserResolver();

        _utterances = UtteranceChannel.CreateBounded();
    }

    /// <summary>Default resolver for the seam ctor: maps SystemDefault to bundled with no registry read.</summary>
    private sealed class AlwaysBundledBrowserResolver : IDefaultBrowserResolver
    {
        public MeetingBrowserSelection ResolveChromiumSelectionOrBundled()
            => MeetingBrowserSelection.BundledChromium;
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

        // Fresh roster per meeting: discard any names retained from the previous one.
        lock (_attendeesLock) _attendees.Clear();

        TransitionState(MeetingAttendeeState.ProvisioningBrowser);

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            // Prefer the user-edited name from the join dialog (persisted in settings); fall back to the
            // auto-built "{user}'s assistant" when it was never set or left blank.
            var displayName = string.IsNullOrWhiteSpace(settings.MeetingAttendeeDisplayName)
                ? BuildDisplayName(settings.SyncUserDisplayName)
                : settings.MeetingAttendeeDisplayName.Trim();

            // 1) Resolve the browser launch spec from settings. For the bundled selection this provisions
            //    Chromium on disk (idempotent; skips fast when cached); Channel selections skip provisioning.
            var spec = await ResolveLaunchSpecAsync(settings, startToken).ConfigureAwait(false);

            // 2) Models — Silero VAD + the sherpa engine — before we join, mirroring LiveMeetingService.
            //    The speaker-ID service degrades to null INSIDE the closure, so this await cannot throw on a
            //    speaker-model failure — StartAsync still reaches Attending; only a Silero/engine failure is fatal.
            var (sileroPath, engine, speakerId) = await _createTranscription(speakerModelProgress, startToken).ConfigureAwait(false);
            startToken.ThrowIfCancellationRequested();
            _transcriptionEngine = engine;
            _speakerId = speakerId;

            if (_speakerId is not null)
                _speakerId.SpeakersReassigned += OnSpeakersReassigned;

            // 3) Join. Subscribe to the lobby signal BEFORE joining so InLobby is observable even if it
            //    fires during JoinAsync. Admitted-immediately meetings skip InLobby (Joining → Attending).
            //    A system browser (Chrome/Edge channel) that fails to LAUNCH degrades once to bundled.
            TransitionState(MeetingAttendeeState.Joining);
            var session = await JoinWithBrowserFallbackAsync(spec, meetingUrl, displayName, startToken)
                .ConfigureAwait(false);

            // Re-check after the long-lived join (WaitForAdmissionAsync polls up to 120s): if Stop ran
            // while we were joining it cancelled startToken and already owns teardown — bail before we
            // hand the now-disposed source/engine onward or clobber the Idle state Stop set.
            startToken.ThrowIfCancellationRequested();

            // 4) Audio source + transcription engine. Default = endpoint loopback (audible); silent
            //    in-browser capture when the window is hidden — with a dispose-then-degrade fallback to
            //    the audible endpoint loopback if the silent path fails (disposing the silent source
            //    unmutes the meeting, so the degrade is actually audible).
            var useSilentCapture = UseSilentBrowserCapture(settings);
            var source = _audioSourceFactory(session, useSilentCapture);
            _audioSource = source;
            try
            {
                await source.StartAsync(startToken).ConfigureAwait(false);
                if (useSilentCapture)
                    _logger.LogInformation("Meeting attendee using silent in-browser audio capture");
            }
            catch (Exception ex) when (useSilentCapture && ex is not OperationCanceledException)
            {
                // Silent in-browser capture failed to produce audio (e.g. the in-page hook captured no
                // remote track, or the tap could not be armed). Dispose it FIRST — that runs the source's
                // teardown, which calls StopAudioCaptureAsync and UNMUTES the meeting — then degrade to the
                // audible endpoint loopback so the meeting is never lost to a silent-capture failure (it
                // becomes "hidden but audible" rather than "silent and untranscribed").
                _logger.LogWarning(ex,
                    "Silent in-browser capture failed to start; degrading to audible endpoint loopback");
                await source.DisposeAsync().ConfigureAwait(false);
                _audioSource = null;

                source = _audioSourceFactory(session, /* useSilentCapture: */ false);
                _audioSource = source;
                await source.StartAsync(startToken).ConfigureAwait(false);
            }

            startToken.ThrowIfCancellationRequested();
            var minSpeechSeconds = settings.MeetingSmartSpeakerDetection ? 1.5f : settings.MeetingMinSpeechSeconds;
            var minDiarizationSamples = (int)System.Math.Round(minSpeechSeconds * 16000);
            _engineService = await _engineServiceFactory(source, sileroPath, engine, _utterances.Writer, _speakerId, minDiarizationSamples, startToken)
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

            // 6) Background roster snapshots: accumulate the participant names so the post-meeting summary
            //    can attribute the diarized speakers. Best-effort and entirely separable from transcription —
            //    disabled when the interval is <= 0. Own CTS (cancelled by StopAsync, awaited by DisposeAsync).
            if (settings.MeetingAttendeeRosterSnapshotMinutes > 0)
            {
                var interval = TimeSpan.FromMinutes(settings.MeetingAttendeeRosterSnapshotMinutes);
                _rosterCts?.Dispose();
                _rosterCts = new CancellationTokenSource();
                _rosterLoop = Task.Run(() => PollRosterAsync(session, displayName, interval, _rosterCts.Token));
            }
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

            // Cancel the roster-snapshot loop too (it never re-enters StopAsync, but cancelling here stops
            // further page reads promptly before LeaveAsync/teardown closes the browser). DisposeAsync awaits it.
            _rosterCts?.Cancel();

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
    /// Resolves the configured <see cref="MeetingBrowserSelection"/> + window-visibility preference into
    /// a concrete <see cref="BrowserLaunchSpec"/>. Bundled provisions Chromium on disk (the only
    /// Playwright-guaranteed build); System Chrome/Edge launch via the Playwright channel and resolve a
    /// match-path from App Paths so the per-process audio + taskbar features can still find the PID;
    /// SystemDefault resolves the OS default to a Chromium-family selection, falling back to bundled.
    /// </summary>
    internal async Task<BrowserLaunchSpec> ResolveLaunchSpecAsync(AppSettings settings, CancellationToken ct)
    {
        var show = settings.MeetingAttendeeShowBrowserWindow;
        var selection = settings.MeetingBrowserSelection;

        // #3: resolve "system default" to a concrete Chromium-family selection, or fall back to bundled
        // when the OS default is non-Chromium / unknown (the resolver never throws).
        if (selection == MeetingBrowserSelection.SystemDefault)
            selection = _defaultBrowserResolver.ResolveChromiumSelectionOrBundled();

        switch (selection)
        {
            case MeetingBrowserSelection.SystemChrome:
                return new BrowserLaunchSpec(null, "chrome", "chrome", ResolveAppPath("chrome.exe"), show);
            case MeetingBrowserSelection.SystemEdge:
                return new BrowserLaunchSpec(null, "msedge", "msedge", ResolveAppPath("msedge.exe"), show);
            case MeetingBrowserSelection.BundledChromium:
            default:
                var path = await _provisionChromium(null, ct).ConfigureAwait(false);   // ~150 MB on first run
                return new BrowserLaunchSpec(path, null, "chrome", path, show);
        }
    }

    /// <summary>
    /// Builds a session for <paramref name="spec"/> and joins. If a system browser (Chrome/Edge channel)
    /// fails to <b>launch</b> (absent / enterprise-policy block), degrades once to bundled Chromium — the
    /// only always-available build — rather than failing the join. A non-launch failure (e.g. never
    /// admitted), or a bundled launch failure, propagates.
    /// </summary>
    private async Task<IMeetingSession> JoinWithBrowserFallbackAsync(
        BrowserLaunchSpec spec, string meetingUrl, string displayName, CancellationToken ct)
    {
        try
        {
            return await BuildSessionAndJoinAsync(spec, meetingUrl, displayName, ct).ConfigureAwait(false);
        }
        catch (BrowserLaunchException ex) when (spec.Channel is not null)
        {
            _logger.LogWarning(ex,
                "System browser channel failed to launch; falling back to bundled Chromium for this meeting");

            // Detach + dispose the failed session before rebuilding so its EnteredLobby handler is removed
            // and no half-initialised browser is leaked.
            await DisposeFailedSessionAsync().ConfigureAwait(false);

            var bundledPath = await _provisionChromium(null, ct).ConfigureAwait(false);
            var bundledSpec = new BrowserLaunchSpec(bundledPath, null, "chrome", bundledPath, spec.ShowWindow);
            return await BuildSessionAndJoinAsync(bundledSpec, meetingUrl, displayName, ct).ConfigureAwait(false);
        }
    }

    private async Task<IMeetingSession> BuildSessionAndJoinAsync(
        BrowserLaunchSpec spec, string meetingUrl, string displayName, CancellationToken ct)
    {
        var session = _sessionFactory(spec);
        _session = session;
        session.EnteredLobby += OnEnteredLobby;
        await session.JoinAsync(meetingUrl, displayName, ct).ConfigureAwait(false);
        return session;
    }

    private async Task DisposeFailedSessionAsync()
    {
        var failed = _session;
        if (failed is null) return;
        _session = null;
        failed.EnteredLobby -= OnEnteredLobby;
        try { await failed.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disposing the failed browser session threw"); }
    }

    /// <summary>
    /// Reads the registered executable path for <paramref name="exe"/> from
    /// <c>SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\&lt;exe&gt;</c> (HKLM first, then HKCU),
    /// or null if absent/unreadable. This supplies a <see cref="BrowserLaunchSpec.MatchExecutablePath"/>
    /// for Channel launches so per-process audio + taskbar-hiding can still find the browser PID; a null
    /// result degrades PID matching to process-name + new-since-launch only.
    /// </summary>
    private static string? ResolveAppPath(string exe)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(subKey + exe);
                if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                    return path.Trim('"');
            }
            catch
            {
                // Registry access denied / malformed — fall through to the next root, then null.
            }
        }
        return null;
    }

    /// <summary>
    /// Renames a diarized speaker label on the live diarizer (in-memory, current meeting only). A no-op
    /// when <see cref="_speakerId"/> is null (diarization off, or degraded-to-null on model failure) — it
    /// must not throw. Thread-safe against a concurrent in-flight identify: the rename takes the same
    /// <c>_lock</c> the speaker-identification service holds.
    /// </summary>
    public void RenameSpeaker(string oldLabel, string newLabel) => _speakerId?.Rename(oldLabel, newLabel);

    /// <summary>
    /// Periodically snapshots the meeting roster (an immediate first snapshot, then every
    /// <paramref name="interval"/>) and folds each into <see cref="_attendees"/>. Best-effort: a failed
    /// snapshot is logged at Debug and the loop continues; cancellation ends it cleanly. Never throws into
    /// the caller, so it cannot affect the meeting.
    /// </summary>
    private async Task PollRosterAsync(
        IMeetingSession session, string botDisplayName, TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var names = await session.GetAttendeeNamesAsync(token).ConfigureAwait(false);
                if (names is { Count: > 0 })
                    AccumulateAttendees(names, botDisplayName);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Don't log the names (sensitive); only that a snapshot failed.
                _logger.LogDebug(ex, "Roster snapshot failed; will retry on the next interval");
            }

            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Folds a snapshot of names into the accumulated union: trims, drops blanks, excludes the attendee's
    /// own display name, and de-duplicates case-insensitively while preserving first-seen order. The
    /// union size then feeds the diarizer as a speaker-count ceiling — Pia joins as its own participant,
    /// so the roster and the diarized voices count the same people.
    /// </summary>
    private void AccumulateAttendees(IReadOnlyList<string> names, string botDisplayName)
    {
        int count;
        lock (_attendeesLock)
        {
            foreach (var raw in names)
            {
                var name = CleanAttendeeName(raw);
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, botDisplayName, StringComparison.OrdinalIgnoreCase)) continue;
                if (_attendees.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) continue;
                _attendees.Add(name);
            }
            count = _attendees.Count;
        }

        // The union only grows, so the ceiling refines monotonically. Polling off or every snapshot
        // failing leaves it at 0 and the diarizer unconstrained.
        if (count > 0) _speakerId?.SetExpectedSpeakers(count);
    }

    /// <summary>
    /// Normalizes a raw roster string to a bare display name. Teams renders self/status suffixes the
    /// row extractor cannot always strip — "Alex's assistant (You)", "Marco, Organizer", "Jane (Guest)".
    /// Takes the first line, then drops a trailing parenthetical and everything from the first comma, so
    /// the bot's own row collapses back to its plain display name (and is then excluded by equality) and
    /// real names don't carry trailing status text. Pure + internal so it can be unit-tested without a DOM.
    /// </summary>
    internal static string CleanAttendeeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var name = raw.Trim();

        // First line only (a row's text node can stack "Name\nMuted\nOrganizer").
        var newline = name.IndexOfAny(['\r', '\n']);
        if (newline >= 0) name = name[..newline];

        // Everything from the first comma is status/role ("Marco, Organizer, Muted").
        var comma = name.IndexOf(',');
        if (comma >= 0) name = name[..comma];
        name = name.Trim();

        // A trailing parenthetical is a status marker ("(You)", "(Guest)", "(External)").
        var paren = name.LastIndexOf('(');
        if (paren >= 0 && name.EndsWith(')')) name = name[..paren];

        return name.Trim();
    }

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
                _speakerId.SpeakersReassigned -= OnSpeakersReassigned;
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

    /// <summary>
    /// Pure decision: use the silent in-browser audio capture when the browser window is hidden (so the
    /// user wants the meeting inaudible on this device). A visible window keeps the audible endpoint
    /// loopback. The user-facing contract is <i>hidden ⇒ silent</i>, so silence is derived from
    /// <see cref="AppSettings.MeetingAttendeeShowBrowserWindow"/> rather than a separate toggle. Unlike
    /// the retired per-process loopback path, the in-browser tap needs no browser PID.
    /// </summary>
    internal static bool UseSilentBrowserCapture(AppSettings settings)
        => !settings.MeetingAttendeeShowBrowserWindow;

    /// <summary>
    /// The production audio-source factory, exposed so a dev-only decorator can wrap it instead of
    /// replacing it — mirroring <see cref="CreateProductionTranscriptionFactory"/>.
    /// </summary>
    internal static Func<IMeetingSession, bool, IAudioCaptureSource> CreateDefaultAudioSourceFactory(
        ILoggerFactory loggerFactory)
        => (session, useSilentCapture) => CreateDefaultAudioSource(session, useSilentCapture, loggerFactory);

    private static IAudioCaptureSource CreateDefaultAudioSource(
        IMeetingSession session, bool useSilentCapture, ILoggerFactory loggerFactory)
    {
        // Silent capture (hidden window) taps the meeting audio inside the browser and mutes the
        // speakers; otherwise the proven endpoint loopback (audible) is the default.
        if (useSilentCapture)
        {
            return new BrowserAudioCaptureService(
                session, loggerFactory.CreateLogger<BrowserAudioCaptureService>());
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
            if (settings.MeetingSmartSpeakerDetection)
            {
                return new AdaptiveSpeakerIdentificationService(
                    new SherpaEmbeddingExtractor(speakerModelPath),
                    loggerFactory.CreateLogger<AdaptiveSpeakerIdentificationService>());
            }
            return new SpeakerIdentificationService(
                speakerModelPath,
                settings.SpeakerEmbeddingThreshold,
                settings.MeetingMaxSpeakers,
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

        // Same for the roster-snapshot loop: StopAsync (called above) cancelled its CTS, so awaiting it
        // here is safe — it never re-enters StopAsync.
        var rosterLoop = _rosterLoop;
        if (rosterLoop is not null)
        {
            try { await rosterLoop.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Roster-snapshot loop fault on dispose"); }
        }

        _watchCts?.Dispose();
        _watchCts = null;
        _watchLoop = null;
        _rosterCts?.Dispose();
        _rosterCts = null;
        _rosterLoop = null;

        // Dispose the start CTS here (not in StopAsync, where a still-running StartAsync may read its
        // token) and the teardown gate now that no further DisposeAllAsync can run.
        _startCts?.Dispose();
        _startCts = null;
        _disposeGate.Dispose();

        _utterances.Writer.TryComplete();
    }
}
