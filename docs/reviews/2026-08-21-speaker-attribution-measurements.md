# Measurements: the speaker-attribution fixture, and what the fixes moved

Date: 2026-08-21. Plan: `docs/superpowers/plans/2026-08-21-speaker-attribution-fixes.md`.
Diagnosis: `docs/reviews/2026-08-21-stale-label-diagnosis.md`.

## How to reproduce a number in this document

```powershell
./scripts/Get-SpeakerReference.ps1 -VideoPath '<recording>.mp4' `
    -LayoutPath scripts/speaker-reference/<name>.layout.json `
    -OutputPath scripts/speaker-reference/<name>.reference.json

./scripts/Invoke-MeetingReplay.ps1 -AudioPath '<recording>.mp4' -RosterSize <humans> -RunName <run>
./scripts/Measure-SpeakerAttribution.ps1 -LogPath <log> -ReferencePath <reference> `
    -NameMapPath scripts/speaker-reference/<name>.names.local.json
```

The reference comes from the video, not from a human: Teams burns a per-participant active-speaker
pill into the recording, and the extractor classifies each tile's name-label rect by mean colour. The
measured margin is a blue lead of ~56/255 when lit against ~4 when idle on both recordings, so the
threshold is not delicate. Speakers are identified by tile position (A, B, C…); the tile-to-name map
is a one-time hand-read and stays in a gitignored `*.names.local.json`.

## Reference timelines

| Recording | Length | Roster | Talkers | Overlap | Unusable layout |
|---|---|---|---|---|---|
| workshop | 964.6 s | 10 | **4** (508.5 / 176.8 / 37.0 / 9.8 s) | 51.8 s | 95.2 s (opens in a different layout for 90 s) |
| lsp | 2997.7 s | 5 | **5** (1752.0 / 721.8 / 204.2 / 115.5 / 12.0 s) | 170.8 s | 0.5 s |

The workshop recording is the roster-much-greater-than-talkers case the ceiling must not inflate: six
of the ten never speak.

## Two caveats on every number below

**The baseline is HEAD + Task 1a, not bare HEAD.** `DebugNoOpMeetingSession` reported an empty
roster, so `SetExpectedSpeakers` was never called and every replay before this one measured
`expected=0` — the roster ceiling off, which is not the shipping configuration. The ~10-line fix has
to be in the baseline for the baseline to mean anything. Three diagnostic log lines (per-segment
label, the pass's reassignment pairs, and what the ViewModel did with them) are also in both builds;
they add no behaviour and are what makes the metric computable at all.

**The fixture measures the service's labels, never the rendered ones.** Everything the metric script
reports is parsed out of the log, which carries service-side labels only. So of whole-plan acceptance
#4 — "≤ 6 distinct labels *numbered from 1*" — the fixture can settle the count and cannot see the
numbering: `Invoke-MeetingReplay.ps1` clicks Stop but never Save, so no transcript artifact exists to
read `DisplayLabel` off either. The renumbering is covered by `DisplayLabel_*` and
`BuildMarkdown_EmitsDisplayLabels` in `MeetingAttendeeViewModelTests` and by nothing else. Read every
label number below as the diarizer's, not the user's.

**Alignment is measured, not assumed.** Replay is paced by `Task.Delay`, so it runs at ~0.84x real
time and wall-clock elapsed is not stream time. The rate comes from the log's own two anchors against
the reference's known duration, and a small offset sweep over the speech masks absorbs pipeline
latency. The residual is printed with every result; a run whose agreement collapses invalidates its
attribution number rather than quietly skewing it.

## Prediction, written before the end-state runs completed

Recorded up front so the delta reads as an explanation held in advance rather than one found
afterwards. Whole-plan acceptance #5 asks for attribution to be *reported*, not targeted.

* **Label count should fall, and that is the point.** Task 3 closes the only path that could mint a
  label from a 1.5–2.0 s segment, and those labels were the residue.
* **Task 4's ceiling will barely bind on the workshop recording.** The roster is 10, so the cap sits
  at 11, and the baseline never reached it. Almost all of the improvement there should come from
  Task 3. On the lsp recording (roster 5, cap 6) the ceiling has room to act.
* **Attribution has one denominator effect and one cascade, and the cascade is the larger risk.**
  The denominator effect is benign: a segment that used to mint its own label scored as wrong, and now
  carries no label and leaves the scored set, which nudges the percentage up without any voice being
  attributed better. The cascade is not. Task 3 also stops a *matched* sub-floor segment from calling
  `RunningCentroid.Add`, and on the parent commit those embeddings did move the centroid. From the
  first sub-floor match onward the centroid trajectory differs, which changes the provisional label of
  later eligible segments, which changes `_clusterBySegment`, which changes the greedy overlap match in
  every subsequent pass. That can move attribution by more than a couple of points in **either**
  direction, and it would not be a fix working — it would be a different trajectory through the same
  unfixed threshold loop. First thing to check if the number moves: diff the pass-by-pass cluster
  counts and cuts between the two logs. An early divergence in the cut sequence is this mechanism.
  Nothing in Tasks 2–6 deliberately touches which voice matches which centroid.
* **The parked-corrections fix should change nothing on the workshop recording.** Its baseline run
  lost zero corrections (see below), so there is nothing there for the fix to recover.
* If accuracy moves more than a few points in either direction, that is an unplanned side effect to
  understand before merging, not a win.

## HEAD + Task 1a — workshop

```
Pacing  : 964.6 s of audio in 1154.5 s wall clock → rate 0.836x
Align   : offset -2.95 s, speech-mask agreement 76.8 %
Segments: 234 emitted, 234 transcribed, 28 below the diarization floor
Passes  : 42, expected=10

LABEL COUNT
  distinct labels ever registered  : 12
  distinct labels in the final pass:  7   [1, 2, 4, 5, 7, 6, 11]
  distinct labels in the transcript:  7
  true talkers in the recording    :  4

RETRO-CORRECTIONS
  emitted: 36    reached a bubble: 36    lost (not journaled yet): 0

ATTRIBUTION (segment midpoint vs the burned-in indicator)
  scored 138 segments (497.4 s) — 79.0 % by segment, 83.2 % by duration
  excluded: 16 overlapped (74.2 s), 38 indicator-off (104.3 s), 14 unusable layout (63.4 s)
  unlabelled (below the 1.5 s floor): 28

CONFUSION (seconds)
  Speaker 2  → Marius     296.0   (+14.0 Lukas, +13.4 Andreas)
  Speaker 5  → Andreas    117.9   (+24.6 Marius, +6.8 Florian, +1.8 Lukas)
  Speaker 7  →  —          19.4 Marius
  Speaker 11 →  —           3.5 Marius
```

Three things worth reading off this rather than the headline:

1. **12 labels for 4 talkers, ending on 7, with the counter at 11.** The defect reproduces on a
   recording, not just in a live meeting.
2. **Two of the four talkers never got a label of their own.** Lukas (37 s) and Florian (9.8 s) are
   scattered across Speaker 2 and Speaker 5. That is the deferred cut→threshold loop, and it is what
   actually decides accuracy — no fix in this plan addresses it.
3. **Zero corrections were lost in this run.** The race in the diagnosis is real — it is proved from
   the 2026-08-21 live log and by a unit test that fails without the fix — but it did not fire here.
   It needs a pass to move the *triggering* segment specifically, and in this replay none did. Worth
   stating plainly: this recording does not exercise that defect.
