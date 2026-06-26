# Implementation Plan — `memory` coding tool (from scratch)

> **Status:** PLANNING ONLY. No code has been written. Every type name, signature, and GUID
> below is **proposed**.
> **Bucket:** `scratch` — the Hermes `memory` tool and Pia's existing memory subsystem share only
> the word "memory". Storage, tool surface, and injection are all net-new. We **reuse the
> registration/dispatch scaffolding** (the `IMemoryToolHandler`-style slot, the
> `BuiltInPluginHandler.From*` factory, `PluginService` route registration, the
> `FunctionCallContent` dispatch loop, and optionally `ActionCardInfo`), but the substantive logic
> is greenfield.
> **Spec:** [`../memory.md`](../memory.md). **Host layer:** [`../tool_registration.md`](../tool_registration.md).
> **Overview invariants:** [`../overview.md`](../overview.md).

---

## 0. The sharp fork up front (read before anything else)

Pia **already has** a thing called "memory": a SQLite object store (`MemoryService`) fronted by
`MemoryToolHandler` exposing **8 tools** (`create_object`, `update_object`, `append_to_list`,
`list_memories`, `query_memory`, `delete_object`, `merge_memories`, `find_duplicates`), with
embeddings + FTS5/vector hybrid search, registered as the built-in plugin **named `memory`**
(`BuiltInPluginDefaults.MemoryPluginId = 10000000-…-0001`).

The Hermes `memory` tool is a **single tool literally named `memory`** that batches
`add`/`replace`/`remove` operations against two flat Markdown files and is **injected into the
system prompt** as a frozen snapshot at session start. It has none of the storage, search, or
8-tool surface of Pia's subsystem.

**Precise collision analysis** (corrected — do not overstate):

| Layer | Collide? | Why |
|-------|----------|-----|
| Tool-name routing (`PluginService._toolNameRoutes[tool.Name]`) | **No** | Existing plugin's tool names are `create_object`/…; the Hermes tool's name is `memory`. Distinct keys → no overwrite. Pia has **no anti-shadow guard** anyway (last writer wins silently — see `tool_registration.md §register() rules`, which Pia does not implement). |
| Plugin **identity / Name** in `BuiltInPluginDefaults` | **Yes (conceptual)** | The plugin `Name = "memory"` is already taken. If both are enabled, the model sees **two memory subsystems**: two `## Plugins` prompt sections, the tool-selection tree's "step 3 → Memory tools", overlapping families. This is the thing to design around. |
| `AtCommandDomain.Memory` `@memory` command | **Yes (conceptual)** | `@memory` maps to the SQLite tools in `AssistantPromptComposer.GetAtCommandToolMapping`. A second store muddies what `@memory` targets. |

**Recommended default (maintainer's call — Open Question #1):** ship the Hermes `memory` as a
**NEW, SEPARATE store alongside** the SQLite subsystem — flat `MEMORY.md` / `USER.md` under
`%LOCALAPPDATA%\Pia`, snapshot-injected at session start — **not** a replacement (replacing
`MemoryService` is a rewrite, out of scope, and regresses the `@memory` UX). Gate it so both are
not active in one posture (a coding/workspace feature flag, or a distinct plugin id that is
**off by default**). New GUID (`…07`), new `BuiltInPluginDefaults` entry, new
`BuiltInPluginHandler.From…` factory — all **proposed**, no `.cs` touched in this plan.

---

## 1. Tool contract (restated exactly from the spec)

### 1.1 Name & purpose

- **Name:** `memory`
- **Purpose:** save durable, compact, high-signal facts that survive across sessions; injected into
  every future turn's system context. Two stores: `memory` (agent's own notes) and `user`
  (user profile).

### 1.2 JSON Schema (verbatim from `memory.md`)

```json
{
  "name": "memory",
  "description": "Save durable facts to persistent memory that survive across sessions. Memory is injected into every future turn, so keep entries compact and high-signal.\n\nHOW: make ALL changes in ONE call via an 'operations' array (each item: {action, content?, old_text?}). The batch applies atomically and the char limit is checked only on the FINAL result — so one call can remove/replace stale entries to free room AND add new ones. Use bare action/content/old_text only for a single lone change.\n\nWHEN: save proactively on a stated preference, correction, or stable fact about environment/conventions/workflow. Priority: user preferences & corrections > environment facts > procedures.\n\nIF FULL: an add is rejected with current entries shown; reissue as ONE batch that removes/shortens enough and adds together.\n\nTARGETS: 'user' = who the user is. 'memory' = your notes (environment, conventions, tool quirks, lessons).\n\nSKIP: trivial/obvious info, easily re-discovered facts, raw data dumps, task progress, completed-work logs, temporary TODO state. Reusable procedures belong in a skill, not memory.",
  "parameters": {
    "type": "object",
    "properties": {
      "action":   {"type": "string", "enum": ["add", "replace", "remove"], "description": "Single-op shape. Omit when using 'operations'."},
      "target":   {"type": "string", "enum": ["memory", "user"], "description": "'memory' = personal notes, 'user' = user profile."},
      "content":  {"type": "string", "description": "Entry content. Required for add/replace (single-op shape)."},
      "old_text": {"type": "string", "description": "Required for replace/remove (single-op): a short unique substring identifying the existing entry."},
      "operations": {"type": "array", "description": "Batch shape: list of operations applied atomically against the FINAL char budget. Preferred for multiple changes or consolidation.",
                     "items": {"type": "object", "properties": {
                       "action":   {"type": "string", "enum": ["add", "replace", "remove"]},
                       "content":  {"type": "string"},
                       "old_text": {"type": "string"}
                     }, "required": ["action"]}}
    }
  }
}
```

> The `description` is **part of the contract** (per `overview.md §Schema conventions`) — copy it
> byte-for-byte into the proposed `[Description]` attribute / schema builder. It is the only steering
> the model gets about when/how to use the tool.

### 1.3 Parameter semantics

| Field | Shape | Required | Meaning |
|-------|-------|----------|---------|
| `action` | `add`\|`replace`\|`remove` | single-op only | The lone change. Omit when `operations` is present. |
| `target` | `memory`\|`user` | yes | Which store. `memory` = agent notes (→ `MEMORY.md`); `user` = user profile (→ `USER.md`). |
| `content` | string | for `add`/`replace` | The entry text to add or the replacement text. |
| `old_text` | string | for `replace`/`remove` | A short **unique substring** identifying the existing entry. Ambiguous or no match → error. |
| `operations[]` | array of `{action, content?, old_text?}` | batch only | Applied **atomically**; char budget checked **only on the final result**. Preferred for multiple changes / consolidation. |

### 1.4 Return shape (proposed, faithful to spec behaviors)

The tool returns a **string** (Pia tool results are stringly-typed; see `MemoryToolHandler` /
`FilesToolHandler` returning `object?` that is rendered to text). Cases:

- **Success:** short confirmation, e.g. `Saved 1 entry to user; removed 2 from memory.` Optionally
  echo the **resulting** store so the model knows the new state (cheap; helps the agent reason).
- **Overflow of a lone `add`:** **reject** and return the store's **current entries** verbatim so the
  agent can reissue as one consolidating batch (spec §2). Make the message explicit:
  `memory store is full (N/MAX chars). Add rejected. Reissue as ONE 'operations' batch that removes/shortens enough then adds. Current entries:\n<dump>`.
- **`old_text` ambiguity / no match:** error string, e.g.
  `old_text "<snip>" matched 0 entries in <target> (expected exactly 1).` or `…matched 3 entries…`.
- **Arg-shape error (self-healing):** precise corrective message (see §3 invariant #10).

> Errors are returned **as the result string**, never thrown into the loop — matches Pia's existing
> handlers and `tool_registration.md §3` ("uniform error envelope").

### 1.5 Required invariants (spec §Contract/behaviors)

1. **Two shapes.** Single-op (`action`+`target`+`content`/`old_text`) **or** `operations[]` batch.
   Batch is preferred and is the only way to free budget + add in one call.
2. **Atomic batch.** All ops in a batch apply together; the **char-limit check runs only on the
   final result** — so a `remove` can free room for an `add` that alone would overflow.
3. **Char budget per store.** Each store has a max size. Lone-`add` overflow → reject + echo current
   entries.
4. **`replace`/`remove` by unique substring.** `old_text` is a short unique substring; ambiguous/no
   match → error. (Deliberately **strict** — see §3 note vs the fuzzy-match invariant.)
5. **Selectivity is the whole game.** The description hard-codes what to SKIP (task progress,
   completed-work logs, transient state, easily re-discovered facts, raw dumps). No code enforces
   this — it is steering only — but the description must be reproduced verbatim.
6. **Frozen snapshot.** Both stores load as a **frozen snapshot into the system prompt at session
   start**; new writes take effect next session / next snapshot (keeps the cache-stable prefix
   intact mid-session).

---

## 2. Placement in Pia.Wpf (following existing conventions)

### 2.1 New types (proposed)

| Artifact | Proposed name / path | Mirrors |
|----------|----------------------|---------|
| Storage service iface | `IAgentMemoryStore` — `src/Pia.Wpf/Services/Interfaces/IAgentMemoryStore.cs` | (new; not `IMemoryService`, which is the SQLite store) |
| Storage service impl | `AgentMemoryStore` — `src/Pia.Wpf/Services/AgentMemoryStore.cs` | flat-file read/write of `MEMORY.md`/`USER.md` |
| Tool-handler iface | `IAgentMemoryToolHandler` — `src/Pia.Wpf/Services/Interfaces/IAgentMemoryToolHandler.cs` | `IFilesToolHandler` / `IMemoryToolHandler` shape |
| Tool-handler impl | `AgentMemoryToolHandler` — `src/Pia.Wpf/Services/AgentMemoryToolHandler.cs` | `FilesToolHandler` |
| Pending-action record | `AgentMemoryToolCall(string ToolName, string Description, string? Details, Func<Task<object?>> Execute)` | `FilesToolCall` |

> Naming avoids the existing `MemoryToolHandler` / `IMemoryToolHandler` / `MemoryService` /
> `IMemoryService` to keep the two subsystems unambiguous in code, matching the conceptual-collision
> resolution in §0. (If Open Question #1 lands on "replace", rename accordingly — but that is a
> rewrite, not this plan.)

### 2.2 Proposed handler interface

```csharp
// PROPOSED — src/Pia.Wpf/Services/Interfaces/IAgentMemoryToolHandler.cs
namespace Pia.Services.Interfaces;

public record AgentMemoryToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);

public interface IAgentMemoryToolHandler
{
    // Mirrors IFilesToolHandler.IsAvailable: suppress tool + prompt addition
    // when the feature is off (Open Question #1 gating).
    bool IsAvailable { get; }
    IList<AITool> GetTools();
    Task<(object? Result, AgentMemoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(AgentMemoryToolCall pendingAction);

    // Snapshot read for system-prompt injection (see §2.5).
    string BuildSnapshotForSystemPrompt();
}
```

### 2.3 Reusable patterns to follow (and which file models each)

| Pattern | Source file to copy | Apply to `memory` |
|---------|---------------------|-------------------|
| `GetTools()` via `AIFunctionFactory` + private schema methods with `[Description]` | `FilesToolHandler` | Single tool `memory`; schema = §1.2 verbatim |
| Dispatch via `HandleToolCallAsync(FunctionCallContent)` switch on `toolCall.Name` | `FilesToolHandler`, `MemoryToolHandler` | One case (`memory`) parsing the two shapes |
| Pending-action approval guard (prepare in `Handle…`, run in `ExecutePendingAction…`) | `FilesToolCall` / `FilesToolHandler.ExecutePendingActionAsync` | **Optional** — see Open Question #2 (silent vs confirmed write) |
| Availability gating via `IsAvailable` + `BuiltInPluginHandler` `isAvailable:` callback | `FromFilesHandler` | Suppress tool + prompt when feature off |
| Path-safety sandbox | `SafeFolderPath.TryResolveInside` / `IsConfiguredAndExists` | **N/A for store path** — the two files live under `%LOCALAPPDATA%\Pia` (a fixed app path), **not** the user `AssistantFilesFolder` sandbox. No user-supplied path reaches the filesystem. |
| Size limits as constants | `FilesToolHandler` (`MaxReadBytes`, `MaxWriteChars`) | `MaxStoreChars` per store (proposed default 4 000–8 000 chars; tune) |
| Privacy logging | `Pia.Logging` `SensitiveDebug` / `SafeUrl` | **Mandatory** — memory content is sensitive user facts; see §2.6 |
| Settings reactivity (volatile field, `SettingsChanged`) | `FilesToolHandler` | Only if a configurable enable flag / size cap is added |

### 2.4 Registration / dispatch wiring (proposed)

Follow the **exact** built-in path used by `files`:

1. **GUID + default entry** — add to `BuiltInPluginDefaults`:
   ```csharp
   // PROPOSED
   public static readonly Guid AgentMemoryPluginId = new("10000000-0000-0000-0000-000000000007");
   // add to PreloadedPluginIds and Defaults with:
   //   Kind = "builtin_tool_pack",
   //   Name = "agent-memory"            // NOT "memory" — avoids the conceptual collision (§0)
   //   ConfigJson = {"handlerId":"agent-memory","defaultEnabled":<see OQ#1>,"systemPromptAddition":"<copied from spec description steering>"}
   ```
   > Use a distinct plugin **Name** (`agent-memory`) so the two memory subsystems are separable in
   > the plugin registry / settings UI, even though the model-facing **tool** name stays `memory`.

2. **Factory** — add `BuiltInPluginHandler.FromAgentMemoryHandler(IAgentMemoryToolHandler, SyncPlugin)`
   mirroring `FromFilesHandler` (including the `isAvailable: () => handler.IsAvailable` arg).

3. **Switch arm** — add `"agent-memory" => BuiltInPluginHandler.FromAgentMemoryHandler(…)` to
   `PluginService.InitializeBuiltInPlugins()`.

4. **DI** — register `IAgentMemoryStore`/`AgentMemoryStore` and
   `IAgentMemoryToolHandler`/`AgentMemoryToolHandler` as singletons in `Bootstrapper.cs`, and add the
   `IAgentMemoryToolHandler` ctor param to `PluginService` (alongside `_filesToolHandler`).

> Once registered, **dispatch is free**: `PluginService.RouteToolCallAsync` already routes by tool
> name, `AiClientService.GetChatCompletionWithToolsAsync` already runs the tool loop, and
> `ChatSession.HandleToolCall` already builds the `ActionCardInfo` for any returned pending action.

### 2.5 System-prompt snapshot injection (the genuinely new hook)

**Problem:** Pia builds the system prompt **per turn** — `ChatSessionManager.StartTurnAsync`
(`src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs`; the `PrepareTurn` call is at line ~379)
calls `AssistantPromptComposer.PrepareTurn`, which assembles `## Plugins` etc. and hands
a fresh `AssistantTurnSetup.SystemPrompt` to `ChatSession.RunTurnAsync`
(`src/Pia.Wpf/ViewModels/Models/ChatSession.cs`, consumed at lines 214/219). There is **no per-session frozen memory snapshot** today, and
`GetCombinedSystemPromptAdditions()` returns **static plugin guidance** (the "which tool to call"
prose), **never memory content**.

**Proposed approach (described, not coded):**

- Compute the snapshot **once per session** and reuse it across that session's turns so the
  cache-stable prefix does not change mid-session (spec §6). Two viable seams:
  - **(A) Cache on `ChatSession`.** At session creation, call
    `IAgentMemoryToolHandler.BuildSnapshotForSystemPrompt()` once, store the string on the session,
    and have `PrepareTurn` append it to the prompt (e.g. as a dedicated `## Memory` section,
    distinct from the `## Plugins` steering section). Re-snapshot only on new session / activation.
  - **(B) Compose in `AssistantPromptComposer`** but key the snapshot to the session id so it is read
    once. Less clean given the composer is per-turn and stateless today.
- **(A) is recommended** — it matches the spec's "session start" wording and the existing
  background-chats / multi-session architecture where each `ChatSession` already owns per-session
  state (token map, messages). Note: background chats and the multiple assistant windows each form a
  session, so each gets its own frozen snapshot at its own start time — acceptable and consistent
  with the spec.
- **Do not** route memory content through `GetCombinedSystemPromptAdditions()` — that method is
  per-turn static steering and is shared cache-stably; injecting volatile content there would defeat
  prefix caching and mix concerns.

### 2.6 Privacy logging (mandatory)

Per `CLAUDE.md §Privacy-First Logging`, memory **content** is sensitive user facts (payloads +
user-named items). Rules for this handler:

- **Never** `LogInformation` entry text, `content`, `old_text`, or store dumps.
- Use `_logger.SensitiveDebug("memory add to {Target}: {Content}", target, content)` for any content
  line (`[Conditional("DEBUG")]` — erased from RELEASE IL).
- Plain `LogInformation`/`LogWarning` may state **counts and outcomes only**:
  `"memory batch applied: {Adds} add, {Removes} remove, {FinalChars} chars"`.
- No URLs are involved, so `SafeUrl` is not needed here; if a store path is ever logged, log it under
  `SensitiveDebug` (it is a fixed app path, low-risk, but content-adjacent).

---

## 3. Cross-cutting invariants (from `overview.md §Cross-cutting design principles`)

Each invariant mapped to **Applies / Partial / N-A** with the reason — scoped to `memory`, not the
whole host layer.

| # | Invariant | Verdict | Reason / what to implement |
|---|-----------|---------|----------------------------|
| 1 | Line-numbered reads are the coordinate system | **N-A** | `memory` neither reads files for the model nor anchors edits on line numbers; edits anchor on `old_text` substrings. |
| 2 | Fuzzy matching on edits is mandatory | **N-A (deliberately strict)** | The spec's `old_text` matching is **exact unique-substring** with ambiguity → error (spec §3). Do **not** implement the 9-strategy fuzzy chain here — that's `patch`'s contract, and conflating them would make `replace`/`remove` silently hit the wrong entry. Call this out explicitly. |
| 3 | Delta-filtered diagnostics | **N-A** | No syntax/lint check applies to Markdown facts. |
| 4 | Loop / dedup guards | **Partial / optional** | A duplicate `add` is harmless (or can be a no-op if the entry already exists). Optional: dedup identical entries on `add`. Not load-bearing. |
| 5 | Staleness tracking | **Partial** | *Within-session:* N-A — the snapshot is frozen, so the model's view is intentionally stale until next session. *Cross-store on disk:* background chats + multiple assistant windows can each write the same `MEMORY.md`/`USER.md` concurrently → real race. Implement **read-current → apply batch → write** under a per-store lock, last-writer-wins; optionally re-read before write to merge. Document the chosen policy. |
| 6 | Return a diff; verify the write persisted | **Partial** | Verify the atomic write landed (re-read after rename) and report counts; a full unified diff is overkill for flat facts — a `+N/-M entries` summary suffices. |
| 7 | Truncate output head+tail | **N-A** | Stores are small (≤ `MaxStoreChars`); the full current-entries echo on overflow is intentionally complete, not truncated. |
| 8 | Pagination everywhere (`offset`/`limit`) | **N-A** | Stores are capped small by design; no pagination needed. The **char budget** is the analog of this anti-overflow principle. |
| 9 | Atomic writes; preserve CRLF/LF + BOM | **Applies (important)** | Write via **temp file + rename**, not in-place. **Preserve CRLF** — the repo is CRLF (per MEMORY.md) and `MEMORY.md`/`USER.md` should match. **Do NOT copy `FilesToolHandler`**, which writes non-atomically with `File.WriteAllText`. New entries appended by this tool must use CRLF line endings. |
| 10 | Self-healing arg validation | **Applies (important)** | Detect & correct: (a) both `action` and `operations` present, or neither; (b) `add`/`replace` missing `content`; (c) `replace`/`remove` missing `old_text`; (d) missing/invalid `target`; (e) `operations` items missing `action`. Return a **precise corrective error string** naming the missing field and the expected shape, e.g. `replace requires old_text identifying the existing entry; got content only.` |
| — | `task_id`-keyed per-session state | **N-A (by design)** | Memory is deliberately **global / cross-session** — the least task-scoped tool in the set. There is no per-task read-dedup cache, cwd, or process registry to key. Note that Pia **does not thread a task/session id into `IPluginToolHandler` handlers today** (`HandleToolCallAsync(FunctionCallContent)` only), so even if wanted it is not available — and it is not needed here. |

**Atomic-batch application algorithm (the core logic, from invariant #2 of the spec + §1.5):**

1. Load current entries for `target` (parse `MEMORY.md`/`USER.md` into a list of entries; entries are
   delimited consistently, e.g. one fact per Markdown bullet/line).
2. Apply all ops **in memory** in order: `add` appends; `replace` finds the **unique** entry matching
   `old_text` (else error, abort batch — no partial write); `remove` likewise.
3. Serialize the resulting store; compute its char length.
4. **Check the budget on the FINAL result only.** If over `MaxStoreChars`:
   - lone `add` → reject + echo current (pre-batch) entries;
   - batch → reject with a message telling the agent the final size and the cap, echoing current
     entries so it can shrink more.
5. If within budget, **atomically write** (temp + rename, CRLF preserved), verify, return a count
   summary.

---

## 4. Build / implementation checklist

- [ ] **Decide Open Question #1** (coexist vs replace; default-enabled or feature-flagged). Plan
      assumes **coexist, off-by-default, distinct plugin name `agent-memory`, tool name `memory`**.
- [ ] **Decide Open Question #2** (silent proactive write vs `ActionCard` confirm).
- [ ] `IAgentMemoryStore` + `AgentMemoryStore`: locate `MEMORY.md`/`USER.md` under
      `%LOCALAPPDATA%\Pia`; parse/serialize entries; atomic temp+rename write with CRLF; per-store
      lock; `MaxStoreChars` constant.
- [ ] `IAgentMemoryToolHandler` + `AgentMemoryToolHandler`:
  - [ ] `GetTools()` returns the single `memory` tool with the **verbatim** schema/description (§1.2).
  - [ ] `HandleToolCallAsync` parses single-op **and** `operations[]` shapes; self-healing validation
        (§3 #10); routes to the atomic-batch algorithm (§3).
  - [ ] Overflow → reject + echo current entries (§1.4).
  - [ ] `old_text` unique-substring match; ambiguity/no-match → error (§1.4).
  - [ ] `IsAvailable` gate; `BuildSnapshotForSystemPrompt()`.
  - [ ] (If confirmed-write per OQ#2) return `AgentMemoryToolCall` pending action; else write silently
        and return the summary directly.
  - [ ] Privacy logging via `SensitiveDebug` only for content (§2.6).
- [ ] `BuiltInPluginHandler.FromAgentMemoryHandler(...)` factory (mirror `FromFilesHandler`).
- [ ] `BuiltInPluginDefaults`: GUID `…07`, `PreloadedPluginIds`, `Defaults` entry (Name
      `agent-memory`).
- [ ] `PluginService`: ctor param + `"agent-memory"` switch arm in `InitializeBuiltInPlugins()`.
- [ ] `Bootstrapper.cs`: DI singletons for store + handler.
- [ ] **Snapshot injection hook** (§2.5, approach A): snapshot computed once at session start, cached
      on `ChatSession`, appended by `AssistantPromptComposer.PrepareTurn` as a `## Memory` section.
- [ ] (If confirmed-write) extend `ActionCardCategory` usage — reuse `ActionCardCategory.Memory` or
      add a value; map via `ActionCardBuilder`.

---

## 5. Test strategy (matches the repo)

Repo uses **xunit.v3** with plain `Xunit.Assert` (no FluentAssertions; MTP via `global.json`).
New fixture `.cs`/`.md` files must be **CRLF** (Write tool emits LF — convert).

Target the **pure logic** (store parse/serialize, batch application, budget check, matching, arg
validation) — keep it free of WPF/DI so it runs as straight unit tests, the way
`AssistantPromptComposer`'s pure helpers are tested.

| Test | Asserts |
|------|---------|
| Batch frees room then adds | A batch that `remove`s enough then `add`s a large entry **passes** the final-result char check even though the `add` alone would overflow. |
| Lone `add` overflow | Over-budget lone `add` is **rejected** and the result **echoes the current entries** unchanged; store on disk is untouched. |
| `old_text` ambiguity | `old_text` matching ≥2 entries → error, batch aborts, **no partial write**. |
| `old_text` no match | `old_text` matching 0 entries → error. |
| `old_text` unique | Exactly-1 match → `replace`/`remove` applied. |
| Single-op vs `operations[]` healing | (a) both shapes present → corrective error; (b) `add` missing `content` → corrective error; (c) `replace`/`remove` missing `old_text` → corrective error; (d) missing/invalid `target` → corrective error. |
| Atomic + CRLF | After a write, the file is rewritten via temp+rename, new entries use **CRLF**, and a re-read returns the expected entries (write-persisted verification, invariant #6/#9). |
| Snapshot frozen mid-session | A write during a session does **not** change that session's injected snapshot; a fresh snapshot reflects it (invariant #6). Test at the handler/store seam (`BuildSnapshotForSystemPrompt` before vs after a write within one cached snapshot). |
| Target routing | `target: "user"` writes `USER.md`, `target: "memory"` writes `MEMORY.md`; stores are independent. |
| Schema fidelity | `GetTools()` emits a single tool named `memory` whose description equals the spec string byte-for-byte. |

---

## 6. Open questions

1. **Coexistence vs replacement (sharpest fork).** Recommended: **coexist** as a separate
   `agent-memory` plugin (tool name `memory`), **off by default**, gated to a coding/workspace
   posture so the model never sees two memory subsystems at once. Alternatives: (a) replace
   `MemoryService` (rewrite — out of scope; regresses `@memory`); (b) leave always-on alongside
   (risks the model confusing the two). Maintainer's call.
2. **Silent proactive write vs `ActionCard` confirmation.** The spec says "save **proactively**"
   (implies silent writes); Pia convention routes every memory write through an `ActionCard`. Plain
   reading leans **silent** (blast radius is low — a write only takes effect at the *next* session's
   snapshot, never mid-session). But privacy-first tension argues for visibility. Options:
   (a) silent write + a lightweight in-app notification/snackbar; (b) reuse
   `ActionCardCategory.Memory` confirmation (consistent with todo/reminder/files). Recommend
   **(a)** to honor the spec's proactive intent while keeping the user informed.
3. **Store location & format details.** Confirm `%LOCALAPPDATA%\Pia\MEMORY.md` /
   `%LOCALAPPDATA%\Pia\USER.md` (vs a `Memory\` subfolder). Confirm the **entry delimiter** (one fact
   per bullet line vs blank-line-separated paragraphs) — this fixes how `old_text` matching scopes an
   "entry".
4. **`MaxStoreChars` value.** Proposed 4 000–8 000 chars per store. Needs tuning against the
   prompt-budget impact (injected on every turn forever).
5. **Snapshot seam.** Confirm approach (A) (cache on `ChatSession`, append in `PrepareTurn`) vs
   threading the snapshot through `AssistantTurnSetup`. Affects how background/multi-window sessions
   each freeze their snapshot.
6. **`@`-command / discoverability.** Should there be an `@agent-memory` (or no) `@`-command? Today
   `@memory` maps to the SQLite tools in `AssistantPromptComposer.GetAtCommandToolMapping`; adding a
   second domain there is optional and orthogonal to the core tool.
```
