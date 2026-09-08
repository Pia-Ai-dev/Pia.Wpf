# Giving Pia eyes on the Windows screen — a monitor, or a chosen window

**Status.** Phases 1 and 2 built; phase 3 has only its text seam, and the rest of it waits on G2.
G1, G2 and G3 are all still open — see the checklist for what closes each. Implementation notes at
the end record where this document did not survive contact with the code.
**Owner.** Marco Altmann.
**Written.** 2026-09-07.
**Origin.** Owner question, 2026-09-07 — "how could we give Pia the ability to *see* the Windows
screen of the user? either using full screen or selective apps?" — brainstormed to a design in the
same session. The four decisions in [Decisions](#decisions) were taken by the owner there and are
load-bearing: a step that contradicts one needs the decision reopened first, not worked around. The
tracking surface is [2026-09-07-screen-vision-checklist.md](2026-09-07-screen-vision-checklist.md).

## What this builds

Three layered capabilities, each shippable on its own:

1. **The user shows Pia something.** A hotkey or a composer button opens a picker of monitors and
   windows; the shot lands in the composer as an attachment the user sees before sending.
2. **Pia asks to look.** A `screen_capture` tool the model can call mid-turn when it decides it needs
   to see, gated by the existing tool-approval machinery.
3. **Pia watches.** A chosen window or monitor observed over time, so Pia can answer about what
   changed — reading extracted text rather than sampling pixels, for the cost reason in D4.

"Selective apps" means a specific top-level window, chosen per capture in phase 1 and constrained by
a named allowlist wherever nobody is at the machine (phase 2's unattended path and phase 3).

## The finding that sizes this work

**Pia can already send an image to a model. The whole pipeline exists.** That is why the attach path
on top of the capture seam is `S` rather than `M` — everything from bitmap to provider is built:

- `ImageAttachmentProcessor.TryPrepare(BitmapSource, ILogger)`
  (`src/Pia.Wpf/Services/Imaging/ImageAttachmentProcessor.cs:45`) takes a bitmap and returns an
  `ImageAttachment` — JPEG bytes, a thumbnail, and a downscale to `MaxLongEdge = 1568` (same file,
  line 12), which is exactly the right ceiling for a 4K monitor grab.
- `AssistantMessage` turns that into a `DataContent` for the provider
  (`src/Pia.Wpf/Models/AssistantMessage.cs:386`).
- `AssistantViewModel.ExecuteHandleImagePasted` already freezes a clipboard `BitmapSource` and hands
  it to that processor (`src/Pia.Wpf/ViewModels/AssistantViewModel.cs:1905`), so a captured bitmap
  needs no new downstream code at all.
- `PrepareImageAttachmentAsync` (same file, line 1917) holds the provider gate: anything other than
  `AiProviderType.PiaCloud` is refused with `Msg_File_ImageProviderUnsupported`, and the attach
  leaves nothing behind.

Nothing in the repo captures a screen today — there is no `BitBlt`, `PrintWindow`, `Gdi32` or
`Windows.Graphics.Capture` anywhere, and `src/Pia.Wpf/Native/` holds only keyboard input and a shell
data object. The delta is therefore capture, a picker, gating, and — for phase 2 — one small change
to the tool round loop.

## Decisions

| # | Question | Decision |
|---|---|---|
| **D1** | Who pulls the trigger? | **All three, layered** — attach, then tool, then watch. Phases 2 and 3 are follow-on. |
| **D2** | Where may captured pixels go? | **PiaCloud only.** Inherit the existing image gate unchanged; build no vision-capability model here. Capture is unavailable, and says why, on any other provider. |
| **D3** | Which run surfaces may capture? | **Everything, gated by grants** — including unattended and scheduled runs. |
| **D4** | Pixels, or extracted text? | **Pixels for phases 1–2; text-first for phase 3.** |

**D3 was taken against the recommendation** of attended-surfaces-only, on the ground that the grant
machinery should carry the weight rather than a hard refusal. Three safeguards are part of that
decision and not optional extras — an implementation that ships the capability without them has not
implemented D3:

1. An unattended capture requires a **pre-existing standing grant**. It can never be auto-approved,
   and never satisfied by a session grant minted inside the same run.
2. An unattended capture may only target a **window named in advance** in the allowlist. Never the
   whole desktop, never an arbitrary window matched by title at run time.
3. Every capture, on every surface, writes one **metadata-only audit line**.

**D4's arithmetic**, for the record: one 1568px frame costs roughly 1.1–1.6k image tokens. A watch
loop sampling every 10 seconds for an hour is ~360 frames, i.e. around half a million image tokens
per hour of watching. Text extraction of the same window is a few hundred tokens per change, and
most ticks change nothing at all.

## Architecture

One new folder, `src/Pia.Wpf/Services/Screen/`, and one seam:

```csharp
public interface IScreenCaptureService
{
    IReadOnlyList<CaptureTarget> EnumerateTargets();
    BitmapSource? Capture(CaptureTarget target);   // frozen before it leaves the UI thread
}

public sealed record CaptureTarget(
    CaptureTargetKind Kind,    // Monitor | Window
    nint Hwnd,                 // 0 for a monitor
    string MonitorDeviceId,    // empty for a window
    Rect Bounds,
    string ProcessName,
    string Title);             // SENSITIVE — see Privacy below
```

The interface is what makes the feature testable: every ViewModel test fakes it, and no test needs a
real desktop.

**Backend: GDI first.** `BitBlt` from the screen DC for a monitor,
`PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` for a specific window. That is about eighty lines of
P/Invoke in `src/Pia.Wpf/Native/ScreenCaptureInterop.cs`, no new package, and it handles
DirectComposition-rendered windows — which is nearly everything, browsers included.
`Windows.Graphics.Capture` is the upgrade path, not the starting point: the project's
`net10.0-windows10.0.17763.0` TFM already projects it, but it drags in a D3D11 device, a frame pool
and a staging-texture copy, and it is only worth that if black frames turn out to be common in
practice (G1 in the checklist).

Write the interop as `[LibraryImport]` partials — the convention already set by
`src/Pia.Wpf/Services/NativeHotkeyService.cs:84-107`, not `DllImport`. The calls needed: `BitBlt`,
`CreateCompatibleDC`, `CreateCompatibleBitmap`, `SelectObject`, `DeleteObject`, `DeleteDC`,
`GetDC`/`ReleaseDC`, `PrintWindow`, `GetWindowRect`, `EnumDisplayMonitors`, `GetMonitorInfoW`,
`EnumWindows`, `GetWindowTextW`, `IsWindowVisible`, `GetWindowLongPtrW`, `DwmGetWindowAttribute`,
`SetWindowDisplayAffinity`.

**Pia excludes itself from capture.** `EnumerateTargets` filters Pia's own windows out of the picker,
and a monitor capture wraps itself in `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` over
every Pia HWND. Without this a full-monitor capture ships the user's chat history — and whatever Pia
has already been told — straight back to the model. It is the single most important line of code in
the feature, and it has three sharp edges:

- **Set it transiently, never at startup.** Display affinity applies to *every* capture on the
  machine, not just Pia's own. A permanent flag makes Pia invisible or black when the user shares
  their screen in a Teams call or takes their own screenshot. Set it, capture, restore `WDA_NONE`.
- `WDA_EXCLUDEFROMCAPTURE` needs Windows 10 2004+; this project's TFM floor is 1809. Fall back to
  `WDA_MONITOR`, which blacks the window out rather than hiding it — still not a leak.
- Under GDI `BitBlt` the excluded region comes back **black, not transparent**. Correct, but a Pia
  window covering most of a monitor can then trip the near-uniform check from Failure modes below.
  Worth knowing before debugging it.

**Everything the interop returns is in physical pixels.** There is no `app.manifest`, no
`app.config` and no DPI setting anywhere in the project, so the process runs at WPF-on-.NET's
default of PerMonitorV2 — which `EmojiPresenter.OnDpiChanged` firing corroborates, since that
override only fires under per-monitor awareness. Confirm it at runtime before A1 lands, then never
mix a `GetWindowRect` value with a WPF DIP without scaling. If it turns out to be system-DPI-aware
instead, `GetWindowRect` returns virtualized coordinates and the screen `BitBlt` comes back scaled on
any mixed-DPI desktop; the fix is `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` around the
capture call rather than a manifest change.

**Target enumeration** rejects, in this order: Pia's own windows; anything not `IsWindowVisible`;
`WS_EX_TOOLWINDOW`; and anything `DwmGetWindowAttribute(DWMWA_CLOAKED)` reports as cloaked — without
that last filter the list fills with ghost UWP entries the user cannot identify. The filter is a pure
predicate over a window descriptor, so it unit-tests against a fake list.

## Phase 1 — the user shows Pia something (`S` on top of the capture seam)

Trigger: a composer button in the Assistant view, plus an optional global hotkey registered through
the existing `INativeHotkeyService` the way the fast-path hotkey already is
(`src/Pia.Wpf/Services/TrayIconService.cs:206`). Both open `ScreenCapturePickerView` — a dialog
listing monitors and eligible windows with a thumbnail each, through the existing `IDialogService`.

The captured bitmap then follows the clipboard-paste path verbatim:

```
picker -> IScreenCaptureService.Capture -> BitmapSource.Freeze()
       -> PrepareImageAttachmentAsync(() => ImageAttachmentProcessor.TryPrepare(bmp, _logger))
       -> pending attachment + thumbnail in the composer
       -> user sends -> DataContent -> PiaCloud
```

Consent in phase 1 is structural rather than a dialog: the user chose the target and sees the
thumbnail before deciding to send. Nothing is captured without a keystroke or a click, and nothing is
sent without a second, separate action.

Per CLAUDE.md the picker's controls each need an `AutomationProperties.AutomationId` with a
`ScreenCapturePicker_` prefix — per-item ids inside the target list must use the binding form
(`{Binding Hwnd, StringFormat='ScreenCapturePicker_Target_{0}'}`), or every row reports the same id —
plus the matching `[InlineData]` row in `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` in the
same change.

## Phase 2 — Pia asks to look (`M`)

A new `ScreenCaptureToolHandler` in `src/Pia.Wpf/Services/`, shaped like the existing
`ChatHistoryToolHandler`: `AIFunctionFactory`-declared functions, snake_case result fields, and a
`ToolClass` of its own.

```
screen_capture(target: "monitor" | "window", match: string?) -> text marker
screen_list_targets()                                       -> the eligible target list
```

**The one piece of real plumbing.** A tool cannot return the image. `AiClientService` builds each
result as a text-only `FunctionResultContent` and appends it to the working messages
(`src/Pia.Wpf/Services/AiClientService.cs:643-646`); the OpenAI-compatible tool-result shape has no
image slot. So:

1. `screen_capture` performs the capture, parks the prepared `ImageAttachment` in a pending slot keyed
   by the call's `CallId`, and returns a short text marker — `captured window, 1568x880`.
2. **After the `foreach` over the round's tool calls has finished** — just before the
   `"Round {Round} complete"` log at line 649 — the loop drains that round's parked captures and
   appends one `ChatRole.User` message per capture, carrying the `DataContent` plus a line naming
   what it is.

   The drain must **not** go immediately after `workingMessages.Add(resultMessage)` at line 646: that
   statement is inside the per-call loop, so a round holding `screen_capture` plus any second call
   would interleave a user message between two tool results. OpenAI-shaped APIs require a round's
   tool results to follow the assistant message contiguously, and reject that sequence.
3. **The image must not ride along forever.** Once the following model response has come back,
   replace the `DataContent` in the working messages with a text placeholder —
   `[screen capture, 1568x880, consumed]` — mirroring what `AgentToolCarryover` does for oversized
   results. Carryover will not do it for you: its truncation and placeholder logic matches only
   `FunctionResultContent`, so an un-swapped capture is re-sent at ~1.5k image tokens on every
   remaining round of the run. Confirm too that nothing persists the bytes:
   `AgentToolExchangeSerializer` filters to `Function*Content` and is safe, but the path that stores
   an agent run's chat history needs checking before this ships.

That is a contained change at a single site, but it is a change to the shared tool round loop, which
is why phase 2 is `M`: the regression surface is every tool call in the product, not just this one.
`HeadlessTurnExecutor` has its own append (`src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:622`) and
needs either the same treatment or an explicit refusal.

Step 2 is G3 in the checklist: verify against PiaCloud that an interleaved user message after a tool
result is accepted *and* attended to. A "no" moves delivery to the next user turn, which changes what
this layer feels like — Pia asks and sees on the next exchange rather than immediately.

**How `match` resolves.** Process name first, then a title substring, and it must resolve to exactly
one target or refuse — a capture is not something to guess at. In an unattended run the resolved
target must additionally be a single allowlist entry (D3 safeguard 2); an ambiguous or off-list match
refuses and names which of the two it was.

The tool must also honour D2: if the run's provider is not PiaCloud, `screen_capture` refuses in the
handler with a legible reason rather than capturing pixels that can never be sent.

## Phase 3 — Pia watches (`L`)

`ScreenWatchService`: one target, one interval, a timer. Each tick takes a UI Automation snapshot of
the target window (in-box `UIAutomationClient`; the repo already knows UIA from
`docs/ui_automation/ui-automation-playbook.md`), hashes the extracted text, and on change appends a
compact delta to a rolling buffer. The model reads that buffer through a `screen_watch_read` tool and
can call `screen_capture` when it needs to actually see — pixels stay on demand, so an idle screen
costs nothing.

On-device `Windows.Media.Ocr` is the fallback for a window whose UIA tree is useless: a canvas, a
remote-desktop session, an image viewer. It runs locally, so it does not weaken D2 — but it needs an
OCR-capable language pack installed, and `OcrEngine.TryCreateFromUserProfileLanguages()` simply
returns null when there is none. Detect that once and disable the fallback with a reason rather than
failing per tick.

Watch mode is only ever armed against an allowlisted target, and a tray indicator shows while it
runs. This phase is deliberately last — it is the one with the standing-consent problem, and neither
phase above needs it.

## Gating, consent and audit

**One new tool class.** `ToolClass` (`src/Pia.Wpf/Models/ToolGateEnums.cs:10`) gains `Screen = 10`.
The enum is persisted and append-only — never renumber, never rename — so the new member goes last.

**The grant does the work, exactly as D3 requires.** `RunAutonomyPolicy.PresetClasses`
(`src/Pia.Wpf/Models/RunAutonomyPolicy.cs:19-25`) is a fixed list: `Memory`, `Todo`, `Reminder`,
`Scheduling`, `Files`. **Do not add `Screen` to it.** `Covers` then returns false for `Screen` under
every autonomy preset, which means the settings preset can never auto-approve a capture and an
unattended run reaches one only through an explicit named grant. Safeguard 1 of D3 is therefore
enforced by *omission*, plus one refusal rule in `ToolAutonomy.Resolve`: a session grant minted
inside the same run must not satisfy an unattended capture.

**Voice is allowed.** `ToolGateSurface.Voice` refuses `Assignment` at every tier, so the question
lands the moment C1's per-surface test rows get written. "Pia, look at my screen" is an attended
action with the user standing right there — `Screen` follows the interactive rules on the voice
surface rather than being refused outright.

**Allowlist.** `ScreenCaptureAllowlistStore` persists the named targets — process name plus a title
pattern — that an unattended run or a watch session may see, edited in settings. An unattended
`screen_capture` whose resolved target is not on the list refuses and says so.

**Audit.** Every capture appends one metadata-only line: timestamp, surface, run id, target kind,
process name, dimensions, and a **hash** of the window title. Do not reuse
`IConsentAuditLog`/`AuditEvent` (`src/Pia.Wpf/Services/Consent/`) — that trail is speaker-consent
shaped (`SpeakerLabel`, a frozen transcription-session vocabulary) and per-session on disk. Model a
separate `ScreenCaptureAuditLog` on `JsonlConsentAuditLog`'s discipline instead: fire-and-forget,
never throws, never blocks the caller, drops on overflow and logs that it dropped. It writes to a new
`PiaPaths.ScreenCaptureAuditDirectory`, declared as a **property** — never a `static readonly` field,
or it freezes at type load and breaks the `PIA_DATA_DIR` routing that gives UI tests a throwaway
profile. `DataDirectoryRoutingTests` and `PiaPathsTests` both need the new row.

**Visible indicator.** A tray state change while watch mode runs, and a toast after any capture made
by an unattended run — the user learns it happened even though they were not asked.

## Failure modes

- **A black or uniform frame** is the OS working as intended: password managers, DRM video and some
  hardware-overlay paths deliberately refuse capture. Detect near-uniform output and refuse with a
  specific reason. Do **not** work around it, and do not fall back to a different capture API to
  defeat it.
- **A minimized or cloaked window** refuses with a reason. Do not silently restore or move a window
  the user did not touch.
- **A non-PiaCloud provider** (D2) disables the capture button with a tooltip and refuses in the tool
  handler, so the failure happens before the capture rather than after it.
- **Mid-stream** capture is refused, matching the existing `IsStreaming` guard on the image path.
- **Multi-monitor**: capture exactly one monitor. Never the virtual-desktop bounding box — it is both
  a token disaster and, on mismatched DPI, a partly black image.

## Privacy and logging

The window title is a user-named item under CLAUDE.md's privacy-first logging rules, so it goes to
`SensitiveDebug` only and is hashed in the audit line. The bitmap and the JPEG bytes are never logged
at any level in any build. Release logs carry target kind, process name and dimensions — enough to
diagnose a capture failure from a user's attached log, and nothing about what was on screen.

## Deliberately out of scope

- **Drag-a-region capture.** The project has no `app.manifest`, so DPI awareness has to be
  established before mixing physical-pixel `GetWindowRect` values with WPF DIPs. Monitor plus window
  covers the stated need; region can come later on top of the same seam.
- **`Windows.Graphics.Capture`.** Only if G1 says black frames are common.
- **Lifting the PiaCloud image gate** — a real per-provider vision-capability model. Worth doing, and
  it would also fix pasted and dropped images on other providers, but it is its own workstream and D2
  says not here.
- **More than one image per message.** The existing composer holds a single image attachment —
  `AttachFirstImageAsync` keeps the first and warns with `Msg_File_OneImageOnly`
  (`src/Pia.Wpf/ViewModels/AssistantViewModel.cs:1885`). Phase 1 inherits that limit, and phase 2's
  round loop must therefore inject captures as separate messages rather than assume a multi-image
  composer. Lifting it is a separate change.
- **Input.** Nothing in this design clicks, types or moves a window. Pia sees; it does not touch.

## Open questions

- **How common are black frames** from `PrintWindow(PW_RENDERFULLCONTENT)` across the apps this user
  base actually runs? Answered by G1, cheaply, before any UI is built.
- **Is a UIA text snapshot usable** in Electron and Chromium windows without the user switching
  accessibility on? Gates phase 3's engine, not phases 1–2.
- **Should watch mode survive a restart?** An armed watch that reattaches after an app restart is
  more useful and much harder to reason about consent-wise. Deferred to phase 3 planning.

## Implementation notes

Added while landing groups A–D and E1. None of this reopens D1–D4; it records where an anchor or a
piece of reasoning above turned out to be wrong, so the next reader trusts the code over the prose.

- **`HeadlessTurnExecutor.cs:622` is the wrong anchor.** That region is the replay path, not a tool
  round loop. A headless run’s live rounds go through `BackgroundAssistantTurnRunner`, which calls
  the same `IAiClientService` loop an interactive turn does, so they inherit the injection for free.
  The replay append does run outside any loop, and there `screen_capture` refuses and tells the model
  to re-issue the call on the resumed step, where the approval has become a named grant.
- **Safeguard 1 needs more than omission.** Leaving `Screen` out of `PresetClasses` is necessary but
  not sufficient: a restored grant envelope parses any `ToolClass` name, so a saved run could name
  `Screen` as an auto-approve class. `ToolAutonomy.Resolve` excludes it explicitly as well.
- **The capture audit directory is routed; the consent trail is not.** `ScreenCaptureAuditDirectory`
  hangs off `LocalDataDirectory` rather than the real profile root, so a walkthrough on a throwaway
  profile leaves nothing in the user’s own trail. `DataDirectoryRoutingTests` turned out to be a
  source scanner with no per-path rows, so only `PiaPathsTests` gained one.
- **The unattended notice is a persistent Flow item, not a toast.** An unattended capture happens
  while the user is elsewhere, so a three-second toast is gone before they look.
- **`screen_list_targets` is filtered for an unattended run.** Handing a run nobody is watching every
  open window’s title contradicted safeguard 2, so for those runs the list drops every display and
  keeps only allowlisted windows. The exactly-one-visible-window rule stays at capture time.
- **`CaptureTarget` carries the process id.** Window handles are recycled, so an approved card or an
  allowlist verdict could otherwise be spent on a different program’s window; the capture re-reads
  the pid and refuses on a mismatch.
- **Pia blacking itself out is its own failure reason.** Below Windows 10 2004 the fallback paints
  Pia black rather than hiding it, which can trip the near-uniform check; that case says so instead
  of blaming the captured app.
- **Six of the nine `ToolClass` consumers needed no `Screen` arm.** They persist an int, parse a name
  or emit a timeline row generically. Only `ToolClassifier`, `ToolAutonomy` and `ActionCardBuilder`
  switch on the member.
- **The screen plugin is GUID `…00B`**, following assignments at `…00A`.
