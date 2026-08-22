# Brief: try every lever on speaker attribution, and keep only what measures

**Status.** Ready to execute. Nothing here has been attempted; every number below is measured.
**Owner.** Marco Altmann.
**Written.** 2026-08-22.
**Origin.** The live-meeting section at the end of
[2026-08-21-speaker-attribution-measurements.md](2026-08-21-speaker-attribution-measurements.md),
which found a failure mode the two work recordings could not show, and an isolation test which found
that the failure is reachable rather than fundamental. Supersedes the scope of
[2026-08-22-threshold-tuning-brief.md](2026-08-22-threshold-tuning-brief.md), which remains valid as
the detail for lever 4.

## The one paragraph that matters

On a live four-person meeting the pipeline scored 84.1 %, and **every single error was one speaker** —
she never got a label at all, and the other three were 37 for 37. Then: handing the *production*
clusterer only that pair's segments and pinning k=2 separates them at **82.4 %**, and does the same on
the other two recordings' closest pairs at **96.3 %** and **90.3 %**. No model change, no enrollment.
So the signal is present, the shipping linkage can extract it, and the pipeline simply never poses the
sub-problem. **This brief is about closing that gap, and then trying everything else.**

## Read these first

- `docs/speaker_attribution/speaker-attribution-fixture-playbook.md` — how to run and score anything
  here. **Read the traps section before quoting any number.** It is the difference between a result and
  a confident wrong answer, and each trap listed there has already produced one.
- The measurements doc's last section, for the live-meeting findings and the isolation table.

## Step 0 — unify the tooling, before any experiment

Two branches diverged and both halves are needed:

| branch | has |
|---|---|
| `feature/speaker-attribution` (current) | the three-recording fixture, live-meeting scoring with a fitted stream origin, per-pair separation matrix, the isolation test, `PIA_BENCH_ENROLL` |
| `feature/diarization-threshold` (head `9da5e139`) | `AdaptiveSpeakerOptions.cs` (the injectable knobs), `PIA_BENCH_MATCH`, `Measure-SpeakerAttribution.ps1 -Provisional` |

`Measure-SpeakerAttribution.ps1` and `DiarizationBenchTests.cs` conflict. Merge them onto a fresh
branch off `feature/speaker-attribution` and verify the merge by reproducing the baselines below to the
digit before changing any behaviour. A merge that moves a baseline is a broken merge, not a finding.

## The baselines every lever is measured against

App runs, scored by `Measure-SpeakerAttribution.ps1`. Reproduce these first.

| | workshop | LSP | testmeeting (live) | testmeeting (replay) |
|---|---|---|---|---|
| attribution, segment / duration | 91.9 % / 91.5 % | 92.1 % / 93.2 % | **84.1 % / 81.1 %** | 81.8 % / 80.3 % |
| scored segments | 172 | 433 | 44 | 44 |
| labels: final / true talkers | 5 / 4 | 6 / 5 | 5 / 4 | 5 / 4 |
| unlabelled, of transcribed | 37 / 234 | 75 / 537 | 21 / 79 | 27 / 84 |

Bench, warm cache, seconds per run:

| | workshop | LSP | testmeeting |
|---|---|---|---|
| `d'` | 1.82 | 2.04 | 1.88 |
| best fixed threshold | 0.400 | 0.345 | 0.375 |
| oracle enrollment @ 8 s | 100.0 %¹ | 94.9 % | 89.5 % |
| oracle clusterer, k pinned | 87.8 % | 89.4 % | 79.2 % |
| closest pair | E/H | B/C | A/C |
| its margin | 0.166 | 0.215 | **0.103** |
| **isolated to that pair, k=2** | **90.3 %** (144 seg) | **96.3 %** (134 seg) | **82.4 %** (17 seg) |

¹ One workshop speaker is `untested` at every budget. Compare oracle figures **only at equal budgets** —
`testmeeting` swings 10.5 points across 8–30 s where LSP moves 0.5.

## Acceptance — what "optimised" has to mean

A lever ships only if all four hold. State each explicitly per lever; a lever that improves one
recording and is silent about the others has not been measured.

1. **No regression on any of the three.** `testmeeting` exists because tuning on the two work
   recordings alone drives the threshold toward more merging, which is exactly what breaks it.
2. **Label count does not inflate.** Report final label count against true talkers for all three. A
   split-happy winner that turns 5 labels into 9 is a loss even if attribution rises — five names for
   four people is what the user actually sees.
3. **Margins under ~2.5 points are not established on the bench.** Confirm them with a real app replay
   (`Invoke-MeetingReplay.ps1`; ~6 min for testmeeting, ~20 workshop, ~65 LSP).
4. **Live label counts do not inherit the bench's ±1 tolerance.** This already caused one false win:
   the bench said 9 → 6 live labels on the workshop and the app replay showed 9 → 9. If a lever's case
   rests on label count, it must be an app replay.

Also report, per lever, the **variance of any adapted quantity across the meeting**. A policy that
scores the same while holding still is the better one.

## The levers, in the order to try them

### 1. Split-candidate pass — best-evidenced, no UI, bench-only

The isolation table is this lever's justification: the split is reachable on all three recordings by
the code already shipping. Add a pass that takes an existing cluster, attempts a 2-way split, and keeps
it when the halves are more self-consistent than the whole (compare within-half mean cosine against the
whole cluster's, with a margin; the observed intra/inter statistics are in the bench report).

Design questions to settle **by measurement**, not by argument:
- What triggers a candidacy — every cluster every pass, or only clusters above a size/variance
  threshold? Cost is not the issue at these sizes; over-splitting is.
- What accepts the split? A margin on self-consistency, a silhouette-style score, or the roster having
  a free slot. Try more than one.
- Interaction with the roster ceiling: a split needs a slot, and the ceiling is `expected + 1`.
- **The named risk: over-splitting the speakers that currently work.** All three recordings have
  speakers at ~100 %. Acceptance criterion 2 is the guard, and it is the one to watch here.

### 2. Young-centroid damping — small, and aimed at a decision we can point to

The absorption happened on the pair's first contact, against a centroid **one segment old**, at
`InitialMatchSimilarity` 0.50. Damp the match bar while a cluster is thin, or refuse to match a cluster
that thin at all. This moves the **provisional** label, so measure with `-Provisional` — final labels
come from the last pass's partition and will barely move. Judge it on provisional accuracy and on
whether it prevents the initial mis-absorption, not on the headline.

### 3. Overlap-aware minting — fixes the label count, not the accuracy

`Speaker 4` on `testmeeting` was minted on a segment the reference says is three people at once. A
mixture lands far from every centroid, which the mint branch reads as a new voice. Refuse to *mint* on
a segment that looks like a mixture; matching one is harmless. One confirmed case (the second phantom
was minted past the end of the video and is unattributable), so treat the mechanism as a hypothesis and
check how many mints across all three recordings land on reference-overlapped segments before building
anything. `testmeeting` is the recording to test on: 19.4 % of its speaking time is overlapped, against
7.6 % and 6.5 %.

### 4. The match-threshold policies (a)–(d)

Full detail in the threshold-tuning brief. Carry its amendment: options (b) 0.345 and 0.400 are both
*below* the 0.50 that already failed, so run them as a **control**, not as the favourite, and expect (d)
— derived from the observed separation distribution — to be the only one that can hold LSP's low bar and
a higher bar on a close pair. Delete the losing policies rather than leaving a mode enum behind.

### 5. `ChooseCut` itself

This is where the failing partition actually lives, and it is the least explored thing in the stack. It
currently sees the sorted merge distances, the previous cluster count, and the roster — it has no view
of per-pair separation and no notion of an unstable split. Note the measured evidence of instability:
on `testmeeting` a pass moved the pair's first segment into a fresh cluster, **took a genuinely-correct
segment of the other speaker with it**, and a later pass dissolved it back.

Task 9's "roster as a target for k" should still expect to refuse — pinning k does worse than the
shipping pipeline on all three recordings (87.8 / 89.4 / 79.2 against 91.9 / 92.1 / 84.1). Write the
refusal down if it refuses again.

### 6. The unlabelled quarter

`MinClusterSegmentSeconds` = 2 s leaves **21 of 79 transcribed segments with no speaker label at all**
on `testmeeting` (26.6 %, against 14 % and 15.8 % on the work recordings — a casual conversation has
far more short turns). They still produce transcript text. Two things to establish, in this order:

1. **What the UI does with them today.** Unexamined. If they silently inherit the previous bubble's
   speaker, that is a wrong attribution the metric cannot see, and it is worth more than any percentage
   point in this brief. Check the ViewModel before changing the floor.
2. Whether short segments can be attributed by merging them into an adjacent same-cluster segment
   rather than by lowering the floor. Lowering it is what caused the earlier sub-floor minting bug; do
   not simply delete the floor.

### 7. Consent-phase enrollment — biggest measured win, feature-sized

The oracle *is* this lever simulated: ~5.4 points at a like-for-like budget, and the absorbed speaker
goes from 0 % to 80 %. It is the only lever measured to give her a label at all. There is **no
enrollment or voiceprint code in either `Services/MeetingAttendee/` or `Services/LiveTranscription/`** —
verified, so this is new work, with UI and a privacy story for storing voice profiles, plus a design
answer for people who join late or never speak their name. Do levers 1–3 first; they may take most of
the same ground for a fraction of the cost.

### 8. Teams' own active-speaker signal — the highest ceiling of anything here

`2026-08-22-browser-active-speaker-gate.md` plans a DEBUG probe and has not been started. It is worth
re-reading in the light of this session, because **the answer key for all three recordings is extracted
from exactly this signal** — 174 intervals from a 280 s meeting with 23.8 s unusable, and the unusable
range turned out to be Teams reflowing the grid as Pia joined. The signal is demonstrably clean enough
to score a pipeline against; if Pia can read it live, attribution stops being a clustering problem.

Unchanged from that plan: it needs one real meeting to answer its three gate questions, and the DOM→audio
offset must be measured with a sign and a spread. One new datum in its favour — its issue 3 was whether
the clock could be trusted, and the live meeting logged `droppedFrames=0` throughout.

### 9. Model and segmentation swaps — last, and only if 1–8 leave a gap

- **Embedding (Task 11).** `testmeeting`'s 0.103 margin is the first real argument for it: even with
  correct centroids the absorbed speaker only reaches 80 %. But the oracle recovers the points on the
  model already shipping, so this buys headroom, not the fix.
- **Segmentation.** `SileroVadDetector` is an RMS energy gate despite the name. `org.k2fsa.sherpa.onnx`
  1.12.40 is already referenced and ships `OfflineSpeakerDiarization`,
  `OfflineSpeakerSegmentationPyannoteModelConfig`, `FastClusteringConfig.NumClusters` and real
  `SileroVadModelConfig`/`TenVadModelConfig`. With 19.4 % overlap on `testmeeting`, genuine overlap
  handling is a real lever — and this is a rewrite, so it needs 1–8 to have run out first.
- **STT.** Orthogonal to everything above: `IdentifyOrRegisterSegment` runs *before* `TranscribeAsync`
  on the same samples, so no model change moves an attribution number. Worth doing anyway — whisper-tiny
  made `testmeeting`'s transcript useless, and an **empty** transcription suppresses the utterance
  entirely, which silently shrinks the scored set. Do not bundle it into a diarization measurement.

## Constraints

- **Branch.** Fresh branch off `feature/speaker-attribution` after the step-0 merge. Do not put
  behaviour changes on `feature/speaker-attribution` itself.
- **Gate.** `dotnet test`, no filter, `failed: 0`. The bench stays `Explicit`.
- **Warnings.** `dotnet build -t:Rebuild -v:n`, Debug **and** Release, `0 Warning(s)`. Incremental
  builds do not re-emit warnings from skipped projects.
- **Privacy.** `testmeeting` is a private call: tile letters only in anything committed, real names only
  in the gitignored `*.names.local.json`. Recordings, WAVs and embedding caches stay under `artifacts/`.
- **Comment discipline.** One short line, only where the why is non-obvious, and no task or spec
  citations in the source.
- Update the measurements doc with a before/after table per lever, at equal enrollment budgets, saying
  which margins were confirmed against an app replay and which are bench-relative.

## If a live meeting happens during this work

Set `PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP` to an absolute path first. Without it a live run cannot be
benched or cross-correlated afterwards, which is why the 2026-08-22 meeting could only be scored and not
replayed through the bench on its own audio. It also remains the only way to test device loopback, which
is still unmeasured — the cloud mix is known to stand in for the *in-browser* tap and nothing more.
