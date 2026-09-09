# Screen vision — checklist

**Status:** Groups A–D landed, C4's tray half excepted, plus E1's service and probe.
**G1 answered 2026-09-09: GDI holds** — 0 black frames across Chromium, Electron, a WPF/Win32 mix
and a DirectX terminal, and no leak of Pia's own window into a display grab.
**G2 answered 2026-09-09: Mixed** — Chromium yields the page body, the one Electron app probed
yields only its own title. So E2's OCR fallback is now REQUIRED rather than conditional, and E3
must read an empty snapshot as "fall back", not "this window has no text".
**G3 ran 2026-09-09 and is still UNANSWERED** — the whole client chain worked and the request died
at the server's model router ("no endpoints found that support image input": 29 candidates, none
image-capable), so the message *shape* was never judged. It needs one run against a deployment
that has a vision endpoint; nothing in the client has to change first. D2 stays unticked, and D2a
is unproven live because no response ever came back. See
[2026-09-09-g3-live-round.md](2026-09-09-g3-live-round.md).
**Owner:** Marco Altmann
**Written:** 2026-09-07
**Origin:** [2026-09-07-screen-vision-design.md](2026-09-07-screen-vision-design.md), which is the
executable design and holds the four owner decisions D1–D4. This file is the tracking surface — tick
each box in the commit that lands it.

**Scales.** *Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a
new surface · `L` a week or more, a new subsystem. *Value:* `High` user-visible or a real risk
closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | What it can cancel |
|---|---|---|
| **G1 — ANSWERED, GDI holds** ([results](2026-09-09-capture-probe.md)) | Does `PrintWindow(PW_RENDERFULLCONTENT)` and a screen `BitBlt` return usable frames across the apps this user base actually runs — Office, Teams, Chrome/Edge, VS Code, a PDF viewer, a remote-desktop window? | The GDI backend. A high black-frame rate sends step A1 back for a `Windows.Graphics.Capture` implementation (a D3D11 device plus a staging-texture copy — `M`, not `XS`) before any UI is worth building. Answered by A5, deliberately the cheapest step in the plan. |
| **G2 — ANSWERED, Mixed** ([results](2026-09-09-uia-text-probe.md)) | Is a UIA text snapshot of a target window usable without the user switching accessibility on, specifically in Electron and Chromium windows? | Phase 3's engine, i.e. group E. A "no" means watch mode is OCR-only (slower, lossier) or does not ship — it does **not** touch groups A–D. |
| **G3 — RAN, still unanswered** ([results](2026-09-09-g3-live-round.md)) | Does PiaCloud accept a `ChatRole.User` message carrying a `DataContent` interleaved between a tool result and the next round, and does the model actually attend to it? | The shape of phase 2. A "no" forces the capture to be delivered on the *next user turn* instead of mid-round, which changes D1's second layer from "Pia asks and sees immediately" to "Pia asks and sees on the next exchange". Answered inside D2. |

Do not tick a dependant of an open gate without revisiting it.

## Group A — the capture seam (foundation, no UI)

- [x] **A1. Build `IScreenCaptureService` over GDI.** `BitBlt` from the screen DC for a monitor,
      `PrintWindow(…, PW_RENDERFULLCONTENT)` for a window, returning a frozen `BitmapSource`;
      interop as `[LibraryImport]` partials in `Native/ScreenCaptureInterop.cs`, matching
      `NativeHotkeyService`. Confirm first that the process really is PerMonitorV2 (nothing in the
      project configures DPI, so it should be WPF's default) and treat every interop coordinate as
      physical pixels.
      *Deps:* — · *Effort:* S · *Value:* Enabler

- [x] **A2. Refuse a black frame instead of sending one.** Detect near-uniform output and fail with a
      specific reason; unit-test it against a synthetic bitmap. A password manager or DRM video is
      the OS working as intended — never work around it.
      *Deps:* A1 · *Effort:* XS · *Value:* High

- [x] **A3. Enumerate targets with the four filters.** Monitors plus top-level windows, rejecting
      Pia's own windows, invisible windows, `WS_EX_TOOLWINDOW`, and `DWMWA_CLOAKED` ghosts. Keep the
      predicate pure so it tests against a fake window list.
      *Deps:* A1 · *Effort:* S · *Value:* Enabler

- [x] **A4. Exclude Pia from capture, transiently.** `SetWindowDisplayAffinity` over every Pia HWND
      around a monitor capture and restored to `WDA_NONE` after — set permanently it would black Pia
      out of the user's own Teams screen-share. Fall back to `WDA_MONITOR` below Windows 10 2004
      (the TFM floor is 1809). Without this step a full-monitor capture ships the chat history back
      to the model.
      *Deps:* A1 · *Effort:* XS · *Value:* High

- [x] **A5. Probe the real desktop. This is G1.** Capture Office, Teams, Chrome/Edge, VS Code, a PDF
      viewer and a remote-desktop window; record which give a usable frame, which give black, and how
      legible 10pt text is at the 1568px ceiling. Write the results into this folder.
      *Deps:* A1, A2 · *Effort:* XS · *Value:* High
      Run 2026-09-09 → [2026-09-09-capture-probe.md](2026-09-09-capture-probe.md). 6 of 6 windows
      and 1 of 1 display usable, 0 black; ~10pt stayed readable at the 1568px ceiling; the Pia
      region came back excluded and the affinity was restored. Office, Teams, a PDF viewer and
      `mstsc` were not open, so a re-run with those would widen the sample — it cannot overturn
      the backend decision.
      **Not run — G1 is open.** The probe is built and excluded from the default gate. It needs an
      unlocked, interactive desktop with Word or Excel, Teams, a browser, VS Code, a PDF, an mstsc
      session and an elevated Task Manager open; a background or disconnected session gets no DWM
      composition. Run `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --explicit only
      --filter-class Pia.Tests.Services.Screen.ScreenCaptureDesktopProbe`. It writes
      `<date>-capture-probe.md` into this folder with the legibility column and the verdict blank.

## Group B — phase 1, the user shows Pia something (gated on G1)

- [x] **B1. Build the picker dialog.** `ScreenCapturePickerView` plus its ViewModel over
      `EnumerateTargets`, one thumbnail per target, through the existing `IDialogService`. Needs
      `ScreenCapturePicker_` automation ids — the per-row id in the binding form, not a literal —
      plus the `ViewAutomationIdTests` row in the same commit.
      *Deps:* A3, A5 (G1) · *Effort:* S · *Value:* Enabler

- [x] **B2. Wire the composer button.** A capture button in the Assistant composer that opens the
      picker and hands the result to the existing `PrepareImageAttachmentAsync`, so the shot lands as
      a thumbnail the user sees before sending. This is the step that makes the feature real.
      *Deps:* B1, A4 · *Effort:* XS · *Value:* High

- [x] **B3. Add the optional global hotkey.** A settings-configured hotkey through
      `INativeHotkeyService`, registered the way the fast-path hotkey already is in
      `TrayIconService`.
      *Deps:* B2 · *Effort:* XS · *Value:* Med

- [x] **B4. Make the provider gate legible.** Disable the capture button with a tooltip when the
      default Assistant provider is not PiaCloud (D2), so the refusal happens before the capture
      rather than after it.
      *Deps:* B2 · *Effort:* XS · *Value:* Med

## Group C — gating, audit, allowlist (needed before any model-initiated capture)

- [x] **C1. Add `ToolClass.Screen = 10` and its gate rules.** Append the member last — the enum is
      persisted. Leave it **out** of `RunAutonomyPolicy.PresetClasses` so no preset can auto-approve
      a capture, and add the rule in `ToolAutonomy.Resolve` that a session grant minted inside the
      same run cannot satisfy an unattended capture. Extend `ToolAutonomyTests` with a `Screen` row
      per `ToolGateSurface` — including `Voice`, which follows the interactive rules here rather than
      refusing the way it does for `Assignment`.
      *Deps:* — · *Effort:* S · *Value:* Enabler

- [x] **C2. Build the capture audit trail.** A `ScreenCaptureAuditLog` on `JsonlConsentAuditLog`'s
      discipline — fire-and-forget, never throws, never blocks, drops loudly — writing timestamp,
      surface, run id, target kind, process name, dimensions and a *hash* of the title. New
      `PiaPaths.ScreenCaptureAuditDirectory` as a **property**, with its `DataDirectoryRoutingTests`
      and `PiaPathsTests` rows.
      *Deps:* — · *Effort:* S · *Value:* High
      Landed. `DataDirectoryRoutingTests` turned out to be a source scanner with no per-path rows, so
      only `PiaPathsTests` gained one. The directory hangs off `LocalDataDirectory` rather than the
      real profile root, so a throwaway-profile walkthrough leaves nothing in the user’s own trail.

- [x] **C3. Build the allowlist and its settings UI.** `ScreenCaptureAllowlistStore` holding the
      process-plus-title-pattern targets an unattended run or a watch session may see. Safeguard 2 of
      D3 — an unattended capture off the list refuses and says so.
      *Deps:* C1 · *Effort:* S · *Value:* High

- [ ] **C4. Tell the user it happened.** A toast after any capture made by an unattended run, and a
      tray state change while watch mode runs. The point of D3 is that the capability exists without
      the user being asked, so it must not exist without them finding out.
      *Deps:* C2 · *Effort:* XS · *Value:* High
      **Half landed.** An unattended capture publishes a persistent Flow item — a three-second toast
      is gone before a user who was elsewhere looks. The tray state change is deliberately not built:
      it has no caller until E3 arms a watch loop, so it would be dead surface with a live key.

## Group D — phase 2, Pia asks to look

- [x] **D1. Build `ScreenCaptureToolHandler`.** `screen_capture(target, match?)` and
      `screen_list_targets()`, shaped like `ChatHistoryToolHandler`, parking the prepared
      `ImageAttachment` in a pending slot keyed by `CallId` and returning a short text marker.
      *Deps:* A3, C1 · *Effort:* S · *Value:* Enabler

- [ ] **D2. Inject the capture into the round loop. This is G3.** Drain the round's parked captures
      **after** the `foreach` over its tool calls, not after the `workingMessages.Add(resultMessage)`
      inside it — a user message interleaved between two tool results is a sequence the provider
      rejects. Verify against PiaCloud that the appended message is accepted *and* attended to before
      building anything on top of it.
      *Deps:* D1 · *Effort:* M · *Value:* High
      **Code complete, G3 open.** The drain sits after the `foreach`, and the swap, the compactor pin
      and eight loop tests are in and green. Nobody has run a live Pia Cloud turn proving the provider
      accepts the interleaved user image message and that the model attends to it, so the box stays
      unticked. To close it: a Debug build with Pia Cloud as the Assistant provider, Notepad open with
      one known sentence, ask Pia to read it and approve the card, then confirm from the Debug log
      that the drain appended an image message, that the round after it replied quoting the sentence,
      and that the image count drops to zero on the round after that.

- [x] **D2a. Swap the consumed image for a placeholder.** Once the following response is back,
      replace the `DataContent` with `[screen capture, WxH, consumed]`, the way
      `AgentToolCarryover` handles oversized results — it will not do it for you, it matches only
      `FunctionResultContent`. Without this the capture is re-sent at ~1.5k image tokens on every
      remaining round. Check in the same step that the agent-run history path does not serialize the
      bytes to SQLite.
      *Deps:* D2 · *Effort:* S · *Value:* High
      Landed, and nothing persists the bytes: `AgentToolExchangeSerializer` keeps only
      `Function*Content`, and `AgentToolCarryover.Capture` drops the tagged message outright. Built
      against the mid-round shape, so a G3 "no" reopens it.

- [x] **D3. Decide `HeadlessTurnExecutor`'s answer.** It appends its own tool results, so it either
      gets the same injection or an explicit refusal for `screen_capture`. Silence here means a
      headless run makes a capture nothing ever looks at.
      *Deps:* D2 · *Effort:* XS · *Value:* Med
      Landed as an explicit refusal on one path and the shared injection on the other. A headless
      run’s live rounds go through `BackgroundAssistantTurnRunner` into the same `AiClientService`
      loop, so they get the injection; its own replay append runs outside any loop and refuses,
      telling the model to re-issue the call on the resumed step. A G3 "no" reopens it.

- [x] **D4. Refuse in the handler on a non-PiaCloud provider.** D2 at the tool boundary: never
      capture pixels that cannot be sent.
      *Deps:* D1 · *Effort:* XS · *Value:* Med

- [x] **D5. Gate matrix test across every surface.** A capture from an unattended run with no
      standing grant refuses; with a standing grant and an allowlisted target it proceeds; with a
      standing grant and an off-list target refuses. This is where D3's safeguards are actually
      proven, not in the design doc.
      *Deps:* D1, C3 · *Effort:* S · *Value:* High

## Group E — phase 3, Pia watches (gated on G2)

- [x] **E1. Extract text from a window via UIA. This is G2.** Snapshot the target's automation tree
      into compact text and measure it against Electron and Chromium windows with accessibility
      untouched. Answer the gate before building the loop around it.
      *Deps:* A3 · *Effort:* M · *Value:* Enabler
      Run 2026-09-09 → [2026-09-09-uia-text-probe.md](2026-09-09-uia-text-probe.md). Chromium
      returned the whole visible document in 13ms warm; the one Electron window returned 27 chars,
      its title and nothing else. Verdict Mixed. The Electron half rests on a single app (Teams and
      VS Code were not running), so a re-run would firm it up.
      **Not run — G2 is open.** The service, the compactor, the gate tests and the probe are in and
      green; the gate itself needs a human at a real desktop with Chromium and Electron windows open
      and no accessibility setting turned on. Run `dotnet test
      tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --explicit only --filter-class
      Pia.Tests.Services.Screen.UiaTextSnapshotDesktopProbe`. It writes `<date>-uia-text-probe.md`
      into this folder, and that file states the Yes / Mixed / No bar it has to be judged against.

- [ ] **E2. Add the on-device OCR fallback. REQUIRED, not conditional — G2 came back Mixed.**
      `Windows.Media.Ocr` over the captured bitmap for
      windows whose UIA tree is useless — a canvas, a remote-desktop session, an image viewer. Local,
      so it does not weaken D2. Detect a missing language pack once
      (`OcrEngine.TryCreateFromUserProfileLanguages()` returns null) and disable with a reason rather
      than failing per tick.
      *Deps:* E1, A1 · *Effort:* S · *Value:* High — an Electron target is blind without it

- [ ] **E3. Build `ScreenWatchService`.** One allowlisted target, one interval, a hash-compare per
      tick and a rolling delta buffer, so an idle screen costs nothing.
      *Deps:* E1, C3 · *Effort:* M · *Value:* High

- [ ] **E4. Expose watch to the model and the user.** A `screen_watch_read` tool over the buffer,
      plus arm/disarm UI with the tray indicator from C4.
      *Deps:* E3, C4 · *Effort:* S · *Value:* High

## Not yet planned

Candidates with no plan doc, kept here so they are not lost:

- **Drag-a-region capture.** Needs the DPI question settled first — the project has no
  `app.manifest`, so physical-pixel `GetWindowRect` values and WPF DIPs cannot be mixed blind. Sits
  on top of the same seam whenever it is wanted.
- **A `Windows.Graphics.Capture` backend.** The upgrade path if G1 says black frames are common.
- **A real per-provider vision-capability model**, lifting the PiaCloud-only image gate. Would fix
  pasted and dropped images on other providers too. Explicitly excluded by D2.
- **More than one image per message.** The composer holds one attachment today
  (`Msg_File_OneImageOnly`), which is why D2 injects captures as separate messages.
- **Watch mode surviving a restart** — reattaching to a target after the app or the watched app
  restarts. More useful, much harder to defend consent-wise.
- **A visible marker on a message saying which target it was captured from.** The audit line has the
  data; the chat bubble does not show it.

## Suggested order

Cheapest decisive work first, then the vertical slices:

1. **A1 → A2 → A5 (G1).** Under a day and a half to learn whether the whole GDI premise holds. Every
   other step in groups A, B and D is dead weight if it does not — do not build the picker first.
2. **A4 immediately after A1**, out of order if convenient. It is `XS`, and it is the one line that
   stops a full-monitor capture from leaking the chat back to the model.
3. **A3 → B1 → B2** — the phase 1 slice, and a shippable feature on its own. **B3, B4** whenever
   convenient afterwards; both are `XS` and independent.
4. **C1 and C2 in parallel with the B group** — independent of the UI, and C2 is the step that makes
   D3's audit promise real. **C3 → C4** next.
5. **D1 → D2 (G3) → D2a → D5.** Answer G3 as early inside D2 as possible: a "no" reshapes the phase,
   and the reshape is much cheaper before D2a and D5 exist. Do not let D2a slip — an un-swapped
   capture quietly multiplies the token cost of every remaining round. **D3, D4** are `XS` cleanups
   after.
6. **E1 (G2)** on its own, whenever phase 3 becomes worth starting — it needs nothing from groups B
   to D and its answer decides whether the rest of E happens at all. Then **E3 → E4**, with **E2**
   only if E1 came back mixed.
