# Implementation Plan: `todo` coding tool (session task list)

> **Status:** PLANNING ONLY — no code in this doc is to be written yet.
> **Classification:** `scratch` (build from scratch). The name `todo` is a false friend: Pia already
> has a *persistent, SQLite-backed, kanban* todo feature exposed as 7 tools (`create_todo`,
> `query_todos`, …) behind action-card approval. The Hermes `todo` is a single **ephemeral,
> task_id-scoped, in-memory** tool with no persistence, no approval card, and merge/replace semantics.
> The contracts are disjoint, so the tool **body is new**; only the **integration plumbing is reused**
> (see [§2](#2-placement-in-piawpf)).
> **Spec source:** [`../todo.md`](../todo.md) · cross-cutting: [`../overview.md`](../overview.md),
> [`../tool_registration.md`](../tool_registration.md).

---

## 1. Tool contract (restated from the spec)

### 1.1 Name & schema

Model-facing tool name: **`todo`** (singular). Verified free — the existing persistent feature exposes
`create_todo` / `query_todos` / `complete_todo` / `update_todo` / `delete_todo` / `list_columns` /
`move_todo`, never the bare `todo`. No anti-shadow collision (`tool_registration.md` §1: a name under a
*different* toolset is rejected without `override`; here there is no name clash at all).

```jsonc
{
  "name": "todo",
  "description": "<copy verbatim from spec todo.md lines 12 — it is part of the contract>",
  "parameters": {
    "type": "object",
    "properties": {
      "todos": {
        "type": "array",
        "description": "Task items to write. Omit to read current list.",
        "items": {
          "type": "object",
          "properties": {
            "id":      {"type": "string", "description": "Unique item identifier"},
            "content": {"type": "string", "description": "Task description"},
            "status":  {"type": "string", "enum": ["pending","in_progress","completed","cancelled"]}
          },
          "required": ["id", "content", "status"]
        }
      },
      "merge": {"type": "boolean", "default": false}
    },
    "required": []
  }
}
```

> The `description` string is **load-bearing** (`overview.md` §"Schema conventions"): it is the only
> steering the model gets. Copy it verbatim, including the soft behavioral rules. With
> `AIFunctionFactory.Create`, pass the description as the third argument exactly as the sibling handlers
> do; if the factory cannot reproduce the nested-array schema faithfully, fall back to a hand-authored
> `AIFunction` so the emitted JSON Schema matches the contract.

### 1.2 Parameter semantics

| Input | Behavior |
|-------|----------|
| no args (`todos` omitted) | **Read.** Return the full current list for this `task_id`. |
| `todos` present, `merge` absent/`false` | **Replace.** Discard the existing list; the provided array becomes the whole list. |
| `todos` present, `merge=true` | **Upsert by `id`.** Update items whose `id` matches an existing item in place (preserving order); append items with new ids in array order. |

### 1.3 Return shape

The spec mandates "always return the full current list" after **any** call (read or write) but does not
fix a serialization. **Recommendation: a JSON array** of `{id, content, status}` in list order, e.g.

```json
[{"id":"1","content":"Read spec","status":"completed"},
 {"id":"2","content":"Write plan","status":"in_progress"}]
```

Rationale: machine-reparseable, stable ordering = priority, and it is what the agent reasons over on the
next turn. Empty list returns `[]`. (Human-readable vs JSON is a minor open question — see [§6](#6-open-questions).)

### 1.4 Required invariants

- **Always return the full list** (post-write included).
- **State is in-memory, scoped to `task_id`** — never persisted, never synced, gone when the process exits.
- Item shape is `{id, content, status ∈ pending|in_progress|completed|cancelled}`; reject anything else.
- **Soft rules (steer, don't enforce):** list order is priority; exactly one `in_progress`; mark
  `completed` on finish, `cancel`+revise on failure. These live in the description; the handler does
  **not** hard-fail them (spec line 45 "enforce softly").

---

## 2. Placement in Pia.Wpf

Mirror the existing built-in-tool conventions exactly. New types in **bold**; everything else is the
established pattern being copied.

### 2.1 New types

| Concern | Type | Location | Notes |
|---------|------|----------|-------|
| Handler interface | **`ISessionTodoToolHandler`** | `src/Pia.Wpf/Services/Interfaces/ISessionTodoToolHandler.cs` | Mirrors `ITodoToolHandler` shape (`GetTools`, `HandleToolCallAsync`, `ExecutePendingActionAsync`). Distinct name avoids the taken `ITodoToolHandler`. |
| Handler impl | **`SessionTodoToolHandler`** | `src/Pia.Wpf/Services/SessionTodoToolHandler.cs` | Holds the per-task state. New body. |
| Ambient task id | **`TaskIdAmbient`** | `src/Pia.Wpf/Services/TaskIdAmbient.cs` | `AsyncLocal<string?>`, mirrors `TokenMapAmbient`. Shared infra (see [§3.7](#37-task_id-keyed-state)). |
| Plugin GUID | **`SessionTodoPluginId = …007`** | `BuiltInPluginDefaults.cs` | Next in sequence after `FilesPluginId` (`…006`). Add to `PreloadedPluginIds` + `Defaults`. |

> **Naming choice:** `SessionTodo*` over alternatives (`EphemeralTodo*`, `AgentTodo*`) because "session"
> aligns with `ChatSession`, the unit the `task_id` keys off. Plugin `Name = "session-todo"`,
> `handlerId = "session-todo"`. The **model-facing tool name stays `todo`** — the plugin name is internal.

### 2.2 Reusable patterns to follow (copy, do not reinvent)

| Pattern | Source file | How `todo` uses it |
|---------|-------------|---------------------|
| Tool registration via `GetTools()` | `TodoToolHandler.cs:28`, `FilesToolHandler` | Return one `AITool` (`AIFunctionFactory.Create(Schema, "todo", "<desc>")`). |
| Dispatch via `HandleToolCallAsync` | `TodoToolHandler.cs:56` | `switch` on `toolCall.Name`; for `todo` → read/replace/merge then return `(jsonList, null)`. |
| Adapter factory | `BuiltInPluginHandler.FromTodoHandler` (`:98`) | Add **`FromSessionTodoHandler`** — identical to `FromFilesHandler` but **no `isAvailable` gate** (the tool is always available; there is no sandbox dependency). |
| Built-in registration | `PluginService.InitializeBuiltInPlugins` (`:73`) | Add `"session-todo" => BuiltInPluginHandler.FromSessionTodoHandler(_sessionTodoToolHandler, config)` to the switch; inject `ISessionTodoToolHandler` into the ctor (`PluginService.cs:42`). |
| Default config | `BuiltInPluginDefaults.Defaults` (`:22`) | Add a `SyncPlugin` entry keyed by `SessionTodoPluginId` with `handlerId:"session-todo"`, `defaultEnabled:true`, and a `systemPromptAddition` describing when to use the list. |
| DI wiring | `Bootstrapper.cs:249` | `services.AddSingleton<ISessionTodoToolHandler, SessionTodoToolHandler>();` — **singleton** (state must outlive a turn and be shared across the process, keyed internally by `task_id`). |
| Privacy logging | `TodoToolHandler.cs:83` (`SensitiveDebug`; note `:62` is a `#if DEBUG` `Debug.WriteLine`, not the helper) | Task `content` is **user-named** (CLAUDE.md "todo title" → sensitive). Log counts/ids at `LogInformation`; log `content` only via `_logger.SensitiveDebug`. No URLs here, so `SafeUrl` is N/A. |

### 2.3 Where it plugs into dispatch

`ChatSession.HandleToolCall` (`:404`) → `_pluginService.RouteToolCallAsync(toolCall)` (`:412`) →
`_toolNameRoutes["todo"]` → the `FromSessionTodoHandler` adapter → `SessionTodoToolHandler`. No dispatch
code changes; registration of the new route is automatic via `RegisterHandler` / `RebuildToolNameRoutes`.

### 2.4 One deliberate divergence from sibling handlers

Every existing `FromXxxHandler` maps a pending action → `PluginToolCall` → an ActionCard (approval gate).
**`todo` has no approval card** (spec + classification: the agent edits its own scratchpad; nothing is
written to disk or the user's data). So:

- `SessionTodoToolHandler.HandleToolCallAsync` **always returns `(result, null)`** — never a pending action.
- `FromSessionTodoHandler` therefore never produces a `PluginToolCall`. `BuiltInPluginHandler` already
  short-circuits when pending is null (`BuiltInPluginHandler.cs:87` pattern), so **no plumbing change** —
  but call it out, since it is the single place `todo` diverges from the six siblings.
- `ExecutePendingActionAsync` is required by the interface but is effectively unreachable; implement it as
  a no-op / `Task.FromResult<object?>(null)`.

---

## 3. Cross-cutting invariants — applicability map

The overview (`overview.md` §"Cross-cutting design principles") lists **10** numbered cross-cutting
principles; `tool_registration.md` adds the host-layer concerns (registry/dispatch/budgeting/approval).
There is **no** separately-numbered "cross-cutting questions" list in either spec doc — the "question #N"
labels below are this plan's own framing of design decisions, not spec citations. **For a todo list most
are N/A.** Honest mapping below — do not pad a todo plan with a code-execution security model.

### 3.1 Native vs MCP (design decision A) — **must be native**

The list is **in-process session state the host re-injects** (see [§3.8](#38-surviving-compression-the-headline-feature)).
An external MCP server can hold state but cannot be re-injected into Pia's per-turn system prompt and is
not keyed by Pia's `ChatSession.Id`. Build natively.

### 3.2 Other cross-toolset decisions — N/A for `todo`

(These are decisions raised by sibling tool plans; none bear on `todo`. Not numbered in the spec.)

| Topic | Verdict for `todo` |
|-------|--------------------|
| code-exec consent/gating (`tool_registration.md` §6 approval guard) | N/A — `todo` runs no commands. Owned by `terminal`/`execute_code` plans. |
| filesystem scope / workspace root | N/A — `todo` touches no filesystem. Owned by file-tool plans. |
| extend vs rebuild `FilesToolHandler` | N/A — different subsystem; this is the `todo`/state subsystem. |
| Python runtime for `execute_code` | N/A. |

### 3.3 Self-healing arg validation (principle #10) — **APPLIES**

Models drop/garble args under context pressure. The handler must repair, not crash:

- **Coerce `merge`**: accept `true`/`false`, `"true"`/`"false"`, `1`/`0`; default `false` on absent/garbage.
- **`todos` array**: if a single object is sent instead of an array, wrap it; if a dict-of-items is sent,
  take its values. Reject non-array, non-object scalars with a precise corrective error.
- **Per item**: require `id` (coerce non-string to string), require `content` (non-empty); reject the
  item with a corrective message if missing. **Validate `status`** against the enum; on an unknown value
  return `{"error":"item <id>: status must be one of pending|in_progress|completed|cancelled"}`.
- Error envelope is a **string `{"error": "..."}`** matching `tool_registration.md` §3 — never throw into
  the agent loop.

### 3.4 task_id-keyed state (principle #"State each tool needs") — **APPLIES (core)**

See [§3.7](#37-task_id-keyed-state). This is the one invariant that is central to `todo`.

### 3.5 Atomic writes (principle #9) — **REFRAMED as a locked in-memory swap**

There is no file, so "temp file + rename / preserve CRLF/BOM" is N/A. The real concern is a **race-free
state mutation**: replace and merge must be atomic against concurrent background turns (Pia runs
multi-assistant / background chats). Implementation: a `ConcurrentDictionary<string, List<TodoItem>>`
keyed by `task_id`, and mutate each task's list under a per-task `lock` (or replace the list reference
atomically). The dictionary itself is the registry; the lock guards read-modify-write for `merge`.

### 3.6 Loop/dedup guard (principle #4) & output cap (`tool_registration.md` §4 budgeting / `max_result_size_chars`) — **WEAK / OPTIONAL**

- **Dedup:** repeated identical reads are cheap (in-memory) and harmless; a hard loop guard is over-
  engineering. Optional: collapse N consecutive identical no-op writes. Mark as nice-to-have.
- **Output cap:** the list is small and bounded. A `max_result_size_chars` is unnecessary in practice,
  but to honor `tool_registration.md` set a generous cap (e.g. the shared default) so a pathological
  10k-item list cannot blow context. Truncate by dropping items with a `"...N more"` marker, not by
  cutting JSON mid-token.

### 3.7b N/A invariants (state explicitly, one line each)

| Principle | Why N/A for `todo` |
|-----------|--------------------|
| #1 line-numbered reads | No file content. |
| #2 fuzzy matching | No `old_string` edits. |
| #3 delta-filtered diagnostics | No syntax/lint check applies. |
| #5 staleness / mtime tracking | No file mtime. |
| #6 return-a-diff / verify-by-reread | Replace/upsert of small in-memory list; full list already returned. |
| #7 head+tail truncation | No long command/script output. |
| #8 pagination | List is small and bounded; no offset/limit. |

### 3.7 `task_id` threading (shared infra — specified here first)

**Finding:** Pia dispatch does **not** thread a task/session id into handlers today.
`IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, ct)` receives only `Name`, `Arguments`,
`CallId`. `ChatSession.Id` (`Guid?`, `ChatSession.cs:46`) exists but stops at the session boundary.

**Recommendation — mirror `TokenMapAmbient` exactly (ambient `AsyncLocal`), do NOT change the handler
signature:**

1. New `TaskIdAmbient` with `private static readonly AsyncLocal<string?> _current` and a `Current`
   property — a direct copy of `TokenMapAmbient.cs`.
2. In `ChatSession.RunTurnAsync`, **right beside** `TokenMapAmbient.Current = TokenMap` (`ChatSession.cs:202`),
   set `TaskIdAmbient.Current = Id?.ToString() ?? "default"`; restore the previous value in the same
   `finally` block that restores `TokenMapAmbient` (`:361`). The `"default"` fallback matches the spec
   default for null sessions / direct test callers.
3. `SessionTodoToolHandler` reads `TaskIdAmbient.Current ?? "default"` to key its state.

| Option | Blast radius | Verdict |
|--------|-------------|---------|
| **A. `TaskIdAmbient` (`AsyncLocal`)** | Two new lines in `RunTurnAsync` + a 28-line class. Zero change to the other 6 handlers or `IPluginToolHandler`. | **Chosen.** |
| B. Add `taskId` param to `IPluginToolHandler.HandleToolCallAsync` | Touches the interface, `BuiltInPluginHandler`, `McpPluginToolHandler`, `PluginService.RouteToolCallAsync`, every handler + every test. | Rejected — large blast radius for one tool. |

> **Why specify it here:** `todo` is the **first** per-task-state tool, so it pays the cost of introducing
> `TaskIdAmbient`. The later stateful coding tools (`process` background-process registry, `read_file`
> read-dedup/mtime caches, persistent-shell cwd — see `overview.md` §"State each tool needs") **reuse the
> same ambient**. Implement `task_id` day one; the spec warns retrofitting is painful (`overview.md`
> §"Tool-registration contract (host-side)" → `task_id` threading; also `tool_registration.md` §1).

### 3.8 Surviving compression (the headline feature)

**Finding (verified):** Pia has **no mid-conversation summarization/compression**. The only context
shrink is hard token-cap truncation: `AiClientService` raises `LlmTruncatedException` on
`finish_reason=Length` (`AiClientService.cs:295,545`) — which **aborts** the turn rather than summarizing
history. There is no summary/compaction subsystem. So the handler alone cannot deliver "survives
compression," but the feature is still deliverable cheaply because the list is small and bounded.

**Recommendation — split the feature: handler owns state, composer owns re-injection.**

- The host **re-injects the current `task_id` list into the per-turn system prompt on every turn**, via
  `AssistantPromptComposer.PrepareTurn` (`AssistantPromptComposer.cs:26`), sibling to the existing
  `_pluginService.GetCombinedSystemPromptAdditions()` call inside `BuildSystemPrompt`
  (`AssistantPromptComposer.cs:119`). Because the prompt is rebuilt from `Messages` each turn
  (`ChatSession.cs:217`), injecting the list there makes it survive **any** present or future truncation
  /compression — without building a compaction subsystem.
- Mechanism: `ISessionTodoToolHandler` exposes a `RenderForPrompt(string taskId)` returning the list as a
  compact block (or empty when the list is empty); `PrepareTurn` appends the block to the system prompt.
  Keep the block tiny and clearly delimited so it does not crowd persona/tool guidance.
- **Ordering caveat (verified):** `PrepareTurn` is **not** called inside `ChatSession.RunTurnAsync`.
  It runs earlier, in `ChatSessionManager` (`ChatSessionManager.cs:379`), and its result is passed in as
  `request.TurnSetup` (consumed at `ChatSession.cs:180`). That is **before** the point where this plan
  proposes setting `TaskIdAmbient` (`ChatSession.cs:202`). So a composer that reads `TaskIdAmbient.Current`
  would see the *previous* turn's value (or null) — the naïve "set at :202, read in `PrepareTurn`" wiring
  **does not work as written**. Resolve one of two ways: (a) set `TaskIdAmbient.Current` in
  `ChatSessionManager` right before the `PrepareTurn` call (the session `Id` is known there), or (b) pass
  the `taskId` explicitly into `PrepareTurn` rather than via the ambient. Pick the wiring during
  implementation — see open-question Q4.

This converts the KNOWN GAP ("not deliverable by the handler alone") into a concrete two-part deliverable.
Confirm the exact injection point with whoever owns `AssistantPromptComposer` (see [§6](#6-open-questions)).

---

## 4. Build / implementation checklist

- [ ] **`TaskIdAmbient`** (`AsyncLocal<string?>`), copied from `TokenMapAmbient.cs`.
- [ ] Set/restore `TaskIdAmbient.Current` in `ChatSession.RunTurnAsync` beside the `TokenMapAmbient` set
      (`:202`) and restore (`:361`). This covers the **handler-dispatch** path (handlers run after `:202`).
      The **composer re-injection** path needs the ambient (or an explicit `taskId`) earlier — see §3.8
      and Q4 (`PrepareTurn` runs in `ChatSessionManager` before `RunTurnAsync`).
- [ ] **`ISessionTodoToolHandler`** interface (`GetTools`, `HandleToolCallAsync`,
      `ExecutePendingActionAsync`, `RenderForPrompt(taskId)`).
- [ ] **`SessionTodoToolHandler`** impl:
  - [ ] `ConcurrentDictionary<string, List<TodoItem>>` keyed by `task_id`; per-task lock for merge.
  - [ ] `GetTools()` returns one `AITool` for `todo` with the verbatim spec description.
  - [ ] `HandleToolCallAsync`: read (no args) / replace (`merge=false`) / upsert-by-id (`merge=true`);
        **always return the full list as JSON**, `(result, null)` always.
  - [ ] Self-healing arg validation (coerce `merge`, normalize `todos` shape, validate `status` enum,
        require `id`+`content`) → string `{"error":...}` on failure.
  - [ ] Generous output cap with item-drop truncation marker.
  - [ ] `RenderForPrompt(taskId)` for the composer.
  - [ ] `ExecutePendingActionAsync` → no-op.
  - [ ] Privacy logging: counts/ids at `LogInformation`; `content` only via `SensitiveDebug`.
- [ ] **Item model**: a small internal record `{ string Id; string Content; SessionTodoStatus Status; }`
      (new enum `pending|in_progress|completed|cancelled`). Do **not** reuse `Models/TodoItem.cs` (GUID +
      kanban shape — wrong contract).
- [ ] **`BuiltInPluginHandler.FromSessionTodoHandler`** factory (no `isAvailable` gate).
- [ ] **`BuiltInPluginDefaults`**: `SessionTodoPluginId = …007`, add to `PreloadedPluginIds` + `Defaults`
      (`handlerId:"session-todo"`, system prompt addition).
- [ ] **`PluginService`**: inject `ISessionTodoToolHandler`; add `"session-todo"` switch arm in
      `InitializeBuiltInPlugins`.
- [ ] **`Bootstrapper`**: `AddSingleton<ISessionTodoToolHandler, SessionTodoToolHandler>()` (singleton).
- [ ] **`AssistantPromptComposer.PrepareTurn`**: append `RenderForPrompt(taskId)` to the system prompt
      (the compression-survival bridge). Source `taskId` per the Q4 decision — either set
      `TaskIdAmbient.Current` in `ChatSessionManager` before the `PrepareTurn` call, or pass `taskId` in
      explicitly (do **not** assume the ambient is already set at composer time — it is not).
- [ ] CRLF line endings on all new `.cs` files (repo convention — MEMORY: Write tool emits LF).

---

## 5. Test strategy (xunit.v3)

Match the repo: xunit.v3 + plain `Xunit.Assert` (no FluentAssertions), `NullLogger<T>.Instance`, hand-
rolled fakes — pattern from `tests/Pia.Wpf.Tests/Unit/ResearchHistoryToolHandlerTests.cs`. New file:
`tests/Pia.Wpf.Tests/Unit/SessionTodoToolHandlerTests.cs`.

| Area | Cases |
|------|-------|
| Read | No-args on empty list → `[]`. No-args after a write → full list in order. |
| Replace | `merge=false` discards prior list; provided array becomes whole list. |
| Merge | `merge=true` updates matching `id` in place (order preserved); appends new ids in array order. |
| Always-full | Every write returns the full current list, not a delta. |
| task_id isolation | Two `task_id`s via `TaskIdAmbient.Current` keep separate lists; cross-talk = fail. Set the ambient inside the test (await flows `AsyncLocal`). |
| Default key | `TaskIdAmbient.Current == null` → keyed under `"default"`. |
| Self-healing | `merge` as `"true"`/`1`; single object instead of array; missing `content`; **bad `status`** → corrective `{"error"}`, no throw. |
| Soft rules | Two `in_progress` items are **accepted** (not enforced) — guards against accidental hard-fail. |
| No approval | `HandleToolCallAsync` returns `pending == null` for every input. |
| RenderForPrompt | Empty list → empty/omitted block; populated list → compact block keyed by `task_id`. |
| Registration | Extend `Architecture/DiRegistrationTests.cs` to assert `ISessionTodoToolHandler` resolves and the `…007` plugin is preloaded with route `todo`. |

> Out of scope for unit tests (integration concern): the `AssistantPromptComposer` re-injection wiring —
> cover with a focused composer test if `PrepareTurn` is unit-testable, else note as manual verification.

---

## 6. Open questions

1. **Return format:** JSON array (recommended) vs human-readable text. JSON is machine-reparseable and
   matches "the agent reasons over the whole plan." Confirm with the model-prompting owner.
2. **`task_id` lifecycle:** when a `ChatSession` is reaped (`ChatSessionManager.ReapStaleSessions`,
   `MaxRetainedSessions=8`) or a new chat starts, should its `todo` entry be evicted? Recommend: evict on
   session disposal to avoid unbounded `ConcurrentDictionary` growth. Needs a hook from session lifecycle
   to the handler (e.g. a `Clear(taskId)` call) — not in the spec, but a real leak otherwise.
3. **Composer injection point:** exact placement/format of the re-injected block in `BuildSystemPrompt`
   (before vs after tool-selection guidance; token budget). Owner of `AssistantPromptComposer` to confirm.
4. **`PrepareTurn` ↔ `TaskIdAmbient` ordering (RESOLVED FINDING — needs a wiring decision):** verified
   that `PrepareTurn` is called in `ChatSessionManager` (`ChatSessionManager.cs:379`), strictly **before**
   `ChatSession.RunTurnAsync` and therefore before the proposed `TaskIdAmbient` set at `ChatSession.cs:202`.
   So `TaskIdAmbient.Current` is NOT yet set when the composer runs. Decide the fix at implementation time:
   (a) set `TaskIdAmbient.Current` in `ChatSessionManager` immediately before the `PrepareTurn` call (the
   session `Id` is in scope there), or (b) thread `taskId` into `PrepareTurn`/`RenderForPrompt` explicitly
   and skip the ambient for the composer path. See [§3.8](#38-surviving-compression-the-headline-feature).
5. **Should `todo` be user-toggleable** like other built-in plugins (`UserEnabled`)? It is agent infra,
   not a user feature — consider defaulting on and hiding from the plugin-management UI, or leave standard.
6. **Soft-rule nudges:** should the handler add a non-fatal advisory string (e.g. "note: 2 items
   in_progress") to the returned payload, or stay silent and rely purely on the description? Recommend
   silent to match "enforce softly."
