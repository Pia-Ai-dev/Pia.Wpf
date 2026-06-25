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
/// prompt) — but audio <b>output</b> is left untouched so the meeting can be captured (we never pass
/// <c>--mute-audio</c> or a fake playback device).
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

    private readonly ILogger<TeamsMeetingSession> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BrowserLaunchSpec _launchSpec;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    private int? _browserProcessId;
    private bool _enteredLobbyRaised;

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
            try
            {
                stillInCall = await page.Locator(HangupButtonSelector)
                    .First
                    .IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hangup probe failed; treating meeting as ended");
                return;
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
            try
            {
                var hangup = page.Locator(HangupButtonSelector).First;
                if (await hangup.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = ProbeTimeoutMs })
                        .ConfigureAwait(false))
                {
                    await hangup.ClickAsync().ConfigureAwait(false);
                    _logger.LogDebug("Clicked hangup to leave the meeting");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hangup click during leave failed (already gone?)");
            }
        }

        await CloseBrowserAsync().ConfigureAwait(false);
    }

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

            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            try
            {
                await overlay.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Hidden,
                    Timeout = DialogDismissTimeoutMs,
                }).ConfigureAwait(false);
                _logger.LogDebug("Dismissed prejoin dialog overlay with Escape");
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Prejoin dialog overlay survived Escape; will dispatch click directly");
            }
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
        if (_context is not null)
        {
            try { await _context.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Browser context close threw"); }
            _context = null;
        }
        _page = null;

        if (_browser is not null)
        {
            try { await _browser.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Browser close threw"); }
            try { await _browser.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Browser dispose threw"); }
            _browser = null;
        }

        if (_playwright is not null)
        {
            try { _playwright.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Playwright dispose threw"); }
            _playwright = null;
        }
    }
}

#pragma warning restore CS0612
