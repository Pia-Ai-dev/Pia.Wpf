# Consent-Management Phase 5 (V4) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan.

**Goal:** Cross-session voice-embedding persistence with its own biometric-data consent scope, enabling "this caller has previously consented" flows with strict expiry handling.

**Architecture:** Voice embeddings become a separate, encrypted, biometric data store under DSGVO Art. 9. A new `IBiometricConsentScope` is required *in addition* to the regular consent scope. Stored consents have explicit expiry (configurable, default 12 months). On meeting start, the diarizer's freshly computed embedding is matched against the persisted store; on hit, the previously-recorded `ConsentEvidence` is loaded and re-validated for freshness; on miss, the normal Phase 1–4 flow runs.

**Spec reference:** §2.4 `ConsentScope.biometric_persistence`, §4.1 storage table for "Voice-Embeddings (über Sessions) — nur mit Extra-Consent", §4.6 retention policy, §8 Phase 5.

**Prerequisites:** Phase 4 V3 merged.

---

## Scope

**In:**
- Encrypted persistent voice-embedding store (separate file from session data).
- New consent prompt step: an explicit second prompt asking permission to remember the speaker's voice profile across sessions.
- `BiometricConsentEvidence` record (separate from `ConsentEvidence`).
- Match-on-meeting-start flow: short-circuit consent prompt for known + valid speakers.
- Expiry policy: 12 months default, configurable; expired entries auto-deleted with audit event.
- Self-service UI: list, inspect, manually delete persisted voice profiles.
- Right to withdrawal extends to biometric store (revoke wipes embedding immediately).

**Out:**
- Anything not directly related to cross-session embedding persistence (this is the final spec phase).

---

## File Structure

**New:**
- `src/Pia.Wpf/Services/Consent/Biometric/IBiometricConsentStore.cs` + `EncryptedFileBiometricConsentStore.cs`.
- `src/Pia.Wpf/Services/Consent/Biometric/BiometricConsentEntry.cs` — record with embedding bytes (encrypted), grant timestamp, expiry, evidence pointer.
- `src/Pia.Wpf/Services/Consent/Biometric/IBiometricMatcher.cs` + `CosineSimilarityBiometricMatcher.cs`.
- `src/Pia.Wpf/Services/Consent/Biometric/BiometricConsentEvidence.cs`.
- `src/Pia.Wpf/Services/Consent/ConsentPromptTemplates.cs` (extend) — `BIOMETRIC_OPT_IN_DE`.
- `src/Pia.Wpf/Views/Settings/BiometricStoreSection.xaml` — list / delete.
- `src/Pia.Wpf/ViewModels/BiometricStoreViewModel.cs`.
- `src/Pia.Wpf/Services/Consent/Biometric/BiometricRetentionWorker.cs` — background sweeper.

**Modified:**
- `LiveMeetingService.cs` — match step on session start.
- `SpeakerConsentEntry.cs` — add `BiometricMatchSource` flag.
- `SecurityProfile.cs` — add `AllowBiometricPersistenceByDefault` (still requires per-speaker opt-in).

**New tests:**
- `EncryptedFileBiometricConsentStoreTests.cs`
- `CosineSimilarityBiometricMatcherTests.cs`
- `BiometricRetentionWorkerTests.cs`
- `CrossSessionConsentReuseIntegrationTests.cs`

---

## Chunk 1: Biometric store

### Task 1: `BiometricConsentEntry`

```csharp
public sealed record BiometricConsentEntry(
    Guid Id,
    string DisplayName,                         // optional, user-supplied
    byte[] EmbeddingCipherText,                 // AES-256-GCM
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    string ConsentEvidencePath,                 // pointer to phase-4 evidence file
    string PromptVersionHash);
```

- [ ] Tests: round-trip serialise/deserialise; null fields rejected; expiry must be > granted.
- [ ] Commit.

### Task 2: `EncryptedFileBiometricConsentStore`

- [ ] File: `%LOCALAPPDATA%\Pia\Biometric\store.bin`. Format: length-prefixed encrypted records, single AES-GCM key wrapping each record's payload + nonce.
- [ ] Operations: `AddAsync`, `RemoveAsync(id)`, `GetAllAsync()`, `GetAsync(id)`.
- [ ] Master key from existing `DpapiHelper`.
- [ ] **Hard rule:** the store file MUST NOT be readable without the user's profile. Tests assert that copying it to a different user's profile raises `CryptographicException` on read.
- [ ] Tests + commit.

---

## Chunk 2: Matcher + retention sweeper

### Task 3: `CosineSimilarityBiometricMatcher`

- [ ] `Task<BiometricConsentEntry?> MatchAsync(float[] embedding, float threshold = 0.85f)`.
- [ ] Decrypts each stored embedding, computes cosine similarity, returns the best match above threshold or `null`.
- [ ] Tests with synthetic embeddings: exact match, near-miss, far-miss.
- [ ] Performance test: 1000 stored entries match within 200 ms (per spec §3.1 latency budget; Phase 5 is the only place where the store can grow large).
- [ ] Commit.

### Task 4: `BiometricRetentionWorker`

- [ ] Background `IHostedService`-equivalent that on app start removes entries past `ExpiresAt`, audits each removal as `BIOMETRIC_ENTRY_EXPIRED`.
- [ ] Tests: simulate clock advance via `TimeProvider`; verify only past-expiry rows removed.
- [ ] Commit.

---

## Chunk 3: Biometric opt-in flow

### Task 5: New prompt template

```
BIOMETRIC_OPT_IN_DE:
"Möchten Sie, dass ich Ihre Stimme für künftige Gespräche speichere,
damit ich Sie beim nächsten Mal nicht erneut um Einwilligung bitten muss?
Diese Speicherung ist freiwillig und kann jederzeit widerrufen werden.
Sagen Sie bitte Ja oder Nein."
```

- [ ] Add to `ConsentPromptTemplates.cs`. Version hash recomputed automatically.
- [ ] Equivalent EN template.
- [ ] Commit.

### Task 6: Two-step consent flow in orchestrator

After regular `INITIAL_CONSENT_LOCAL_ONLY` GRANT:

```
1. If SecurityProfile.AllowBiometricPersistenceByDefault == true:
2.   Play BIOMETRIC_OPT_IN.
3.   Classify response (reuse cascading classifier).
4.   On Grant:
5.     Capture stable embedding (≥ 5 s of accumulated speech).
6.     Encrypt + insert into biometric store with default expiry.
7.     Append audit event BIOMETRIC_CONSENT_GRANTED.
8.   On Deny: append BIOMETRIC_CONSENT_DENIED. Continue normally.
```

- [ ] Tests: regular consent path unchanged; biometric path only runs when flag true; biometric deny does not affect regular consent.
- [ ] Commit.

### Task 7: Match-on-session-start short-circuit

- [ ] On `NewSpeakerJoined`: compute embedding (after enough audio accumulates), match against store.
- [ ] If matched: load referenced `ConsentEvidence`, check freshness (`ExpiresAt > now`). On valid: transition the speaker directly to `Granted`, append audit event `BIOMETRIC_MATCH_REUSED_CONSENT` with the matched entry id. Skip TTS prompt.
- [ ] If matched but expired: delete stale entry, fall through to normal consent flow.
- [ ] Failing integration test: two-meeting scenario — first meeting grants + biometric opt-in; second meeting verifies short-circuit.
- [ ] Commit.

---

## Chunk 4: User-facing store management

### Task 8: Settings page

- [ ] `BiometricStoreSection.xaml` lists each entry: display name (editable), granted date, expiry, originating prompt version, [Löschen] button.
- [ ] Deletion immediately removes from store + audits `BIOMETRIC_ENTRY_USER_DELETED`.
- [ ] Bulk action: "Alle gespeicherten Stimmen löschen" with confirmation.
- [ ] Manual UI verification.
- [ ] Commit.

### Task 9: Revocation extension

- [ ] `IRevocationService.RevokeAsync` (Phase 4) extended: also remove any biometric entry whose embedding matches the revoked speaker's session embedding above threshold.
- [ ] Tests + commit.

---

## Chunk 5: Verification + tag

### Task 10: Cross-session integration test

- [ ] Simulate two consecutive `LiveMeetingService` lifecycles using a fake audio source replaying the same speaker. Assert: second meeting skips the consent prompt and emits `BIOMETRIC_MATCH_REUSED_CONSENT`.
- [ ] Tampering test: edit the store file to corrupt an entry; matcher logs and skips, falls through to fresh consent. Audit `BIOMETRIC_STORE_CORRUPTION_DETECTED`.

### Task 11: Compliance review checklist

Manually walk through and confirm before tagging:
- Biometric data stored in a separate, encrypted file.
- Per-entry expiry enforced.
- Default expiry surfaced in the consent prompt itself ("für zwölf Monate").
- Self-service deletion exists and is one click.
- Revocation pipeline removes biometric entries.
- No biometric data ever reaches a cloud endpoint.
- Audit chain captures every grant, reuse, expiry, deletion.

### Task 12: Tag

```bash
git tag consent-phase5-v4
```

---

## Closing notes

This completes the spec's full roadmap (§8 Phase 1 through 5). After Phase 5 ships, future work would be operational: telemetry on consent rates, multilingual prompt expansion beyond DE/EN, integration with corporate identity providers for enterprise-scoped reuse, and accessibility (sign-language consent paths).
