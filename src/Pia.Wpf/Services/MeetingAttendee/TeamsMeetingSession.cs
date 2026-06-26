using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Pia.Logging;
using Pia.Services.Exceptions;

// Microsoft.Playwright 1.59 marks LocatorIsVisibleOptions.Timeout [Obsolete] (CS0612) but ships no
// replacement on the options object; the per-probe timeout is load-bearing for the lobby/admission
// polling here, so we keep using it deliberately rather than change the runtime behavior of this port.
#pragma warning disable CS0612

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Joins a Microsoft Teams meeting through an automated Chromium browser (Microsoft.Playwright) and
/// keeps the session alive for the meeting's duration so its audio can be captured by the live
/// transcription pipeline.
///
/// The browser is launched <b>headed but positioned far off-screen</b>: a headed browser gives a
/// real audio render session (the whole reason the attendee can hear the meeting), while the
/// off-screen window position keeps it invisible. The bot joins <b>muted</b> — microphone and
/// camera permissions are simply not granted (Playwright denies by default, with no hanging OS
/// prompt). At the Chromium level audio <b>output</b> is left untouched (we never pass
/// <c>--mute-audio</c> or a fake playback device), so the meeting still renders and can be captured.
///
/// <para>On the <b>hidden (silent) path</b> the meeting must not be audible on the device — otherwise
/// the user attending the same call on the same machine gets an echo. Silence is achieved
/// <i>in-page</i> (not by muting Chromium, which would also kill capture): an init script wraps
/// <c>RTCPeerConnection</c> to collect the inbound audio tracks, and <see cref="StartAudioCaptureAsync"/>
/// taps them through Web Audio while muting the page's media elements. See those members and
/// <c>BrowserAudioCaptureService</c>.</para>
///
/// The join flow is ported from the Node/Playwright blueprint
/// (<c>microsoft-teams-meeting-bot/.../join-procedure.ts</c>): resolve the launcher URL, go to it,
/// "Continue on this browser", type the name, "Join now", wait through the lobby until admitted.
///
/// <para><b>UNVERIFIED:</b> the live join flow, off-screen-headed audio rendering, and PID capture
/// cannot be exercised in this environment. Only <see cref="TeamsMeetingUrl.BuildLauncherUrl"/> is
/// unit-tested. See the class TODOs and the unit handover.</para>
/// </summary>
public sealed class TeamsMeetingSession : IMeetingSession
{
    /// <summary>
    /// Name of the dedicated <see cref="HttpClient"/> used to resolve the Teams meeting-URL redirect.
    /// The meeting URL is sensitive (it embeds the meeting context / launch params and effectively
    /// grants join access). The URL is kept out of support-attachable logs by the app-wide
    /// <c>SafeUrl</c> sanitisation in <c>HttpLoggingHandler</c>: in Release builds every URL is
    /// reduced to <c>{scheme}://host-NNN</c> (host-only, path and query stripped), so the meeting
    /// join-secret never reaches logs at any level. In Debug builds the full URL appears in
    /// Debug-level output only, which is not included in support attachments.
    /// </summary>
    public const string MeetingRedirectHttpClientName = "meeting-redirect";

    // ---- Centralized selectors / page text (ported from join-procedure.ts) ------------------
    private const string ContinueOnWebSelector = "button[data-tid=\"joinOnWeb\"]";
    private const string NameInputSelector = "input[placeholder=\"Type your name\"]";
    private const string JoinNowSelector = "button:has-text(\"Join now\")";
    private const string HangupButtonSelector = "button[id=\"hangup-button\"]";
    private const string LobbyText = "Someone will let you in shortly";
    /// <summary>Fluent UI (Northstar) modal backdrop that can layer over the prejoin screen.</summary>
    private const string DialogOverlaySelector = ".ui-dialog__overlay";
    /// <summary>
    /// Button inside the "Are you sure you don't want audio or video?" (get-user-media) modal the Teams
    /// web prejoin shows when the bot grants no mic/camera permission. Clicking it dismisses the modal
    /// and lets the join proceed. Verified against the live DOM (2026-06-26): the modal does NOT close on
    /// Escape, which is why the prior Escape-only dismissal left the <see cref="DialogOverlaySelector"/>
    /// scrim intercepting the "Join now" click. <c>data-focus-target</c> is stable across the localized
    /// button label.
    /// </summary>
    private const string GumContinueSelector = "button[data-focus-target=\"gum-continue\"]";

    // ---- Roster (participant list) ----------------------------------------------------------------
    /// <summary>
    /// Candidate selectors for the toggle that opens the "People" roster panel. Joined with commas so
    /// Playwright matches the first that exists; the Teams web client has renamed this control across
    /// versions (legacy <c>#roster-button</c>, newer aria-labelled buttons). UNVERIFIED in this
    /// environment — refined from the DEBUG roster-DOM sample logged on the first read.
    /// </summary>
    private const string RosterButtonSelector =
        "#roster-button, button[data-tid=\"roster-button\"], " +
        "button[aria-label*=\"People\" i], button[aria-label*=\"participant\" i]";
    /// <summary>
    /// Selector matching a single roster row; used only to detect that the panel is populated. The
    /// verified person-row marker is <c>[data-tid^="attendeesInMeeting-..."]</c>; <c>[role="treeitem"]</c>
    /// is kept as a looser fallback (it also matches the "In this meeting (N)" section-header row, so it
    /// is used only for the populated-check, never for name extraction).
    /// </summary>
    private const string RosterItemSelector = "[data-tid^=\"attendeesInMeeting-\"], [role=\"treeitem\"]";

    /// <summary>
    /// In-page extractor for the visible roster names. Tries a few row selectors in priority order and,
    /// per row, prefers an explicit title node, then the row's aria-label, then its text — taking the
    /// first line so trailing status text ("Muted", role) is dropped. Returns a (possibly empty) string
    /// array. Kept resilient to selector drift because the live DOM cannot be observed here.
    /// </summary>
    private const string RosterNamesScript = """
        () => {
          // Verified against the live Teams web roster (2026-06-26): each person row in the People panel
          // is a [data-tid="attendeesInMeeting-<display name>"] element whose aria-label is
          // "<name>, <role?>, <mute state>", so the name is the first comma-separated segment. The older
          // roster-list-item / roster-list-title / participantStatesText selectors no longer exist.
          let rows = Array.from(document.querySelectorAll('[data-tid^="attendeesInMeeting-"]'));
          // Fallback when the panel is not open: the on-stage video tiles carry the display name as the
          // data-tid of a node inside a [role="menuitem"]; filter out the non-name helper tids.
          if (!rows.length) {
            rows = Array.from(document.querySelectorAll('[role="menuitem"] [data-tid]'))
              .filter(e => {
                const t = e.getAttribute('data-tid') || '';
                return t && !/^(participant-avatar|voice-level|ai-interpreter)/.test(t);
              });
          }
          const names = [];
          for (const r of rows) {
            let name = '';
            const aria = r.getAttribute('aria-label');
            if (aria) name = aria.split(',')[0].trim();
            if (!name) name = (r.getAttribute('data-tid') || '').replace(/^attendeesInMeeting-/, '').trim();
            if (name) names.push(name);
          }
          return names;
        }
        """;

    /// <summary>In-page capture of the roster region's markup, logged once (DEBUG) to refine the selectors.</summary>
    private const string RosterDomScript = """
        () => {
          const panel = document.querySelector('[data-tid^="calling-roster"], [role="tree"], [data-tid="roster-list"]');
          return panel ? panel.outerHTML : (document.body ? document.body.innerHTML.slice(0, 4000) : '');
        }
        """;

    // ---- Silent in-browser audio capture (hidden path) -------------------------------------------
    /// <summary>Name of the Playwright function the page calls to ship PCM/format to the host.</summary>
    private const string AudioBindingName = "__piaAudioSink";

    /// <summary>
    /// Init script (added to the context BEFORE the first navigation, hidden path only) that wraps
    /// <c>RTCPeerConnection</c> so every inbound (remote) audio track is collected into
    /// <c>window.__piaRemoteTracks</c> as Teams negotiates the call. It must run before Teams creates
    /// its peer connection, hence the init script. It does NOT mute anything and does NOT start capture
    /// — muting is deferred to <see cref="AudioCaptureStartScript"/> so a failed/degraded capture never
    /// leaves the meeting silent.
    /// </summary>
    private const string AudioHookInitScript = """
        (() => {
          if (window.__piaAudioHooked) return;
          window.__piaAudioHooked = true;
          const remoteTracks = new Set();
          window.__piaRemoteTracks = remoteTracks;
          const OrigPC = window.RTCPeerConnection;
          if (!OrigPC) return;
          const Wrapped = function (...args) {
            const pc = new OrigPC(...args);
            try {
              pc.addEventListener('track', (e) => {
                try {
                  const tr = e && e.track;
                  if (tr && tr.kind === 'audio') {
                    remoteTracks.add(tr);
                    tr.addEventListener('ended', () => { try { remoteTracks.delete(tr); } catch (_) {} });
                    if (typeof window.__piaConnectTrack === 'function') window.__piaConnectTrack(tr);
                  }
                } catch (_) {}
              });
            } catch (_) {}
            return pc;
          };
          Wrapped.prototype = OrigPC.prototype;
          try { Object.setPrototypeOf(Wrapped, OrigPC); } catch (_) {}
          try { window.RTCPeerConnection = Wrapped; } catch (_) {}
          try { window.webkitRTCPeerConnection = Wrapped; } catch (_) {}
        })();
        """;

    /// <summary>
    /// Started post-admission once the PCM binding exists. Builds a Web Audio graph that taps every
    /// collected remote track and a <c>ScriptProcessorNode → gain(0) → destination</c> chain — the
    /// gain-0 sink makes the graph pump (so <c>onaudioprocess</c> fires and we get PCM) while emitting
    /// silence. <c>ScriptProcessorNode</c> is used over <c>AudioWorklet</c> deliberately: it needs no
    /// blob/module URL, so Teams' CSP can't block it. Each remote track is also attached to a muted
    /// <c>&lt;audio&gt;</c> element — the known Chrome quirk (crbug 121673) is that a remote WebRTC
    /// track only feeds Web Audio if it is ALSO sunk to a media element; a muted element satisfies that
    /// without reaching the speakers. Speaker muting (sweep + observer + play hook) is armed only on the
    /// FIRST captured track, so a meeting with no remote audio (capture impossible) is never muted.
    /// Returns a short status string for host-side logging.
    /// </summary>
    private const string AudioCaptureStartScript = """
        () => {
          try {
            if (window.__piaCaptureStarted) return 'already';
            const post = window.__piaAudioSink;
            if (typeof post !== 'function') return 'no-binding';
            window.__piaCaptureStarted = true;

            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            window.__piaCtx = ctx;
            try { if (ctx.resume) ctx.resume(); } catch (_) {}
            const inputGain = ctx.createGain(); inputGain.gain.value = 1;
            const proc = ctx.createScriptProcessor(4096, 1, 1);
            const silent = ctx.createGain(); silent.gain.value = 0;
            let pumping = false;

            proc.onaudioprocess = (ev) => {
              try {
                const ch = ev.inputBuffer.getChannelData(0);
                const f32 = new Float32Array(ch);
                const bytes = new Uint8Array(f32.buffer);
                let bin = ''; const CH = 0x8000;
                for (let i = 0; i < bytes.length; i += CH) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + CH));
                post(window.btoa(bin));
              } catch (_) {}
            };

            const muteEl = (el) => { try { el.muted = true; el.volume = 0; } catch (_) {} };
            const sweep = () => { try { document.querySelectorAll('audio,video').forEach(muteEl); } catch (_) {} };
            window.__piaMuteState = { interval: 0, observer: null, origPlay: null };
            const beginMuting = () => {
              if (window.__piaMuteState.interval) return;
              sweep();
              try {
                const mo = new MutationObserver(() => sweep());
                mo.observe(document.documentElement, { subtree: true, childList: true });
                window.__piaMuteState.observer = mo;
              } catch (_) {}
              try {
                const proto = HTMLMediaElement.prototype;
                window.__piaMuteState.origPlay = proto.play;
                proto.play = function (...a) { muteEl(this); return window.__piaMuteState.origPlay.apply(this, a); };
              } catch (_) {}
              try { window.__piaMuteState.interval = window.setInterval(sweep, 1000); } catch (_) {}
            };

            const connected = new WeakSet();
            window.__piaConnectTrack = (tr) => {
              try {
                if (!tr || connected.has(tr)) return;
                connected.add(tr);
                const ms = new MediaStream([tr]);
                const a = document.createElement('audio'); a.muted = true; a.volume = 0; a.srcObject = ms;
                try { const p = a.play(); if (p && p.catch) p.catch(() => {}); } catch (_) {}
                (window.__piaSinks = window.__piaSinks || []).push(a);
                ctx.createMediaStreamSource(ms).connect(inputGain);
                if (!pumping) {
                  pumping = true;
                  inputGain.connect(proc); proc.connect(silent); silent.connect(ctx.destination);
                  try { if (ctx.resume) ctx.resume(); } catch (_) {}
                  beginMuting();
                }
              } catch (_) {}
            };

            post('f:' + Math.round(ctx.sampleRate) + ':1');
            Array.from(remoteTracks_()).forEach(window.__piaConnectTrack);
            function remoteTracks_() { return window.__piaRemoteTracks || []; }
            return 'started:' + (window.__piaRemoteTracks ? window.__piaRemoteTracks.size : 0);
          } catch (e) { return 'error:' + (e && e.message ? e.message : String(e)); }
        }
        """;

    /// <summary>Tears the in-page tap down and unmutes the page's media elements. Best-effort.</summary>
    private const string AudioCaptureStopScript = """
        () => {
          try {
            const st = window.__piaMuteState;
            if (st) {
              try { if (st.interval) window.clearInterval(st.interval); } catch (_) {}
              try { if (st.observer) st.observer.disconnect(); } catch (_) {}
              try { if (st.origPlay) HTMLMediaElement.prototype.play = st.origPlay; } catch (_) {}
            }
            try { document.querySelectorAll('audio,video').forEach((el) => { try { el.muted = false; el.volume = 1; } catch (_) {} }); } catch (_) {}
            try { (window.__piaSinks || []).forEach((a) => { try { a.srcObject = null; } catch (_) {} }); } catch (_) {}
            window.__piaSinks = [];
            try { if (window.__piaCtx && window.__piaCtx.close) window.__piaCtx.close(); } catch (_) {}
            window.__piaCtx = null;
            window.__piaCaptureStarted = false;
            window.__piaConnectTrack = null;
            return 'stopped';
          } catch (e) { return 'error:' + (e && e.message ? e.message : String(e)); }
        }
        """;

    // ---- Timeouts (ms) ----------------------------------------------------------------------
    private const float ContinueOnWebTimeoutMs = 30_000;
    private const float NameInputTimeoutMs = 15_000;
    /// <summary>How long, in total, we wait to be admitted after clicking "Join now".</summary>
    private const int AdmissionTimeoutMs = 120_000;
    /// <summary>Poll cadence while waiting in the lobby / waiting for the meeting to end.</summary>
    private const int PollIntervalMs = 2_000;
    /// <summary>Per-iteration probe timeout when polling the hangup control.</summary>
    private const float ProbeTimeoutMs = 1_000;
    /// <summary>Timeout for the real "Join now" click before falling back to a synthetic click.</summary>
    private const float JoinNowClickTimeoutMs = 10_000;
    /// <summary>How long we wait for a dismissed prejoin dialog overlay to detach.</summary>
    private const float DialogDismissTimeoutMs = 2_000;
    /// <summary>How long we wait for the roster panel to populate after clicking the People button.</summary>
    private const float RosterOpenTimeoutMs = 5_000;
    /// <summary>
    /// How long we wait for the hangup control to detach after clicking it, confirming the call is
    /// actually tearing down before we close the browser (so leaving never depends on an RTC timeout).
    /// </summary>
    private const float LeaveConfirmTimeoutMs = 5_000;
    /// <summary>Per-step budget for closing the Playwright context/browser before killing the process tree.</summary>
    private const int BrowserCloseTimeoutMs = 5_000;

    private readonly ILogger<TeamsMeetingSession> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BrowserLaunchSpec _launchSpec;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    private int? _browserProcessId;
    private bool _enteredLobbyRaised;
    private bool _rosterDomLogged;
    private bool _audioBindingExposed;

    // Serializes all page access that can run concurrently once the meeting is live: WaitForEndAsync's
    // 2 s hangup poll (on the orchestrator's watch loop), LeaveAsync's hangup probe, and
    // GetAttendeeNamesAsync (on the orchestrator's roster-snapshot loop). Playwright forbids concurrent
    // operations on one IPage, so without this the roster poll would intermittently collide with the
    // hangup poll. Acquired per individual page op and released before any Task.Delay, so the long-lived
    // poll never starves the roster read. Not disposed: its AvailableWaitHandle is never accessed, so it
    // allocates no unmanaged handle, and skipping Dispose sidesteps a teardown race with an in-flight read.
    private readonly SemaphoreSlim _pageGate = new(1, 1);

    public int? BrowserProcessId => _browserProcessId;

    public event EventHandler? EnteredLobby;

    public TeamsMeetingSession(
        BrowserLaunchSpec launchSpec,
        IHttpClientFactory httpClientFactory,
        ILogger<TeamsMeetingSession> logger)
    {
        _launchSpec = launchSpec ?? throw new ArgumentNullException(nameof(launchSpec));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger;
    }

    public async Task JoinAsync(string meetingUrl, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (_browser is not null)
            throw new InvalidOperationException("This session has already joined a meeting.");

        cancellationToken.ThrowIfCancellationRequested();

        // Resolve the launcher URL (follow the meeting-URL redirect) BEFORE handing it to Playwright,
        // then apply the pure transform. The redirect-follow is the only network step here; the URL
        // mutation itself is the unit-tested pure function.
        var launcherUrl = await ResolveLauncherUrlAsync(meetingUrl, cancellationToken).ConfigureAwait(false);

        // Snapshot existing chrome.exe PIDs so we can attribute the freshly-spawned root process.
        var preExistingChromePids = SnapshotChromiumPids();

        await LaunchBrowserAsync(cancellationToken).ConfigureAwait(false);

        _browserProcessId = ResolveBrowserProcessId(preExistingChromePids);

        // Hidden window ⇒ suppress its taskbar button so no orphan button appears. Best-effort: a miss
        // is cosmetic (a visible button), never a join failure. The on-screen window keeps its button.
        if (!_launchSpec.ShowWindow && _browserProcessId is int rootPid)
        {
            try
            {
                await BrowserWindowChrome.SuppressTaskbarButtonAsync(rootPid, _logger, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Suppressing the meeting browser taskbar button failed; continuing");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var page = _page ?? throw new InvalidOperationException("Browser page was not created.");

        // Step 1: open the launcher and continue on the web (rather than the native app).
        _logger.LogDebug("Navigating to Teams launcher URL");
        await page.GotoAsync(launcherUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
            .ConfigureAwait(false);

        await page.WaitForSelectorAsync(
            ContinueOnWebSelector,
            new PageWaitForSelectorOptions { Timeout = ContinueOnWebTimeoutMs }).ConfigureAwait(false);
        await page.ClickAsync(ContinueOnWebSelector).ConfigureAwait(false);
        _logger.LogDebug("Clicked 'Continue on this browser'");

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: enter the display name and join.
        await page.WaitForSelectorAsync(
            NameInputSelector,
            new PageWaitForSelectorOptions { Timeout = NameInputTimeoutMs }).ConfigureAwait(false);
        await page.FillAsync(NameInputSelector, displayName).ConfigureAwait(false);
        await page.WaitForSelectorAsync(JoinNowSelector).ConfigureAwait(false);

        // The new Teams web prejoin can layer a modal over the screen that swallows the click on
        // "Join now". Best-effort dismiss it, then click — falling back to a synthetic DOM click if
        // an overlay still intercepts the real pointer event.
        await DismissBlockingDialogAsync(page).ConfigureAwait(false);
        await ClickJoinNowAsync(page).ConfigureAwait(false);
        _logger.LogDebug("Clicked 'Join now'; awaiting admission");

        // Step 3: wait for admission. The bot may sit in the lobby ("Someone will let you in
        // shortly") until a host admits it; admission is signalled by the hangup control appearing.
        await WaitForAdmissionAsync(page, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Meeting attendee admitted to the call");
    }

    public async Task WaitForEndAsync(CancellationToken cancellationToken = default)
    {
        var page = _page;
        if (page is null) return;

        // Meetings can run for hours, so we cannot rely on a fixed Playwright timeout (and Playwright
        // WaitFor* does not accept a CancellationToken). Poll the hangup control: when it is no
        // longer visible — or the page/selector throws (navigation to a "call ended" page, closed
        // context) — the meeting has ended.
        while (!cancellationToken.IsCancellationRequested)
        {
            bool stillInCall;
            // Hold the page gate only for the probe itself (released before the delay below) so the
            // roster-snapshot loop can interleave its own page reads between iterations.
            await _pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                stillInCall = await page.Locator(HangupButtonSelector)
                    .First
                    .IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hangup probe failed; treating meeting as ended");
                return;
            }
            finally
            {
                _pageGate.Release();
            }

            if (!stillInCall)
            {
                _logger.LogDebug("Hangup control no longer visible; meeting ended");
                return;
            }

            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task LeaveAsync()
    {
        var page = _page;
        if (page is not null)
        {
            // Serialize against the still-running hangup poll / roster snapshot before touching the page.
            await _pageGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var hangup = page.Locator(HangupButtonSelector).First;
                if (await hangup.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                        .ConfigureAwait(false))
                {
                    await hangup.ClickAsync(new LocatorClickOptions { Timeout = JoinNowClickTimeoutMs })
                        .ConfigureAwait(false);
                    _logger.LogDebug("Clicked hangup to leave the meeting");

                    // Clicking hangup leaves the call immediately (verified), so waiting for the control to
                    // detach confirms the RTC session is being torn down. This keeps the actual "leave"
                    // from depending on the browser-close path, which on a headed Chromium still in a live
                    // call can block until an RTC timeout — the slow-leave symptom.
                    try
                    {
                        await hangup.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Hidden,
                            Timeout = LeaveConfirmTimeoutMs,
                        }).ConfigureAwait(false);
                        _logger.LogDebug("Hangup control detached; meeting left");
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogDebug("Hangup control still visible after click; proceeding to close the browser");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hangup click during leave failed (already gone?)");
            }
            finally
            {
                _pageGate.Release();
            }
        }

        await CloseBrowserAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetAttendeeNamesAsync(CancellationToken cancellationToken = default)
    {
        var page = _page;
        if (page is null) return Array.Empty<string>();

        // One page op at a time (see _pageGate): block until the hangup poll / leave probe releases.
        await _pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRosterOpenAsync(page).ConfigureAwait(false);

            var names = await page.EvaluateAsync<string[]>(RosterNamesScript).ConfigureAwait(false);

            // First read only: dump the roster region to the DEBUG log so the (unverifiable-here) selectors
            // can be refined from a real run. Names are user content ⇒ SensitiveDebug (erased from release IL).
            if (!_rosterDomLogged)
            {
                _rosterDomLogged = true;
                try
                {
                    var dom = await page.EvaluateAsync<string?>(RosterDomScript).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(dom))
                        _logger.SensitiveDebug("Meeting roster DOM sample: {RosterDom}", Truncate(dom, 2_000));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Roster DOM sample capture failed");
                }
            }

            if (names is null || names.Length == 0) return Array.Empty<string>();

            var cleaned = new List<string>(names.Length);
            foreach (var n in names)
            {
                var name = n?.Trim();
                if (!string.IsNullOrEmpty(name)) cleaned.Add(name);
            }
            return cleaned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reading the meeting roster failed; returning no attendees for this snapshot");
            return Array.Empty<string>();
        }
        finally
        {
            _pageGate.Release();
        }
    }

    public async Task StartAudioCaptureAsync(
        Action<int, int> onFormat,
        Action<byte[]> onPcm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onFormat);
        ArgumentNullException.ThrowIfNull(onPcm);
        var page = _page ?? throw new InvalidOperationException("Browser page was not created.");

        // Expose the page→host channel once. The page calls window.__piaAudioSink(msg): "f:<rate>:<ch>"
        // for the one-time format announcement, otherwise a base64-encoded little-endian Float32 PCM
        // chunk. Exposed on the CONTEXT (the generic typed overload lives on IBrowserContext) so it is
        // available to the page; the callback runs on Playwright's dispatch thread, so it must not block.
        // PCM is user meeting content ⇒ it is NEVER logged here; only the chunk size is, and only on the
        // first frame (see BrowserAudioCaptureSource).
        if (!_audioBindingExposed && _context is not null)
        {
            await _context.ExposeFunctionAsync<string>(AudioBindingName, msg =>
            {
                try
                {
                    if (string.IsNullOrEmpty(msg)) return;
                    if (msg.StartsWith("f:", StringComparison.Ordinal))
                    {
                        var parts = msg.Split(':');
                        if (parts.Length >= 3
                            && int.TryParse(parts[1], out var rate)
                            && int.TryParse(parts[2], out var channels))
                        {
                            onFormat(rate, channels);
                        }
                        return;
                    }

                    onPcm(Convert.FromBase64String(msg));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "In-browser audio binding callback failed for one message");
                }
            }).ConfigureAwait(false);
            _audioBindingExposed = true;
        }

        // Spin up the in-page Web Audio tap. Page-gated so it never collides with the hangup / roster
        // polls (Playwright forbids concurrent ops on one IPage).
        string status;
        await _pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            status = await page.EvaluateAsync<string>(AudioCaptureStartScript).ConfigureAwait(false);
        }
        finally
        {
            _pageGate.Release();
        }

        _logger.LogInformation("In-browser audio capture armed: {Status}", status);

        // A hard wiring failure (binding missing, in-page exception) means no audio will ever flow —
        // throw so the orchestrator degrades to the audible endpoint loopback. "started:N" / "no-tracks"
        // are NOT failures: tracks can connect late (via the RTCPeerConnection hook), and the caller's
        // no-audio watchdog covers the case where none ever do.
        if (status is null
            || status == "no-binding"
            || status.StartsWith("error:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"In-browser audio capture failed to arm ({status}).");
        }
    }

    public async Task StopAudioCaptureAsync()
    {
        var page = _page;
        if (page is null) return;

        try
        {
            await _pageGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var status = await page.EvaluateAsync<string>(AudioCaptureStopScript).ConfigureAwait(false);
                _logger.LogDebug("In-browser audio capture stopped: {Status}", status);
            }
            finally
            {
                _pageGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping in-browser audio capture failed (page already gone?)");
        }
    }

    /// <summary>
    /// Best-effort: if the roster panel is not already populated, click the "People" toggle and wait
    /// briefly for rows to appear. Swallows every failure — the caller reads whatever is rendered.
    /// </summary>
    private async Task EnsureRosterOpenAsync(IPage page)
    {
        try
        {
            if (await page.Locator(RosterItemSelector).First
                    .IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs }).ConfigureAwait(false))
            {
                return;
            }

            var button = page.Locator(RosterButtonSelector).First;
            if (await button.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                    .ConfigureAwait(false))
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = ProbeTimeoutMs }).ConfigureAwait(false);
                await page.Locator(RosterItemSelector).First
                    .WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = RosterOpenTimeoutMs,
                    }).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Opening the meeting roster panel failed; reading whatever is rendered");
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    public async ValueTask DisposeAsync()
    {
        await CloseBrowserAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Follows the meeting-URL redirect(s) to the launcher URL, then applies the pure
    /// <see cref="TeamsMeetingUrl.BuildLauncherUrl"/> transform. If the redirect-follow fails we fall
    /// back to transforming the original URL — the launch params still suppress the native dialog.
    /// </summary>
    private async Task<string> ResolveLauncherUrlAsync(string meetingUrl, CancellationToken cancellationToken)
    {
        string resolved;
        try
        {
            // Use the dedicated client whose pipeline has HttpLoggingHandler removed: the meeting URL
            // is sensitive (it grants join access) and must never reach the support-attachable logs.
            var client = _httpClientFactory.CreateClient(MeetingRedirectHttpClientName);
            using var response = await client
                .GetAsync(meetingUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            resolved = response.RequestMessage?.RequestUri?.ToString() ?? meetingUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not follow meeting-URL redirect; using the original URL");
            resolved = meetingUrl;
        }

        return TeamsMeetingUrl.BuildLauncherUrl(resolved);
    }

    private async Task LaunchBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);

        var args = new List<string>
        {
            // Allow media to start playing without a user gesture so meeting audio renders.
            // NOTE: deliberately NO --mute-audio and NO fake audio output device — muting output
            // or faking the playback device would kill the very audio we need to capture.
            "--autoplay-policy=no-user-gesture-required",
            // Occlusion / background-throttling insurance: a non-visible (off-screen) window can have
            // its renderer backgrounded/throttled, which can stall the audio render we capture.
            "--disable-features=CalculateNativeWinOcclusion",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-background-timer-throttling",
        };
        if (!_launchSpec.ShowWindow)
        {
            // Far off-screen + a real size so the page lays out yet nothing is visible on screen.
            args.Add("--window-position=-32000,-32000");
            args.Add("--window-size=1280,720");
        }
        // else: no off-screen args — let the window open on-screen and the meeting be audible.

        var options = new BrowserTypeLaunchOptions
        {
            // Headed: required so Chromium creates a real audio render session we can capture.
            Headless = false,
            Args = args.ToArray(),
        };
        // Exactly one of Channel / ExecutablePath is set (mutually exclusive in Playwright): a Channel
        // drives a system/branded install (Chrome/Edge), an ExecutablePath the bundled / arbitrary build.
        if (_launchSpec.Channel is not null)
            options.Channel = _launchSpec.Channel;
        else
            options.ExecutablePath = _launchSpec.ExecutablePath;

        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surface launch failures as a distinct type so the orchestrator can degrade a system
            // browser (Chrome/Edge channel) — which may be absent or blocked by enterprise policy — to
            // the always-available bundled Chromium, without retrying genuine join failures.
            var which = _launchSpec.Channel is not null ? $"channel '{_launchSpec.Channel}'" : "bundled Chromium";
            throw new BrowserLaunchException($"Failed to launch the meeting browser ({which}).", ex);
        }

        // Microphone/camera are intentionally NOT granted: the bot joins muted with no hanging OS
        // prompt. We do not fake the input device either (deny-by-default is enough for the first
        // shot; --use-fake-device-for-media-stream remains an option if a fake mic is later needed).
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            Permissions = [],
        }).ConfigureAwait(false);

        // Hidden ⇒ silent path: arm the RTCPeerConnection track-collection hook BEFORE the first
        // navigation so it is in place when Teams creates its peer connection. The actual tap + speaker
        // muting starts later in StartAudioCaptureAsync (so a failed capture never mutes the meeting).
        // The on-screen path leaves audio fully audible and skips this entirely.
        if (!_launchSpec.ShowWindow)
        {
            await _context.AddInitScriptAsync(AudioHookInitScript).ConfigureAwait(false);
        }

        _page = await _context.NewPageAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the set of matching browser PIDs (per the launch spec) before launch, so a
    /// before/after diff can attribute the freshly-spawned browser process tree.
    /// </summary>
    private string[] SnapshotChromiumPids()
    {
        // Returns a token list "pid" entries; we only need to diff PIDs, so collect them as strings.
        return GetMatchingChromiumProcesses().Select(p => p.Id.ToString()).ToArray();
    }

    /// <summary>
    /// Picks this session's browser root PID from the chrome.exe processes that appeared after
    /// launch.
    ///
    /// TODO (UNVERIFIED): a single Chromium launch spawns many chrome.exe processes (browser root,
    /// renderers, GPU, audio service). The per-process loopback source (Unit 3) keys off the ROOT
    /// via INCLUDE_TARGET_PROCESS_TREE, so we want the root here. Without WMI/NtQueryInformationProcess
    /// we cannot read the parent PID cheaply, so we use the documented heuristic "earliest StartTime
    /// among the newly-spawned matching processes" (the parent spawns before its children). The
    /// default audio path (endpoint loopback) does not use this value, so an approximate PID is
    /// acceptable for the first shot; it must be validated before the per-process path ships.
    /// </summary>
    private int? ResolveBrowserProcessId(string[] preExistingPids)
    {
        try
        {
            var preExisting = new HashSet<string>(preExistingPids, StringComparer.Ordinal);

            Process? root = null;
            foreach (var proc in GetMatchingChromiumProcesses())
            {
                if (preExisting.Contains(proc.Id.ToString()))
                {
                    proc.Dispose();
                    continue;
                }

                if (root is null)
                {
                    root = proc;
                    continue;
                }

                try
                {
                    if (proc.StartTime < root.StartTime)
                    {
                        root.Dispose();
                        root = proc;
                    }
                    else
                    {
                        proc.Dispose();
                    }
                }
                catch
                {
                    proc.Dispose();
                }
            }

            var pid = root?.Id;
            root?.Dispose();
            return pid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the browser process id");
            return null;
        }
    }

    /// <summary>
    /// Enumerates running processes named per the launch spec (<c>chrome</c> or <c>msedge</c>) whose
    /// main-module path matches the spec's resolved executable, so we never pick up the user's own
    /// browser of the same name. When the spec has no resolved path (App Paths lookup failed for a
    /// Channel launch), every same-named process is a candidate and the pre-launch snapshot diff in
    /// <see cref="ResolveBrowserProcessId"/> narrows it to the newly-spawned tree. Module access can
    /// throw for protected/exited processes, so each lookup is guarded; excluded processes are disposed.
    /// </summary>
    private IEnumerable<Process> GetMatchingChromiumProcesses()
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(_launchSpec.ProcessName);
        }
        catch
        {
            yield break;
        }

        foreach (var proc in candidates)
        {
            string? modulePath = null;
            try
            {
                modulePath = proc.MainModule?.FileName;
            }
            catch
            {
                // Access denied / process exited — cannot confirm it is ours.
            }

            if (IsLaunchedBrowserProcess(_launchSpec.MatchExecutablePath, modulePath))
            {
                yield return proc;
            }
            else
            {
                proc.Dispose();
            }
        }
    }

    /// <summary>
    /// Pure predicate: is a same-named process (already filtered by process name) one we may attribute
    /// to our launch? When <paramref name="matchExecutablePath"/> is known, require an exact path match
    /// so the user's own browser of the same name is excluded. When it is null (App Paths resolution
    /// failed for a Channel launch), we cannot disambiguate by path — accept the process and rely on the
    /// pre-launch snapshot diff to exclude pre-existing PIDs.
    /// </summary>
    internal static bool IsLaunchedBrowserProcess(string? matchExecutablePath, string? modulePath)
    {
        if (matchExecutablePath is null)
            return true;
        if (modulePath is null)
            return false;
        return string.Equals(modulePath, matchExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best-effort dismissal of the Fluent UI (Northstar) modal that the new Teams web prejoin can
    /// layer over the screen — a <c>.ui-dialog__overlay</c> scrim that intercepts pointer events and
    /// makes a real click on "Join now" time out (51+ retries over 30s in the field).
    ///
    /// We do not have a positive identification of the dialog: the browser is launched far
    /// off-screen (so the audio render session is real but nothing is visible), which means it
    /// cannot be observed interactively. So this captures the dialog's text to the DEBUG-only log —
    /// a single re-run then reveals whether it is a consent gate, a permissions modal, or a promo —
    /// and attempts an Escape dismiss (most Northstar dialogs close on Escape). The DispatchEvent
    /// fallback in <see cref="ClickJoinNowAsync"/> covers overlays that survive Escape.
    /// </summary>
    private async Task DismissBlockingDialogAsync(IPage page)
    {
        try
        {
            var overlay = page.Locator(DialogOverlaySelector).First;
            if (!await overlay.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                    .ConfigureAwait(false))
            {
                return;
            }

            // The dialog text can embed the meeting subject (sensitive), so it is logged only in
            // DEBUG builds — SensitiveDebug and its argument evaluation are erased from release IL.
            try
            {
                var dialogText = await page.GetByRole(AriaRole.Dialog).First
                    .InnerTextAsync(new LocatorInnerTextOptions { Timeout = ProbeTimeoutMs })
                    .ConfigureAwait(false);
                _logger.SensitiveDebug(
                    "Prejoin dialog overlay is blocking 'Join now': {DialogText}", dialogText);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Prejoin dialog overlay present but its text could not be read");
            }

            // The get-user-media modal ("Are you sure you don't want audio or video?") that appears
            // because the bot grants no mic/camera does NOT close on Escape — its "Continue without audio
            // or video" button does. Click that button (a few attempts, as the modal can re-render) and
            // wait for the overlay to detach; fall back to Escape for any other Northstar dialog that has
            // no such button. The DispatchEvent fallback in ClickJoinNowAsync covers an overlay that still
            // survives this.
            var continueButton = page.Locator(GumContinueSelector).First;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!await overlay.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                        .ConfigureAwait(false))
                {
                    _logger.LogDebug("Prejoin dialog overlay dismissed");
                    return;
                }

                try
                {
                    if (await continueButton.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                            .ConfigureAwait(false))
                    {
                        await continueButton.ClickAsync(new LocatorClickOptions { Timeout = ProbeTimeoutMs })
                            .ConfigureAwait(false);
                        _logger.LogDebug("Clicked 'Continue without audio or video' to dismiss the prejoin dialog");
                    }
                    else
                    {
                        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
                        _logger.LogDebug("Prejoin dialog had no continue button; pressed Escape");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Prejoin dialog dismiss attempt failed; retrying");
                }

                try
                {
                    await overlay.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Hidden,
                        Timeout = DialogDismissTimeoutMs,
                    }).ConfigureAwait(false);
                    _logger.LogDebug("Prejoin dialog overlay detached");
                    return;
                }
                catch (TimeoutException)
                {
                    // Still present — loop and try again.
                }
            }

            _logger.LogDebug("Prejoin dialog overlay survived dismissal; will dispatch the Join now click directly");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Prejoin dialog dismissal probe failed; continuing to click");
        }
    }

    /// <summary>
    /// Clicks "Join now", falling back to a synthetic DOM click if a stray overlay still intercepts
    /// the real pointer event. A normal click (and <c>Force = true</c>) routes a mouse event through
    /// the button's page coordinates, so an intercepting overlay swallows it; <c>DispatchEvent</c>
    /// dispatches the click straight to the button element and skips hit-testing.
    /// </summary>
    private async Task ClickJoinNowAsync(IPage page)
    {
        var joinNow = page.Locator(JoinNowSelector).First;
        try
        {
            await joinNow.ClickAsync(new LocatorClickOptions { Timeout = JoinNowClickTimeoutMs })
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug(ex, "Real click on 'Join now' was intercepted; dispatching a synthetic click");
            await joinNow.DispatchEventAsync("click").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the bot is admitted (hangup control present). While waiting, raises
    /// <see cref="EnteredLobby"/> the first time the lobby text is observed. Throws
    /// <see cref="TimeoutException"/> if not admitted within <see cref="AdmissionTimeoutMs"/>.
    /// </summary>
    private async Task WaitForAdmissionAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + AdmissionTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Admitted?
            try
            {
                if (await page.Locator(HangupButtonSelector).First
                        .IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Admission probe (hangup) failed; will retry");
            }

            // Still in the lobby? Surface it once.
            if (!_enteredLobbyRaised)
            {
                try
                {
                    if (await page.GetByText(LobbyText).First
                            .IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                            .ConfigureAwait(false))
                    {
                        _enteredLobbyRaised = true;
                        _logger.LogDebug("Meeting attendee is in the lobby, waiting to be admitted");
                        RaiseEnteredLobby();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Lobby probe failed; will retry");
                }
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Meeting attendee was not admitted within {AdmissionTimeoutMs / 1000} seconds.");
    }

    private void RaiseEnteredLobby()
    {
        var handler = EnteredLobby;
        if (handler is null) return;
        try { handler.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogDebug(ex, "EnteredLobby subscriber threw"); }
    }

    /// <summary>
    /// Tears down page → context → browser → Playwright, swallowing per-step failures (mirroring
    /// <c>LiveMeetingService.DisposeAllAsync</c>). Idempotent: nulls each handle as it is released.
    /// </summary>
    private async Task CloseBrowserAsync()
    {
        var closedCleanly = true;

        if (_context is not null)
        {
            closedCleanly &= await CloseWithTimeoutAsync(_context.CloseAsync(), "context").ConfigureAwait(false);
            _context = null;
        }
        _page = null;

        if (_browser is not null)
        {
            // CloseAsync fully tears the browser down. We deliberately do NOT also call DisposeAsync:
            // Playwright's IBrowser.DisposeAsync is just `new ValueTask(CloseAsync())`, so on an
            // already-closed browser (e.g. after a meeting that never fully joined) it re-enters the
            // close path on a torn-down connection and throws an internal NullReferenceException.
            closedCleanly &= await CloseWithTimeoutAsync(_browser.CloseAsync(), "browser").ConfigureAwait(false);
            _browser = null;
        }

        // If a close call hung (a headed Chromium that was still in a live call can block teardown), kill
        // the captured browser process tree so stopping the attendee stays prompt instead of waiting out
        // an internal timeout.
        if (!closedCleanly)
            TryKillBrowserProcessTree();

        if (_playwright is not null)
        {
            try { _playwright.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Playwright dispose threw"); }
            _playwright = null;
        }
    }

    /// <summary>
    /// Awaits a Playwright close call but gives up after <see cref="BrowserCloseTimeoutMs"/> so a hung
    /// teardown cannot stall stopping the attendee. Returns true if the close completed (cleanly or by
    /// throwing), false if it timed out — in which case the caller kills the process tree. A timed-out
    /// close task is left observed (its exception is swallowed) so it never surfaces as unobserved.
    /// </summary>
    private async Task<bool> CloseWithTimeoutAsync(Task closeTask, string what)
    {
        var finished = await Task.WhenAny(closeTask, Task.Delay(BrowserCloseTimeoutMs)).ConfigureAwait(false);
        if (finished == closeTask)
        {
            try { await closeTask.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Browser {What} close threw", what); }
            return true;
        }

        _ = closeTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        _logger.LogDebug(
            "Browser {What} close did not complete within {Ms}ms; killing the process tree", what, BrowserCloseTimeoutMs);
        return false;
    }

    /// <summary>
    /// Last-resort teardown: kills the captured meeting-browser process tree by PID. Used only after a
    /// Playwright close call times out. The PID is the launch-attributed root (see
    /// <see cref="ResolveBrowserProcessId"/>), which is a heuristic. We only kill when the launch spec
    /// resolved a concrete executable path (bundled Chromium, or a system Chrome/Edge whose App Paths
    /// lookup succeeded): in that case the candidate set is filtered to OUR exe, so the PID cannot be the
    /// user's own same-named browser. When the path is unknown we skip the kill and accept the rare leaked
    /// process — the bounded <see cref="CloseWithTimeoutAsync"/> has already made Stop prompt regardless.
    /// Best-effort: every failure is swallowed.
    /// </summary>
    private void TryKillBrowserProcessTree()
    {
        if (_launchSpec.MatchExecutablePath is null)
        {
            _logger.LogDebug(
                "Skipping meeting browser process-tree kill: launch executable path is unknown, so the "
                + "PID cannot be safely attributed to our browser");
            return;
        }
        if (_browserProcessId is not int pid) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            _logger.LogDebug("Killed meeting browser process tree (pid {Pid})", pid);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Killing the meeting browser process tree failed");
        }
    }
}

#pragma warning restore CS0612
