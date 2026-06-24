# HANDOVER / Open Questions — Per-Speaker Colored Chat Bubbles (attendee branch)

**Date:** 2026-06-24
**Branch:** `feature/meeting_attendee`
**Plan:** `docs/superpowers/plans/2026-06-24-meeting-per-speaker-bubbles.md`
**HEAD at handover:** `6c109d2`

---

## TL;DR — read this first

The full migration is implemented, builds clean, and the entire non-network test suite is green
(**Gate B: 920 / 920 passed, 0 failed, 0 skipped**). Every planned phase shipped.

**BUT: "the migration achieves its goal" CANNOT be claimed yet.** This workflow never exercised a
live multi-speaker meeting or any real audio path. The single empirical fact the whole feature hinges
on — **speaker-label stability on the real mixed downstream loopback stream** — is **UNVALIDATED**.
See [The central caveat](#the-central-caveat-read-before-shipping) below. Everything else is a
known-and-accepted limitation; this one is an open risk that only a real meeting can close.

---

## What shipped

Six build units, ten plan phases, all committed to `feature/meeting_attendee`. In commit order:

| Commit | Unit | Phases | What landed |
|--------|------|--------|-------------|
| `a2d3a64` | U1 | Phase 0 (Deliverable A) | Wrap-bug fix + auto-scroll in `MeetingAttendeeOverlay`. Inner horizontal `StackPanel` → 2-column `Grid` (`*` wrapping `TextBlock` + `Auto` indicator) in both bubble bodies, so `TextWrapping="Wrap"` finally fires. Per-bubble `PropertyChanged` subscribe/unsubscribe tied to the front-trim, scroll-on-Append gated on already-at-bottom. |
| `b3ccade` | U2 | Phases 1–2 | Ported the self-contained diarizer verbatim: `ISpeakerIdentificationService` + `SpeakerIdentificationService` (SherpaOnnx `SpeakerEmbeddingExtractor`, centroid cosine matching). Added `EnsureSpeakerEmbeddingAsync` / `IsSpeakerEmbeddingAvailable` / `SpeakerEmbeddingModelPath` mirroring `EnsureSileroVadAsync`. **No consent types** ported (the service is self-contained). |
| `f14b958` | U3 | Phases 3–5 | Data model + engine label + settings. `TranscriptUtterance.SpeakerLabel` (optional record param), `TranscriptBubble.SpeakerLabel`/`ColorIndex` (`[ObservableProperty]`), engine produces the label at the segment tag point (guarded by `MinDiarizationSamples` = 1.5 s, inner try/catch logs enum only), `AppSettings.EnableMeetingDiarization=true` / `SpeakerEmbeddingThreshold=0.70f`. |
| `685c295` | U4 | Phase 6 (+P7 reference-only) | Orchestrator wiring in `MeetingAttendeeService`: owns/constructs/threads/disposes the per-session speaker service, **degrades to null** on any download/construct failure (join still reaches `Attending`, never `Error`). Disposes `_speakerId` strictly **after** the engine drain. P7 (Bootstrapper) confirmed no-change (per-session construction). |
| `320ec38` | U5 | Phases 8–9 | The correctness gate. 3-value `SpeakerToDisplayNameConverter` (`You`→"you"; non-blank `SpeakerLabel` wins; else `CounterpartName`; else "them"). **Label-keyed merge** in `GetOrCreateBubble` (`sameWindow AND string.Equals(last.SpeakerLabel, speakerLabel, Ordinal)`). 5-color palette + `ColorIndex` assignment; theme brushes in `Light.xaml`/`Dark.xaml`; the `Them` body inline `Background` DELETED and replaced with a `Border.Style` (default Setter + 4 `ColorIndex` DataTriggers). |
| `1c426f8` | U6 | Phase 10 (Deliverable C) | In-session speaker rename (within-meeting only). `IMeetingAttendeeService.RenameSpeaker` passthrough (`_speakerId?.Rename`, null-safe), VM `RelabelSpeaker` re-keys the palette slot synchronously then retroactively relabels live bubbles, edit-pencil + right-click affordance in XAML, EN/DE/FR resources. Discarded at meeting end (fresh service per meeting); no persistence. |

Two post-implementation quality/fix commits also landed on the branch:

| Commit | What |
|--------|------|
| `6649132` | Simplify pass: routed the four label-rendering diarizer log lines (match / borderline / new-speaker / rename) from `LogInformation` → `SensitiveInformation` (DEBUG-only, erased from release IL) because labels become user-typed names after rename; deduped the model-path `Path.Combine`. |
| `6c109d2` | Review fixes: (1) gated the new-bubble (collection-`Add`) auto-scroll on already-at-bottom (diarization adds a bubble per speaker switch, so the ungated jump yanked a reading user to the bottom each switch); (2) swapped the pre-existing `Engine done … text='{Text}'` transcript-text log from `LogDebug` → `SensitiveDebug` (transcript text is a payload; `LogDebug` is not release-erased and level is runtime-configurable). |

> A 9th commit `f456533` ("drain VAD windows incrementally to avoid ring-buffer overflow") is also on
> the branch between U6 and the simplify pass. It is a deliberate transcription-path fix, not part of
> this migration's plan; flagged here only so the log is not surprising.

---

## Build + test status (Gate B)

- **Build:** `dotnet build` (full solution incl. the test project) → **0 errors**. The only warnings are
  pre-existing `xUnit1051` analyzer warnings in unrelated test files. A green build proves the test
  project compiles.
- **Tests (Gate B):** `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` →
  **`Bestanden!` (Passed!)** — total **920**, failed **0**, succeeded **920**, skipped **0**.
  The ~18 known live-network provider tests are excluded by the filter (they are not part of the 920).
- **Headline correctness tests present and green** (in
  `tests/Pia.Wpf.Tests/ViewModels/MeetingAttendeeViewModelTests.cs`):
  - `Utterances_DifferentSpeakerLabelWithinWindow_SplitIntoTwoBubbles` — two distinct labels within the
    25 s window → **2** bubbles (fails against the old Speaker-only key; passes only after the
    label-keyed merge).
  - `Utterances_NullLabelSegmentMidRun_SplitsTheColoredRun` — `Speaker 1` / `null` / `Speaker 1`
    in-window → **3** bubbles, pinning the shipped null-label SPLIT behavior so a future ABSORB change
    is a deliberate, tested diff.

**What the tests do NOT cover:** they exercise the bubble/merge/color/rename *logic* via the internal
`AddUtterance` seam with synthetic labels. They do **not** run real audio, real diarization, the XAML
binding/coloring at runtime, or the SherpaOnnx model. Runtime UI behavior is unverified by this workflow.

---

## The central caveat (read before shipping)

> **Speaker-label STABILITY on the real mixed downstream loopback stream is UNVALIDATED.**
> (Plan §1 and §6 risk #1.) This workflow exercised **no live multi-speaker meeting and no audio path** —
> only synthetic-label unit tests. Until a real meeting validates label stability empirically,
> **"the migration achieves its goal" cannot be claimed.**

Why this is the core risk and not a tuning footnote:

- The attendee feeds the diarizer **a single mixed downstream loopback stream** — endpoint WASAPI
  loopback captures the Teams DOWNSTREAM render mix (blending **all** remote participants into one
  stream). This is **NOT a far-field room microphone** — do not dismiss it as a mic-placement problem.
  One mixed stream is the **worst case** for centroid speaker-ID: speaker overlap, codec artifacts, and
  per-participant capture/level variance all degrade the voice embeddings.
- The bubble merge key is `string.Equals(last.SpeakerLabel, speakerLabel, Ordinal)`. **Bubble
  correctness is therefore a direct function of label stability.**

**The fragmentation failure mode (spell it out):** if the diarizer assigns an *unstable* label to a
single physical voice — i.e. mid-monologue it re-registers that same voice as a fresh `"Speaker N"`
instead of matching the existing centroid — the ordinal merge key sees a different label and **refuses to
merge**. The result is that **one person's continuous monologue fragments into many separate bubbles**.
This is not a cosmetic glitch: it is a *variant of the very "single line / lines scrolling out of the
window" symptom this migration was built to cure*. It is **not** a color collision (that is the cosmetic
palette-wrap limitation below) — it is a bubble-identity / bubble-split failure driven by label
instability.

The code is structured to **compile and degrade safely regardless** (degrade-to-null, fresh service per
meeting, 1.5 s minimum-samples guard, tunable threshold), but safe degradation is not the same as
achieving the goal. Only a real multi-speaker meeting on the real loopback path can validate it.

---

## Known limitations carried by design (accepted, not bugs)

These were resolved decisions (plan §8) and/or documented risks (§6) — they ship as-is on purpose:

1. **Palette wrap is cosmetic (mod 5).** Palette size is 5; the 6th distinct *stable* speaker reuses
   slot 0's color — a **color collision, not a bubble split** (identity stays `SpeakerLabel`; bubbles
   still split correctly). Distinct from the fragmentation failure mode above. Matches the POC.
   (§6 risk #3, decision #6.)
2. **Null-label mid-run SPLIT (decision #5, pinned by test).** Sub-1.5 s segments emit
   `SpeakerLabel=null` → ColorIndex 0; arriving mid-run they interrupt a colored speaker's bubble and
   create a one-off uncolored bubble. Shipped as the simple deterministic behavior and **pinned by the
   `…NullLabelSegmentMidRun_SplitsTheColoredRun` regression test**, so a future "absorb null into the
   previous bubble" change is a deliberate, tested diff — not a silent behavior swap. (§6 risk #4.)
3. **Model language coverage is zh/en only.** The model is 3D-Speaker CAM++
   (`3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`, Mandarin + English). Other-language
   voices may embed poorly, which **feeds the fragmentation risk above** (worse embeddings → more
   instability). Do not claim general multilingual diarization. (§6 risk #2.)
4. **Rename is eventual-consistency (P10).** A diarization segment already in flight when the user
   renames can land a stray *old-label* bubble just after the retroactive relabel walk completes.
   Cosmetic; self-corrects on the next utterance. Do not claim perfect consistency. (§6 risk #13.)

---

## PRIVACY follow-up (deferred — do not lose this)

Voice embeddings are **biometric data**. (Plan §2 PRIVACY NOTE, §6 risk #6, decision #1.)

- **What ships now:** embeddings are computed and centroided **in memory per session, never persisted**,
  discarded at meeting end (fresh service per meeting). The attendee already shows a consent
  acknowledgement checkbox before a meeting is joined. Diarizer log lines that render speaker labels are
  DEBUG-only (`SensitiveInformation`), and transcript text is DEBUG-only (`SensitiveDebug`) so neither
  leaks into release support logs.
- **What is the minimum bar, not a full model:** the existing checkbox is the floor. **A full consent
  surface for biometric processing is deferred** — explicitly declined for this migration and tracked as
  a follow-up so it is not lost.
- **Recommended follow-up:** a first-class consent surface (the POC had one) that *gates* diarization,
  plus an explicit disclosure that "voice fingerprints are computed locally and discarded at meeting
  end." Note: **cross-meeting / persistent speaker memory remains OUT** precisely because persisting
  biometric embeddings reopens the whole consent question; any future persistent recognized-speaker
  store must come with that first-class consent surface and a "forget" control.

---

## Salvage-source note (you can now delete the source branch)

The diarizer, the rename command, and the affordance XAML were salvaged from
**`feature/meeting_transscription`** (pinned commits `dc3619f` + `966deab`), which the plan header marked
"scheduled for deletion." That branch was previously the **only** ref carrying those files (verified
across every ref, the reflog, and the stash — they were never committed elsewhere), so it had to survive
until the port landed.

**The port has landed.** The ported files now live **on `feature/meeting_attendee` (committed:
`b3ccade` for the diarizer + model download, `1c426f8` for the rename)**. The salvage no longer depends
on the delete-scheduled branch, so **`feature/meeting_transscription` can be safely deleted** (both the
local branch and `origin/feature/meeting_transscription`). (§6 risk #12 — now mitigated.)

---

## Concrete next steps for the human

1. **Run a real multi-speaker meeting on the real loopback path** (the one validation this workflow
   could not do). Watch specifically for the **fragmentation failure mode**: does one person's
   continuous monologue stay in one bubble, or does it split into many as the diarizer re-registers the
   same voice as new `"Speaker N"` labels? That observation is what closes — or reopens — the central
   caveat. This is the gate on claiming the migration achieves its goal.
2. **Tune `SpeakerEmbeddingThreshold`** (currently `0.70f`, backing property only, no settings UI). This
   is the primary knob against label instability, but meaningful tuning needs real-audio measurement
   first. If you see fragmentation, this is the first dial to turn (lower → more merging / more false
   joins; higher → more splitting). Decide afterwards whether a settings UI is warranted.
3. **Decide null-absorb vs split** (decision #5). If sub-1.5 s null-label segments visibly chop up the
   conversation in practice, flip the shipped SPLIT to ABSORB (merge a null-label segment into the
   previous bubble). The `…NullLabelSegmentMidRun_SplitsTheColoredRun` test pins today's behavior, so the
   change will be a deliberate, tested diff. Re-tuning `MinDiarizationSamples` (1.5 s) is the related lever.
4. **Schedule the biometric-consent follow-up** (see above) before any move toward persistent
   cross-meeting speaker memory — persistence is the line that mandates the full consent surface.
5. **Delete `feature/meeting_transscription`** (local + remote) once you have confirmed the ported files
   are on `feature/meeting_attendee` — the salvage dependency is discharged.
6. **Validate the runtime UI** the tests could not: per-speaker colors render and stay stable, the wrap
   fix actually wraps long lines, auto-scroll stays pinned at the bottom but does not yank a user who has
   scrolled up, and the rename pencil/right-click retroactively relabels live bubbles.
