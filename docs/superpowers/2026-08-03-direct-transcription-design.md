# Direct Transcription with Spoken-Consent Gate — Design

**Date:** 2026-08-03 · **Branch:** `feature/direct-transcription` · **Status:** Design — owner decisions resolved (§8), build plan issued, not yet implemented

> **Revision 2026-08-03b.** Rewritten against the owner's five decisions (§8) and a full grounding sweep of both
> the old branch (`origin/feature/meeting_transscription` @ 966deab) and the current tree. §3.5 (four-component
> consent with a hard, fuzzy-matched Pia reference) and §8 changed materially; §3.2/§3.3/§3.6–§3.10, §4, §5 and §6
> were corrected where the grounding showed a claim was stale.

## 1. Goal

A direct (local) transcription session that needs no Teams meeting and no browser:

- Captures the **local microphone** and the **system audio output** (WASAPI loopback) simultaneously.
- Everything from the microphone is transcribed unconditionally and always labeled **"me"** (localized: me/ich/moi).
- Voices on the loopback side are **not transcribed** until that speaker utters a genuine consent sentence, e.g.:
  - EN: *"My name is John Doe and I accept that this meeting gets recorded by Pia."*
  - DE: *"Mein Name ist John Doe und ich bin einverstanden, dass dieses Gespräch von Pia aufgezeichnet wird."*
  - FR: *"Je m'appelle John Doe et j'accepte que cette réunion soit enregistrée par Pia."*
- The spoken name is extracted and **bound to the speaker's voice** (diarizer cluster), so their bubbles show "John Doe" instead of "Speaker 2".
- Consent unlocks **voice-data measurement** for that speaker (per-speaker speech statistics in-session; optional persistent voice profile as a later phase).

## 2. Grounding — what exists

Two prior bodies of work feed this design:

**Current branch** (`feature/agent-run-spine` lineage): the live-transcription pipeline used by the Teams attendee. Per-source pipeline `IAudioCaptureSource` (16 kHz mono float32, ~800-sample hops) → `LiveTranscriptionEngineService` (energy VAD → bounded segment queue → optional diarization → shared `ITranscriptionEngine` → `TranscriptUtterance` sink). Shared UI base `TranscriptOverlayViewModel` (bubbles, journal, rename, relabel, Markdown save). Engines: Whisper (explicit `en/de/fr/auto`) and Parakeet TDT v3 (multilingual auto). Diarizers: manual `SpeakerIdentificationService` (stable labels) and `AdaptiveSpeakerIdentificationService` (re-clustering, **retroactive relabels** via `SpeakersReassigned`). No microphone capture source exists. `TranscriptSpeaker.You` exists but has no producer.

**Old branch** (`origin/feature/meeting_transscription`): a complete `LiveMeetingService` (mic + loopback, consent-gated) plus a five-phase consent stack (~60 files, well-tested): `ConsentStateManager`, `ConsentGate`, rule/LLM consent classifiers, pre-consent ring buffers, hash-chained signed audit log, biometric consent store (encrypted, 12-month retention, cross-session voice-print match), revocation, blocklist, security profiles. **But**: its consent model is Pia-prompted German-TTS yes/no — no consent sentence, no name extraction, no French. It also diverged from current APIs (engine ctor reshaped, `TranscriptChannel` enum deleted, `SherpaOnnxVadDetector` deleted, pause/resume deleted, model-download signatures changed).

Known defects in the old implementation this design must not inherit:

| # | Old defect | Consequence |
|---|-----------|-------------|
| D1 | Loopback segments < 1.5 s get `SpeakerLabel = null`; **two** independent bypasses — the engine's own gate was guarded by `speakerLabel is not null` so it never evaluated them, *and* the forwarder treated null-label as "trusted mic" | **Consent bypass** for short loopback speech |
| D2 | `_rawUtterances` channel created once in ctor, completed in every `StopAsync` | **Resume after Stop produces no utterances** |
| D3 | Gate placement described as "pre-STT" but actually post-STT | Docs/comments lied; text discarded, audio transcribed anyway |
| D4 | Ambiguous → TTS clarification → re-prompt with no retry cap | Infinite prompt loop |
| D5 | `no problem` classified as Deny (deny tokens stripped before grant scan) | Wrong (fail-safe direction) |
| D6 | Ring buffers wiped only on Grant, never on Deny/Timeout/session end | Unconsented audio lingers in RAM |
| D7 | Consent evidence/snippet/retention workers never wired (`consentEvidencePath: ""`) | Nachweispflicht unfulfilled despite spec |

## 3. Architecture

### 3.1 Component overview

```
                     ┌────────────────────────────── DirectTranscriptionService (singleton) ─┐
 MicAudioCapture ──► │ LiveTranscriptionEngineService (You, no diarizer)  ─┐                  │
 (ported, WaveInEvent│                                                     ├─► _raw channel  │
  16k/16bit mono)    │ LiveTranscriptionEngineService (Them, manual        │   (per session) │
 LoopbackAudioCapture│  SpeakerIdentificationService, minDiar 1.5 s)      ─┘        │        │
 (existing, as-is) ──►                                                              ▼        │
                     │                        ConsentForwardLoop (THE gate, post-STT)        │
                     │   Speaker==You ──────────────────────────────► emit                   │
                     │   Speaker==Them, label==null ────────────────► drop (fixes D1)        │
                     │   Speaker==Them, state==Granted ─────────────► emit                   │
                     │   else ► NamedConsentClassifier(text):                                │
                     │            match  ► Grant + bind name + emit consent utterance        │
                     │            no match ► drop (text zeroed, audio already released)      │
                     └────────────────────────────────────────► _public channel ► ViewModel ─┘
```

- **No changes to `LiveTranscriptionEngineService`.** The old branch wove the gate into the engine (4 extra ctor params + pause/resume). This design keeps the engine untouched: both engines write *unfiltered* into a **private** raw channel owned by the orchestrator; the single consumer is the consent forward loop. The privacy boundary is the forward loop — the raw channel is not observable from outside the service. This gives zero regression risk for the Teams-attendee path and no ctor churn.
- **Gate is explicitly post-STT** (and documented as such): unconsented loopback audio *is* transcribed locally in RAM — that is unavoidable, since consent detection requires reading the text — and the text of unconsented speakers is dropped before it can reach any channel, UI, log, or disk.
- **No pre-consent ring buffers in v1.** The old branch buffered 30 s/speaker of pre-consent audio only to discard it on grant (replay was forbidden by its own spec §3.9). Not buffering at all is simpler and strictly more private. The consent *evidence* is the transcribed sentence itself (§3.6).
- Mic engine gets **no diarizer** — every mic utterance is `TranscriptSpeaker.You`. Loopback engine gets the **manual** diarizer (see §3.4).
- **The order of the switch is load-bearing.** `Speaker == You` must be tested **first**, because the mic engine has no diarizer and therefore produces `SpeakerLabel == null` for *every* utterance — a null-label check placed first would silently drop the entire microphone side. Then the null-label drop, then the consent state. The null-label drop must be unconditional: besides the sub-1.5 s case there is a third producer of null labels — a diarizer exception, which the engine catches and swallows for a segment of any length.
- The `minDiar 1.5 s` figure is the current engine ctor's **default** (`16000 * 3 / 2`); the loopback engine passes no explicit value.

### 3.2 New service: `DirectTranscriptionService`

`Pia.Services.LiveTranscription.DirectTranscriptionService : IDirectTranscriptionService` (new interface in `Pia.Services.Interfaces`), DI singleton. Shape ported from old `LiveMeetingService`, reconciled to current APIs, with the `MeetingAttendeeService` factory-seam pattern for testability:

- **States**: `Idle, Preparing, Prepared, Starting, Running, Stopping, Error` (no Paused — no Strategy A). The enum is **net-new**: `LiveMeetingState`, `ILiveMeetingService` and `SpeakingChangedEventArgs` were all deleted from the current branch (only two dangling `<see cref>` mentions survive in `IMeetingAttendeeService.cs`), so there is nothing to import — only the *shape* is reused.
- **`PrepareAsync`** (idempotent warmup, runs while the disclaimer shows): `EnsureSileroVadAsync(httpClientFactory, logger, ct)` (current signature — **no** progress param), `TranscriptionEngineFactory.CreateAsync` (unchanged), `EnsureSpeakerEmbeddingAsync(httpClientFactory, logger, ct, progress)` (**progress moved to last, after the token**) + manual `SpeakerIdentificationService(path, settings.SpeakerEmbeddingThreshold, maxSpeakers: settings.MeetingMaxSpeakers, logger)`. Speaker-model failure is **fatal for start** here (unlike the attendee's degrade-to-null): without diarization there is no per-speaker consent, so a consent-gated session must not silently degrade into "one anonymous speaker". Surface a localized error instead. `PrepareAsync` also **establishes the session**: new session id, `IConsentStateManager.ResetSession()`, fresh diarizer.
- **`StartAsync`**: build fresh `MicAudioCaptureService` + `LoopbackAudioCaptureService`, build a **fresh raw channel per start** (fixes D2; the *public* channel stays stable for the service lifetime, `SingleReader` contract preserved), construct both engines with the **current** ctor `(speaker, source, sileroVadModelPath, engine, sink, logger, speakerId, minDiarizationSamples)` — note the vad-path/engine argument order differs from the old branch — start forward loop, then sources, then engines. Sources start last-possible — the privacy boundary comment carries over. A `LiveTranscriptionEngineService` and a `LoopbackAudioCaptureService` are both **single-use** (`StartAsync` throws once started, `DisposeAsync` tears down the VAD/WASAPI objects), which is why every start builds fresh instances.
- **`StopAsync`** = *pause the pipeline*: atomic check-and-set (copy `MeetingAttendeeService`'s `_stateLock` pattern — the old branch's guard was not atomic and a double stop over-releases the WASAPI COM objects), stop sources → **`DisposeAsync` both engines and await them** → dispose sources → **then** complete the raw channel → await the forward loop → return to `Prepared`. The engine has no `StopAsync`: `_vad.Drain()`, the trailing segment and its final `_sink.WriteAsync` all happen inside `DisposeAsync`, and the sink write is `WriteAsync` (not `TryWrite`) — completing the raw channel first would throw `ChannelClosedException` and silently lose the trailing utterance. `StopAsync` **preserves** the diarizer, the shared `ITranscriptionEngine` and the consent map, so Resume is fast and a consented speaker does not have to re-consent after a pause.
- **`EndSessionAsync`** = *end the session*: `StopAsync` if needed, then dispose the shared `ITranscriptionEngine` and the diarizer **last** (native ONNX — it must not be disposed while a segment is in flight), `ResetSession()` on the consent map, rotate the session id, return to `Idle`. The public channel completes only in `DisposeAsync`.
- **Events**: `StateChanged`, `SpeakerConsentChanged(label, oldState, newState, name?)` (new, for per-speaker UI chips), `SpeakerRegistered` forwarded from the diarizer ("Speaker N detected — awaiting consent"), `SpeakingChanged(speaker, isSpeaking)` per side (mic level indicator; `SpeakingChangedEventArgs` is re-declared, not imported). All of these fire on background threads — `SpeakerRegistered` in particular fires on the engine's segment loop, *inside* `TranscribeSegmentAsync` and *before* the utterance reaches the raw channel, so "the forward loop is single-threaded" does **not** make its handlers race-free. Subscribers marshal via `IUiDispatcher`; the consent map keeps its own lock.
- **`RenameSpeaker(old,new)`** delegates to diarizer + consent map (rename preserves consent state, as on the old branch). **`RevokeSpeaker(label)`** flips the consent map to `Revoked`.

### 3.3 The consent model (simplified state machine)

The old prompt-driven machine (Prompted/Timeout/Ambiguous + TTS + 2 s sweep) does not fit a speaker-initiated model and is dropped. States for v1:

```
Unknown ──(consent sentence recognized)──► Granted ──(UI revoke)──► Revoked
   │  utterances: transcribed in RAM,                 utterances: dropped again;
   └─ classified for consent, then dropped            bubbles of that speaker removed
```

- `ConsentStateManager`'s *mechanics* are ported (one `_lock`, mutate-inside / log-and-raise-outside, a `Raise` that try/catches subscriber throws so a bad subscriber cannot break the state machine, `Rename` mutates the dictionary key and deliberately raises no event because state is preserved). Its *surface* is reshaped: the enum is **trimmed to `Unknown, Granted, Revoked`** — the prompt machine is gone, so `Prompted/Timeout/Ambiguous/Denied` would be members no code can ever produce — and `RecordClassification(label, classification, transcriptText, promptHash, promptText, sttModelId)` is replaced by `Grant(label, extractedName, evidence)` / `Revoke(label)`. Consequences: no `MarkPrompted`, no `SweepTimeouts`, no `PromptTimeout`, and **no `GrantConfidenceThreshold` on the manager** (§3.5 owns the one threshold).
- `SpeakerConsentEntry` becomes an **immutable record snapshot**. The old class handed the live mutable entry out from under the lock, which was safe only while a single background task mutated it; v1 has a background forward loop *and* a UI revoke on the dispatcher thread, so snapshots are the only lock-honest shape. Its `ConsentScope`/`BiometricMatchSource`/`Embedding` members are dropped (they point into the left-behind cloud/biometric subtrees).
- **Consent is session-scoped.** `PrepareAsync` starts everyone at `Unknown` via `ResetSession()`. Because the manager is a DI **singleton**, forgetting that reset would leak consent from one session into the next inside a single app run. `StopAsync`/Resume is *not* a new session (§3.2). Cross-session voice-print reuse (old Phase 5 biometric store) is explicitly **v2** (§7, owner decision D-3).
- The **consent utterance itself is emitted** to the transcript (owner decision D-2: the visible, in-band record that consent was given, mirrored in the saved Markdown). Everything the speaker said *before* it is gone — never buffered, never replayed.
- Revocation (v1): a UI action on the speaker's chip. It flips state to `Revoked` (future utterances drop) and removes that speaker's bubbles + journal entries from the in-memory transcript. That removal path does **not** exist on the shared base today (`RelabelSpeaker` rewrites labels; there is no remove-by-label), so `TranscriptOverlayViewModel` gains one additive `protected void RemoveSpeaker(string label)` (§3.9). Spoken revocation sentences: v2.

### 3.4 Diarizer choice: manual, not adaptive — a consent-safety decision

The adaptive diarizer retroactively reassigns segment labels after re-cluster passes. Under a consent gate this is unsound in both directions: an utterance emitted under a Granted label could be retroactively reassigned to an unconsented speaker (already displayed — a consent violation), and dropped utterances of a "wrong" label can never be retroactively emitted (audio is gone, replay forbidden). **The direct-transcription session therefore always uses the manual `SpeakerIdentificationService`** (stable, monotonic labels, no reassignment), regardless of `MeetingSmartSpeakerDetection`. That setting keeps governing the Teams attendee only. Documented trade-off: occasional over-splitting (same voice → two labels) means a speaker may need to re-consent if the diarizer forks their cluster; the fuzzy matcher + rename mitigate.

### 3.5 `NamedConsentClassifier` (net-new)

`Pia.Services.Consent.NamedConsentClassifier : INamedConsentClassifier` — input: utterance text + active language hint (`TargetSpeechLanguage`); output: `NamedConsentResult(bool IsConsent, string? ExtractedName, string Language, float Confidence)`. The method is **synchronous** (pure string work, no I/O) — see the LLM note below.

A consent sentence must contain **four** components, **within one utterance** (one VAD segment — that is the "genuine sentence" requirement):

1. **Name introduction** with a capturable name: `my name is (…)` / `i am (…)`, `mein name ist (…)` / `ich heiße (…)` / `ich bin (…)`, `je m'appelle (…)` / `mon nom est (…)`.
2. **Acceptance verb**: accept / agree / consent; bin einverstanden / akzeptiere / stimme zu; j'accepte / je consens / je suis d'accord.
3. **Recording reference**: recorded / recording / aufgezeichnet / aufgenommen / Aufzeichnung / enregistrée / enregistrement.
4. **A reference to Pia** — **hard requirement** (owner decision D-1, 2026-08-03), satisfied by a **fuzzy** match.
   meeting/conversation/Gespräch/réunion words remain **boosters only**: in v1 they change neither the decision nor the confidence (kept in the lexicon for the v2 LLM assist), so no reviewer should expect them to matter.

**Why Pia is a hard requirement and still fuzzy.** Requiring the assistant's name is what makes the sentence an act of consent *to this recorder* rather than an overheard remark about recording in general — a participant saying "I'm fine with being recorded" to someone else in the room must not unlock the gate. But STT mangles a 3-letter proper noun, so the match cannot be literal. The rule is:

- A token is a Pia reference iff **(a)** it is in the explicit STT-alias set, **or** **(b)** its length ≥ 4, `Levenshtein(token, "pia") ≤ 1`, and it is not in the false-friend blocklist.
- **Alias set** (deliberate, per-language justification): `pia`, `pias`, `pia's` (EN possessive survives normalization), `pea`, `peas`, `peer`, `pier` (EN STT of /ˈpiːə/ — the doc's own examples), `piya`, `peeya` (EN phonetic spellings), `pija`, `piha`, `bia` (DE: /j/-glide and p→b voicing confusion), `pya`, `piat` (FR: glide and orthographic silent-t), plus the letter-spelled sequence `p i a` matched as three consecutive single-letter tokens.
- **Blocklist for rule (b)** — real words one edit from "pia" that would otherwise false-positive: `pita`, `pisa`, `pima`, `pika`. Rule (b) is length-gated at ≥ 4 precisely because Levenshtein ≤ 1 on a 3-letter word admits `via`, `pie`, `pin`, `pit`, `pig`, `pip` — too loose for a hard requirement.
- The Pia token may sit anywhere in the utterance; the preposition (`by`/`von`/`durch`/`par`) is **not** required, because STT drops unstressed function words.

Matching rules:

- Normalize with the old rule classifier's `Normalize` (lowercase; keep letters/digits/whitespace/apostrophe, replace every other char with a **space** so punctuation stays a token boundary and `\b`-anchored patterns still match at sentence ends). Do **not** reuse the old `StripMatches` — that is the D5 carrier.
- Per-language keyword matching with fuzzy tolerance (Levenshtein ≤ 1 per token, for lexicon words ≥ 5 chars only) to absorb STT errors.
- Name = capture between the name marker and the conjunction (`and/und/et`) or end of utterance, 1–4 tokens, letters/hyphens/apostrophes only, title-cased. Empty or non-name captures ⇒ no consent.
- Negation guard: any negation token (`not/don't/nicht/kein/ne…pas`) scoped to the acceptance clause ⇒ **not** consent (never "deny" — absence of consent already means dropped). This dodges the old D5 strip-order bug class entirely: there is no deny lexicon competing with the grant lexicon, and `no problem` / `kein Problem` cannot be misread as refusal.
- **Confidence model (four components).** All four matched **crisply** (exact lexicon hit, no Levenshtein repair; Pia matched as the exact token `pia`) ⇒ **0.95**. Any of the four satisfied only by a **repair** (Levenshtein-repaired keyword, or Pia matched via alias/rule (b) rather than exact `pia`) ⇒ **0.85**. Any component missing, or the negation guard tripping ⇒ `IsConsent = false`, confidence `0`. Grant threshold **0.85**, declared once as `NamedConsentClassifier.GrantConfidenceThreshold`. There is deliberately **no** second threshold in `ConsentStateManager`: the manager exposes `Grant(...)` and does not re-judge confidence (the old branch's `RecordClassification` demoted anything below `0.9f` to `Ambiguous`, which would have silently swallowed every fuzzy-repaired grant).
- Try the hinted language first, then the other two (people answer in their own language regardless of the session setting). `TargetSpeechLanguage.Auto` ⇒ try en, de, fr in that order. The winning language is reported in the result and stored in the evidence.
- **No LLM fallback in v1.** The old `CascadingConsentClassifier` took *concrete* rule+LLM classifiers and the LLM classifier itself is left behind, so porting a permanently-disabled cascade would ship dead code. The gate is fully offline by construction; a v2 LLM assist would arrive as an async decorator over `INamedConsentClassifier` and could never overrule the negation guard.
- The classifier ships with a table-driven test suite per language (verbatim sentences, STT-mangled variants, near-misses that must NOT match: name without acceptance, acceptance without name, **no Pia reference**, negated acceptance, consent split across two utterances).

On grant, the forward loop (single-threaded — it is the sole raw-channel reader, so no gate/grant race exists by construction):

1. `ConsentStateManager` → `Granted` with `ConsentEvidence(sentence text, confidence, timestamp, language, sttModelId)`.
2. `RenameSpeaker(diarizer label → extracted name)` (diarizer + consent map + UI relabel via the existing `TranscriptOverlayViewModel.RelabelSpeaker`).
3. Emit the consent utterance (already relabeled).
4. Append audit + evidence records (§3.6).

### 3.6 Consent evidence & audit (fixes D7)

Their own spec (old branch, `consent-management-spezifikation.md`) demands persistable consent evidence (Art. 7 DSGVO Nachweispflicht). v1 keeps this proportionate:

- **`ConsentEvidenceStore` (new, small)**: one JSON file per grant under `%LOCALAPPDATA%\Pia\ConsentEvidence\<session>\<label>.json` — extracted name, full consent sentence, language, confidence, session id, granted-at, STT model id. DPAPI-protected (reuse `DpapiHelper`). Written at grant time (not on session end — a crash must not lose evidence). Revocation appends a revocation record beside it (evidence is preserved, per the old branch's tested rule). **Failure mode to guard:** `DpapiHelper.Encrypt`/`Decrypt` return `string.Empty` instead of throwing on any `CryptographicException`/`FormatException`, so an empty return for non-empty input must be treated as a **write failure** — otherwise a DPAPI fault silently persists an empty, unrecoverable evidence file, which is exactly the D7 gap.
- **Audit log**: port `JsonlConsentAuditLog` + `AuditEvent` (metadata only, never transcript text, **never the extracted name** — the name is personal data and lives only in the DPAPI-protected evidence file) under `%LOCALAPPDATA%\Pia\ConsentAudit\`. Event types used in v1: `SESSION_STARTED`, `SESSION_STOPPED`, `SPEAKER_DETECTED`, `CONSENT_GRANTED`, `CONSENT_REVOKED`, `EVIDENCE_WRITE_FAILED`, `DROPPED_UNLABELED_LOOPBACK` and `DROPPED_UNCONSENTED` (counters, batched at stop — one line per utterance would itself be a transcript-shaped record). `AuditEvent`'s `PreviousEventHash`/`Signature` fields are **dropped** in v1 (the hash-chained/signed variant, its `AuditChainSigner` DPAPI key and the `verify-audit-chain` CLI are all v2; carrying two always-null fields is not "the mechanism ported"). Note the class does **not** create its parent directory and its `FileStream` is opened inside the background drain task, so a missing directory surfaces only as one logged line and then swallows every append — directory creation belongs to the static session factory.
- Not in v1 (owner decision D-4): expiry stamps, a retention/cleanup worker, snippet audio evidence.

### 3.7 Mic = "me" labeling

`SpeakerToDisplayNameConverter.Resolve` currently hardcodes lowercase `"you"` for `TranscriptSpeaker.You`. Change it to return the localized resource `Speaker_Me` (en "me", de "ich", fr "moi"), added to `CommonStrings.resx` + `.de.resx` + `.fr.resx`. The Markdown export resolves through the same method and gets the same label.

Two caveats the "safe: `You` has no producer" argument does not cover:

- The converter is an App.xaml `StaticResource` built with the implicit parameterless ctor and `Resolve` is `static`, so localization must go through `LocalizationSource.Instance["Speaker_Me"]` (precedent: five existing converters). Constructor-injecting `ILocalizationService` is not available without touching App.xaml.
- `Convert` falls back to `TranscriptSpeaker.You` for *any* `values[0]` that is not a `TranscriptSpeaker` (including `DependencyProperty.UnsetValue`), so a broken binding in **either** overlay would start rendering the localized "me"/"ich"/"moi". Keep that fallback as-is, but be aware of it.
- `tests/Pia.Wpf.Tests/Converters/SpeakerToDisplayNameConverterTests.cs` asserts the literal `"you"` (and `"them"` twice). Those assertions must be updated in the same change — they compile either way, and `dotnet test` does not run on the dev machine, so nothing else would catch them.

### 3.8 Voice-data measurement

- Add `double? DurationSeconds = null` to `TranscriptUtterance` as the **6th, last** positional parameter (additive: the record has exactly one production construction site, no `with` expressions, no deconstruction and no positional patterns anywhere in src or tests — verified). The engine knows `samples.Length / 16000.0` at emit time; a one-line addition at the emit site is **the only engine change**.
- **Aggregation lives in the service, not the ViewModel.** The forward loop is the only place that knows both the duration and the consent decision, so it records one `VoiceSample` per *emitted* utterance and exposes `GetVoiceStats()`; the arithmetic sits in a pure static `VoiceStatsCalculator`. Putting it in the ViewModel would need a per-utterance hook on the shared base (`ConsumeUtterancesAsync` is private) and would put measurement *outside* the privacy boundary — the opposite of the invariant below.
- Per consented speaker: total speech time, utterance count, mean utterance length, share of measured speech. Shown in a stats flyout and written into the saved transcript's YAML front matter.
- Unconsented speech is **not** measured (its utterances never leave the forward loop) — measurement is a consent benefit by design, which is exactly the requested behavior ("this should greatly help to measure the voice data").
- Persistent per-person voice profiles (embedding enrollment via the old biometric store): **v2** (§7). Owner decision D-3: v1 ships **no** `IVoiceProfileStore` seam — no speculative extension point.

### 3.9 UI

New `DirectTranscriptionViewModel : TranscriptOverlayViewModel` (current base ctor: `ISettingsService, ILocalizationService, IFileDialogService, ILogger, IUiDispatcher`; bubbles/journal/rebuild/relabel/save inherited) + `DirectTranscriptionOverlay` UserControl, hosted in `AssistantView.xaml` exactly like the meeting attendee (toolbar toggle button — mic icon — `IsDirectTranscriptionVisible`, overlay at `Grid.RowSpan=3`, `Panel.ZIndex=12` — 9/10/11/20 are taken). **Mutual exclusion is net-new logic**: `IsVoiceModeActive` and `IsMeetingAttendeeVisible` are independent flags today and neither toggle consults the other, so "opening one closes the other" has to be written into `AssistantViewModel`'s toggle methods.

The shared base needs exactly **two additive changes** — the design's "save inherited" is not achievable otherwise, because `SaveTranscriptAsync()` is `private` and `BuildMarkdown()` is `internal` and non-virtual:

1. `internal string BuildMarkdown()` → `internal virtual string BuildMarkdown()` (accessibility unchanged, so the existing `MeetingAttendeeViewModelTests` calls still compile) so the subclass can prepend YAML front matter + the stats block.
2. `protected void RemoveSpeaker(string speakerLabel)` — filters the journal, releases the palette slot, rebuilds the bubbles (for §3.3 revoke).

Neither changes any observable behaviour of the Teams-attendee overlay.

- **Disclaimer/start panel** (ported pattern): explains local-only processing, mic + system-audio capture; shows the consent sentence **in all three languages** so the host can read it to participants or share it; ToggleSwitch gates Start; `PrepareAsync` warms models underneath (old `BeginWarmup` pattern).
- **Per-speaker consent chips** (new; replaces the old single global badge): one chip per detected loopback speaker — "Speaker 2 · awaiting consent" (muted) → "John Doe · consented ✓" (accent) — driven by `SpeakerRegistered` + `SpeakerConsentChanged`. Chip context menu: rename, revoke.
- Bubbles: mic right-aligned "me" bubbles; consented loopback speakers left-aligned with the existing 5-color palette. Unconsented speakers produce no bubbles at all.
- Footer: Stop / Resume / Save / Save & Summarize. The old silent-save-then-hand-over-a-path flow cannot be ported verbatim: both types it needed (`MeetingSummarizationRequest`, `PathShortener`) were deleted from the current branch, and the current pattern is `event EventHandler<string>? SummarizeRequested` carrying a **fully built prompt string** with no file path. v1 therefore does prompt-only (`BuildSummaryPrompt()` = localized instruction + the front-matter-free Markdown), matching `MeetingAttendeeViewModel`.
- All new strings in `CommonStrings.resx` + `.de.resx` + `.fr.resx`. This is not an incremental addition: **all 30 `LiveTrans_*` keys were deleted from the current branch** (grep count 0), so every disclaimer/status/consent/chip/footer string is a fresh three-locale entry, and `LocalizationTests` fails on missing *and* orphaned keys in both directions.

### 3.10 Settings, DI, arch tests

- **No off-switch for the consent gate.** It is not an `AppSettings` field; a toggle that disables a GDPR gate is a liability, and the feature is meaningless without it.
- New settings (local-only, no sync mirror): `DirectTranscriptFolder` (empty ⇒ reuse `MeetingTranscriptPaths` default). Mic/loopback device pickers: **v2** (no device settings exist anywhere today; both captures use default devices, consistent with `AudioRecordingService`).
- DI (Bootstrapper): `AddSingleton<IDirectTranscriptionService, DirectTranscriptionService>`, `AddSingleton<IConsentStateManager, ConsentStateManager>`, `INamedConsentClassifier`, `IConsentAuditLog` (factory: `Directory.CreateDirectory` + per-session `session_{guid:N}.jsonl`), `IConsentEvidenceStore` (factory: root dir + `DpapiHelper`), `AddScoped<DirectTranscriptionViewModel>`. **`services.AddSingleton(TimeProvider.System)` must be added**: `TimeProvider` is registered nowhere on the current branch (the old branch registered it inside the left-behind biometric block) and `ConsentStateManager`'s ctor requires it, so the plain registration would throw at first resolve — a startup crash, not a compile error.
- Arch tests: interfaces in `Pia.Services.Interfaces` must be registered or exempted (`DiRegistrationTests` sweeps only `Pia.Services.Interfaces` + `.E2EE` + `.MeetingAttendee`, so consent interfaces are invisible to it — a coverage hole worth closing with a fourth enumeration block, not a reason to hide interfaces there). The service-suffix carve-out is a **two-file** edit: add `ConsentNamespace = "Pia.Services.Consent"` to `ArchitectureTestBase` *and* `.And().DoNotResideInNamespace(ConsentNamespace)` to `NamingConventionTests.ServiceClasses_MustFollowNamingConvention` — without it, `NamedConsentClassifier`, `ConsentStateManager` and `JsonlConsentAuditLog` all fail (neither "Classifier", "Manager" nor "Log" is an allowed suffix). Three further rules reach the new namespace by prefix and get **no** carve-out: `Services_MustNotHave_AsyncVoidMethods`, `Services_ShouldNot_DependOn_ViewModels`, `Services_MustNotInject_ViewModels`.
- Logging: every ported/new file follows the privacy policy — consent sentences, names, labels only via `SensitiveDebug`/`SensitiveInformation`; the old consent files predate `SafeUrl`/`Sensitive*` and must be swept during the port.

## 4. Port manifest

**As-is (old branch → current, trivial fixes only):** `MicAudioCaptureService` — **one file**, `public static class PcmConversion` is declared at the top of it, there is no separate `PcmConversion.cs`; the current `IAudioCaptureSource` is byte-identical to the old one, so it is a clean drop-in (sweep the two log lines that print mic **device product names** into `SensitiveDebug`). `ConsentState` (trimmed, §3.3), `IConsentAuditLog`, `JsonlConsentAuditLog` (+ tests), `ConsentStateManager`'s lock/event mechanics, `RuleBasedConsentClassifier.Normalize` (that helper only — **not** `StripMatches`), `tests/.../FakeTimeProvider.cs` as source (no `Microsoft.Extensions.TimeProvider.Testing` package is referenced), the `SpeakerRegistered` bootstrap hook, the old `BeginWarmup` / disclaimer / status-mapping VM patterns.

**Reshaped, not copied:** `SpeakerConsentEntry` (→ immutable record; drops `ConsentScope`/`BiometricMatchSource`/`Embedding`, which would otherwise drag ~15 left-behind files into the compile), `ConsentEvidence` (`PromptVersionHash`/`PromptTextPlayed` → `Language` + name/label fields), `IConsentStateManager` (`Grant`/`Revoke`/`ResetSession` instead of `RecordClassification`/`MarkPrompted`/`SweepTimeouts`), `AuditEvent` (drop the two v2 hash-chain fields), `IConsentClassifier` → `INamedConsentClassifier` (the old `promptText` parameter existed only for the TTS flow; it becomes the language hint).

**Adapted:** `LiveMeetingService` → `DirectTranscriptionService` (current engine ctor **argument order**, current model-download signatures, fresh-raw-channel-per-start, forward-loop gate, atomic stop guard, no TTS/orchestrators/sweep, no `TranscriptChannel` branch — that enum is deleted), `LiveTranscriptionViewModel` → `DirectTranscriptionViewModel` (re-based on current `TranscriptOverlayViewModel`), `LiveTranscriptionOverlay` → `DirectTranscriptionOverlay` (per-speaker chips instead of the global badge; **every** `SpeakerToDisplayNameConverter` MultiBinding needs the third `CounterpartName` binding — the current `Convert` returns `string.Empty` for fewer than 3 values, so a verbatim 2-binding port renders every speaker name blank with no error), `SpeakerToDisplayNameConverter` ("me").

**Net-new:** `NamedConsentClassifier` + a `Levenshtein` helper (none exists; only `JaroWinkler`) + trilingual test tables, `ConsentEvidenceStore`, `IDirectTranscriptionService` and its state/event types (all three old types were deleted), `SpeakerConsentChanged` plumbing, per-speaker chips UI, `VoiceStatsCalculator` + `SpeakerVoiceStats`, `TranscriptUtterance.DurationSeconds`, `DirectTranscriptMarkdown`, all ~40 resx keys × 3 locales.

**Dropped from the port after grounding:** `MeetingTranscriptWriter` + `MeetingFrontMatter` + their 9 tests. Both types were deleted from the current branch; the writer's `Resolve` call sites and its tests assert the *old* two-arg converter and the old `"you"`/`"Speaker"` labels; and its `StripFrontMatter`/`TryParseFrontMatter` half has **no consumer** on this branch (nothing reads transcript Markdown back). Only the YAML *pattern* is reused, by the new `DirectTranscriptMarkdown` renderer.

**Left behind (deliberate):** Strategy A/B orchestrators + engine Pause/Resume + `SecurityProfile`/`SecurityModeProvider` (one behavior: selective gating), TTS prompt flow + `ConsentPromptTemplates` + timeout sweep (speaker-initiated model), `PerSpeakerRingBufferRegistry`/`SpeakerRingBuffer` (no buffering in v1), `ConsentGate` (the forward loop's switch replaces the state-map indirection), `PostSttDefenseFilter` + `TranscriptChannel` (single gate point; no second channel to defend), Cloud/ + Privacy/ subtrees (no cloud in this path), `ConservativeCrossTalkResolver` (no multi-label producer; limitation documented instead), `SherpaOnnxVadDetector` (current VAD stays), LLM consent classifier wiring (interface ported, disabled by default).

## 5. Known limitations (documented, accepted for v1)

1. **Cross-talk** (inherited, was documented on the old branch): one embedding/label per VAD segment; under overlap the dominant voice wins and a quieter unconsented speaker's words could ride along in a consented segment. Mitigation: none feasible with the current diarizer; documented in the disclaimer text.
2. **Mic is single-identity**: everyone speaking into the local mic is "me". Multiple people in the local room are not distinguished (and are implicitly consented via the host). Documented in the disclaimer.
3. **Local STT of unconsented audio**: consent detection requires transcribing in RAM. Text is dropped synchronously in the forward loop; nothing unconsented is displayed, logged, persisted, or leaves the process.
4. **Diarizer over-splitting** may require a speaker to re-consent if their voice forks into a new cluster (manual diarizer, no re-clustering).
5. **Pia's own TTS / system sounds / music** arrive on loopback and are gated like any unconsented speaker (they never match the consent sentence — self-filtering, but they do burn STT cycles). Note `AppSettings.MeetingMaxSpeakers` defaults to **`0` = unlimited**, so it caps nothing with the shipped default; if a cap matters, the direct session must pass its own non-zero value.
6. **Short loopback speech is unattributable and therefore dropped** — for consented speakers too. Any loopback segment below `minDiarizationSamples` (1.5 s), and any segment whose diarization *throws*, arrives with `SpeakerLabel = null`, and the gate drops all null-label `Them` utterances unconditionally (that is the D1 fix). The cost is silent loss of very short loopback utterances ("yes", "mhm") even after consent. Stated in the disclaimer.

## 6. Test plan

- `NamedConsentClassifierTests`: table-driven EN/DE/FR — exact sentences, STT-mangled variants (fuzzy repairs), rejections (name-only, acceptance-only, **no Pia reference**, negated, split across utterances, empty name), the false-friend blocklist (`pita`/`pisa`/`via`/`pie` must not satisfy the Pia component), `no problem` / `kein Problem` are not refusals (D5 regression), name-extraction edge cases (hyphenated, 1-token, 4-token), and the confidence contract (all-crisp = 0.95, any repair = 0.85, both ≥ threshold).
- `DirectTranscriptionServiceTests` (pattern: `MeetingAttendeeServiceStateTests` fixture + a `FakeAudioSource` over a channel + NSubstitute): state machine, **mic utterances always pass**, **null-label loopback drops (D1 regression)**, unconsented drop → consent sentence → subsequent pass + rename, revoke → drop again, **stop/start restart produces utterances (D2 regression)**, consent survives a stop/resume but not a new session, dispose ordering, evidence written at grant time, gate is fail-closed when the classifier throws, drop counters audited as batched counters.
- `ConsentStateManagerTests`, `JsonlConsentAuditLogTests`, `ConsentEvidenceStoreTests`, `DirectTranscriptMarkdownTests`, `VoiceStatsCalculatorTests`, `PcmConversionTests`, `LevenshteinTests`. Ported test files must be re-namespaced from `Pia.Wpf.Tests.*` to the house `Pia.Tests.*` convention, and the four `ConsentStateManagerTests` facts that exercise the deleted prompt machine (Prompted / below-threshold-Ambiguous / Deny / Timeout) are replaced by grant/revoke/rename/reset facts.
- `DirectTranscriptionViewModelTests`: chips lifecycle, relabel-on-grant, revoke removes bubbles, front matter contains the stats block, mic bubbles render as "me".
- Existing tests that must be **updated** by this feature: `SpeakerToDisplayNameConverterTests` (three literal assertions).
- Arch tests to keep green (all already exist): DI registration completeness, the `Pia.Services.Consent` naming carve-out, no `async void` under `Pia.Services.*`, ViewModel layer rules, localization completeness in all three locales, `AssistantViewParseTests` / `AssistantHostedOverlayParseTests` (a missing `loc:Str` key fails there too).
- Build: zero warnings, Debug + Release, `-p:EnableWindowsTargeting=true` on macOS. `dotnet test` **cannot run on the dev machine** (net10.0-windows) — tests are written and compiled here, executed on Windows/CI only. No agent may claim a test passed.

## 7. Phasing

**V1 (this design):** everything above.

**V2 candidates (in likely order):** persistent voice profiles via the old biometric store (encrypted, 12-month retention, cross-session consent reuse — the consent sentence then explicitly covers enrollment), hash-chained + signed audit log (+ `verify-audit-chain` CLI), spoken revocation sentences, mic/loopback device pickers, TTS-assisted consent prompting (opt-in), consent-snippet audio evidence (Strict-profile idea), deny sentences + session blocklist.

## 8. Owner decisions (resolved 2026-08-03)

These five questions were open at design time. They are now **decided**; where a decision contradicts an
earlier section of this document, the decision wins and that section has been rewritten to match.

1. **D-1 — Consent sentence strictness: Pia is a HARD requirement, matched FUZZILY.** A consent sentence needs
   **four** components: name introduction (with a capturable name), acceptance verb, recording reference, and a
   reference to Pia. The Pia match is fuzzy (`Levenshtein ≤ 1` gated at token length ≥ 4, plus an explicit
   STT-alias set and a false-friend blocklist) — see §3.5 for the alias set and why each entry is there.
   meeting/conversation/Gespräch/réunion words stay **boosters only** and have no effect on the v1 decision or
   confidence. The confidence model is restated for four components: all four crisp ⇒ **0.95**; any component
   repaired by a fuzzy match ⇒ **0.85**; threshold **0.85** as a single config const
   (`NamedConsentClassifier.GrantConfidenceThreshold`). *This supersedes the earlier §3.5 sentence that called
   the Pia mention "a booster, not a hard requirement".*
2. **D-2 — The consent utterance IS emitted in-band.** It appears in the visible transcript and in the saved
   Markdown, as the human-readable record that consent was given (§3.3, §3.5). It is *also* written to the
   evidence store — in-band and evidence-side are not alternatives.
3. **D-3 — v1 = in-session voice statistics + session-scoped consent.** No persistent voice profiles, no
   biometric store, no cross-session consent reuse, and **no `IVoiceProfileStore` seam** — v1 must not carry a
   speculative extension point for a v2 feature. "Measure the voice data" means the per-speaker in-session
   statistics of §3.8. Persistent enrollment stays in §7 (v2).
4. **D-4 — Consent evidence is write-only in v1.** Evidence files are written at grant time and revocation
   records appended beside them; there is **no expiry stamp, no cleanup worker and no retention service**.
   The retention question (old spec: 3 years) is deferred with the rest of the evidence lifecycle to v2.
5. **No off-switch for the gate** (restated from §3.10, unchanged): the gate is not a setting, not a debug flag,
   and not a test hook. The only way text of an unconsented loopback speaker reaches a channel is a bug.
