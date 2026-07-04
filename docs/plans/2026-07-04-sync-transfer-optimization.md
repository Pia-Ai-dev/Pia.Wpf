# Sync Data-Transfer Optimization — Pia.Server ↔ Pia.Wpf

Status: proposed (2026-07-04). Scope: server repo `C:\projects\Pia`, client repo `C:\projects\Pia.Wpf`.

## Context

The Pia sync protocol (server: `Pia.Server\Sync\`, client: `src\Pia.Wpf\Services\SyncClientService.cs`, shared DTOs: `src\Pia.Shared\`) must scale to **thousands of E2EE-enabled clients polling one server**. Today's design is correct and delta-based, with ETag/304 on pull and gzip on push — but the expensive parts scale with *client count*, not with *change volume*. This plan targets the mechanisms that dominate cost at N×(poll every 5 min): per-poll server work, redundant requests, and payload dead weight.

## Verified findings (current state)

**What already works well** (keep):
- Delta pull via `?since=` cursor (server `SyncedAt` timestamps, server-clock cursor → no skew re-sends).
- ETag + `If-None-Match` → 304 on pull; client skips empty pushes entirely.
- Push body gzip-compressed; server Brotli/Gzip response compression enabled (`Program.cs:303-311`).
- E2EE payloads are ZLib-compressed *before* encryption; tombstones are bare GUID lists.

**What scales badly** (the problems, ordered by cost at thousands of clients):

| # | Problem | Where |
|---|---------|-------|
| P1 | 304 saves bandwidth but **zero server work**: every pull — including idle no-change polls — runs ~13 sequential EF queries, full DTO projection, then computes the ETag *afterwards* (SHA256 of response counts+ticks) | `SyncEndpoints.cs:52-88`, `SyncService.PullAsync` |
| P2 | **Full plugin catalog** loaded and put into `Plugins.Upserted` on *every* pull — every 200 response re-ships the whole catalog (names, descriptions, ConfigJson, CabHash) even when one todo changed. (Note: plugins ARE included in the ETag — verified `SyncEndpoints.cs:75-76` — so no stale-304 bug, but the byte/query cost is real.) | `SyncService.cs:400-448` |
| P3 | **No push channel** — fixed 5-min timer per client; idle clients poll forever. Plus one unconditional `GetDevicesAsync()` HTTP call *every* cycle when E2EE is ready (pending-device check) | `SyncClientService.cs:43,206-209` |
| P4 | **Settings blob re-sent on every push** — `ToSyncSettings` stamps `ModifiedAt = UtcNow` unconditionally, so any push carries a freshly-encrypted settings payload | `SyncMapper.cs:674-722` |
| P5 | Push **batch-loads ALL existing entity IDs for the user across 9 tables** whenever anything changed — cost grows with total data volume, not the delta | `SyncService.cs:530-573` |
| P6 | **Missing `(UserId, SyncedAt)` indexes** on Templates, Providers, Sessions, Memories, Todos, KanbanColumns (Personas/ScheduledJobs/ResearchSessions have them) | `PiaDbContext.cs` |
| P7 | **Nulls written on the wire**: no global `WhenWritingNull`; under E2EE every item ships all plaintext keys as `:null` (~60-150 B/item pre-compression). Envelope always serializes ~10 empty `{"upserted":[],"deleted":[]}` blocks | `Pia.Shared\Models\Sync*.cs`, server has no `ConfigureHttpJsonOptions` |
| P8 | **Chat sync**: separate service, one uncompressed PUT per chat, no ETag/conditional GET on chat pull; serialized by the client's 500 ms/host throttle | `AssistantChatSyncService.cs` |
| P9 | **No pagination**: first sync pushes up to 10k sessions in one *uncompressed* body (`PostAsJsonAsync`, unlike the gzip delta push); pull has no size limit/continuation | `SyncClientService.cs:227-361` |
| P10 | Vestigial wire weight: `ResearchSessions` envelope (always empty from client), `SyncProvider.ApiKey` (always null), `SyncScheduledJob.AnswerLength`; `SyncSession` duplicates `TemplateName`/`ProviderName` per row. Serializer casing inconsistent (main push PascalCase, first-sync push camelCase) | `Pia.Shared`, `SyncClientService.cs:335,540` |

**Compatibility constraint:** the server consumes `Pia.Shared` via submodule `C:\projects\Pia\lib\Pia.Wpf` pinned at v1.3.203 (pre-Personas) with TEMP shim files (`SyncPersona.TEMP.cs`, `SyncPersonaEnvelopes.TEMP.cs`) that must be deleted when the submodule is bumped. Any DTO change must sequence: Pia.Wpf repo → submodule bump → shim cleanup, and old clients in the field must tolerate the change.

## Recommended approach — 5 phases, ordered by impact-per-effort

### Phase 1 — Cheap no-change detection (server-only, biggest win, ~2-3 days)

Make an idle poll cost ~0-2 primary-key reads instead of 13 queries + projection + serialization.

- **New `SyncStates` table**: `UserId` (PK/FK), `long DataVersion`, `ChangedAt` — plus a global `PluginCatalogVersion` (single row). Separate table, not columns on the user row, to avoid Identity concurrency-stamp interactions. New model + `PiaDbContext.cs` registration + one migration (server auto-migrates at startup, `Program.cs:437-442`).
- **Bump via a `SaveChangesInterceptor`** (none exists yet; register in `AddDbContextFactory` options, `Program.cs:56`): tracked entries of synced entity types (incl. `UserSettings`, `UserPluginPreferences`, **`Devices`**) bump `DataVersion` per distinct UserId in the same transaction. Interceptor beats an explicit bump in `PushAsync` because the admin Blazor UI and device registration also write these tables. Special cases: `Plugins`/`GroupPlugins` bump the global `PluginCatalogVersion`; `/api/sync/reset` uses `ExecuteDeleteAsync` (bypasses interceptors, `SyncEndpoints.cs:303-322`) → bump explicitly there. Cache both versions in the already-registered `IMemoryCache` (`Program.cs:292`) with write-through invalidation; DB read as cold-start fallback (single-server assumption holds).
- **Pull fast path** (`SyncEndpoints.cs:35-114`): *before* `PullAsync`, compute `ETag = W/"v{DataVersion}-c{CatalogVersion}-s{since.Ticks}"` from the cached versions. If-None-Match match → **304 immediately, zero entity work**. Including `since.Ticks` preserves per-representation semantics (two devices with different cursors never share a false 304); client cursor doesn't advance on 304 (`SyncClientService.cs:592-614`), so the ETag is stable across idle polls. Read version *before* projection: a concurrent write mid-pull just causes one extra full pull next cycle — never a wrong 304. Old clients' cached SHA-256 ETags mismatch once → one extra 200, harmless.
- **Plugin catalog fix** (`SyncService.cs:400-448`): optional `?catalogVersion=` param — matches current → skip Plugins/GroupPlugins/UserPluginPreferences queries and return an empty Plugins block; absent (old clients) → current full-catalog behavior. Echo `CatalogVersion` back as a new nullable pull-response field (additive DTO — batch into Phase 5 submodule bump). Group-allowlist changes bump CatalogVersion; per-user plugin preference changes bump that user's DataVersion.

**Impact:** at 5000 clients @ 5 min, steady-state sync DB/CPU drops ~90%+ (idle polls are the vast majority); every 200 loses the full-catalog payload (KB–tens of KB each). **Rollback:** remove the fast-path if-block — the existing content-derived ETag path stays beneath it. **Risk:** a missed bump path → delayed changes (stale 304 until the next write); mitigate with a config flag to bypass the fast path.

### Phase 2 — Poll pressure: jittered adaptive polling, NO push channel (~1 day)

With Phase 1, 5000 clients @ 5 min ≈ 17 req/s of near-free 304s — one Kestrel server shrugs. Building SSE/SignalR now would add connection-lifecycle complexity (and the SignalR WPF client is a new dependency) to eliminate load that no longer exists.

- **Client** (`SyncClientService.cs:41-127`): replace the fixed 5-min `Timer` with per-cycle scheduling — base 5 min + ±20% random jitter (kills thundering-herd alignment after server restarts / resume-from-sleep), backoff to ~15 min after ~6 consecutive idle cycles (304 + nothing pushed), instant reset to base on any local mutation.
- **Fold the pending-device check into pull**: Devices are interceptor-covered (Phase 1), so a new pending device changes the ETag → client gets a 200 carrying a new optional `PendingDevices` field (additive DTO, Phase 5). New clients drop the unconditional per-cycle `GetDevicesAsync` (`SyncClientService.cs:205-208`, `1204-1244`); old clients keep the extra call and keep working.
- **Deferred escalation path (documented, not built):** if sub-minute change propagation ever becomes a *latency* requirement, hand-rolled SSE `/api/sync/notify` (per-user `Channel` signaled by the Phase-1 interceptor; client `HttpClient` + `ResponseHeadersRead` line-reader) — zero new dependencies, ~250 LOC.

**Impact:** one full HTTPS request per cycle per client removed; request rate cut up to ~3× via backoff; no synchronized spikes.

### Phase 3 — Byte trimming (~2 days)

1. **Server** `Program.cs`: `ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = WhenWritingNull)`. Under E2EE every item currently ships ~60-150 B of `"field":null` keys. Verified safe: System.Text.Json POCO binding cannot distinguish absent from explicit null, so old clients are unaffected.
2. **Client**: one shared `static JsonSerializerOptions` (camelCase, WhenWritingNull, case-insensitive) for both the main gzip push (`SyncClientService.cs:540`) and the first-sync push (`:335`) — fixes the PascalCase/camelCase inconsistency, trims push nulls.
3. **Gzip the first-sync push** (`:335`) by reusing the existing gzip path (`:540-560`); server already runs `UseRequestDecompression`.
4. **Stop re-sending Settings every push**: hash the serialized **plaintext** settings (must be pre-encryption — the random per-record DEK makes ciphertext differ every run), persist `LastPushedSettingsHash` in AppSettings, include Settings only on change (`SyncMapper.cs:674`; push sites `SyncClientService.cs:281,435`). Server already treats absent Settings as no-change (`SyncService.cs:496,711`).
5. **Assistant chats** (`AssistantChatSyncService.cs`): gzip the per-chat PUT bodies; add ETag/If-None-Match to the chat pull riding the user DataVersion. Batched multi-chat endpoint: optional, deferred.
6. **Vestigial fields** (`ResearchSessions` envelope, `SyncProvider.ApiKey`, `SyncScheduledJob.AnswerLength`, `SyncSession.TemplateName/ProviderName`): **keep** — submodule skew + old clients make wire stability worth more than the bytes, and WhenWritingNull makes the nulls free. Re-evaluate after fleet upgrade.

**Explicitly NOT doing:** protobuf/MessagePack/binary framing (bytes are dominated by base64 AES-GCM ciphertext already ZLib-compressed pre-encryption; Brotli/gzip transport already eats JSON key overhead — single-digit-% gain for large complexity and a broken submodule-skew story); ciphertext delta-encoding (impossible under per-record random DEKs); HTTP/3 work.

**Impact:** E2EE 200-response bodies ~30-50% smaller pre-compression (null elision + no catalog); routine pushes lose the ~KB settings ciphertext; first-sync push ~70-85% smaller.

### Phase 4 — DB scalability (server-only, ~2 days)

1. **Indexes**: one migration adding `(UserId, SyncedAt)` for Templates, Providers, Sessions, Memories, Todos, KanbanColumns (mirror existing pattern at `PiaDbContext.cs:124/:195/:207`).
2. **Push ID loading** (`SyncService.cs:530-573`): replace the nine "load ALL user entity IDs" queries with per-type `WHERE UserId = @u AND Id IN (incomingIds)`, skipping types with no incoming changes. The ID sets are only used for insert-vs-update classification and quota counting — membership of the *incoming* IDs suffices. Cost becomes O(delta) instead of O(total rows).
3. **Pull pagination (opt-in)**: `?limit=N` caps each collection ordered by `SyncedAt` asc + additive `HasMore` response field. The `since` cursor is the continuation token; never truncate mid-SyncedAt-group (rows from one push share a batch timestamp) so no data can be skipped. Old clients omit `limit` → byte-identical behavior.
4. **First-sync chunked push**: client splits the migration push into ~200-item batches (sessions dominate); fits the 30/60s rate limit; server unchanged.

### Phase 5 — Ship sequencing (submodule skew)

1. **Server-only, zero wire change — deploy first:** Phase 4.1 indexes, 4.2 push scoping, Phase 1 SyncState + fast-path ETag, Phase 3.1 WhenWritingNull.
2. **Client-only, zero DTO change — next Pia.Wpf release:** Phase 2 jitter/backoff, Phase 3.2-3.5. All work against current *and* new server.
3. **Shared DTO additions** (all additive/nullable in `Pia.Shared\Sync\SyncPullResponse.cs`): `CatalogVersion?`, `PendingDevices?`, `HasMore?`. Then bump the submodule at `C:\projects\Pia\lib\Pia.Wpf`, delete `SyncPersona.TEMP.cs`/`SyncPersonaEnvelopes.TEMP.cs`, bind `/push` to the real `SyncPushRequest` (verify persona field parity with the shims first).
4. **Server consumes the new fields** (catalogVersion skip, pendingDevices, pagination). Clients that never send `catalogVersion`/`limit` get current behavior — old clients stay fully functional through every step.

Rollback per phase: 1 = disable fast-path if-block; 2 = revert timer constants; 3.1 = delete one Program.cs line; 4.2 = revert to full-ID loads; DTO additions are nullable and inert when unused.

## Verification

- **Server unit/integration tests** (Pia repo): 304 fast path returns without entity queries (assert query count via EF logging or a counting interceptor); version bumps from push, admin-UI write, device registration, and `/reset`; catalogVersion skip vs. omitted param parity; pagination never splits a SyncedAt group.
- **Client tests** (Pia.Wpf repo, MTP runner, exclude `Pia.Wpf.Tests.Integration.Providers` namespace per known baseline): settings-hash gating (unchanged settings → absent from push), jitter/backoff scheduling, shared serializer options round-trip.
- **Cross-version matrix (manual, two machines or two profiles):** old client ↔ new server (must behave byte-identically apart from ETag format), new client ↔ old server (jitter + gzip first-sync + null-trimmed push all accepted).
- **Wire measurement:** capture one idle cycle and one single-todo-change cycle before/after (the client's `HttpLoggingHandler` + server pull-elapsed logs already expose sizes/timings) — expect idle cycle ≈ 2 requests → 1 request with 304 and no body, change cycle response to shrink by the plugin catalog size.
- **E2EE regression:** full sync round-trip with E2EE enabled on a fresh profile: first-sync migration (now chunked+gzip), delta push/pull, device-pending surfacing via pull.
