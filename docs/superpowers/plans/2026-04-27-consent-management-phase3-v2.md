# Consent-Management Phase 3 (V2) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan.

**Goal:** Strategy A (Pause & Re-Consent), cross-talk handling, all three security modes (Strict/Standard/Permissive), and a session-persistent voice-embedding blocklist.

**Architecture:** Phase 3 hardens the system for production. A new `ISecurityModeProvider` switches behaviour at runtime between three profiles. `IConsentOrchestrator` is split out to host both Strategy A and Strategy B as policies. The blocklist filter (Phase 1 conceptually, real implementation here) runs *before* the ring buffer.

**Spec reference:** §3.9 Strategy A, §3.10 cross-talk, §7 security modes, §3.9 blocklist filter.

**Prerequisites:** Phase 2 V1 merged.

---

## Scope

**In:**
- `IConsentOrchestrator` with `StrategyA` and `StrategyB` implementations.
- `ICrossTalkResolver` — when ≥ 2 speakers active, only forward audio if all are Granted (default) or apply source separation if available.
- `ISecurityModeProvider` + Strict/Standard/Permissive presets.
- Settings UI for selecting security mode.
- Session-persistent voice-embedding blocklist (Denied speakers cannot re-enter the buffer).
- Permissive Mode confirmation dialog ("erhöhte rechtliche Verantwortung").

**Out:**
- Cloud pipeline (Phase 4).
- Cross-session voice-embedding *whitelist* persistence (Phase 5).

---

## File Structure

**New (production):**
- `src/Pia.Wpf/Services/Consent/IConsentOrchestrator.cs` + `StrategyAOrchestrator.cs`, `StrategyBOrchestrator.cs`.
- `src/Pia.Wpf/Services/Consent/ICrossTalkResolver.cs` + `ConservativeCrossTalkResolver.cs`.
- `src/Pia.Wpf/Services/Consent/ISecurityModeProvider.cs` + `SecurityModeProvider.cs`.
- `src/Pia.Wpf/Services/Consent/SecurityProfile.cs` — record with strategy, cloud allowance, retention days, snippet-persistence flag.
- `src/Pia.Wpf/Services/Consent/VoiceEmbeddingBlocklist.cs` — keeps `float[]` embeddings + cosine-similarity check.
- `src/Pia.Wpf/Services/Consent/IBlocklistFilter.cs` + implementation.
- `src/Pia.Wpf/Views/Settings/SecurityModeSection.xaml` + code-behind.
- `src/Pia.Wpf/ViewModels/SecurityModeViewModel.cs`.

**Modified:**
- `LiveMeetingService.cs` → drives the orchestrator instead of doing flow-control inline.
- `LiveTranscriptionEngineService.cs` → consults `IBlocklistFilter` before VAD enqueue.
- Settings model + persistence.

---

## Chunk 1: Security mode provider + profiles

### Task 1: `SecurityProfile` record + presets

```csharp
public sealed record SecurityProfile(
    SecurityMode Mode,
    NewSpeakerStrategy Strategy,           // PauseAndReConsent | SelectiveRecording
    bool AllowEuCloud,
    bool AllowNonEuCloud,
    int TranscriptRetentionDays,
    int ConsentEvidenceRetentionDays,
    bool PersistConsentAudioSnippet);

public enum SecurityMode { Strict, Standard, Permissive }
public enum NewSpeakerStrategy { PauseAndReConsent, SelectiveRecording }
```

Presets per spec §7:
- `Strict`: Strategy A, no cloud, 7d transcript, snippet=true.
- `Standard`: Strategy B, EU cloud, 30d transcript, snippet optional.
- `Permissive`: Strategy B, all cloud, 90d transcript.

- [ ] Tests: each preset matches spec table. Commit.

### Task 2: `ISecurityModeProvider`

- [ ] Subscribes to settings changes; raises `ProfileChanged`.
- [ ] Persists selected mode via existing `ISettingsService`.
- [ ] Tests + commit.

### Task 3: Settings UI section

- [ ] `SecurityModeSection.xaml` with three radio cards.
- [ ] On Permissive selection, modal dialog: "Sie übernehmen die rechtliche Verantwortung für externe Verarbeitung. Fortfahren?" with explicit `Verstanden, fortfahren` / `Abbrechen` buttons.
- [ ] Manual UI verification.
- [ ] Commit.

---

## Chunk 2: Strategy A (Pause & Re-Consent)

### Task 4: Pause/resume primitives

- [ ] Add `PauseAsync` / `ResumeAsync` to `LiveTranscriptionEngineService`. Pause stops enqueuing VAD segments to STT but keeps the audio source running into the ring buffer. Resume reverses.
- [ ] Tests for pause idempotency + resume-after-pause throughput. Commit.

### Task 5: `StrategyAOrchestrator`

Per spec §3.9 Strategy A:

```
1. On NewSpeakerJoined → PauseAsync on every engine of every Granted speaker.
2. Run consent prompt for the new speaker (TTS + classifier).
3. On Grant: ResumeAsync everything. Discard the new speaker's pre-consent buffer.
4. On Deny/Timeout: ResumeAsync the others; keep new speaker permanently blocked
   (add embedding to blocklist).
```

- [ ] Failing integration test exercising the four branches.
- [ ] Implement.
- [ ] Audit events: `STRATEGY_A_PAUSED`, `STRATEGY_A_RESUMED`, with paused-duration metadata.
- [ ] Commit.

### Task 6: Wire orchestrator selection

- [ ] DI factory selects `StrategyAOrchestrator` or `StrategyBOrchestrator` based on `ISecurityModeProvider.Current.Strategy`.
- [ ] React to runtime profile changes during a meeting: log a warning, but do NOT switch mid-meeting (would invalidate consents). Apply on next session start.
- [ ] Commit.

---

## Chunk 3: Cross-talk resolver

### Task 7: `ICrossTalkResolver`

Spec §3.10:

```csharp
GateDecision Resolve(IReadOnlyCollection<string> activeSpeakerLabels);
```

- [ ] Conservative implementation: `Drop` unless every active speaker is `Granted`.
- [ ] Tests for: zero speakers (Drop), one Granted (Pass), one Granted + one Denied (Drop), two Granted (Pass).
- [ ] Wire into the engine: when diarization output reports overlapping labels for the same VAD segment, route through the resolver instead of single-speaker gate.
- [ ] Commit.

### Task 8: Diarization upgrade for cross-talk detection

- [ ] Investigate sherpa-onnx capability — `SpeakerIdentificationService.IdentifyOrRegister` currently returns a single label. Cross-talk needs either segment-level multi-label or a separate diarizer. **If unavailable in current sherpa-onnx version, document as a known limitation** and fall back to single-label detection (Phase 3 ships without true cross-talk; resolver still guards against future capability).
- [ ] Commit either the upgraded multi-label detection or the documented limitation as a markdown note in the consent module.

---

## Chunk 4: Voice-embedding blocklist (session-persistent)

### Task 9: `VoiceEmbeddingBlocklist`

- [ ] Collection of `float[]` embeddings + threshold (default 0.85, per spec §3.9).
- [ ] `bool ShouldDrop(float[] embedding)` checks cosine similarity against any blocked embedding.
- [ ] Tests covering: empty list (no drop), exact match (drop), below-threshold (no drop), above-threshold (drop).
- [ ] Commit.

### Task 10: `IBlocklistFilter` integrated before ring buffer

- [ ] On `Denied` / `Timeout` / `Revoked` transitions: snapshot the speaker's embedding (from `SpeakerIdentificationService`) and add to blocklist.
- [ ] In the engine pipeline: every VAD segment with a known embedding is checked against the blocklist *before* being enqueued to ring buffer. If matched, drop with audit event `DENIED_SPEAKER_BLOCKED`.
- [ ] **Important:** Phase 3 keeps the blocklist *in memory for the session only*. Cross-session persistence is biometric data (Art. 9 DSGVO) and requires its own consent — Phase 5.
- [ ] Tests + manual verification: deny a speaker, verify they cannot re-enter the same session.
- [ ] Commit.

---

## Chunk 5: Verification + tag

### Task 11: Profile-driven E2E tests

- [ ] One e2e test per security profile: simulate the same multi-speaker scenario, verify behaviours match the preset (Strategy A pauses, Strategy B doesn't; cloud calls blocked in Strict; etc.).
- [ ] Commit.

### Task 12: Manual exploratory testing across modes + tag

- [ ] Walk each mode through golden + denied + timeout paths.
- [ ] Verify Permissive dialog appears once per activation.
- [ ] Verify deny → blocked-for-session in all modes.
- [ ] `git tag consent-phase3-v2`.
