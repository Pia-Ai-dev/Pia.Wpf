# Review: Auto Speaker Detection (Teams Meeting Transcription)

Date: 2026-08-12
Trigger: Two test meeting recordings with 4 participants each produced 7–8 detected speakers, and some
audio was labeled with the generic "meeting" placeholder instead of a speaker.

## How the feature works today

Speaker detection is fully local (no Azure/Whisper diarization):

1. **VAD** (Silero) emits speech segments, minimum 0.5 s to transcribe.
2. **Embedding**: a sherpa-onnx CAM++ model (`3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`)
   computes a voice embedding per segment — but only for segments **≥ 1.5 s**
   (`_minDiarizationSamples` in `LiveTranscriptionEngineService`).
3. **Labeling** (smart mode, default — `MeetingSmartSpeakerDetection = true`):
   `AdaptiveSpeakerIdentificationService` gives every segment an instant provisional label from the
   nearest cluster centroid, journals every embedding, and re-clusters the whole meeting every 5
   segments (or after 30 s) via `SpeakerClusterer`. Early mistakes self-heal through
   `SpeakersReassigned` retro-corrections.
4. **Clustering** (`SpeakerClusterer`): average-linkage agglomerative clustering over L2-normalized
   embeddings. The cut is **data-derived**: the largest gap in the dendrogram's merge-distance
   sequence, clamped to the [0.30, 0.70] cosine-distance band (fallback 0.50, hysteresis 0.03 toward
   the previous pass's cluster count, hard cap `MaxClusters = 12`).
5. **Display**: `SpeakerToDisplayNameConverter` resolves mic → "me", labeled → "Speaker N" (or the
   user-renamed name), unlabeled → the counterpart name, which defaults to the localized generic
   placeholder `MeetingAttendee_Speaker_Placeholder` = **"meeting"**.

Key files:

| File | Role |
|---|---|
| `src/Pia.Wpf/Services/LiveTranscription/SpeakerClusterer.cs` | AHC + largest-gap cut (`ChooseCut`, `Cluster`) |
| `src/Pia.Wpf/Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs` | Smart mode: provisional labels, journal, re-cluster passes |
| `src/Pia.Wpf/Services/LiveTranscription/SpeakerIdentificationService.cs` | Manual threshold mode (not used by Teams path when smart mode on) |
| `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs` | Pipeline glue; 1.5 s diarization gate |
| `src/Pia.Wpf/Services/MeetingAttendee/MeetingAttendeeService.cs` | Teams wiring; roster polling; diarizer construction |
| `src/Pia.Wpf/Converters/SpeakerToDisplayNameConverter.cs` | Label resolution incl. "meeting" fallback |
| `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs` | Bubble merging (25 s window, ordinal-equal label) |

## Root causes of the observed symptoms

### 7–8 speakers from 4 participants (over-detection)

1. **The cut has no prior about the speaker count.** `ChooseCut` simply takes the largest gap in the
   merge-distance sequence. Teams codec compression, room echo, and varying mic distances scatter one
   person's embeddings, so a spurious "largest gap" at a low distance splits a single voice into 2–3
   clusters. The only guard is `MaxClusters = 12` — far above the real 4.
2. **Short-segment embeddings are noisy.** 1.5 s is the floor for a usable embedding; borderline
   segments still poison the dendrogram.
3. **Overlap/crosstalk.** A VAD segment containing two simultaneous talkers yields a mixed embedding
   that becomes its own cluster.
4. **Model language fit.** The embedding model is CAM++ **zh_en**-trained; the test meetings were
   German, which plausibly degrades embedding separability further (unverified).

### Generic "meeting" label mixed into the transcript

- Every segment < 1.5 s (interjections like "ja", "genau", laughter) gets **no embedding** →
  `SpeakerLabel = null` → rendered as the generic "meeting" placeholder.
- A null label also **splits** the current bubble run (merge key requires ordinal-equal labels), so
  the transcript alternates Speaker N / meeting bubbles.

## Improvement potential (ranked by expected impact)

1. **Roster-count prior (main fix).** The app already polls the Teams participant roster
   (`PollRosterAsync` → `AccumulateAttendees`; names currently feed only the post-meeting summary).
   Feeding the participant count live into `ChooseCut` lets it prefer the candidate cut whose cluster
   count is closest to the expected count (+1 slack for dial-ins/guests) instead of blindly taking the
   largest gap, with force-merge down to the expected count as a backstop. Directly targets the
   4 → 7–8 failure; small, pure-logic, well-testable change.
2. **Duration weighting + gating.** Journal segment durations alongside embeddings; exclude sub-2 s
   embeddings from the clustering input (they keep provisional labels) and weight the Lance–Williams
   linkage by summed segment duration (WPGMA) so long clean utterances dominate cluster geometry.
3. **Small-cluster merging (optional).** Post-pass, merge clusters with very few segments / little
   total speech (e.g. < 3 segments or < 6 s) into their nearest neighbor — these are almost always
   echo/overlap artifacts.
4. **Short-segment label inheritance (optional).** Assign undiarized < 1.5 s segments to the
   adjacent/previous labeled speaker within the bubble window instead of leaving them null. Kills the
   "meeting" interleaving and the bubble splitting.
5. **Model evaluation (optional, follow-up).** Test an alternative embedding model (e.g. ERes2Net /
   VoxCeleb-trained) against a German recording if items 1–2 don't fully fix over-detection.

## Agreed scope

Selected for implementation: **1 (roster-count prior)** and **2 (duration weighting/gating)**.
Items 3 and 4 are deferred as optional phase 2. Item 5 is a separate experiment if needed.

## Implementation plan

### 1. Roster-count prior

- `SpeakerClusterer.Cluster(...)` / `ChooseCut(...)`: new `expectedSpeakers` parameter (0 = off,
  current behavior). When set: among candidate cuts whose gap is competitive with the best gap
  (within a small slack constant), prefer the cut whose cluster count is closest to
  `expectedSpeakers` (+1 slack); hysteresis toward the previous count still applies on ties. If the
  result still exceeds `expectedSpeakers + 1`, force-merge down (same mechanism as the existing
  `MaxClusters` guard).
- `ISpeakerIdentificationService`: add `SetExpectedSpeakers(int count)` as a default no-op interface
  member (keeps existing test fakes compiling). `AdaptiveSpeakerIdentificationService` overrides it,
  stores it under the lock, and `RunPassUnderLock` passes it to the clusterer. The manual service
  ignores it (threshold model has no count concept).
- `MeetingAttendeeService`: after each `AccumulateAttendees`, push `_attendees.Count` into the
  diarizer. The roster union grows over the meeting, so the prior refines itself every snapshot.
  Roster polling disabled (`MeetingAttendeeRosterSnapshotMinutes <= 0`) → count stays 0 → prior
  silently off.

### 2. Duration weighting + gating

- Journal entries carry `DurationSeconds` (from `samples.Length / sampleRate` — zero extra cost).
- Gating: embeddings below a `MinClusterSegmentSeconds = 2.0` floor are excluded from the clustering
  input; they keep their provisional/previous label.
- Weighting: Lance–Williams linkage uses summed segment durations as cluster weights instead of raw
  member counts.

### 3. Tests (gate: `dotnet test`, failed: 0)

- `SpeakerClustererTests`: prior picks the 4-cluster cut over a larger 7-cluster gap; +1 slack; prior
  off = unchanged behavior; hysteresis interaction; weighted linkage; force-merge with prior.
- `AdaptiveSpeakerIdentificationServiceTests`: `SetExpectedSpeakers` flows into passes; sub-2 s
  segments excluded from clustering but keep labels; duration weighting end-to-end with fake
  extractor.
- `MeetingAttendeeService` state tests: roster accumulation pushes the expected count to the
  diarizer.

### Constraints / notes

- No new settings UI; the prior is driven by the existing roster-snapshot setting. Constants follow
  the existing `internal const` style.
- Changes confined to `SpeakerClusterer.cs`, `AdaptiveSpeakerIdentificationService.cs`,
  `ISpeakerIdentificationService.cs`, `MeetingAttendeeService.cs` (+ tests). The manual diarizer and
  the direct-transcription path stay untouched (adaptive mode is deliberately disabled there because
  retro-reassignment is unsound under the per-speaker consent gate).
- Zero-warning policy: verify with `dotnet build -t:Rebuild -v:n` in Debug and Release.
- Privacy discipline unchanged: embeddings/centroids stay in-memory, actively zeroed on
  Reset/Dispose/journal eviction; speaker labels logged only via `SensitiveDebug`/`SensitiveInformation`.
