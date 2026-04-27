# Consent-Management Phase 4 (V3) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan.

**Goal:** PII pseudonymisation pipeline before any cloud call, cloud-provider abstraction with EU/US/Other categories, end-to-end revocation tooling, and optional consent-audio-snippet persistence.

**Architecture:** A new `IPreCloudPipeline` runs immediately before every outbound LLM request. It filters by `consent_scope`, runs local PII detection, pseudonymises with a reversible map kept locally, sends, then de-pseudonymises the response. A new `ICloudProviderRegistry` enumerates providers and their compliance category. Revocation extends Phase 1's state machine with full data-removal: redact stored transcripts, fire provider-specific deletion APIs, regenerate or delete summaries.

**Spec reference:** §5 pre-processing pipeline, §4.7 revocation, §2.3 optional snippet, §7 mode-specific cloud rules.

**Prerequisites:** Phase 3 V2 merged.

---

## Scope

**In:**
- Local PII detection (regex baseline + named-entity recogniser if available locally).
- Reversible pseudonymisation with per-session mapping table.
- `ICloudProviderRegistry` + EU/US/Other categories with AVV / SCC metadata.
- `IPreCloudPipeline` enforced at every cloud call site (audit + sample test).
- Revocation tooling: redaction of persisted transcripts, deletion calls to providers, summary regeneration.
- Optional `consent_audio_snippet.opus` capture (configurable, off by default outside Strict Mode).
- Encryption-at-rest for persisted artefacts (AES-256-GCM, key in OS keystore).

**Out:**
- Cross-session voice-embedding persistence (Phase 5).

---

## File Structure

**New:**
- `src/Pia.Wpf/Services/Consent/Privacy/IPiiDetector.cs` + `RegexPiiDetector.cs` + (optional) `NamedEntityPiiDetector.cs`.
- `src/Pia.Wpf/Services/Consent/Privacy/PseudonymisationMap.cs` — bidirectional dictionary, session-scoped.
- `src/Pia.Wpf/Services/Consent/Privacy/Pseudonymiser.cs` — applies map.
- `src/Pia.Wpf/Services/Consent/Privacy/IPreCloudPipeline.cs` + `PreCloudPipeline.cs`.
- `src/Pia.Wpf/Services/Consent/Cloud/ICloudProviderRegistry.cs` + `StaticCloudProviderRegistry.cs`.
- `src/Pia.Wpf/Services/Consent/Cloud/CloudProviderDescriptor.cs` — id, jurisdiction, category, AVV path.
- `src/Pia.Wpf/Services/Consent/Revocation/IRevocationService.cs` + `RevocationService.cs`.
- `src/Pia.Wpf/Services/Consent/Snippet/ConsentSnippetRecorder.cs` — captures the response audio segment when configured.
- `src/Pia.Wpf/Infrastructure/SessionEncryption.cs` — AES-256-GCM helpers.

**Modified:**
- All existing cloud call sites in the codebase. Audit them: `Pia.Services` chat clients, summarisation pipeline. Each must route through `IPreCloudPipeline`.

**New tests:**
- `RegexPiiDetectorTests.cs`
- `PseudonymiserRoundTripTests.cs`
- `PreCloudPipelineGateTests.cs` (refuses calls without scope)
- `RevocationServiceTests.cs`
- `ConsentSnippetRecorderTests.cs`

---

## Chunk 1: PII detection + pseudonymisation

### Task 1: `RegexPiiDetector`

Patterns to ship:
- German names (rough): heuristic uppercase-token sequences flagged for review.
- E-mail (RFC 5322 simplified).
- IBAN (any country).
- DE phone numbers + international `+` prefix.
- DE addresses (street + number heuristic).
- Credit card numbers (Luhn).

- [ ] Tests: each pattern has positive + negative samples; no false positive on innocuous text.
- [ ] Commit.

### Task 2: `Pseudonymiser` + `PseudonymisationMap`

- [ ] Map: `Dictionary<string original, string pseudonym>` with per-session salt; pseudonym format `[ENTITY-TYPE-N]` (e.g. `[NAME-1]`, `[IBAN-1]`).
- [ ] `Apply(text)` returns pseudonymised text.
- [ ] `Reverse(text)` swaps placeholders back.
- [ ] Round-trip property test: `Reverse(Apply(text)) == text` for any string built from PII patterns.
- [ ] Commit.

### Task 3: `IPreCloudPipeline`

```csharp
public interface IPreCloudPipeline
{
    Task<CloudCallContext> PrepareAsync(string transcript, ConsentScope scope, CancellationToken ct);
    Task<string> PostProcessAsync(string cloudResponse, CloudCallContext ctx, CancellationToken ct);
}
```

- [ ] `PrepareAsync` enforces:
  - `scope.AllowsCloud(provider.Category)` — else throws `CloudCallNotPermittedException` with audit event `CLOUD_CALL_BLOCKED`.
  - PII detected, pseudonymised, mapping retained in `ctx`.
  - Audit event `CLOUD_CALL_PREPARED` with provider id + category + pseudonym count, NOT content.
- [ ] `PostProcessAsync` reverses the map.
- [ ] Failing tests for all branches. Commit.

### Task 4: Refactor every cloud call site

- [ ] Grep for `chat.SendAsync` / equivalent. Identify call sites.
- [ ] Wrap each through `IPreCloudPipeline`.
- [ ] Add a single integration test that proves: with Strict Mode set, any cloud call attempt throws and audits.
- [ ] Commit.

---

## Chunk 2: Cloud provider registry

### Task 5: `CloudProviderDescriptor` + categories

```csharp
public enum CloudJurisdiction { EuOnly, UsAdequacyFramework, OtherThirdCountry }

public sealed record CloudProviderDescriptor(
    string Id, string DisplayName, CloudJurisdiction Jurisdiction,
    bool RequiresExplicitDrittlandConsent,
    string AvvDocumentationUrl);
```

- [ ] Static registry shipped with known providers (Mistral, Anthropic, OpenAI, etc., as available).
- [ ] Settings UI selector filtered by current security mode.
- [ ] Commit.

### Task 6: `ConsentScope` revisited

Phase 1 didn't include scope. Resurrect from spec §2.4:

```csharp
public sealed record ConsentScope(
    bool LocalProcessing, bool EuCloudProcessing, bool NonEuCloudProcessing,
    bool BiometricPersistence);
```

- [ ] Per-speaker scope stored in `SpeakerConsentEntry`.
- [ ] Prompt templates parameterised per scope (one prompt mentioning EU cloud, one mentioning US cloud, etc.).
- [ ] `IPreCloudPipeline` reads scope from the speaker entry of every utterance touching cloud.
- [ ] Tests + commit.

---

## Chunk 3: Revocation tooling

### Task 7: `IRevocationService`

```csharp
Task RevokeAsync(string speakerLabel, CancellationToken ct);
```

Workflow per spec §4.7:
1. Mark `Revoked`.
2. Add embedding to blocklist (Phase 3 component).
3. If transcript persisted: redact this speaker's segments (replace text with `[REVOKED]` placeholder, keep timestamps for audit), or delete entirely if user opts.
4. If summary cached locally: delete or regenerate without this speaker (regeneration triggers another consented cloud call, so default = delete).
5. If a cloud call already happened: invoke provider-specific deletion API where available; record an `OUTSTANDING_PROVIDER_DELETION` audit event for providers without deletion API.
6. Append `REVOCATION` audit event.
7. Keep original `ConsentEvidence` (as nachweis that consent did once exist) + add `RevocationEvidence`.

- [ ] Failing tests for each step (mock provider client, mock transcript store).
- [ ] Implement.
- [ ] Commit.

### Task 8: Revocation UI

- [ ] Add a "Widerruf" button per speaker in the meeting overlay AND a separate "Widerruf für vergangene Sitzungen" page that lists historical sessions and their speakers, gated by re-authentication.
- [ ] Confirmation dialog with summary of what will be deleted.
- [ ] Commit.

---

## Chunk 4: Optional consent-audio-snippet persistence

### Task 9: `ConsentSnippetRecorder`

- [ ] Captures the speaker's response audio (10–15 s window starting from prompt-end) when `SecurityProfile.PersistConsentAudioSnippet == true`.
- [ ] Encodes to Opus (libopus via existing audio dependency, or fall back to WAV if not available — document choice).
- [ ] Stores under `session_<uuid>/consent/speaker_<id>_grant.opus`, encrypted via `SessionEncryption`.
- [ ] **This is a controlled exception to "no audio persisted" — make the exception loud:** logs, audit event `SNIPPET_PERSISTED`, separate consent-evidence file lists `audio_snippet_path`.
- [ ] Tests: file written only when flag true; file is ungreppable (encrypted); file cleared by `RevocationService`.
- [ ] Commit.

### Task 10: Encryption-at-rest

- [ ] `SessionEncryption` derives a session key from a master key in DPAPI; manifest stores key id only.
- [ ] All persisted artefacts (transcripts, snippets, evidence) wrapped through this.
- [ ] Tests + commit.

---

## Chunk 5: Verification + tag

### Task 11: End-to-end privacy test

- [ ] Drive a session in Standard Mode. Speaker says PII-rich text. Cloud summary is requested.
- [ ] Assert via captured HTTP traffic (tests with `HttpClientFactory` mock): the request body contains zero original PII tokens (only pseudonyms).
- [ ] Response is reverse-mapped before display.
- [ ] Commit.

### Task 12: Revocation E2E

- [ ] Complete a session with persisted transcript + snippet.
- [ ] Revoke the speaker.
- [ ] Verify: transcript redacted, snippet file deleted, audit chain extended with `REVOCATION`.
- [ ] Verify: `ConsentEvidence` still on disk (audit purposes).
- [ ] Commit.

### Task 13: Tag

```bash
git tag consent-phase4-v3
```
