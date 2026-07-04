# Implementation Plan — Sync Data-Transfer Optimization (Pia.Server ↔ Pia.Wpf)

Target branches: server repo `C:\projects\Pia` · client repo `C:\projects\Pia.Wpf` (`feature/meeting_attendee` or a fresh `feature/sync-optimization`) · Date: 2026-07-04
Companion proposal: [`2026-07-04-sync-transfer-optimization.md`](./2026-07-04-sync-transfer-optimization.md) (read first — it carries the problem analysis P1–P10, the rationale for what is *not* done, and the phase rationale this plan executes).

> **All line numbers below were verified against the current code on 2026-07-04.** Where the proposal's numbers or claims were off, this plan uses the verified ones and flags the correction. Unresolved product/scope decisions are collected in [§11](#11-open-questions) and asked interactively at hand-off.

---

## 1. Summary

The proposal is sound. This plan turns it into file-by-file work and folds in six findings from a full re-read of both repos that change *how* (not *whether*) the phases are built:

| Finding | Impact on the plan |
|---|---|
| **A. The current pull ETag is STRONG** (`SyncEndpoints.cs:79/:92` — quoted SHA-256, no `W/`). The proposal's fast-path emits a **weak** `W/"v…"` ETag. A client stores exactly one ETag and echoes it in `If-None-Match`; if the 200-path and fast-path emit different formats they can never match → the fast path never fires. | Phase 1 **must** change the 200-path ETag (`:92`) to emit the *same* version ETag it checks in the fast path. Not optional. |
| **B. There is no local-mutation → sync hook anywhere.** `SyncNowAsync` has exactly two callers: the timer (`:123`) and the manual "Sync now" button (`AccountSettingsViewModel:535`). No entity service nudges sync. | **Decided:** Phase 2 ships **pure jitter + backoff** — no mutation trigger. Local edits sync on the next scheduled cycle; backoff engages only after 6 idle cycles, so an active user rarely hits the ceiling. No new cross-cutting plumbing. |
| **C. The server already accepts both PascalCase and camelCase pushes.** Main push is PascalCase (`:540`), first-sync is camelCase (`:335`); both work today → the server binder is already case-insensitive. | Phase 3.2 casing unification is a **client-only cleanup with zero server-compat risk**. Removes a risk the proposal flagged. |
| **D. The chat pull already paginates** (`AssistantChatSyncService.cs:461-471`, `since`+`cursor`+`hasMore`, `maxPages=100`). It only lacks a conditional GET. | Phase 3.5 chat work is **just gzip + ETag**, not pagination. |
| **E. `/api/sync/reset` clears the user's E2EE flag via `db.SaveChangesAsync()` at `:334`** — that call *does* go through the change tracker, so a Phase-1 interceptor would fire on it, while the `ExecuteDeleteAsync` wipes at `:306-322` bypass it. | Phase 1 reset handling must both add an explicit bump *and* avoid double-counting against the incidental `:334` save. |
| **F. `SyncPersona.TEMP.cs` exists** at `C:\projects\Pia\src\Pia.Server\Sync\SyncPersona.TEMP.cs` with **field parity confirmed** against the client's canonical `SyncPersona.cs` (only XML-doc wording differs). | Resolves the proposal's "verify parity first" caveat — the Phase 5 rebind is parity-safe. |

Everything else in the proposal verified as stated (P1, P2, P4, P5, P6, P7, P8 conditional-GET, P9, P10 vestigials + casing). The query count in P1 is really **14–15**, not "~13" (Users, UserSettings, Templates, Personas, Providers, Sessions×2, Memories, Todos, KanbanColumns, ScheduledJobs, ResearchSessions, GroupPlugins [conditional], Plugins, UserPluginPreferences) — the mechanism (full work then ETag-after) is exactly as described.

## 2. Scope

**IN**
- Phase 1: `SyncStates` table + global `PluginCatalogVersion`, a `SaveChangesInterceptor` bumping versions, cache-backed pull **fast-path 304** before any entity work, and an optional `?catalogVersion=` catalog skip.
- Phase 2: jittered + backoff client polling; fold the per-cycle pending-device check into pull.
- Phase 3: server global `WhenWritingNull`; one shared client serializer; gzip the first-sync push; settings-hash gate; chat-PUT gzip + chat-pull conditional GET.
- Phase 4: the six missing `(UserId, SyncedAt)` indexes; O(delta) push ID loading; opt-in pull pagination; chunked first-sync push.
- Phase 5: additive DTO fields, submodule bump, shim deletion, `/push` rebind.
- Server + client tests per phase; a cross-version manual matrix.

**OUT (rationale in the proposal)**
- SSE / SignalR push channel (deferred; documented escalation path only).
- protobuf/MessagePack/binary framing, ciphertext delta-encoding, HTTP/3.
- Removing the four vestigial fields (`SyncProvider.ApiKey`, `SyncScheduledJob.AnswerLength`, `SyncSession.TemplateName/ProviderName`) — kept for wire stability; `WhenWritingNull` makes the nulls free.
- A batched multi-chat sync endpoint (deferred; the 500 ms/host throttle makes it attractive later, but it is not required now).

---

## 3. Phase 1 — Cheap no-change detection (server-only)

> Effort ~2–3 days. Biggest win: an idle poll drops from 14–15 queries + full projection + serialization to **one cache read (0 DB queries on cache hit)**. Deploys first (no wire change).

### 3.1 `SyncState` model + `PluginCatalogVersion`

New model file `C:\projects\Pia\src\Pia.Server\Models\SyncState.cs` (mirror the shape/attributes of the existing server entities in `Pia.Server\Models`):

```csharp
namespace Pia.Server.Models;

/// <summary>Per-user monotonic sync version. Bumped in the same transaction as any write to that
/// user's synced data (see SyncStateInterceptor). Read on every pull to answer 304 without touching
/// entity tables.</summary>
public sealed class SyncState
{
    public Guid UserId { get; set; }          // PK + FK -> AspNetUsers
    public long DataVersion { get; set; }     // bumped per user write
    public DateTime ChangedAt { get; set; }
}

/// <summary>Single-row global counter for the shared plugin catalog (Plugins + GroupPlugins).
/// Id is a fixed sentinel Guid so there is exactly one row.</summary>
public sealed class PluginCatalogState
{
    public Guid Id { get; set; }              // fixed sentinel (e.g. Guid.Empty)
    public long CatalogVersion { get; set; }
    public DateTime ChangedAt { get; set; }
}
```

> Two tables, not columns on the user row — avoids Identity's concurrency-stamp interactions (proposal's stated reason). `PluginCatalogState` is a separate 1-row table rather than a sentinel row in `SyncState` because `SyncState.UserId` is a real FK; a fake user row would break referential integrity.

Register in `C:\projects\Pia\src\Pia.Server\Data\PiaDbContext.cs`:
- Add `public DbSet<SyncState> SyncStates => Set<SyncState>();` and `public DbSet<PluginCatalogState> PluginCatalogStates => Set<PluginCatalogState>();` in the DbSet block (`:13-46`).
- In `OnModelCreating` (`:48-609`), add config blocks mirroring the `UserSettings` UserId-keyed pattern (`:94-103`): `HasKey(e => e.UserId)`, `ToTable(...)`, and the FK to the user. Respect the `isPostgres` branch (`:52`) if column types need it.

One EF migration; it auto-applies at startup (`Program.cs:437-442`, `MigrateAsync()`). Provider-agnostic (SQLite dev / PostgreSQL prod).

### 3.2 `SyncStateInterceptor` (bump on write)

New `C:\projects\Pia\src\Pia.Server\Sync\SyncStateInterceptor.cs`. **No interceptor exists in the repo today** — this is net-new. It must:

- Override `SavingChangesAsync` / `SavingChanges` (fires at the single `SaveChangesAsync` per push — `SyncService.cs:1727` — inside the explicit transaction opened at `:520`, before commit at `:1728`).
- Scan `ChangeTracker.Entries()` for `Added/Modified/Deleted` of the **watched per-user types**, group by `UserId`, and upsert-bump `SyncState.DataVersion` for each distinct user **in the same unit of work**.
- For `ServerPlugin` / `GroupPlugin` writes, bump the single `PluginCatalogState.CatalogVersion` instead.
- **Exclude `SyncState`/`PluginCatalogState` themselves** from the scan (re-entrancy guard — the bump adds tracked entries).
- Write through to `IMemoryCache` (see 3.3) after a successful save so reads stay hot.

Watched per-user types (**decided**): the 9 push-loaded types in `SyncService.cs:530-573` — `ServerTemplate, ServerPersona, ServerProvider, ServerSession, ServerMemory, ServerTodo, ServerKanbanColumn, ServerScheduledJob, ServerResearchSession` — plus `UserSettings`, `ServerUserPluginPreference`, `ServerDevice` (Phase 2 pending-device-via-pull), **`ServerAssistantChat`** (so the Phase 3.5 chat-pull conditional-GET can ride `DataVersion`), **`ServerReminder`**, and the **`User`** E2EE-flag write. `ServerReminder` is not currently in the pull envelopes, so bumping on a reminder write yields a next-pull 200 that carries no reminder data — a small, deliberate cost accepted for conservative correctness (never a stale 304 for any user-scoped write, and future-proofs reminders becoming synced).

Registration — `C:\projects\Pia\src\Pia.Server\Program.cs`:
- The current `AddDbContextFactory<PiaDbContext>(options => …)` at `:56-66` uses the **no-`IServiceProvider`** overload. To inject the singleton `IMemoryCache` (registered `:292`), switch to the `(IServiceProvider sp, DbContextOptionsBuilder options)` overload and add:
  ```csharp
  builder.Services.AddSingleton<SyncStateInterceptor>();          // before line 56
  builder.Services.AddDbContextFactory<PiaDbContext>((sp, options) =>
  {
      // …existing UseNpgsql/UseSqlite…
      options.AddInterceptors(sp.GetRequiredService<SyncStateInterceptor>());
  });
  ```
- This single registration covers **every** `PiaDbContext` path — the factory, the scoped context `SyncService` consumes (resolved at `:440`, and injected directly per `SyncService.cs:21-35`), Identity's `AddEntityFrameworkStores<PiaDbContext>` (`:146`), and DataProtection's `PersistKeysToDbContext` (`:153`). That is exactly what the proposal needs: the admin Blazor UI and device registration also bump versions, with no per-call-site wiring.
- The interceptor is a **singleton** → must be stateless / thread-safe (it takes `IMemoryCache` only; it works on the `DbContext` handed to each callback).

> **Verify (interceptor inheritance):** that the scoped `PiaDbContext` implicitly registered by `AddDbContextFactory` inherits the factory's interceptors. EF Core does share the options, but confirm with a test that a push actually bumps `DataVersion` (§10).

### 3.3 Version cache

Reuse the existing `IMemoryCache` (`Program.cs:292`) via a thin singleton wrapper — mirror the established `GuardrailVerdictCache` singleton-over-`IMemoryCache` pattern (`Program.cs:260-262`). New `SyncVersionCache` (register near the sync DI at `:281-283`):

- `long GetUserDataVersion(Guid userId)` and `long GetCatalogVersion()` — cache read; on miss, read the DB row (single PK read) and cache. Cold-start fallback; single-server assumption holds (proposal).
- `void BumpUser(Guid userId, long newValue)` / `void BumpCatalog(long newValue)` — write-through, called by the interceptor after save.

### 3.4 Pull fast-path 304 (`SyncEndpoints.cs`)

The pull handler is `:35-114`; `PullAsync` is invoked at `:52`, **before** any ETag work; the current strong ETag is built `:59-78`, hashed `:79`, compared `:82-87`, and set on the 200 response at `:92`.

Insert the fast path **between `:51` and `:52`** (after `userId` resolution + `since` Kind normalization at `:43-47`, before `PullAsync`):

```csharp
// Fast path: answer no-change with zero entity work.
var dataVersion    = versionCache.GetUserDataVersion(userId);
var catalogVersion = versionCache.GetCatalogVersion();
var versionETag    = $"W/\"v{dataVersion}-c{catalogVersion}-s{since.Ticks}\"";
var ifNoneMatch    = http.Request.Headers.IfNoneMatch.ToString();   // same read as today's :82
if (ifNoneMatch == versionETag)
    return Results.StatusCode(StatusCodes.Status304NotModified);     // no PullAsync, no queries
```

Then **change the 200-path ETag (`:92`) to emit `versionETag`** (Finding A — otherwise the fast path is dead). The existing content-hash builder (`:59-79`) can be **deleted** once the version ETag is authoritative, or kept behind the bypass flag (below) as a fallback; simplest is to delete it and rely on the version ETag.

Why this is correct:
- Including `since.Ticks` keeps per-representation semantics — two devices with different cursors never collide on a false 304 (proposal).
- The client cursor does **not** advance on 304 (`SyncClientService.cs:611-615` returns `ServerTimestamp = null`; the advance guard at `:195` is skipped), so the ETag is stable across idle polls.
- Reading the version **before** projection means a concurrent write mid-pull at worst causes one extra full pull next cycle — never a wrong 304.
- Old clients hold a strong SHA-256 ETag → one guaranteed mismatch → one extra 200, then they cache the new format. Harmless.

**Bypass flag:** gate the fast-path `if` on a config value (e.g. `Sync:DisableFastPath304`) read once at startup, so a missed-bump incident can be mitigated without redeploy (proposal's risk mitigation).

### 3.5 Plugin-catalog skip (`SyncService.cs:400-448`)

- Add an optional param to `PullAsync` (`:37`): `PullAsync(Guid userId, DateTime since, long? catalogVersion = null)`.
- When `catalogVersion == versionCache.GetCatalogVersion()`, **skip** the GroupPlugins (`:407`), Plugins (`:419`), and UserPluginPreferences (`:421`) queries and leave `response.Plugins` empty.
- When absent (old clients) → current full-catalog behavior (verified: `:419` loads the *entire* catalog with **no `SyncedAt` filter** today, so `catalogVersion` is the only correct gate).
- Bind a `long? catalogVersion` query param on the endpoint (`:35`) and thread it through the `:52` call.
- Echo the catalog version back via the additive `CatalogVersion?` response field (Phase 5.3 DTO). Group-allowlist changes bump `CatalogVersion`; per-user preference changes bump that user's `DataVersion` (interceptor distinguishes `GroupPlugin` from `ServerUserPluginPreference`).

### 3.6 Reset handling (`SyncEndpoints.cs:293-350`)

The wipes at `:306-322` use `ExecuteDeleteAsync` → **bypass the interceptor**. Since `User` is watched (§3.2), the E2EE-flag clear via `db.SaveChangesAsync()` at `:334` **does** bump `DataVersion` through the interceptor for the reset user — but only when that save actually runs (e.g. an E2EE-disabled reset may skip it). So keep an **explicit** `versionCache.BumpUser(userId, …)` + persist after the transaction to guarantee the bump in every reset path. `DataVersion` is a monotonic change-signal, so an extra increment from the incidental `:334` save is harmless (the version just advances by 2) — no de-dup needed.

**Impact:** at 5000 clients @ 5 min, steady-state sync DB/CPU drops ~90 %+ (idle polls dominate); every 200 loses the full-catalog payload (KB–tens of KB). **Rollback:** flip `Sync:DisableFastPath304` (and, if the content ETag was kept, it resumes). **Risk:** a missed bump path → stale 304 until the next write for that user; the bypass flag is the mitigation.

---

## 4. Phase 2 — Jittered adaptive polling; fold the device check (client-only)

> Effort ~1 day. With Phase 1, 5000 clients @ 5 min ≈ 17 req/s of near-free 304s. No push channel (proposal).

### 4.1 Variable-period scheduling (`SyncClientService.cs`)

Current: `StartBackgroundSync` (`:117-128`) creates a `System.Threading.Timer` with a fixed 10 s due time and a fixed `SyncInterval = 5 min` period (`:43`). No jitter, no backoff, no `Change()` call anywhere.

Rework to per-cycle self-scheduling:
- Convert the fixed-period timer to a one-shot timer that re-arms itself at the end of each `SyncNowAsync` via `_syncTimer.Change(nextDelay, Timeout.InfiniteTimeSpan)`.
- `nextDelay = base ± 20 % jitter`, `base = 5 min`. Jitter kills thundering-herd alignment after server restarts / resume-from-sleep.
- After **6 consecutive idle cycles** (304 **and** nothing pushed — detectable from `PushChangesAsync`'s return and `PullChangesAsync`'s `(Pulled==0, PullSucceeded, ServerTimestamp==null)` tuple at `:611-615`), back off toward ~15 min (`base × grow`, capped).
- Reset the idle counter (and thus the period, back to `base`) whenever a cycle is **not** idle — i.e. a push sent changes **or** a pull returned a 200 with rows. **Decided: no mutation trigger** — local edits are not plumbed to re-arm the timer; they sync on the next scheduled cycle. The manual "Sync now" button (`AccountSettingsViewModel:535`) remains the escape hatch for immediate propagation.

> Randomness note: `Math.Random`/`DateTime.Now` are fine in the client (unlike workflow scripts). Use a single `Random` instance guarded appropriately.

### 4.2 Fold the pending-device check into pull

Current: `SyncNowAsync:206-209` calls `CheckForPendingDevicesAsync` **every cycle** whenever `_deviceMgmt != null && _e2ee.IsReady()`, which hits `_deviceMgmt.GetDevicesAsync()` (`:1208`) with no throttle — one extra HTTPS round-trip per cycle per client.

- `ServerDevice` is interceptor-watched (Phase 1) → a new/pending device bumps the user's `DataVersion` → the pull returns a 200 carrying a new optional `PendingDevices` field (Phase 5.3).
- New clients **drop** the unconditional `GetDevicesAsync` and read pending devices from the pull response instead; old clients keep the extra call and keep working.
- Until the DTO lands (Phase 5), keep the current call but move it off the hot path onto a slower cadence (e.g. every Nth cycle) as an interim (this interim is itself Q-scoped under Phase 2).

**Deferred (documented, not built):** hand-rolled SSE `/api/sync/notify` (per-user `Channel` signaled by the Phase-1 interceptor; client `HttpClient` + `ResponseHeadersRead` line-reader) — ~250 LOC, zero new deps — *only if* sub-minute propagation ever becomes a latency requirement.

**Impact:** one full HTTPS request/cycle/client removed; request rate cut up to ~3× via backoff; no synchronized spikes.

---

## 5. Phase 3 — Byte trimming

> Effort ~2 days. Split across server (5.1) and client (5.2–5.5).

### 5.1 Server global `WhenWritingNull` (`Program.cs`)

No `ConfigureHttpJsonOptions` / `Configure<JsonOptions>` exists in the server HTTP pipeline (the only `WhenWritingNull` is a local options in `DataExportService.cs:13`, unrelated). Add one line near the other service registrations:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
```

Verified safe: System.Text.Json POCO binding can't distinguish absent from explicit null, so old clients are unaffected. Under E2EE this elides ~60–150 B of `"field":null` keys per item. **Required** for the Phase 5.3 nullable fields to be omitted for old clients.

### 5.2 One shared client serializer (`SyncClientService.cs`)

Introduce one `private static readonly JsonSerializerOptions` — `PropertyNamingPolicy = CamelCase`, `DefaultIgnoreCondition = WhenWritingNull`, `PropertyNameCaseInsensitive = true` — and use it at **both** push sites: `:540` (`SerializeToUtf8Bytes(request, opts)`) and `:335` (first-sync). This fixes the PascalCase (`:540`) / camelCase (`:335`) split and trims push nulls. **Finding C:** the server already accepts both casings (case-insensitive binder), so this is a pure client cleanup — no server change, do **not** make the server case-sensitive. `AssistantChatSyncService.JsonOptions` (`:25`, already web/camelCase) can adopt the same instance.

### 5.3 Gzip the first-sync push (`:335`)

Replace the uncompressed `PostAsJsonAsync` (`:335`) with the existing gzip path — copy the delta-push block at `:541-553` (`SerializeToUtf8Bytes` → `GZipStream(CompressionLevel.Fastest, leaveOpen: true)` → `StreamContent` + `ContentType application/json` + `ContentEncoding "gzip"`) → `PostAsync`. The server already runs `UseRequestDecompression` (`Program.cs:535`). ~70–85 % smaller first-sync body.

### 5.4 Settings-hash gate (stop re-sending Settings every push)

Verified root cause: `SyncMapper.ToSyncSettings` (`:674-722`) stamps `ModifiedAt = DateTime.UtcNow` **unconditionally** at `:678`, and under E2EE re-encrypts with a **fresh random DEK** (`E2EEService.cs:95`) + **random nonce** (`CryptoService.cs:13-14`) → ciphertext differs every run. So the hash **must** be over the **plaintext** field set.

1. Add `public string? LastPushedSettingsHash { get; set; }` to `AppSettings.cs`, beside `LastPullETag` (`:180`) — mirrors the existing persisted-ETag pattern.
2. Extract the plaintext field projection at `SyncMapper.cs:683-699` into a helper `object BuildSettingsPlainPayload(AppSettings)` so the E2EE anonymous payload **and** the hash share one source. Serialize with a **deterministic** ordering (the `ModeProviderDefaults` / `ModePersonaDefaults` dictionaries must be sorted — otherwise enumeration order churns the hash) → SHA-256 → base64.
3. At both push builders (`:281` first-sync, `:435` delta), if `hash == settings.LastPushedSettingsHash`, send the request with `Settings = null` (server treats absent Settings as no-change — verified `SyncService.cs:496` short-circuit and `:711` gate); else include `ToSyncSettings` and persist the new hash **after** a successful push (mirror the cursor save at `:347-351`) — never before, or a failed push strands the settings.
4. **Reconcile with the delta-push short-circuit (`:532-538`):** it keys on entity/delete/pref counts and does **not** count Settings. Add a "settings changed" flag to that condition so a *settings-only* change still POSTs (today it would be short-circuited away).

### 5.5 Assistant chats (`AssistantChatSyncService.cs`)

- **Gzip the per-chat PUT:** replace `JsonContent.Create` at `:191-196` with the same gzip `StreamContent` pattern as `SyncClientService.cs:541-553`. (Server `UseRequestDecompression` is global at `Program.cs:535` — verify it applies to the `/api/v1/chats/*` route before shipping the client change.)
- **Conditional GET on chat pull:** the pull (`RunStartupPullAsync:325-459`) already does `since`+`cursor`+`hasMore` pagination (`:461-471`, `maxPages=100`) — it only lacks an ETag. Add `If-None-Match` before the `GetAsync` (`:352`) and handle `HttpStatusCode.NotModified` in the non-success branch (`:353`) as an early no-op return (do **not** invalidate capability on 304). Ride the user `DataVersion` server-side — this works because **`ServerAssistantChat` is an interceptor-watched type (§3.2)**, so any chat write bumps `DataVersion` and invalidates the chat ETag. Persist the chat ETag like `LastPullETag`.
- Batched multi-chat endpoint: **deferred** (the 500 ms/host static throttle in `RateLimitRetryHandler.cs:17` serializes N chats at ≥500 ms apart regardless — a true batch endpoint is the only escape, out of scope here).

**Vestigial fields:** keep all four (`SyncProvider.ApiKey:19`, `SyncScheduledJob.AnswerLength:28`, `SyncSession.TemplateName:15`/`ProviderName:17`) — submodule skew + `WhenWritingNull` make wire stability worth more than the bytes. Re-evaluate after fleet upgrade.

**Impact:** E2EE 200 bodies ~30–50 % smaller pre-compression (null elision + no catalog); routine pushes lose the ~KB settings ciphertext; first-sync push ~70–85 % smaller.

---

## 6. Phase 4 — DB scalability (server-only)

> Effort ~2 days.

### 6.1 Missing `(UserId, SyncedAt)` indexes (`PiaDbContext.cs`)

Verified: `ServerPersona` (`:124`), `ServerScheduledJob` (`:195`, `ix_scheduled_jobs_user_synced`), `ServerResearchSession` (`:207`, `ix_research_sessions_user_synced`) **have** the index. **Missing** on the six the proposal names, at these entity blocks: `ServerTemplate` (`:106-113`), `ServerProvider` (`:128-138`), `ServerSession` (`:141-151`), `ServerMemory` (`:154-165`), `ServerTodo` (`:168-176`), `ServerKanbanColumn` (`:179-186`). Add, mirroring the **named** variant at `:195/:207`:

```csharp
entity.HasIndex(e => new { e.UserId, e.SyncedAt }).HasDatabaseName("ix_<table>_user_synced");
```

One migration, auto-applied at startup, provider-agnostic.

### 6.2 O(delta) push ID loading (`SyncService.cs:530-573`)

Verified: nine `(_context.<T>.Where(x => x.UserId == userId).Select(x => x.Id).ToListAsync()).ToHashSet()` loads that pull **every** ID for the user, and every consumer is a `.Contains(<incomingId>)` probe (insert-vs-update classification at `:583-590`/`:611-697`/`:1071`, quota counting, and log counts at `:578-580`) — **the sets are never enumerated to find server rows absent from the incoming batch.** So scoping to incoming IDs yields identical results.

Minimal safe change: add `&& incomingIds.Contains(x.Id)` to each round-1 query, where `incomingIds` is the materialized `List<Guid>` of that type's incoming `Upserted` IDs (for Sessions it is `request.Sessions.Added`, not `Upserted`). Guard empty (skip the query, use an empty set) — mirror the `*IdsToUpdate.Count > 0 ? … : new Dictionary` pattern at `:616/:627/…`. The `PluginPreferences` full-load at `:1648-1650` (keyed by string `PluginId`) gets the same treatment (already inside a `Count > 0` guard).

- Update/remove the now-misleading log counts at `:578-580` (they'd report "matching-incoming" not "total user rows").
- **Decided: minimal scope.** Keep the round-2 full-entity loads (`:611-697`, already scoped to `*IdsToUpdate`) as-is — do **not** collapse the two rounds. Smallest, lowest-risk diff.
- Add a one-line comment documenting the invariant: *these sets classify incoming IDs only; do not enumerate them to detect server-only rows.*

Cost becomes O(delta) instead of O(total rows).

### 6.3 Opt-in pull pagination

Add `?limit=N` (bindable on `SyncEndpoints.cs:35`, threaded into `PullAsync`): cap each collection ordered by `SyncedAt` asc, and add the additive `HasMore` response field. The `since` cursor is the continuation token — **never truncate mid-`SyncedAt`-group** (rows from one push share a batch timestamp) so no data is skipped. Old clients omit `limit` → byte-identical behavior. (The six new indexes make the ordered scan cheap.) **Decided:** `HasMore` is a **single response-level flag** (not per-collection) — set true when any collection was capped.

### 6.4 Chunked first-sync push (client)

Split `PerformFirstSyncMigrationAsync` (`:227-361`) — which today sends up to 10 k sessions (`GetSessionsAsync(0, 10_000)` at `:256`) in one body — into ~200-item batches (sessions dominate). Fits the 30/60 s rate limit; server unchanged. Combine with the gzip from 5.3. Also lifts the silent 10 k cap for users with more sessions.

---

## 7. Phase 5 — Ship sequencing (submodule skew)

The server consumes an **older pinned** `Pia.Shared` via submodule `C:\projects\Pia\lib\Pia.Wpf`; its `SyncPullResponse`/`SyncPushRequest` differ from the client canonical **only** by the missing `Personas` member, bridged by two shims: `SyncPersona.TEMP.cs` (defines `Pia.Shared.Models.SyncPersona`, **field-parity confirmed**) and `SyncPersonaEnvelopes.TEMP.cs` (defines `SyncPushRequestWithPersonas`/`SyncPullResponseWithPersonas`). The endpoint binds the shim (`SyncEndpoints.cs:118`, `:163`, `:355`); `SyncService` uses the shim types (`:37/:42/:476-481`).

**Order (each step keeps old clients fully functional):**

1. **Server-only, zero wire change — deploy first:** Phase 4.1 indexes, 4.2 push scoping, Phase 1 (`SyncState` + interceptor + fast-path ETag + catalog skip), Phase 3.1 `WhenWritingNull`. The new `CatalogVersion?`/`PendingDevices?`/`HasMore?` fields can be added to the **shim** subclasses (`SyncPersonaEnvelopes.TEMP.cs:34/:36`) for immediate server use before the submodule bump.
2. **Client-only, zero DTO change — next Pia.Wpf release:** Phase 2 jitter/backoff + device-check fold, Phase 3.2–3.5 (shared serializer, gzip first-sync, settings-hash, chat gzip+ETag). All work against current *and* new server.
3. **Shared DTO additions:**
   - New DTO `src\Pia.Shared\Models\SyncPendingDevice.cs` (**decided** shape): a small record/class — `Guid Id`, `string? Name`, `DateTime CreatedAt` — enough to render a meaningful pending-device prompt without a follow-up `GetDevicesAsync` call. No `[JsonPropertyName]` (casing serializer-controlled), consistent with the other `Sync*` models.
   - Add to client canonical `src\Pia.Shared\Sync\SyncPullResponse.cs` after `:25` (all nullable): `public long? CatalogVersion { get; set; }`, `public List<SyncPendingDevice>? PendingDevices { get; set; }`, `public bool? HasMore { get; set; }` (single response-level flag).
   - Then bump the submodule at `C:\projects\Pia\lib\Pia.Wpf` to a tag that **includes the persona DTOs and `SyncPendingDevice`** (already in canonical), `git add lib/Pia.Wpf`.
4. **Delete shims + rebind** (order matters — the bumped tag must contain `SyncPersona` *before* deleting, else CS0433 / missing member):
   - Delete `SyncPersona.TEMP.cs` and `SyncPersonaEnvelopes.TEMP.cs`.
   - `SyncEndpoints.cs:118` param `SyncPushRequestWithPersonas → SyncPushRequest`; `:163` `PushAsync(userId.Value, request, request.Personas) → PushAsync(userId.Value, request)`; `:355` `ValidatePushRequest(SyncPushRequest)`.
   - `SyncService.cs:37` return `SyncPullResponseWithPersonas → SyncPullResponse` (+ `:42` ctor); `:476-481` drop the `SyncEntityChanges<SyncPersona>? personas = null` param and read `request.Personas` directly; migrate the additive fields from the shim onto the real `SyncPullResponse`.
5. **Server consumes the new fields** (`catalogVersion` skip already wired in Phase 1; `pendingDevices`; `limit`/`HasMore`). Clients that never send `catalogVersion`/`limit` get current behavior throughout.

Per-phase rollback: 1 = `Sync:DisableFastPath304` flag; 2 = revert timer constants; 3.1 = delete one `Program.cs` line; 4.2 = revert to full-ID loads; DTO additions are nullable and inert when unused.

---

## 8. File-touch summary

**Server (`C:\projects\Pia`)**
| File | Phase | Change |
|---|---|---|
| `src\Pia.Server\Models\SyncState.cs` (new) | 1 | `SyncState` + `PluginCatalogState` models |
| `src\Pia.Server\Sync\SyncStateInterceptor.cs` (new) | 1 | bump versions on watched writes |
| `src\Pia.Server\Sync\SyncVersionCache.cs` (new) | 1 | `IMemoryCache` wrapper |
| `src\Pia.Server\Program.cs` | 1,3 | interceptor+cache DI, `(sp,options)` overload `:56`, `ConfigureHttpJsonOptions` |
| `src\Pia.Server\Data\PiaDbContext.cs` | 1,4 | new DbSets/config; six `(UserId,SyncedAt)` indexes |
| `src\Pia.Server\Sync\SyncEndpoints.cs` | 1,4,5 | fast-path 304 `:51-52`; version ETag `:92`; `catalogVersion`/`limit` params; reset bump `:293-350`; shim rebind |
| `src\Pia.Server\Sync\SyncService.cs` | 1,4,5 | catalog skip `:400-448`; O(delta) IDs `:530-573`; pagination; persona param removal |
| `src\Pia.Server\Sync\*.TEMP.cs` | 5 | delete both |
| `lib\Pia.Wpf` (submodule) | 5 | bump to persona+additive-fields tag |
| Migrations | 1,4 | `SyncState` tables; six indexes |

**Client (`C:\projects\Pia.Wpf`)**
| File | Phase | Change |
|---|---|---|
| `src\Pia.Wpf\Services\SyncClientService.cs` | 2,3,4 | jitter/backoff timer; device-check fold; shared serializer `:335/:540`; gzip first-sync; settings-hash gate; chunked first-sync; `limit`/`HasMore` handling |
| `src\Pia.Wpf\Services\SyncMapper.cs` | 3 | extract `BuildSettingsPlainPayload`; keep `ModifiedAt` |
| `src\Pia.Wpf\Services\AssistantChatSyncService.cs` | 3 | gzip PUT `:191`; conditional GET `:352` |
| `src\Pia.Wpf\Models\AppSettings.cs` | 3 | `LastPushedSettingsHash`; chat-pull ETag field |
| `src\Pia.Shared\Sync\SyncPullResponse.cs` | 5 | additive nullable fields (`CatalogVersion?`, `PendingDevices?`, `HasMore?`) |
| `src\Pia.Shared\Models\SyncPendingDevice.cs` (new) | 5 | `SyncPendingDevice` DTO (Id, Name, CreatedAt) |
| (`Bootstrapper.cs`) | 1?/2 | only if a new client collaborator is introduced (`:445-455` block) |

---

## 9. Privacy-logging compliance

Per `CLAUDE.md`: any new log line touching payloads, user-named items, or URLs must use `SensitiveDebug`/`SafeUrl`. Relevant here: pull/push URLs now carry `?catalogVersion=`/`?limit=` — log them via `SafeUrl.Format`. Version numbers, counts, and `DataVersion`/`CatalogVersion` are non-sensitive and may log at info. The settings-hash is a SHA-256 of plaintext — do **not** log the plaintext payload; the hash itself is safe.

## 10. Verification

- **Server tests** (Pia repo, existing `tests\Pia.Server.Tests\Sync\`): 304 fast path returns with **zero entity queries** (assert via a counting interceptor or EF query logging); `DataVersion` bumps from push, admin-UI write, device registration, and `/reset`; the interceptor is inherited by the scoped `PiaDbContext` (Finding-verify); `catalogVersion` match skips catalog vs omitted-param full catalog; pagination never splits a `SyncedAt` group; push ID loading returns identical insert/update classification scoped vs unscoped.
- **Client tests** (MTP runner; exclude `Pia.Wpf.Tests.Integration.Providers` per the known baseline — ~18 known live-network failures there, gate on no failures **outside** it): settings-hash gating (unchanged settings ⇒ `Settings` absent from push; settings-only change still POSTs despite the `:532-538` short-circuit); deterministic hash across dict reordering; jitter/backoff scheduling (idle-count → backoff, activity → reset); shared serializer round-trip (camelCase, null elision); gzip first-sync accepted.
- **Cross-version matrix (manual, two profiles):** old client ↔ new server (byte-identical apart from the ETag format flip → one extra 200); new client ↔ old server (jitter + gzip first-sync + null-trimmed camelCase push all accepted).
- **WebAuthn smoke test (manual, same pass):** the global `WhenWritingNull` (§5.1) also applies to `Results.Ok(new { options, state })` in `Auth/WebAuthnEndpoints.cs` (`:31` register, `:107` login), consumed by the browser's `navigator.credentials` parser rather than an STJ-POCO client. Absent-vs-null is spec-equivalent for optional WebAuthn dictionary members, so this is a verification step, not a code change: run one passkey register and one passkey login against the built server before shipping.
- **Wire measurement:** capture one idle cycle and one single-todo-change cycle before/after using the client's `HttpLoggingHandler` + server pull-elapsed logs — expect idle ≈ 2 requests → 1 request (304, no body, `GetDevicesAsync` gone); change-cycle response shrinks by the plugin-catalog size.
- **E2EE regression:** fresh-profile full round-trip — chunked+gzip first-sync, delta push/pull, settings omitted when unchanged, device-pending surfaced via pull.
- **Build/test gate:** `dotnet build` both repos; server `dotnet test`; client via the MTP runner with the namespace exclusion above.

---

## 11. Decisions & remaining verifications

**Resolved at hand-off (2026-07-04), baked into the sections above:**

1. **Phase 2 = pure jitter + backoff, no mutation trigger** (§1-B, §4.1). Local edits sync on the next scheduled cycle; the manual "Sync now" button is the immediate-propagation escape hatch.
2. **Interceptor watched types** (§3.2): the 9 push types + `UserSettings` + `ServerUserPluginPreference` + `ServerDevice` + `ServerAssistantChat` + `ServerReminder` + `User` (E2EE-flag). Conservative correctness — every user-scoped write invalidates the 304, accepting a small cost for reminder writes that carry no pull-envelope payload.
3. **`PendingDevices` = new `SyncPendingDevice` DTO** (`Guid Id`, `string? Name`, `DateTime CreatedAt`) (§7.3, §8).
4. **Push ID loading = minimal `IN(incomingIds)` on round-1 only** (§6.2); round-2 loads unchanged. `HasMore` = single response-level flag (§6.3).

**Resolved with in-plan defaults (flagged inline):** the ETag-format unification (Finding A — mandatory), `CatalogVersion` typed `long?`, keeping all four vestigial fields, deferring SSE and the batched-chat endpoint.

**Remaining verifications (confirm during implementation, not blocking the plan):**
- The scoped `PiaDbContext` from `AddDbContextFactory` inherits the factory's interceptors (§3.2 test).
- `UseRequestDecompression` (`Program.cs:535`) applies to the `/api/v1/chats/*` route before shipping the chat-PUT gzip (§5.5).
- Server push binder is case-insensitive (implied by today's dual-casing tolerance; confirm no custom manual JSON parse exists) before the client casing unification (§5.2).
