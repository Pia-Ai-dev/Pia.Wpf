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

---

# 2026-08-22 — exact alignment, the bench, and two bugs the fixture was hiding

Written while executing `docs/superpowers/plans/2026-08-22-diarization-bench-and-threshold.md`
(Phases 0–3), on both fixture recordings.

## The alignment caveat is retired, and the old fit was worse than advertised

Every attribution number above carries a fitted wall-clock→stream mapping. `SileroVadDetector` now
reports the sample index each segment opened at, `Segment identified:` carries `start=`, and the
metric drops the fit entirely when every identified segment has one.

The re-measurement is not a small correction to the *method* — it caught the fit failing outright on a
fresh replay of the same recording:

| workshop, end-state code | recorded above (fitted 0.90 s) | this run, fitted | this run, **exact** |
|---|---|---|---|
| attribution by segment | 91.2 % | 77.1 % | **91.9 %** |
| attribution by duration | 91.9 % | 77.4 % | **91.5 %** |
| speech-mask agreement | 81.5 % | 76.2 % | 83.7 % |
| scored segments | 159 (565.2 s) | 144 (514.2 s) | 172 (607.1 s) |
| excluded as "no speaker lit" | — | 25 (78.4 s) | **0 (0.0 s)** |

The fitted offset came back as **6.30 s**, against a coarse sweep spanning ±6.0 s — it saturated its
own search bound, which is a clamp reported as a fit. The `no speaker lit` bucket collapsing from
78.4 s to zero is the mechanism: the offset had been pushing segments into stretches where the
reference's indicator is off. Exact alignment reproduces the previously recorded figure to within
0.7 points and needs no rate, no offset and no residual.

So the swing this document warned about is real, and it is a property of the *fixture* rather than of
the code: the workshop recording loses 95 s to a layout change and 191 s to silence, which flattens
the alignment objective enough that two replays of the same audio land 15 points apart. Any workshop
number quoted from a fitted log should be treated as unusable.

One stated divergence: segments below the 1.5 s diarization gate never reach identification, so they
never report a position. The exact path requires a position from every segment that *was* identified,
and the speech-mask check therefore covers labelled speech only (206 of 234 segments here).

## The rendered numbering is now observed, not inferred

`Invoke-MeetingReplay.ps1` walks the UIA tree after Stop and reads the labels the transcript is
actually rendering. Workshop, roster 10:

```
rendered: 73 labelled bubbles, 5 distinct
LABEL CHECK: pass (Speaker 1..5, roster 10)
```

Service-side the final pass holds `[Speaker 1, 2, 4, 5, 10]` — five labels with gaps. On screen they
are `Speaker 1..5`. That is the renumbering layer doing its job, and the two observations together are
stronger than either alone: the count *and* the numbering are now measured.

## Dropped segments: zero here, and the old warning could never have fired

The queue is `BoundedChannelFullMode.DropOldest`, under which `TryWrite` never fails — it evicts and
returns true. The "transcription is falling behind" branch was therefore unreachable, so every earlier
"no drops observed" reading meant nothing. The counter now hangs off the channel's `itemDropped`
callback, which discards the same segment as before.

**Workshop replay: 0 dropped segments.** No per-drop warning appears anywhere in the log; the
stop-time summary only prints when the count is non-zero, so a zero is evidenced by absence. At the
replay's 0.836x this is the easy case — the live 1.0x reading is Monday's.

## The bench reproduces the app, with a named divergence

`artifacts/wav/workshop-replay.wav`: 30,863,406 bytes = 964.48 s at 16 kHz mono s16le, against 964.6 s
of source audio — 0.12 s short, inside AAC priming and padding. The header confirms PCM, 1 channel,
16000 Hz, 16-bit.

| | app replay | bench | Task 4 tolerance |
|---|---|---|---|
| segments above the gate | 206 | 206 | ±2 % — pass |
| speech-mask agreement | 83.7 % | 83.7 % | — pass |
| attribution by segment | 91.9 % | 94.2 % | ±2 pts — **2.3, marginal** |
| distinct labels, final state | 5 `[1,2,4,5,10]` | 4 `[1,2,4,5]` | same count — **off by one** |

Both misses have one observed cause rather than a plausible one. The pass sequences agree exactly for
28 passes and then diverge:

```
app   (42): 5,4,4,4,4,4,4,4,4,4,4,5,5,5,4,4,4,4,5,5,5,5,5,5,5,5,5,5, 5,5, 4,4, 5,5,5,5,5,5,5,5,5,5
bench (41): 5,4,4,4,4,4,4,4,4,4,4,5,5,5,4,4,4,4,5,5,5,5,5,5,5,5,5,5, 4,4, 5,5, 5,5,5,5,5,5, 4,4,4
```

A two-pass phase shift, then a tail that settles on 4 clusters where the app settles on 5 — the
latency-trigger difference the bench documents (the app's 30 s trigger measures wall clock between
identify calls, which STT throughput gates; the bench measures stream time). The label disagreement is
also much smaller than the unlabelled counts suggest: app 206 − 7 nulls = 199 labelled, bench
206 − 8 = 198. **One segment.**

Cost: 0.5 s segmenting plus 8.5 s identifying for 964 s of audio, about 100x realtime, against a
3-minute target for a 50-minute recording. The sherpa native loads from the test host with no added
`PackageReference`.

## Two bugs, and the second one inverted the plan's conclusion

**1. A relative `-DumpPath` landed the WAV under `bin/`.** The app runs with its own working
directory, so `artifacts/wav/x.wav` resolved against the build output — where a `-t:Rebuild` deletes
it. Now resolved against the repo root. The tee also names its file `<name>-replay.wav` when both the
replay and dump vars are set, which the script now states.

**2. The embedding cache did not own its vectors, and every oracle number was computed from zeros.**
`Normalize` mutates in place and returns the same array; the cache stored that instance; the
identification service zeroes every embedding it holds on dispose, which is deliberate biometric
hygiene. The bench disposes the service before saving, so the file was written after the wipe:
160,019 of 160,698 bytes were `0x00`. The keys were perfect, which is why it looked plausible.

The signature was diagnosable from the report alone: intra and inter similarity both exactly 0.000
with σ 0.000, d' NaN, and a "best" threshold of 0.000 at a 50.0 % pair-decision error — chance. With
all similarities tied, nearest-centroid returns the first centroid every time and scores the dominant
speaker's share, which is why the result looked like a number rather than a failure.

`EmbeddingCache` now copies on both `Put` and `TryGet`, with a regression test that wipes the caller's
array after handing it over. What it changed:

| workshop oracle | from zeroed vectors | **from real vectors** |
|---|---|---|
| intra / inter similarity | 0.000 / 0.000, d' NaN | **0.506 ± 0.159 / 0.254 ± 0.116, d' 1.82** |
| best fixed threshold, pair error | 0.000, 50.0 % | **0.400, 16.6 %** |
| ORACLE enrollment (30 s/speaker) | 86.3 % | **98.7 % / 99.3 %** |
| ORACLE clusterer (k = 4 true talkers) | 74.4 % | **87.8 % / 89.4 %** |

**This reverses Task 7's decision.** 86.3 % sits in the plan's `< 88 %` bucket — "CAM++ `zh_en` is the
ceiling, Task 11 before any threshold tuning", which writes off Tasks 8 and 9. 98.7 % sits in `≥ 97 %`
— "the embedding is fine; the clustering and the threshold loop are the entire gap", which makes
Tasks 8 and 9 the work and consent enrollment the strongest product lever. The broken number pointed
exactly the wrong way, and it pointed there confidently.

Cache acceptance now holds: a warm run differs from cold in two lines only, the compute count
(206 → 0) and timing (8.5 s → **0.1 s**). Every measured value is identical.

## What Task 7 says on the workshop, and what it does not

- **Discriminability:** intra 0.506 ± 0.159 against inter 0.254 ± 0.116, d' **1.82**. The distributions
  separate. Best single fixed threshold **0.400**, at a 16.6 % pair-decision error.
- **Oracle nearest-centroid:** **98.7 %** by segment, 99.3 % by duration, over 153 segments. Read
  the per-speaker table in the LSP section below before using this: two of the four speakers were
  never scored, so it is not a bound on this recording.
- **Oracle k pinned to the true talker count (4):** 87.8 % / 89.4 % — *below* the live run's 91.9 %.
  Pinning k changes nothing on this recording: `expectedSpeakers` is a downward-only cap and the
  natural cut already sat at 4 or fewer, so 10 and 4 give the same answer. The adaptive
  online-plus-repass pipeline already beats naive offline clustering with k known.

Two things worth holding against the headline number.

**The best fixed threshold is 0.400 — the exact floor of the production clamp.** Today
`_matchSimilarity = clamp(1 − cut, 0.40, 0.60)`, and the baseline sits pinned at 0.60 for passes 1–10.
The optimum for this audio is the bottom of the band, so the derived threshold is systematically too
high. That is direct evidence for Task 8, from a recording rather than from an argument.

**98.7 % is not a bound on the quiet speakers.** `NearestCentroid` spends each speaker's first 30 s on
enrollment and scores the remainder, so a speaker with less than 30 s of speech contributes zero
scored segments. Workshop talk time is D 37.0 s, E 9.75 s, H 508.5 s, I 176.75 s — E is consumed
entirely, D nearly so, and 180 scoreable segments became 153 scored. The pooled figure is therefore
close to the accuracy on the two talkative speakers, while the confusions this fixture cares about
live among the quiet ones. A per-speaker breakdown is printed for exactly this reason; the numbers
above predate it.

## LSP — the recording that settles it, now measured without a fit

`artifacts/wav/lsp-replay.wav`: 95,926,318 bytes = 2997.7 s at 16 kHz mono s16le against 2997 s of
source, header PCM / 1 channel / 16000 Hz / 16-bit. Task 0 accepted on both recordings.

**The numbering claim is now observed on the recording the acceptance criterion was written about.**

```
rendered: 161 labelled bubbles, 6 distinct
LABEL CHECK: pass (Speaker 1..6, roster 5)
```

Whole-plan acceptance #4 asked for "≤ 6 distinct labels numbered from 1 for the 5-talker recording".
Service-side the final pass holds `[Speaker 1, 7, 8, 9, 10, 13]` — six labels with gaps — and the
screen shows `Speaker 1..6`. That inference is now an observation.

**Dropped segments: 0.** 537 segments queued, 474 identified, 63 below the 1.5 s gate, and no
per-drop warning anywhere in the log. Both fixture replays are clean, so transcription backpressure is
a live-only risk and Monday's 1.0x run is the first chance to see it.

### Exact alignment reproduces the recorded figure exactly

| LSP, end-state code | recorded above (fitted) | this run, **exact** |
|---|---|---|
| attribution by segment | 92.1 % | **92.1 %** |
| attribution by duration | 93.5 % | **93.2 %** |
| speech-mask agreement | 95.4 % | **95.4 %** |
| distinct labels ever registered | 13 | 13 |
| distinct labels in the final pass | 6 | 6 |
| corrections aimed at an in-flight segment | 4 (parked) | 4 (parked) |

The plan asked that exact alignment land inside the 92.1–92.3 % band the three fits bracketed. It
lands on 92.1 % with no rate, no offset and no residual. **The alignment caveat is retired**: every
attribution number from here on is reported against the VAD's own sample positions.

Note the contrast with the workshop. There, exact alignment agreed with the recorded figure while a
*fresh* fit of the same audio was 15 points out. Here the fit was never in trouble — which is exactly
what this document already said about the two recordings, now confirmed from the other direction.

### The Alexander/Andreas residue, at exact alignment

| label | → | A | B | C | D | E |
|---|---|---|---|---|---|---|
| Speaker 1 | A | 1609.4 | 27.7 | . | . | . |
| Speaker 10 | B | . | 572.5 | **127.4** | . | . |
| Speaker 7 | C | 3.9 | . | **8.0** | . | 2.2 |
| Speaker 8 | D | . | . | . | 9.1 | . |
| Speaker 13 | E | . | . | . | . | 81.6 |
| Speaker 9 | — | 5.5 | . | . | . | . |

C (Alexander) is still filed inside B's (Andreas's) label: **127.4 s there against 8.0 s in his own**,
where the fitted measurement said 142.1 s / 8.0 s. The residue is confirmed and slightly smaller than
previously reported. `Speaker 9` is a 5.5 s spurious cluster that wins no speaker at all.

### The bench: same segmentation, diverging pass sequence

474 of 537 segments above the gate — identical to the app — and **34 s wall clock for a 50-minute
recording**, against a 3-minute target.

| | app replay | bench | Task 4 tolerance |
|---|---|---|---|
| segments above the gate | 474 | 474 | ±2 % — pass |
| speech-mask agreement | 95.4 % | 95.4 % | — pass |
| labels ever minted | 13 | 14 | — |
| distinct labels, final state | 6 | 7 | same count — **off by one** |
| attribution by segment | 92.1 % | 89.8 % | ±2 pts — **2.3, marginal** |
| `Adaptive pass:` count | 110 | 101 | — |

The same shape as the workshop, and the same observed cause: the app's 30 s pass trigger measures wall
clock between identify calls, which STT throughput gates, while the bench measures stream time. Over
60 minutes of wall clock that costs 9 passes. The consequence is visible in the matrix — the bench
folds E's 81.6 s into `Speaker 1` where the app kept E on its own label, which is most of the 2.3
points.

**What this does and does not invalidate.** Task 4's tolerance is missed marginally on both
recordings, in opposite directions (+2.3 on the workshop, −2.3 here), so it is not a constant offset
that could simply be subtracted. Two things follow:

- **Task 7 is unaffected.** `Similarity` and `NearestCentroid` consume only (embedding, true speaker,
  duration). They never touch the bench's clustering, its labels or its pass sequence, and the
  segmentation is identical to the app's. The oracle bound rests on the part that reproduces exactly.
- **The bench is still the right harness for Task 8, but only for deltas.** It is deterministic — a
  warm run reproduces a cold one to the digit — so comparing threshold policies on it is sound. Its
  *absolute* attribution is not the app's, and a policy change worth less than about 2.5 points cannot
  be told from the app-versus-bench gap on a single recording. Report Task 8 as bench-relative, and
  confirm the winner on an app replay before believing a small margin.

### Task 7 on LSP, with the per-speaker breakdown

- **Discriminability:** intra 0.539 ± 0.183 against inter 0.222 ± 0.122, **d' 2.04** — better separated
  than the workshop's 1.82. Best single fixed threshold **0.345** at a 15.6 % pair-decision error.
- **Oracle nearest-centroid: 95.2 % by segment, 97.8 % by duration**, over 420 of 445 scoreable
  segments.
- **Oracle k pinned to the true talker count (5):** 89.4 % / 90.6 % — again *below* the live run's
  92.1 %.

| speaker | enrolled | scored | accuracy |
|---|---|---|---|
| A (Marco) | 30.3 s | 292 seg / 1605.5 s | 94.2 % |
| B (Andreas) | 32.2 s | 99 seg / 568.0 s | 99.0 % |
| C (Alexander) | 30.2 s | 23 seg / 108.2 s | **91.3 %** |
| E (Dirk) | 35.5 s | 6 seg / 48.2 s | 100.0 % |
| D (Martin) | 9.1 s | **0 seg** | untested — enrollment took every segment |

And the workshop's headline figure has to be withdrawn as a bound now that the same breakdown exists:

| speaker | enrolled | scored | accuracy |
|---|---|---|---|
| H | 31.0 s | 132 seg / 386.5 s | 99.2 % |
| I | 38.8 s | 21 seg / 121.7 s | 95.2 % |
| D | 31.0 s | **0 seg** | untested |
| E | 11.4 s | **0 seg** | untested |

**Two of four workshop speakers were never scored**, so 98.7 % is the accuracy on H and I and nothing
more. LSP's 95.2 % covers four of five speakers and 420 of 445 segments, and is the number to quote.

## The decision Task 7 exists to make

**LSP oracle nearest-centroid is 95.2 % by segment, which is the plan's `88–96 %` bucket: "Real
headroom in both — Tasks 8 + 9 first (cheaper), then Task 11."**

Reading it out properly:

- The live run is 92.1 %. Perfect enrollment on the *current* embedding model buys **3.1 points by
  segment / 4.6 by duration**. That headroom is real and it belongs to the matching policy. The two
  figures are not scored over quite the same set — the oracle scores 420 segments against the live
  run's 433, because 25 went to enrollment — so treat the delta as indicative rather than exact.
- The `< 88 %` bucket, which would have made an embedding-model swap a precondition and Tasks 8–9
  mostly wasted effort, is excluded on both recordings by a wide margin.
- By duration LSP reaches 97.8 %, and the workshop's (inflated) 98.7 % is in the `≥ 97 %` bucket too.
  Every reading of the fixture puts Tasks 8 and 9 first. Task 11 stays a follow-up, not a gate.
- Consent-phase enrollment is confirmed as the strongest product lever the assessment named: named
  enrollment is precisely what the oracle simulates.

**The concrete Task 8 finding, measured rather than argued.** The best fixed similarity threshold is
**0.345 on LSP** and **0.400 on the workshop**. Production computes
`_matchSimilarity = clamp(1 − cut, 0.40, 0.60)` and the baseline sits pinned at 0.60 for passes 1–10.
So the optimum is at the very floor of the clamp on one recording and *below the clamp's reachable
range* on the other, while the live policy spends its first ten passes at the opposite end of the
band. Task 8's option (b), a fixed threshold, now has a value to try and a reason to try it.

A caveat to carry into Task 8: pinning k does *worse* than the shipping pipeline on both recordings
(87.8 % vs 91.9 % on the workshop, 89.4 % vs 92.1 % here). The adaptive online-plus-repass design
already beats naive offline clustering with the answer key for k, so Task 9's "roster as a target for
k" should expect to find little, and the plan's instruction to write down a measured refusal looks
likely to be the outcome.

## Task-by-task state after this session

| # | Task | State |
|---|---|---|
| 0 | Tee both recordings | **done** — both WAVs verified for format, duration and segment count |
| 1 | Exact stream offsets | **done and verified on both recordings** — alignment caveat retired |
| 2 | Numbering read back | **observed**: `Speaker 1..5` (roster 10) and `Speaker 1..6` (roster 5) |
| 3 | Segment drops visible | **0 drops on both replays**; the old warning was unreachable |
| 4 | Offline bench | segmentation and mask exact; attribution ±2.3 pts and one label out, cause observed — usable for deltas, not absolutes |
| 5 | Exact-time scoring | done; `-Baseline` delta table still unbuilt, no consumer yet |
| 6 | Embedding cache | **fixed** — was saving zeros; cold and warm now identical |
| 7 | Oracle bound | **done, both recordings, with per-speaker breakdown** → `88–96 %` → Tasks 8 + 9 |

Gate after this session: `dotnet test` **4432 total, failed: 0**, 54 skipped, bench `Not Run`. Debug
and Release rebuilds at `0 Warning(s)`.

**Not done, and deliberately so:** nothing in Phase 4. Tasks 8 and 9 change which voice gets which
label, and Monday's meeting is the acceptance test for the end state measured above. They belong on
`feature/diarization-threshold` afterwards.

# 2026-08-22 — Task 8: the match threshold, measured against the cut it is derived from

Written on `feature/diarization-threshold`. Origin: `2026-08-22-threshold-tuning-brief.md`.

Everything below is **bench-relative**: 11 settings over both recordings, warm embedding cache,
deterministic. No number here has been confirmed on an app replay, and by the bench's own ±2.3-point
rule none of the accuracy margins is large enough to be established without one. See *What is not
established* at the end. The shipping default is therefore **unchanged** by this work.

## Two structural results, which reframe the brief

### 1. There is no threshold ↔ cut feedback loop

The brief describes matching and clustering as feeding "each other undamped". They do not.
`RunPassUnderLock` hands the clusterer three inputs — the eligible journal embeddings, the previous
pass's cluster count and the roster — and none of them depends on `_matchSimilarity`. The coupling is
one-directional: cut → threshold, with no back edge.

Measured rather than read: the per-pass **cut trace is identical to the digit across all 11 settings on
both recordings**, including settings that hold the threshold at 0.30 and at 0.60. The bench asserts
this and prints the verdict, so a future change that introduces a back edge will say so.

What follows is the real scope of the knob. A pass reassigns every eligible segment, so an eligible
segment's *final* label is the last pass's partition, and no threshold moves that partition. What the
threshold does own:

- the **provisional** label shown live, until the next pass corrects it (<= 5 segments or 30 s);
- segments between the 1.5 s diarization gate and the 2 s clustering floor, which never enter a
  dendrogram and therefore keep their provisional label for good - 43 of 474 on LSP, 47 of 206 on the
  workshop; and
- indirectly, which stable cluster id a rebuilt cluster inherits, because the pass matches new clusters
  to old ones by *segment overlap* and the provisional assignments are that overlap. This is why the
  live-versus-final counts move by more than the sub-floor band alone can explain: 13 segments against
  the ~19 sub-floor segments that carry any label at all on LSP.

Policies (c) damped and (d) separation were designed to stabilise a loop that does not exist.

### 2. The Alexander/Andreas residue is not a matching error

The brief names the residue as the thing this fixture cares about, and predicts that a too-high
threshold "mints and merges wrongly in exactly that way". It does not. C's seconds inside B's label are
**unmoved to the decimal by every setting tried**, from 0.20 to 0.60:

| setting (bench) | C inside B's label | C in his own label |
|---|---|---|
| derived (shipping) | 127.4 s | 4.3 s |
| fixed 0.20 … 0.60, damped, separation | **127.4 s** | 4.3 s |

That is the expected consequence of result 1: the residue is a partition produced by the dendrogram, so
it is out of reach of the match threshold by construction. Closing it needs the cut, the embedding model
or enrollment — not this knob.

## The sweep

`correct` is the comparator, not the percentage: the segment set is identical across settings, while the
percentage's denominator shrinks as unlabelled segments leave it. Raising the threshold "improves" the
percentage by refusing to label, which is why 0.45 reads higher than 0.30 while getting fewer segments
right.

**LSP** (roster 5, 5 true talkers, 474 segments above the gate):

| setting | final: correct | by seg | unlabelled | final labels | live: correct | live labels |
|---|---|---|---|---|---|---|
| **derived (a)** | 380 | 89.8 % | 24 | 7 | 384 | **13** |
| fixed 0.20 | 387 | 90.0 % | 17 | 6 | 396 | 9 |
| fixed 0.25 | **387** | 90.0 % | 17 | 6 | 396 | 9 |
| fixed 0.275 | 387 | 90.0 % | 17 | 7 | 396 | 9 |
| fixed 0.30 | 386 | 90.0 % | 18 | **6** | **397** | **9** |
| fixed 0.325 | 386 | 90.0 % | 18 | 6 | 397 | 10 |
| fixed 0.345 | 384 | 89.9 % | 20 | 6 | 395 | 10 |
| fixed 0.375 | 383 | 89.9 % | 21 | 7 | 388 | 11 |
| fixed 0.40 | 381 | 89.9 % | 23 | 7 | 386 | 12 |
| fixed 0.45 | 381 | 90.3 % | 25 | 8 | 384 | 12 |
| fixed 0.50 | 379 | 90.2 % | 27 | 8 | 379 | 17 |
| fixed 0.55 | 376 | 90.2 % | 30 | 8 | 374 | 25 |
| fixed 0.60 | 374 | 90.1 % | 32 | 8 | 369 | 28 |
| damped α 0.2 (c) | 382 | 90.1 % | 23 | 7 | 386 | 13 |
| separation (d) | 377 | 90.2 % | 29 | 8 | 372 | 27 |

**Workshop** (roster 10, 4 true talkers, 206 segments above the gate):

| setting | final: correct | by seg | unlabelled | final labels | live: correct | live labels |
|---|---|---|---|---|---|---|
| **derived (a)** | 163 | 94.2 % | 8 | 4 | 162 | 9 |
| fixed 0.20 | 165 | 94.3 % | 5 | 4 | 166 | 6 |
| fixed 0.25 | **165** | 94.3 % | 5 | 4 | **166** | **5** |
| fixed 0.275 | 164 | 94.3 % | 6 | 4 | 165 | 5 |
| fixed 0.30 | 164 | 94.3 % | 6 | 4 | 164 | 6 |
| fixed 0.345 | 164 | 94.3 % | 6 | 4 | 164 | 6 |
| fixed 0.40 | 163 | 94.2 % | 7 | 4 | 162 | 8 |
| fixed 0.45 | 160 | 94.7 % | 11 | 4 | 158 | 10 |
| fixed 0.50 | 160 | 94.7 % | 11 | 4 | 158 | 13 |
| fixed 0.55 | 153 | 94.4 % | 19 | 4 | 149 | 14 |
| fixed 0.60 | 150 | 94.3 % | 23 | **5** | 143 | 20 |
| damped α 0.2 (c) | 162 | 94.2 % | 8 | 4 | 161 | 9 |
| separation (d) | 152 | 95.0 % | 21 | 4 | 148 | 17 |

The **veto holds**: the workshop's final label count stays at 4 for every setting except fixed 0.60,
which is the one direction this work is not proposing.

`live` scores the instant provisional label instead of the corrected one, via the new
`-Provisional` switch on `Measure-SpeakerAttribution.ps1`. Without it the sweep is nearly invisible —
which is result 1 stated as a measurement rather than as an argument.

## The winner, and the size of the win

**Fixed, in the 0.20–0.345 plateau.** Both recordings agree, in both directions:

- (a) derived is beaten on LSP by every fixed setting at or below 0.40, and matched on the workshop.
- (c) damped is (a) with a slower transient: +2 segments on LSP, −1 on the workshop. It removes the
  swing without moving the accuracy, because the swing was never the cost.
- (d) separation lands the threshold at 0.48–0.70 — the statistic says *higher*, and higher is
  measurably worse: −3 segments on LSP and −11 on the workshop, with 27 and 17 labels shown live. It
  is the clearest loser of the four and it was the most principled-looking.

Inside the plateau the spread is 1–3 segments and the LSP label structure is byte-identical, so the
value cannot be chosen by measurement on these two recordings. **0.30 is the pick because it is
furthest from both edges of the plateau**, not because it measured best.

Against (a), at 0.30:

| | LSP | workshop |
|---|---|---|
| final-state correct | 380 → 386 (+6) | 163 → 164 (+1) |
| final-state by segment | 89.8 % → 90.0 % | 94.2 % → 94.3 % |
| final labels (true talkers) | 7 → **6** (5) | 4 → 4 (4) |
| live correct | 384 → 397 (+13) | 162 → 164 (+2) |
| live distinct labels | 13 → **9** | 9 → 6 |
| segments corrected by a pass | 71 → 66 | 7 → 7 |

The accuracy movement is small. The **live label count** is the result worth having: a third fewer
distinct speaker labels appear during the meeting, which is the symptom the 2026-08-21 live test
reported ("11 labels, up to Speaker 17"), and it is not an artefact of a shrinking denominator.

## Why lower wins, when the oracle's best pairwise threshold is 0.345/0.400

Two different quantities. `DiarizationOracle.Similarity` fits a threshold for *segment-to-segment*
decisions; `_matchSimilarity` is compared against a *running centroid*, whose mean over n vectors raises
same-speaker similarity. A centroid threshold should therefore sit **above** the pairwise optimum, and
the measurement says it belongs below it. So the brief's "the optimum sits below the clamp's reachable
range" was comparing two scales; the sweep is what actually settles it, and it happens to point the same
direction for a different reason.

The mechanism is the asymmetric cost of the two errors. A wrong provisional label is corrected by the
next pass within five segments. A **spuriously minted** label is durable: it is a new "Speaker N" the
user watches appear, it survives until a pass orphans it, and it pushes the live count toward the roster
ceiling where `_labelByCluster.Count >= roster + slack` starts forcing matches. Minting is the expensive
error, so the optimum is biased toward matching.

## The shipping policy is not the square wave the brief describes

On the bench's pass sequence, (a) on LSP sits at the 0.60 rail for passes 1–2, is on the 0.40 floor by
pass 4, and spends **87 of 101 passes there**, with six brief excursions to ~0.50:

```
0.600 0.600 0.407 0.400 0.498 0.503 0.400 0.421 0.416 0.400 0.400 0.400 …
```

min 0.400, max 0.600, mean 0.411, sd 0.035. The workshop is the same shape: min 0.400, max 0.544, mean
0.412, sd 0.030, 34 of 41 passes on the floor. (The brief's "pinned at 0.60 for passes 1–10" is the
*app* trace; the bench visits 101 passes where the app visits 110, and the early phase is shorter here.)

So the shipping policy is, in practice, "0.40 after a two-pass warmup" — which is why fixed 0.40 scores
within one segment of it on LSP. `MatchSimilarityMin = 0.40` is not a guard rail, it is the operating
point, and it is above the whole plateau. Variance is not the defect; the floor's *value* is.

Damping (c) confirms this from the other side: it cuts the excursions (51 passes on the floor instead of
87, sd 0.028) and buys two segments. A policy that holds still is not better if it holds still in the
wrong place.

## What is not established

- **Every accuracy margin here is under the bench's own confirmation threshold.** The bench's absolute
  attribution differs from the app's by about 2.3 points in either direction; the win at 0.30 is 0.2
  points by segment (final) and 1.1 points (live). Neither is established without an app replay of LSP
  (`Invoke-MeetingReplay.ps1`, ~65 min).
- The **label counts** are the exception worth arguing about: the known bench↔app divergence is one
  label, and this is four (13 → 9 live on LSP). That margin does clear its noise floor, but on one
  measure, on one harness.
- Because of the above the **shipping default is unchanged**: `FixedMatchSimilarity` is null, the
  derivation and its clamp still run, and the winner is reachable only from the bench
  (`PIA_BENCH_MATCH=0.30`). Flipping it waits on a replay - which the next section ran, on the workshop.
- Both recordings are mostly-one-talker (A holds 1605 s of 2729 s on LSP; H holds 386 s of the 607 s scored on the
  workshop). A balanced four-way meeting could pick a different point in the plateau, and nothing here
  measures that.

## The app replay refuses the confirmation — workshop, 2026-08-22

Run through the shipping harness (`Invoke-MeetingReplay.ps1`, workshop recording, roster 10, 19 min
wall clock) against a build whose default was temporarily flipped to 0.30 — the app constructs the
service with default options, so there is no other way to measure the setting end to end. Both runs
scored with the same scorer; `-Provisional` supplies the live column.

| | shipping | fixed 0.30 | the bench predicted |
|---|---|---|---|
| final-state correct | 158 (91.9 %) | 159 (91.9 %) | +1 — reproduced |
| final-state labels (4 true talkers) | 5 | 5 | no change — reproduced |
| live correct | 159 | 161 | +2 — reproduced |
| **live distinct labels** | **9** | **9** | **9 → 6 — did not reproduce** |
| labels ever registered | 10 | 9 | — |
| unlabelled, final | 37 | 35 | direction only |

**The accuracy deltas reproduce exactly; the label-churn win does not.** +1 correct segment on the final
label and +2 on the live one are the bench's workshop figures to the segment — and they are 1–2 segments
out of 173, which is noise. The claim that mattered, nine live labels down to six, does not survive the
harness change at all.

The reason is the divergence this document already records, not a new one. The count of *distinct
provisional labels* depends on how minting interleaves with passes, and the app fires passes on wall
clock between identify calls while the bench fires them on stream time. A final label count is robust to
that — it is a partition, and the recorded divergence is ±1 label. A live count is not. The assertion
earlier in this section, that the live label delta "does clear its noise floor" because the known
bench↔app divergence is one label, took a tolerance measured on the final count and applied it to a
metric that does not share it. The replay is what caught that, which is the whole reason the rule exists.

**Consequence.** The flip is reverted: `FixedMatchSimilarity` is null again and the shipping build is
untouched. Task 8's winner is **identified but unconfirmed**, and that is the state to carry forward:

- every margin that reproduces is 1–2 segments;
- the one margin large enough to act on does not reproduce on this recording;
- LSP is untested end to end, and it is where the bench's delta was largest (13 live labels → 9). It is
  the trustworthy half of the fixture, at ~65 minutes. If the label-churn win is real anywhere it is
  there — and if it fails there too, a fixed threshold is not worth shipping and Task 8 closes as a
  measured refusal, the same shape the brief pre-authorised for Task 9.

One measurement bug found and fixed on the way: `-Provisional` was written to refuse `-LogPath`, on the
assumption that an app log carries no pre-correction label. It does — the scorer's log path has always
set `Label` at identify time and overwritten only `Final` when a correction lands. That guard would have
made this comparison impossible.

## What changed in the code

- `AdaptiveSpeakerOptions` (internal, `src/Pia.Wpf/Services/LiveTranscription/`): the knobs the sweep
  needed — `FixedMatchSimilarity`, `InitialMatchSimilarity`, `MatchSimilarityMin/Max`,
  `MinClusterSegmentSeconds`, `WarmupSegments`, `PassSegmentStride`. Every default is the shipping
  constant, so an omitted options object is the shipping build; `derived` reproduces the pre-change
  bench numbers exactly (89.8 % LSP, 94.2 % workshop).
- The pass log line carries `match=` so an app replay can be scored the same way.
- `Measure-SpeakerAttribution.ps1 -Provisional` scores the pre-correction label. Bench input only — an
  app log does not carry it.
- The bench takes `PIA_BENCH_MATCH` (comma-separated thresholds, one run each, defaulting to one
  shipping run), writes `<name>.<setting>.segments.jsonl` per setting, and asserts the cut traces.
- **Deleted with the experiment**, their results recorded above so nobody re-runs them blind: the
  `MatchThresholdPolicy` enum, the EMA damping (c) and the intra/inter separation statistic (d).

## Task 8 acceptance

| ask | state |
|---|---|
| knobs injectable | done — `AdaptiveSpeakerOptions`, defaults = shipping |
| four policies, both recordings, warm cache | done — 11 settings covering (a) (b)×9 (c) (d) |
| winner beats (a) on LSP, does not lose on the workshop | met by fixed 0.20–0.345; 0.30 picked |
| `_matchSimilarity` variance across the meeting | reported per setting: min/max/mean/sd + rail counts |
| Alexander/Andreas confusion matrix | reported — **unmoved by every setting** |
| delete the losers | done — (c) and (d) and the enum are gone |
| margins confirmed against the app | workshop replay run: the +1/+2 segment deltas reproduced, the label-churn win did **not**. LSP untested |

Task 9 is untouched. Result 1 above bears on it directly: `ChooseCut` is the only thing that can move a
final-state partition, so it is now the *only* remaining lever in this subsystem short of the embedding
model or enrollment — which raises the value of measuring it, without changing the expectation that
"largest gap, capped by the roster" survives the test.

# 2026-08-22 (afternoon) — the first live meeting, scored against an answer key

Everything above was measured by replaying a recording. This section is a **live** meeting: Pia joined
a real Teams call, four humans talked for 4:40, and the cloud recording of that same call supplies the
answer key. It is the first time the shipping pipeline has been scored on audio it heard live.

The recording is `testmeeting`. Speakers are tile letters here and stay that way — unlike the two work
recordings above, this was a private call, so no names appear outside the gitignored sidecar.

Three things make it worth keeping as a third fixture:

- **Three of the four talkers are women, and one pair is genuinely close.** Neither existing fixture
  has a same-gender pair that the pipeline fails on outright.
- **19.4 % of speaking time is overlapped**, against 7.6 % on the workshop and 6.5 % on LSP. A relaxed
  conversation with cross-talk, not a meeting with a speaker queue.
- It is the same call captured two ways — live through the browser tap, and by replaying Teams' cloud
  mix — with one answer key covering both.

It is also short. 4:40 is one twelfth of LSP, so read the confusion matrix, not the percentage.

## The reference, and a cheaper way to get one

| | value |
|---|---|
| duration | 280.4 s, 1122 frames at 4 fps |
| tiles | 5 — four humans plus the attendee itself, which never lights |
| talk | A 49.8 s · B 72.8 s · C 52.0 s · D 78.0 s |
| no highlight | 50.0 s |
| overlap | 40.0 s |
| unusable layout | 23.8 s, one range: 2.25–26.0 s |
| intervals | 174 |

The playbook priced the layout at "the hour" of hand-measuring. It does not have to be. The pill is a
strongly-coloured rectangle, so one pass over the video that heat-maps pill-coloured pixels and takes
connected components hands back the label rects directly — for four of the five tiles here, including
both name overlays burned over video, which is the case a grid-relative guess gets wrong. The fifth is
the attendee's own tile, which never lights and was placed from the rail's pitch.

Two numbers say the rects are right rather than merely plausible. The pill reads a blue lead of 67/255
where the pale avatar circle behind it reads 25, so the classifier is not working near its threshold.
And **23.8 s of the 280.4 lands in `invalidRanges`, as a single range, 2.25–26.0 s.** That range is not
noise: it ends when Pia is admitted and Teams reflows the grid from four tiles to five, which the log
timestamps at 15:30:38.1. The validity test found the join without being told about it.

## Alignment: a live run needs an origin, and this one has three witnesses

A replay starts the file at its beginning, so stream time *is* recording time. A live run joins a
meeting already being recorded, so stream 0 sits somewhere inside the reference — and scoring exact
positions against the wrong origin is a confident wrong answer. `Measure-SpeakerAttribution.ps1` now
fits that origin from the speech masks at rate 1.0 when the log is a live run, and prints it. The
origin stays 0 for a replay by construction, so no number above this line moved.

**Fitted origin 27.15 s**, runner-up peak 30.75 s scoring 2650 against 3415 — a 29 % margin, not the
near-tie a dense back-and-forth can produce. Two independent witnesses agree:

| how | value |
|---|---|
| speech-mask fit | 27.15 s |
| capture-armed timestamp minus the recording's start | 27.23 s |
| the grid reflow that admission itself causes | 26.0 s |

The origin also fixes the coverage window, which matters here: the reference covers stream −27.1 to
253.3 s, and **the meeting outlived the recording by about 20 s — five segments, 16.1 s.** Those are
now excluded in their own bucket rather than counted as speech the reference denies. A recording can be
stopped before the meeting is.

## The result: three speakers perfect, the fourth invisible

```
scored segments : 44  (156.5 s)
correct         : 37  = 84.1 % by segment, 81.1 % by duration
overlapped      : 9  (33.0 s)   excluded, ambiguous reference
outside video   : 5  (16.1 s)   excluded, the recording had stopped
no label at all : 21
```

84.1 % against the workshop's 91.9 % and LSP's 92.1 %. But the headline is the wrong way to read this
run, because the errors are not spread at all:

```
label          A       B       C       D
Speaker 1      .      56.0     4.3     .     -> B
Speaker 2     30.7     .      25.3     .     -> A
Speaker 3      .       .       .      40.3   -> D
```

**C never gets a label of her own.** She is filed under A's label for 25.3 s and under B's for 4.3 s.
The arithmetic closes exactly: 44 scored segments, 7 errors, and **all 7 are C's — every one of her
cleanly-attributable segments is wrong, and every other speaker's is right.** Excluding C, the run is
37 for 37.

So this is not a pipeline that is 84 % accurate. It is a pipeline that resolved three of four talkers
without a single mistake and did not notice the fourth existed.

## Where it went wrong: the first thing C ever said

The per-segment timeline puts the failure on one decision. C's first utterance is segment 5, at
recording 63.1 s, and it was filed under A's label. **Every later segment of hers went the same way** —
six to A, one to B, none to her own. Twelve repasses never split them.

At that moment the run had minted three labels against a ceiling of five
(`_expectedSpeakers + ExpectedSpeakerSlack` = 4 + 1), so a fourth was free for the taking. **C was not
blocked by the roster ceiling.** She cleared the match bar against A's centroid — one segment old at
that point, with the bar at `InitialMatchSimilarity` 0.50, the strictest value the policy ever holds.

**But that is the provisional decision, and it is not what the score reads.** A pass reassigns every
eligible segment, so a final label is the last pass's partition. Comparing the two for C's seven
scored segments:

| seg | recording | provisional | final | |
|---|---|---|---|---|
| 5 | 63.1 s | Speaker 2 | Speaker 2 | via Speaker 5 and back |
| 21 | 131.8 s | Speaker 5 | Speaker 2 | reassigned |
| 27 | 158.2 s | Speaker 2 | Speaker 2 | |
| 37 | 196.7 s | Speaker 2 | Speaker 2 | |
| 38 | 200.3 s | Speaker 2 | Speaker 2 | |
| 46 | 228.4 s | Speaker 2 | Speaker 1 | reassigned |
| 49 | 243.8 s | Speaker 2 | Speaker 2 | |

Only eight reassignments happened in the whole run, and the interesting two are on segment 5: a pass
moved it out of A's label into a fresh `Speaker 5`, **taking segment 9 — which is genuinely A's — with
it**, and a later pass dissolved that cluster back into A's label. So the clusterer did try to split
this pair, split it *wrongly*, and gave up.

That places the defect in the dendrogram's partition, not in the online match bar: the threshold owns
the provisional label, and here provisional and final agree on five of seven anyway. Consistent with
the earlier finding on LSP's residue — only `ChooseCut`, the embedding model, or enrollment can move a
label that the partition itself is placing wrongly.

## The phantom labels are born on cross-talk

Seven labels were minted for four talkers. Three of the four real births are clean single-speaker
segments — B at 32.5 s, A at 38.6 s, D at 58.0 s. `Speaker 4` is different: it was minted on segment
10, which the reference says is **three people talking at once**. `Speaker 7` was minted on the last
segment of the run, past the end of the video, where there is no reference at all — so this is one
confirmed case and one unattributable, not yet a pattern.

A mixture of three voices lands far from every centroid, which is exactly the condition the mint branch
reads as "a voice we have not heard". So on this recording label inflation is driven by overlap, not by
the threshold — a different defect from the one Task 8 addresses, with a different fix (refuse to
*mint* on a segment that looks like a mixture; matching one is harmless). It also explains why the
count is worse here: 19.4 % of speaking time is overlapped against 7.6 % and 6.5 %.

Neither phantom carries a meaningful number of scored seconds, so they cost accuracy nothing. They cost
the transcript five speaker names for four people, which is what a user actually sees.

## Cloud mix against live browser tap: the same failure, to the tenth of a second

The playbook lists the gap between a cloud-mixed recording and what Pia actually hears as unmeasured.
The same meeting is now available both ways, against one answer key and from the same build.

| | live (in-browser tap) | replay (cloud mix) |
|---|---|---|
| segments emitted / transcribed | 80 / 79 | 84 / 84 |
| below the diarization floor | 17 | 20 |
| clusters per pass | 3,4,5,5,4,5,4,4,4,4,4,5 | 4,5,5,5,5,5,5,5,5,5,5,5 |
| labels ever minted | 7 | 5 |
| labels in the transcript | 5 | 5 |
| scored | 44 (156.5 s) | 44 (160.7 s) |
| correct | **84.1 % / 81.1 %** | **81.8 % / 80.3 %** |
| A in A's label | 30.7 s | 30.7 s |
| C in A's label | 25.3 s | 25.3 s |
| C in B's label | 4.3 s | 4.3 s |

**The three cells that carry the finding are identical.** B and D differ by a few seconds and the
headline by 2.3 points, which is the pass-timing sensitivity already documented for this fixture.

Read this narrowly. It says a cloud-mixed recording is a faithful stand-in for the **in-browser tap**,
which is what Pia used here — itself a tap on Teams' own mixed page audio. It says nothing about device
loopback, with its second D/A-A/D pass and Teams' AGC. That case is still unmeasured.

## The bench: the embedding model can separate this pair, and the policy cannot

The pooled statistics look ordinary — `d' 1.88` against 1.82 and 2.04, best fixed threshold 0.375
against 0.400 and 0.345. A pooled d' can hide one bad pair, so the oracle now prints a per-pair
matrix:

```
              A       B       C       D
  A       0.452   0.203   0.350   0.237
  B       0.203   0.644   0.264   0.192
  C       0.350   0.264   0.587   0.204
  D       0.237   0.192   0.204   0.417
```

A/C sit at **0.350** while every other pair sits between 0.192 and 0.264. Against the tighter of the
two self-similarities (A's 0.452) that leaves a margin of **0.103** — less than half of either work
recording:

| recording | closest pair | cross | self | margin | is it the pair that fails? |
|---|---|---|---|---|---|
| workshop | E/H | 0.336 | 0.502 | 0.166 | E loses its label, H keeps it |
| LSP | B/C | 0.292 | 0.507 | 0.215 | yes — C inside B is this fixture's known residue |
| testmeeting | A/C | 0.350 | 0.452 | **0.103** | yes — C inside A |

**But 0.103 is a margin, not a collision.** The oracle settles it — carefully, because on a recording
this short the oracle's own enrollment budget moves the answer more than the pipeline does. The budget
is now `PIA_BENCH_ENROLL` (default 30, so every number above reproduces), and sweeping it:

| `PIA_BENCH_ENROLL` | pooled | scored | C | A |
|---|---|---|---|---|
| 8 s | **89.5 %** | 38 seg | 80.0 % (4/5) | 85.7 % (6/7) |
| 10 s | 97.1 % | 35 seg | 100 % (4/4) | 83.3 % (5/6) |
| 12 s | 97.1 % | 35 seg | 100 % (4/4) | 83.3 % (5/6) |
| 15 s | 96.8 % | 31 seg | 100 % (3/3) | 80.0 % (4/5) |
| 20 s | 100 % | 23 seg | 100 % (1/1) | 100 % (3/3) |
| 30 s | 100 % | 14 seg | *untested* | 100 % (1/1) |

That is playbook trap 4 one level up: **the pooled figure rises as the scored set shrinks, and the 30 s
default reports a meaningless 100 % having consumed C entirely.** Quote the **8 s row — 89.5 % over 38
segments** — because it is the one with a real sample behind it, and say which budget produced it.

The comparison to the other two recordings has to be made at the same budget, and it turns out only
this recording is sensitive:

| recording | oracle @ 8 s | scored | @ 12 s | @ 30 s | live | headroom @ 8 s |
|---|---|---|---|---|---|---|
| workshop | 100.0 % | 170 | 99.4 % | 98.7 % | 91.9 % | 8.1 pts¹ |
| LSP | 94.9 % | 434 | 94.7 % | 95.2 % | 92.1 % | 2.8 pts |
| testmeeting | 89.5 % | 38 | 97.1 % | 100 % | 84.1 % | **5.4 pts** |

¹ E is `untested` at every budget — only 11.4 s of speech — so the workshop figure is not a bound on it.

LSP moves 0.5 points across the whole range because 30 s barely dents 434 segments; `testmeeting` moves
10.5. **So the headroom here is about 5.4 points, not the 13 the 12 s row suggests**, and it sits between
the two work recordings rather than dwarfing them.

What survives that correction is the part that matters: **at 8 s enrollment C scores 80 %, where the live
run scores 0 %.** Correct centroids do not make her perfect — the 0.103 margin shows up as a real
residual, and the absorbing speaker A drops to 85.7 % — but they give her a label at all, which no
setting of the matching policy did. The ceiling is not the embedding model.

The `ORACLE clusterer (k = 4)` figure is 79.2 %, below the live run's 84.1 % — the third recording in a
row where pinning k does worse than the shipping pipeline.

### The pair *is* separable, by the production clusterer, on the production embedding

The enrollment oracle replaces the clustering entirely, so on its own it leaves open whether the
clusterer could ever find this split. That is answerable directly: hand the **production**
`SpeakerClusterer` nothing but the closest pair's segments and pin k=2.

| recording | closest pair | isolated, k=2 | segments | the same pair in the live run |
|---|---|---|---|---|
| LSP | B/C | **96.3 %** / 98.0 % | 134 | 127.4 s of C inside B's label |
| workshop | E/H | **90.3 %** / 92.0 % | 144 | E has no label of its own |
| testmeeting | A/C | **82.4 %** / 87.6 % | 17 | C has no label of her own, 0 % correct |

**On all three recordings the pair the pipeline cannot separate is separable — by the same clusterer,
on the same embeddings, with no model change and no enrollment.** The two well-sampled cases are
unambiguous at 134 and 144 segments; `testmeeting`'s 17 segments make its 82.4 % indicative rather than
precise, but the distance from 0 % is not in doubt.

So the information is present and the linkage can extract it. What the shipping pipeline never does is
**pose the question** — the pair is one sub-problem inside a global partition that is optimising
something else, and the cut that would separate them is not the cut that best splits the whole set.
That is the most specific statement the fixture has produced about where the remaining accuracy lives,
and it points at a fix shape rather than at a model swap: a **split-candidate pass** that takes an
existing cluster, tries a 2-way split, and keeps it when the halves are more self-consistent than the
whole. Untested — but it is exactly the operation the table above performs by hand.

## What this changes

- **Task 8 cannot fix this recording, and that is a third measured refusal, not a new mandate.** The
  match threshold owns the provisional label; C's provisional and final labels agree on five of seven
  segments, and the two that differ move her between two wrong labels. Both of the brief's candidate
  values (0.345 from LSP, 0.400 from the workshop) are below the 0.50 that already absorbed her. This
  recording still belongs in the Task 8 A/B — as the case that must not *regress*, since tuning on two
  recordings that both want more merging would ship exactly that — but the fix for C lives in
  `ChooseCut`, the embedding, or enrollment.
- **Consent-phase enrollment is now measured rather than argued, and it is worth about 5 points.** The
  oracle's budget is not a hypothetical on this recording — the meeting opened with each participant
  saying their own name for roughly 12 s. It is the only lever measured so far that gives C a label at
  all, rather than trading her against someone else, and that is a bigger deal than the 5 points.
- **A new defect, separate from Task 8:** minting a label on an overlapped segment. `Speaker 4` was born
  that way here — on a segment the reference says is three people at once. `Speaker 7` was minted past
  the end of the video, so it is unattributable either way: one confirmed case, not a pattern yet. Cheap
  to test, and it is the likeliest explanation for five names on four people.
- **A split-candidate pass is now the best-evidenced fix, and it is new.** Isolating the closest pair and
  pinning k=2 recovers it on all three recordings — 96.3 %, 90.3 %, 82.4 % — with no model change and no
  enrollment. The shipping pipeline never poses that sub-problem. Trying a 2-way split of an existing
  cluster and keeping it when the halves are more self-consistent than the whole is a smaller change
  than consent enrollment, needs no UI and no privacy story, and is testable entirely on the bench.
  **Do this before Task 11, and probably before the rest of Task 8.**
- **Task 11 stays a follow-up, but this is the first recording that argues for it.** A margin of 0.103
  is thin, and it shows: even with correct centroids the absorbed speaker only reaches 80 % and the
  absorbing one drops to 85.7 %. A better embedding would widen that margin. It is still not what costs
  the 5 points — the oracle recovers those on the model we already ship — but "the embedding is fine"
  is a weaker statement here than on either work recording.

## Two items the playbook listed as unmeasured are now measured

- **The roster size `TeamsMeetingSession` actually reports.** Every number before this used an env-var
  roster. The live run reports `expected=4` against a People panel reading "In this meeting (5)" — the
  real path works and correctly excludes Pia itself.
- **Dropped hops in the browser capture.** `BrowserAudioCaptureService` writes into a `DropOldest`
  channel upstream of the VAD's sample counter, so one drop would shift every later position against
  page time, permanently and silently. Over the whole meeting: `droppedFrames=0`. That is also what
  makes a rate-1.0 origin fit legitimate rather than assumed.

## Caveats on this section

- 4:40 and 44 scored segments. The confusion matrix is unambiguous; the percentages carry a wide
  interval.
- **21 of the 79 transcribed segments carry no speaker label at all** (26.6 %, against 14 % on LSP and
  15.8 % on the workshop) — short utterances below `MinClusterSegmentSeconds`, which a casual
  conversation produces far more of. They still produce transcript text. What the UI attributes them to
  was not examined here.
- The transcript ran on **whisper-tiny**, not the medium model the earlier sections used. Diarization
  is upstream of the text, so this touches no number here; it does mean the transcript itself is
  near-useless, and one segment produced an empty result.
