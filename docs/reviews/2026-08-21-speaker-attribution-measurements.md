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

**How this prediction held up: partly wrong, and in an interesting place.** The cascade it feared did
not happen — cluster geometry is byte-identical. But accuracy moved ~18 points on the LSP recording for
a mechanism the prediction explicitly ruled out. See "Why it moved, which contradicts the prediction"
below before treating anything in this section as the conclusion.

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

## Where this diverged from the plan, and why

| Plan said | What shipped |
|---|---|
| Task 2's invariant "fails on HEAD and passes after Task 3" — the cheapest hypothesis being that a gated segment is never iterated | **The cheapest hypothesis is wrong** and the log rules it out: on the parent commit `RunPassUnderLock` writes every `gatedCluster` straight back into `newLabelByCluster`, so a sub-floor segment's label *cannot* be the one that vanishes. The real mechanism is the correction/utterance race, and the fix is in the ViewModel. Task 5's hard gate therefore depended on that, not on Task 3. Full argument in the diagnosis. |
| Task 3: deleting the `gatedClusters` carry-over leaves dead code behind | It leaves a **new** way to break the same invariant. "A cluster whose only members are sub-floor cannot exist" does not hold: a cluster minted by an eligible segment can lose every eligible member to a later pass while a sub-floor segment still points at it, and with the carry-over gone that cluster is droppable. So the pass now also sweeps dangling `_clusterBySegment` entries and clears their label. That made `SpeakerReassignment.NewLabel` nullable — the symmetric partner of Task 3's own `SpeakerSegmentResult.Label` change, not a second unrelated one. |
| Task 5 lists the two overlays' bindings, `BuildMarkdown` and `DefaultAttendees` | Also `DirectTranscriptMarkdown`, which both overlays use for the **vault** copy — the one that gets ingested and summarized. Leaving it out would have shown Speaker 3 in the export and Speaker 17 in the vault. The `voiceStats:` block is keyed by the diarizer's label rather than a bubble, so it maps through the bubbles to stay consistent inside one document. |
| Task 7 names the type `DebugWavTeeAudioCaptureSource` | `DebugWavTeeAudioCaptureService`. `NamingConventionTests` requires an approved suffix and `Source` is not one; its three siblings (`Mic`/`Loopback`/`DebugFile`) all end in `Service`. |
| Task 1: "three pieces, none of which touches production code paths" | Two small additions were unavoidable. **Three log lines** (the per-segment label, a pass's reassignment pairs, and what the ViewModel did with them) — without them a replay's labels are not recoverable from the log and there is no metric at all. **Six AutomationIds** on the attendee join form, because it had none and the playbook's own rule is not to fall back to pixel-offset clicking; that is what makes the replay a script instead of a manual click-through. |
| Optional: an unlabelled-transcript switch | Left out. The plan marks the default as an owner call, and it is the one item here that changes what users see rather than what the code does. Still worth doing: on this recording two of four talkers never got a label of their own, so the summary will confidently mis-attribute them. |

## Still open

* **Nothing here has been through a real meeting.** Every number in this document comes from a replay
  against `DebugNoOpMeetingSession`: the roster arrives from an env var, `TeamsMeetingSession` never
  runs, and the six new AutomationIds have only ever been driven by `Invoke-MeetingReplay.ps1`. The
  renumbering in particular has never been *seen* — the metric reads service-side labels, and the
  replay script clicks Stop but never Save. **This needs a human smoke test before it is trusted**, and
  it is the one acceptance item genuinely outstanding.
* **The cut → threshold feedback loop.** Deliberately deferred, and it is what decides accuracy. The
  baseline shows why: `_matchSimilarity` is re-derived from the dendrogram cut every pass, and the two
  quietest talkers never earned a label of their own at any cut the run visited. **The fixture is now
  the arbiter for it** — which was the whole reason for deferring — and the LSP confusion matrix is its
  best input: `Speaker 10` holds 142 s of Alexander inside Andreas's label while Alexander's own label
  holds 8 s.
* **Embedding discriminability.** CAM++ `zh_en` on German over the Teams codec. Unblocked now: the
  fixture makes a model A/B a measurable one-line experiment.
* **The loopback-fidelity delta.** `PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP` proves the tee works, but
  the recordings are cloud-mixed Teams audio and Pia captures device loopback. Only a live capture
  produces the number that says how optimistic this fixture is.
* **The consent gate is still absent from the attendee path**, as the assessment found. Unchanged here.

## End state — workshop

```
Align   : offset 0.90 s, speech-mask agreement 81.5 %
Segments: 234 emitted, 234 transcribed, 28 below the diarization floor
Passes  : 42, expected=10   ← cluster-count sequence byte-identical to the baseline

LABEL COUNT
  distinct labels ever registered  : 10   (was 12)
  distinct labels in the final pass:  5   (was 7)   [1, 2, 4, 5, 10]
  distinct labels in the transcript:  5   (was 7)
  true talkers in the recording    :  4

RETRO-CORRECTIONS
  emitted: 38    reached a bubble: 38    lost: 0

ATTRIBUTION
  scored 159 segments (565.2 s) — 91.2 % by segment, 91.9 % by duration
  unlabelled (no placeable speaker): 37   (was 28)
```

### What actually changed, measured without the reference

The two runs emit the same 234 segments with the same sample counts and the same 42-pass
cluster-count sequence, so a direct label diff needs no reference and no alignment at all:

```
206 diarized segments in both runs
  190 carry an identical label   (92.2 %)
   16 changed — every one of them off a label that only existed because of the mint bug:
        7 × Speaker 7  → no label / Speaker 10
        3 × Speaker 11 → no label / Speaker 2
        2 × Speaker 6  → no label / Speaker 2
        4 × folded into an existing cluster
```

That is the honest statement of the code's effect: **16 of 206 labels moved, all of them off
sub-floor mints, and the clustering trajectory did not change.**

### The accuracy delta is not worth 12 points, and the fixture says so

79.0 % → 91.2 % is the headline, and most of it is not the fix. Scoring both logs at both offsets:

| pinned offset | HEAD | end state |
|---|---|---|
| −2.95 s (HEAD's own fit) | **79.0 %** | 71.8 % |
| +0.90 s (end state's own fit) | 70.4 % | **91.2 %** |

Each run scores best at its own fitted offset and ~8 points worse at the other's, so the offset is a
property of the *run*, not of the recording: `Task.Delay` pacing jitter is not uniform, and a
two-anchor linear map leaves a time-varying residual that a single offset can only compromise on.

Decomposing by duration instead, which needs no cross-run alignment: the baseline's `Speaker 7` and
`Speaker 11` held 22.9 s of speech and mapped to no reference speaker at all, so every second of it
scored wrong. Removing exactly that from the baseline's own numbers gives 413.8 / (497.4 − 22.9) =
**87.2 %**, i.e. about **+4 points by duration is attributable to the fix**, and the remaining ~5 is
alignment residual. That is consistent with the prediction above — a denominator effect — and *not*
with the centroid cascade, which the identical cluster sequence rules out.

**Fixture limitation to fix before the next comparison:** `DebugFileAudioCaptureService` logs only
"playing" and "finished playing", so the wall-clock→stream map has two anchors and no way to follow
drift. A periodic hop-count line (as `BrowserAudioCaptureService` already emits) would make the map
piecewise-exact and remove this term entirely. Until then, treat a cross-run attribution delta under
~8 points as noise, and read the label counts and the label diff instead — both are exact.

**A bug found in the metric script while doing this, worth knowing:** the structured logger renders a
null label as the literal `(null)`, which the parser first counted as a real speaker. It inflated the
end state's label count by one and scored its unplaceable segments as wrong. Fixed; every number above
is post-fix.

## Does the STT engine matter here?

Measured, because it is a reasonable thing to want to speed up:

* **Whisper medium is not the bottleneck and switching it saves nothing.** 773.5 s of speech
  transcribed in 803.7 s of engine time (1.04x real time), inside a replay whose wall clock is set by
  `DebugFileAudioCaptureService`'s per-hop `Task.Delay` at 0.836x. Transcription had headroom
  throughout and produced **zero** empty results in 234 segments.
* **Text quality is almost irrelevant to what this fixture measures.** `IdentifyOrRegisterSegment`
  runs *before* `TranscribeAsync` on the same samples, so segmentation, embeddings, clustering, cuts,
  minting and the roster ceiling are all upstream of the text.
* Two exceptions, both arguing for keeping the stronger model. An **empty** transcription suppresses
  the utterance entirely, so a weaker model silently shrinks the scored set. And transcription
  *latency* is the width of the correction/utterance race window, and it also decides when the
  diarizer's 30 s wall-clock pass trigger fires — 4 of this run's 41 pass intervals came from that
  trigger rather than the segment stride, and they landed identically in both runs at the same model.
* The lever that would actually shorten a replay is a rate multiplier on that `Task.Delay`. It is not
  free: at 4x the 30 s latency trigger essentially stops firing, so the pass sequence changes. Fine
  for a smoke test, not for a number that goes in this document.

## LSP — the recording that actually settles it

50 minutes, roster 5, all 5 speak, and its reference covers all but 0.5 s of the video. That makes it
the trustworthy half of the fixture: the workshop recording loses 95 s to a layout change and another
191 s to silence, which flattens the alignment objective and lets the fit wander.

| | HEAD + Task 1a | end state |
|---|---|---|
| distinct labels ever registered | **26** | **13** |
| distinct labels in the final pass | **16** | **6** |
| distinct labels in the transcript | 16 | 6 |
| true talkers | 5 | 5 |
| corrections aimed at an in-flight segment | **9** (dropped) | 4 (parked, then applied) |
| speech-mask agreement | 90.4 % | 95.4 % |
| attribution, by segment | **73.4 %** | **92.1 %** |
| attribution, by duration | **77.4 %** | **93.5 %** |

Whole-plan acceptance #4 asked for "≤ 6 distinct labels numbered from 1 for the 5-talker recording".
The final pass holds exactly 6 service-side labels, which `DisplayLabel` renders as Speaker 1–6.

### The gain here is not an alignment artefact

Unlike the workshop, this result survives every way of fitting the wall-clock→stream map — offset only
at the anchor rate, offset pinned at either run's value, and a joint rate+offset fit:

| fit | HEAD | end state |
|---|---|---|
| offset only, anchor rate | 73.9 % | 92.1 % |
| offset pinned −0.55 s | 73.9 % | 92.3 % |
| offset pinned +0.40 s | 74.1 % | 92.1 % |
| joint rate + offset | 73.4 % | 92.1 % |

An upper bound on the alignment contribution: the head run's speech mask disagrees with the reference
on 9.6 % of frames against the end run's 4.6 %, so at most ~5 points of the ~18 can be mapping error.
The rest is real.

### Why it moved, which contradicts the prediction

The confusion matrices show a structural change no two-second time shift can produce:

```
HEAD    Speaker 2  : Marco 1302.7  Andreas 147.9  Alexander 24.1  Dirk 34.8
        Speaker 11 : Marco  128.0  Andreas 403.9  Alexander 98.5
        …plus 12 more labels holding 1–25 s each

END     Speaker 1  : Marco 1559.7  Andreas   3.6                      → 99.8 % pure
        Speaker 10 :               Andreas 570.6  Alexander 142.1
        Speaker 13 : Dirk    81.6                                     → pure
        Speaker 8  : Martin   9.1                                     → pure
```

Marco was split across two labels and Andreas's label held 128 s of Marco; afterwards Marco's label is
99.8 % Marco. **I predicted this could not happen** — "nothing in Tasks 2–6 touches which voice matches
which centroid" — and that was wrong in a way worth naming. Task 3 does not change centroids or cluster
shapes (the 111-pass cluster-count sequence is byte-identical between the runs), but it does change
`_clusterBySegment`: removing the spurious sub-floor clusters removes them from the greedy
overlap match, so each new cluster inherits the *right* stable id instead of losing the tie to a
one-segment mint. The mint path was polluting identity assignment, not cluster geometry. Nine dropped
corrections on HEAD, all of them permanent mis-attributions, are the second contribution.

So the plan's expectation that Tasks 2–6 are label-count-only was too conservative: they are, on cluster
geometry, and they are not, on which cluster keeps which label.

### The residue is the deferred defect

`Speaker 10` still holds 142.1 s of Alexander inside Andreas's label. Alexander (204 s of speech) never
gets a label of his own worth the name — 8 s on `Speaker 7`. That is the cut→threshold loop, untouched
by design, and it is the next thing to fix.

## Task 7 acceptance — the dump round-trips

Setting both the replay path and the dump path tees the replay itself, so the mechanism is verifiable
without a live meeting:

```
run 1  PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE=<45 s clip>
       PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP=<...>\tee.wav
       → tee-replay.wav : pcm_s16le, 16000 Hz, mono, 47.1 s;  13 VAD segments closed

run 2  PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE=<...>\tee-replay.wav
       → 13 VAD segments closed, 14 utterances, 3 labels
```

Same segment count out of the dump as out of the original, which is what the tee promises: the WAV and
the pipeline saw the same stream. (The WAV runs 2 s longer than the clip — AAC decoder priming and
padding, not the tee.)

**This proves the mechanism, not the thing the task exists for.** These recordings are cloud-mixed
Teams audio; Pia captures device loopback of a browser tab, with a second D/A→A/D pass and different
AGC. The delta between the two — how optimistic the `artifacts/` fixture is — still needs one live
capture with `PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP` set and no replay path. That half stays open.
