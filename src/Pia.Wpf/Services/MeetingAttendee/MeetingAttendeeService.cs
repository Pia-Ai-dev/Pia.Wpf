using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Orchestrates the meeting attendee. Modelled closely on
/// <see cref="LiveMeetingService"/>: it owns the browser session, the audio source, and one
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
    private readonly Func<CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine)>> _createTranscription;
    private readonly Func<string, IMeetingSession> _sessionFactory;
    // (session, usePerProcessLoopback) → source. usePerProcess is already resolved against the
    // settings flag + PID availability by the orchestrator, so the factory just builds the right one.
    private readonly Func<IMeetingSession, bool, IAudioCaptureSource> _audioSourceFactory;
    // Builds AND starts the transcription engine service, returning it as IAsyncDisposable (the only
    // surface the orchestrator needs). Folding start into the factory keeps the engine service a clean
    // seam — tests substitute an observable IAsyncDisposable instead of spinning real reader loops.
    private readonly Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, CancellationToken, Task<IAsyncDisposable>> _engineServiceFactory;

    private readonly Channel<TranscriptUtterance> _utterances;
    private readonly object _stateLock = new();
    private MeetingAttendeeState _state = MeetingAttendeeState.Idle;

    private IMeetingSession? _session;
    private IAudioCaptureSource? _audioSource;
    private IAsyncDisposable? _engineService;
    private ITranscriptionEngine? _transcriptionEngine;

    // The background loop that awaits the meeting's natural end then stops us. Owns its own CTS so
    // StopAsync can cancel WaitForEndAsync without awaiting (and thus deadlocking) the loop itself.
    private Task? _watchLoop;
    private CancellationTokenSource? _watchCts;

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
            createTranscription: async ct =>
            {
                var settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                var log = loggerFactory.CreateLogger<MeetingAttendeeService>();
                var sileroPath = await LiveTranscriptionModels
                    .EnsureSileroVadAsync(httpClientFactory, log, ct).ConfigureAwait(false);
                var engine = await TranscriptionEngineFactory
                    .CreateAsync(settings, httpClientFactory, downloadProgress: null, log, ct).ConfigureAwait(false);
                return (sileroPath, engine);
            },
            sessionFactory: chromiumPath => new TeamsMeetingSession(
                chromiumPath,
                httpClientFactory,
                loggerFactory.CreateLogger<TeamsMeetingSession>()),
            audioSourceFactory: null,
            engineServiceFactory: async (source, sileroPath, engine, sink, ct) =>
            {
                var svc = new LiveTranscriptionEngineService(
                    TranscriptSpeaker.Them,
                    source,
                    sileroPath,
                    engine,
                    sink,
                    loggerFactory.CreateLogger<LiveTranscriptionEngineService>());
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
        Func<CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine)>> createTranscription,
        Func<string, IMeetingSession> sessionFactory,
        Func<IMeetingSession, bool, IAudioCaptureSource>? audioSourceFactory,
        Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, CancellationToken, Task<IAsyncDisposable>> engineServiceFactory)
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

    public async Task StartAsync(string meetingUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingUrl);

        lock (_stateLock)
        {
            if (_state is not (MeetingAttendeeState.Idle or MeetingAttendeeState.Error))
                throw new InvalidOperationException($"Cannot start while {_state}");
        }

        TransitionState(MeetingAttendeeState.ProvisioningBrowser);

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            var displayName = BuildDisplayName(settings.SyncUserDisplayName);

            // 1) Browser on disk (idempotent; skips fast when cached).
            var chromiumPath = await _provisionChromium(null, cancellationToken).ConfigureAwait(false);

            // 2) Models — Silero VAD + the sherpa engine — before we join, mirroring LiveMeetingService.
            var (sileroPath, engine) = await _createTranscription(cancellationToken).ConfigureAwait(false);
            _transcriptionEngine = engine;

            // 3) Join. Subscribe to the lobby signal BEFORE joining so InLobby is observable even if it
            //    fires during JoinAsync. Admitted-immediately meetings skip InLobby (Joining → Attending).
            var session = _sessionFactory(chromiumPath);
            _session = session;
            session.EnteredLobby += OnEnteredLobby;

            TransitionState(MeetingAttendeeState.Joining);
            await session.JoinAsync(meetingUrl, displayName, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // 4) Audio source + transcription engine. Default = endpoint loopback; per-process only when
            //    the AppSettings flag is set AND the browser PID is known.
            var source = ResolveAudioSource(session, settings);
            _audioSource = source;
            await source.StartAsync(cancellationToken).ConfigureAwait(false);

            _engineService = await _engineServiceFactory(source, sileroPath, engine, _utterances.Writer, cancellationToken)
                .ConfigureAwait(false);

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
    /// <see cref="LiveMeetingService"/>'s teardown: engine service → audio source → meeting session →
    /// transcription engine. Each step is null-guarded and its exception swallowed so one failure does
    /// not abort the rest. Called both on the error path (where only the session/engine may exist) and
    /// on normal stop.
    /// </summary>
    private async Task DisposeAllAsync()
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

        _utterances.Writer.TryComplete();
    }
}
