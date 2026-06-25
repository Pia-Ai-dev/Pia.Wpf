# Implementation Plan — Meeting-Attendee Browser (choice · visibility · default browser)

Target branch: `feature/meeting_attendee` · Date: 2026-06-25
Companion analysis: [`2026-06-25-meeting-browser-feature-deepdive.md`](./2026-06-25-meeting-browser-feature-deepdive.md) (read first — it carries the
research, citations, and the resolved product decisions this plan executes).

> **Resolved decisions (deep-dive §"DECISIONS — RESOLVED 2026-06-25"):**
> 1. **#1 menu** = Bundled Chromium (default) + System Chrome + System Edge.
> 2. **#2 default** = Hidden via taskbar-button suppression (window left off-screen); a "show window" toggle ships too.
> 3. **#2 silent** = YES — productionize per-process loopback so "hidden" also means inaudible. ⚠️ top risk (unverified path).
> 4. **#3** = Ship the default-system-browser option now; anonymous join only, no SSO/live profile.

---

## 1. Summary & reframe

Today the attendee launches **one** browser: bundled Playwright Chromium, headed, parked off-screen at
`-32000,-32000`, captured via the **audible endpoint-loopback** mixer
(`TeamsMeetingSession.LaunchBrowserAsync` `:253-285`; `MeetingAttendeeService.ResolveAudioSource` `:423-433`).
All three feature requests mutate that single launch decision, so the plan builds **one cohesive
"meeting browser" surface** rather than three bolt-ons:

- one settings group (`AppSettings`),
- one settings-UI section (`GeneralSettingsViewModel` + `GeneralView.xaml`),
- one **launch-spec resolver** that turns a `MeetingBrowserSelection` + visibility/silence prefs into a
  single `BrowserLaunchSpec`, consumed by `TeamsMeetingSession`.

**The load-bearing refactor is PID matching.** `GetMatchingChromiumProcesses()` (`TeamsMeetingSession.cs:365`)
is hardcoded to `Process.GetProcessesByName("chrome")` + `string.Equals(modulePath, _chromiumExecutablePath)`.
That PID (`ResolveBrowserProcessId` `:309`) feeds **both** downstream features this plan adds:
the **per-process (silent) audio** path and the **HWND-from-PID taskbar** fix. System Edge is `msedge.exe`;
system Chrome is `chrome.exe` at a *different* path — either silently returns no PID, which silently
disables silent-audio and taskbar-hiding. **So PID matching must become a function of the resolved
selection before #1, #2-silent, or #3 can work.** This is Phase 0 and everything depends on it.

**Backbone constraint (unattended automation):** bundled Chromium is the only Playwright-guaranteed build;
system/branded browsers are opt-in convenience with a caveat (deep-dive §Backbone). Bundled stays the
default in every code path, and **silent (per-process) audio is most reliable with bundled** (unique exe
path ⇒ unambiguous PID) — a second reason bundled is the default.

---

## 2. Scope

**IN**
- `MeetingBrowserSelection` setting: `BundledChromium` | `SystemChrome` | `SystemEdge` | `SystemDefault`, with graceful fallback to bundled.
- `MeetingAttendeeShowBrowserWindow` setting (default `false`) + on-screen launch when `true`.
- Taskbar-button suppression for the hidden window (`WS_EX_TOOLWINDOW` via a new Win32 helper).
- Per-process (silent/inaudible) audio when hidden, with graceful fallback to endpoint loopback (audible) + logged degrade.
- Default-system-browser detection (registry `UserChoice` ProgId → Channel), anonymous join only.
- PID-matching parameterized by the resolved selection (the Phase-0 foundation).
- Settings UI, localization (en/de/fr), and unit tests for every new pure decision.

**OUT (with rationale)**
- **SSO / live-profile reuse** (`LaunchPersistentContextAsync` against the user's main profile). Chrome explicitly disallows automating the default profile; it locks the profile dir and rewrites teardown. Attendee keeps the anonymous display-name join.
- **Firefox / WebKit / Safari.** Playwright's are separate patched builds, not the system browser; they break the Chromium-selector/flag automation. A non-Chromium *default* (#3) falls back to bundled.
- **Brave / Vivaldi / Opera as named menu items.** Chromium-family but no `Channel` value → would need the heavier `ExecutablePath` path. They are reachable only incidentally via #3 fallback-to-bundled (i.e. not driven).
- **`SW_HIDE` / minimize / separate virtual desktop** for hiding. Deep-dive §Occlusion: changes window state and risks the audio render; taskbar suppression on the proven off-screen path is the chosen mechanism.
- **New-headless mode.** Unverified WASAPI capture on Windows + Playwright 1.59 `Headless=true` lands on the null-audio `chrome-headless-shell`. Not pursued.

---

## 3. Phase 0 — Foundation: settings + launch-spec + PID matching (NO behavior change)

Phase 0 introduces the abstraction and the new settings but keeps **bundled + hidden-audible** as the
runtime default, so the app behaves exactly as today until later phases flip defaults. It is the
prerequisite for all others.

### 3.1 `AppSettings` (`src/Pia.Wpf/Models/AppSettings.cs`, meeting block `:107-122`)

Add next to the existing meeting fields:

```csharp
// Which browser the meeting attendee drives. Bundled Chromium is the only Playwright-guaranteed
// build (reliable default); System Chrome/Edge are opt-in convenience (may be affected by browser
// updates / enterprise policy); SystemDefault detects the OS default and falls back to bundled when
// it is not a Chromium-family browser.
public MeetingBrowserSelection MeetingBrowserSelection { get; set; } = MeetingBrowserSelection.BundledChromium;

// Show the attendee's browser window on-screen. Default false = hidden (window parked off-screen and
// its taskbar button suppressed). When true, the window opens normally and the meeting is audible.
public bool MeetingAttendeeShowBrowserWindow { get; set; } = false;
```

New enum (new file `src/Pia.Wpf/Models/MeetingBrowserSelection.cs`, or beside `SttBackend`):

```csharp
namespace Pia.Models;

public enum MeetingBrowserSelection
{
    BundledChromium,
    SystemChrome,
    SystemEdge,
    SystemDefault,
}
```

> Enums already round-trip through `SettingsService` (see `SttBackend`, `TargetSpeechLanguage`), so no
> serializer change is needed. Verify by asserting default + persisted round-trip in a settings test.

### 3.2 `BrowserLaunchSpec` — the resolver output (new file under `Services/MeetingAttendee/`)

A pure record describing *how to launch* and *how to recognize the launched process*. This is what
decouples #1/#3 from the launch + PID code:

```csharp
namespace Pia.Services.MeetingAttendee;

/// <summary>How the meeting attendee should launch its browser, and how to recognize its process.</summary>
public sealed record BrowserLaunchSpec(
    // Exactly one of these is set: ExecutablePath (bundled / arbitrary Chromium) XOR Channel ("chrome"/"msedge").
    string? ExecutablePath,
    string? Channel,
    // Process name(s) to scan when attributing the launched browser PID ("chrome" and/or "msedge").
    string ProcessName,
    // Resolved on-disk binary path used to disambiguate the PID from the user's own browser of the same
    // name. Bundled => the provisioned chrome.exe. Channel => resolved from App Paths (3.4); null only if
    // resolution failed, in which case PID matching falls back to process-name + new-since-launch only.
    string? MatchExecutablePath,
    // True = window visible on-screen; false = parked off-screen + taskbar button suppressed.
    bool ShowWindow);
```

### 3.3 `TeamsMeetingSession` — consume the spec instead of a bare path

- **Ctor** (`:86-95`): replace `string chromiumExecutablePath` with `BrowserLaunchSpec launchSpec`; store
  `_launchSpec`. Keep `_chromiumExecutablePath` only as `_launchSpec.MatchExecutablePath` for the PID scan.
- **`LaunchBrowserAsync`** (`:253-285`): build `BrowserTypeLaunchOptions` from the spec:

```csharp
var args = new List<string>
{
    "--autoplay-policy=no-user-gesture-required",
    // Occlusion / background-throttling insurance for any non-visible state (deep-dive flag table).
    "--disable-features=CalculateNativeWinOcclusion",
    "--disable-backgrounding-occluded-windows",
    "--disable-renderer-backgrounding",
    "--disable-background-timer-throttling",
};
if (!_launchSpec.ShowWindow)
{
    args.Add("--window-position=-32000,-32000");
    args.Add("--window-size=1280,720");
}
// else: no off-screen args — let the window open on-screen (optionally "--start-maximized").

var options = new BrowserTypeLaunchOptions { Headless = false, Args = args.ToArray() };
if (_launchSpec.Channel is not null) options.Channel = _launchSpec.Channel;       // system Chrome/Edge
else options.ExecutablePath = _launchSpec.ExecutablePath;                          // bundled / arbitrary
_browser = await _playwright.Chromium.LaunchAsync(options).ConfigureAwait(false);
```

  Set **either** `Channel` **or** `ExecutablePath`, never both (deep-dive R1: mutually exclusive).
- **`GetMatchingChromiumProcesses`** (`:365`) + **`SnapshotChromiumPids`** (`:291`): parameterize by the spec:
  - `Process.GetProcessesByName(_launchSpec.ProcessName)` instead of literal `"chrome"`.
  - Match `modulePath` against `_launchSpec.MatchExecutablePath` when non-null; when null, fall back to
    "new chrome/msedge process not present in the pre-launch snapshot" (the snapshot diff in
    `ResolveBrowserProcessId` `:309` already excludes pre-existing PIDs).
- After launch, expose the resolved root PID (already done via `BrowserProcessId`); Phase 2 reuses it.

### 3.4 `MeetingAttendeeService.StartAsync` — resolve selection → spec (`:173-269`)

Insert a resolution step before the session is built (currently `:204-217`). The production ctor's
`sessionFactory` (`:126-129`) changes from `chromiumPath => new TeamsMeetingSession(chromiumPath, …)` to
`spec => new TeamsMeetingSession(spec, …)`; the `_provisionChromium` seam (`:43`, `:205`) is called **only**
for the bundled selection.

> **The delegate type change touches three sites, not one.** Changing the lambda shape forces the
> `_sessionFactory` field type at **`:49`** and the **internal test ctor parameter at `:156`** from
> `Func<string, IMeetingSession>` to `Func<BrowserLaunchSpec, IMeetingSession>`. Update all three together,
> and fix the test `sessionFactory` lambdas accordingly (Phase-0 test sweep).

```csharp
// 1) Resolve the browser launch spec from settings (replaces the unconditional provision at :205).
var spec = await ResolveLaunchSpecAsync(settings, startToken).ConfigureAwait(false);
...
var session = _sessionFactory(spec);     // was _sessionFactory(chromiumPath)
```

`ResolveLaunchSpecAsync` (new internal, unit-tested):

```csharp
internal async Task<BrowserLaunchSpec> ResolveLaunchSpecAsync(AppSettings settings, CancellationToken ct)
{
    var show = settings.MeetingAttendeeShowBrowserWindow;
    var selection = settings.MeetingBrowserSelection;

    // #3: resolve "system default" to a concrete Chromium-family selection, or fall back to bundled.
    if (selection == MeetingBrowserSelection.SystemDefault)
        selection = _defaultBrowserResolver.ResolveChromiumSelectionOrBundled();   // Phase 3

    switch (selection)
    {
        case MeetingBrowserSelection.SystemChrome:
            return new BrowserLaunchSpec(null, "chrome", "chrome", ResolveAppPath("chrome.exe"), show);
        case MeetingBrowserSelection.SystemEdge:
            return new BrowserLaunchSpec(null, "msedge", "msedge", ResolveAppPath("msedge.exe"), show);
        case MeetingBrowserSelection.BundledChromium:
        default:
            var path = await _provisionChromium(null, ct).ConfigureAwait(false);   // ~150MB on first run
            return new BrowserLaunchSpec(path, null, "chrome", path, show);
    }
}
```

`ResolveAppPath(exe)` reads `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<exe>` (default value),
falling back to `HKCU\…\App Paths`; returns `null` if absent (PID match then degrades per 3.3). This gives a
`MatchExecutablePath` for Channel launches so silent audio + taskbar-hiding can still find the PID.

> **Channel-not-installed fallback:** if a Channel launch throws (browser absent / enterprise policy block),
> catch in `StartAsync`, log a non-fatal degrade, and retry once with the bundled spec
> (`EnsureChromiumAsync`). Bundled is always available. Covered by a unit test on the fallback decision.

### 3.5 Tests (Phase 0)
- `ResolveLaunchSpecAsync` table test: each selection → expected spec (mock `_provisionChromium`, `_defaultBrowserResolver`).
- Settings round-trip test for the two new fields (default values + persisted reload).
- `GetMatchingChromiumProcesses` parameterization: assert it scans the spec's `ProcessName` (refactor-safe test — extract the predicate as an internal static taking (processName, matchPath, modulePath) and unit-test that pure predicate).

**Acceptance (Phase 0):** build green; full meeting flow behaves exactly as today (bundled, hidden-but-audible); new tests pass. No UI yet.

---

## 4. Phase 1 — #1 Browser choice (settings + UI)

Depends on Phase 0. Pure wiring; the launch branch already exists.

### 4.1 ViewModel (`GeneralSettingsViewModel.cs`)
Mirror the `EnableMeetingDiarization` wiring exactly:
- Collection (beside `:126-129`): `public IEnumerable<MeetingBrowserSelection> MeetingBrowserSelections => Enum.GetValues<MeetingBrowserSelection>();`
- `[ObservableProperty] private MeetingBrowserSelection _meetingBrowserSelection;` and `[ObservableProperty] private bool _meetingAttendeeShowBrowserWindow;`
- `partial void OnMeetingBrowserSelectionChanged(...)` / `OnMeetingAttendeeShowBrowserWindowChanged(...)` → `if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);` (mirror `:179-181`).
- Load block (mirror `:216`): `MeetingBrowserSelection = settings.MeetingBrowserSelection; MeetingAttendeeShowBrowserWindow = settings.MeetingAttendeeShowBrowserWindow;`
- Save block (mirror `:488`): `settings.MeetingBrowserSelection = MeetingBrowserSelection; settings.MeetingAttendeeShowBrowserWindow = MeetingAttendeeShowBrowserWindow;`

### 4.2 View (`Views/SettingsViews/GeneralView.xaml`)
Add a "Meeting browser" section after the diarization block (`:334-363`), reusing the ComboBox+converter
pattern from STT Backend (`:248-264`):

```xml
<!-- Meeting browser -->
<StackPanel Margin="0,0,0,20">
  <TextBlock Text="{loc:Str Settings_MeetingBrowser_Section}" Style="{StaticResource PiaSettingsSectionLabelStyle}"/>
  <ComboBox ItemsSource="{Binding MeetingBrowserSelections}" SelectedItem="{Binding MeetingBrowserSelection}"
            Width="400" HorizontalAlignment="Left">
    <ComboBox.ItemTemplate>
      <DataTemplate><TextBlock Text="{Binding Converter={StaticResource EnumToLocalizedStringConverter}}"/></DataTemplate>
    </ComboBox.ItemTemplate>
  </ComboBox>
  <TextBlock Text="{loc:Str Settings_MeetingBrowser_Description}" Style="{StaticResource PiaSettingsDescriptionStyle}"/>
  <CheckBox Content="{loc:Str Settings_MeetingBrowser_ShowWindow}" IsChecked="{Binding MeetingAttendeeShowBrowserWindow}" Margin="0,8,0,0"/>
  <TextBlock Text="{loc:Str Settings_MeetingBrowser_ShowWindow_Description}" Style="{StaticResource PiaSettingsDescriptionStyle}" Margin="22,4,0,0"/>
</StackPanel>
```

### 4.3 Enum display (`Converters/EnumToLocalizedStringConverter.cs:15-39`)
Add cases:
```csharp
MeetingBrowserSelection.BundledChromium => "Enum_MeetingBrowser_Bundled",
MeetingBrowserSelection.SystemChrome    => "Enum_MeetingBrowser_Chrome",
MeetingBrowserSelection.SystemEdge      => "Enum_MeetingBrowser_Edge",
MeetingBrowserSelection.SystemDefault   => "Enum_MeetingBrowser_Default",
```

### 4.4 Localization (`Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx`)
Add the four `Enum_MeetingBrowser_*` keys plus the four `Settings_MeetingBrowser_*` keys in all three files.
Label the system options as "may be affected by browser updates or enterprise policy."

> **Placement:** the analogous keys (`Settings_SttBackend`, `Settings_Diarization_Section`, `Enum_SttWhisper`,
> …) live in **`ViewStrings.resx`**, so put the new keys there for consistency. `LocalizationSource`
> (`Localization/LocalizationSource.cs:21-27`) merges CommonStrings + ViewStrings + MessageStrings +
> OptimizingStrings and resolves a key across all four, so CommonStrings would *also* work at runtime — but
> ViewStrings matches where the mirrored keys actually are.

### 4.5 Tests
- `GeneralSettingsViewModelTests`: setting the property persists; load reflects settings.

**Acceptance (Phase 1):** choosing System Chrome/Edge drives that browser; choosing an uninstalled one falls back to bundled with a logged degrade; bundled still default.

---

## 5. Phase 2 — #2 Hide (taskbar suppression) + show toggle

Depends on Phase 0 (the resolved root PID).

### 5.1 Win32 helper (new `Services/MeetingAttendee/BrowserWindowChrome.cs`)
P/Invoke to remove the taskbar button of the off-screen window:
- `EnumWindows` + `GetWindowThreadProcessId` → find the **top-level, visible, titled** window owned by the
  root PID (skip child/GPU/utility windows: require `GetWindowLongPtr(hwnd, GWL_STYLE)` has `WS_VISIBLE` and
  a non-empty `GetWindowText`, or that it is the largest top-level window of that PID).
- Apply `WS_EX_TOOLWINDOW` and clear `WS_EX_APPWINDOW` — **parenthesize the OR before the mask** (in C#, `&`
  binds tighter than `|`, so the obvious-looking `… | WS_EX_TOOLWINDOW & ~WS_EX_APPWINDOW` would never clear
  APPWINDOW):
  ```csharp
  var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
  SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
  ```
  Style is read/written via `GetWindowLongPtr`/`SetWindowLongPtr` (not `GetWindow`). To make the taskbar-button
  change take effect, the documented trick is `ShowWindow(SW_HIDE)` then `ShowWindow(SW_SHOWNA)` — invisible
  here because the window is already off-screen. Alternatively call `ITaskbarList3::DeleteTab(hwnd)`.
- **Timing:** the HWND exists only after the browser window is created. Poll with a short bounded retry
  (e.g. up to ~3s, 100ms cadence) after `LaunchBrowserAsync`. If no HWND resolves, log and continue — a
  visible taskbar button is a cosmetic miss, never a join failure.

### 5.2 Wire-up (`TeamsMeetingSession.LaunchBrowserAsync`, after PID resolution)
When `!_launchSpec.ShowWindow`, call `BrowserWindowChrome.SuppressTaskbarButton(_browserProcessId)` (best-effort, try/catch-logged). When `ShowWindow`, do nothing (the on-screen window keeps its normal taskbar button).

### 5.3 Tests
Win32 is not unit-testable here (no live window; "no winwright"/build-test gate). Extract the **window-pick
predicate** (visible + titled + owned-by-pid) as a pure function over a small struct list and unit-test
*that*. The P/Invoke call itself is covered by the Phase-2 manual smoke (join a meeting, confirm no taskbar
button) — list it as a manual acceptance item, not an automated test.

**Acceptance (Phase 2):** with show=false, no orphan taskbar button appears; with show=true, the window is
visible and interactable. (Meeting is still audible at this point — silence is Phase 4.)

---

## 6. Phase 3 — #3 Default system browser

Depends on Phase 0 (`SystemDefault` branch in `ResolveLaunchSpecAsync` 3.4).

### 6.1 `IDefaultBrowserResolver` (new, under `Services/MeetingAttendee/`)
```csharp
public interface IDefaultBrowserResolver
{
    // Resolves the OS default browser to a Chromium-family selection, or BundledChromium when the
    // default is non-Chromium / unknown (graceful, never throws).
    MeetingBrowserSelection ResolveChromiumSelectionOrBundled();
}
```
Implementation reads `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice`
→ `ProgId`, maps:
- ProgId contains `ChromeHTML` → `SystemChrome`
- ProgId contains `MSEdgeHTM` → `SystemEdge`
- anything else (Firefox `FirefoxURL`, Brave, Opera, unknown) → `BundledChromium` + a logged reason.

### 6.2 DI (`Bootstrapper.cs`, beside `:285-286`)
`services.AddSingleton<Services.MeetingAttendee.IDefaultBrowserResolver, DefaultBrowserResolver>();`
Inject into `MeetingAttendeeService` (production ctor) and store `_defaultBrowserResolver`. Add a seam in the
internal test ctor (default to a stub returning `BundledChromium`).

### 6.3 Anonymous join unchanged
No `LaunchPersistentContextAsync`, no profile dir — the existing display-name join (`joinOnWeb` → name →
"Join now") is reused verbatim for every selection.

### 6.4 Tests
- `DefaultBrowserResolver` ProgId→selection mapping table (inject a registry-read seam so it is testable without touching the live registry).
- `ResolveLaunchSpecAsync` with `SystemDefault` → uses the resolver result.

**Acceptance (Phase 3):** with default = Chrome/Edge, the attendee uses it; with default = Firefox/other, it silently uses bundled with a logged reason.

---

## 7. Phase 4 — #2 Silent (per-process loopback) ⚠️ TOP RISK

Depends on Phases 0 (parameterized PID) and 2 (hidden window). This is where "hidden" becomes "inaudible."

### 7.1 Behavior change (`MeetingAttendeeService.ResolveAudioSource` `:423-433`, `UsePerProcessLoopback` `:440-441`)

**RESOLVED — retire the `MeetingAttendeeUseProcessLoopback` field.** The user-facing contract is
*hidden ⇒ silent*, so silence is derived from `MeetingAttendeeShowBrowserWindow`, not a second toggle.
Delete `AppSettings.MeetingAttendeeUseProcessLoopback` (`:117`) and the new rule becomes:
```csharp
internal static bool UsePerProcessLoopback(AppSettings settings, IMeetingSession session)
    => !settings.MeetingAttendeeShowBrowserWindow            // hidden ⇒ want silent
       && session.BrowserProcessId is int;                   // and we have a PID to isolate
```
> `ProcessLoopbackAudioCaptureService` and `LoopbackAudioCaptureService` live in
> **`src/Pia.Wpf/Services/LiveTranscription/`** (`namespace Pia.Services.LiveTranscription`), already
> imported by `MeetingAttendeeService.cs:6` — no new using needed there.

**Graceful fallback (must dispose the half-activated source first).** `CreateDefaultAudioSource` (`:443-455`)
builds `ProcessLoopbackAudioCaptureService(pid, …)` when `usePerProcess`. Today a per-process
`StartAsync` throw is fatal via `StartAsync`'s catch; the fallback goes in the orchestrator's source-start
step (`source.StartAsync` is at **`:233`**, after `_audioSource = source` at `:232`). On
`PlatformNotSupportedException` (Win < 20348, guarded before any COM alloc at `ProcessLoopbackAudioCaptureService.cs:78`)
**or** any activation failure:

1. **`await source.DisposeAsync()` on the failed per-process source FIRST.** A mid-activation throw leaves
   its WASAPI RCWs assigned (`_audioClient` `:86`, `_captureClient` `:93`, `_audioClient.Start()` `:96`) and
   the class does **not** self-clean on throw — reassigning `_audioSource` without disposing it leaks the
   RCWs (the exact double-release/leak hazard `_disposeGate` + `DisposeAllAsync` exist to prevent).
2. Then construct + start `LoopbackAudioCaptureService` (audible), assign it to `_audioSource`, and log a
   warning. The meeting is never lost to a silent-capture failure — it degrades to "hidden but audible."

### 7.2 PID reliability note (design, not code)
Per-process loopback uses `INCLUDE_TARGET_PROCESS_TREE` on the root PID. With **bundled Chromium** the exe
path is unique ⇒ unambiguous PID ⇒ most reliable silent capture. With **Channel** (system Chrome/Edge) the
exe path matches the user's own browser; the snapshot-diff excludes already-running instances, but a Chrome
window the user opens *during* the join window could be mis-attributed. Acceptable for v1; log the matched
PID + module path at Information so a mis-match is diagnosable. (Reinforces "bundled is the most robust
silent path.")

### 7.3 ⚠️ Verification gate (cannot be retired by the test suite)
`ProcessLoopbackAudioCaptureService` is **UNVERIFIED in production** (its own class doc `:22-26`): the interop
is correct-by-construction but has never run against a live target render stream, and the team's gate is
build/test only (no live-app driving, per project convention). **Phase 4 is not "done" on green tests.** It
requires a one-time **manual runtime spike** by someone with a real Teams meeting:
1. Join a real meeting with `MeetingBrowserSelection = BundledChromium`, window hidden.
2. Confirm transcription bubbles populate (audio *is* being captured), AND
3. Confirm the meeting is **inaudible** through the machine's speakers (per-process isolation works), AND
4. Confirm clean teardown (no orphaned `chrome.exe`, the WASAPI RCWs release once — see `MeetingAttendeeService` dispose notes `:368-421`).
If the spike fails, the graceful fallback (7.1) keeps the feature shippable as "hidden but audible" while the
per-process path is fixed. **Treat 7.3 as the phase's exit criterion and call it out in the PR.**

### 7.4 Tests
- **Rewrite the existing `UsePerProcessLoopback_TrueOnlyWhenFlagSetAndPidKnown`**
  (`tests/Pia.Wpf.Tests/Services/MeetingAttendee/MeetingAttendeeServiceStateTests.cs:327-341`): it asserts
  the old flag-gated contract and **will fail** under the new rule (it builds `AppSettings` with the
  now-deleted flag and a PID, expecting `false`; the new rule returns `true` because `ShowBrowserWindow`
  defaults to hidden). Retarget it to the new truth table: hidden+PID ⇒ true; `ShowBrowserWindow=true` ⇒
  false; hidden+no-PID ⇒ false.
- **Fallback decision test:** the catch wraps `source.StartAsync()` (orchestrator `:233`), *not* source
  construction — so make a **source whose `StartAsync` throws**, not a throwing factory
  (`CreateDefaultAudioSource`/the factory only constructs). Add a throwing mode to the test rig's
  `FakeAudioSource` (`MeetingAttendeeServiceStateTests.cs:497-501`, whose `StartAsync` currently can't
  throw) or inject a substitute per-process source via the factory seam; assert the orchestrator disposes
  it, builds the endpoint source, and still reaches `Attending`.

---

## 8. Phase 5 — Localization + test sweep + docs

- Verify all new keys exist in **all three** resx files (en/de/fr); a missing key renders the raw key at runtime.
- Run the full suite under the MTP runner with the known-failure namespace filtered (`--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`); gate = no failures **outside** that namespace.
- Update the deep-dive doc's status line to "implemented" once 0-4 land; note the 7.3 spike outcome.

---

## 9. Sequencing & dependencies

```
Phase 0 (foundation) ─┬─> Phase 1 (#1 choice)
                      ├─> Phase 2 (#2 hide/show) ─> Phase 4 (#2 silent ⚠)
                      └─> Phase 3 (#3 default)
Phase 5 (loc + tests) runs continuously; final sweep last.
```
Phases 1, 2, 3 are independent after 0 and can land in any order / parallel PRs. **Phase 4 must follow
Phase 2** (needs the hidden-window contract) and carries the manual gate. Ship 0→2 first so "hidden
(audible)" works immediately; 4 upgrades it to silent.

---

## 10. Risks

1. **Per-process loopback unverified (TOP).** Mitigation: graceful fallback to audible endpoint (7.1) + mandatory manual spike (7.3). The feature ships degraded-but-working even if the silent path fails.
2. **Channel launch fragility** (browser absent, enterprise policy block, headless-mode divergence — deep-dive R1). Mitigation: catch + fall back to bundled (3.4); bundled stays default.
3. **HWND-from-PID timing / wrong window.** Mitigation: bounded retry, strict window-pick predicate (top-level+visible+titled+root-PID), best-effort (never fails the join).
4. **Channel + per-process PID mis-attribution** (user's own Chrome). Mitigation: snapshot-diff + log matched PID/path; document bundled as most reliable for silent.
5. **`MatchExecutablePath` null for Channel** (App Paths missing). Mitigation: degrade to process-name + new-since-launch matching; log.
6. **Missing resx keys** in de/fr. Mitigation: Phase 5 cross-file check.

---

## 11. File-touch index

| File | Change | Phase |
|---|---|---|
| `Models/AppSettings.cs` (`:107-122`) | + `MeetingBrowserSelection`, `MeetingAttendeeShowBrowserWindow`; **delete `MeetingAttendeeUseProcessLoopback` (`:117`)** | 0, 4 |
| `Models/MeetingBrowserSelection.cs` | new enum | 0 |
| `Services/MeetingAttendee/BrowserLaunchSpec.cs` | new record | 0 |
| `Services/MeetingAttendee/TeamsMeetingSession.cs` (ctor `:86`, `LaunchBrowserAsync :253`, `GetMatchingChromiumProcesses :365`, `SnapshotChromiumPids :291`) | consume spec; parameterize PID scan; occlusion flags; conditional off-screen args | 0, 2 |
| `Services/MeetingAttendee/MeetingAttendeeService.cs` (`_sessionFactory` field `:49`, prod ctor `:100`, `sessionFactory :126`, internal test ctor param `:156`, `StartAsync :173` + source-start `:233`, `ResolveAudioSource :423`, `UsePerProcessLoopback :440`) | sessionFactory type → `Func<BrowserLaunchSpec,IMeetingSession>` (3 sites); `ResolveLaunchSpecAsync`; channel-fallback; silent-when-hidden + dispose-then-fallback; inject resolver | 0, 1, 3, 4 |
| `Services/MeetingAttendee/ChromiumProvisioner.cs` (`EnsureChromiumAsync :45`) | called only for bundled | 0 |
| `Services/MeetingAttendee/BrowserWindowChrome.cs` | new Win32 helper | 2 |
| `Services/MeetingAttendee/IDefaultBrowserResolver.cs` + impl | new registry resolver | 3 |
| `Bootstrapper.cs` (`:285-286`) | register `IDefaultBrowserResolver` | 3 |
| `ViewModels/GeneralSettingsViewModel.cs` (`:126`, `:179`, `:216`, `:488`) | properties, collection, load/save, change handlers | 1 |
| `Views/SettingsViews/GeneralView.xaml` (after `:363`) | meeting-browser section | 1 |
| `Converters/EnumToLocalizedStringConverter.cs` (`:15-39`) | enum→key cases | 1 |
| `Resources/Strings/ViewStrings{,.de,.fr}.resx` | new `Settings_MeetingBrowser_*` + `Enum_MeetingBrowser_*` keys (matches where `Settings_*`/`Enum_*` keys live; CommonStrings also resolves via merged lookup) | 1, 5 |
| `tests/.../MeetingAttendee/*`, `tests/.../ViewModels/GeneralSettingsViewModelTests.cs` | unit tests per phase | all |

---

## 12. Out-of-scope follow-ups (noted, not built)
- Dedicated, signed-in **automation profile** for Teams SSO (a *separate* persistent dir, not the user's main profile) — only if anonymous join proves insufficient.
- Per-process loopback for **Channel** browsers hardened against same-name PID mis-attribution (parent-PID walk via `NtQueryInformationProcess`).
- Brave/Vivaldi/Opera as first-class `ExecutablePath` menu entries (heavier "at your own risk" path).
