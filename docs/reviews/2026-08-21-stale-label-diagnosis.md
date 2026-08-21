# Diagnosis: why `Speaker 16` outlived its cluster

Date: 2026-08-21. Produced by Task 2 of
`docs/superpowers/plans/2026-08-21-speaker-attribution-fixes.md`. Evidence:
`%LOCALAPPDATA%\Pia\Logs\pia-2026-08-21.log` plus the code paths named below.

## Answer

A re-cluster pass runs **inside** the identify call for the segment that triggered it, and that
segment's utterance does not reach the ViewModel until its transcription finishes — seconds later.
`TranscriptOverlayViewModel.ApplyReassignments` only rewrites journal entries that already exist, so
a correction aimed at that in-flight segment is **discarded**. The utterance then arrives carrying the
pre-pass provisional label, which is now stale forever.

It is not the sub-floor carry-over. That was the plan's cheapest hypothesis and the log rules it out
(see "What it is not" below).

## The mechanism, step by step

1. `LiveTranscriptionEngineService.TranscribeSegmentAsync` calls
   `_speakerId.IdentifyOrRegisterSegment(...)` **before** `_engine.TranscribeAsync(...)`, and only
   writes the utterance to the sink after transcription returns.
2. `AdaptiveSpeakerIdentificationService.ProcessEmbedding` captures its return value
   (`result = new SpeakerSegmentResult(segId, _labelByCluster[cluster])`) **before** deciding whether
   a pass is due, then runs `RunPassUnderLock()` in the same call. The returned label is therefore
   pre-pass by construction — two existing tests document this ("its label is the stale pre-pass
   provisional one by design").
3. `SpeakersReassigned` fires at the end of that same call. The pass may well have moved the
   triggering segment: it is the newest member of the dendrogram and the greedy overlap match is free
   to hand its cluster a different stable id.
4. `ApplyReassignments` walks `_journal` and matches by `SegmentId`. The triggering segment has no
   journal entry yet, so its correction matches nothing and is dropped — silently, since the method's
   `any` flag only tracks entries it did change.
5. Transcription finishes. `AddUtterance` journals the utterance with `utterance.SpeakerLabel`, the
   value captured in step 2. Nothing revisits it: the next pass only emits a reassignment if the
   label changes *again*.

The exposure is exactly one segment per pass, and always the newest one. The attendee path runs a
single engine whose segment loop is serial, so at most one segment is ever identified-but-not-journaled
at a time — the one the pass is running inside. This is not a rare interleaving; it is the shape of
every pass.

## Primary-source confirmation

`Speaker 16` is present in the pass-18 label set and absent from pass 19 onward:

```
14:49:47.677  Adaptive pass labels: [Speaker 1, Speaker 16, Speaker 2, Speaker 17, …]   (pass 18, 5 clusters)
14:50:08.464  Adaptive pass labels: [Speaker 1, Speaker 2, Speaker 17, …]               (pass 19, 4 clusters)
```

The log around pass 19 shows the race directly:

```
14:50:08.3503  Engine start: Them 158720 samples          ← identify begins (9.92 s of audio)
14:50:08.4638  Adaptive pass: 78/89 segments → 4 clusters cut=0.63 expected=4 changed=5
14:50:08.4641  Adaptive pass labels: [… Speaker 16 is gone …]
14:50:18.1984  Engine done: Them 9848ms text='Also da haben wir auch schon einige Kunden jetzt. …'
14:50:18.1992  Consumer received utterance from Them (len=170)
```

113 ms after that segment's identify started, the pass emitted 5 corrections and dropped
`Speaker 16`'s cluster. The segment's utterance reached the ViewModel **9.73 s later**. Any of those
5 corrections addressed to it was thrown away, and the label it carried into the journal was the one
minted before the pass.

Two conditions have to coincide for the *invariant* to break rather than merely the attribution:
the correction must be lost, **and** the same pass must drop the old label rather than recycle it.
Pass 19 did both — it went 5 → 4 clusters, so no new cluster was left unmatched, `nextOrphan` never
advanced, and the unclaimed stable id's label was dropped outright instead of being handed to a new
cluster. That is why `Speaker 16` vanished from the service while a bubble still showed it.

The specific bubble could not be re-checked: the saved transcript from that meeting is not on this
machine. The assessment records that it contains `Speaker 16`, and the mechanism above is confirmed
from the log and the code independently of which bubble it was.

## What it is not

**Not the sub-floor carry-over.** `RunPassUnderLock` computes `gatedClusters` from every journaled
segment under `MinClusterSegmentSeconds` and writes each one straight into `newLabelByCluster`. So on
HEAD a cluster referenced by an in-journal sub-floor segment lands in either `takenPrev` or
`gatedClusters`, and both branches keep its label alive. A sub-floor segment's label therefore
*cannot* be the one that disappears — the journal held 99 segments against a 2000 cap, so nothing was
evicted either. The plan's Task 2 acceptance ("fails on HEAD and passes after Task 3") does not hold:
Task 3 does not touch this path, and Task 5's hard gate depends on the ViewModel fix below, not on
Task 3.

**Not `ApplyReassignments`' null-`SegmentId` skip.** A journal entry can only carry a label if it also
carries a segment id — the engine assigns both from the same result, or neither.

**Not journal eviction.** `JournalCap` is 1000 against 114 utterances.

## Fixes this diagnosis requires

1. **Hold corrections for segments not yet seen** (`TranscriptOverlayViewModel`). A reassignment whose
   segment id is absent from the journal is parked and applied when that utterance arrives, instead of
   being dropped. This is the fix for the observed defect.
2. **No dangling cluster references after a pass** (`AdaptiveSpeakerIdentificationService`). Task 3
   deletes the `gatedClusters` carry-over, which *introduces* a second route to the same violation: a
   cluster minted by an eligible segment can lose every eligible member to a later pass while a
   sub-floor segment still points at it, and once the carry-over is gone that cluster is droppable. The
   pass must therefore leave every `_clusterBySegment` value a live key of `_labelByCluster`, clearing
   the label of anything it strands. "No label" is the honest target, matching Task 3's rule for a
   sub-floor segment that matches nothing.

Together these make the invariant — no bubble may carry a label absent from the service's live label
set — hold by construction rather than by luck.
