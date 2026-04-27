# Consent-Management Phase 2 (V1) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Multi-speaker selective recording (Strategy B), hash-chained tamper-evident audit log, LLM-based consent-classifier fallback, and a first-class Post-STT defense filter.

**Architecture:** Builds on Phase 1. The single-buffer model becomes a `Dictionary<speakerLabel, SpeakerRingBuffer>`. The audit log gains a hash chain (`previous_event_hash` + Ed25519 signature). The classifier becomes a two-stage cascade: rule-based → LLM fallback for low-confidence outputs. The post-STT defense check is extracted from `LiveMeetingService` into its own injectable component.

**Tech Stack:** Same as Phase 1 + `System.Security.Cryptography` (SHA-256, Ed25519) + an LLM client (reuse the existing chat client from `Pia.Services` — confirm during Task 0).

**Spec reference:** `docs/consent-management-spezifikation.md` §3.6, §3.7 Stufe 2, §3.9 Strategie B, §4.4 hash chain.

**Prerequisites:** Phase 1 MVP merged (`consent-phase1-mvp` tag).

---

## Scope

**In:**
- Per-speaker ring buffers (drop the single-buffer assumption from Phase 1).
- `Strategy B` new-speaker handling: existing GRANTED pipelines keep running while a new speaker awaits consent.
- LLM classifier fallback when rule-based confidence < 0.9.
- Hash-chained, signed audit log.
- `IPostSttDefenseFilter` extracted from `LiveMeetingService`.

**Out (Phase 3+):**
- Strategy A (pause & re-consent), cross-talk handling.
- Standard/Permissive security modes.
- Persistent voice-embedding blocklist across sessions.

---

## File Structure

**New files (production):**
- `src/Pia.Wpf/Services/Consent/PerSpeakerRingBufferRegistry.cs` — keyed wrapper around `SpeakerRingBuffer`.
- `src/Pia.Wpf/Services/Consent/IPostSttDefenseFilter.cs` + `PostSttDefenseFilter.cs`.
- `src/Pia.Wpf/Services/Consent/LlmConsentClassifier.cs` — wraps existing chat client.
- `src/Pia.Wpf/Services/Consent/CascadingConsentClassifier.cs` — composite: rule → LLM.
- `src/Pia.Wpf/Services/Consent/HashChainedAuditLog.cs` — replaces `JsonlConsentAuditLog` (or wraps it).
- `src/Pia.Wpf/Services/Consent/AuditChainSigner.cs` — Ed25519 signing helper, key from OS keystore.

**Modified:**
- `AuditEvent.cs` → add `PreviousEventHash`, `Signature`.
- `JsonlConsentAuditLog.cs` → split: writer becomes pluggable; chain logic lives in `HashChainedAuditLog`.
- `LiveMeetingService.cs` → use `PerSpeakerRingBufferRegistry`, drop single-buffer assumption, wire `Strategy B`.

**New tests:**
- `PerSpeakerRingBufferRegistryTests.cs`
- `PostSttDefenseFilterTests.cs`
- `CascadingConsentClassifierTests.cs`
- `HashChainedAuditLogTests.cs`
- `MultiSpeakerStrategyBIntegrationTests.cs`

---

## Chunk 1: Per-speaker buffer registry + Strategy B

### Task 1: `PerSpeakerRingBufferRegistry` (TDD)

- [ ] **Step 1: Write failing tests** — `Append` to multiple speaker keys creates separate buffers; `Drain(label)` clears only that speaker's buffer; `RemoveAll()` clears everything; total memory cap enforced (configurable, default 100 MB equivalent samples).
- [ ] **Step 2: Implement.** Backing store `ConcurrentDictionary<string, SpeakerRingBuffer>`. Total-cap check on `Append`: when sum across buffers exceeds cap, evict oldest samples from largest buffer (NOT disk spill).
- [ ] **Step 3: Run tests, commit.**

```bash
git commit -m "feat(consent): per-speaker ring buffer registry with global memory cap"
```

### Task 2: Per-speaker pipelines in `LiveMeetingService` (Strategy B)

- [ ] **Step 1: Failing integration test** — two simulated speakers; Speaker 1 grants, Speaker 2 still in PROMPTED. Assert: Speaker 1 utterances flow to `Utterances`; Speaker 2 utterances do NOT (gate already drops them, but verify per-speaker independence).
- [ ] **Step 2: Modify the utterance forwarder** — track `Set<string> speakersWithActiveConsentFlow`; first sighting of an unknown label triggers the consent flow asynchronously (TTS prompt → MarkPrompted) while existing GRANTED speakers keep producing utterances.
- [ ] **Step 3: Crucial rule from spec §3.9 Strategy B point 5** — on later GRANT, do NOT drain the buffer retroactively. The buffered audio was captured *before* consent and must be discarded. Add an explicit `mgr.OnGrant += (_, label) => buffers.Drain(label).Discard()` to make this intent visible.
- [ ] **Step 4: Run tests, commit.**

```bash
git commit -m "feat(consent): multi-speaker Strategy B selective recording"
```

### Task 3: Audit events for new states

- [ ] Append events: `STRATEGY_B_PENDING_CONSENT`, `PRE_CONSENT_BUFFER_DISCARDED`, with sample counts and durations only — no audio content references.
- [ ] Commit.

---

## Chunk 2: Post-STT defense filter as a component

### Task 4: Extract `IPostSttDefenseFilter`

- [ ] **Step 1: Failing test.** Filter accepts `(TranscriptUtterance, ConsentState)`; returns `Allow|DropAndAudit`. For any non-Granted state on a `TranscriptChannel.Regular` utterance: returns `DropAndAudit` and emits a `DROPPED_TRANSCRIPT_NO_CONSENT` audit event with reason `post_stt_filter_caught_race`.
- [ ] **Step 2: Implement** — pure function over current state, no I/O of its own except via injected `IConsentAuditLog`.
- [ ] **Step 3: Replace inline filter in `LiveMeetingService` with the new component.**
- [ ] **Step 4: Add a health-check counter** — number of post-STT drops per session. Surface to logs at session end (gate bug indicator per spec §6.6).
- [ ] **Step 5: Commit.**

```bash
git commit -m "feat(consent): post-STT defense filter as injectable component"
```

---

## Chunk 3: Cascading classifier with LLM fallback

### Task 5: `LlmConsentClassifier`

- [ ] **Step 1: Confirm available chat client.** Read `src/Pia.Wpf/Services/` for the existing OpenAI/Anthropic/local client. Adapter wraps that.
- [ ] **Step 2: Failing test.** Mock the chat client; assert: returns `Grant|Deny|Ambiguous` parsed from a strict JSON response; never throws (clamps malformed output to `Ambiguous` with confidence 0.0).
- [ ] **Step 3: Implement.** Prompt template: short, single-shot, returns `{"decision":"grant|deny|ambiguous","confidence":0.0-1.0,"reason":"..."}`. Send only the candidate transcript text + the prompt that was played — no other context.
- [ ] **Step 4: Privacy gate** — refuse to send to a non-EU endpoint while in Strict Mode. (Strict Mode is the only mode in Phase 1; Phases 3 will gate this on `consent_scope`.)
- [ ] **Step 5: Commit.**

```bash
git commit -m "feat(consent): LLM-based consent classifier"
```

### Task 6: `CascadingConsentClassifier`

- [ ] **Step 1: Failing test.** Rule-based confidence ≥ 0.9 → return rule output unchanged, LLM not called. < 0.9 → call LLM, combine: keep rule decision if LLM agrees (boost confidence), demote to `Ambiguous` if they disagree.
- [ ] **Step 2: Implement.** Takes `IConsentClassifier rule, LlmConsentClassifier llm` via DI.
- [ ] **Step 3: Update DI registration** — `IConsentClassifier` resolves to `CascadingConsentClassifier`.
- [ ] **Step 4: Commit.**

```bash
git commit -m "feat(consent): cascade rule-based and LLM consent classifiers"
```

---

## Chunk 4: Hash-chained audit log

### Task 7: Add `PreviousEventHash` + `Signature` to `AuditEvent`

- [ ] **Step 1: Update record** with new fields, both nullable on read for backwards compatibility with Phase-1 logs.
- [ ] **Step 2: Migration note in code comment.** A reader encountering a Phase-1 line (no hash) treats it as the chain root.

### Task 8: `AuditChainSigner` + `HashChainedAuditLog` (TDD)

- [ ] **Step 1: Failing tests.**
  - Each appended event's `PreviousEventHash` equals SHA-256 of the canonical-JSON-without-signature of the previous event.
  - Each event's `Signature` verifies against the session's Ed25519 public key.
  - Tampering with any line breaks chain verification on a separate `Verify(path)` method.
- [ ] **Step 2: Implement signer.** Generate a per-session Ed25519 keypair on first append; store the private key encrypted in DPAPI (`src/Pia.Wpf/Infrastructure/DpapiHelper.cs` already exists), public key in the `manifest.json` next to the audit log.
- [ ] **Step 3: Implement chained log.** Wraps the existing JSONL writer. Reads the last line on construction to seed `PreviousEventHash`.
- [ ] **Step 4: Replace DI registration** of `IConsentAuditLog` with `HashChainedAuditLog`.
- [ ] **Step 5: Commit.**

```bash
git commit -m "feat(consent): hash-chained signed audit log"
```

### Task 9: CLI verifier (developer tool, not shipped)

- [ ] Add `tools/verify-audit-chain` console project that takes a path, reports `OK` or the index of the first broken line. Useful in incident response.
- [ ] Commit.

---

## Chunk 5: Verification

### Task 10: Multi-speaker integration test

Two distinct speaker labels, three messages:
1. Speaker 1 says "ja" → granted, transcript flows.
2. Speaker 2 says "vielleicht" → ambiguous, clarification prompt, then "ja" → granted.
3. Speaker 1 keeps talking throughout — uninterrupted, no buffer drain on Speaker 2 grant.

Assert:
- Both speakers end up GRANTED.
- Audit log chain verifies.
- No Speaker 2 utterances appear in `Utterances` from before their grant moment.

### Task 11: Manual end-to-end + tag

- [ ] Run app, drive a real two-person meeting.
- [ ] Inspect audit log: chain verifies via the CLI tool.
- [ ] Tag: `git tag consent-phase2-v1`.
