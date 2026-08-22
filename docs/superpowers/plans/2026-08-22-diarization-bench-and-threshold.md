# Plan: make the fixture cheap and exact, then break the threshold loop

Date: 2026-08-22 (Saturday). The next real Teams meeting is **Monday 2026-08-24**, which is the one
outstanding acceptance item of `2026-08-21-speaker-attribution-fixes.md`. This plan is what to build
in the two days before it.

Grounding: `docs/reviews/2026-08-21-speaker-attribution-measurements.md` (what the fixture measured),
`docs/reviews/2026-08-21-speaker-attribution-assessment.md` (Defects 1 and 4, still open),
`1c329d9f`…`9cd3bd60` (what shipped).

## The two constraints that shape this plan

**One experiment currently costs an hour.** `DebugFileAudioCaptureService` paces with `Task.Delay`, so
a replay runs at 0.836x real time: the workshop recording is 964.6 s of audio in 1154.5 s of wall
clock, and the LSP recording is 2997 s of audio in ~60 minutes plus a 3-minute drain. The deferred
work — the cut → threshold feedback loop — is a *sweep*, not a single change. Six settings across two
recordings is twelve hours. It cannot be done through the app, and that, not indecision, is why it is
still deferred. **The first half of this plan removes that cost; the second half spends what is left.**

**Production behaviour freezes Sunday night.** Monday's meeting is the acceptance test for the end
state that is already measured. Phases 0–2 either add no production behaviour or add only exact
measurement; Phase 4 changes attribution policy and therefore lands on `feature/diarization-threshold`
and merges *after* Monday. The reason is the previous plan's own: a smoke test that covers two
changes at once tells you nothing about either.

## Out of scope, and why

| Deferred | Why not now |
|---|---|
| Consent-phase enrollment (named verification) | The assessment's strongest lever and still true, but it is a feature with UI and a privacy story. Task 7 measures its *ceiling* first, which is the cheap half of the decision. |
| Migration to `SherpaOnnx.OfflineSpeakerDiarization` | Needs the batch-vs-streaming retention call, which is an owner decision and changes a stated privacy claim. Task 13 measures what it would buy, so the decision is made against a number. |
| Teams DOM `voice-level` active-speaker signal | Still needs a live meeting. Monday is a smoke test, not an instrumentation session; do not load it up. |
| Wiring the per-speaker consent gate into the Teams path | A feature, unchanged from the last plan. |
| Restructuring the segment queue so transcription backpressure cannot cost the diarizer an embedding | Task 3 measures whether it happens at all first. The fix is either identification on the audio thread (which would block capture) or a second queue and an asynchronous label path — a real refactor, and not one to do on a hunch. |
| Lowering `MaxSegmentSamples` | Task 12 makes it measurable; changing it before the bench exists is guessing. |

## Sequencing

| # | Task | Verifiable by | Production change? |
|---|---|---|---|
| 0 | Tee both recordings to WAV | two unattended runs, no code | no |
| 1 | Exact stream offsets on every segment | `dotnet test` + a re-scored replay | yes, tiny |
| 2 | Replay script saves and reads back the numbering | the saved export shows Speaker 1..k | no |
| 3 | Make STT-queue segment drops visible | `dotnet test`, then read the fixture logs | yes, tiny |
| 4 | Offline diarization bench | reproduces the app replay's label sequence | no (test-side) |
| 5 | Exact-time scoring mode in the metric | reproduces 92.1 % ± 1 with residual 0 | no |
| 6 | Embedding cache + sweep driver | a 12-point sweep in under a minute | no |
| 7 | **Oracle-enrollment upper bound** | one number: embedding or clustering? | no |
| 8 | Decouple the online threshold from the cut | bench sweep, both recordings | yes — after Monday |
| 9 | Roster as a target for k, not only a ceiling | LSP up, workshop not inflated | yes — after Monday |
| 10 | Report; update the measurements doc | | no |
| 11 | Embedding model A/B | bench | evaluation only |
| 12 | A real VAD on speech-mask agreement | bench | evaluation only |
| 13 | `OfflineSpeakerDiarization` as an upper bound | bench | evaluation only |

Tasks 8 and 9 are **gated on Task 7**: it decides whether they are worth doing at all.

---

## Phase 0 — start before writing any code

## Task 0 — Tee both recordings to WAV

The bench needs the exact PCM the pipeline hears. Do not reach for `ffmpeg`: the app's Media
Foundation decode is the thing under test, and `DebugWavTeeAudioCaptureService` already tees a replay
(proven in `93516223` — same 13 segments out of the dump as out of the original). Set both env vars
in one run:

```
PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE=artifacts/meeting_recording/<recording>.mp4
PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP=artifacts/wav/<name>.wav
```

Two unattended runs, ~20 min and ~60 min. Start them now; they are the input to everything in Phase 2.
Doing this through `Invoke-MeetingReplay.ps1` also produces a fresh scoreable log for free.

**Acceptance.** Two `pcm_s16le` 16 kHz mono WAVs whose durations match the recordings within the AAC
decoder's priming/padding (~2 s), and whose replay reproduces the run's segment count.

**Privacy.** These are full recordings of real colleagues. `artifacts/` is already gitignored — keep
them there, and do not move them under `scripts/`.

---

## Phase 1 — improve Monday's evidence (production, small, lands before Monday)

## Task 1 — Exact stream offsets on every segment

**Why this is first.** Every attribution number in the measurements doc carries an alignment caveat,
because the metric recovers stream time from wall-clock log stamps through a fitted rate and offset.
That caveat cost real credibility: the workshop recording's number swings 10 points with the fitting
method, and even the LSP result has to be quoted with "up to ~5 points could be mapping error". The
information is not actually missing — the VAD knows exactly how many samples it has consumed. It
just never says so.

**Change.** `SileroVadDetector` keeps a running count of samples fed to `Process`, and each closed
segment carries the sample index at which it opened. `OnSegment` becomes
`Action<float[], long>` (or a small `VadSegment` record — decide at the keyboard, whichever keeps
`LiveTranscriptionEngineService` cleaner), and the existing `Segment identified:` line adds
`start={Seconds:F2}`. Timings are not sensitive, so this is a plain `SensitiveDebug` field alongside
the label that is already there.

Do **not** thread it into `TranscriptUtterance`: nothing in the UI needs it, and the metric reads the
log.

**Acceptance.**
- A unit test feeds a known silence/speech/silence pattern and asserts the reported start offset
  equals the sample position where speech began, including across a `MaxSegmentSamples` flush and a
  `Drain()`.
- Re-run one replay (the workshop recording, ~20 min, the cheap one) and re-score it in the new exact
  mode from Task 5: the fitted-offset sweep and its residual are gone, and the reported attribution
  lands inside the range the three fits already bracketed. If it lands *outside* that range, the
  fitting was wrong in a way worth understanding before trusting either number — stop and say so.

**Risk.** Hop boundaries: the VAD counts what it is fed, and the capture source may drop a hop under
load (`DebugFileAudioCaptureService` logs exactly that). A dropped hop shifts every later offset. Log
the cumulative sample count once at drain so a shift is detectable rather than silent.

## Task 2 — The replay script saves, and reads the numbering back

The measurements doc's second caveat: the fixture reads service-side labels only, so
"≤ 6 distinct labels **numbered from 1**" was settled for the count and never for the numbering. The
reason is one missing click — `Invoke-MeetingReplay.ps1` invokes `MeetingAttendee_Stop` and stops
there, even though `MeetingAttendee_Save` already carries an AutomationId.

**Change, as built.** After Stop, walk the UIA tree for every `Speaker N` the transcript is actually
rendering, write them next to the log, and check the distinct numbers are `1..k` with no gaps. Clicking
Save was the first plan; both save paths open a dialog (a Win32 file dialog, or the vault's title
dialog) which an unattended script should not have to drive, and the export is already unit-tested
where the rendering is not. Reading the screen is the stronger evidence anyway.

**Acceptance.** A replay prints `LABEL CHECK: pass (Speaker 1..k, roster N)` or a loud warning naming
the numbers it actually found — it does not throw, because a numbering gap must not discard an hour of
replay. This is the one Phase-1 task that turns an *inferred* invariant into an *observed* one.

## Task 3 — Make STT-queue segment drops visible

**A finding from reading the code for this plan, not from a review.** `LiveTranscriptionEngineService`
feeds segments through a bounded channel of 8 with `FullMode = DropOldest`, and speaker identification
runs *inside* `TranscribeSegmentAsync` — downstream of that channel. So when transcription falls
behind, the dropped segment is lost to the diarizer too: that voice's evidence never reaches the
journal, never enters a pass, and never gets a centroid. Today it logs one line per drop at Warning
and nothing aggregates it.

The workshop numbers say this is close to the edge on live audio: 773.5 s of speech transcribed in
803.7 s of engine time (1.04x speech time). At the replay's 0.836x the pipeline had 30 % headroom; at
1.0x live it has ~17 %, and a burst of back-to-back turns is exactly when both the queue fills and
attribution matters most.

**And the reason it has never been seen: the existing warning cannot fire.** `DropOldest` makes
`TryWrite` succeed by evicting the oldest queued item, so the `else` branch that logs "transcription is
falling behind" is dead code. The count now comes from the channel's `itemDropped` callback, which
observes the same eviction without changing which segment is lost.

**Change, deliberately minimal.** Count drops, and log the total at stop alongside the segment count.
Do **not** restructure the queue yet — that means either identifying on the audio reader thread
(embedding extraction would block capture) or a second queue and an asynchronous label path (a real
refactor). Measure first: grep the Phase-0 logs for drops. If the fixture drops nothing, this is a
live-only risk and Monday's log will say; if it drops on a replay at 0.836x, it is already corrupting
the numbers in the measurements doc and the refactor gets its own task with evidence behind it.

**Acceptance.** A unit test fills the queue and asserts the drop counter and the stop-time summary
line. Then: a sentence in the measurements doc reporting the drop count for both fixture runs.

---

## Phase 2 — the bench (test-side only, changes no production behaviour)

## Task 4 — Offline diarization bench

**What it is.** A program that reads a teed WAV, pushes it through the *production*
`AudioHopResampler` → `SileroVadDetector` → `SherpaEmbeddingExtractor` →
`AdaptiveSpeakerIdentificationService` with no `Task.Delay`, no STT, and no UI, and writes one JSONL
line per segment: `{startSeconds, endSeconds, segmentId, label}` plus one per pass:
`{passIndex, clusterCount, cutDistance, matchSimilarity, reassignments}`.

**Why it is honest.** It runs the shipping types, not a reimplementation — the moment it stops doing
that it stops being able to arbitrate anything. Three things make it deterministic where the app is
not: stream-time offsets instead of wall clock, a virtual clock (the service's internal constructor
already takes `Func<DateTimeOffset>`, so `PassMaxLatency` fires on stream time), and no STT
scheduling. Reproduce the app's own gates exactly or the comparison is void: the 1.5 s
`_minDiarizationSamples` gate lives in the engine, not the service, so the bench must apply it too.

**It is also the principled version of a lever the measurements doc rejected.** Speeding up the
replay's `Task.Delay` was considered and turned down because at 4x the diarizer's 30 s wall-clock pass
trigger stops firing and the pass sequence changes — 4 of the workshop run's 41 pass intervals came
from that trigger. A stream-time virtual clock fires it at the same points regardless of how fast the
bench runs, which is why the bench can be 20x faster and still comparable where a rate multiplier
could not.

**Where it lives.** `tests/Pia.Wpf.Tests/Services/LiveTranscription/DiarizationBench.cs`, driven by a
`[BenchFact]` attribute mirroring `LiveApiFactAttribute` (`Explicit = true`, so `dotnet test` with no
filter still ignores it and the gate is unchanged). No new project: the test project already
references `Pia.Wpf`, which carries `org.k2fsa.sherpa.onnx`. **Verify this first, in ten minutes,
before writing the bench:** `SileroVadDetectorSpeechEventsTests` constructs the VAD with an empty
model path and the VAD discards it, so nothing in the suite has ever loaded a sherpa native from the
test output. If the native assets do not flow transitively, add a direct `PackageReference` to the
test project — the standard fix, and cheaper to find now than at the end. Inputs come from env vars,
as the replay path already does.

**Known divergences from the app, to be stated in the report rather than discovered later.** No STT
means no queue drops (Task 3). The tee quantizes to 16-bit PCM before the bench reads it, where the
live pipeline is float32 — measured as irrelevant to segmentation already, but it is a difference.
Pass triggering differs where it is time-based rather
than stride-based: in the app the 30 s trigger measures wall clock *between identify calls*, which STT
throughput gates, while the bench measures stream time. Expect a small difference in which passes fire
from the latency trigger, and check it against the app run's pass sequence in the acceptance below.

**Acceptance — the one that matters.** Over the teed WAV of the LSP recording, the bench reproduces
the app replay's run: same segment count within 2 %, same distinct-label count in the final pass, and
attribution within 2 points. Anything worse and the bench measures a different pipeline than the one
that ships; reconcile it before Task 7, because every later number depends on this.

**Target cost.** Under 3 minutes for the 50-minute recording, of which almost all is embedding
extraction. That is the 20x that makes the rest of this plan possible.

## Task 5 — Exact-time scoring in `Measure-SpeakerAttribution.ps1`

Add `-SegmentsPath <jsonl>` to score the bench's output, and make the existing log path use the new
`start=` field when it is present. Both feed the same reference-joining, bucketing and
confusion-matrix code that is already validated — do not fork the scorer, or the bench and the app
stop being comparable.

With exact offsets there is nothing to fit: drop the offset sweep on that path and print the speech-
mask agreement as a plain sanity check. Keep `-Offset` and the sweep for scoring the older logs.

Add `-Baseline <jsonl|log>` to print a two-column delta table. A sweep whose output has to be diffed
by eye will be read carelessly.

**Acceptance.** Scoring the existing LSP end-state run in the new mode reproduces 92.1 % ± 1 point.
The metric changing its answer by more than that means one of the two paths is wrong.

## Task 6 — Embedding cache and sweep driver

Embedding extraction is the bench's whole cost and it is invariant under every parameter Phase 4
touches. Cache it: one binary file per (WAV, embedding model), holding segment offsets, durations and
vectors, keyed by content hash of the inputs. A sweep then re-runs only
`AdaptiveSpeakerIdentificationService` over cached vectors — milliseconds per setting, so trying a
twelfth idea costs nothing.

**Privacy, non-negotiable.** A cached embedding is biometric data about named colleagues. It goes
under `artifacts/` (already gitignored), never under `scripts/`, and the cache file is written with no
names in it — segment offsets only, exactly like the anonymised-by-tile-position reference. Add the
path to `.gitignore` explicitly anyway, next to the `*.names.local.json` line, on the same reasoning:
the ignore rule is documentation.

Driver: `scripts/Invoke-DiarizationSweep.ps1`, taking a settings matrix and emitting one table across
both recordings.

**Acceptance.** A 12-setting sweep over both recordings completes in under a minute from a warm cache,
and re-running the *current* settings reproduces Task 4's numbers exactly — a cache that changes the
answer is worse than no cache.

---

## Phase 3 — the diagnostic that decides where the accuracy is

## Task 7 — The oracle-enrollment upper bound

**This is the highest-value task in the plan and it is nearly free once Task 6 exists.** Everything
after it is conditional on its answer.

The reference timeline gives the true speaker for every segment. So: label the cached embeddings with
the truth, and compute what no clustering policy can beat.

1. **Discriminability.** Intra-speaker and inter-speaker cosine similarity distributions (mean, σ,
   overlap), per speaker and pooled. If the two distributions overlap heavily, no threshold policy
   can separate them and Defect 1 is not the ceiling — Defect 4 is.
2. **Oracle nearest-centroid.** Enroll each speaker from their first ~30 s of reference-labelled
   speech, then classify every segment by nearest centroid. This is the accuracy the *current
   embedding model* supports with perfect enrollment and no clustering at all.
3. **Oracle k.** Re-run the real clusterer with `k` pinned to the true talker count and the online
   threshold fixed at its initial 0.50, to separate "inferring k is the problem" from "matching is the
   problem".

**How to read the result, decided in advance so the number cannot be rationalised after the fact:**

| Oracle nearest-centroid | Reading | Do next |
|---|---|---|
| ≥ 97 % | The embedding is fine; the clustering and the threshold loop are the entire gap | Tasks 8 + 9, and consent enrollment becomes the strongest product lever |
| 88–96 % | Real headroom in both | Tasks 8 + 9 first (cheaper), then Task 11 |
| < 88 % | CAM++ `zh_en` on German over the Teams codec is the ceiling | Task 11 before any threshold tuning |

End state today is 92.1 %. If the oracle bound comes back near that, the threshold loop is *not*
worth the week it looks like it is worth, and that is the most useful thing this plan could discover.

**Acceptance.** All three numbers, for both recordings, plus the per-speaker breakdown for Alexander
and Andreas — the pair the measurements doc names as the residue (142.1 s of Alexander filed inside
Andreas's label, while Alexander's own label holds 8 s).

---

## Phase 4 — the fix (branch `feature/diarization-threshold`, merges after Monday)

Gated on Task 7. Nothing here is written before that number exists.

## Task 8 — Decouple the online threshold from the cut

Today: `_matchSimilarity = clamp(1 − cut, 0.40, 0.60)`, recomputed every pass. Matching and clustering
feed each other undamped, and the baseline traversed the whole band inside one meeting (0.60 pinned
for passes 1–10, 0.40 for 19–21). The clamp bounds the swing and does nothing to stabilise it.

Implement as a selectable policy so the bench can A/B rather than argue, then **delete the losers** —
a mode enum that survives the experiment is a configuration surface nobody asked for:

- (a) current, derived per pass — the baseline
- (b) fixed at `InitialMatchSimilarity`
- (c) damped: EMA toward `1 − cut`, α ≈ 0.2
- (d) derived from the observed centroid-separation distribution rather than the cut, using Task 7's
  intra/inter statistic as the shape

**Acceptance.** Attribution on both recordings for all four, from cache; the winner beats (a) on LSP
and does not lose on workshop. Also report the *variance* of `_matchSimilarity` over the meeting —
stability is the point, and a policy that scores the same while holding still is the better one.

## Task 9 — Roster as a target for k, not only a ceiling

`ChooseCut` applies `expectedSpeakers` as a downward-only cap, deliberately: it protects the
mostly-muted workshop case, where 10 on the roster and 2 talking must not become 10 labels. With a
known roster, "the candidate cut closest to k" is a much stronger prior than "largest gap" — but only
where the roster reflects who actually speaks.

So this task is defined by its counter-case: any change here must be measured on **both** recordings,
and the workshop label count is the veto. Candidate policies: target-k when the roster is small and
every attendee is unmuted (not knowable), target-k with a floor on observed talkers, or leave
`ChooseCut` alone and let Task 8 carry it. Fully expect the honest answer to be "leave it alone" —
write that down if so, because the assessment recommends this change and a measured refusal is worth
more than a silent one.

## Task 10 — Report

Update `docs/reviews/2026-08-21-speaker-attribution-measurements.md` in place (it is the fixture's
running record, not a per-plan artifact): the exact-alignment numbers from Task 1, the drop counts
from Task 3, the bench-vs-app reconciliation from Task 4, Task 7's bound, and the Phase-4 result with
the Alexander/Andreas confusion matrix. Say which caveats are now retired, and keep the ones that are
not.

---

## Phase 5 — evaluation only, no production change

Each of these is a one-line change against the bench once Task 6 exists. Run them, record the number,
change nothing: they inform decisions that are not this plan's to make.

## Task 11 — Embedding model A/B

CAM++ `zh_en` is a Chinese/English model doing German over the Teams codec. Alternatives sit in the
same sherpa release the current model comes from, so this is a model path and a re-extraction. Score
Task 7's discriminability statistic *and* end-to-end attribution — the first says whether a swap can
help, the second whether it does.

## Task 12 — A real VAD

`SileroVadDetector` is not Silero: it takes a model path, discards it (`_ = modelPath`), and runs an
energy detector, because the ONNX binding failed against `Microsoft.ML.OnnxRuntime` 1.24.4. The
already-referenced sherpa package ships `SileroVadModelConfig` and `TenVadModelConfig`, which sidestep
that binding entirely. Segmentation sits upstream of every diarization decision, and turn boundaries
are where the errors are, so score it on speech-mask agreement (95.4 % today) and on how many
segments hit the 20 s `MaxSegmentSamples` flush cap. Rename the class as part of whatever this leads
to — a type called `SileroVadDetector` that is not one has already misled one review.

## Task 13 — `OfflineSpeakerDiarization` as an upper bound

Batch-diarize a whole teed WAV with `FastClusteringConfig.NumClusters` pinned to the roster and score
it with the same metric. This is not a migration and does not decide one: it is the number the
retention decision needs — what a maintained implementation with a known k would get on this audio.

---

## Task 14 — The unlabelled-transcript switch, and a disclaimer that does not oversell

Two halves of the same idea: say what attribution is, and offer a way out when it is wrong.

**The switch.** `AppSettings.MeetingSuppressSpeakerLabels`, off by default, on the Meeting settings tab
under the diarization toggles. Implemented in `TranscriptOverlayViewModel.ResolveDisplayLabel`, which is
the single point every surface reads: the bubbles, both Markdown exports and the vault copy. Toggling it
re-resolves the bubbles already on screen. The suppressed label must not reappear anywhere — the
`voiceStats:` block used to fall back to the diarizer's raw label when a bubble had none, which would
have put `Speaker 17` back into the ingested front matter, so that fallback is now bubble-first.

**The disclaimer.** Pinned above the transcript list, not inside it, so it cannot scroll away. It states
the mechanism (voice similarity), both failure modes (two voices merged, one person split), that earlier
labels get revised, and what to do about it (click to rename). No accuracy figure: the 92.1 % is one
recording, replayed, with an env-var roster, and quoting it in the product would be exactly the
overclaim this text exists to avoid. It mirrors the vocabulary of the direct-transcription disclaimer's
existing `CrossTalkLimitation` / `ShortUtteranceLimitation` lines rather than inventing a new register.

**Acceptance.** Four ViewModel tests (suppressed bubbles carry no label, a renamed speaker is hidden
too, the numbering returns unchanged when switched back off, the export carries no label) plus one on
the front-matter fallback. Localized in all three shipped languages, since `LocalizationTests` requires
complete de/fr translations.

---

## Global constraints

- **Gate:** `dotnet test`, no filter, `failed: 0`. The bench and its sweeps are `Explicit` and stay
  out of it.
- **Warnings:** `dotnet build -t:Rebuild -v:n`, Debug **and** Release, `0 Warning(s)`. Task 1 touches
  a `#if DEBUG` neighbourhood; check Release specifically.
- **Line endings: LF, not CRLF.** CLAUDE.md says new `.cs` files must be CRLF; measured against the
  committed blobs, **0 of 1319** tracked `.cs`/`.xaml` files contain a CR, there is no `.gitattributes`
  and `core.autocrlf` is unset. Following the instruction would make every new file the odd one out, so
  this plan follows the tree. Worth reconciling CLAUDE.md in a pass of its own.
- Comment discipline: one short line, only where the why is non-obvious, no task or spec citations.
- **Privacy.** Teed WAVs and cached embeddings are the most sensitive artifacts in this repo — real
  colleagues' voices and biometric vectors. `artifacts/` only, gitignored, no names in any file the
  bench writes. Timings and label numbers may be logged; audio paths stay `SensitiveDebug`.
- **Phases 0–3 change no attribution behaviour.** If a task in those phases starts to need one, it
  belongs to Phase 4 — stop and move it.

## Whole-plan acceptance

1. `dotnet test` green; Debug and Release rebuilds at zero warnings; the gate's runtime is unchanged
   (nothing in Phase 2 leaks into a default run).
2. A parameter sweep over both recordings runs in under a minute from a warm cache, and reproduces
   the app replay's numbers at the current settings.
3. The alignment caveat is retired: attribution is reported from exact stream offsets, with no fitted
   offset and no ±5-point disclaimer.
4. The numbering claim is observed rather than inferred — a saved transcript from an unattended replay
   shows `Speaker 1..k`.
5. Task 7's three numbers exist for both recordings, and the plan's next step is chosen by them and
   written down.
6. Phase 4, if Task 7 says it is worth doing, is on its own branch with a before/after table on both
   recordings — and is *not* merged into whatever Monday's smoke test runs against.

## Run book — the next Windows session starts here

Everything below is written but **unrun**: no test has executed, the bench has never run, and the
PowerShell was never even syntax-checked (no `pwsh` on the machine that wrote it). Expect to fix a typo
before you read a number. Work is on `feature/speaker-attribution`, **uncommitted**.

### 0. Sanity, two minutes

```powershell
dotnet build -t:Rebuild -v:n            # then again with -c Release; both must say 0 Warning(s)
dotnet test                             # no filter. failed: 0, and the bench reported Not Run
```

New tests that must pass, so you know what to look at if one does not:
`SileroVadDetectorStreamPositionTests` (5), `LiveTranscriptionEngineBackpressureTests` (2),
`DiarizationOracleTests` (5) and `BenchEmbeddingCacheTests` (3) — both in `DiarizationOracleTests.cs` —
plus three `SuppressSpeakerLabels_*` and `BuildMarkdown_CarriesNoSpeakerLabel_WhenSuppressed` in
`MeetingAttendeeViewModelTests`, and `Render_VoiceStats_DoNotResurrectALabelTheBubbleSuppressed` in
`DirectTranscriptMarkdownTests`. Twenty in all.

### 1. Tee both recordings (Task 0) — start this first, it is ~80 minutes unattended

```powershell
Get-ChildItem artifacts/meeting_recording          # confirm the exact file names first
$lsp      = 'artifacts/meeting_recording/Hilfesystem LSP - SP-20260615_133248.mp4'
$workshop = 'artifacts/meeting_recording/MMS & PR PRO Workshop-20260721_112529.mp4'

# Workshop first: 16 minutes of audio, so it fails fast if anything is wrong.
./scripts/Invoke-MeetingReplay.ps1 -AudioPath $workshop -RosterSize 10 `
    -RunName workshop-exact -DumpPath artifacts/wav/workshop.wav
./scripts/Invoke-MeetingReplay.ps1 -AudioPath $lsp -RosterSize 5 `
    -RunName lsp-exact -DumpPath artifacts/wav/lsp.wav
```

Roster sizes are humans excluding Pia: **5** for LSP, **10** for the workshop. Each run prints its log
path and a `LABEL CHECK:` line — that line is Task 2, and it is the first time anything has looked at
the rendered numbering.

### 2. Re-score those runs at exact alignment (Tasks 1 + 5)

```powershell
./scripts/Measure-SpeakerAttribution.ps1 -LogPath <log from step 1> `
    -ReferencePath scripts/speaker-reference/lsp.reference.json `
    -NameMapPath scripts/speaker-reference/lsp.names.local.json
```

Expect the header to read `Align : exact — every segment reported its own stream position`. If it still
prints a fitted offset, the log has no `start=` field and step 1 ran an old build.

**What to compare against.** The end-state LSP run measured 92.1 % by segment / 93.5 % by duration with
a fitted alignment. Exact alignment should land inside the range the three fits bracketed (92.1–92.3 %).
If it lands outside, the fitting was wrong in a way worth understanding before trusting either number —
say so rather than quietly adopting the new figure.

Also grep both logs for the thing that has never been visible:

```powershell
Select-String -Path <log> -Pattern 'falling behind|to transcription backpressure'
```

Any hit means the fixture has been losing voice evidence to transcription lag all along, and every
label count in the measurements doc is a lower bound on what the diarizer should have seen.

### 3. Bench the same recording (Task 4)

```powershell
$env:PIA_BENCH_WAV       = 'artifacts/wav/lsp.wav'
$env:PIA_BENCH_ROSTER    = '5'
$env:PIA_BENCH_REFERENCE = 'scripts/speaker-reference/lsp.reference.json'
$env:PIA_BENCH_OUT       = 'artifacts/wav/bench'
dotnet test -- --explicit only --filter-method "*Bench_MeasuresARecording*"
```

The first run is cold: it loads the CAM++ model and computes every embedding, so give it minutes. It is
also the first time **any** test has loaded a sherpa native from the test output — the existing VAD test
passes an empty model path and the VAD discards it. If construction fails, add a direct
`PackageReference Include="org.k2fsa.sherpa.onnx"` to `tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`.

Outputs land in `PIA_BENCH_OUT`: `*.report.txt`, `*.segments.jsonl`, `*.passes.log`,
`*.embeddings.bin`. Score the segments with the same metric:

```powershell
./scripts/Measure-SpeakerAttribution.ps1 -SegmentsPath artifacts/wav/bench/lsp.segments.jsonl `
    -ReferencePath scripts/speaker-reference/lsp.reference.json
```

**Task 4's acceptance, and it gates everything after it:** against the app replay from step 1 the bench
must land within **2 % on segment count**, on the **same distinct-label count** in the final state, and
within **2 points on attribution**. Wider than that and the bench is measuring a different pipeline —
reconcile it before quoting any oracle number, because they all rest on it. The known-legitimate
differences are listed in `DiarizationBench`'s own summary: no transcription means no backpressure
drops and no segment discarded for empty text, and the 30 s pass trigger fires on stream time here.

Then re-run the same command to confirm the cache works: second run should report
`0 computed this run` and reproduce the numbers exactly.

### 4. Read the answer (Task 7)

The bench report ends with the two oracle lines. Read `ORACLE enrollment (30 s/speaker)` against the
live figure from step 2 using the table in Task 7 — ≥ 97 % puts the whole gap in the matching policy,
< 88 % makes CAM++ `zh_en` the ceiling and Task 8 mostly wasted effort. **Write the number and the
decision into `docs/reviews/2026-08-21-speaker-attribution-measurements.md` before doing anything with
it**, because that is the document the next plan will be argued from.

Run it for the workshop recording too. Its reference is the weak one (95 s lost to a layout change,
191 s to silence), so treat a disagreement between the two recordings as a question about the fixture,
not about the model.

### 5. Do not start Task 8 before Monday's meeting

Phase 4 changes which voice gets which label. If it is in the build Monday, the smoke test cannot tell
you which change caused what it shows.

## Monday's smoke test — the checklist this deadline exists for

One real Teams meeting, and it should collect everything a live meeting is the only source of. Set
`PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP` and **no** replay path: the same run then also produces the
loopback capture that answers how optimistic the whole `artifacts/` fixture is.

1. Join a real meeting with 3+ people who will actually talk. Let it run 15+ minutes.
2. Watch the bubbles: labels should be `Speaker 1`…`Speaker k` with no gaps and no number above the
   headcount. A gap is Task 5's renumbering failing on real data.
3. Rename one speaker and confirm the rename sticks across the next re-cluster pass.
4. Click **Save**. Check the vault document: `speakers:` and `voiceStats:` must name the same set.
5. Summarize, and check the summary's speaker references against who actually spoke.
6. Afterwards, from the log: the roster size `TeamsMeetingSession` actually reported (every number in
   the measurements doc came from an env-var roster), the dropped-segment count from Task 3, and the
   final label count against the real headcount.
7. Keep the dumped WAV. Replay it through the bench and compare against the cloud-mixed recordings —
   that delta is the fixture's honesty coefficient, and it has never been measured.

## Decisions taken (2026-08-22)

**The unlabelled-transcript switch: build it, default off** — with a disclaimer in the transcript area
stating what speaker attribution actually is. Both are done (Task 14). The switch hides every diarized
label, including a renamed one: a rename names a cluster the diarizer built, so it is no more verified
than `Speaker 1`. Bubble grouping and colour stay — a colour makes a weaker claim than a name, and
flattening it would cost readability for no honesty gained.

**Scope for the weekend: through Task 7.** Tasks 8+ wait for Monday.

## Status, 2026-08-22

| # | Task | State |
|---|---|---|
| 0 | Tee both recordings to WAV | **needs Windows** — nothing else in Phase 2 can run until this exists |
| 1 | Exact stream offsets | done: `VadSegment.StartSample`, `start=` on the identify line, 5 tests |
| 2 | Replay script reads the numbering back | done, **as an on-screen read rather than an export check** — see below |
| 3 | Segment drops made visible | done, and the existing warning turned out to be unreachable — see below |
| 4 | Offline diarization bench | done (`DiarizationBench`, `[BenchFact]`), unrun: needs Task 0 |
| 5 | Exact-time + bench scoring in the metric | done (`-SegmentsPath`, exact alignment), unrun on Windows |
| 6 | Embedding cache | done (`EmbeddingCache`, keyed by stream position). **Sweep driver deliberately not built** — see below |
| 7 | Oracle-enrollment upper bound | done (`DiarizationOracle`), arithmetic unit-tested, unrun on a recording |
| 14 | Unlabelled-transcript switch + capability disclaimer | done |

### Where this diverged from the plan

| Planned | What happened |
|---|---|
| Task 2 verifies the numbering from a **saved export** | It reads the **rendered** labels out of the UIA tree instead. Both Save paths need a dialog (a Win32 file dialog, or the vault's title dialog), which an unattended script should not have to drive on a German-locale desktop. The export path is already covered by `BuildMarkdown_EmitsDisplayLabels`; the *rendering* had no coverage at all, and that is the claim the measurements doc calls unobserved. Strictly better evidence, so the substitution is deliberate. |
| Task 3 counts drops on the existing "falling behind" warning | **That warning is unreachable.** The queue is `BoundedChannelFullMode.DropOldest`, under which `TryWrite` never fails — it evicts silently and returns true. So the loss has never been logged, not once, and every "no drops observed" reading to date means nothing. The counter now hangs off the channel's own `itemDropped` callback, which changes no behaviour: the same segment is still the one discarded. |
| Task 6 ships a sweep driver | Not built, and it would have been theatre: every knob the threshold work needs (`MatchSimilarityMin/Max`, `MinClusterSegmentSeconds`, the `1 − cut` derivation) is an `internal const`. Making them injectable *is* Task 8, which is after Monday. The cache is the part that makes a sweep cheap, and it exists. |
| Task 7 needs embeddings from the service | The service computes them internally and returns only a label, so the bench reads them back out of the cache after the run. Working as intended, but it means the oracle is only available when the cache is writable. |

### What is measured and what is not

Everything above compiles clean in Debug and Release, and the new unit tests are written — but they have
**not been run**: `dotnet test` needs Windows, and this work was done on macOS. The bench and the metric
script have never executed at all. Neither has the PowerShell: there is no `pwsh` on the machine that
wrote it. First Windows run should therefore expect to fix a typo, not to read a number.

Two things to check early on Windows, both cheap and both able to invalidate a day's work:

1. `dotnet test` with no filter, `failed: 0`, and the bench still reported as `Not Run`.
2. That the sherpa **native** runtime reaches the test output. Nothing in the suite has ever loaded
   one — `SileroVadDetectorSpeechEventsTests` passes an empty model path and the VAD discards it — so a
   cold-cache bench run is the first time it is exercised from the test host. If it fails, add a direct
   `PackageReference` to the test project.
