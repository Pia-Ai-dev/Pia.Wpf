# Plan: let the user force a re-decision on one transcript bubble, and make it stick

**Status.** Planned, not started.
**Owner.** Marco Altmann.
**Written.** 2026-09-03.
**Origin.** A live Teams meeting on 2026-09-03 whose transcript put two people under one speaker
label: a long stretch at the start of the meeting was labelled *Florian* but was actually *Nils*,
while other bubbles on the same label really were Florian. The question that produced this plan was
"can we add a reliable way to force the auto detection to re-evaluate a chat bubble?". Builds on the
measured findings in [2026-08-21-speaker-attribution-measurements.md](2026-08-21-speaker-attribution-measurements.md)
(Lever 6 in particular) and is independent of the accuracy levers in
[2026-08-22-attribution-levers-brief.md](2026-08-22-attribution-levers-brief.md) — this is a
correction affordance, not a detector improvement.

## The problem

The only correction the UI offers today is the pencil on a bubble's speaker name, and it is a
**whole-label rename** (`MeetingAttendeeViewModel.RenameSpeakerLabelAsync`,
`src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs:424`). On a cluster holding two people that is
the wrong tool: retyping *Florian* → *Nils* also renames the bubbles that are correctly Florian.
There is no way to move a single bubble anywhere.

**Reliable** is the load-bearing word. `AdaptiveSpeakerIdentificationService` re-clusters the whole
meeting every 5 segments or 30 s and reassigns every eligible segment (`RunPassUnderLock`,
`src/Pia.Wpf/Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs:232`), so an
unprotected per-bubble correction is undone within half a minute. `_renamedClusters` (`:48`) is
*not* the mechanism to reuse here — it only stops a renamed cluster from being recycled; it does
not pin which segments belong to it.

## What "re-evaluate" has to mean

Re-running the same clustering over the same vectors returns the same answer, so a bare "try again"
button would be a no-op. Only two things can differ from the original decision: **a constraint the
user supplies** (honoured by construction rather than earned by a similarity score), and
**maturity** — the first label was often decided against a centroid one segment old. So the feature
is a *constrained re-decision*, not a retry.

**One click fixes one bubble.** The pin sticks and nothing else moves: pinned segments are held out
of the dendrogram, so unpinned siblings still in the wrong cluster are re-clustered without that
evidence and stay where they are. Constraint *propagation* — re-posing the clustering so similar
segments follow — is a separate, larger design and is out of scope. Owner decision, 2026-09-03.

**Each segment is decided on its own**, not once for the bubble. Owner decision, 2026-09-03: a
bubble genuinely can hold two voices — often that is the mistake being corrected — so re-detecting
one may legitimately split it into several bubbles on rebuild. Two consequences to build for
deliberately:

- Unlabelled short utterances (`SegmentId` present, no label) inherit whichever run now precedes
  them (`TranscriptGrouping.cs:27`), so a split can move a "genau" to the other side. Correct
  behaviour, but it belongs in a test rather than being discovered in a meeting.
- Re-detect mints **once per call** even when it fires over several segments, so an unknown voice
  spread across a bubble gets one new label, not one per segment.

## Scope

In: the Teams meeting attendee overlay, live, with smart detection on
(`AppSettings.MeetingSmartSpeakerDetection`, default `true`).

Out, deliberately:

- **The direct-transcription overlay.** Same base view model, but consent there is keyed by speaker
  label (`src/Pia.Wpf/Services/Consent/`), so moving segments across labels moves text across a
  consent boundary. Its own decision, not this change.
- **Correcting a saved transcript.** Journaled embeddings are biometric and are zeroed on
  `Reset`/`Dispose` by design (`WipeBiometricStateUnderLock` `:432`, commit `58c8d562`). Nothing
  survives the meeting to re-evaluate; persisting it is a separate privacy call.
- Cross-meeting voiceprints / enrollment — lever 7 in
  [2026-08-22-attribution-levers-brief.md](2026-08-22-attribution-levers-brief.md).
- **Roster names in the picker.** `AssignSegments` refuses a label no cluster carries, and the
  options come only from speakers already visible in `Bubbles`. So "this is Nils, whom the detector
  never separated at all" takes two steps — re-detect (which mints a fresh label), then the pencil
  to name it. Offering `ObservedAttendees` (`MeetingAttendeeService.cs:623`) directly, minting a
  named cluster in one action, is the obvious follow-up and is left out on purpose: it makes assign
  able to *create* speakers, a wider contract than "move these segments".

## Design

### 1. Two new service operations, pinned

On `ISpeakerIdentificationService`
(`src/Pia.Wpf/Services/LiveTranscription/ISpeakerIdentificationService.cs`), after `Rename` `:43`,
as default-bodied members following the `SetExpectedSpeakers { }` precedent at `:56` — so the manual
`SpeakerIdentificationService` and the three test fakes keep compiling and truthfully refuse:

```csharp
bool AssignSegments(IReadOnlyList<long> segmentIds, string targetLabel) => false;
bool RedetectSegments(IReadOnlyList<long> segmentIds) => false;
```

`bool`, not a list of reassignments. The caller must not learn the labels that way: the single
reconciliation path is `SpeakersReassigned` → `ApplyReassignments`
(`TranscriptOverlayViewModel.cs:318`), and the unattended twin
`ScheduledMeetingRecorder.MeetingJournal.ApplyReassignments` (`:258`) has *no* other path, so a
return-value design would leave it permanently stale. `false` cleanly means "refused"; assigning a
segment to the cluster it is already in returns `true` and emits nothing, which is a real operation
("this one is right, freeze it").

### 2. `AdaptiveSpeakerIdentificationService`

**New state:** `Dictionary<long, int> _pinnedClusterBySegment`. Segment ids and cluster ids only —
no vectors, so it adds no new `Array.Clear` site. Cleared in `WipeBiometricStateUnderLock` `:432`
(which `Reset` and `Dispose` both route through) and on journal eviction next to the existing
`_clusterBySegment.Remove` at `:138`. `_nextSegmentId` stays monotonic across `Reset`, so a pin can
never be resurrected by a colliding id.

**`AssignSegments`** — resolve label → cluster over `_labelByCluster`, lowest cluster id wins (see
§5 on why two clusters can share a label), no match → `false`. Lowest-id is a **decision, not a
tie-break**: §5 makes the duplicate state more reachable rather than less, so assigning to a
duplicated label lands on whichever cluster happens to have the lower id, invisibly to the user, and
that choice decides which centroid their correction is measured against later. Accepted as low
severity; recorded so it is not mistaken for an accident. Per id: linear-scan `_segments`,
skip if evicted, set both `_clusterBySegment[id]` and `_pinnedClusterBySegment[id]`, and queue a
`SpeakerReassignment` when the label actually changed. Probe `_segments`, **not**
`_clusterBySegment` — a journaled sub-floor segment that was never placed has no entry there
(`:175-176`) but is legitimately assignable.

**`RedetectSegments`** — per id, `BestClusterUnderLock` with the segment's current cluster excluded
(one new optional `excludeCluster = -1` parameter; cluster ids are always `>= 0`, so both existing
callers are unaffected). Accept at `MatchSimilarityMin` (0.40), **not** the live `_matchSimilarity`:
Lever 6's 26-for-26 nearest-centroid result was measured on segments that had already *failed* the
match bar, so gating on the adaptive threshold would refuse exactly the measured cases. Nothing
clears it → mint, once per call.

Sub-floor segments (under the 2 s clustering floor, `MinClusterSegmentSeconds`) **may move but may
never mint**. They are journaled with a real embedding and are only held out of *clustering*;
re-detect is a centroid comparison, which is the operation Lever 6 scored in the 1.5–2 s band — the
band holding the bubbles most likely to be wrong, so greying re-detect out there would disable it
where it helps most. Minting from one is the bug
`Pass_HasNoClusterDefinedOnlyBySubFloorSegments` (`AdaptiveSpeakerIdentificationServiceTests.cs:378`)
locks down, so a sub-floor segment that matches nothing becomes unlabelled instead. Below the 1.5 s
VAD gate there is no `SegmentId` at all (`LiveTranscriptionEngineService.cs:172` never calls the
diarizer), so those bubbles are structurally out of reach and the menu must not pretend otherwise.

**Four sites in `RunPassUnderLock` `:232`:**

| Site | Change |
|---|---|
| `:236-238` `journalIndex` | skip pinned segments — removes them from the dendrogram *and* from the overlap vote at `:263-267` in one edit |
| `:217-223` `EligibleCountUnderLock` | the same exclusion. **The most dangerous line in the change:** miss it and a pass runs on an empty dendrogram, deleting every label (`:352`) and nulling every segment (`:361-366`) — a whole-transcript wipe |
| `:296` orphan skip | also skip a cluster that holds a pin, exactly as it already skips `_renamedClusters`, or a zero-overlap pin target has its label recycled onto a different voice |
| before `:348` | carry every pin target the pass did not match back into `newLabelByCluster`. `_renamedClusters` buys protection from *recycling* only; nothing writes it back, so `:352-355` would delete the label out from under the pin |

Exclusion rather than "cluster normally, then override" is mandatory. Left in the dendrogram, a
pinned segment votes under its *pinned* cluster at `:263-267`, and two pins are enough to make the
other voice's new cluster claim the target's stable id — a full label swap.

**Centroids.** A pinned embedding does **not** feed a target that survived the pass with earned
members. The file states that policy twice already (`:153-158` "the match was forced not earned",
`:169-174` "must never move a centroid"), and a pin is the most forced match there is — the user may
be correcting for a reason the audio does not support (a room mic, a speakerphone, two people on one
device). Exclusion from `journalIndex` delivers this for free. **But a target with zero earned
members — a "ghost", which includes every freshly minted re-detect label — must have its centroid
rebuilt from its own still-journaled eligible pins.** Otherwise it vanishes from
`_centroidByCluster` on the next pass, can never be instant-matched again, and the next segment of
that voice mints a *second* label: the correction would degrade the thing it fixed. Rebuild from the
journal rather than carrying the old `RunningCentroid` by reference, which keeps the unconditional
wipe at `:349` exactly as it is. A ghost held only by sub-floor pins ends up label-only with no
centroid; that is safe, and correct, since nothing acoustically trustworthy defines it.

**Threading.** Compute under `_lock`; raise `SpeakersReassigned` — and `SpeakerRegistered` for a
mint — only after releasing it, through a helper shared with the existing raise block at `:204-212`.
The interface doc at `:58-63` notes the consent flow subscribes to `SpeakerRegistered`, so a label
that appears without it leaves the consent map without an entry.

Re-detect mints **past the roster ceiling** (`:153-158`): the user has asserted a voice the roster
did not account for, and refusing them on a head count scraped off a Teams panel would be the wrong
authority. That is a deliberate exception and needs one line of comment at the mint site.

**Logging.** Count at Information, label via `SensitiveInformation` / `SensitiveDebug`, matching
`Rename` `:410`. Use a **distinct prefix** — not `"Adaptive pass reassigned: [...]"`, which
`scripts/Measure-SpeakerAttribution.ps1:155` greps to score attribution. Reusing it would silently
fold hand-corrections into the measured accuracy.

**Known behaviours to write down, not fix.**

- A ghost whose voice the clusterer later merges elsewhere sits acoustically close to the absorbing
  cluster, so a *new* unpinned segment of that voice can land on either label. It self-corrects
  within one stride (~5 segments) because the new segment enters the dendrogram. The pinned segments
  never move.
- `_labelByCluster.Count` grows with ghosts, so the roster ceiling at `:153-154` starts
  force-matching slightly earlier. Arguably right — the user asserted more voices than the roster
  deduped to. If it proves wrong, the one-line fix is to count only clusters that have a centroid.
- Exclusion is single-shot, so on a two-voice meeting pressing re-detect twice toggles back to the
  original answer. Intended: with two voices, "not this speaker" has exactly one other answer.
- **Pin on rename is retroactive only** (owner decision on F1, 2026-09-04). Renaming pins the
  segments the cluster holds *at that moment*, so the typed name survives a merge and its existing
  bubbles stay put — but a *later* segment of that voice is unpinned and follows the algorithm as
  before. That is the same per-segment granularity the rest of the feature has, and the alternative
  (pinning the cluster forever) is the whole-label freeze the plan rejects above.
- **Renaming the dominant speaker can pause passes for a stride or two.** Pinned segments leave the
  warm-up count, so renaming a cluster that holds most of the eligible segments can drop the unpinned
  count below `WarmupSegments` until new audio arrives. Self-correcting, and it is the guard working
  as intended rather than a defect — a pass on the handful that remain is exactly what
  `Pass_IsSkipped_WhenTooFewUnpinnedEligibleSegmentsRemain` exists to prevent.

### 3. Bubble → segment ids

`TranscriptBubble` (`src/Pia.Wpf/Models/TranscriptBubble.cs`) carries no segment ids, so the view
model cannot say which segments a bubble is made of. Add a plain `List<long>` behind
`IReadOnlyList<long> SegmentIds` — nothing binds to it, so no `ObservableCollection` and no
`[ObservableProperty]` — plus one optional `long? segmentId` on `Append` `:60`, recorded *after* the
blank-text early return at `:62` so a segment whose text was dropped never claims membership.

Rebuild equivalence holds by construction, because the ids are derived from the same journal replay
rather than carried across it: `AddUtterance` `:234` passes `utterance.SegmentId`,
`RebuildBubblesFromJournal` `:371` passes `entry.SegmentId`. Same entries, same order, same
`GetOrCreateBubble` merge rule ⇒ identical `SegmentIds` partition.
`ScheduledMeetingRecorder.MeetingJournal.Project` `:308` compiles unchanged via the default
parameter and needs no ids — leave it alone rather than threading them through for symmetry.

Fix in the same change: `src/Pia.Wpf/Models/TranscriptUtterance.cs:19` claims `SegmentId` is null
whenever `SpeakerLabel` is. It is not — the adaptive service returns an id with a **null** label for
an unplaceable segment (`:180-181`), and this whole feature depends on the id being present exactly
when the diarizer ran.

### 4. Service, ViewModel, View

`IMeetingAttendeeService` / `MeetingAttendeeService.cs:556`, beside `RenameSpeaker`, under the same
"no-op when diarization is off, must not throw" contract:

```csharp
bool AssignSegmentsToSpeaker(IReadOnlyList<long> segmentIds, string targetLabel);
bool RedetectSpeakerForSegments(IReadOnlyList<long> segmentIds);
```

No default interface methods here — one production implementation plus three fakes
(`MeetingAttendeeViewModelTests.cs:1328`, `ScheduledMeetingRecorderTests.cs:78` and `:294`), all
updated. The existing `SpeakersReassigned` fan-out at `MeetingAttendeeService.cs:122-125` is what
lands the correction in both journals and needs no change.

`MeetingAttendeeViewModel`, beside `RenameSpeakerLabelAsync` `:424`, both commands taking the bubble
as their single `CommandParameter`:

- `AssignSpeakerCommand` — enabled when the bubble has segment ids, labels are not suppressed, and
  at least one other speaker exists.
- `RedetectSpeakerCommand` — additionally requires the bubble to *have* a label, since "not this
  speaker" is meaningless otherwise. Assign does not, because "that 'genau' was Alice" is exactly
  the case worth fixing.

The picker needs a second value that a single `CommandParameter` cannot carry, and the user must
pick by `DisplayLabel` (renumbered, `:280`) while the service needs `SpeakerLabel` (identity). So
add one member to `IDialogService`, mirroring `ShowInputDialogAsync` `:31`:

```csharp
Task<string?> ShowSelectionDialogAsync(string title, string prompt, IReadOnlyList<string> options);
```

The options come from `Bubbles` (distinct `SpeakerLabel`, minus the bubble's own) rather than the
diarizer's `KnownLabels` — exactly the set the user can see, and the pair invariant is already
locked by `Bubbles_NeverCarryALabelTheDiarizerHasDropped` (`MeetingAttendeeViewModelTests.cs:931`).
A `MenuItem.ItemsSource` submenu was rejected: a `MenuItem` with children does not raise `Command`,
so the clicked bubble cannot be latched without `ContextMenuOpening` code-behind, which is View
logic the repo's style rules push back on.

The view model does **not** apply the result itself — it relies on `SpeakersReassigned` →
`OnSpeakersReassigned` `:443` → `ApplyReassignments`, which is idempotent (`:335` skips unchanged
labels, `:339` rebuilds only if something moved), so the synchronous re-entrant path is safe.

`MeetingAttendeeOverlay.xaml` — two `MenuItem`s in the existing `Border.ContextMenu` `:415-425`,
reusing its `PlacementTarget.Tag` bridge, with per-item AutomationIds on a prefix disjoint from
`MeetingAttendee_ChipRename_`: `MeetingAttendee_BubbleAssign_{0}` and `_BubbleRedetect_{0}`. On
refusal, the snackbar already injected at `TranscriptOverlayViewModel.cs:65`.

### 5. Companion fix — `Rename` stops at the first cluster

Adaptive `Rename` `:400` has no collision guard (the manual implementation refuses,
`SpeakerIdentificationService.cs:198`), so renaming *Florian* → *Nils* when a *Nils* label already
exists leaves **two clusters carrying "Nils"**. That state is actually stable and renders correctly:
each cluster keeps its own stable id, both land in `_renamedClusters`, and the UI merges by label.
The real defect is narrower — `Rename` returns after the first match (`:412`), so a later
`Rename("Nils", …)` renames only one of them and the speaker half-splits. Fix: rename *every*
cluster carrying `oldLabel`, tracking `any` instead of returning early.

**Not a merge.** Actually merging the two clusters in `_clusterBySegment` would be undone within
five segments — the next pass re-clusters, the two voices separate again, and only one of them can
claim the stable id in the greedy overlap match (`:283-288`). A real merge needs the same pinning
machinery applied to every segment of the absorbed cluster, which is a third stickiness problem and
not in scope.

## Files touched

- `src/Pia.Wpf/Services/LiveTranscription/ISpeakerIdentificationService.cs` — two members
- `src/Pia.Wpf/Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs` — pin map, the
  two operations, four pass sites, ghost-centroid carry, `Rename`, wipe and eviction
- `src/Pia.Wpf/Services/MeetingAttendee/IMeetingAttendeeService.cs` + `MeetingAttendeeService.cs`
- `src/Pia.Wpf/Models/TranscriptBubble.cs`, `src/Pia.Wpf/Models/TranscriptUtterance.cs` (doc fix)
- `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs` — pass the id at both `Append` call sites
- `src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs` — two commands plus the option list
- `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml`
- `src/Pia.Wpf/Services/Interfaces/IDialogService.cs`, `DialogService`, and the new dialog view
- `src/Pia.Wpf/Resources/Strings/ViewStrings.{resx,de.resx,fr.resx}` — new keys in all three
- `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs:56` — bump the `MeetingAttendeeOverlay` row if
  the walk picks the new items up
- `docs/ui_automation/ui-automation-playbook.md:61` — the new per-bubble ids
- `docs/release_notes/RELEASE.md` — one bullet, per `docs/release_notes/README.md`

Tracking surface: [2026-09-03-manual-bubble-reattribution-checklist.md](2026-09-03-manual-bubble-reattribution-checklist.md).

## Tests

`tests/Pia.Wpf.Tests/Services/LiveTranscription/AdaptiveSpeakerIdentificationServiceTests.cs`, a new
`// ---- user speaker corrections ----` section reusing the existing `Seg(degrees, seconds)` and
`RecordingClusterer` doubles from `SpeakerIdentificationTestDoubles.cs`.

**Write this one first**, because its failure mode is a wiped transcript rather than a stuck pin:

- `Pass_IsSkipped_WhenTooFewUnpinnedEligibleSegmentsRemain` — 6 segments (one pass), pin all 6, feed
  4 more → still one pass and `KnownLabels` intact; 2 more → pass 2 with 6 inputs.

Then stickiness, which is the point of the feature:

- `Assign_SticksAcrossTwoLaterPasses` — assert the label held **and** that no event after the assign
  mentions that segment id at all. The silence is the strong assertion: it proves exclusion rather
  than luck.
- `Assign_KeepsTheTargetAlive_WhenThePassWouldMergeItAway` — the `_renamedClusters`-is-not-enough
  test. Also assert the scripted clusterer's input count: a length mismatch throws inside the pass,
  is swallowed at `:193-198`, and would let the test pass for the wrong reason.
- `Redetect_SticksAcrossTwoLaterPasses`.
- `Assign_ToTheSegmentsOwnCluster_ReturnsTrue_AndEmitsNothing`, then survives a scripted merge.

Mechanics and guards: pinned segments absent from the dendrogram input (mirroring
`Pass_ExcludesSubFloorSegments` `:291`); the target centroid does not follow a pin (mirroring
`SubFloorSegment_TakesBestMatch_WithoutMintingOrMovingTheCentroid` `:308`); re-detect takes the
nearest *other* centroid under a scripted layout; one mint and one `SpeakerRegistered` for a
multi-segment re-detect; a minted label survives the next pass (the ghost carry); a sub-floor
segment moves but never mints; unknown label refused; evicted segment refused; `Reset` clears the
pins; the default interface members refuse.

`tests/Pia.Wpf.Tests/ViewModels/MeetingAttendeeViewModelTests.cs`, extending the
`ApplyReassignments` block at `:329`:

- `Bubble_SegmentIds_SurviveAJournalRebuild` — the equivalence test.
- `Bubble_SegmentIds_SkipUtterancesWithoutOne`.
- `AssignSpeaker_ForwardsTheSegmentIds_AndTheIdentityLabel` — the dialog returns a *display* label;
  assert the service received the `SpeakerLabel`. The display↔identity mix-up is the bug most likely
  to ship.
- `AssignSpeaker_LandsOnTheBubbleThroughTheReassignmentEvent` — a real
  `AdaptiveSpeakerIdentificationService` wired to `vm.ApplyReassignments`, built like
  `Bubbles_NeverCarryALabelTheDiarizerHasDropped` `:931`. The one test that catches an API that
  reports success and does nothing.
- `Redetect_CanSplitABubble` — three segments in one bubble, one moved away; assert the rebuild's
  bubble boundaries and that a null-label utterance between them inherits the run that now precedes
  it. This locks in the per-segment granularity.
- `AssignSpeaker_OffersOnlyOtherSpeakersLabels`; commands disabled while `SuppressSpeakerLabels`;
  re-detect disabled for an unlabelled bubble; snackbar on refusal (which covers manual mode).

`tests/Pia.Wpf.Tests/Services/MeetingAttendee/MeetingAttendeeServiceStateTests.cs` — corrections are
safe no-ops when `_speakerId` is null (mirrors `:793`).

## Verification

1. `dotnet build -t:Rebuild -v:n`, then again with `-c Release` — **0 Warning(s), 0 Error(s)** in
   both. WPF re-reports `src/` warnings under the generated `_wpftmp.csproj`; fixing the source
   clears both.
2. `dotnet test` with no filter. The bar is `failed: 0`.
3. End-to-end, which is what actually proves stickiness:
   `scripts/Invoke-MeetingReplay.ps1 -AudioPath <fixture> -RosterSize <n> -RunName pin-check`
   replays a recording through the real attendee pipeline against a throwaway profile. The script
   drives the join form and then *waits* for the audio to play out — right-click a bubble in that
   live window during the wait, correct it, and let the replay run several more minutes. Then confirm
   from `%LOCALAPPDATA%\Pia\Logs\pia-*.log` that no later `Adaptive pass reassigned:` line contains
   the pinned segment ids.
4. `scripts/Measure-SpeakerAttribution.ps1` on a replay with **no** correction applied, to show the
   change is inert when unused — no accuracy or label-count movement against the baselines in
   [2026-08-21-speaker-attribution-measurements.md](2026-08-21-speaker-attribution-measurements.md).
   Read the traps section of
   [speaker-attribution-fixture-playbook.md](speaker-attribution-fixture-playbook.md) before quoting
   any number from either run.
5. UIA smoke per `docs/ui_automation/ui-automation-playbook.md`: open the overlay, right-click a
   "Them" bubble, and confirm `MeetingAttendee_BubbleAssign_*` and `_BubbleRedetect_*` resolve.
