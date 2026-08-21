# Assessment: meeting speaker attribution after the roster-ceiling change

Date: 2026-08-21. Evidence: the 4-participant Teams meeting of 2026-08-21 14:39–15:01
(Whisper medium, smart auto-detect), its saved transcript, and `pia-2026-08-21.log`, which recorded
all 21 re-cluster passes and every VAD segment boundary.

Follows up on `docs/reviews/2026-08-12-speaker-detection-review.md` and commit `9ace88af`
("Cap speaker detection with what the roster already knows", 2026-08-13), whose stated acceptance bar
was "final distinct labels ≤ 5 (ideally 4)".

## What the run actually did

| Measurement | Value |
|---|---|
| VAD segments emitted | 114, 541.9 s of speech, median 3392 ms, max 17248 ms |
| < 1.5 s — no embedding, never reach the diarizer | 15 |
| 1.5–2.0 s — embedded, but excluded from clustering | **12** |
| ≥ 2.0 s — embedded and clustered | 87 |
| `expected=` on every pass | **4** (the roster ceiling was live all meeting) |
| Clusters per pass | 4 → 5 → 3 → 5 → **4**; never above the cap |
| Distinct labels in `_labelByCluster`, final pass | **11** |
| `_speakerCounter` reached | **17** |
| Distinct labels in the saved transcript | 12 |

The three duration buckets reconcile exactly with the pass log — the final pass reports
`87/99 segments`, and 114 − 15 = 99, 99 − 12 = 87 — so the reading below is arithmetically anchored,
not inferred.

## Diagnosis

### Not the problem: the clusterer, the roster ceiling, or the segmentation

Three things I expected to be broken are measurably fine, and it is worth being explicit because two
of them are where a fix would naturally be aimed.

**The clusterer converged.** `expected=4` was pushed on every pass and no pass ever reported more
than 5 clusters; the final pass found exactly 4. Reverting `9ace88af` would not help.

**Segmentation was clean, including through the consent round.** The four consent sentences plus
Marco's reply came through as **five separate segments** — 5824, 5632, 5408, 6176 and 6432 ms — each
closed on silence with a 1.2–3.0 s gap before the next opened. Nobody's sentence was glued to
anyone else's. Where the transcript shows two people inside one bubble, that is the 25 s bubble
merger combining consecutive utterances that carry **the same label**: a mislabeling, downstream of
correct audio boundaries. (Unhandled overlap and the 17.2 s maximum segment are still real
weaknesses — they were simply not the failure here.)

So the four-way enrollment round did not fail because the audio was cut wrong. It failed because
three of the four voices were assigned to one cluster.

### Defect 1 — the online threshold is derived from the offline cut, and it swung across its entire range

`_matchSimilarity = clamp(1 − cutDistance, 0.40, 0.60)`. Every pass retunes the live matcher from the
dendrogram's cut, and the live matcher's decisions then become the next pass's input. That loop has
no damping, and over one meeting it traversed the whole permitted interval:

| Passes | Cut | Derived threshold | Clusters | Consequence |
|---|---|---|---|---|
| 1–10 | 0.32 → 0.41 | pinned at **0.60** (strictest) | 4 → 5 | a real voice fails to match and mints a new label |
| 11–18 | 0.41 ↔ 0.56, oscillating | 0.44 ↔ 0.59 | 3 → 5 | churn; pass 11 over-merges *below* the roster count |
| 19–21 | 0.63 | pinned at **0.40** (most permissive) | 4 | distinct voices glue onto the dominant speaker |

Both user-visible symptoms come out of this one mechanism, in sequence. The strict phase manufactured
spurious speakers; the permissive phase merged the real ones. At a 0.40 cosine threshold, two
different German male voices over the Teams codec match comfortably — which is why `Speaker 1`
absorbed every long turn, and why Marco's, Alf's and Michael's consent sentences ended up sharing a
label while Martin kept his own.

The roster ceiling participates in the drift. The cap can only bind by selecting a *coarser*
candidate cut, and a coarser cut mechanically lowers the derived threshold. Passes 7–18 sat at
exactly 5 clusters — the cap value — while the cut climbed 0.38 → 0.56. So capping the count loosened
the matcher. (The force-merge branch was deliberately exempted from raising the reported cut in
`9ace88af`; the candidate-rescan branch was not.)

### Defect 2 — a 500 ms gap between two constants mints permanent, uncorrectable speakers

Two duration floors disagree:

- `LiveTranscriptionEngineService.cs:47` — embed at **≥ 1.5 s** (`16000 * 3 / 2`).
- `AdaptiveSpeakerIdentificationService.cs:30` — cluster at **≥ 2.0 s** (`MinClusterSegmentSeconds`),
  introduced by `9ace88af`.

A segment in between gets an embedding and takes the instant provisional path
(`AdaptiveSpeakerIdentificationService.cs:136`), which mints `Speaker ++_speakerCounter` on a missed
match. That label is then permanent by construction: excluded from the dendrogram by the 2.0 s floor,
carried over verbatim each pass as a `gatedCluster` (`:260`), skipped by orphan recycling (`:275`),
and never subject to `_expectedSpeakers` — the ceiling only ever reaches `_clusterer.Cluster(...)`.
`Speaker 17` exceeding even `SpeakerClusterer.MaxClusters = 12` proves no cap applies on that path.

The residue is measurable: the final pass found 4 clusters and `_labelByCluster` held 11, so **7
gated-only clusters** survived with nothing but sub-floor segments pointing at them. (Not every
1.5–2.0 s segment misses the threshold, and ≥ 2 s segments mint on the provisional path too — which
is the rest of how the counter reached 17.)

`9ace88af` moved the noisiest segments *out* of the capped, self-healing path and *into* the uncapped,
never-corrected one — the cap was applied to the population that was already converging and withdrawn
from the one causing the symptom.

### Defect 3 — the visible number is a mint counter, and stale labels survive in the transcript

`Speaker N` is `_speakerCounter`, which only grows, so a correct 4-cluster result still displays as
`Speaker 17`. This is the loudest symptom and the cheapest fix — renumber to 1..k in first-appearance
order at render time.

One caveat found while checking it: the saved transcript contains **`Speaker 16`, which is absent from
the final pass's label set** (it disappears between passes 18 and 19). At least one bubble kept a
label whose cluster had already been recycled, so retro-reassignment did not reach it. Worth a look on
its own, and it is a precondition for the renumbering fix, which assumes the render path reflects
final cluster state.

### Defect 4 — embedding discriminability

Now promoted from "contributing factor". With segmentation exonerated, the reason a 0.40–0.60
threshold cannot separate four speakers is that the embeddings themselves are not far enough apart.
`3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` (`LiveTranscriptionModels.cs:35`) is
CAM++ trained on Chinese + English; the meeting is German, over the Teams Opus codec with AGC and
noise suppression. Still unverified — but it is now the plausible primary cause rather than a footnote,
and the sensitivity of everything downstream to a 0.20-wide threshold band is the symptom of a
cramped embedding space.

## Why the gate stayed green

`dotnet test` is at `failed: 0`, and two tests assert the exact mechanism that produced `Speaker 17`:

- `Pass_ExcludesSubFloorSegments_ButTheyKeepTheirLabels`
- `Pass_CarriesOverAClusterOnlyReferencedBySubFloorSegments`

Nothing anywhere asserts an *outcome* — no test says "4 voices in, ≤ 5 labels out" on realistic audio.
The suite verifies that the code does what it was written to do, which was never in doubt. That is how
a change shipped green, satisfied its own review, and still missed its acceptance bar by 12 labels.

## Separate finding: the consent round had no effect in this mode

Worth knowing independently of accuracy, because it was described to four colleagues on the record.

The per-speaker spoken-consent gate — `ConsentForwardLoop`, `INamedConsentClassifier`, the blocklist
filter, the evidence store — exists only in `DirectTranscriptionService` (mic + loopback mode).
`MeetingAttendeeService` and the whole `Services/MeetingAttendee/` folder contain no consent code at
all; the Teams path's only gate is a one-time operator checkbox before joining
(`MeetingAttendee_Consent_Label`: "I confirm I'm allowed to have an assistant join and transcribe this
meeting").

So in this run nothing was filtered by voice profile and no speaker was excluded for lacking a consent
sentence. Verbal consent was given and is in the transcript, so nothing improper happened — but the
mechanism described in the meeting ("if that sentence does not come, that voice profile simply is not
transcribed") was not the one running. Either wire the gate into the attendee path or stop describing
it as active there.

## Recommendation

### 0. Build the fixture first. Nothing else can be evaluated without it.

Two 4-participant recordings and now a live meeting have been spent measuring by eye, and the last
attempt moved the metric the wrong way without anyone being able to tell until after the meeting.
Most of the machinery exists: `PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE` already replays a WAV through
the real attendee path with a no-op session. What is missing is a way to *produce* the WAV — audio is
discarded after transcription by design.

- Add a dev-only WAV dump env var in the loopback capture path, symmetric to the existing replay var.
- Capture one real 4-person meeting once, with consent, and hand-label the speaker timeline.
- Metric: distinct-label count vs truth, plus per-segment attribution accuracy. Full DER is overkill.
- Then make it a test. An outcome assertion on a fixture is what the suite has never had.

This is also the only way to answer "is it 80% wrong?" with a number instead of an impression.

### 1. Fix Defects 2 and 3 now — small, self-contained, and most of the visible damage

- **A sub-floor segment must never mint a cluster.** It takes its best existing match, or an
  inherited/null label. This is the one rule that closes the mint path while keeping the 2.0 s floor's
  benefit. Do *not* instead delete `MinClusterSegmentSeconds` — that is a revert to the behaviour that
  produced 7–8 speakers.
- **Apply the roster ceiling to the provisional path too**, not only to `_clusterer.Cluster`. A voice
  that would be the 6th label when the roster says 4 should take its best match, exactly as the manual
  service already does at its cap.
- **Renumber labels to 1..k at render time** — after resolving why `Speaker 16` outlived its cluster.
- Invert the two tests above rather than keeping them.

Expected effect on this recording: 11 labels → roughly 4–5, numbered 1–5. It makes the transcript
plausible. It does **not** fix who said what — Defect 1 and Defect 4 own that.

### 2. Break the threshold feedback loop

Defect 1 is the one that decides accuracy, and it is a design issue rather than an oversight: one
scalar derived from the dendrogram drives the online matcher, so every cut movement retunes matching
and every matching error reshapes the next cut. Options, cheapest first:

- Stop deriving the online threshold from the cut at all — hold it fixed, or move it only with heavy
  damping. The clamp bounds the swing but does nothing to stabilise it.
- Decide k rather than infer it. With the roster known, `k = 4` is a far stronger prior than
  "largest gap, capped at 5" — and it removes the mechanism by which capping loosens the matcher.

### 3. Then evaluate the pipeline swap and the embedding model together

`org.k2fsa.sherpa.onnx` 1.12.40 — already referenced — ships a full diarization pipeline (verified by
reflecting the shipped assembly):

| Type | What it gives you |
|---|---|
| `FastClusteringConfig.NumClusters` | pin k to the roster count instead of inferring it. The main prize: it removes Defect 1's inference step entirely. |
| `OfflineSpeakerDiarizationConfig.Embedding` | the same `SpeakerEmbeddingExtractorConfig` already in use, so a model A/B (TitaNet / VoxCeleb ResNet vs CAM++ zh_en) is a one-line change — the Defect 4 experiment. |
| `OfflineSpeakerSegmentationPyannoteModelConfig` | pyannote frame-level segmentation. Not needed for the consent-round failure, but it is the only thing here that addresses overlap and the 17 s segments. |
| `MinDurationOn` / `MinDurationOff` | tunable segmentation hysteresis, replacing the 512 ms energy hangover. |
| `Process(float[])` → `{Start, End, Speaker}[]` | a speaker timeline to re-attribute a transcript against. |

`SileroVadModelConfig` and `TenVadModelConfig` are in the same assembly, which retires the reason
Silero was abandoned: that failure was a `Microsoft.ML.OnnxRuntime` binding problem
(`prob ≈ 0.0005` for every window), not the model. Sherpa's own VAD sidesteps the binding, and
`silero_vad.onnx` is already downloaded.

Adopting it deletes `SpeakerClusterer`, most of `AdaptiveSpeakerIdentificationService`, and the energy
VAD in favour of a maintained implementation — consistent with preferring light dependencies, since
this one is already present. New cost: one pyannote model (~6 MB).

**The trade-off to decide.** `OfflineSpeakerDiarization` is batch — it wants the meeting's audio, not
a stream:

- **(a) Rolling window** — diarize the last 30–60 s repeatedly. Bounded retention; attribution near
  window edges stays imperfect.
- **(b) Buffer the meeting, diarize at the end, re-attribute the saved transcript.** Best accuracy.
  16 kHz mono float32 is ~64 KB/s, so 21 minutes is ~80 MB, in memory only, wiped on stop. Live
  bubbles keep provisional labels; the saved and summarized transcript gets accurate ones.

(b) is the better product but it changes a privacy claim already stated out loud — that audio is
discarded the moment it is transcribed. Nothing would reach disk, but audio would persist in memory
for the meeting's duration. That is a deliberate call, and if taken the wording must change with it.

### 4. The two accuracy multipliers, after step 0 exists

- **Consent-phase enrollment.** Each participant already states a named sentence and the roster
  already supplies the names — and the segmentation evidence above says those five sentences arrive as
  five clean segments, so the enrollment set is *already there and usable today*. That converts
  unknown-k diarization into 4-way verification with a reject option: a much easier problem, fully
  local, no Teams dependency, and it yields real names instead of `Speaker N`. Given that segmentation
  is not the blocker, this is now the strongest single lever available.
- **Teams DOM active-speaker signal.** `TeamsMeetingSession.cs:110` filters out a `voice-level`
  `data-tid` as noise. If it carries a live per-participant speaking indicator it is ground truth with
  names. Two checks before trusting it: whether the signal exists in the configuration Pia actually
  runs (it appears in the *fallback* on-stage-tile selector, not the People-panel path used for names,
  and tiles are virtualized so camera-off participants may not render); and the DOM-to-audio latency
  offset — loopback capture is buffered, and if the DOM lags by a few hundred ms, attribution breaks
  exactly at turn boundaries, which is where the errors already are. Measure it, do not assume zero.

## Interim: wrong labels are worse than no labels

While attribution is unreliable it should not be presented as reliable, because the error propagates
rather than staying cosmetic. Here `Speaker 1` absorbed every long turn, and the summary consequently
assigned all five action items to Marco Altmann, including ones he did not raise. A transcript with no
speaker labels would have produced a more truthful summary than one with confident wrong ones.

Suggested: a switch that ships the transcript unlabelled (or labelled only where confidence is high),
default on until the fixture shows a number worth trusting. Transcription quality itself is not in
question — the text is good, and that is the harder half of the problem already solved.
