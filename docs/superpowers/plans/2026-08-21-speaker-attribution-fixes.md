# Plan: speaker-attribution fixes that are verifiable today

Date: 2026-08-21. Diagnosis: `docs/reviews/2026-08-21-speaker-attribution-assessment.md`.
Supersedes the open items of `docs/reviews/2026-08-12-speaker-detection-review.md`.

**Revised 2026-08-21 (later the same day).** Two real Teams cloud recordings were added to
`artifacts/meeting_recording/`, which removes this plan's original blocker. The audio is **AAC 16 kHz
mono**, decoding through Media Foundation to **PCM 16 kHz mono** — the pipeline's native format, so
both replay through the existing `PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE` path with **zero new code**.
Better still, Teams burns a **per-participant active-speaker highlight** into the video (a blue pill
behind the name label, verified moving between participants), so a reference speaker timeline is
machine-extractable rather than hand-labelled. Task 1 is therefore replaced: it is now the fixture
harness, not a capture tee.

| Recording | Length | Participants | Notes |
|---|---|---|---|
| `Hilfesystem LSP - SP-20260615_133248` | 49:57 | 5 (Marco Altmann, Andreas Auerswald, Alexander Freund, Martin H…, Dirk Erb…) | 3 cameras on, large name-labelled tiles; long and realistic |
| `MMS & PR PRO Workshop-20260721_112529` | 16:04 | 10, mostly muted | Screen share + 2×5 avatar grid; the "roster ≫ talkers" case the ceiling must not inflate |

## Out of scope, and why

| Deferred | Why not now |
|---|---|
| **The cut → threshold feedback loop.** `_matchSimilarity = clamp(1 − cut, 0.40, 0.60)` is recomputed every pass, so matching and clustering feed each other undamped; it traversed the whole band in one meeting (0.60 pinned for passes 1–10, 0.40 pinned for 19–21). This is the *primary* accuracy defect. | **Unblocked by Task 1, but still not in this plan.** It changes *accuracy*, so it must be measured against the harness — not shipped alongside it. Do Tasks 1–6, get a baseline number, then open a separate plan. Changing matching policy in the same pass that first establishes the metric is how `9ace88af` happened. |
| Embedding model A/B (CAM++ `zh_en` → TitaNet / VoxCeleb) | Unblocked by Task 1. First real experiment to run once a baseline exists; it needs no app change beyond a model path. |
| Migration to `SherpaOnnx.OfflineSpeakerDiarization` (`FastClusteringConfig.NumClusters`, pyannote segmentation) | Still needs the batch-vs-streaming retention decision, which is an owner call, not a fix. |
| Consent-phase enrollment (named verification) | Design work. Note neither recording contains a consent round, so validating it needs a third recording. |
| Teams DOM `voice-level` active-speaker signal | Still needs a live meeting: the recordings prove the *rendered* indicator exists, not that the live DOM exposes it in the configuration Pia runs, nor the DOM-to-audio offset. |
| Lowering `MaxSegmentSamples` to split 17 s segments | Unblocked by Task 1; trades diarization granularity against Whisper accuracy, so it needs the metric to arbitrate. |
| Wiring the per-speaker consent gate into the Teams path | A feature, not a fix. (The in-app checkbox text is accurate; if `Pia.Docs` claims per-speaker consent for the attendee path, correct it there — separate repo, follow-up.) |
| Dev-only WAV capture tee (the original Task 1) | Demoted, not dropped — see Task 7. The recordings are cloud-mixed, not loopback-captured, so it remains the only way to measure the exact stream Pia hears. |

## Sequencing

Strict order. Task 5 is hard-gated on Task 3.

| # | Task | Verifiable by |
|---|---|---|
| 1 | Fixture harness: replay + reference extraction + metric | Manual, then a scripted report |
| 2 | Service+VM invariant test, and the stale-label diagnosis it produces | `dotnet test` |
| 3 | A sub-floor segment must never define a cluster | `dotnet test` |
| 4 | Roster ceiling on the provisional path | `dotnet test` |
| 5 | Display renumbering to 1..k — **only after Task 2 is green** | `dotnet test` |
| 6 | Outcome-level regression suite | `dotnet test` |
| 7 | Dev-only WAV capture tee (loopback fidelity) | Manual: dump a session, replay it |
| — | Optional: unlabelled-transcript switch (owner decision) | `dotnet test` |

---

## Task 1 — Fixture harness: replay, reference, metric

**Goal.** Turn the two recordings into a repeatable measurement. Three pieces, none of which touches
production code paths.

### 1a. Replay (works today, no code)

`PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE=<path to the .mp4>` already routes
`DebugFileAudioCaptureService` (Media Foundation extracts the audio track from video) into the real
engine, VAD, diarizer and VM. Verified: both files decode to PCM 16 kHz mono, so
`AudioHopResampler` is a pass-through and no resampling artifact is introduced.

Run each recording once and keep the artifacts:

- The saved transcript (Pia's label timeline).
- `pia-<date>.log`, which already carries everything needed: `Adaptive pass:` (eligible/total,
  clusters, cut, expected), `Adaptive pass labels: [...]`, and `VAD segment CLOSED … duration=Nms`.

**One real gap to close first.** `DebugNoOpMeetingSession.GetAttendeeNamesAsync` returns an **empty
roster**, so `AccumulateAttendees` never fires and `MeetingAttendeeService.cs:601` never calls
`SetExpectedSpeakers` — the roster ceiling is **off** during replay, i.e. replay does not exercise the
shipping configuration. Fix: a `PIA_DEBUG_MEETING_ATTENDEE_ROSTER` env var (semicolon-separated names)
that `DebugNoOpMeetingSession` returns from `GetAttendeeNamesAsync`. ~10 lines, `#if DEBUG`, and it
makes the replay honest. Without it every replay measures `expected=0`.

### 1b. Reference timeline from the burned-in indicator

Teams renders a blue pill behind the *speaking* participant's name label, per participant, in both
recordings. Extract it with a script (not app code) under `scripts/`:

- Read the tile→name mapping **once per recording, by hand**, from a single frame; store it as a small
  JSON alongside the reference. Positions are fixed per layout, and the two recordings have different
  layouts, so this is per-recording config, not a general solution.
- Sample frames at 4–10 Hz (`ffmpeg -vf fps=…`), crop each tile's name-label rect, and classify the
  pill by mean colour — blue pill vs dark background. No OCR needed for detection.
- Emit `{start, end, speaker}` intervals per participant, merging gaps under ~250 ms.

Three caveats to encode in the script's output, not discover later:

1. **The indicator lags.** It attacks slightly late and lingers after speech stops, so boundaries are
   approximate. Score per-segment attribution accuracy (does Pia's label for this VAD segment match
   the reference speaker covering its midpoint?) rather than strict DER, which would be dominated by
   boundary error that is the *indicator's*, not Pia's.
2. **Layout reflow.** A join/leave mid-meeting shifts tile positions. Detect it — assert the tile grid
   is stable by checking the non-speaking label pixels stay put — and fail loudly rather than silently
   mislabelling.
3. **Simultaneous highlights are overlap ground truth.** Keep them; do not collapse to one speaker.

### 1c. Metric

A small script that joins the log's VAD segment boundaries against the reference and reports:

- distinct labels ever registered, and at the end, vs. the true talker count;
- per-segment attribution accuracy, and a confusion matrix over reference speakers;
- the share of speech in segments whose reference is ambiguous (overlap), reported separately so it
  cannot flatter or damn the result.

**Privacy.** These are real internal/customer meetings, and names are visible in the video.
`artifacts/` is already gitignored — verified. Commit **only** the derived reference with speakers
anonymised (`A`, `B`, `C`), keeping the name mapping in a local-only sidecar. Never commit audio,
video, or frames.

**Acceptance.** Both recordings replay end to end; a reference timeline exists for each; the metric
script prints a baseline for `HEAD`. That baseline number is the deliverable — every later task is
measured against it.

---

## Task 2 — Service + VM invariant test, and the stale-label diagnosis

**This is the most valuable item in the plan.** No test today spans
`AdaptiveSpeakerIdentificationService` and `TranscriptOverlayViewModel`; each is tested against its
own contract, which is why the suite could stay green while the pair produced 11 labels for 4
clusters.

**The invariant.** After any re-cluster pass has been applied, no bubble may carry a speaker label
that is absent from the service's live label set. Violated today: the saved transcript contains
`Speaker 16`, which disappears from `Adaptive pass labels:` between passes 18 and 19.

**Steps.**

1. Expose the live label set for assertion. Add to `AdaptiveSpeakerIdentificationService` an
   `internal IReadOnlyCollection<string> KnownLabels` returning `_labelByCluster.Values` under the
   lock. Internal, not public — the interface stays unchanged.
2. Build the pair harness in `tests/…/ViewModels/MeetingAttendeeViewModelTests.cs` (its existing
   fakes already cover the VM's ctor): drive utterances through the real adaptive service, forward
   `SpeakersReassigned` into `ApplyReassignments`, then assert the invariant over `Bubbles`.
3. Reproduce with the real pass sequence. `RecordingClusterer.Scripted` (already in
   `AdaptiveSpeakerIdentificationServiceTests`) accepts scripted `ClusterResult`s, so replay the
   observed sequence from `pia-2026-08-21.log` — cluster counts
   `4,4,4,4,4,4,5,5,5,5,3,5,5,5,5,5,5,5,4,4,4` with cuts `0.32 … 0.63` — over segments whose
   durations mirror the real mix (87 at ≥ 2 s, 12 at 1.5–2.0 s, 15 below 1.5 s).

   **The counts alone will not reproduce it.** `Speaker 16` was lost to *identity* churn, not to a
   count change: `ClusterResult.AssignmentPerSegment` drives the greedy overlap match that decides
   which stable cluster id each new cluster inherits, and that is where a label is orphaned or
   recycled. Script the **assignment arrays** so a cluster's membership shifts between two passes
   while the count stays put — a count-only script will show the invariant holding and lead to the
   wrong conclusion that there is no bug.

**Cheapest hypothesis, check it first.** Reassignments are only emitted for segments the pass
*iterated*, and gated (sub-floor) segments are never iterated (`RunPassUnderLock` walks
`journalIndex` only). If `Speaker 16`'s last surviving member was sub-floor, no reassignment could
ever be emitted for it and the bubble keeps the label forever. If that is the mechanism, **Task 3
fixes this as a side effect** — confirm before writing any separate fix.

Other candidates, in order: `ApplyReassignments` skips journal entries with a null `SegmentId`
(`TranscriptOverlayViewModel.cs:302`), which is every segment under 1.5 s; and `GetOrCreateBubble`
label inheritance can carry a label onto a null-label utterance that no reassignment will ever
revisit. (`JournalCap = 1000` against 114 utterances rules out journal eviction.)

**Acceptance.** The invariant test fails on `HEAD` and passes after Task 3. Keep it permanently — it
is the structural answer to "the suite verifies the code does what it was written to do".

---

## Task 3 — A sub-floor segment must never define a cluster

**Goal.** Close the label-minting path that produced 7 gated-only clusters and drove
`_speakerCounter` to 17.

**Root cause.** Two floors disagree: the engine embeds at ≥ 1.5 s
(`LiveTranscriptionEngineService.cs:47`, `16000 * 3 / 2`) while clustering floors at ≥ 2.0 s
(`AdaptiveSpeakerIdentificationService.cs:30`). A segment in between reaches the provisional path
(`:136`), mints `Speaker ++_speakerCounter`, and that label is then unreachable by every correction
mechanism.

**Do not delete `MinClusterSegmentSeconds`.** Putting 1.5–2.0 s embeddings back into the dendrogram is
a revert to the pre-`9ace88af` behaviour that produced 7–8 speakers.

**The rule — three cases, in `ProcessEmbedding`.**

| Case | Behaviour |
|---|---|
| `durationSeconds >= MinClusterSegmentSeconds` | Unchanged: match at `_matchSimilarity`, else mint; fold into the centroid. |
| Sub-floor, a cluster matches at `bestSim >= _matchSimilarity` | Take that label. Do **not** call `RunningCentroid.Add` — a sub-floor embedding must never move a centroid. |
| Sub-floor, no cluster matches | Return **no label**. Do **not** mint, and do **not** force a nearest match. |
| Sub-floor, no cluster exists at all | Return no label. (Warm-up has not run; there is nothing to match against.) |

The similarity floor in cases 2–3 is the point. Forcing a nearest match unconditionally would trade
"mints a wrong new speaker" for "confidently attributes to a wrong existing one" — harder to notice
and feeding exactly the false-attribution problem the assessment closes on. A nearest-match verdict on
a mostly-silence embedding is noise, and it would also silently override the inheritance behaviour
that already exists for sub-1.5 s segments (the 2026-08-12 review's item 4), which is the right answer
for "ja"/"genau".

**Contract ripple.** "No label" needs `SpeakerSegmentResult.Label` to become `string?`
(`ISpeakerIdentificationService.cs:71`). This is contained: the engine already declares
`string? speakerLabel = null` and only assigns from the result
(`LiveTranscriptionEngineService.cs:155-163`), so a null flows straight into the existing null-label
path — bubble inheritance, then the placeholder at run start. `IdentifyOrRegister` and
`IdentifyOrRegisterWithEmbedding` keep their non-nullable returns; neither has a production consumer
outside `SpeakerIdentificationService`'s own delegation (`:66`, `:70`) and one test, so the manual
service's semantics are untouched. Update the interface's `<summary>` for
`IdentifyOrRegisterSegment` to state when null is returned.

Warm-up is why case 4 exists at all: `EligibleCountUnderLock() >= WarmupSegments` (6) means the first
pass can be minutes in, so during warm-up the only clusters that exist are provisional ones — minting
from a sub-floor embedding there would create a garbage attractor that nothing corrects.

**Consequence — delete the `gatedClusters` carry-over.** Once no cluster can be defined by a sub-floor
segment alone, a cluster whose only members are sub-floor cannot exist, so the carry-over block at
`:260-272` and its exclusion from orphan recycling at `:275` become dead code. Remove them rather than
leaving an unreachable path; that is what actually retires the 7 surviving clusters.

**Tests.**

- `SubFloorSegment_TakesBestMatch_WithoutMintingOrMovingTheCentroid` — a sub-floor segment far from
  every centroid still returns the nearest existing label; the centroid is unchanged (assert via a
  follow-up eligible segment's match outcome).
- `SubFloorSegment_MintsOnlyWhenNoClusterExists`.
- Replace `Pass_CarriesOverAClusterOnlyReferencedBySubFloorSegments` with
  `Pass_HasNoClusterDefinedOnlyBySubFloorSegments` — the assertion inverts.
- Keep `Pass_ExcludesSubFloorSegments_ButTheyKeepTheirLabels`; it is still true, and now trivially so.
- Keep `Pass_SkippedWhileEveryEmbeddingIsSubFloor_KeepingProvisionalState` — warm-up behaviour is
  unchanged.

**Fold in while in these files:** `SileroVadDetector.cs:35` says `// 30 s flush cap` on a
`20 * 16000` value — correct the number. `RunningCentroid._count` is written and never read — delete
it.

---

## Task 4 — Roster ceiling on the provisional path

**Goal.** Make `SetExpectedSpeakers` mean something outside the dendrogram. Today `_expectedSpeakers`
reaches only `_clusterer.Cluster(...)`, so the provisional path is uncapped — `Speaker 17` exceeded
even `SpeakerClusterer.MaxClusters = 12`, which proves no cap applies there.

**Change.** In `ProcessEmbedding`, before minting: if `_expectedSpeakers > 0` and
`_labelByCluster.Count >= _expectedSpeakers + SpeakerClusterer.ExpectedSpeakerSlack` and a cluster
exists, force the best match instead of minting — no centroid update, exactly as
`SpeakerIdentificationService.cs:131-141` already does at its own cap. Reuse
`ExpectedSpeakerSlack` rather than a second constant.

Ceiling only, never a target: at or below the cap, behaviour is byte-identical. A meeting with silent
attendees must not be pulled up toward the roster size — the same reason the symmetric
"closest to expected" form was rejected in the 2026-08-12 review.

**Tests.** `ProvisionalPath_AtTheRosterCeiling_ForcesBestMatchInsteadOfMinting`;
`ProvisionalPath_BelowTheCeiling_IsUnchanged`; `ProvisionalPath_WithNoRoster_IsUnchanged` (count 0).

**Note.** This bounds the label *count*. It does not fix *which* voice gets which label — that is the
deferred threshold work. Do not tune any constant here to chase attribution.

---

## Task 5 — Display renumbering to 1..k

**Hard gate: do not start until Task 2's invariant test is green.** Renumbering assigns sequential
numbers by first appearance, so a stale label would be handed a plausible number and stop looking
absurd — strictly worse than today, where `Speaker 17` is visibly wrong.

**Goal.** `Speaker N` is `_speakerCounter`, which only grows, so even a correct 4-cluster result can
render as `Speaker 17`.

**Where.** In the VM, not the service. Service-side labels are identity: they key the consent map,
the palette (`_speakerColorIndex`), and `Rename`. Do not renumber them.

**Change.**

- Add `[ObservableProperty] private string? _displayLabel;` to `Models/TranscriptBubble.cs`, beside
  `SpeakerLabel`. `SpeakerLabel` keeps its current meaning and stays the `CommandParameter` for
  rename.
- In `TranscriptOverlayViewModel`, hold `Dictionary<string,int> _displayNumberByLabel` and assign on
  first appearance. Populate it in `GetOrCreateBubble`, and clear-and-rebuild it inside
  `RebuildBubblesFromJournal` so incremental and rebuilt paths agree by construction (the same
  discipline that method already documents). `ClearTranscript` clears it.
- Map only auto-generated labels: `Speaker <digits>` → `Speaker <n>`. A user-renamed label passes
  through unchanged. Deriving the pattern from the existing `$"Speaker {…}"` format means a rename to
  something like "Speaker 12" is still matched — acceptable, and `Rename`'s collision guard already
  prevents two identities sharing a display string.
- Switch the display sites only: `MultiBinding` `Binding Path="SpeakerLabel"` → `DisplayLabel` at
  `MeetingAttendeeOverlay.xaml:245,308` and `DirectTranscriptionOverlay.xaml:295,348,412`, and the
  `Resolve` call in `TranscriptOverlayViewModel.cs:559` (`BuildMarkdown`) plus `:132`. Leave every
  `CommandParameter="{Binding …SpeakerLabel}"` alone.

**This also fixes the summary prompt.** `MeetingAttendee_SummaryPrompt_Attendees` tells the model the
transcript uses `"Speaker 1", "Speaker 2", etc.`; today it receives numbers up to 17 against a
4-name roster. `BuildSummaryPrompt` appends `BuildMarkdown()` verbatim
(`MeetingAttendeeViewModel.cs:313`), so renumbering repairs the prompt with no prompt change.

**Tests.** In `MeetingAttendeeViewModelTests`: numbers are assigned in first-appearance order; a gap
in the raw labels (1, 2, 17) renders as (1, 2, 3); a rebuild reproduces the same mapping; a renamed
label is not renumbered; `BuildMarkdown` emits display labels.

---

## Task 6 — Outcome-level regression suite

**Goal.** The suite has never asserted an outcome. Add the one class of test that would have caught
this, using the geometry harness that already exists.

`FakeExtractor` maps a segment's first sample (degrees) to a unit vector on a circle, so cosine
similarity between two voices is exactly `cos(Δθ)` — voice separation is a dial. `Seg(degrees,
seconds)` already encodes duration.

**New file** `tests/…/Services/LiveTranscription/AdaptiveSpeakerOutcomeTests.cs`, using the **real**
`SpeakerClusterer` (not the scripted one — this measures the pipeline, not the plumbing):

- `FourVoices_ProduceAtMostFiveLabels` — four voices at 0/25/50/75°, ~40 segments in realistic
  interleaved order with the observed duration mix (≈ 75 % ≥ 2 s, ≈ 10 % at 1.5–2.0 s, ≈ 15 % below),
  `SetExpectedSpeakers(4)`. Assert: distinct labels ever registered ≤ 5, and distinct labels at the
  end ≤ 4.
- `FourVoices_ShortInterjections_DoNotAddLabels` — the same voices where every interjection is
  sub-floor. This is the direct regression test for the 17-label failure, so it must actually fail on
  `HEAD`: **jitter each sub-floor segment's angle by ±30–40°** so its embedding genuinely misses every
  centroid (cos 25° ≈ 0.91 sails over any threshold in the band, so unjittered short segments of a
  known voice would match on `HEAD` and the test would pass before the fix). The jitter is what
  mostly-silence embeddings do in reality. Assert the label count is unchanged by the interjections.
  Use a fixed, hand-written jitter sequence rather than a random one — the suite must be
  deterministic.
- `OneVoice_StaysOneLabel` — the degenerate case that used to slam the threshold to its strict
  extreme.
- `RosterCeiling_DoesNotInflate` — three voices, `SetExpectedSpeakers(6)`: still 3 labels.

**Explicit limitation, to be stated in a comment in the file:** synthetic geometry proves *bounding
and stability*, never attribution accuracy. Attribution is measured against the Task 1 fixture and
nowhere else. Do not tune thresholds against these numbers.

---

## Task 7 — Dev-only WAV capture tee (loopback fidelity)

Demoted from Task 1, still worth doing. The recordings are **cloud-mixed** Teams audio; Pia captures
**device loopback** of a browser tab. Both are 16 kHz mono post-codec mixes, so the fixture is a good
proxy — but likely a slightly optimistic one (no second D/A→A/D pass, different AGC). This task is the
only way to measure the exact stream Pia hears, and it is also how a *consent-round* recording gets
made for the deferred enrollment work.

**Shape.** A decorator over the existing seam, not a change to any capture service.
`IAudioCaptureSource` (`Services/LiveTranscription/IAudioCaptureSource.cs`) is already 16 kHz mono
float32, and both entry points already accept an injected factory.

**New file** `src/Pia.Wpf/Services/LiveTranscription/DebugWavTeeAudioCaptureSource.cs`, wrapped in
`#if DEBUG` exactly like `DebugFileAudioCaptureService`:

```csharp
#if DEBUG
public sealed class DebugWavTeeAudioCaptureSource : IAudioCaptureSource
{
    // Never drop: a dropped hop would put audio in the WAV that the pipeline never saw, or the
    // reverse. A 32 KB local write is microseconds, so Wait cannot realistically stall capture.
    private const int ChannelCapacity = 200;

    private readonly IAudioCaptureSource _inner;
    private readonly string _path;
    private readonly Channel<float[]> _channel;   // FullMode = Wait
    private WaveFileWriter? _writer;
    private Task? _pump;
    ...
}
```

- `StartAsync` opens the writer with `new WaveFormat(16000, 16, 1)` (16-bit PCM, not IEEE float —
  `MediaFoundationReader` in the replay path reads it without question), starts `_inner`, then starts
  a pump that reads `_inner.Reader`, writes the hop, and forwards it.
- `StopAsync`/`DisposeAsync` flush and dispose the writer **before** disposing `_inner`, and are
  idempotent.
- `SampleRate`/`IsRunning` delegate to `_inner`; `Reader` is the tee's own channel.

**Wiring.** Two new env vars beside the existing pair in `Bootstrapper.cs:32-33`:

```csharp
public const string DebugMeetingAttendeeAudioDumpEnvVar = "PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP";
public const string DebugDirectTranscriptionAudioDumpEnvVar = "PIA_DEBUG_DIRECT_TRANSCRIPTION_AUDIO_DUMP";
```

In the existing `#if DEBUG` blocks (attendee at `:598`, direct at `:642`), when the dump var is set,
wrap the production source instead of replacing it:

- Attendee — the factory is `Func<IMeetingSession, bool, IAudioCaptureSource>`, and the production
  builder `CreateDefaultAudioSource` (`MeetingAttendeeService.cs:734`) is **`private static`**, so
  Bootstrapper cannot wrap it today. Promote it by adding an `internal static
  CreateDefaultAudioSourceFactory(ILoggerFactory)` that returns that `Func`, mirroring the existing
  `CreateProductionTranscriptionFactory` / `CreateEngineServiceFactory` statics the Bootstrapper
  already calls. Then wrap the `Func`'s result, keeping the `useSilentCapture` argument intact —
  `BrowserAudioCaptureService` and `LoopbackAudioCaptureService` must both be tee-able.
- Direct — wrap `loopbackSourceFactory` and, separately, `micSourceFactory` (two files; the mic
  stream is a different speaker and must not be mixed into one WAV).

**Sharp edge — the factory is called twice.** `MeetingAttendeeService.cs:295` creates the source, and
`:315` re-creates it with `useSilentCapture: false` when silent capture fails. Suffix an instance
ordinal onto the file name (`…-1.wav`, `…-2.wav`) so the second call cannot truncate the first file.
Do not append to one file — the two captures are not contiguous.

**Privacy.** The dump is the most sensitive artifact the app can produce, so: `#if DEBUG` only (the
type does not exist in Release IL), off unless the env var is set, and the destination is the path the
operator chose. Log via `SensitiveDebug` that a dump is active plus the path; log nothing at
`Information`.

**Tests.** `DebugWavTeeAudioCaptureSourceTests`: a fake inner source emitting known hops →
(a) every hop is forwarded in order, (b) the written WAV round-trips to the same samples, (c) double
`StopAsync` is safe, (d) the writer is flushed before the inner source is disposed. The two
Bootstrapper branches are wiring, not logic — no test.

**Acceptance.** Set the dump var, run a meeting, get a WAV; feed that WAV to
`PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE` and the meeting replays. Then compare the Task 1 metric on
loopback audio against the same meeting's cloud recording — that delta is the number this task exists
to produce, and it tells you how optimistic the `artifacts/` fixture is.

---

## Optional — unlabelled-transcript switch (owner decision)

Wrong labels propagate: `Speaker 1` absorbed every long turn, so the summary assigned all five action
items to one person. A `MeetingSuppressSpeakerLabels` setting that renders and exports the transcript
without labels is a small change on top of Task 5 (`DisplayLabel` returns null → `Resolve` falls back
to the counterpart name). **The default is your call**; the argument for defaulting it on is that a
truthful unlabelled summary beats a confident wrong one until the fixture produces a number.

## Global constraints

- **Gate:** `dotnet test`, no filter, `failed: 0`.
- **Warnings:** `dotnet build -t:Rebuild -v:n` in Debug **and** Release, `0 Warning(s)`. Task 1 adds a
  `#if DEBUG` type — check Release specifically for an unused-usings or unreachable-code warning.
- New `.cs` files must be **CRLF**.
- Comment discipline: one short line, only where the *why* is non-obvious; no task/spec citations.
- Privacy: labels via `SensitiveDebug`/`SensitiveInformation` only; the Task 1 WAV path is
  `SensitiveDebug`.
- Nothing in Tasks 2–6 touches `SpeakerClusterer.ChooseCut` or `_matchSimilarity`. If a change starts
  to need that, stop — it belongs to the deferred work.

## Whole-plan acceptance

1. `dotnet test` green; Debug and Release rebuilds at zero warnings.
2. Task 2's invariant test fails on `HEAD` and passes at the end.
3. Task 6's `FourVoices_*` tests fail on `HEAD` and pass at the end.
4. A `HEAD` baseline from Task 1 exists for both recordings, and the end-state run beats it on
   **label count**: ≤ 6 distinct labels numbered from 1 for the 5-talker recording, and no inflation
   toward 10 on the mostly-muted one.
5. **Attribution accuracy is reported, not promised.** Task 1 now makes it measurable, so print the
   number for `HEAD` and for the end state — but nothing in Tasks 2–6 targets it, and any change that
   would move it belongs to the deferred threshold work. If accuracy moves here, that is an unplanned
   side effect worth understanding before merging, not a win to claim.
