# The far end coming back in through the microphone

**Status:** implemented — runtime verification on real hardware still open · **Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** two defects reported against Pia 1.4.15 from the live transcript of 2026-09-01 09:15.

## What was reported

1. When the other party spoke shortly after the local user's last word, their sentence was appended to
   the local user's own bubble.
2. The other party's consent sentence appeared a second time, instantly, under **ich**.

The reporter guessed the two were the same cause. They were.

## Root cause

The meeting played over loudspeakers. The microphone re-recorded the far end, and the mic channel is
attributed to `TranscriptSpeaker.You` purely because of **which device the audio arrived on** —
`DirectTranscriptionService` builds the mic engine with `TranscriptSpeaker.You` and no diarizer, and the
loopback engine with `TranscriptSpeaker.Them` and the diarizer. Nothing ever compares voices.

The transcript proves the path is acoustic rather than a software duplication: the loopback pass produced
"Ilkin Kotsch", the mic pass produced "**Irkin** Kotsch". Both channels share one recognizer instance, so
identical audio would have produced an identical string. Two different strings means two different
recordings of the same voice.

Which of the two symptoms appeared was a race, not a rule:

- `TranscriptUtterance.Timestamp` was `DateTimeOffset.Now` at the moment STT **returned**, not when the
  audio was spoken. `VadSegment.StartSample` was already computed and thrown away.
- `TranscriptOverlayViewModel.GetOrCreateBubble` only ever compares against `Bubbles[^1]`.

Mic decode first → the echo joins the open "ich" bubble (symptom 1). Loopback decode first → its bubble
breaks the run and the echo opens a new "ich" bubble (symptom 2).

### Two things this is not

- **Not a missing pause rule.** `TranscriptGrouping.ShouldReuse` measures its 25 s window from
  `last.StartTimestamp`, not from the previous utterance. It is not a silence-gap rule, and tuning
  `BubbleWindowSeconds` would not have changed the mirroring at all.
- **Not only cosmetic.** `ConsentForwardLoop.ProcessAsync` emits every `Speaker == You` utterance
  unconditionally, before any check. Remote speech arriving through the microphone therefore bypassed the
  consent gate entirely — and because mic utterances carry a null `SpeakerLabel`, `RevokeSpeaker` /
  `RemoveSamplesFor` / `RenameSamples` could never reach it afterwards.

## What was built

### 1. The microphone now goes through the OS echo canceller

Pia was the odd one out: `MicAudioCaptureService` opens a raw NAudio `WaveInEvent` — the legacy MME path,
which applies no audio processing at all. Every VoIP client either opens the mic through an
echo-cancelling OS path or runs its own AEC against the render stream.

`WindowsAecMicCaptureService` drives the **Voice Capture DSP** (`CLSID_CWMAudioAEC`, `mfwmaaec.dll`) in
source mode: it opens the default capture and render endpoints itself and emits 16 kHz mono 16-bit PCM —
already the pipeline's format, so it drops straight into the `IAudioCaptureSource` seam.

Details worth knowing before touching it:

- **Property keys.** `MFPKEY_WMAAECMA_*` all share format id `6f52c567-0360-4bd2-9617-ccbf1421c939`, with
  pids counting up from `PID_FIRST_USABLE` (2) in header declaration order. `SYSTEM_MODE` is 2 and
  `DMO_SOURCE_MODE` is 3. `SYSTEM_MODE` is the one property the DSP requires; it is set to
  `SINGLE_CHANNEL_AEC` (0).
- **Device selection is left alone.** `MFPKEY_WMAAECMA_DEVICE_INDEXES` defaults to `(-1, -1)`, the default
  capture and render endpoint — the same render endpoint `LoopbackAudioCaptureService` records.
- **The render keep-alive is not optional.** The DSP produces *no output at all* while the render endpoint
  has no active stream, so a session with nothing playing would capture silence. The source holds an
  inaudible `WasapiOut` open for its lifetime.
- **The interop is hand-written.** NAudio ships `MediaBuffer` / `DmoOutputDataBuffer` / `IMediaBuffer` as
  public types and they are reused, but `NAudio.Dmo.MediaObject`'s only constructor takes the *internal*
  `NAudio.Dmo.IMediaObject`, so it cannot be handed a `CWMAudioAEC` instance. `IMediaObject`,
  `IPropertyStore` and the media-type structs live in `VoiceCaptureDspInterop.cs`, declared in full vtable
  order.
- **Fallback is mandatory and automatic.** `EchoCancellingMicCaptureService` starts the DSP source and
  falls back to `MicAudioCaptureService` if it throws — including the case where it starts but never
  produces, which `StartAsync` catches by waiting up to 3 s for the first buffer.

`AppSettings.MicEchoCancellation` (default on) turns it off for someone who always wears a headset and
dislikes the noise suppression and gain control that come with the DSP.

### 2. Utterances carry their own clock

`IAudioCaptureSource.StartedAt` dates sample 0 — set off the **first delivered frame**, back-dated by that
frame's length, because the device takes an unknown moment to actually open.
`LiveTranscriptionEngineService` turns `VadSegment.StartSample` into `TranscriptUtterance.SpeechStart` /
`SpeechEnd`.

Deliberately *not* done: the rendered transcript still shows `Timestamp`. Moving the visible times to
speech time would let a late-arriving utterance print a time earlier than the bubble above it, which is a
separate change with its own design question.

### 3. A cross-channel detector, as the backstop

`EchoDetector` runs from the `You` branch of `ConsentForwardLoop.ProcessAsync`. AEC closes the loudspeaker
path; the detector closes the consent hole itself and covers the case AEC cannot — someone physically in
the room who is also on the call.

Two independent signals:

- **Suspicion** comes from the far end's voice-activity windows, fed from the loopback engine's VAD via
  `DirectTranscriptionService.WireSpeakingChanged`. Known immediately, long before any text exists, so a
  mic segment that does not overlap far-end speech is emitted with **zero** added latency.
- **Confirmation** comes from the far end's recognised text. Token overlap at 0.6, tightened to an exact
  match under four words so a coincidental "ja" is not mistaken for an echo.

A suspect whose counterpart text has not been recognised yet is **parked**, never awaited. `RunAsync` is
the sole reader of the raw channel, so waiting inline would block the very utterance being waited for —
`ConsentForwardLoopTests.RunLoop_ResolvesAHeldEchoOnceTheLoopbackTextArrives_WithoutDeadlocking` pins
that. The read loop uses a bounded wait so a parked utterance is released even while the channel is quiet,
and anything still parked at shutdown is emitted rather than lost.

Drops are counted as `ConsentForwardLoop.DroppedEchoCount` and land in the session-stopped audit event as
`droppedMicEcho` — which is also the number to watch when deciding whether the detector's thresholds need
tuning now that AEC is in front of it.

### 4. Decodes on the shared recognizer are serialized

Not the cause here, but found on the way: both engine services share one `ITranscriptionEngine` and called
`TranscribeAsync` concurrently with no lock, on the strength of an XML-doc claim that a sherpa-onnx
recognizer is thread-safe for concurrent `Decode`. sherpa-onnx offers `DecodeMultipleOfflineStreams` for
the concurrent case, so the claim was not safe to rely on. Both engines now hold a decode gate.

## How far the interop is already proven

The build machine has no audio hardware at all, and the explicit smoke test
(`WindowsAecMicCaptureSmokeTests`, run with `-explicit only`) reports:

```
echo canceller unavailable: Voice capture DSP AllocateStreamingResources failed.
```

That is the expected stopping point, and it is a useful one: `CoCreateInstance`, both
`IPropertyStore.SetValue` calls and `IMediaObject.SetOutputType` — vtable slot 7, taking the hand-declared
`DspMediaType` and a marshalled `WAVEFORMATEX` — all returned success before
`AllocateStreamingResources` (slot 16) gave up trying to open devices that do not exist. A wrong vtable
order or struct layout would have failed or crashed well before that. What is *not* proven is anything
past device open: `ProcessOutput`, the `DmoOutputDataBuffer` round trip, and whether the cancellation
actually removes the echo.

## Still open

**Runtime verification on real hardware.** None of the DSP path can be exercised by `dotnet test` — the
build machine has no audio endpoints. What needs doing on a machine with loudspeakers:

1. Run a meeting on loudspeakers and repeat the reported scenario: speak, then have the other party start
   within a second or two. Their sentence must appear once, under their name, never under **ich**.
2. Read `%LOCALAPPDATA%\Pia\Logs\pia-*.log`. All three of these are `LogInformation` / `LogWarning`, so
   they survive a Release build — the WAV tee behind `PIA_DEBUG_DIRECT_TRANSCRIPTION_AUDIO_DUMP` does not,
   its whole composition block is `#if DEBUG`.
   - `Echo-cancelling mic capture started at 16000 Hz mono` — the DSP engaged.
   - `Echo-cancelling mic capture unavailable; falling back` — it did not, and the exception text names the
     step that gave up.
   - `Could not open a silent render stream for the echo canceller` — the keep-alive failed. This is the
     silent chain: no reference stream means the DSP produces no output, which trips the three-second
     first-buffer timeout, which means the fallback, which means the echo is back. Expect the
     "unavailable" warning right behind it.
3. Read `droppedMicEcho` off the session-stopped audit event. With AEC working it should be 0 or close to
   it; a high count means AEC is not engaging and the detector is carrying the whole load.

## Scope: direct transcription only

The Teams meeting attendee is unaffected and was deliberately left alone. It captures **no microphone at
all** — `MeetingAttendeeService` builds only a loopback source (`LoopbackAudioCaptureService`, or the
per-process tap), so it has neither the echo path nor the mic-side consent hole. Nothing in
`TranscriptGrouping`'s shared shape drifts as a result: this change touches attribution and emission, not
grouping.

## Aside, not fixed here

`MicAudioCaptureService` constructs `WaveInEvent` without setting `DeviceNumber`, so the fallback path
records waveIn **device 0** — not necessarily the Windows default input. Worth confirming against the
`LogAvailableDevices()` output before assuming the right microphone is being recorded.
