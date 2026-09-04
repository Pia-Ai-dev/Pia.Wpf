# Evidence: the bubble pin holds, and costs nothing when unused

**Status.** Both gates cleared, 2026-09-04, on three real Teams recordings, plus one app-level
replay through the real menu. Method differs from the one the checklist proposed — see *Why the bench
and not the replay*.
**Owner.** Marco Altmann.
**Written.** 2026-09-04.
**Origin.** Step A6a and decision gates G-A and G-B of
[2026-09-03-manual-bubble-reattribution-checklist.md](2026-09-03-manual-bubble-reattribution-checklist.md).
"Reliable" is the load-bearing word in
[2026-09-03-manual-bubble-reattribution.md](2026-09-03-manual-bubble-reattribution.md); this is what
makes it a measurement rather than a claim.

## G-A — a pin survives the rest of the meeting

A correction is fired a quarter of the way into each recording, from a seam between two identify
calls — exactly where a right-click lands. The run then continues to the end of the recording.

The assertion is the **silence**: no `Adaptive pass reassigned:` line after the correction may name a
pinned segment id. A segment that merely happens to end up on the right label proves nothing about
exclusion; only never being touched does.

| recording | length | eligible segments | pinned at | passes after the pin | pinned ids moved |
|---|---|---|---|---|---|
| 2026-08-19 daily | 28.5 min | 266 | 460 s | 42 | **0** |
| 2026-08-20 daily | 28.0 min | 249 | 388 s | 43 | **0** |
| 2026-08-21 daily | 16.9 min | 158 | 258 s | 26 | **0** |

Each recording is run twice: once pinning a single segment, once pinning **five consecutive**
segments onto one label. The five-segment case is the one that matters — a single pin cannot produce
the mechanic the plan names, where two pins left in the dendrogram vote under their pinned cluster
and swap a stable id between two voices. Both cases moved nothing.

## G-B — the change is inert when nobody uses it

The same three recordings, no correction fired, run on this branch and on `main`, sharing the same
embedding cache so the only difference is the diarizer code.

| recording | segments compared | passes (branch / main) | labels (branch / main) | differing lines |
|---|---|---|---|---|
| 2026-08-19 daily | 266 | 56 / 56 | 7 / 7 | **0** |
| 2026-08-20 daily | 249 | 55 / 55 | 6 / 6 | **0** |
| 2026-08-21 daily | 158 | 33 / 33 | 4 / 4 | **0** |

673 segments, byte-identical per-segment output. This is stronger than the "no accuracy or
label-count movement" the gate asked for: it is exact equality of the whole assignment, not agreement
of two summary numbers.

## Why the bench and not the replay

The checklist named `Invoke-MeetingReplay.ps1` plus `Measure-SpeakerAttribution.ps1`. Both gates were
cleared with `DiarizationBench` instead, for three reasons:

- **`Measure-SpeakerAttribution.ps1` needs a reference** built by `Get-SpeakerReference.ps1`, and
  none exists for these recordings. Building one is a separate pipeline run over the video's tile
  highlights, and it would still only produce an accuracy number to compare — where the bench
  produces the per-segment assignment itself.
- **The bench is deterministic and cheap.** It runs the shipping VAD and the shipping
  `AdaptiveSpeakerIdentificationService` over the real audio with the pass clock on stream time, so a
  28-minute meeting and its 56 passes take 40 seconds. That is what made the against-`main` diff
  possible at all, and what makes it repeatable.
- **`MediaFoundationReader` decodes the mp4 directly**, so no WAV tee and no replay run was needed to
  get the audio in.

What the bench does **not** cover, and where the evidence for it is instead:

- **The ViewModel/journal round-trip** — that a correction actually reaches a bubble.
  `AssignSpeaker_LandsOnTheBubbleThroughTheReassignmentEvent` wires a real
  `AdaptiveSpeakerIdentificationService` to `ApplyReassignments` and asserts the bubble moved. It is
  the test the plan describes as the one that catches an API reporting success and doing nothing —
  and the app-level run below covers the same ground end to end.
- **Wall-clock pass triggering.** The bench advances the clock with the stream, so the 30 s latency
  trigger fires on stream time. A live meeting with transcription backpressure visits a slightly
  different pass sequence. The pin mechanism does not depend on which passes run, only that they do —
  and 26 to 43 of them ran.
- **Speech-to-text.** Nothing is dropped to transcription backpressure and no segment is discarded
  for producing empty text, so the bench sees slightly more segments than the app would.

## The app-level check the bench cannot do

Run 2026-09-04 against a throwaway profile, with the 16.9-minute recording replayed through the real
attendee pipeline and both corrections fired from the real right-click menu.

- Both `MeetingAttendee_BubbleAssign_*` and `_BubbleRedetect_*` resolve on the bubble's context menu
  and act. Re-detect moved one segment; the picker offered exactly the other speakers visible on
  screen and assign moved three.
- **11 further passes and 3 reassignment batches followed, and not one named a pinned segment id.**
  That is the ViewModel/journal round-trip the bench cannot see, on the wall clock rather than stream
  time.
- Sticky display numbering was visible in the same transcript: Speaker 1, 2, 3, 4, 6, 7 — a gap where
  the diarizer dropped a label, and no number moved under a speaker who had not spoken.

## Caveats on the numbers

- **The roster is a guess.** These are daily stand-ups and the head count was not recorded, so all
  runs used `roster=6`. The roster sets the cluster ceiling, so it moves the label counts — but both
  gates compare runs at the *same* roster, so it cannot affect either answer.
- **Label counts are not accuracy.** 7, 6 and 4 surviving labels are reported to show the two runs
  agree, not to say the attribution was right. Nothing here scores who actually spoke.
- The recordings are not in the repo. Reproduce with
  `$env:PIA_BENCH_WAV`, `$env:PIA_BENCH_ROSTER`, `$env:PIA_BENCH_OUT` and
  `dotnet test -- --explicit only --filter-method "*Pin_*"`.
