# Diagnosis: why speaker attribution gets worse the longer a meeting runs

**Status.** Two mechanisms established, one on real audio and one reproduced; the reporter's first
symptom is explained by the former but was *not* reproduced here. Unconfirmed on the reporter's own
recordings. No code changed.
**Owner.** Marco Altmann.
**Written.** 2026-09-04.
**Origin.** Field report, 2026-09-04: several recorded meetings, 9 participants. Two symptoms — a
participant who first speaks at ~30 min is persistently labelled as someone who spoke 10 minutes
earlier, and participants who were labelled correctly start acquiring new labels at ~40 min. Builds on
[2026-08-21-speaker-attribution-measurements.md](2026-08-21-speaker-attribution-measurements.md) and
[2026-08-22-threshold-tuning-brief.md](2026-08-22-threshold-tuning-brief.md).

## Short answer

Every pass throws the whole meeting's partition away and re-derives it from scratch: one global cut
across one dendrogram over every eligible embedding. Nothing in it is anchored to the identities the
previous pass established, and `RunPassUnderLock` reassigns **every** eligible segment, re-attaching
stable cluster ids afterwards by greedy segment-overlap vote.

That gives the two symptoms different causes with a shared root:

- **A voice permanently inside another's label** is one global cut failing to separate a close pair.
  Established on real audio, threshold-independent.
- **Settled speakers acquiring new labels late** is the wholesale re-cut firing when the speaker
  population changes. Reproduced here, and the trigger is that newcomers arrive late.

Neither is a match-threshold problem in the way it looks, but the threshold is not irrelevant either —
see *What is not the cause* below.

## Mechanism 1: one global cut cannot separate 9 voices when two are close

`SpeakerClusterer.ChooseCut` picks a single distance for the entire dendrogram — the largest gap in the
merge-distance sequence whose upper edge falls inside `[CutMin 0.30, CutMax 0.70]`. For a 9-speaker
partition to come out right, all 8 between-speaker merges must sit above that one number and every
within-speaker merge below it.

If two participants are closer to each other than the widest speaker's internal spread, no single cut
satisfies both, and the cut that is right for the other seven merges that pair — at every subsequent
pass, permanently.

**This is measured on real audio, not inferred.** On the LSP recording, 127.4 s of Alexander sits
inside Andreas's label, and it is *unmoved to the decimal* by every match threshold from 0.20 to 0.60.
The earlier work already named it: "out of the threshold's reach by construction." It is the closest
thing in the repo to the reporter's first symptom, and it is a partition failure.

What this mechanism does **not** explain on its own is why the victim would be specifically the person
who started talking at minute 30. See *What was not reproduced*.

## Mechanism 2: a population change forces a wholesale re-cut — this is the late relabelling

The pass reassigns every eligible segment, and a cluster keeps its previous label only if it wins the
overlap vote for that stable id; each previous id can be claimed once. So the moment the cut moves —
which is exactly when a new voice forces the cluster count up — the partition shifts as a unit.
Clusters that split lose the vote for their old id, and the loser takes a recycled orphan label
(someone else's old number) or a freshly minted `Speaker N`.

Reproduced (details below): a newcomer arriving at minute 30 drove the cluster count from 6 straight to
the roster cap of 10, where it stayed, and in the same 10-minute bucket two *already-stable* incumbents
fell to 77% and 66% purity — over-split into fragment labels, not merged with anyone. Both recovered to
100% by the next bucket, so in the harness the event is **transient**. Whether the reporter's version
is transient or sticks is exactly what their log would settle.

The reporter's two symptoms are therefore plausibly cause and effect rather than two independent bugs:
a newcomer that mechanism 1 mishandles is also the event that fires mechanism 2.

## Mechanism 3: the roster ceiling binds at exactly 9 participants

`cap = min(MaxClusters 12, expectedSpeakers + ExpectedSpeakerSlack 1)`. With a 9-person roster that is
**10**. Nine real voices plus any split fragments do not always fit in 10 slots, so the
over-segmentation guard in `Cluster` keeps merging "cheapest remaining first" until they do — and the
cheapest remaining merge is not necessarily two halves of one voice. It can be two different people,
and nothing later undoes it.

A 9-person meeting is close to the worst case the current defaults allow: it is the largest roster for
which the cap is still below `MaxClusters`, so the ceiling is tightest at the headcount that needs the
most slack. In the reproduction the count reached 10 and stayed there for the rest of the meeting.

## Mechanism 4: label numbers only ever go up

In every condition tried, including the benign one, the service minted **12 to 14 labels for 9
speakers**. Real audio agrees: the shipping build minted 13 labels for 5 talkers on the 50-minute LSP
recording. Orphaned auto-labels are recycled, but the counter that names them never rewinds, so a long
meeting drifts toward high numbers regardless of whether the partition is good.

**This is service-side only and the UI already hides it.** `SpeakerDisplayNumbering.Resolve` rewrites
any `Speaker N` to a dense 1..N in first-appearance order, so nobody sees `Speaker 14`. Corrected
2026-09-04 — an earlier revision of this doc overstated it as user-visible.

### 4b. But the renumbering is re-derived on every pass, and that *is* user-visible

`RebuildBubblesFromJournal` calls `_displayNumbering.Reset()` before replaying the journal, and
`ApplyReassignments` triggers a rebuild whenever a pass changed anything. So display numbers are
re-derived from scratch, densely, in journal order, several times a minute.

The consequence is worse than the inflation it replaced: when a pass drops one service label, **every
display number after it shifts down**, so a single merge renumbers several *unrelated* speakers on
screen. A participant can change number without a single one of their own segments being reassigned.

The comment at `TranscriptOverlayViewModel.cs:372` states the tradeoff deliberately — "a stale label
dropped by a pass must not keep its number, or the renumbering would not close the gaps it exists to
close." Gap-closing was chosen over identity stability. For the field report, that is exactly the wrong
way round: a user tracking who said what needs a number that does not move, and a gap in the sequence
costs them nothing.

This is the cheapest sound fix available for symptom 2. Make the numbering sticky — assign on first
sight of a service label, never re-derive, never reuse, reset only on `Clear`/`Reset`. It touches one
small class plus the VM, cannot affect the clustering core, and therefore needs no bench measurement.

## What was not reproduced

Stated plainly, because it constrains what can be claimed:

- **The late arrival being absorbed into an old speaker's label.** In all four conditions the newcomer
  at minute 30 received its **own 100%-pure label**. The harness does not reproduce the reporter's
  first symptom. Mechanism 1 remains the best explanation for it because it is measured on real audio,
  but the "dominant cluster's radius swallows a quiet newcomer" story specifically is a hypothesis this
  experiment did **not** support.
- **Monotonic purity decay.** Purity stayed at 97–100% in every bucket of every condition. Nothing here
  shows the partition itself decaying as the meeting lengthens. What the runs show is churn *events*
  that land late because newcomers arrive late, plus the label inflation above. That may well be what
  "the longer it runs, the worse it gets" feels like from the outside, but it is not the same claim and
  should not be dressed up as one.
- **The designed close pair degrading with length.** Speakers 3 and 4 were built to sit close, and in
  one condition they did share a label at minutes 20–30 — but they *separated* by minute 40 as more
  data arrived. On that pair, more data helped.

  The reporter independently confirms this direction: two people merged into one label, then correctly
  split about five minutes later with the earlier bubbles repaired. So the retroactive relabel path
  works, and the global cut does often move to a *better* place as evidence accumulates. The same
  wholesale re-cut is therefore both the repair mechanism and the churn mechanism — which is why the
  fix cannot simply be to stop re-cutting.

## Related defect found while investigating: a pass can delete a user-typed name

Reproduced 2026-09-04. `Rename` gives a cluster exactly two protections — priority when the
stable-id overlap vote is a *tie*, and immunity from having its label recycled onto another voice. It
does **not** pin which segments belong to it.

So when a pass merges a renamed cluster into a larger one, the renamed cluster wins no overlap vote
(11 votes to 5 in the probe), is skipped by orphan recycling *because* it is renamed, and is therefore
never written into `newLabelByCluster` — after which `_labelByCluster.Clear()` at `:352` drops it.
`KnownLabels` came back `["Speaker 1"]`: the typed name was gone, and its segments were reassigned onto
the winning label, so the name disappears from the transcript too.

The trigger is the same merge as mechanism 1, which means the name is most likely to be destroyed in
exactly the situation that made the user type it. `Rename_SurvivesReclusterPasses`
(`AdaptiveSpeakerIdentificationServiceTests.cs:103`) covers only the case where the renamed cluster
keeps its overlap, so the losing case is untested.

**There is no narrow fix.** Carrying the name back into `_labelByCluster` would be cosmetic only:
`KnownLabels` is `internal` and read by nothing but tests, and the bubbles come from the journal's
per-segment labels, which the pass has already rewritten onto the winning label. A phantom entry with
no members and no centroid can never be instant-matched again either. Preserving the name means
preserving the segment→label mapping, which means keeping those segments out of the merge — the pin in
[2026-09-03-manual-bubble-reattribution.md](2026-09-03-manual-bubble-reattribution.md), which notes the
same write-back gap at `:348`. So this defect is not separable work; it is one more argument for that
plan, and the test for the losing case belongs with the fix rather than ahead of it.

## What is not the cause

- **The match threshold, for the residue.** `_matchSimilarity` (clamped `[0.40, 0.60]`) is measured
  irrelevant to the Alexander/Andreas residue across 0.20–0.60. A pass reassigns every segment ≥ 2 s,
  so the threshold owns only the provisional label shown for the next ≤ 5 segments or 30 s, and the
  sub-2 s segments that never enter a dendrogram at all.
- **But not for churn.** The threshold *does* move label churn: a fixed 0.30 took live labels from
  **13 to 9** on the LSP bench, because provisional assignments are the overlap evidence the stable-id
  vote runs on. Do not carry forward a blanket "the threshold is irrelevant" — it is irrelevant to
  symptom 1 and relevant to symptom 2.
- **Journal eviction.** `DefaultMaxJournaledSegments` is 2000 and an evicted segment's label freezes,
  but the LSP fixture ran 111 passes across 50 minutes — a meeting of this length is nowhere near the
  cap. Not the 40-minute cliff.
- **Hysteresis pinning the count via gap densification.** The first hypothesis: that merge distances
  densify until `HysteresisGapDelta` 0.03 makes every candidate competitive and the cluster count
  freezes. **Refuted** by the pipeline runs, where the count moved freely (5 → 7 → 8 → 9 in the
  uniform condition and 5 → 6 → 10 in the unequal one).
- **The pre/post-force-merge count mismatch.** `_lastPassClusterCount` is the post-merge count while
  `ChooseCut`'s candidates are counted pre-merge. Real, but self-limiting: once previous = cap the
  ceiling branch stops firing. A one-time coarsening, not a runaway.

## How it was reproduced

The shipping `AdaptiveSpeakerIdentificationService` and `SpeakerClusterer` were driven unchanged over a
60-minute synthetic meeting: 192-d embeddings, 9 voices (one deliberately close pair), speakers 1–7
from the start, 8 from minute 30, 9 from minute 45, one 2.5 s utterance every 6 s, roster 9. Only the
embedding extractor and the clock were replaced. The harness is not in the tree — it asserted `Fail` to
print its table, which the test gate forbids — but it is one self-contained file and the conditions
below are enough to rebuild it.

Talk-time imbalance and crosstalk were modelled because the fixture recordings show both: LSP's five
talkers hold 1752 / 722 / 204 / 116 / 12 s (a 146:1 ratio) with 170.8 s of overlap in 2997 s (5.7%).

| condition | labels minted / 9 | purity, min 10 → 60 | reassignments per 10 min | count trace |
|---|---|---|---|---|
| equal turns | 12 | 100 → 99% | 9, 0, 0, 0, 6, 12 | 4 7 7 8 8 10 |
| unequal talk time | 13 | 100 → 99% | 6, 0, 0, 3, **21, 33** | 5 6 6 6 10 10 |
| unequal + 6% crosstalk | 14 | 98 → 98% | 22, 8, 1, 2, 12, 15 | 4 5 7 7 8 10 |
| unequal + crosstalk, easy geometry | 14 | 99 → 99% | 11, 15, 3, **32**, 15, 9 | 4 7 6 10 10 10 |

Reading it:

- **Churn is back-loaded** in the unequal condition — 6, 0, 0, 3, 21, 33. Quiet for the first half
  hour, then climbing as the late speakers arrive. This is the reproduction of mechanism 2.
- **The count slams into the cap and stays.** 5 6 6 6 **10 10**: pinned at 6 through minute 35, then
  straight to the ceiling when speaker 8 appears. Mechanism 3.
- **Label inflation is universal** — 12 to 14 labels for 9 speakers in all four conditions, including
  the one with uniform turns and no crosstalk. Mechanism 4.
- **Purity is flat.** No condition degrades. The 98% in the crosstalk runs is roughly the crosstalk
  segments themselves, whose "true" speaker is arbitrary — they are ~50/50 mixtures of two voices, so
  scoring them against one of the two is not meaningful.

The caveat that belongs on all of it: the geometry is synthetic and the drift and crosstalk rates were
chosen, not measured. The runs establish that the shipping logic produces late churn spikes, cap
pinning and label inflation under conditions the fixture recordings show to be realistic. They are not
evidence about the reporter's specific meetings, and they do not reproduce the reporter's first
symptom at all.

## Confirming it on the real recordings

The pass log line carries everything needed:

```
Adaptive pass: {Eligible}/{Segments} segments → {Clusters} clusters cut={Cut} expected={Expected}
               changed={Changed} match={Match}
```

It is `LogDebug`, so it survives into release IL, but the minimum level is `Debug` only when
`Bootstrapper.IsDevMode` is true — and that is `#if DEBUG`. A release install therefore logs none of
this. Two ways to get the trace:

1. **Replay a recording through the bench** — no live meeting, and the embedding cache means only the
   first run pays for ONNX:

   ```powershell
   $env:PIA_BENCH_WAV    = '<recording>.wav'   # 16 kHz mono
   $env:PIA_BENCH_ROSTER = '9'
   dotnet test --filter-class "*DiarizationBenchTests*"
   ```

   With a reference built by `scripts/Get-SpeakerReference.ps1`, `Measure-SpeakerAttribution.ps1` turns
   it into the same accuracy and label-count numbers this repo already quotes.

2. **Run a Debug build in the next meeting** and read `%LOCALAPPDATA%\Pia\Logs\pia-*.log`.

What each column would settle:

- `expected=` — **check this first.** If it is 0 rather than 9 the roster poll is failing, the cap is
  12 not 10, and mechanism 3 is off the table. It has been wrong before: the measurements doc had to
  correct a whole baseline because `SetExpectedSpeakers` was never called.
- `Clusters` flat while `Eligible` grows and a new voice is talking → the newcomer is being absorbed,
  which would finally reproduce symptom 1.
- `Clusters` jumping to the cap with a `changed=` spike in the same pass → mechanism 2, the wholesale
  re-cut. Whether the following passes settle or keep churning is what tells transient from permanent.
- `changed=` totals per 10 minutes → the churn curve, directly comparable to the table above.
- `cut=` wandering pass to pass while `Clusters` holds → the partition is being re-decided on thin
  evidence.

## Directions, not yet a plan

Roughly cheapest first. Nothing here is costed, and none of it should start before the confirmation
above, because that decides which mechanism actually bit this recording.

- **Make the re-cut asymmetric — not frozen.** The obvious fix, freezing every segment older than some
  horizon, is *wrong*, and the field report says so: the reporter also watched two people merged into
  one label get correctly split five minutes later, with the old bubbles repaired. A horizon freeze
  would have prevented that repair. What is needed is a re-cut that may still improve a cluster whose
  identity is weakly supported, while refusing to renumber one that has been stable and pure for 40
  minutes. The pass currently has no notion of either, which is the actual defect: one indiscriminate
  mechanism doing both jobs.
- **Admit a new voice without re-cutting everyone.** Detect "this cluster is new" and mint, rather than
  letting the global cut move to make room. Same root, narrower fix.
- **Per-cluster cut instead of one global one.** `SpeakerSplitOptions` exists for exactly this and ships
  `Off`; mechanism 1 is the argument for finishing it. The measured candidate is `Margin 0.15,
  MinSegments 8, MinHalf 3, AbsorbBelow 4`.
- **Reconsider `ExpectedSpeakerSlack = 1` for large rosters.** Slack proportional to roster size, or
  simply not capping below `MaxClusters`, would loosen mechanism 3 where it is tightest.
- **Don't let the label counter outrun the speakers.** Mechanism 4 is cosmetic next to the rest and
  probably the cheapest thing here that a user would actually notice.
- **Keep crosstalk out of the dendrogram.** Overlapped segments bridge clusters under average linkage,
  and an embedding poorly explained by any cluster is a cheap signal for them. Lower confidence than
  the rest — the harness did not isolate a crosstalk-specific failure beyond the arbitrary-truth noise.

The enrollment lever from the earlier assessment still dominates all of these on raw accuracy — the
oracle with perfect enrollment measured 95–98% against 92% live. These are the fixes for *stability*,
which is what the field report is actually about.
