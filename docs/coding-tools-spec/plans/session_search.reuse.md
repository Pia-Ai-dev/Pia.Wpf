# Plan: `session_search` (REUSE) — Modification Instructions

> **Bucket:** `reuse` — Pia already has the full substrate (SQLite message store + FTS5 index +
> a working FTS5 search method + a proven read-only built-in tool-handler pattern). The work is a
> well-scoped **extension** (primarily message-level FTS for DISCOVERY), not a rebuild.
>
> **Scope of this doc:** planning only. It names exact existing files/classes/methods, maps spec
> requirements to current behavior, and gives ordered, implementable instructions **without** writing C#.

---

## 1. Tool contract (from `docs/coding-tools-spec/session_search.md`)

A single tool, `session_search`, that does FTS5 full-text retrieval over a local SQLite message store
and message-window navigation. **No LLM calls. No writes.** Every shape returns real messages.

### Parameters (exact)

| Param | Type | Default | Notes |
|---|---|---|---|
| `query` | string | — | FTS5 query (DISCOVERY). Omit for browse/read/scroll. |
| `session_id` | string | — | Session to READ (alone) or SCROLL inside (with `around_message_id`). |
| `around_message_id` | integer | — | Message id to center a SCROLL window on. |
| `window` | integer | 5 (max 20) | Messages each side of the anchor (SCROLL). |
| `limit` | integer | 3 (max 10) | Max sessions to return (DISCOVERY). |
| `role_filter` | string | `"user,assistant"` | Comma-separated roles to include. |
| `sort` | string (`newest`\|`oldest`) | — | Temporal bias on FTS5 ranking (DISCOVERY). |
| `profile` | string | — | Read from another profile's DB. **Drop if single-profile.** |

### Four calling shapes (dispatch by which args are present)

1. **DISCOVERY** — `query` set. FTS5 search, dedupe hits by session lineage, return top `limit` sessions.
   Each result carries: `session_id`, `title`, `when`, `source`, `snippet` (highlighted excerpt),
   `bookend_start` (first 3 user+assistant msgs = the goal), `messages` (±5 around the match, anchor
   flagged), `bookend_end` (last 3 msgs = the resolution), `match_message_id`.
2. **SCROLL** — `session_id` + `around_message_id` (+ `window`). ±`window` messages around the anchor.
   No FTS5, no bookends. Forward via `messages[-1].id`, backward via `messages[0].id`. The boundary
   message appears in **both** adjacent windows as an orientation marker.
3. **READ** — `session_id` only. Dump the whole session; head+tail for large ones (e.g. first 20 + last 10).
4. **BROWSE** — no args. Recent sessions chronologically (titles, previews, timestamps).

### Behaviors / invariants

- **FTS5 semantics:** AND default between terms; support explicit `OR`. Snippet highlighting.
- **Source-first caveat** (must be baked into the tool description): this searches *conversation
  history only*, not live external sources. If the user gave a URL/file/account, inspect that source —
  don't conclude "not found" from session history alone.
- **Cheap:** pure DB/FTS5, **zero model calls**. Bounded output via windows + bookends, not full transcripts.
- `role_filter` defaults to `user,assistant` (skip tool/system noise). `profile` reads another profile's DB.

---

## 2. What already exists (verified against the codebase)

| Capability | Where | Current behavior |
|---|---|---|
| Chat header store | `AssistantChats` table (`SqliteContext.cs:211-226`) | `Id` (TEXT GUID PK), `Title`, `CreatedAt`/`UpdatedAt`/`LastAccessedAt` (ISO-8601 TEXT), `WindowMode`, `ProviderId` (TEXT GUID), `ExtraJson`. Indexed on `UpdatedAt`, `LastAccessedAt`. |
| Per-message store | `AssistantChatMessages` table (`SqliteContext.cs:228-245`) | `Id` (TEXT GUID PK), `ChatId` (FK, `ON DELETE CASCADE`), **`Ordinal` (INTEGER)**, `Role`, `Content`, `ThinkingContent`, `Timestamp`, `Tokens`, `ModelName`, Persona snapshot. Indexed on **`(ChatId, Ordinal)`** (`IX_AssistantChatMessages_ChatId_Ordinal`). |
| Aggregate FTS5 index | `AssistantChatsFts` virtual table (`SqliteContext.cs:588-623`) | `fts5(ChatId UNINDEXED, Title, Body)`. **One row per chat** — `Body` is all message `Content` joined with `\n\n`. Rebuilt on schema init if it was the old contentless (`content=''`) shape; backfilled from chats if empty (`BackfillAssistantChatsFts`, `:625-652`). |
| FTS5 search method | `AssistantChatService.SearchAsync` (`AssistantChatService.cs:127-187`) | Chat-level FTS via `Id IN (SELECT ChatId FROM AssistantChatsFts WHERE … MATCH @Search)`, plus optional date-range + `providerId` filters. **`ORDER BY UpdatedAt DESC`** only (no relevance rank). Paginated via `offset`/`limit`. Returns chat headers only — **no messages**. |
| FTS query builder | `BuildFtsQuery` + `SanitizeFtsToken` (`AssistantChatService.cs:444-469`) | Splits on whitespace, keeps only letters/digits, lowercases (which **neutralizes `AND`/`OR`/`NOT`**), appends `*` for prefix match. Result is an implicit-AND prefix query. **Strips all boolean operators.** |
| Single-chat fetch | `AssistantChatService.GetAsync` (`:105-125`) + `GetMessagesAsync` (`:343-379`) | `GetMessagesAsync` returns the **whole** session ordered by `Ordinal ASC`, mapping each row to `SyncAssistantChatMessage` (its `Id` is the GUID, not `Ordinal`). |
| FTS maintenance points | `ReplaceFtsRowAsync` (`:381-406`), `DeleteCoreAsync` (`:195-220`), `DeleteAllAsync` (`:232-273`), `EvictOlderThanAsync` (`:275-314`) | Every write/delete path in the service explicitly keeps `AssistantChatsFts` in sync (no triggers). |
| Read-only tool-handler pattern | `ResearchHistoryToolHandler` (`ResearchHistoryToolHandler.cs`) + `IResearchHistoryToolHandler` | `GetTools()` returns `AITool[]` via `AIFunctionFactory.Create`; `HandleToolCallAsync` dispatches by `toolCall.Name`, returns `(Result, null PendingAction)` — **no action card, immediate execution**. Logs only `ToolName` at `LogInformation`. Arg coercion via `GetStringArg`/`GetIntArg` (handle `JsonElement`). |
| Built-in plugin adapter | `BuiltInPluginHandler` (`BuiltInPluginHandler.cs`) | `From*Handler` factories wrap a domain handler as `IPluginToolHandler`; `isAvailable` lambda optionally gates tool + system-prompt visibility (used by `FromFilesHandler`). |
| Registration path | `PluginService.InitializeBuiltInPlugins` (`PluginService.cs:73-94`) | `switch` on `handlerId` from `ConfigJson` → `From*Handler` → `RegisterHandler(id, adapter)`. |
| Defaults + well-known GUIDs | `BuiltInPluginDefaults` (`BuiltInPluginDefaults.cs`) | Each built-in has a GUID, a `SyncPlugin` default with `ConfigJson` carrying `handlerId`, `defaultEnabled`, and `systemPromptAddition`. Listed in `PreloadedPluginIds`. |
| DI wiring | `Bootstrapper.cs` | `IResearchHistoryToolHandler`→`ResearchHistoryToolHandler` (`:246`), `IFilesToolHandler`→`FilesToolHandler` (`:250`), `IAssistantChatService`→`AssistantChatService` (`:261`) all singletons. |

**Persistence shape returned to callers:** `SyncAssistantChat` / `SyncAssistantChatMessage`
(`Pia.Shared/Models/SyncAssistantChat.cs`). Messages are text-only (`Content`/`ThinkingContent`),
roles are `"user"`/`"assistant"`, each has a GUID `Id`, `Timestamp`, optional Persona.

---

## 3. Hard constraints (read before designing)

These are non-negotiable framing decisions; they shrink the surface area dramatically.

- **C1 — Zero model calls (spec, repeated).** Do **not** reuse `ResearchHistoryService.HybridSearchAsync`
  or `EmbeddingService`. An ONNX embedding forward pass *is* a model call. Borrow only the **structural
  shape** of `ResearchHistoryToolHandler` (read-only, `(Result, null)`, no action card), never its
  embedding/hybrid code. **DISCOVERY is FTS5-only.**
- **C2 — Read-only ⇒ no approval guard.** All shapes execute immediately and return
  `(Result, null PendingAction)`. No `ActionCardInfo`. Because `HandleToolCallAsync` always
  returns a null `PendingAction`, `ExecutePendingActionAsync` is **never invoked** — its body
  can be a trivial pass-through. (`ResearchHistoryToolHandler.ExecutePendingActionAsync` actually
  calls `pendingAction.Execute()`, but that path is likewise unreachable for read-only tools.)
- **C3 — Integer message id = per-chat `Ordinal`.** See §6 decision; this threads through SCROLL,
  DISCOVERY, and the new FTS rowid mapping.
- **C4 — Native, not MCP.** This searches Pia's own chat DB via `SqliteContext`; no shell/filesystem
  MCP server has access to it. (Cross-cutting Q3 resolved.)

---

## 4. Gap analysis

| # | Spec requirement | Current behavior | Needed change |
|---|---|---|---|
| G1 | DISCOVERY must identify the **matched message** (for `match_message_id`, the ±window center, and a per-message snippet). | `AssistantChatsFts` is **one aggregate row per chat** (`Body` = all messages joined). It can tell you *which chat* matched, never *which message*. | **New message-level FTS5 table** over `AssistantChatMessages` (e.g. columns `ChatId UNINDEXED`, `Ordinal UNINDEXED`, `Role UNINDEXED`, `Content`). Match → row → `(ChatId, Ordinal)` → window center + `snippet()` highlight. `AssistantChatsFts` stays for BROWSE/title search, **not** DISCOVERY. |
| G2 | `around_message_id` / `match_message_id` are **integers**. | Pia message identity is a **GUID** (`SyncAssistantChatMessage.Id`); `Ordinal` is the only per-chat integer. | Map the integer contract to **per-chat `Ordinal`** (already indexed). All shapes that surface an id must `SELECT Ordinal` and expose it — **never** the GUID. |
| G3 | SCROLL: ±`window` messages around the anchor, with the boundary message shared between adjacent windows. | `GetMessagesAsync` returns the **whole** session; no range slice. | New **Ordinal-range query**: `WHERE ChatId=@c AND Ordinal BETWEEN @anchor-@w AND @anchor+@w ORDER BY Ordinal`. Surface `Ordinal` as id so caller can re-page via `messages[0].id`/`messages[-1].id`. |
| G4 | DISCOVERY: `bookend_start` (first 3 user+assistant), `bookend_end` (last 3). | No bookend assembly. | New code over an Ordinal-ordered fetch: take first 3 / last 3 messages (after role filter). De-dup if the session is short enough that bookends overlap the window. |
| G5 | READ: head+tail dump for large sessions (e.g. first 20 + last 10). | `GetAsync`/`GetMessagesAsync` return the entire session unbounded. | New head+tail assembly with a size threshold (count-based). Below threshold → full dump; above → first N + elision marker + last M. |
| G6 | BROWSE: recent sessions chronologically with title + preview + timestamp. | `SearchAsync(searchText:null)` already returns headers `ORDER BY UpdatedAt DESC`. **No preview.** | Reuse `SearchAsync` (empty text). Add a per-chat **preview** (first user message or `Body` snippet) — small new fetch, or reuse `AssistantChatsFts.Body` truncated. |
| G7 | FTS5: AND default **and explicit `OR`**; snippet highlighting. | `BuildFtsQuery`/`SanitizeFtsToken` strip `OR` (lowercased to a literal token) and append `*`. No `snippet()`/`highlight()` usage anywhere. | **DISCOVERY-specific query builder** that preserves `OR` (and AND-by-default) instead of reusing `BuildFtsQuery`. Use FTS5 `snippet()` on the new message table for highlighted excerpts. Do **not** change `BuildFtsQuery` (the history-UI search depends on its prefix behavior — see §7). |
| G8 | `role_filter` (default `user,assistant`). | `SearchAsync` has no role filter; `AssistantChatMessages.Role` exists. | `WHERE Role IN (…)` on the new message-query paths. Parse the comma-list; default to `user,assistant`; ignore unknown roles. |
| G9 | `sort=newest\|oldest` temporal bias on FTS ranking. | `SearchAsync` is `ORDER BY UpdatedAt DESC` only; no bm25/rank blend. | DISCOVERY orders by FTS `bm25()` rank, then applies a temporal tiebreak/bias by chat `UpdatedAt` (or message `Timestamp`) per `sort`. Exact blend is an **open question** (§8) — propose a heuristic, don't fake precision. |
| G10 | Dedupe DISCOVERY hits by **session lineage**. | `AssistantChats` has **no** parent/fork/lineage column. | No lineage data exists ⇒ **conscious downgrade to dedupe-by-`session_id`** (one best hit per chat). Document this as a deliberate degrade, not a true lineage walk. |
| G11 | Optional `profile` (read another profile's DB). | Pia is single-profile: one local DB via `SqliteContext`; no cross-profile DB access. | **Drop `profile`** (spec explicitly allows dropping if single-profile). Omit from schema. |
| G12 | Tool must be registered and discoverable by the model. | No `session_search` handler, factory, default, or system prompt exists. | New `SessionSearchToolHandler` + interface, a `BuiltInPluginHandler.FromSessionSearchHandler` factory, a `BuiltInPluginDefaults` entry (new GUID + `handlerId` + system prompt with the source-first caveat), a `PluginService` switch arm, and `Bootstrapper` DI registration. |
| G13 | `task_id` threading (cross-cutting Q4). | `IPluginToolHandler.HandleToolCallAsync` receives only `FunctionCallContent` (+ CT); no session/task id is threaded into handlers. | **Non-blocking for `session_search`** — its schema has no `task_id` and all four shapes are self-contained. Flag in §8; do not gate this tool on retrofitting handler signatures. |

---

## 5. Cross-cutting questions — resolution for this tool

`session_search` is **read-only, zero-write, zero-model**. Most of the suite-wide questions are N/A here.

| Q | Topic | Resolution for `session_search` |
|---|---|---|
| Q1 | Code-execution consent model | **N/A.** No process/code execution. No new consent surface. |
| Q2 | Filesystem scope / sandbox / workspace root | **N/A.** Reads the chat DB via `SqliteContext`, not the filesystem. `SafeFolderPath`/`FilesToolHandler` are untouched. |
| Q3 | Native vs MCP delegation | **Native.** Searches Pia's own SQLite chat store; no external MCP server can reach it. |
| Q4 | `task_id` threading | **Flag, don't block.** Spec schema has no `task_id`; Pia doesn't thread a session id into `IPluginToolHandler.HandleToolCallAsync` today; all four shapes are self-contained. Note for the broader suite (§8). |
| Q5 | Extend vs rebuild `FilesToolHandler` | **N/A.** This tool shares no names/code with the file tools. New handler, separate substrate. |
| Q6 | Python runtime for `execute_code` | **N/A.** No code execution; no new runtime dependency. (Honors the user's minimal-dependency preference — no new NuGet needed; FTS5 ships with `Microsoft.Data.Sqlite`.) |

---

## 6. Decision: integer message id ⇒ per-chat `Ordinal`

**Recommendation: use per-chat `Ordinal` as the integer message id.** Rationale:

- The id is **never used without a `session_id`**: SCROLL requires `session_id` + `around_message_id`;
  DISCOVERY results each carry their own `session_id`; READ is session-only. So **per-chat scope is
  sufficient** — a globally-unique integer is not needed.
- `Ordinal` is exactly the column `IX_AssistantChatMessages_ChatId_Ordinal` already indexes, so the
  SCROLL range slice (`Ordinal BETWEEN …`) and the window-center math are free.
- **Stability check:** `SaveCoreAsync` deletes and re-inserts all messages for a chat, assigning
  `Ordinal = 0..n` in `Messages` list order (`AssistantChatService.cs:72-96`). Chat history is
  **append-only** in practice (new turns appended), so existing positions are preserved across saves.
  Only a mid-history edit/delete would shift ordinals — which the chat history flow does not do.
  Document this assumption; if mid-history mutation is ever added, the id contract must be revisited.
- **Alternative considered:** FTS5 `rowid` on the new message table. Rejected — rowid is opaque,
  decoupled from message order, and useless for the SCROLL "page forward/back" semantics the spec needs.

**Implementation note:** every new query that returns a message must `SELECT Ordinal` and surface it as
the id field, **not** `msg.Id` (the GUID). The existing `GetMessagesAsync` maps `Id` to the GUID; the new
read paths need their own mapping that carries `Ordinal`.

---

## 7. Ordered modification instructions

> Split principle (from the FTS-maintenance risk): **FTS-maintenance code must EXTEND
> `AssistantChatService`** (it owns the write transaction and all index-sync points). The **query/assembly
> logic can be NEW alongside** (its own handler + read methods) so it never touches the write paths.

### Step 1 — Add a message-level FTS5 table (schema)

- [ ] In `SqliteContext`, add an `EnsureAssistantChatMessagesFts()` mirroring `EnsureAssistantChatsFts`
      (`:588-623`): create `CREATE VIRTUAL TABLE IF NOT EXISTS AssistantChatMessagesFts USING fts5(
      ChatId UNINDEXED, Ordinal UNINDEXED, Role UNINDEXED, Content)`. Call it from `EnsureSchema`
      next to `EnsureAssistantChatsFts()` (`:251`).
- [ ] Add `BackfillAssistantChatMessagesFts()` mirroring `BackfillAssistantChatsFts` (`:625-652`):
      if the new FTS table is empty but `AssistantChatMessages` has rows, `INSERT … SELECT ChatId,
      Ordinal, Role, Content FROM AssistantChatMessages`. (One FTS row per message, unlike the
      aggregate table.)
- [ ] Mirror the contentless-table self-heal check (the `content=''` rebuild guard) so a future schema
      change can drop/rebuild cleanly.

### Step 2 — Maintain the new FTS index in EVERY existing write/delete path (EXTEND `AssistantChatService`)

This is the **#1 regression risk** (§ "Regression risks"). A single missed path = silent index drift.
Every place that currently touches `AssistantChatsFts` must also touch `AssistantChatMessagesFts`:

- [ ] `ReplaceFtsRowAsync` (`:381-406`) — currently deletes+inserts the single aggregate row. Add:
      delete all `AssistantChatMessagesFts` rows for the chat, then insert one row **per message**
      (mirror the per-message insert loop at `:72-96`, with `Ordinal`). Keep it inside the existing
      `SaveCoreAsync` transaction (`:30-103`).
- [ ] `DeleteCoreAsync` (`:195-220`) — add `DELETE FROM AssistantChatMessagesFts WHERE ChatId=@id`
      alongside the existing `AssistantChatsFts` delete.
- [ ] `DeleteAllAsync` (`:232-273`) — add `DELETE FROM AssistantChatMessagesFts` alongside the existing
      `DELETE FROM AssistantChatsFts`.
- [ ] `EvictOlderThanAsync` (`:275-314`) — add the per-chat message-FTS delete in the per-id loop.
- [ ] Confirm `SaveFromRemoteAsync` is covered (it routes through `SaveCoreAsync`, so it is — verify).

### Step 3 — Add read-only query methods (NEW, alongside on `AssistantChatService` or a new query type)

Pure reads; no write-path risk. Either extend `IAssistantChatService`/`AssistantChatService` with new
methods or introduce a dedicated `ISessionSearchStore`. Prefer extending `IAssistantChatService` so the
new methods sit next to `GetMessagesAsync`/`SearchAsync` and reuse `SqliteContext`.

- [ ] **DISCOVERY query:** `MATCH` against `AssistantChatMessagesFts` (DISCOVERY-specific FTS query —
      see Step 5), apply `role_filter` via `Role IN (…)`, order by `bm25()` with `sort` bias (§G9),
      dedupe to one best hit per `ChatId` (§G10), take top `limit`. For each surviving hit return
      `ChatId`, matched `Ordinal` (= `match_message_id`), and a `snippet()` highlight from the new table.
- [ ] **Window slice (SCROLL + DISCOVERY center):** `SELECT Ordinal, Role, Content, Timestamp, …
      FROM AssistantChatMessages WHERE ChatId=@c AND Ordinal BETWEEN @lo AND @hi AND Role IN (…)
      ORDER BY Ordinal`. Surface `Ordinal` as the id. Clamp `window` to max 20.
- [ ] **Bookends:** first 3 and last 3 role-filtered messages by `Ordinal` (two small ordered queries
      or one full-ordered fetch sliced in memory for small sessions).
- [ ] **READ head+tail:** count messages; below threshold → all; above → first N + last M with an
      elision marker between.
- [ ] **BROWSE:** reuse `SearchAsync(searchText:null, …)` for headers; add a short preview per chat
      (first user message `Content`, truncated, or a truncated `AssistantChatsFts.Body`).

### Step 4 — Add the tool handler (NEW, model on `ResearchHistoryToolHandler`)

- [ ] New `ISessionSearchToolHandler` + `SessionSearchToolHandler` mirroring
      `IResearchHistoryToolHandler`/`ResearchHistoryToolHandler`:
  - [ ] `GetTools()` returns **one** tool `session_search` via `AIFunctionFactory.Create` (arg-dispatch,
        **not** ResearchHistory's two-tool split). Schema = §1 params **minus `profile`** (§G11).
        Make `query`/`session_id`/`around_message_id` nullable so shape dispatch works.
  - [ ] `HandleToolCallAsync` dispatches by **which args are present** (DISCOVERY if `query`; SCROLL if
        `session_id`+`around_message_id`; READ if `session_id` only; BROWSE if none). Returns
        `(Result, null PendingAction)` always (**C2** — read-only, no action card).
  - [ ] `ExecutePendingActionAsync` is a trivial pass-through, never invoked at runtime because
        `HandleToolCallAsync` always returns a null `PendingAction` (interface satisfaction only).
  - [ ] Reuse the `GetStringArg`/`GetIntArg` `JsonElement`-coercion helpers' approach for args
        (`window`, `limit`, `around_message_id` come as int/JsonElement).
  - [ ] Apply defaults/caps in the handler: `window` 5 (cap 20), `limit` 3 (cap 10),
        `role_filter` → `user,assistant`, `sort` validated to `newest`/`oldest` (else ignore).
  - [ ] Result rendering: a compact text or JSON block per shape (DISCOVERY: per-session
        `session_id`/`title`/`when`/`source`/`snippet`/`bookend_start`/`messages`(anchor-flagged)/
        `bookend_end`/`match_message_id`). Keep output bounded (windows + bookends, not whole transcripts).
  - [ ] **`source` field:** see §8 open question — likely derive from `ProviderId` and/or `WindowMode`.

### Step 5 — DISCOVERY-specific FTS query builder (NEW, do NOT modify `BuildFtsQuery`)

- [ ] New builder that: defaults to AND between terms, **preserves explicit `OR`**, and emits valid
      FTS5 syntax against `AssistantChatMessagesFts`. Decide between (a) a curated builder that
      recognizes `OR` tokens and otherwise prefix-matches, or (b) accepting near-raw FTS5 with minimal
      sanitization. Recommend (a) for safety (avoid passing raw user text that can throw FTS5 syntax errors).
- [ ] Do **not** touch `BuildFtsQuery`/`SanitizeFtsToken` (`:444-469`) — the history-UI search
      (`AssistantHistoryViewModel`) relies on its prefix/operator-stripping behavior. (§7 regression risk.)

### Step 6 — Register the tool (NEW entries; model on existing built-ins)

- [ ] `BuiltInPluginDefaults`: add `SessionSearchPluginId` (new well-known GUID, e.g.
      `10000000-0000-0000-0000-000000000007`), add to `PreloadedPluginIds`, add a `SyncPlugin` default
      with `Kind="builtin_tool_pack"`, `Name="session-search"`, `ConfigJson` carrying
      `handlerId="session-search"`, `defaultEnabled:true`, and a `systemPromptAddition` that includes
      the **source-first caveat** (searches conversation history only; if a URL/file/account was given,
      inspect that source — don't conclude "not found" from history alone).
- [ ] `BuiltInPluginHandler`: add `FromSessionSearchHandler(ISessionSearchToolHandler, SyncPlugin)`
      mirroring `FromResearchHistoryHandler` (`:161-177`). No `isAvailable` gate (always available).
- [ ] `PluginService.InitializeBuiltInPlugins` (`:79-88`): add a `"session-search" => …FromSessionSearchHandler(…)`
      switch arm; inject `ISessionSearchToolHandler` into `PluginService` (constructor field, like
      `_researchHistoryToolHandler`).
- [ ] `Bootstrapper.cs`: register `services.AddSingleton<ISessionSearchToolHandler, SessionSearchToolHandler>();`
      near the other tool handlers (`:246`/`:250`). The handler depends on `IAssistantChatService`
      (already a singleton at `:261`) — no new store registration needed if Step 3 extends it.

### Step 7 — Privacy-logging compliance (CLAUDE.md "Privacy-First Logging")

- [ ] Query text, snippets, message content, titles, and session ids are **sensitive payloads/user-named
      items** → log only via `SensitiveDebug`/`SensitiveTrace` (compile-erased in RELEASE).
- [ ] Dispatch/diagnostic logging at `LogInformation` may include **only** the tool name, the resolved
      shape, and a result **count** — mirror `ResearchHistoryToolHandler` which logs just `ToolName`.
- [ ] No URLs are involved; `SafeUrl` not needed here.

---

## 8. Regression risks

- **R1 — FTS index drift (highest).** The new `AssistantChatMessagesFts` must be maintained in *every*
  write/delete path (Step 2). A missed path (e.g. `EvictOlderThanAsync`) silently leaves stale message
  rows → DISCOVERY returns deleted/edited content. Mitigate: enumerate all paths (done in Step 2),
  and consider a single private `ReplaceMessageFtsRowsAsync` helper called from each, parallel to
  `ReplaceFtsRowAsync`, so the maintenance logic lives in one place.
- **R2 — Touching `BuildFtsQuery` would regress history search.** `AssistantHistoryViewModel`'s
  search box depends on the prefix-match + operator-stripping behavior. The DISCOVERY `OR` requirement
  must go in a **separate** builder (Step 5), never by editing the shared one.
- **R3 — `Ordinal` re-numbering on save.** `SaveCoreAsync` rewrites ordinals `0..n`. If any future
  feature mutates message history mid-stream, previously-returned `around_message_id` values would point
  at different messages. Document the append-only assumption (§6); not a regression today.
- **R4 — Index size / write latency.** One FTS row per message (vs one per chat) multiplies index size
  and adds per-message inserts on every save. For typical chat volumes this is negligible, but note it;
  the backfill (Step 1) runs once and could be slow on very large existing DBs — it already gates on
  "FTS empty," so it won't re-run.
- **R5 — No sandbox/UX regression.** `FilesToolHandler`, `SafeFolderPath`, the sandbox folder setting,
  and the action-card pipeline are **untouched** — this tool is read-only and DB-only. Confirmed N/A.
- **R6 — Tool-count creep in the prompt.** Adds one always-on tool to the default set. Acceptable; it is
  cheap and read-only, consistent with `research-history`.

---

## 9. Open questions

- **`source` field semantics.** The spec lists `source` per DISCOVERY result but doesn't define it.
  Candidates: `ProviderId` (which LLM provider produced the chat), `WindowMode` (`Assistant` vs other),
  or a literal `"chat-history"` marker. **Recommend** `WindowMode` (+ optionally provider) — confirm intent.
- **`sort=newest|oldest` blend formula.** bm25 relevance vs recency is undefined. Propose a heuristic
  (e.g. rank by `bm25()`, then within near-ties bias by `UpdatedAt`; or a weighted score) — do **not**
  invent false precision. Needs a product decision.
- **`OR` builder strategy (Step 5).** Curated recognizer vs near-raw FTS5 passthrough. Raw passthrough
  risks FTS5 syntax exceptions on arbitrary user text; recommend the curated approach.
- **Drop `profile`?** Pia is single-profile (one `SqliteContext`); no cross-profile DB exists.
  **Recommend dropping** per the spec's own allowance. Confirm no near-term multi-profile plan.
- **READ head+tail thresholds.** Spec example is "first 20 + last 10 when large." Confirm the count
  threshold that triggers truncation and the head/tail sizes.
- **`task_id` threading (cross-cutting, suite-wide).** Not needed for `session_search`, but the broader
  coding-tools suite wants it day one. Decide separately whether to widen
  `IPluginToolHandler.HandleToolCallAsync` to carry a session/task id before more tools land — retrofitting
  later is painful. Out of scope for this tool; flagged for the suite owner.
- **Lineage dedupe.** Confirmed downgrade to per-`session_id` dedupe (§G10). If true fork/lineage tracking
  is ever wanted, `AssistantChats` needs a parent/lineage column — out of scope here.
