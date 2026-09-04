# Checklist: manual per-bubble speaker re-attribution

**Status.** In progress — A1–A6 and E1 landed 2026-09-04 on `feature/long-meeting-degradation`.
**Owner.** Marco Altmann.
**Written.** 2026-09-03.
**Origin.** The tracking surface for
[2026-09-03-manual-bubble-reattribution.md](2026-09-03-manual-bubble-reattribution.md). Read that
plan first — every step below assumes its design decisions, and the line numbers it cites.

Tick boxes in the commit that lands the step.

**Effort.** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface
· `L` a week or more, a new subsystem.

**Value.** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Cleared by | Affects |
|---|---|---|---|
| G-A | Does the pin actually survive a real 20-minute replay, not just the unit tests? | A6a — a replay with the correction fired through a **test seam** (the shape of `MeetingAttendeeViewModel.RelabelSpeakerForTest` `:441`), so no UI is needed | A negative cancels D2 and D3. A correction that silently unsticks in the field is worse than no button, and the design goes back to the drawing board before any UI ships |
| G-B | Does the change stay inert when unused — no accuracy or label-count movement on a clean replay? | A6a's second run, scored by `Measure-SpeakerAttribution.ps1` | A negative means the pass surgery has a side effect the unit tests missed; blocks D2 and F2 |

Do not tick a dependant of an open gate without revisiting it.

## Steps

- [x] **A1 — Pin state and the two service operations.** Add
      `Dictionary<long, int> _pinnedClusterBySegment`, `AssignSegments` and `RedetectSegments` to
      `AdaptiveSpeakerIdentificationService`, plus the two default-bodied members on
      `ISpeakerIdentificationService` and the `excludeCluster` parameter on `BestClusterUnderLock`.
      No pass changes yet, so a pin does not stick at this point.
      *Deps:* — · *Effort:* S · *Value:* Enabler

- [x] **A2 — The warm-up count guard, and its test, before any other pass change.** Exclude pinned
      segments from `EligibleCountUnderLock` and land
      `Pass_IsSkipped_WhenTooFewUnpinnedEligibleSegmentsRemain`. Its failure mode is a wiped
      transcript, not a stuck pin, so it goes in ahead of the rest of the surgery.
      *Deps:* A1 · *Effort:* XS · *Value:* High

- [x] **A3 — Pass surgery: exclusion, orphan skip, pin-target carry.** The remaining three sites in
      `RunPassUnderLock` — skip pins in `journalIndex`, skip pin-holding clusters in the orphan
      sweep, and carry unmatched pin targets into `newLabelByCluster`.
      *Deps:* A2 · *Effort:* S · *Value:* High

- [x] **A4 — Ghost-centroid rebuild.** A pin target with zero earned members gets its centroid
      rebuilt from its own still-journaled eligible pins. Without this, every freshly minted
      re-detect label dies on the next pass and the next segment of that voice mints a second one.
      *Deps:* A3 · *Effort:* XS · *Value:* High

- [x] **A5 — Wipe, eviction and threading.** Clear the pin map in `WipeBiometricStateUnderLock` and
      on journal eviction; move both events outside `_lock` through a helper shared with
      `ProcessEmbedding`; add the distinct log prefix that `Measure-SpeakerAttribution.ps1` does not
      grep.
      *Deps:* A1 · *Effort:* XS · *Value:* High

- [x] **A6 — Stickiness and mechanics tests.** The rest of the
      `AdaptiveSpeakerIdentificationServiceTests` section: assign and re-detect surviving two
      passes, target-kept-alive, dendrogram exclusion, centroid does not follow a pin, one mint per
      call, sub-floor moves but never mints, refusals, `Reset` clears pins, default members refuse.
      *Deps:* A4, A5 · *Effort:* S · *Value:* High

- [ ] **A6a — Replay evidence, before any UI (gates G-A and G-B).** Two
      `Invoke-MeetingReplay.ps1` runs: one firing a correction mid-replay through a test seam and
      checking the log for later reassignments of the pinned ids, one clean run scored by
      `Measure-SpeakerAttribution.ps1` against the existing baselines. This is what makes "reliable"
      a measurement rather than a claim.
      *Deps:* A6 · *Effort:* S · *Value:* High

- [ ] **B1 — `TranscriptBubble.SegmentIds`.** The id list plus the optional `Append` parameter, fed
      from both `AddUtterance` and `RebuildBubblesFromJournal`, with the rebuild-equivalence and
      skip-null tests. Includes the stale `TranscriptUtterance.cs:19` doc fix.
      *Deps:* — · *Effort:* XS · *Value:* Enabler

- [ ] **C1 — `IDialogService.ShowSelectionDialogAsync`.** One new dialog method, its view, and its
      registration — the picker the assign command needs. Localised keys in all three resx files.
      *Deps:* — · *Effort:* S · *Value:* Enabler

- [ ] **D1 — Service forwarding.** `AssignSegmentsToSpeaker` / `RedetectSpeakerForSegments` on
      `IMeetingAttendeeService` and `MeetingAttendeeService`, plus the three test fakes and the
      null-`_speakerId` no-op test.
      *Deps:* A1 · *Effort:* XS · *Value:* Enabler

- [ ] **D2 — The two view-model commands.** `AssignSpeakerCommand` and `RedetectSpeakerCommand`
      with their `CanExecute` rules, the option list built from `Bubbles`, the display↔identity
      mapping, and the refusal snackbar. Tests including the real-service pair test.
      *Deps:* B1, C1, D1, gates G-A + G-B · *Effort:* S · *Value:* High

- [ ] **D3 — Context-menu items.** Two `MenuItem`s in `MeetingAttendeeOverlay.xaml` on the
      `MeetingAttendee_BubbleAssign_` / `_BubbleRedetect_` prefixes, the `ViewAutomationIdTests` row
      bump, and the playbook's overlay row.
      *Deps:* D2 · *Effort:* XS · *Value:* High

- [x] **E1 — `Rename` renames every matching cluster, and pins it.** The companion fix, so a later
      rename of a name shared by two clusters no longer half-splits the speaker — plus the F1
      decision (owner, 2026-09-04: **pin on rename**), so a pass that merges the cluster away can no
      longer delete the typed name. `Rename_SurvivesAScriptedMerge` covers the losing case.
      *Deps:* A1 · *Effort:* XS · *Value:* High

- [ ] **F1 — UIA smoke through the real menu.** Open the overlay, right-click a "Them" bubble, and
      confirm `MeetingAttendee_BubbleAssign_*` and `_BubbleRedetect_*` resolve and act. A6a already
      proved the pin; this proves the affordance reaches it.
      *Deps:* D3 · *Effort:* XS · *Value:* Med

- [ ] **F2 — Release note.** One bullet in `docs/release_notes/RELEASE.md`, per that folder's README.
      *Deps:* F1 · *Effort:* XS · *Value:* Med

## Not yet planned

- Roster names in the picker — assign directly to an `ObservedAttendees` name, minting a named
  cluster in one action instead of re-detect-then-rename. Deliberately out of scope; see the plan's
  Scope section.
- Constraint propagation — re-posing the clustering with the user's correction as a must-link /
  must-not-link so similar segments follow. The measured k=2 isolation results in
  [2026-08-21-speaker-attribution-measurements.md](2026-08-21-speaker-attribution-measurements.md)
  are the evidence this would work; the owner decision on 2026-09-03 was one bubble per click.
- An un-pin operation ("let the algorithm decide this one again"). Re-assigning already re-pins, and
  nobody has asked for it.
- A visible marker distinguishing a user-set label from a detected one. The hook already exists in
  `SpeakerConsentChip`'s `DisplayName` comment.
- Correcting a saved transcript, which needs the biometric-persistence decision first.

## Suggested order

Cheapest decisive work first, then the vertical slice:

1. **A2's test against A1** — the transcript-wipe guard is the one failure the feature could ship
   that is worse than the bug it fixes. A1 → A2 before anything else.
2. **A3 → A4 → A5 → A6 → A6a** — finish the diarizer, prove stickiness on the bench, then clear
   both gates on a replay. Everything above the service is dead weight until a pin actually holds.
3. **E1** in parallel whenever convenient — independent of the pin work and two lines.
4. **B1, C1, D1** — three small independent enablers; any order, or together.
5. **D2 → D3** — the UI slice, only after A6a.
6. **F1 → F2.**
