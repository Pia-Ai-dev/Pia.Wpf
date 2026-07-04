# Smart Speaker Auto-Detect (Adaptive Diarization) — Design

**Date:** 2026-07-04
**Branch:** `feature/meeting_attendee`
**Status:** Approved approach (user delegated detail decisions); ready for implementation planning.

---

## 1. Problem

The meeting attendee's per-speaker diarization (`SpeakerIdentificationService`) assigns speaker
labels with a single greedy online pass: each VAD segment's voice embedding is matched against
running per-speaker centroids using one fixed cosine threshold (`SpeakerEmbeddingThreshold`,
default 0.50). This is error-prone from meeting to meeting:

1. **First impressions are permanent.** The first segment of a voice becomes that speaker's
   centroid seed. A bad first segment (crosstalk, laughter, codec artifacts) poisons the profile
   and nothing ever heals it.
2. **Decisions are never revisited.** A wrongly registered speaker keeps attracting segments;
   past bubbles are never corrected.
3. **One global threshold must fit all voices.** Similar-sounding participants need a high
   threshold; a variable mic needs a low one — so the user must trial-and-error the threshold,
   max-speakers cap, and min-speech sliders per participant set.

The 2026-06-24 handover (`docs/superpowers/handover/2026-06-24-meeting-per-speaker-bubbles-open-questions.md`)
documents the resulting **fragmentation failure mode** (one voice splits into many "Speaker N"
bubbles) and notes the attendee feeds the diarizer a **single mixed downstream loopback stream** —
the worst case for greedy centroid matching.

## 2. Goal

An optional **smart auto-detect mode** (ON by default) that removes all manual diarization tuning:
the system keeps every segment's embedding for the duration of the meeting and **periodically
re-clusters all of them**, so speaker assignments self-heal as evidence accumulates — including
**full retroactive correction** of already-rendered transcript bubbles.

### User decisions (from brainstorming Q&A)

| Question | Decision |
|----------|----------|
| Scope | Audio re-clustering only (no Teams DOM active-speaker scraping in this iteration) |
| Retro edits | Full retro correction — past bubbles may re-assign, merge, and split |
| Settings | One toggle, zero knobs; manual tuning controls hidden while auto is ON |
| Default | ON by default; manual mode (current behavior + sliders) reachable by toggling OFF |
| Approach | A: periodic agglomerative (AHC) re-clustering with a data-derived cut |

### Non-goals

- Teams DOM active-speaker signal / automatic real-name attribution (possible follow-up; the
  roster-union → summary-prompt flow stays as is).
- Cross-meeting / persistent speaker memory (explicitly OUT — reopens the biometric-consent
  question per the handover's privacy note).
- Repurposing or removing existing `ISpeakerIdentificationService` members. Note: the meeting
  attendee is the interface's only real consumer today — the "blocklist/consent flow" mentioned in
  its doc comments is a remnant of the salvaged POC branch (no `ConsentStateManager` type exists in
  `src/`). Members stay additive so the sole test stub
  (`tests/Pia.Wpf.Tests/Services/MeetingAttendee/MeetingAttendeeServiceStateTests.cs`) updates
  mechanically.
- Changing the embedding model (stays 3D-Speaker CAM++ via sherpa-onnx).

## 3. Architecture overview

```
VAD segment (≥1.5s)
   │
   ▼
AdaptiveSpeakerIdentificationService          (auto mode; manual mode keeps today's service)
   ├─ IEmbeddingExtractor.Compute(samples)    (seam over sherpa-onnx SpeakerEmbeddingExtractor)
   ├─ journal: (segmentId, embedding)         (in-memory, wiped at meeting end)
   ├─ instant provisional label                → utterance carries (SegmentId, Label) → bubble now
   └─ every ≥5 new segments or ≥30s:
        SpeakerClusterer.Cluster(all embeddings)   (pure, unit-testable)
        → stable-label mapping vs previous pass
        → SpeakersReassigned event (only changed segments)
              │
              ▼
MeetingAttendeeService (forwards event) → MeetingAttendeeViewModel
              │
              ▼
TranscriptOverlayViewModel.ApplyReassignments(map)
   ├─ update utterance journal labels
   └─ rebuild Bubbles from journal (same merge rules) → transcript self-corrects
```

The existing fast path is preserved: every segment still gets an **instant** label so bubbles
render in real time; the re-cluster pass only *corrects* afterwards.

## 4. Components

### 4.1 `IEmbeddingExtractor` (new, `Services/LiveTranscription`)

Thin seam over the native sherpa-onnx extractor so the adaptive service is unit-testable without
an ONNX model:

```csharp
public interface IEmbeddingExtractor : IDisposable
{
    int Dim { get; }
    float[] Compute(float[] samples, int sampleRate);   // 16 kHz mono float32 → embedding
}
```

`SherpaEmbeddingExtractor` wraps `SpeakerEmbeddingExtractor` (moved from the private
`ComputeEmbedding` in today's service). The existing manual `SpeakerIdentificationService` is NOT
refactored onto the seam (no behavior change in manual mode).

### 4.2 `ISpeakerIdentificationService` additions

```csharp
/// Identify-or-register that also returns the segment id under which the adaptive
/// service journals this segment's embedding. Manual mode returns monotonically
/// increasing ids too (they are simply never reassigned).
SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate);

/// Raised after a re-cluster pass changed the label of already-emitted segments.
/// Carries only the changed (SegmentId → new Label) pairs. Never raised in manual mode.
event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;
```

```csharp
public readonly record struct SpeakerSegmentResult(long SegmentId, string Label);
public readonly record struct SpeakerReassignment(long SegmentId, string NewLabel);
```

`SpeakerIdentificationService` (manual) implements `IdentifyOrRegisterSegment` by wrapping its
existing logic with a counter and never raises the event. Existing members
(`IdentifyOrRegister`, `IdentifyOrRegisterWithEmbedding`, `Rename`, `Reset`,
`SpeakerRegistered`) stay untouched — they have no other production consumer today (see §2
non-goals), so keeping the change additive is about mechanical test-stub updates, not about
protecting another feature.

### 4.3 `SpeakerClusterer` (new, pure logic — the testable heart)

Static-free class with no I/O, no native deps, deterministic:

```csharp
public sealed class SpeakerClusterer
{
    /// embeddings[i] is L2-normalized. Returns cluster index per embedding.
    public ClusterResult Cluster(IReadOnlyList<float[]> embeddings);
}
public sealed record ClusterResult(int[] AssignmentPerSegment, int ClusterCount, float CutDistance);
```

**Algorithm — average-linkage AHC with a data-derived cut:**

1. Pairwise cosine distance matrix (`1 − sim`); O(n²) memory (n ≤ 2000 → ≤ 16 MB float, see caps).
2. Average-linkage agglomeration via Lance–Williams updates with a per-row nearest-neighbor
   cache → O(n²) time. Record the merge distance sequence.
3. **Cut selection (replaces the threshold slider):** consider merge distances inside the
   guardrail band `[CutMin, CutMax] = [0.30, 0.70]`; cut at the **largest gap** between
   consecutive merge distances whose upper edge falls in the band.
   - All merges below `CutMin` → single speaker (one cluster).
   - No gap in band (degenerate) → fall back to cutting at distance `0.50`
     (equivalent to today's default threshold).
   - Single-cluster outcome (all merges below `CutMin`): `ClusterResult.CutDistance` is still
     defined — it is `CutMin` (0.30), so the instant path's derived match threshold stays strict
     rather than degenerate.
   - **Hysteresis:** if the best and second-best gaps differ by `< 0.03`, prefer the cut whose
     resulting cluster count matches the previous pass's count (label churn dampening).
4. **Sanity cap:** if the cut yields more than 12 clusters (max of today's manual cap), keep
   merging until 12 — over-segmentation guard for pathological audio.

Constants live in the clusterer as named `internal const float` with rationale comments; they are
deliberately NOT user-facing settings.

### 4.4 `AdaptiveSpeakerIdentificationService` (new, implements `ISpeakerIdentificationService`)

State (all under one lock, mirroring today's service):

- `List<(long SegmentId, float[] Embedding)>` journal (embedding store).
- Current assignment: `Dictionary<long /*segmentId*/, int /*clusterId*/>`.
- `Dictionary<int /*clusterId*/, string>` display labels (holds user renames).
- Per-cluster centroids (mean of member embeddings) for the instant provisional path.
- Monotonic `_nextSegmentId`, `_speakerCounter` ("Speaker N" numbering, never reused in-meeting).

**Instant path (`IdentifyOrRegisterSegment`):** compute embedding, journal it, match against
current cluster centroids with the **adaptive similarity threshold `1 − CutDistance`** (the
clusterer outputs a cosine *distance*; centroid matching compares cosine *similarity*, so a pass
yielding `CutDistance = 0.30` means "match at sim ≥ 0.70"). Before the first pass the threshold is
similarity 0.50 (today's default). At or above → provisional member of that cluster; below →
provisional new "Speaker N" (raises `SpeakerRegistered`, like today). Returns
`(segmentId, label)` immediately.

**Re-cluster pass:** triggered at the end of an identify call when `newSegmentsSinceLastPass ≥ 5`
**or** (`≥ 1` new segment **and** `≥ 30 s` since the last pass). Runs synchronously on the engine's
segment thread (measured and logged; at n = 2000, O(n²) Lance–Williams is tens of ms — acceptable
against a multi-hundred-ms STT step). Steps:

1. `SpeakerClusterer.Cluster(allEmbeddings)` — skipped until ≥ 6 journaled segments (warm-up:
   provisional labels only).
2. **Stable-label mapping:** match new clusters to previous clusters greedily by descending
   segment-overlap count. Tie-break: prefer the previous cluster whose label was **user-renamed**
   (so "Alice" survives a merge), then the larger cluster. Matched clusters inherit the previous
   cluster's display label; unmatched new clusters get the next "Speaker N" (raises
   `SpeakerRegistered`). Labels of vanished clusters retire.
3. Diff old vs new assignment → raise `SpeakersReassigned` with only the changed pairs
   (outside the lock, subscriber exceptions caught — same pattern as `SpeakerRegistered`).

**Caps:** embedding journal capped at 2000 entries; beyond that the oldest entries are dropped and
their assignments frozen. Safe because the VM's utterance journal (§4.7) keeps only the newest
1000 utterances, and every journaled utterance's segment is among the newest ≤ 2000 segments — so
a frozen segment can never correspond to an utterance the UI could still rebuild.

**`Rename(old, new)`:** re-keys the display-label map by cluster id — renames survive re-cluster
passes by construction. **`Reset`/`Dispose`:** extend today's biometric wipe — zero every journaled
embedding `float[]` and every centroid before dropping references (same
`WipeBiometricStateUnderLock` discipline).

### 4.5 Selection & orchestration (`MeetingAttendeeService`)

- `TryCreateSpeakerIdentificationAsync` builds `AdaptiveSpeakerIdentificationService` when
  `settings.MeetingSmartSpeakerDetection` is true, else the existing manual service with the
  existing settings. Degrade-to-null contract unchanged (model download failure never blocks the
  join).
- In auto mode the manual knobs are ignored (`SpeakerEmbeddingThreshold`, `MeetingMaxSpeakers`);
  `MeetingMinSpeechSeconds` is replaced by the fixed 1.5 s embed minimum (today's default) via the
  existing `minDiarizationSamples` engine parameter.
- New `IMeetingAttendeeService` event, forwarded from the per-session diarizer (subscribed on
  start, unsubscribed on teardown):

```csharp
event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;
```

### 4.6 Engine (`LiveTranscriptionEngineService`)

Calls `IdentifyOrRegisterSegment` instead of `IdentifyOrRegister` and stamps the id on the
utterance. `TranscriptUtterance` gains an additive optional param:

```csharp
public sealed record TranscriptUtterance(
    TranscriptSpeaker Speaker, string Text, DateTimeOffset Timestamp,
    string? SpeakerLabel = null, long? SegmentId = null);
```

Sub-minimum segments keep `SpeakerLabel = null, SegmentId = null` (unchanged null-split behavior,
still pinned by the existing regression test).

### 4.7 Transcript VM (`TranscriptOverlayViewModel`) — utterance journal + rebuild

Bubbles concatenate utterance text today, so retro correction needs per-utterance retention. The
journal and `ApplyReassignments` live in the abstract **base** `TranscriptOverlayViewModel`
(alongside `Bubbles` and `RelabelSpeaker`); the attendee VM is currently its only subclass, but the
journal is base-level state because `AddUtterance` (base) must feed it:

- New private journal `List<UtteranceEntry>` (`Speaker`, `Text`, `Timestamp`, `Label`,
  `SegmentId`), appended by `AddUtterance`, capped at 1000 entries (oldest dropped — comfortably
  above the 200-bubble UI cap so a rebuild can never resurrect trimmed content beyond the cap).
- New `internal void ApplyReassignments(IReadOnlyList<SpeakerReassignment> changes)`:
  1. Update `Label` on matching journal entries (by `SegmentId`).
  2. Rebuild the `Bubbles` collection from the journal on the UI thread using the **same** merge
     rules as the incremental path (extract the merge decision from `GetOrCreateBubble` so both
     paths share it), then trim **until** under the 200-bubble cap (the existing `TrimIfNeeded`
     removes at most one `TrimBatch` per call — a full rebuild must loop, not call it once).
  3. `_speakerColorIndex` persists across rebuilds → speakers keep their colors; a label that
     disappears keeps its slot reserved (harmless).
  No-op when `changes` is empty or nothing matches (e.g. all affected entries already trimmed).
- `RelabelSpeaker(old, new)` (manual rename) additionally updates journal labels, otherwise the
  next rebuild would revert the rename.
- Rebuild replaces `Bubbles` contents in place (clear + re-add under one dispatcher action —
  same dispatcher marshalling as `AddUtterance`).

`MeetingAttendeeViewModel` subscribes to the service's `SpeakersReassigned` and forwards to
`ApplyReassignments`. In manual mode the event never fires, so behavior is unchanged there.

### 4.8 Settings + UI

- `AppSettings.MeetingSmartSpeakerDetection` (bool, default **true**, local-only — no
  SyncSettings mirror, same as the other diarization knobs).
- `MeetingSettingsViewModel`: new `[ObservableProperty] bool meetingSmartSpeakerDetection` with
  load/save (same `_isLoading` guard pattern).
- `AssistantView.xaml` Meeting tab: new CheckBox ("Smart speaker detection (automatic)") with a
  description line, directly under the diarization enable toggle. The three tuning StackPanels
  (threshold / max speakers / min speech) get `Visibility` bound to the toggle (collapsed when
  auto is ON) in addition to their existing `IsEnabled="{Binding EnableMeetingDiarization}"`.
  Stored slider values persist untouched and take effect again when auto is toggled OFF.
- Localization: EN/DE/FR strings in `ViewStrings.resx` (+ `.de`, `.fr`) for the toggle label and
  description.

## 5. Data flow (auto mode, end to end)

1. Segment → embedding → journal → instant provisional label → `TranscriptUtterance(label, segId)`
   → bubble appears immediately (unchanged latency).
2. Pass trigger → AHC over all embeddings → adaptive cut → stable-label mapping → changed pairs.
3. `SpeakersReassigned` → attendee service forward → VM → journal label update → bubble rebuild.
4. User renames "Speaker 2" → "Alice": diarizer re-keys cluster's display label; VM updates
   palette slot + bubbles + journal. Subsequent passes keep "Alice" attached to her cluster.
5. Meeting end → transcript export reads corrected bubbles; `Dispose` zeroes all embeddings,
   centroids, and label maps.

## 6. Error handling

- **Re-cluster pass failure:** caught inside the service; previous assignment stays; logged
  (`LogWarning`, counts/durations only). The instant path keeps working — a clustering bug can
  never take down transcription.
- **Event subscribers:** invoked outside the lock; exceptions caught and logged (existing
  `SpeakerRegistered` pattern).
- **Rebuild failure in the VM:** caught and logged (existing `AddUtterance` pattern); journal
  stays consistent, next reassignment retries a full rebuild.
- **Model unavailable / download failure:** unchanged degrade-to-null — meeting joins without
  diarization.
- **In-flight race on rename:** same eventual-consistency as today (a stray old-label utterance
  self-corrects on the next pass — the pass is now the healing mechanism, an improvement over
  today's permanent stray).
- **Logging privacy:** labels and per-label similarity dumps stay `SensitiveInformation`
  (DEBUG-erased); pass stats (segment/cluster/changed counts, cut distance, duration ms) are
  plain `LogDebug` — no user content.

## 7. Privacy

Same floor as today, explicitly restated: voice embeddings are biometric data; auto mode retains
**per-segment embeddings** (not just centroids) **in memory only, per meeting**, never persisted,
actively zeroed on stop/dispose. The existing attendee consent checkbox remains the gate. No
cross-meeting memory. Memory bound: 2000 segments × 192 floats ≈ 1.5 MB (+ O(n²) distance matrix
≤ 16 MB transiently during a pass).

## 8. Testing

All pure logic is seam-isolated; no test needs the native ONNX model:

1. **`SpeakerClustererTests`** (new): synthetic unit-vector embeddings around K well-separated
   directions with controlled noise (deterministic, no RNG or seeded RNG):
   - K = 1, 2, 3, 5 speakers → correct cluster count and assignments.
   - Guardrail band: degenerate gap → 0.50 fallback; all-below-CutMin → 1 cluster; > 12 → capped.
   - Hysteresis: ambiguous gap keeps previous count.
   - **Self-healing scenario:** a "poisoned" outlier first segment lands with its true speaker
     once enough evidence accumulates (the core promise of the feature).
2. **`AdaptiveSpeakerIdentificationServiceTests`** (new, fake `IEmbeddingExtractor` returning
   scripted embeddings): provisional labeling, pass triggers (segment-count and elapsed-time via
   an injectable clock), reassignment diff (only changed pairs), stable-label mapping + rename
   survival across merge, `SpeakerRegistered` raised for genuinely new labels only, cap behavior,
   wipe-on-dispose (embeddings zeroed).
3. **`TranscriptOverlayViewModel` / `MeetingAttendeeViewModelTests`** (extend): rebuild
   equivalence (incremental bubbles == rebuild-from-journal for the same utterances), retro merge
   (two labels collapse → bubbles merge), retro split (one bubble's utterances re-split),
   rename-then-reassign keeps the rename, journal trim, existing null-split regression stays green.
4. **Engine drain tests** (extend): `SegmentId` stamped when diarized, null when below minimum.
5. **`MeetingSettingsViewModelTests`** (extend): toggle load/save default-true.
6. **`MeetingAttendeeService` tests** (extend): auto/manual service selection by setting; event
   forwarding; `TryCreate…` degrade-to-null unchanged.

Gate: `dotnet build` clean + `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
green (known live-network failures excluded per project baseline).

**Runtime validation (carried over from the handover, still human-gated):** a real multi-speaker
Teams meeting on the real loopback path is the only way to validate end-to-end label quality. The
design's success criterion there: a monologue that initially fragments visibly heals within one
pass (≤ 30 s), and two-person meetings converge to exactly two stable speakers without touching
any slider.

## 9. Risks & mitigations

| Risk | Mitigation |
|------|------------|
| AHC gap heuristic wrong on hard audio (similar voices) | Guardrail band + 0.50 fallback ≈ never worse than today's default; manual mode remains one toggle away |
| Visible bubble churn on reassignment | Changes-only diffs, hysteresis dampening, color stability across rebuilds |
| Pass cost grows with meeting length | O(n²) Lance–Williams, 2000-embedding cap, duration logged |
| Renames lost across passes | Labels keyed by cluster id + rename-preferring tie-break; covered by tests |
| Interface change ripples to other consumers | Additive members only; manual service implements trivially; the only other touch point is the mechanical test-stub update (§2 non-goals) |
