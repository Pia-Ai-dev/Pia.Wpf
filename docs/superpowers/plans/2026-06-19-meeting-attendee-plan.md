# Meeting Attendee — First-Shot Implementation Plan

- **Date:** 2026-06-19
- **Branch:** `feature/meeting_attendee` (based on `origin/feature/meeting_transscription` @ `e3e6054`, plus cherry-picked save-to-markdown `50f6aa4`)
- **Mode:** Autonomous first shot. Definition of Done is compile + unit tests + static review. Live behaviour (browser join, audio flow) is **UNVERIFIABLE in this environment** — see DoD.

## Goal

A local "meeting attendee": the user pastes a Teams meeting URL into the UI; in the background Pia joins the meeting via an automated browser as **"{User}'s assistant"**, captures the meeting audio, and transcribes it on-device using Pia's **existing** live-transcription pipeline. A live transcript shows in the UI and is saved at meeting end.

**v1 scope (locked):** Teams only · works whether or not the user is personally in the meeting · live transcript display + save at end · transcript only (no auto-summary/Q&A).

## Why this shape (read before coding)

- The blueprint at `g:\Git\microsoft-teams-meeting-bot` is a **Node/Playwright** bot that joins Teams via a headless browser and **scrapes Teams' cloud captions** (no audio capture, no local STT). We reuse only its **join-automation knowledge**, not its caption approach.
- This branch **already contains a complete local live-transcription pipeline** (see Reuse Map). The feature is therefore small in concept: **add a browser-join attendee whose audio is fed into the existing pipeline.** Do **not** reinvent audio capture, VAD, the STT engine, the utterance stream, the bubble UI, or transcript saving.

## Reuse Map — existing code to build on (READ these; match their patterns)

| File | Role | How we reuse it |
|------|------|-----------------|
| `src/Pia.Wpf/Services/LiveTranscription/IAudioCaptureSource.cs` | 16 kHz mono Float32 source → `ChannelReader<float[]>` | The seam the browser audio plugs into |
| `src/Pia.Wpf/Services/LiveTranscription/LoopbackAudioCaptureService.cs` | WASAPI **endpoint** loopback → downmix → resample → 50 ms hops | **First-shot default audio source**; also the template for the per-process source |
| `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs` | source → Silero VAD → sherpa → `TranscriptUtterance` (tagged speaker) | Reuse verbatim; construct one for the attendee |
| `src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs` | Orchestrator: sources + engines + merged utterance channel + state machine | **Model the attendee orchestrator on this** (start/stop/dispose ordering) |
| `src/Pia.Wpf/Services/Interfaces/ILiveMeetingService.cs` | `LiveMeetingState`, `Utterances` reader | Mirror for `IMeetingAttendeeService` |
| `src/Pia.Wpf/Services/LiveTranscription/TranscriptionEngineFactory.cs` | Builds the sherpa engine from settings | Reuse `CreateAsync(...)` |
| `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionModels.cs` | `EnsureSileroVadAsync`, model download-with-progress (`IProgress<ModelDownloadProgress>`) | Reuse Silero ensure; **mirror the download pattern for Chromium** |
| `src/Pia.Wpf/Models/TranscriptUtterance.cs` | `TranscriptUtterance`, `TranscriptSpeaker { You, Them }` | Reuse; attendee audio is `TranscriptSpeaker.Them`. Do **not** add enum values |
| `src/Pia.Wpf/Models/TranscriptBubble.cs` | UI bubble model | Reuse for the attendee VM |
| `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs` | Consumes `Utterances` → bubbles, save command, listening state | **Model `MeetingAttendeeViewModel` on this** |
| `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml` (+ `.xaml.cs`) | Overlay rendering bubbles + `ListeningIndicator` | Reuse controls / mirror the overlay |
| `src/Pia.Wpf/Services/LiveTranscription/MeetingTranscriptPaths.cs` | Transcript file path/save helper (from cherry-pick) | Reuse for saving the attendee transcript |
| `src/Pia.Wpf/Services/Interfaces/IFileDialogService.cs` (+ impl) | Save-as dialog | Reuse for "Save transcript" |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` (`ToggleLiveTranscriptionCommand`, `IsLiveTranscriptionVisible`) | How live transcription is surfaced today (overlay toggled from Assistant) | **Mirror this entry-point pattern** — not a new nav page |
| `src/Pia.Wpf/Bootstrapper.cs` (~L227 `ILiveMeetingService`, ~L283 `LiveTranscriptionViewModel`) | DI registrations | Register the new services/VM adjacent to these |
| `src/Pia.Wpf/Models/AppSettings.cs` (`SyncUserDisplayName`) | User display name | Base `"{Name}'s assistant"` on this |
| `tests/Pia.Wpf.Tests/...` (xUnit v3 + NSubstitute) | Test conventions | `{Class}Tests`, `Substitute.For<>()`, `IDisposable` temp dirs |

## New Build Units

Implement in order. Each unit must leave `dotnet build` green for the whole solution and **must not break existing tests**. Commit each unit with a conventional prefix (`feat:`/`test:`/`refactor:`). **Stage only your own files** — never `git add -A`/`git add .` (leave `docs/superpowers/plans/2026-06-10-*` untracked). Put new code under `src/Pia.Wpf/Services/MeetingAttendee/` (new folder) unless a file clearly belongs beside its siblings (e.g. an `IAudioCaptureSource` impl in `Services/LiveTranscription/`).

### Unit 1 — Chromium provisioning (`ChromiumProvisioner`)
- Add `Microsoft.Playwright` `PackageReference` to `src/Pia.Wpf/Pia.Wpf.csproj`.
- `IBrowserProvisioner`: `Task<string> EnsureChromiumAsync(IProgress<ChromiumDownloadProgress>? progress, CancellationToken ct)` → returns the Chromium executable path; idempotent (skip if cached).
- Cache under `%LOCALAPPDATA%\Pia\Browsers` (set `PLAYWRIGHT_BROWSERS_PATH`). Provision on demand via `Microsoft.Playwright.Program.Main(["install","chromium"])`. Support an overridable download host (`PLAYWRIGHT_DOWNLOAD_HOST`) wired to a `const`/setting **defaulting to the Playwright CDN** — the **central-site URL is OPEN QUESTION #2**; leave a clear `TODO` + setting hook.
- Mirror `LiveTranscriptionModels` logging/progress style.
- **Acceptance:** compiles; path-resolution/"already-present" logic is a pure method with a unit test. Network download is **not** exercised in tests.

### Unit 2 — Teams join automation (`TeamsMeetingSession`)
- `IMeetingSession : IAsyncDisposable`: `Task JoinAsync(string meetingUrl, string displayName, CancellationToken ct)`, `Task WaitForEndAsync(CancellationToken ct)`, `Task LeaveAsync()`, `int? BrowserProcessId { get; }`, plus a state/event if useful.
- `TeamsMeetingSession` uses `Microsoft.Playwright`. Launch Chromium at the provisioned exe path, **headed but positioned off-screen** (e.g. window position far negative) so a real audio render session exists yet nothing is visible. Block/Fake the bot's **mic+camera** (so it's muted) but allow audio **output**. Do **not** use `--use-fake-device-for-media-stream` for playback.
- Port the join flow from the blueprint `g:\Git\microsoft-teams-meeting-bot\apps\teams-bot\src\procedures\join-procedure.ts` (read it). Steps + selectors:
  - Resolve launcher URL: follow redirects; strip `msLaunch=true`; add `msLaunch=false&type=meetup-join&directDl=true&suppressPrompt=true`. **Extract this URL transform as a pure function** (unit-tested).
  - `page.goto(launchUrl)` → click `button[data-tid="joinOnWeb"]` ("Continue on this browser").
  - Fill `input[placeholder="Type your name"]` with `displayName`; click `button:has-text("Join now")`.
  - Lobby detection: text `"Someone will let you in shortly"`. Admitted: `button[id="hangup-button"]` present (timeout → `Error`).
  - `WaitForEndAsync`: completes when the hangup button disappears / "call ended". `LeaveAsync`: click hangup if present, then close context/browser.
- Centralize selectors as named consts.
- **Acceptance:** compiles; URL transform + any pure helpers unit-tested. Live join is **UNVERIFIED** (flag it).

### Unit 3 — Browser audio source
- **First-shot default:** reuse `LoopbackAudioCaptureService` (endpoint) as the attendee's `IAudioCaptureSource`. No new code on the default path. (Caveat to record: endpoint loopback also captures other system audio and is audible — acceptable for first shot; isolation is the per-process source below.)
- **Per-process (implement, but NON-DEFAULT + flag UNVERIFIED):** `ProcessLoopbackAudioCaptureService : IAudioCaptureSource` doing per-process WASAPI loopback targeting `BrowserProcessId` via `ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_PARAMS` (`VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`, `INCLUDE_TARGET_PROCESS_TREE`). Reuse the downmix→resample→50 ms-hop→channel logic: prefer extracting a shared pure helper (`Pcm → 16 kHz mono float hops`) used by both this and (optionally) the endpoint service — but only refactor the existing service if its tests still pass. Isolate all P/Invoke in one file. Selectable via an `AppSettings` flag, **default = endpoint**.
- **Acceptance:** endpoint path wired + compiles; per-process file compiles with correct interop signatures, flagged UNVERIFIED; the extracted resample/hop helper has a unit test feeding a known WAV → asserts 16 kHz mono float output.

### Unit 4 — Orchestrator (`MeetingAttendeeService`)
- `IMeetingAttendeeService` (mirror `ILiveMeetingService`): `MeetingAttendeeState { Idle, ProvisioningBrowser, Joining, InLobby, Attending, Stopping, Error }`; `event EventHandler<MeetingAttendeeState>? StateChanged`; `ChannelReader<TranscriptUtterance> Utterances`; `Task StartAsync(string meetingUrl, CancellationToken ct)`; `Task StopAsync(CancellationToken ct)`.
- `MeetingAttendeeService` modelled on `LiveMeetingService.StartAsync`: read settings → `displayName = format(SyncUserDisplayName)` → provision Chromium → `EnsureSileroVadAsync` + `TranscriptionEngineFactory.CreateAsync` → `session.JoinAsync` (drive state transitions) → create audio source (endpoint default) + start → one `LiveTranscriptionEngineService(TranscriptSpeaker.Them, source, sileroPath, engine, _utterances.Writer, logger)` → background task awaits `session.WaitForEndAsync` then calls `StopAsync`. `StopAsync` stops source+engine, `session.LeaveAsync`, disposes all (mirror `DisposeAllAsync` ordering + error swallowing). Transcript **saving is handled by the VM** (reuse existing save), so the orchestrator only exposes `Utterances`.
- **Acceptance:** compiles; **state machine fully unit-tested** with `Substitute.For<IMeetingSession>()` + faked `IAudioCaptureSource` — assert transition sequence, error path (join fails → `Error` + cleanup), and dispose ordering. This unit is the most verifiable — test it well.

### Unit 5 — VM + View + entry point + DI + localization
- `MeetingAttendeeViewModel` (scoped), modelled on `LiveTranscriptionViewModel`: subscribe to `IMeetingAttendeeService.Utterances` → `ObservableCollection<TranscriptBubble>`; map `StateChanged` → status text; `[ObservableProperty] MeetingUrl`; `[RelayCommand] Start` (validate URL non-empty/Teams-like, gate on consent ack, call `service.StartAsync(MeetingUrl)`), `Stop`, `SaveTranscript` (reuse existing save + `MeetingTranscriptPaths`/`IFileDialogService`). A one-time `ConsentAcknowledged` bool gates Start (localized confirmation string).
- Entry point: mirror the `ToggleLiveTranscription` pattern in `AssistantViewModel`/`AssistantView.xaml` — add a "Join meeting" affordance revealing a URL input + the overlay (reuse `LiveTranscriptionOverlay`/bubble controls bound to the attendee VM, or a thin `MeetingAttendeeOverlay` copy). Keep UI minimal and consistent.
- DI in `Bootstrapper.cs` (near the live-transcription registrations): `IMeetingAttendeeService` (singleton), `IBrowserProvisioner` (singleton), `IMeetingSession` (transient or via factory), `MeetingAttendeeViewModel` (scoped).
- Localization: add keys to `CommonStrings`/`ViewStrings` (`.resx`, `.de.resx`, `.fr.resx`) for labels/status/consent, mirroring existing keys. **No hardcoded user-facing strings.**
- **Acceptance:** compiles; VM logic unit-tested (URL validation, command `CanExecute`/consent gating, utterance→bubble mapping, state→status) with a faked service.

### Unit 6 — Tests + DI architecture
- Add the tests called out above (xUnit v3 + NSubstitute). Ensure `tests/.../Architecture/DependencyInjectionTests.cs` still passes with the new registrations (extend if it enumerates services).
- Run the full suite once at the very end (Verify phase) — the suite is ~11 min; per-class filtering does not narrow it, so don't run it per unit.

## Audio strategy summary
First shot proves **join → audio → transcript** with the proven endpoint loopback (no new audio code on the default path). Per-process isolation is implemented but non-default and UNVERIFIED. If a headless/automated browser turns out **not** to render a capturable audio stream at all, the documented fallback is an **in-page WebRTC tap** (inject JS AudioWorklet in the Teams page → PCM → Playwright binding → `IAudioCaptureSource`) — note it as future work; do not build it in the first shot.

## Privacy / logging (this branch)
`Pia.Logging` (`SafeLog`/`SafeUrl`) does **not** exist on this branch. Follow the existing `LiveTranscription` logging conventions. Do **not** log the meeting URL, transcript text, or the user's display name at `Information` level — use `Debug` or omit. Keep any sensitive dumps behind `#if DEBUG`.

## Definition of Done (autonomous)
1. `dotnet build` → **0 errors** (warnings acceptable, don't add new ones gratuitously).
2. `dotnet test` → all unit tests pass (new + existing).
3. Static review (correctness + guidelines + security) has **no unaddressed high/critical** findings in the new code.
4. **UNVERIFIED — must be flagged in handover, not claimed done:** actual browser join against live Teams; actual audio capture + flow into STT; Playwright Chromium download/run; per-process loopback; off-screen-headed audio rendering.

## Open questions to surface in handover
1. **Base branch:** built on `feature/meeting_transscription` (+ cherry-picked save-to-markdown from `enhance-transcription-service`). Is that the intended integration target, or should this rebase onto `main` once the transcription work merges?
2. **Chromium central download host:** what URL should `PLAYWRIGHT_DOWNLOAD_HOST` point at? (Currently defaults to the public Playwright CDN.)
3. **Live vs end-of-meeting:** earlier you chose "end-of-meeting" based on my (incorrect) claim that live was more work — live already exists, so the first shot uses it (live display + save at end). Confirm that's the desired behaviour.
4. **Audio isolation default:** ship endpoint loopback (captures all system audio, audible) or switch the default to per-process (isolated, inaudible) once verified?
5. **Consent/recording policy:** is a one-time in-app acknowledgment sufficient, or is there an org policy requirement?
6. **Bot identity:** display name `"{SyncUserDisplayName}'s assistant"` — acceptable, or a fixed/configurable name?
