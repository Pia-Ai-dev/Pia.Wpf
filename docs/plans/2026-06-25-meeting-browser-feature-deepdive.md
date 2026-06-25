# Meeting-Attendee Browser: Engineering Deep-Dive on Three Feature Requests

> Date: 2026-06-25 · Branch: `feature/meeting_attendee` · Status: **implemented (Phases 0–4)** per
> [`2026-06-25-meeting-browser-implementation-plan.md`](./2026-06-25-meeting-browser-implementation-plan.md).
> #1 (browser choice), #2-hide (taskbar suppression + show toggle), and #3 (default system browser) are
> code-complete and unit-tested. #2-silent (per-process loopback, §7) is **code-complete but NOT runtime-verified**:
> the build/test gate cannot exercise live WASAPI capture, so the mandatory manual spike (plan §7.3) is still
> pending. Until that spike passes, the verified behaviour is "hidden but audible" via the graceful
> dispose-then-degrade fallback; the silent path activates and degrades on failure but has not been confirmed
> inaudible against a real meeting.
>
> Scope: three feature requests against the Teams meeting-attendee browser launch path
> (`Microsoft.Playwright` 1.59.0, bundled Chromium launched headed-but-off-screen):
> **#1** let the user choose the browser · **#2** the orphan taskbar button (hide / show / UI toggle) ·
> **#3** use the user's default system browser.
>
> Research was web-sourced (playwright.dev .NET docs, Chromium source/design docs, Chrome for Developers)
> and adversarially verified. Confidence is called out where it matters; two starting assumptions were
> corrected by the research (see "What is NOT settled").

## Backbone: reliability of unattended automation drives every decision

The meeting attendee is **unattended automation**. Playwright is documented to *guarantee* only the
**bundled, version-pinned Chromium** it ships with; it bundles its own builds precisely because it relies
on patches, and "Each version of Playwright needs specific versions of browser binaries to operate."
System Chrome/Edge auto-update independently of that pin.

The reliability differential between bundled and non-bundled is **not** "auto-update drifts from a pinned
CDP revision" (that phrasing is *not* a Playwright statement — do not repeat it). The real, documented
basis is:

- **Only the bundled browser is guaranteed.** For an arbitrary `ExecutablePath`, the API doc says
  verbatim: *"Note that Playwright only works with the bundled Chromium, Firefox or WebKit, use at your
  own risk."*
- **Enterprise policy interference**, specific to branded browsers: *"Certain Enterprise Browser Policies
  may impact Playwright's ability to launch and control Google Chrome and Microsoft Edge."*
- **New-headless-mode divergence**: branded Chrome/Edge switched to a new headless implementation, *"so
  expect different behavior in some cases."*

A deliberate scope distinction that recurs below: the *"use at your own risk"* caveat is
**`ExecutablePath`-only**. The stable **`Channel` values (`chrome`, `msedge`) are an officially supported
opt-in** — a real but *softer* caveat (enterprise policy + headless divergence), not "at your own risk."

**Consequence:** bundled Chromium stays the reliable **default**. Requests #1 and #3 are **opt-in
convenience** features carrying an explicit reliability caveat — #3 (arbitrary path / default browser)
heavier than #1 (named Channel).

## Treat all three as ONE "meeting browser" design, not three bolt-ons

The three requests share one surface. Build them as:

1. **One AppSettings group** in `src/Pia.Wpf/Models/AppSettings.cs`: a `MeetingBrowserSelection` enum
   (`BundledChromium` | `SystemChrome` | `SystemEdge` | `SystemDefault`) plus
   `bool MeetingAttendeeShowBrowserWindow` — alongside the existing `MeetingAttendeeUseProcessLoopback`
   (these belong together: see the coupling below).
2. **One settings-UI section** in `ViewModels/GeneralSettingsViewModel.cs` (`[ObservableProperty]` +
   `partial OnXChanged → SaveSettingsAsync`) and `Views/SettingsViews/GeneralView.xaml`, following the
   existing pattern in that file.
3. **One shared launch-options builder** inside `TeamsMeetingSession.LaunchBrowserAsync()` (lines 259–274)
   that resolves selection → `{ExecutablePath | Channel}` + window/visibility args + occlusion/throttle
   flags in a single place.

### The cross-cutting coupling you must design for first

`TeamsMeetingSession.GetMatchingChromiumProcesses()` (line 365) is hardcoded to
`Process.GetProcessesByName("chrome")` and matches on
`string.Equals(modulePath, _chromiumExecutablePath, …)`. That feeds `ResolveBrowserProcessId()` (line 309),
whose PID is used by **two** downstream features:

- the **per-process loopback** audio path (`ProcessLoopbackAudioCaptureService`), and
- any **HWND-from-PID** taskbar fix proposed for #2a.

So #1 (browser choice) and #2 (silent-hidden bot) are **coupled through PID matching**:

- **System Edge runs as `msedge.exe`** → `GetProcessesByName("chrome")` never finds it.
- **System Chrome is `chrome.exe` at a different path** → the `_chromiumExecutablePath` equality fails.

Either change silently breaks PID resolution, which breaks per-process audio isolation *and* the HWND
lookup. **The process-matching predicate must become a function of the resolved browser selection**
(process-name set + the actual launched binary path) before #1 or #2-silent ships. This is the single
strongest argument for one cohesive design.

---

## Request #1 — Let the user pick which browser the attendee uses

**(a) Feasibility:** Feasible, bounded to the **Chromium family**. `_playwright.Chromium` (the `Chromium`
BrowserType) drives bundled Chromium, system Chrome/Edge via `Channel`, and arbitrary Chromium-family
binaries via `ExecutablePath`. Playwright's Firefox and WebKit are *separate patched builds*, not the
user's installed Firefox/Safari, and would break both the Chromium-DOM-selector/flag join automation
*and* Teams web support. **Firefox/WebKit are out.**

**(b) Options & trade-offs:**

| Option | Mechanism | Reliability caveat |
|---|---|---|
| **Bundled Chromium** (current) | `ExecutablePath = provisioned path` | Guaranteed by Playwright. Reliable default. |
| **System Chrome** | `Channel = "chrome"` | Officially supported opt-in. Enterprise policy + headless-mode divergence; auto-updates not version-pinned. |
| **System Edge** | `Channel = "msedge"` | Same class as Chrome. |

`Channel` and `ExecutablePath` are mutually exclusive intents — set one, not both. (`Channel="chromium"`
is **not** a branded browser; it selects full new-headless mode — keep it out of the user-facing list.)

**(c) Recommendation:** Offer `{Bundled Chromium (default, recommended), System Chrome, System Edge}`.
Label the two system options "may be affected by browser updates / enterprise policy." Keep bundled as the
default in every code path.

**(d) Integration sketch:**
- `AppSettings.cs`: add `MeetingBrowserSelection` enum.
- `MeetingAttendeeService.StartAsync`: resolve selection; call `ChromiumProvisioner.EnsureChromiumAsync()`
  only when bundled is chosen (skip the ~150 MB download for Channel paths). Pass selection into the
  `sessionFactory`.
- `TeamsMeetingSession.LaunchBrowserAsync` (lines 259–274): branch the launch-options builder between
  `ExecutablePath` (bundled) and `Channel` (chrome/msedge).
- `GetMatchingChromiumProcesses` (line 365): parameterize by selection — process-name set (`chrome`
  and/or `msedge`) + the launched binary path. **Mandatory** for per-process audio + HWND lookup.

**(e) Effort:** **M** — the launch branch is small; the PID-matching rework and settings/UI wiring are the
bulk.

**(f) Risks:** Channel launch fails if the branded browser isn't installed (must fall back to bundled with
a logged, non-fatal degrade); enterprise policy can block control; PID-matching regressions silently
disable per-process audio.

---

## Request #2 — Browser shows a taskbar button that can't be opened

The user's real complaint is the **taskbar button**, not visibility — the window is *already* off-screen
and invisible (`--window-position=-32000,-32000`, `--window-size=1280,720`, lines 267–268). It is headed
and `WS_VISIBLE`, which is exactly what produces the orphan taskbar button.

### #2a — "Hide completely" (default behavior)

**(a) Feasibility:** Feasible and low-risk **if scoped to the taskbar button only.**

**(b) Options:**

1. **Suppress the taskbar button, leave the window where it is** *(recommended)*. Apply
   `WS_EX_TOOLWINDOW` extended style (or `ITaskbarList::DeleteTab`) to the browser's top-level HWND. The
   window stays off-screen + `WS_VISIBLE`, so the **proven audio render path is untouched**.
2. **`SW_HIDE` / minimize / hidden virtual desktop** — changes the window's visibility/occlusion state.
   Per occlusion research these are *probably* fine (the window is already occluded + backgrounded today
   and audio survives because audible WebRTC tabs are exempt from background pausing and intensive timer
   throttling), but `SW_HIDE`/minimized are **only medium-confidence** and a separate hidden desktop is
   **RISKY**. None of this is settled by docs. **Do not adopt as the default.**

**(c) Recommendation:** **Option 1 — taskbar-button suppression only.** Do not change window position or
visibility for the default "hide" behavior.

**Getting the HWND from the captured browser PID** (Win32 P/Invoke): take the root PID from
`ResolveBrowserProcessId()`, then `EnumWindows` + `GetWindowThreadProcessId`, selecting the top-level,
visible, non-zero-titlebar window owned by that PID (the browser's main window). Apply
`GetWindowLongPtr/SetWindowLongPtr` with `GWL_EXSTYLE |= WS_EX_TOOLWINDOW`, or call
`ITaskbarList3::DeleteTab(hwnd)`. Timing dependency: the HWND exists only after the browser window is
created — poll briefly after launch.

> **CRITICAL COUPLING — hiding the window does NOT silence the meeting.** The default capture is
> `LoopbackAudioCaptureService` = endpoint WASAPI loopback = the whole render-device mix, which is
> **audible through the user's speakers regardless of window state.** Suppressing the taskbar button
> removes the orphan button but the meeting is still heard. A truly **hidden *and* silent** bot requires
> the **per-process loopback path** (`ProcessLoopbackAudioCaptureService`, gated by
> `MeetingAttendeeUseProcessLoopback`, default false), which is **currently UNVERIFIED in production** and
> depends on the same PID resolution discussed above (Win10 build 20348+). Treat "silent hidden bot" as the
> *same design surface* as #2a, and as a product decision (below), not a freebie.

**(d) Integration sketch:** New small Win32 helper (e.g.
`Services/MeetingAttendee/BrowserWindowChrome.cs`) called from `TeamsMeetingSession` after launch, fed the
resolved root PID. Reuse the parameterized `GetMatchingChromiumProcesses`/`ResolveBrowserProcessId`.

**(e) Effort:** **S** for taskbar suppression; **M+** if "silent hidden" (verifying per-process loopback)
is in scope.

**(f) Risks:** HWND-from-PID timing (window not yet created → retry loop); multi-process Chromium (target
the *root* window, not a child/GPU process); per-process loopback remains unverified.

### #2b — "Show fully" + #2c — expose in UI

**(a) Feasibility:** Easy.

**(b/c) Recommendation:** Make the off-screen args **conditional**. When "show" is selected, drop
`--window-position=-32000,-32000` and use a normal on-screen position (optionally `--start-maximized`),
and **do not** apply the taskbar suppression from #2a. **#2c:** a settings toggle, "Show the meeting
browser window" (`MeetingAttendeeShowBrowserWindow`, default **false** = hidden).

**(d) Integration sketch:** `LaunchBrowserAsync` reads the flag and assembles args accordingly;
`GeneralSettingsViewModel` + `GeneralView.xaml` add the toggle in the meeting-browser section.

**(e) Effort:** **S.**

**(f) Risks:** None material; a visible window means the user *will* hear the meeting (acceptable when they
explicitly chose "show").

---

## Request #3 — Use the user's default system browser (if allowed)

**(a) Feasibility:** Feasible **only when the default resolves to a Chromium-family browser**, and carries
the **heaviest** reliability caveat of the three (the `ExecutablePath` *"use at your own risk"* path for
anything that isn't `chrome`/`msedge`).

**(b) Options & detection:**
- Read default via registry:
  `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice` → `ProgId`.
- Map ProgId → launch:
  - Chrome ProgId → `Channel="chrome"` (supported).
  - Edge ProgId → `Channel="msedge"` (supported).
  - **Brave / Vivaldi / Opera**: Chromium-family but **have no `Channel` value** → would require
    `ExecutablePath` to their binary (the heavier "at your own risk" path) or fall back to bundled.
  - **Firefox / non-Chromium / unknown ProgId**: **cannot be driven** → fall back to bundled Chromium.
- **Degrade gracefully:** any non-Chromium or unmappable default silently falls back to bundled Chromium
  with a logged reason.

**The SSO temptation — recommend AGAINST by default.** Reusing the user's signed-in profile for Teams SSO
means `LaunchPersistentContextAsync(userDataDir, …)`. This:
- **Locks the profile directory** (Chromium forbids two instances on one user-data dir) — the user can't
  use that browser concurrently;
- is **explicitly unsupported against Chrome's *main* profile** ("automating the default Chrome user
  profile is not supported … may result in pages not loading or the browser exiting"); the supported
  pattern is a *dedicated* automation profile signed in once;
- **changes teardown shape** — it returns a single `IBrowserContext` (no `_browser` handle); closing the
  context closes the browser, so the current `_browser`+`_context` teardown must be rewritten.

The attendee already **joins anonymously by display name** (`button[data-tid="joinOnWeb"]` → "Type your
name" → "Join now"). Keep that. SSO is not worth the lock + teardown rework + unsupported-profile hazard.

**(c) Recommendation:** Ship #3 as `SystemDefault` selection = "detect default → Channel if chrome/edge,
else fall back to bundled," **anonymous join only, no persistent profile.** Frame it as a convenience with
the strongest caveat. Genuinely consider deferring it (see decisions).

**(d) Integration sketch:** New `IDefaultBrowserResolver` (registry read → enum).
`MeetingAttendeeService.StartAsync` calls it when selection == `SystemDefault`, then funnels into the same
`Channel`/`ExecutablePath` builder as #1. PID-matching must cover the resolved process name/path.

**(e) Effort:** **M** (registry mapping + fallback + the shared #1 plumbing).

**(f) Risks:** ProgId→browser mapping is brittle across versions/vendors; non-Chromium defaults are
common; heaviest reliability caveat of the three.

---

## Occlusion / throttling mitigation flags (cheap insurance for any non-visible state)

Add these to `Args` for off-screen / suppressed / hidden states. Confirmed by name and meaning:

| Flag | What it does |
|---|---|
| `--disable-features=CalculateNativeWinOcclusion` | Turns off Windows occlusion calculation entirely (most directly relevant Windows lever — stops occluded windows being treated as backgrounded). |
| `--disable-backgrounding-occluded-windows` | Stops Chrome treating a foreground tab as backgrounded when its window is occluded. |
| `--disable-renderer-backgrounding` | Stops non-foreground tabs getting a lower process priority (does not by itself affect timers/painting). |
| `--disable-background-timer-throttling` | Disables timer throttling in background pages/tabs (guards the timer-dependent join path). |

> **RESIDUAL RISK:** The occlusion behavior of `SW_HIDE`/minimized (and especially a separate virtual
> desktop) is only **medium/low confidence** — derived from reading the occlusion-tracker source, not a
> verbatim guarantee, and **not transferable from Linux/PulseAudio material.** It is **only retirable by a
> runtime spike**, which is **out of scope here** (the team verifies via build/test, not by driving the
> live app). This is the main reason the #2a recommendation **stays headed + off-screen +
> taskbar-suppressed** rather than hiding the window.

---

## Honest statement of what is NOT settled (two starting assumptions corrected by the research)

- **"Headless can't render capturable audio, so it must stay headed" is REFUTED.** The determining
  variable is *which browser implementation runs*, not headed-vs-headless — a WASAPI render session is
  per-stream/per-process and independent of window visibility. Full new-headless Chromium architecturally
  *should* render to a real endpoint. BUT: (1) in Playwright 1.59, plain `Headless=true` launches
  **`chrome-headless-shell`** (the stripped build with null/fake audio), so a naive bool flip **would**
  break capture; new-headless is reachable only via `Channel="chromium"`; and (2) whether full
  new-headless on **Windows WASAPI** opens a *loopback-capturable* render endpoint is **unknown / low
  confidence**. We recommend headed-off-screen **not** because "headless can't do audio" but because
  **headed-off-screen is the proven path and headless audio on Windows is unverified and would need a
  spike.** Do not pursue headless to achieve #2.
- **The reliability mechanism was mis-stated.** Not "auto-update drifts from a pinned CDP revision" —
  rather "only the bundled browser is guaranteed; branded/system browsers are use-at-your-own-risk
  (`ExecutablePath`) or subject to enterprise-policy + headless-mode behavior differences (`Channel`)."
  The backbone (bundled = reliable default; #1/#3 = caveated opt-in) stands.
- **Per-process loopback (the silent-hidden bot) is UNVERIFIED in production** and gated behind a
  default-false flag.
- The off-screen→occluded conclusion and SW_HIDE/minimized audio-survival are medium confidence
  (source-read inference), not guarantees.

---

## DECISIONS — RESOLVED 2026-06-25

1. **#1 browser menu** → **Bundled Chromium (default) + System Chrome + System Edge.** Firefox/WebKit
   excluded (technical); Brave/Vivaldi/Opera excluded from the named list.
2. **#2 default window state** → **Hidden via taskbar-button suppression** (`WS_EX_TOOLWINDOW`, window
   left off-screen on the proven audio path). A settings toggle (#2c, "Show the meeting browser window")
   is added either way; "Show fully" (#2b) is supported when the toggle is on.
3. **#2 silent-hidden** → **YES — productionize the per-process loopback path** so "hidden" also means
   **inaudible**. ⚠️ This path (`MeetingAttendeeUseProcessLoopback`, Win10 build 20348+) is **currently
   UNVERIFIED in production**; making it the basis of a shipped feature means its verification gap must be
   closed — and per the team's build/test-only gate (no live-app driving) that verification is a
   **runtime spike that is not covered by the normal test suite**. Plan must call this out as the project's
   top risk.
4. **#3 default system browser** → **SHIP NOW.** Detect via registry `UserChoice` ProgId → `Channel` for
   Chrome/Edge, graceful fallback to bundled Chromium otherwise. **Anonymous join only — no live-profile
   SSO** (`LaunchPersistentContextAsync` against the user's main profile is unsupported + locks the dir).

*Next step: a sequenced implementation plan. Implementation not yet started.*

---

### Files referenced

- `src/Pia.Wpf/Services/MeetingAttendee/TeamsMeetingSession.cs` — `LaunchBrowserAsync()` lines 259–274;
  `ResolveBrowserProcessId()` line 309; `GetMatchingChromiumProcesses()` line 365 (hardcoded `chrome` +
  path equality).
- `src/Pia.Wpf/Services/MeetingAttendee/ChromiumProvisioner.cs` — `EnsureChromiumAsync()` (skip for
  Channel paths).
- `src/Pia.Wpf/Services/MeetingAttendee/MeetingAttendeeService.cs` — `StartAsync` selection resolution +
  sessionFactory.
- `src/Pia.Wpf/Models/AppSettings.cs` — lines 111/117 (`MeetingAttendeeDisplayName`,
  `MeetingAttendeeUseProcessLoopback`); add `MeetingBrowserSelection`, `MeetingAttendeeShowBrowserWindow`.
- `src/Pia.Wpf/ViewModels/GeneralSettingsViewModel.cs` + `src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml`
  — single meeting-browser settings section.
- New: `Services/MeetingAttendee/BrowserWindowChrome.cs` (Win32 HWND/taskbar P/Invoke),
  `IDefaultBrowserResolver` (registry).
