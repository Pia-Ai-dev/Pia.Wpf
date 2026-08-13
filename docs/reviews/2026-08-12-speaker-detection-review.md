# Review: Auto Speaker Detection (Teams Meeting Transcription)

Date: 2026-08-12. Revised 2026-08-13 after a code-verified second pass: the roster prior became a
**ceiling** (the symmetric "closest to expected" form regresses meetings with silent attendees),
duration weighting was demoted to phase 2 as low-yield, three previously missed over-detection
amplifiers joined the scope (root causes 5–7 below), and short-segment label inheritance was
promoted — it is the only fix for the second symptom.

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
5. **Hysteresis feedback (code-verified).** `RunPassUnderLock` seeds the clusterer's hysteresis with
   `_labelByCluster.Count`, which between passes includes up to +5 provisional registrations — an
   upward-only ratchet: whenever an inflated-count candidate sits within the 0.03 gap slack,
   hysteresis promotes and then preserves the over-split. The documented intent is "toward the
   previous **pass's** count".
6. **Degenerate passes slam the instant-match threshold.** When every merge lands below the cut band
   (one dominant voice so far) or the input is ≤ 1 embedding, `Cluster` reports `CutMin` (0.30) and
   the adaptive service derives `_matchSimilarity = 0.70` — the strictest allowed, exactly when the
   evidence says "one speaker". The same voice's next noisy segments then fail the instant match,
   register spurious "Speaker N" clusters, and feed cause 5. (Symmetric failure: a 0.70 cut →
   glue-everything threshold 0.30.)
7. **Speaker numbering only grows.** A 4↔5 cluster-count oscillation across passes mints fresh
   "Speaker N" labels (the re-split half loses the greedy overlap tie for its old stable id) while
   orphaned labels vanish silently — transcripts show "Speaker 7"/"Speaker 8" in a 4-person meeting
   and the summary prompt maps roster names against inflated numbers. Perceived count rather than
   cluster count, but the same user-visible symptom.

### Generic "meeting" label mixed into the transcript

- Every segment < 1.5 s (interjections like "ja", "genau", laughter) gets **no embedding** →
  `SpeakerLabel = null` → rendered as the generic "meeting" placeholder.
- A null label also **splits** the current bubble run (merge key requires ordinal-equal labels), so
  the transcript alternates Speaker N / meeting bubbles.

## Improvements (final ranking after the 2026-08-13 pass)

1. **Roster-count ceiling (main fix).** The app already polls the Teams participant roster
   (`PollRosterAsync` → `AccumulateAttendees`) — and polling is **ON by default**
   (`MeetingAttendeeRosterSnapshotMinutes` defaults to 2; JSON-only setting). Pia joins the meeting
   as its own participant, so the local user's voice is part of the diarized stream AND of the
   roster (only the bot's display name is excluded): the roster-union count aligns with the diarized
   voices with no self-exclusion correction. Used strictly as a **ceiling** — never a target — it
   directly caps the 4 → 7–8 failure while leaving meetings with silent attendees untouched. Names
   are display-name-deduped (a meeting-room device undercounts its humans), hence the +1 slack.
2. **Duration gating.** Exclude sub-2 s embeddings from the clustering input (they keep their
   provisional labels); short borderline segments are the dominant noise source in the dendrogram.
3. **Adaptive-loop hygiene.** Three cheap fixes for causes 5–7: seed hysteresis with the previous
   pass's count, clamp the derived instant-match threshold, recycle orphaned labels before minting
   new ones.
4. **Short-segment label inheritance.** Assign undiarized < 1.5 s segments the previous labeled
   bubble's speaker within the merge window instead of leaving them null. Kills the "meeting"
   interleaving and the bubble splitting — the only fix for symptom 2. (The alternative — embedding
   sub-1.5 s segments for label-only — is unsafe as-is: a below-threshold short embedding would
   REGISTER a new speaker via the provisional path.)

Deferred:

- **Small-cluster merging (phase 2).** Post-pass, merge clusters with very little total speech into
  their nearest neighbor. The only measure that removes an artifact cluster surviving WITHIN the
  ceiling (4 real + 1 echo = 5 ≤ cap); revisit if the recordings still over-count after items 1–3.
- **Duration weighting (phase 2, demoted).** Weighting the Lance–Williams linkage by summed
  duration is low-yield once gating has removed the noisy embeddings — and a half-measure anyway
  (the instant path's `RunningCentroid` and the pass centroid rebuild stay unweighted either way).
- **Model evaluation (separate experiment).** Test an alternative embedding model (e.g. ERes2Net /
  VoxCeleb-trained) against a German recording if the above don't fully fix over-detection.

## Agreed scope

Selected for implementation: **1 (roster ceiling)**, **2 (duration gating)**, **3 (adaptive-loop
hygiene)**, **4 (short-segment inheritance)**. Small-cluster merging and duration weighting are
phase 2; the model evaluation is a separate experiment if needed.

## Implementation plan

### 1. Roster-count ceiling

- `SpeakerClusterer.Cluster(embeddings, previousClusterCount = 0, expectedSpeakers = 0)` and
  `ChooseCut(sortedMergeDistances, previousClusterCount, expectedSpeakers)`; 0 = off;
  `cap = expectedSpeakers + 1`.
- `ChooseCut` decides in this order: (a) largest gap, (b) hysteresis exactly as today, (c) ceiling
  LAST and only downward — if the chosen candidate's cluster count exceeds the cap, rescan the
  candidates within `HysteresisGapDelta` of the best gap and take the one with the **largest count
  ≤ cap**; if none qualifies, keep the choice and let force-merge finish. At or below the cap the
  result is byte-identical to today, so silent attendees (roster ≫ talkers) can never pull the
  count upward — the reason the symmetric "closest to expected" form was rejected.
- `Cluster` force-merges while `clusters > Math.Min(MaxClusters, cap)` (the existing guard loop
  with the tighter cap).
- Force-merging no longer raises the reported `CutDistance` (drop `cut = Math.Max(cut, m.Distance)`
  in the guard loop): cap merges must not silently retune `_matchSimilarity`. The `MaxClusters = 12`
  path effectively never fired, so this is a safe unification.
- Hysteresis seed: keep the last pass's `ClusterCount` in a field and pass that as
  `previousClusterCount` instead of `_labelByCluster.Count` (root cause 5).
- `ISpeakerIdentificationService.SetExpectedSpeakers(int count)` as a default no-op interface
  member (existing fakes keep compiling). The adaptive service stores `Math.Max(0, count)`
  thread-safely and reads it once per pass; the manual service ignores it (the threshold model has
  no count concept).
- `MeetingAttendeeService`: after each `AccumulateAttendees`, push `_attendees.Count` into the
  diarizer. The union only grows, so the ceiling refines monotonically. Polling disabled
  (`MeetingAttendeeRosterSnapshotMinutes <= 0`) or roster scrape failing → count stays 0 → ceiling
  silently off, today's behavior.

### 2. Duration gating

- Journal entries carry `DurationSeconds` (from `samples.Length / sampleRate` — zero extra cost).
- `MinClusterSegmentSeconds = 2f`: embeddings below the floor are excluded from the clustering
  input; they keep their provisional/previous label.
- A pass runs only when the **eligible** count reaches `WarmupSegments`; the stride/latency
  triggers stay as they are. A skipped pass leaves all provisional state untouched — without this,
  a stretch of short segments would rebuild the centroid/label maps from (nearly) empty pass
  output, wiping every known speaker and slamming the threshold.
- The pass rebuild must carry over (not wipe) the label + centroid of stable clusters that are
  absent from the pass output but still referenced by journaled gated segments — otherwise a
  participant who only produced 1.5–2 s interjections silently loses their cluster and re-registers
  as a new "Speaker N" on their next utterance.
- The pass currently assumes 1:1 indexing between the journal and the clustering input (input
  build, members list, apply loop, centroid rebuild); the filtered input needs an eligible→journal
  index map at every site.

### 3. Adaptive-loop hygiene

- Threshold clamp: `_matchSimilarity = Math.Clamp(1f - cr.CutDistance, MatchSimilarityMin,
  MatchSimilarityMax)` with 0.40/0.60 constants — kills the 0.70-strict / 0.30-glue extremes from
  degenerate passes (root cause 6).
- Label recycling: when a pass would mint "Speaker N" for an unmatched new cluster, first reuse a
  label orphaned by the same rebuild (previous stable id no longer mapped and not referenced by
  gated segments), skipping user-renamed labels. Keeps numbering ≈ distinct voices (root cause 7).
  No retraction event needed — nothing in the Teams path subscribes to `SpeakerRegistered`.

### 4. Short-segment label inheritance

- `TranscriptOverlayViewModel.GetOrCreateBubble` (shared by live append and
  `RebuildBubblesFromJournal`): a Them-segment with a null label arriving within the 25 s window of
  the last bubble inherits that bubble's non-null label and merges into the run instead of
  splitting it. A null at run start (no labeled predecessor in-window) keeps today's "meeting"
  placeholder. The VM journal keeps the truthful null; inheritance is re-derived on every rebuild,
  so retro-corrections stay consistent (reassignments never target null-label segments — they have
  no segment id).
- Contract change: `Utterances_NullLabelSegmentMidRun_SplitsTheColoredRun` becomes the inheritance
  test; `Utterances_NullLabelSameSpeaker_StillMerge` stays.

### 5. Tests (gate: `dotnet test`, failed: 0)

- `SpeakerClustererTests`: ceiling never inflates (best gap at k=2, competitive noise gap at k=5,
  expected 6 → still 2); ceiling picks the largest competitive count ≤ cap; ceiling off (0) =
  unchanged behavior; hysteresis wins before the ceiling applies; force-merge with cap and
  unchanged reported `CutDistance` (adjust the existing MaxClusters test); expected = 1 → cap 2.
- `AdaptiveSpeakerIdentificationServiceTests`: `SetExpectedSpeakers` flows into passes; hysteresis
  seeded with the last pass count, not the inflated live count; threshold clamped after a
  degenerate pass; a pass with too few eligible embeddings is skipped and preserves
  centroids/labels; a gated-only cluster survives a pass; sub-2 s segments keep labels but stay out
  of clustering; a 4↔5 oscillation recycles a label instead of minting a new number.
- `MeetingAttendeeServiceStateTests`: roster accumulation pushes the expected count to the
  diarizer; polling disabled → no push.
- `MeetingAttendeeViewModelTests`: mid-run null inherits and merges; run-start null keeps the
  placeholder; rebuild reproduces inheritance.

### Constraints / notes

- No new settings UI; the ceiling is driven by the existing roster-snapshot setting. Constants
  follow the existing `internal const` style.
- Changes confined to `SpeakerClusterer.cs`, `AdaptiveSpeakerIdentificationService.cs`,
  `ISpeakerIdentificationService.cs`, `MeetingAttendeeService.cs`, `TranscriptOverlayViewModel.cs`
  (+ tests). The manual diarizer and the direct-transcription path stay untouched (adaptive mode is
  deliberately disabled there because retro-reassignment is unsound under the per-speaker consent
  gate).
- Zero-warning policy: verify with `dotnet build -t:Rebuild -v:n` in Debug and Release.
- Privacy discipline unchanged: embeddings/centroids stay in-memory, actively zeroed on
  Reset/Dispose/journal eviction; speaker labels logged only via `SensitiveDebug`/`SensitiveInformation`.
- Future idea (out of scope): the Teams roster DOM already exposes `voice-level` tids, currently
  filtered out as noise in `TeamsMeetingSession.RosterNamesScript` — a potential active-speaker
  ground-truth signal for diarization.

### Validation

`dotnet test` (failed: 0) and zero-warning rebuilds in Debug and Release, then re-run the two
4-participant recordings end-to-end. Acceptance: final distinct labels ≤ 5 (ideally 4), label
numbers not materially above the distinct voices, no "meeting" bubbles mid-run.
