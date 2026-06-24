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

---

## Follow-ups implemented (2026-06-24)

Three of the deferred follow-ups from the original handover were implemented on `feature/meeting_attendee`
after the handover above was written (which captured state at HEAD `6c109d2`). They are **code-only
changes**: build is clean and the non-network suite is green (Gate B below), but — as with the original
migration — **no live meeting and no audio path were exercised by this workflow.** Read
[The central caveat](#the-central-caveat-read-before-shipping) — it is **unchanged** (restated at the end
of this section).

**HEAD after these follow-ups:** `94e8423`.

| Commit | Follow-up | What landed |
|--------|-----------|-------------|
| `58c8d56` | **#2 — wipe biometric embeddings at meeting end** | `SpeakerIdentificationService` now **actively erases** the in-memory voice embeddings on dispose, instead of leaving them to GC. |
| `80b11dd` | **#4a — diarization settings UI** | The `EnableMeetingDiarization` toggle and the `SpeakerEmbeddingThreshold` slider are now exposed in the General → **Speech** settings tab. |
| `8eb9ee5` | **#4b — download-progress dialog** | A progress dialog now shows during the first diarized meeting while the ~27 MB speaker-embedding model downloads; degrade-to-null is preserved on failure. |
| `94e8423` | **#4b — follow-up fix** | Extracted the `SpeakerModelDownloadUi` helper out of the `Pia.ViewModels` namespace into `Pia.ViewModels.Models` so the `MvvmPatternTests` NetArchTest passes (the helper is plumbing, not an `ObservableObject` view model). No behaviour change. |

### #2 — Biometric embeddings explicitly wiped on dispose (`58c8d56`)

- **File:** `src/Pia.Wpf/Services/LiveTranscription/SpeakerIdentificationService.cs`.
- **What changed:** dispose previously only called `_extractor.Dispose()` and left `_speakers`
  (the centroid `float[]` vectors) and `_displayLabels` to garbage collection. Now a private
  `WipeBiometricStateUnderLock()` (called under `_lock` by **both** `Reset()` and `Dispose()`)
  `Array.Clear`s **each** `SpeakerCentroid.Centroid` `float[]` to zero **before** clearing the two
  dictionaries and resetting the counter — i.e. **scrub-the-bytes**, not just drop references. A
  `_disposed` guard makes `Dispose()` idempotent (prevents a double native
  `SpeakerEmbeddingExtractor.Dispose()` on any shutdown path that disposes twice).
- **This upgrades the PRIVACY section's previous wording.** That section said embeddings were
  "discarded at meeting end (fresh service per meeting)" and "never persisted" — true, but they were
  *left to GC*. They are now **actively zeroed**. The earlier "discarded / left to GC" framing in
  [PRIVACY follow-up](#privacy-follow-up-deferred--do-not-lose-this) is superseded by this active erasure
  for the in-memory centroids.
- **When it runs:** dispose is invoked at every teardown path — natural end (`WatchForEndAsync` →
  `StopAsync` → `DisposeAllAsync` → `_speakerId.Dispose()`, after the engine drain), user-clicked stop,
  and app shutdown all route through `StopAsync`. The `Reset()` path (which now also scrubs) remains
  effectively dead-call-site code, but sharing the wipe method means it is correct if ever wired up.
- **No automated test** (sanctioned skip): constructing the real service requires a native ONNX model
  (its ctor calls `new SpeakerEmbeddingExtractor(config)` and reads `_extractor.Dim`); there is no
  model-gating test helper to mirror, and a pure test would either leak the private `SpeakerCentroid`
  type or test a trivial `Array.Clear` wrapper. Correctness is guaranteed structurally by the single
  shared `WipeBiometricStateUnderLock` used by both paths.

### #4a — Diarization enable toggle + `SpeakerEmbeddingThreshold` in settings (`80b11dd`)

- **Files:** `ViewModels/GeneralSettingsViewModel.cs`, `Views/SettingsViews/GeneralView.xaml`,
  `Resources/Strings/ViewStrings.resx` (+ `.de.resx`, `.fr.resx`),
  `tests/Pia.Wpf.Tests/ViewModels/GeneralSettingsViewModelTests.cs` (new).
- **What shipped:** in the General → **Speech** tab, a CheckBox for `EnableMeetingDiarization`
  (default **true**, mirrors the existing `AutoCaptureSelectedText` toggle) and, gated on that toggle
  (`StackPanel IsEnabled="{Binding EnableMeetingDiarization}"`), a Slider for `SpeakerEmbeddingThreshold`
  (range **0.50–0.95**, default **0.70**, **0.05** tick grid with snap, mirrors the
  `ChatHistoryRetentionDays` slider) plus an F2 display string. The change handlers persist via
  `SaveSettingsAsync` guarded by `_isLoading`; the VM also subscribes to `ILocalizationService.LanguageChanged`
  to re-raise the display on language switch. All user strings are localized in en/de/fr (real
  translations). Three new unit tests verify load/persist of both properties; all green.
- **Copy direction verified** against `SpeakerIdentificationService` (`bestSim >= _matchThreshold` ⇒ same
  speaker, cosine similarity): higher threshold ⇒ voices split more readily / fewer merged; lower ⇒ more
  grouped.
- **Scope note (unchanged from the original handover):** these settings only affect the **next** meeting —
  `MeetingAttendeeService` reads settings and builds the diarizer fresh per `StartAsync`, so there is no
  live re-bind. This addresses next-step #2's "decide whether a settings UI is warranted" by shipping the
  UI; the **threshold-tuning question itself remains open** because meaningful tuning still needs
  real-audio measurement (see central caveat).

### #4b — Download-progress dialog for the speaker-embedding model (`8eb9ee5`, fix `94e8423`)

- **Files:** `Services/Interfaces/ITranscriptionService.cs`,
  `Services/LiveTranscription/LiveTranscriptionModels.cs`,
  `Services/MeetingAttendee/IMeetingAttendeeService.cs` + `MeetingAttendeeService.cs`,
  `ViewModels/MeetingAttendeeViewModel.cs`, the three `ViewStrings` resx files, plus two test stubs
  updated for the widened signatures; and (in `94e8423`)
  `ViewModels/Models/SpeakerModelDownloadUi.cs` (extracted helper).
- **What shipped:** an **additive** optional `IProgress<ModelDownloadProgress>?` threaded from
  `MeetingAttendeeViewModel.StartAsync` down through the `_createTranscription` seam →
  `TryCreateSpeakerIdentificationAsync` → `EnsureSpeakerEmbeddingAsync`, which now uses the existing
  `DownloadWithProgressAsync` helper. The UI reuses the existing `ModelDownloadContentDialog` with a
  **lazy-show / terminal-dismiss** pattern: the dialog appears only on the first real *Downloading*
  report (a cached model emits none → no flash; pre-gated on
  `EnableMeetingDiarization && !IsSpeakerEmbeddingAvailable()`), and a terminal
  `ModelDownloadProgress(Completed)` report emitted from a **`finally`** dismisses it on success,
  failure→null, **and** cancellation — so the dialog is never stuck.
- **Degrade-to-null preserved (unchanged contract):** `TryCreateSpeakerIdentificationAsync` still swallows
  every exception (including `OperationCanceledException`) and returns null; `Progress<T>.Report` never
  throws, so reporting cannot make a join fatal. The dialog's **Cancel** is backed by a VM-owned CTS
  (separate from the start token) and means **"skip diarization, keep joining the meeting"** — it never
  aborts the meeting join.
- **A runtime bug was fixed by construction pre-commit:** `CancellationTokenSource.Cancel()` invokes the
  registered `dialog.Hide()` callback **synchronously** on the caller's thread, and the `Progress<T>`
  callback runs on a thread-pool thread (past `ConfigureAwait(false)`). Since `ContentDialog` is a
  `DispatcherObject`, an off-UI-thread `Hide()` would `VerifyAccess`-throw and leave a stuck dialog. The
  whole progress-handling body (and the dispose backstop) is now routed through the UI dispatcher. Build
  cannot catch this and runtime is unverified, so it was addressed structurally.
- **No automated test** for the dialog flow (needs a live WPF dialog host + a real model download); the
  existing `TryCreateSpeakerIdentificationAsync_*` degrade-to-null tests still pass with the
  trailing-optional progress param.
- **`94e8423`** is a pure follow-up refactor: the dialog helper was a private nested class of
  `MeetingAttendeeViewModel`, so NetArchTest's `ViewModelClasses_MustInherit_ObservableObject` rule
  failed. It moved unchanged to `Pia.ViewModels.Models.SpeakerModelDownloadUi` (the established home for
  VM-adjacent non-`ObservableObject` helpers, e.g. `ChatSessionManager`). No behaviour change.

### #3 — null-label SPLIT: deliberately LEFT UNCHANGED

The null-label mid-run **SPLIT** behavior (known limitation #2 / decision #5, pinned by
`…NullLabelSegmentMidRun_SplitsTheColoredRun`) was **deliberately not touched**. The choice between SPLIT
and ABSORB depends on whether sub-1.5 s null-label segments visibly chop up a real conversation — an
observation that **only a real meeting can provide**. Flipping it now would be a guess; the regression
test still pins today's SPLIT so any future flip remains a deliberate, tested diff. (Original next-step #3
stays open.)

### Build + test status (Gate B) for these follow-ups

- **Build:** `dotnet build` (full solution incl. tests) → **0 errors**. All warnings are pre-existing and
  unrelated to the changed files (e.g. `NU1903` SQLitePCLRaw advisory, `MVVMTK0034` in `FlowViewModel`,
  `xUnit1051` in unrelated test files).
- **Tests (Gate B):** `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` →
  **`Bestanden!` (Passed!)** — total **923**, failed **0**, succeeded **923**, skipped **0**. The +3 over
  the original handover's 920 are the new `GeneralSettingsViewModelTests`. The ~18 known live-network
  provider tests remain excluded by the filter.

### The central caveat is UNCHANGED

> **Speaker-label STABILITY on the real mixed downstream loopback stream remains UNVALIDATED.**

None of these three follow-ups exercised a live meeting or any real audio path — they are code, build, and
unit-test verified only. The one empirical fact the feature hinges on (does one physical voice keep a
stable label, or fragment into many `"Speaker N"` bubbles on the real mixed loopback stream?) is **still
not validated**. "The migration achieves its goal" **still cannot be claimed.** Original
[next steps](#concrete-next-steps-for-the-human) #1 (run a real multi-speaker meeting), #3 (decide
null-absorb vs split), and the runtime-UI validation (#6) remain open; #2 (settings UI) and the
biometric-wipe portion of the PRIVACY follow-up are now addressed in code (real-audio threshold tuning
still pending), and a **first-class biometric-consent surface and any persistent cross-meeting speaker
memory remain explicitly OUT / deferred.**

### Residual open items (new-code review findings, all low / optional)

A code review of these three follow-ups surfaced only low-severity, non-blocking items (none is a
functional regression):

1. **No *Downloading* report when the server omits `Content-Length`** (`LiveTranscriptionModels.cs`): the
   dialog only lazy-shows on a percentage report, which is gated on `totalBytes > 0`. The real GitHub
   release URL serves `Content-Length` (≈28.3 MB), so the dialog shows in practice; latent only if the
   CDN/headers change. Optional hardening: emit an indeterminate report on the first chunk when length is
   unknown.
2. **`ApplyPhase` has no case for the new `Completed` phase** (`ModelDownloadContentDialog.xaml.cs`, file
   unchanged but the `Completed` enum + the path routing it to the dialog are new): the terminal
   `Completed` report can momentarily collapse both panels to a title-only body before `Hide()` wins the
   race. Optional fix: add a no-op `Completed` case, or filter `Completed` out of the dialog's progress
   subscription in `SpeakerModelDownloadUi`.
3. **`DisposeAsync` can hang at app-shutdown** (`SpeakerModelDownloadUi.cs`): if a speaker dialog is still
   open and the dispatcher has begun shutting down, the queued cancel may never run and the dispose await
   never completes. Narrow window — on the normal path the terminal `Completed` has already cancelled the
   CTS so the block is a no-op. Optional: time-box the dismissal wait or check
   `Dispatcher.HasShutdownStarted`.
4. **Threshold not snapped to the 0.05 grid on load** (`GeneralSettingsViewModel.cs`): `InitializeAsync`
   clamps to range but not to the tick grid, so an off-grid stored value (only reachable by a manual
   settings-file edit) triggers a one-time idempotent re-save when the snapping slider renders. Cosmetic.
   Optional: snap on load the same way the change handler does.
5. **No integrity/signature verification on the downloaded model** (pre-existing pattern, **not introduced
   here**): the ~27 MB CAM++ `.onnx` is fetched over HTTPS and handed to the native extractor with no
   hash check — the same pattern as the Silero/Whisper/Parakeet downloads. Optional supply-chain
   hardening: pin and verify a known SHA-256 across all model downloads.
6. **Threshold StackPanel has two consecutive description `TextBlock`s** (`GeneralView.xaml`): the
   value-display and the helper-description stack with only 6 px between them — denser than other sliders.
   Cosmetic. Optional: add a top margin to the second block.
