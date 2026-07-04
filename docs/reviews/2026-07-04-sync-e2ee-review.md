# Review — Sync & E2EE hardening pass (2026-07-04)

Scope: the "Sync updates" commits landed together on 2026-07-04.
- Client (Pia.Wpf): `f19a36a` — API keys device-local, chat ExtensionData in ciphertext,
  todo ColumnId encrypted-only, LastAccessedAt day-truncation + `max()` retention guard,
  E2EE-permanent UI, `403 e2ee_required` handling.
- Server (Pia): `4ac71e5` — server-side E2EE enforcement (`403 e2ee_required` on plaintext
  push / plaintext chat PUT), `/api/sync/reset` clears E2EE state, Persona `OutputFormat`
  added, chat ExtensionData strip.

Method: 9 hypotheses, each investigated by an agent that read+quoted the actual method;
confirmed defects re-checked by an independent adversarial "refute" agent. Verdicts below
reflect the refutation pass.

## Confirmed findings

### 1. H3 — Plaintext-push `403` during the E2EE window silently strands 3 entity classes — **HIGH** (upheld)
When an account is E2EE-enabled on device B while device A is mid-session (A already ran its
one-time server-E2EE check, `_hasVerifiedServerE2EEStatus=true`, so it won't re-check), A's
next plaintext push is rejected `403 e2ee_required` and returns 0 — but the **pull is not
E2EE-gated**, succeeds, and advances `LastSyncTimestamp` to server-now
(`SyncClientService.cs:186-199`). Onboarding completion runs `PerformFirstSyncMigrationAsync`
which re-pushes 7 entity types cursor-independently, so templates/personas/providers/
sessions/memories/kanban/todos **recover**. Three classes do **not**:
- **Scheduled jobs** — delta push is cursor-gated (`GetModifiedSinceAsync` → `WHERE UpdatedAt >= @Since`,
  `ScheduledJobService.cs:92-95`) and the migration request omits `ScheduledJobs` (`SyncClientService.cs:268-320`).
- **Plugin preferences** — `GetPendingPreferenceChanges()` **drains** `_pendingPrefs` on read
  (`PluginService.cs:604-611`) at request-build time (`SyncClientService.cs:490`), *before* the POST.
  On the 403 (or **any** push failure — transient 500/network) the changes are gone and never
  re-enqueued; migration omits them. Worse: `pushedCount` (`SyncClientService.cs:499-506`) excludes
  plugin prefs, so a prefs-only cycle hits the short-circuit `return 0` (L516-521) after the drain —
  **permanent silent loss even outside the E2EE window.**
- **Assistant chats** — the coalesced op is removed from `_desired` before processing and dropped on
  the 403 bare-`return` (`AssistantChatSyncService.cs`); no onboarding hook re-drives chats, and
  `RunStartupPushAsync` sets `AssistantChatsBackfilledAt` even when every push 403'd (reset only on logout).

Impact: convergence defect, **not** local data loss (local DB retains everything). Items touched
during the window are silently, effectively-permanently absent from cloud/other devices until the
user re-edits them (or Force Full Resync for jobs; logout/login for chats). None of the three are in
the doc's accepted-tradeoff list.

### 2. H7 — Settings can be stored plaintext at rest under an E2EE account — **LOW** (upheld)
The 7 content entities are correctly enforced two ways: `ValidateE2EEEntity`
(`SyncEndpoints.cs:462-511`, missing payload → 400) and apply paths that null all plaintext under
`if (request.IsE2EEEncrypted)`. **Settings is the only exception**: it is absent from the validation
loop, and its apply branch is uniquely payload-guarded — `if (request.IsE2EEEncrypted && request.Settings.EncryptedPayload is not null)`
(`SyncService.cs:715`) — with an `else` that serializes the plaintext DTO with `EncryptedPayload=null`
(L738-748). A client sending `IsE2EEEncrypted=true` + a null-payload Settings persists plaintext
settings at rest under an E2EE account, silently. Buggy/malicious-client-only path (the honest client
always encrypts, `SyncMapper.cs:681-702`) and the fields are non-secret UI/model prefs (API keys are
device-local), so severity is low — but it violates the stated "E2EE account never stores plaintext at
rest" invariant and is inconsistent with every other entity.

### 3. H6 — `/api/sync/reset` is non-atomic with unsafe ordering — **LOW** (upheld)
`SyncEndpoints.cs:293-341`: 11 `ExecuteDeleteAsync` calls + `Devices`/`WrappedUmks` deletes + user
flag-clear/`SaveChangesAsync`, with **no wrapping transaction**. Key material (`Devices` L314,
`WrappedUmks` L315) is destroyed *before* the escape flag `IsE2EEEnabled=false` is committed (L327). A
crash/throw at L327 after L314-315 committed leaves the account with no key material but
`IsE2EEEnabled=true` → every plaintext push 403s and there are no keys to do E2EE. **Recoverable** (reset
is auth-gated only, idempotent — a retry clears the flag), so it is a transient inconsistency, not a
permanent brick. Low severity.

## Not bugs (verified against source)

- **H1** — Persona `OutputFormat` **is** encrypted+decrypted client-side (`SyncMapper.cs:139,151,174,194`);
  the server-side additions complete the round-trip. No data loss.
- **H2** — Provider E2EE key round-trip is correct: `ToSyncProvider` embeds the decrypted key in the
  payload (`:220-238`); `FromSyncProvider` decrypts and re-encrypts into `EncryptedApiKey` (`:264-281`);
  the `UpdateProviderAsync` guard doesn't clobber it (`ProviderService.cs:171-175`). Rotated-key fix is real.
- **H4** — `max(LastAccessedAt, …)` retention is correct: every writer binds `ToString("O")` and all
  values are UTC (`AssistantChatService.cs`, `SqliteContext.cs:218-220`), so lexicographic max is
  chronological. *Robustness note:* normalize the inbound wire value to UTC in `FromSyncAssistantChat`
  to stay correct if a future server ever returns an offset form instead of `Z`.
- **H5** — Chat cipher payload round-trips and stays compatible with old anonymous-type ciphertext:
  `EncryptRecord`/`DecryptRecord` use default (PascalCase, case-sensitive) STJ options
  (`E2EEService.cs:80,151`); `AssistantChatCipherPayload` + `SyncAssistantChat` both carry
  Title/ProviderId/Messages + `[JsonExtensionData]`. *Aside:* `WorkingDirectory` is not synced in either
  mode (documented "not-yet-synced"); pre-existing, out of scope.
- **H8** — `EnableE2EEAsync` drives a full encrypted re-push (`AccountSettingsViewModel.cs:692` →
  `PerformFirstSyncMigrationAsync`, cursor=`MinValue`). No stuck state.
- **H9** — Memory pull cannot regress `LastAccessedAt`: `UpdateObjectDataAsync` never writes that column
  (`MemoryService.cs:147-161`); the truncated value is only written on first-sight insert. Stronger than
  the chat guard.

## Fixes applied (this branch)

**H7 — Settings plaintext hole (server, Pia)**
- `SyncEndpoints.cs` `ValidatePushRequest`: Settings now goes through `ValidateE2EEEntity` under
  E2EE, so a null-payload settings push is rejected `400` like every other entity.
- `SyncService.cs`: settings apply is now selected purely by `if (request.IsE2EEEncrypted)` (dropped
  the `&& …EncryptedPayload is not null` conjunct) — the plaintext `else` is unreachable under E2EE.
- Tests: `SyncE2EEEnforcementTests.Push_E2EEEnabled_PlaintextSettings_IsRejectedAndNotPersisted`,
  `…_EncryptedSettings_StoredEncryptedNotPlaintext`.

**H6 — reset atomicity (server, Pia)**
- `SyncEndpoints.cs` `/api/sync/reset`: wrapped the whole delete-sequence + E2EE-flag clear in
  `BeginTransactionAsync`/`CommitAsync` (the `ExecuteDeleteAsync` calls enlist), so a mid-sequence
  failure rolls back instead of leaving `IsE2EEEnabled=true` with no key material.
- Test: `SyncE2EEEnforcementTests.Reset_ClearsE2EEStateAndSyncData` (happy-path — see caveat below).

**H3 — E2EE-window stranding (client, Pia.Wpf)**
- *Scheduled jobs:* added to `PerformFirstSyncMigrationAsync` (fetched via
  `GetModifiedSinceAsync(DateTime.MinValue)`), so onboarding's cursor-independent re-push recovers them.
- *Plugin prefs:* `PluginService.GetPendingPreferenceChanges()` is now peek-only; added
  `ClearPreferenceChangesAfterSuccessfulPush()` called next to `_deleteTracker.ClearAfterSuccessfulPush()`.
  Added plugin prefs to the push short-circuit check so a prefs-only cycle actually pushes. Fixes loss
  on **any** failed push, not just the E2EE window.
- *Chats:* `AssistantChatSyncService.RunStartupPushAsync` no longer marks `AssistantChatsBackfilledAt`
  done when a push hit `403 e2ee_required` (checks `_syncClient.IsE2EEOnboardingRequired`), so the
  backfill re-runs on next launch after onboarding instead of stranding chats until logout/login.
- Tests: `SyncClientServiceE2EEWindowTests` (prefs-not-cleared-on-403, prefs-only-pushes-and-clears,
  migration-includes-jobs).

### Verification scope (honest)
- Both repos build clean. New tests: 3 server + 3 client, all passing.
- Server Sync+E2EE suite: 230 passed / 10 skipped (Docker) / 0 failed.
- Client full gate (`-namespace- "Pia.Wpf.Tests.Integration.Providers"`): 1259 total, **4 failed**. All
  four were proven **pre-existing** by re-running the identical gate on a stashed (pristine) tree — same
  4 fail there (1256 total): `MistralProviderHandlerTests.ShouldEmitReasoning_False_ForNonCapableModels`,
  `VaultSyncTests.ToVaultSyncMemory_E2EEOn_EncryptsPayload_PathStaysNull`,
  `AsyncSafetyTests.Services_MustNotHave_AsyncVoidMethods` (flags pre-existing `VaultWatcher.Fire`),
  `IngestServiceTests.Reingesting_the_same_source_does_not_create_duplicate_pages`. None touch the code
  I changed. My +3 tests account for the 1256→1259 delta and all pass. **No new failures introduced.**
- Tests exercise the **mechanisms** (validation rejects, reset happy-path, prefs survive a simulated
  403, migration body carries jobs). The end-to-end multi-device E2EE-window scenario is **reasoned
  from the code, not exercised** by an integration test. H6 rollback-on-failure is not unit-tested
  (needs mid-transaction failure injection); the happy-path test would pass with or without the
  transaction, so it is not evidence of atomicity — the fix rests on EF Core's ambient-transaction
  enlistment, which is correct by construction.

### Known limitation introduced by the prefs fix
- Peek-at-build + clear-all-on-success opens a sub-second window: a plugin toggle made *between* request
  build and push success is cleared unsent (it would be re-sent next cycle under the old drain-at-build
  behavior). This exactly matches the existing `SyncDeleteTrackerService` precedent, so it is accepted;
  clearing only the peeked snapshot instead of all would close it if ever needed.

### Deferred (documented, not fixed — would sprawl / out of scope)
- H3 chat convergence is now "next launch" not "immediate": a mid-session re-trigger on
  `E2EEOnboardingCleared` (re-run backfill + re-enqueue the dropped coalesced op) would make chats
  converge without a restart. Left out to avoid rewiring the chat background loop unsupervised.
- The pre-existing root cause — `SyncNowAsync` advances the cursor on pull success regardless of push
  outcome — is untouched (riskiest, least-verifiable change). The migration + peek/clear fixes sidestep
  it. Worth a deliberate follow-up.
- H4 inbound-UTC normalization and H5 `WorkingDirectory`-under-E2EE are noted above as robustness items.
