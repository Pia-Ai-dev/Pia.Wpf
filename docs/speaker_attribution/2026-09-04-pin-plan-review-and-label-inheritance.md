# Review: the bubble-pin plan, and what to do about label inheritance

**Status.** Investigation only. Nothing implemented; implementation deferred to a later session by
owner decision, 2026-09-04.
**Owner.** Marco Altmann.
**Written.** 2026-09-04.
**Origin.** Two of three follow-ups the owner selected after
[2026-09-04-long-meeting-degradation.md](2026-09-04-long-meeting-degradation.md): review
[2026-09-03-manual-bubble-reattribution.md](2026-09-03-manual-bubble-reattribution.md) against that
investigation's findings, and work out what to do about unlabelled utterances leaking into labelled
bubbles. The third — the name-drop defect — collapsed into the pin plan and is recorded there.

## Part 1 — the pin plan holds up

Read in full against the degradation findings. The load-bearing decisions are right, and three of them
are right for reasons the degradation work independently confirmed:

- **Rejecting `_renamedClusters` as the mechanism.** Confirmed empirically: a renamed cluster that
  loses the overlap vote has its label deleted outright (probe in the degradation doc). Recycling
  immunity is not membership protection.
- **Exclusion from `journalIndex` rather than "cluster then override."** The plan's reason — a pinned
  segment left in the dendrogram votes under its pinned cluster and two pins can swap a stable id — is
  the same greedy-overlap mechanic that produces the churn in mechanism 2 of the degradation doc.
- **Per-segment, not per-label, granularity.** This is what makes the feature compatible with the
  field observation that a merged label sometimes splits correctly a few minutes later. A per-label
  freeze would have pinned the wrong merge and destroyed that repair.
- **Ordering `Pass_IsSkipped_WhenTooFewUnpinnedEligibleSegmentsRemain` first**, because the
  `EligibleCountUnderLock` miss wipes the whole transcript rather than sticking a pin. Correct call.
- **Verification step 4** — replay with no correction applied, to show the change is inert when
  unused. This is the discipline the threshold work lacked and it should stay.

Four findings follow. None of them invalidates the plan; two are gaps in its coverage and two are
interactions it does not yet account for.

### F1 — the pencil rename is left unprotected, and §5 may read as though it is covered

§5 is titled a companion fix to `Rename`, and it fixes a real defect (returning after the first match,
so a later rename half-splits a duplicated label). But it does **not** make a rename sticky. The
write-back added in §2 — "carry every pin target the pass did not match back into `newLabelByCluster`"
— is scoped to *pin targets*. A cluster that is renamed but holds no pinned segment is still dropped by
`_labelByCluster.Clear()`, exactly as reproduced on 2026-09-04.

So after this plan lands, the only correction most users will ever find (the pencil) still loses their
typed name whenever a pass merges that cluster away — and that merge is the same event that made the
name wrong enough to type. The two new context-menu commands are protected; the old one is not.

Two ways to close it, and the choice is the owner's:

- **Pin on rename** — at rename time, pin the segments currently carrying that label. Nearly free once
  the machinery exists, and it is what "respects a manually named bubble" actually means. The hazard is
  the mirror of the plan's own reasoning: renaming a cluster that holds two people pins both under one
  name. `RedetectSegments` is the escape hatch, so the state is recoverable, but the first thing a user
  does after mislabelling is often to rename again — and a pinned segment will not follow.
- **Extend the write-back to `_renamedClusters`** — cheaper, and **not worth doing alone.** With no
  pinned members the carried cluster has no centroid, so it can never be instant-matched again, and
  its segments have already been reassigned onto the winning label. `KnownLabels` is `internal` and
  read only by tests. The user would see nothing change.

Recommendation: pin on rename, decided explicitly rather than inherited, with a test that a rename
survives a scripted merge — the `Rename_SurvivesReclusterPasses` sibling that does not exist today.

### F2 — the picker's display↔identity mapping can go stale while the dialog is open

§4 has the user pick by `DisplayLabel` and the service receive `SpeakerLabel`, and names the mix-up as
"the bug most likely to ship". There is a second, subtler version of it that the proposed test
(`AssignSpeaker_ForwardsTheSegmentIds_AndTheIdentityLabel`) would not catch.

`ShowSelectionDialogAsync` is awaited. While it is open, segments keep arriving and passes keep
running; every pass that changes anything triggers `RebuildBubblesFromJournal`, which calls
`_displayNumbering.Reset()` and re-derives every display number densely in journal order. If a service
label dies during that await, **every display number after it shifts down.** The options list, and any
`DisplayLabel → SpeakerLabel` map built when the dialog opened, now describe a different assignment
than the screen behind it.

The user picks "Speaker 4" meaning the person they can see; the map resolves it to whoever was fourth
when the dialog opened. This is a correctness bug in the feature, not a cosmetic one.

Mitigations, cheapest first:

- **Make display numbering sticky** (the fix proposed in the degradation doc, §4b). It removes the
  failure mode at the source rather than defending against it, and it is independently justified. This
  is the argument for sequencing that small change *before* the picker rather than after.
- Failing that, resolve the choice to `SpeakerLabel` at pick time by index into a list captured with
  its identity labels, and re-validate against `Bubbles` before calling the service — refusing with the
  existing snackbar if the label no longer exists.

### F3 — §5 makes the duplicate-label state more reachable, which `AssignSegments` resolves arbitrarily

§2 resolves a target label to a cluster with "lowest cluster id wins", acknowledging via §5 that two
clusters can legitimately carry one label. §5's fix then renames *every* cluster carrying `oldLabel`,
which makes duplicates more common, not less. Assigning to a duplicated label therefore lands on
whichever cluster happens to have the lower id — invisible to the user, and it decides which centroid
their correction is measured against later.

Low severity and probably acceptable, but it deserves a sentence in the plan saying so, because "lowest
id wins" currently reads as an arbitrary tie-break rather than a decision about a state §5 encourages.

### F4 — inheritance means the pin will not deliver what a user expects from it

The plan notes that unlabelled short utterances inherit whichever run now precedes them and files it
as correct behaviour that belongs in a test. That is right as far as it goes, but understates the
consequence for *this* feature: a user can pin a bubble's label and that bubble will still accrete
other people's short utterances, because inheritance happens at grouping time and no pin touches it.

"I named this bubble and it is still wrong" is the most likely way the feature gets reported as broken.
Part 2 is what to do about it.

## Part 2 — label inheritance, the leak

### What the rule is

`TranscriptGrouping.ShouldReuse` (`src/Pia.Wpf/Services/LiveTranscription/TranscriptGrouping.cs`)
merges an utterance into the previous bubble when the speaker matches, the bubble started less than
`BubbleWindowSeconds` (25) ago, and **either** the labels are equal **or** the incoming label is blank
while the run's is not. The last clause is the leak, and it is deliberate: it keeps a "ja", a "genau"
or laughter from shattering a run.

Two properties matter and neither is obvious from the call site:

- **There is no adjacency check.** The window is measured from `last.StartTimestamp`, so a blank
  utterance 24 seconds after the bubble *began* still inherits, no matter how long the silence before
  it was. The bubble already tracks `EndTimestamp`, so the information needed to do better is present
  and unused.
- **Inheritance is unconditional on headcount.** The rule's premise is that a short interjection
  belongs to whoever currently holds the floor. In a two-party call that is usually true. In a
  nine-person meeting the opposite is closer to true — short interjections are largely what the *other
  eight* produce while one person talks. The premise inverts as the roster grows, and the rule does
  not know the roster size.

### Which segments are blank

Four populations, and they are not equally fixable:

1. **Below the 1.5 s diarization gate** — `LiveTranscriptionEngineService` never calls the diarizer, so
   there is no `SegmentId` at all. Structurally unlabelled, permanently, and out of reach of any
   correction affordance. This is the population that leaks most.
2. **Between 1.5 s and 2 s** (`MinClusterSegmentSeconds`) that matched no centroid. Journaled with a
   real embedding, held out of clustering, so no pass ever revisits them. 43 of 474 segments sat in
   this band on the LSP fixture and 47 of 206 on the workshop — but only the unmatched subset is blank.
3. **Eligible segments that matched nothing**, mostly during warm-up before any centroid exists.
4. **Segments a pass explicitly nulls** — the dangling-cluster cleanup emits
   `SpeakerReassignment(segId, null)` for a segment whose cluster the rebuild dropped.

Only (2)–(4) are addressable by better attribution. (1) can only be addressed by the grouping rule,
which is why the grouping rule is the lever here.

### Options

- **(a) Leave it.** Defensible today, and it is the right default for a two-party call. Weakest for
  exactly the meeting shape in the field report.
- **(b) Decouple the inheritance window from the bubble window** — *recommended, and then measured
  inert: see [2026-09-04-inheritance-adjacency-measurement.md](2026-09-04-inheritance-adjacency-measurement.md).
  Across the three references no real inheritance sits more than 7.25 s after the run, and every
  threshold low enough to bite removes correct inheritances before wrong ones — adjacency does not
  separate the two.* Keep the 25 s bubble
  window on `StartTimestamp`, but allow inheritance only when the new utterance is close to
  `last.EndTimestamp` (a few seconds). A "genau" a second after someone stops speaking is theirs; one
  after eighteen seconds of silence is not. One added condition in one pure function, no new state, and
  it narrows the leak without fragmenting anything the rule exists to protect.
- **(c) Gate inheritance on headcount.** Skip inheritance when more than two speakers are known.
  Directly targets the premise inversion, and blunt: it turns every interjection in a group meeting
  into its own unattributed bubble, which is a visible cost for an invisible correctness gain.
- **(d) Mark inherited text instead of absorbing it silently.** Keep the grouping, render the inherited
  span so it does not read as an assertion about who spoke. Most honest, most UI work, and it needs a
  design decision about the bubble's visual language.
- **(e) Never inherit.** Truthful and rejected: it produces exactly the shattered transcript the rule
  was written to prevent.

Recommendation: **(b)**, then re-assess. It is the only option that improves correctness without a
visible cost or a design conversation, and it composes with the others. (d) is the better long-term
answer if the leak turns out to be dominated by population (1), which (b) cannot fully fix.

### Scope, and one honest limitation

`ShouldReuse` is one pure static function with direct coverage in
`tests/Pia.Wpf.Tests/Services/LiveTranscription/TranscriptGroupingTests.cs`, including
`ShouldReuse_LetsAnUnlabeledSegmentInheritTheRun`, which would need to be joined by an
`…_OnlyNextToTheRun` sibling rather than changed. Both the live overlay and the unattended recorder go
through the helper, so they cannot drift apart. That makes (b) a genuinely small change.

**The fixture cannot measure it.** `Measure-SpeakerAttribution.ps1` scores service-side labels parsed
out of the log, and this is a grouping rule applied above that layer — so no accuracy or label-count
number will move, in either direction, and a green fixture run is not evidence the change helped.
Judging (b) needs a transcript-level comparison on a replay, or a UI check, and that should be decided
before the work starts rather than discovered when the numbers come back flat.

## Suggested order

1. **Sticky display numbering** — smallest, user-visible, no bench measurement needed, and F2 makes it
   a de-facto prerequisite for the picker.
2. **Inheritance option (b)** — small, independent, and it is what keeps a corrected bubble corrected.
3. **The pin plan**, with F1 settled first (pin on rename, or explicitly not) and F3 written down.
