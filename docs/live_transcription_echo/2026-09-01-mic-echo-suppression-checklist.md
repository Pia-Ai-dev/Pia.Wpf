# Microphone echo suppression — checklist

**Status:** A–D landed 2026-09-01; E is open · **Owner:** Marco Altmann · **Written:** 2026-09-01
**Origin:** [2026-09-01-mic-echo-suppression.md](2026-09-01-mic-echo-suppression.md), which root-causes
the two live-transcription defects reported against Pia 1.4.15.

Tick each box in the commit that lands it.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Steps

- [x] **A1 — Voice Capture DSP interop.** `IMediaObject`, `IPropertyStore` and the media-type structs in
      full vtable order, reusing NAudio's public DMO buffer types.
      *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **A2 — Echo-cancelling microphone source.** `WindowsAecMicCaptureService` drives the DSP in source
      mode, holds a silent render stream open, and publishes 16 kHz mono float frames.
      *Deps:* A1 · *Effort:* M · *Value:* High
- [x] **A3 — Automatic fallback.** `EchoCancellingMicCaptureService` picks the DSP source and drops back
      to the plain microphone when it fails or never produces a buffer.
      *Deps:* A2 · *Effort:* S · *Value:* High
- [x] **A4 — Opt-out setting.** `AppSettings.MicEchoCancellation`, its checkbox in the assistant settings,
      three locales, and its liveness classification.
      *Deps:* A3 · *Effort:* XS · *Value:* Med
- [x] **B1 — Speech time on every utterance.** `IAudioCaptureSource.StartedAt` plus
      `TranscriptUtterance.SpeechStart` / `SpeechEnd` derived from `VadSegment.StartSample`.
      *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **C1 — Cross-channel echo detector.** `EchoDetector`: voice-activity overlap decides suspicion,
      recognised text decides the verdict, undecided suspects are parked.
      *Deps:* B1 · *Effort:* M · *Value:* High
- [x] **C2 — Gate integration.** The `You` branch of `ConsentForwardLoop.ProcessAsync` consults the
      detector; the read loop releases parked utterances on a bounded wait and on shutdown.
      *Deps:* C1 · *Effort:* S · *Value:* High
- [x] **D1 — Serialize the shared recognizer.** A decode gate in both sherpa engines, and the unverified
      thread-safety claim removed from the Whisper doc.
      *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **E1 — Verify on real hardware.** Loudspeaker meeting, then the three release-safe log lines and the
      `droppedMicEcho` count off the session-stopped audit event. See "Still open" in the plan doc; the WAV
      tee is `#if DEBUG` and is not available on a shipped build.
      *Deps:* A2, C2 · *Effort:* XS · *Value:* High
- [ ] **E2 — Watch the render keep-alive.** `Could not open a silent render stream for the echo canceller`
      is the one failure that disables AEC without looking like an AEC failure. Confirm it does not fire on
      real hardware, and decide whether it deserves a user-visible warning rather than a log line.
      *Deps:* E1 · *Effort:* XS · *Value:* Med

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| E1's `droppedMicEcho` count | Does the DSP remove the echo well enough that the detector almost never fires? | Whether C1's overlap and similarity thresholds need tuning, or stay conservative as a pure consent-hole guard |
| E1's startup log line | Does the DSP engage on this hardware at all, or is the fallback silently carrying every session? | Whether A2 needs a second capture strategy (WASAPI communications category, or a bundled AEC) |

## Not yet planned

- Rendering the transcript's visible times from `SpeechStart` instead of recogniser-return time. Needs a
  decision on ordering first: a late-arriving utterance would otherwise print a time earlier than the
  bubble above it.
- `MicAudioCaptureService` records waveIn device 0 rather than the Windows default input.

## Suggested order

A1 → A2 → A3 → B1 → C1 → C2 → A4 → D1 → E1 → E2. The cheap deterministic work (B1, C1, C2, D1) is all
testable in `dotnet test`; A2 and E1 are the parts only real hardware can settle.
