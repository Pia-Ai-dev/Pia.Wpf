# Plan — `delegate_task` (REUSE / modification instructions)

> **Classification:** `reuse`. Pia's `ChatSessionManager` + `ChatSession` + `AiClientService` already
> supply the load-bearing substrate `delegate_task` needs (per-session PII isolation, concurrent
> background turns, a session registry with a soft-cap reaper, a bounded multi-round tool loop). What is
> missing is a **delegation runner** layered on top. This is new code that *builds directly on* existing
> primitives — not an absent equivalent. Delegation is intrinsic to Pia's own session model; an MCP
> server cannot provide it, so the implementation is native by necessity (see §7, cross-cutting #3).
>
> **Scope of this doc:** `delegate_task` only. It is PLANNING — it describes changes precisely enough to
> implement, but writes no C#. Decisions it cannot make alone are surfaced in §8 (Open Questions).
>
> **Headline architectural call (made here, see §3.1):** children **bypass `ChatSession`** and run on a
> new lower-layer runner over `AiClientService.GetChatCompletionWithToolsAsync` with a per-child
> `ITokenMapService` and a **new child-specific tool-handler callback**. The detached-`ChatSession` route
> is documented as a fallback (§3.2) for the day the product wants child transcripts persisted/browsable.

---

## 1. The exact tool contract (from `delegate_task.md`)

### 1.1 Schema (abridged; dynamic parts noted)

`delegate_task` accepts **two shapes**:

- **Single:** `goal` (required) `[+ context, toolsets, role]`.
- **Batch:** `tasks: [{goal (required), context?, toolsets?, role?}, ...]` run **in parallel**.

| Field | Type | Meaning |
|-------|------|---------|
| `goal` | string (req in single) | What the subagent should accomplish. Self-contained — child sees no parent history. |
| `context` | string | Background: file paths, errors, project structure, constraints. |
| `toolsets` | string[] | Toolsets to enable for the child. **Default: inherit parent's enabled toolsets**; may narrow. |
| `tasks` | object[] | Batch/parallel mode. Each item: `{goal (req), context?, toolsets?, role?}`. |
| `role` | enum `leaf`\|`orchestrator` | `leaf` (default): child cannot delegate further. `orchestrator`: may spawn grandchildren up to `max_spawn_depth`. |

The **description string is rebuilt per `definitions()` call** to reflect the user's live
`max_concurrent_children` (default ~3) and `max_spawn_depth`.

### 1.2 Behavior contract (5 invariants)

1. **Isolation.** Each child = fresh context (no parent transcript), own `task_id`, own
   terminal/cwd/background-process registry. **Parent blocks** until children finish, then receives their
   final outputs.
2. **Self-contained goals.** Child sees only `goal` + `context`. The schema forces standalone briefs.
3. **Toolset restriction.** Default inherit parent's; can narrow per task. **Always strip
   dangerous-in-children tools:** `delegate_task` (unless `role=orchestrator`), `clarify`, `memory`,
   `send_message`/messaging, `execute_code`.
4. **Roles.** `leaf` (default) cannot delegate → bounds the tree. `orchestrator` may spawn grandchildren
   up to `max_spawn_depth`.
5. **Concurrency cap.** Enforce `max_concurrent_children`; queue or reject beyond it with a clear error.

### 1.3 Implementation checklist (from spec)

- [ ] Single + batch shapes; per-task `goal` required.
- [ ] Fresh `task_id` + isolated state per child; parent blocks then collects results.
- [ ] Toolset inherit/restrict; strip `delegate_task`(non-orch)/`clarify`/`memory`/`execute_code`/messaging.
- [ ] `leaf`/`orchestrator` roles + `max_spawn_depth`; `max_concurrent_children` cap.
- [ ] Dynamic description reflecting live limits.

---

## 2. What already exists (verified against the codebase)

All paths below were read and verified. Citations are exact.

| Concern | Where it lives today | What it does |
|---------|----------------------|--------------|
| **Per-session PII isolation** | `ChatSessionManager.CreateSession()` → `_tokenMapFactory()` (`ChatSessionManager.cs:96-121`); `ChatSession.TokenMap` (`ChatSession.cs:44`); `TokenMapAmbient.Current` set in `RunTurnAsync` (`ChatSession.cs:201-202`) | Each session/turn gets its own `ITokenMapService`; the ambient `AsyncLocal` flows down each `await` so concurrent turns never share a PII namespace. **Directly reusable per child.** |
| **Bounded multi-round tool loop** | `AiClientService.GetChatCompletionWithToolsAsync(...)` (`AiClientService.cs:120-364`) | UI-agnostic `IAsyncEnumerable<ChatStreamItem>`; `maxToolRounds = 10` (`:160`); invokes a `Func<FunctionCallContent, Task<object?>>? toolHandler` per call (`:341`); aggregates usage; throws `LlmTimeoutException`/`LlmTruncatedException`. **This is the reuse target for the child runner.** |
| **Tool routing** | `PluginService.RouteToolCallAsync(FunctionCallContent, ct)` (`PluginService.cs:265-284`); `_toolNameRoutes` (`:29`) | Routes a tool name → handler; returns `(Result, PluginToolCall? PendingAction)`. **Reusable inside the child tool-handler.** |
| **Tool list aggregation** | `PluginService.GetAllTools()` (`PluginService.cs:222-243`) | Flat `IList<AITool>` from all enabled handlers. **Reuse, then subtractively filter (see §4).** |
| **Session registry + soft-cap reaper** | `ChatSessionManager._sessions` / `_allSessions` (`:40-41`); `ReapStaleSessions()` / `MaxRetainedSessions=8` (`:52,204-242`) | Keeps N most-recently-active sessions; reaps non-active Idle/Error. **A memory soft-cap — NOT the delegation concurrency gate (see §5).** |
| **Concurrent background turns** | `ChatSession.RunTurnAsync` + `IsSessionActive` probe (`ChatSession.cs:175-394`); `IBackgroundChatNotifier` routing (`ChatSessionManager.cs:153-162`) | Background turns run independently and notify the user on terminal/WaitingForTool states. Establishes that **concurrent isolated turns already work**; the child runner reuses the isolation pattern, not this UI-affine driver verbatim. |
| **Persona-driven turn prep + tool gating** | `AssistantPromptComposer.PrepareTurn(...)` (`AssistantPromptComposer.cs:26-59`) | Builds the system prompt and resolves tools. Supports **all-tools** or an **allow-list** from `@`-commands (`GetAllowedToolNames`, `:188-197`). **No deny-list / subtractive filter exists.** |
| **Write-op confirmation** | `ChatSession.HandleToolCall(...)` (`ChatSession.cs:404-487`); `ActionCardInfo.WaitForUserDecisionAsync()`; `ChatState.WaitingForTool` (`ChatState.cs:16`) | Write ops (`pendingAction != null`) build an inline `ActionCardInfo`, flip to `WaitingForTool`, and **block the turn on a user click**. **A child has no user watching it — this path must NOT be reused for children (see §4 step 5).** |
| **Tool-handler contract** | `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, ct)` (`IPluginToolHandler.cs:19-20`) | Receives **only** `FunctionCallContent` + `CancellationToken`. **No `task_id`/session id is threaded (see §4 step 6, cross-cutting #4).** |
| **Privacy logging helpers** | `Pia.Logging` (`SensitiveDebug`, `SafeUrl`); usage e.g. `ChatSessionManager.cs:283,506`; `ChatSession.cs:425-428` | `[Conditional("DEBUG")]` sensitive logging + URL redaction. **The runner must comply (see §4 step 8).** |
| **Config precedent** | `AppSettings.AssistantFilesFolder` + `SettingsChanged` event (referenced in capability map) | Pattern for adding `max_concurrent_children` / `max_spawn_depth` settings (§4 step 7). |

---

## 3. The architectural fork (decided)

### 3.1 DECISION — children bypass `ChatSession`, run on a new lower-layer runner

**Discriminating criterion:** *do children need to be persisted, resumable, or browsable in history?*
The spec's whole point is **context hygiene** — *"only the distilled result returns to the parent"*
(`delegate_task.md` §"Why it matters"). Children are therefore **ephemeral**: their verbose intermediate
work must NOT leak into the parent context or into the persisted chat store. So the runner should reuse
the layer **below** `ChatSession`:

- `AiClientService.GetChatCompletionWithToolsAsync(...)` — the UI-agnostic tool loop.
- A fresh per-child `ITokenMapService` from the existing `_tokenMapFactory` — per-child PII isolation.
- `PluginService.RouteToolCallAsync(...)` — tool execution.
- A **new child-specific `toolHandler` callback** (the `Func<FunctionCallContent, Task<object?>>` argument)
  that NEVER builds action cards, applies the deny-list, and enforces the headless write-op policy.

**Why this is right (and dissolves three KNOWN GAPS at once):**

`ChatSession.RunTurnAsync` is the wrong target because it is built for the foreground UX, not headless
fan-out:

- It is **UI-thread-affine** — no `Task.Run`, no `ConfigureAwait(false)` by design (`ChatSession.cs:22-26`).
- It streams into an `ObservableCollection<AssistantMessage>` and raises UI events
  (`StateChanged`/`TurnCompleted`/`RunFailed`).
- For write ops it **blocks on `ActionCardInfo.WaitForUserDecisionAsync()`** (`ChatSession.cs:441-457`) —
  a child has no user watching it.

Bypassing it makes three gaps disappear because they become properties of the new child `toolHandler`:

1. **No headless run-to-completion entry** → the runner *is* that entry; it consumes the async-enumerable
   and concatenates `TextDelta`s into the child's final text.
2. **No action-card owner for headless children** → the child `toolHandler` never creates cards; it applies
   the write-op policy itself (§4 step 5).
3. **No deny-list filtering** → the child `toolHandler` (and the child tool list it is paired with) applies
   the subtractive filter (§4 step 1).

> **Reconciliation note.** The CLASSIFICATION rationale described children as "detached (non-active)
> sessions." That framing is *not wrong* — it correctly identified isolation, the registry, and the reaper
> as reusable substrate. This plan refines it: we reuse the **isolation pattern** (`_tokenMapFactory`,
> `TokenMapAmbient`, the tool loop) but **not the `ChatSession` driver**, because children are ephemeral.
> This is a deliberate narrowing, surfaced here, not a silent flip.

### 3.2 FALLBACK — detached `ChatSession` route (documented, not chosen)

*If* the product later decides child transcripts should be persisted/resumable/browsable in history, the
fallback is to create non-active `ChatSession`s via `ChatSessionManager` and add a new
**run-to-completion** entry alongside `StartTurnAsync` (which is fire-and-forget via `SafeFireAndForget`
and cannot return final text). That route still requires the same deny-list and write-op-policy work, plus
it must suppress/route action cards for an unwatched child — i.e. it inherits all the problems §3.1 avoids.
Defer unless persistence is a confirmed requirement.

---

## 4. Gap analysis + ordered modification instructions

### 4.1 Gap analysis table

| # | Spec requirement | Current behavior | Needed change |
|---|------------------|------------------|---------------|
| G1 | Strip `delegate_task`/`clarify`/`memory`/`execute_code`/messaging from children | `AssistantPromptComposer.PrepareTurn` is allow-list (`@`-commands) or all-tools only — **no subtractive filter** | New deny-list filter over `GetAllTools()` in the child runner (NOT in `PrepareTurn`) |
| G2 | Fresh `task_id` per child, threaded into handlers | `IPluginToolHandler.HandleToolCallAsync` receives only `FunctionCallContent`+`ct`; no session/task id | Thread `task_id` (= child run `Guid`) into dispatch — a **`tool_registration` host-layer change**; cross-reference, don't implement here |
| G3 | Parent blocks → collects child final text | `StartTurnAsync` is fire-and-forget (`SafeFireAndForget`, `ChatSessionManager.cs:435`); returns no text | New run-to-completion runner that `await`s each child and returns concatenated final text |
| G4 | Headless children must not block on user confirmation | Write ops block on `ActionCardInfo` (`ChatSession.cs:441-457`) | Child `toolHandler` applies a **headless write-op policy** (strip-writes or auto-decline) — see step 5 |
| G5 | `max_concurrent_children` cap; queue/reject with clear error | Only `MaxRetainedSessions` reaper exists (a memory cap, not a concurrency gate) | New `SemaphoreSlim`-style gate distinct from the reaper |
| G6 | `leaf`/`orchestrator` roles + `max_spawn_depth` | No leaf/orchestrator notion; no recursion-depth bound | New role + depth state, threaded with `task_id`; leaf children never see `delegate_task` |
| G7 | Dynamic description reflecting live limits | Tools built via `AIFunctionFactory` with static `[Description]`; no per-call rebuild | New dynamic-schema mechanism + settings for the limits (depends on `tool_registration` §2.3) |
| G8 | Named `toolsets` (e.g. `terminal`,`file`,`web`) | Pia exposes flat per-plugin `AITool` lists; **no toolset grouping** | New toolset→tool-name mapping (a small static table, like `GetAtCommandToolMapping`) |
| G9 | Privacy-first logging | n/a (new code) | Runner must use `SensitiveDebug`/`SafeUrl` for goals/context/results |

### 4.2 Ordered modification instructions

> All steps below describe **new code alongside** existing primitives. No existing tool's behavior should
> change except the additive `task_id` threading in G2 (owned by the `tool_registration` plan).

**Step 1 — Add a deny-list / toolset filter (G1, G8).**
Build the child's tool list by starting from `PluginService.GetAllTools()` and applying, in order:
(a) a **subtractive deny-list** that always removes `delegate_task` (unless `role==orchestrator`),
`clarify`, `memory` tools, `execute_code`, and messaging tools; (b) an optional **toolset narrowing**
from the call's `toolsets` (or parent's enabled toolsets when omitted). Implement the toolset→tool-name
mapping as a small static table mirroring `AssistantPromptComposer.GetAtCommandToolMapping`
(`AssistantPromptComposer.cs:166-186`) — e.g. `file` → `read_file`,`write_file`,`list_files`,`delete_file`.
Put this filter **in the child runner**, not in `PrepareTurn` (which is persona/@-command-coupled and
would regress foreground turns). Deny-list precedes toolset narrowing so a `toolsets` value can never
re-admit a stripped tool.

**Step 2 — Add a child run-to-completion runner (G3).**
Introduce a new delegation runner (e.g. a `DelegationService`/`IDelegationService` registered in
`Bootstrapper`) with a method that takes `(goal, context, toolsets, role, depth, parentTaskId)`, returns
the child's distilled final text, and internally:
- Creates a fresh `ITokenMapService` via the injected `_tokenMapFactory` and initializes it.
- Sets `TokenMapAmbient.Current` for the child's logical flow (mirror `ChatSession.cs:201-202,360-361`),
  restoring the previous value in a `finally`.
- Builds a **minimal message list**: a child system prompt (a focused brief derived from `goal`+`context`,
  with NO parent transcript) + one user message. It does **not** reuse `PrepareTurn`'s persona tool-selection
  tree; it composes a leaner brief and pairs it with the filtered tool list from Step 1.
- Calls `GetChatCompletionWithToolsAsync(childMessages, provider, filteredTools, childToolHandler, mode, ct)`
  and consumes the `IAsyncEnumerable`, concatenating `TextDelta.Text` into a buffer; on `Finished`, captures
  usage if useful.
- Returns the buffered text (final-pass `TokenMap.Detokenize` if tokenization is enabled, matching
  `ChatSession.cs:356-357`).

**Step 3 — Threading inversion: children run OFF the UI thread (interacts with §5).**
Unlike `ChatSession.RunTurnAsync`, the child runner SHOULD use `Task.Run` + `ConfigureAwait(false)`: there
is no UI binding to preserve, and **batch mode needs true concurrency**. The parent's `delegate_task`
tool-handler `await`s the children; that await yields the UI thread, so the parent's own (UI-affine)
`RunTurnAsync` does not deadlock while children run. **State this explicitly in code comments** — it is the
deliberate opposite of the documented `ChatSession` UI-affinity rule.

**Step 4 — Concurrency gate (G5).**
Add a `SemaphoreSlim`-style gate sized by `max_concurrent_children` inside the runner (single instance for
the window/process, per chosen scope). Batch mode acquires per child; on cap, **queue** (await the
semaphore) by default, OR **reject** the overflow with a clear, model-readable error string if the product
prefers fail-fast. **Do NOT conflate with `ReapStaleSessions`/`MaxRetainedSessions`** — that is a session
memory soft-cap, unrelated to in-flight child concurrency.

**Step 5 — Headless write-op policy (G4). [DECISION REQUIRED — see §8]**
The spec's strip-list omits `file`/`todo`/`reminder` **write** tools, which in Pia require
`ActionCardInfo.WaitForUserDecisionAsync()` confirmation (`ChatSession.cs:435-483`). A detached child has
no user watching. The child `toolHandler` must therefore NOT mirror `ChatSession.HandleToolCall`'s
card-build-and-block branch. Instead, when `RouteToolCallAsync` returns a non-null `PendingAction`, choose
one policy (recommend **auto-decline** or **strip all write tools** for a privacy-first default;
**auto-approve is the unsafe option** and should not be the default):
- **Strip writes:** Step 1's deny-list also removes all write tools for children → `PendingAction` never
  occurs. Simplest, most conservative.
- **Auto-decline:** keep write tools visible but, on `PendingAction`, return the same declined-style string
  `ChatSession.cs:483` returns (so the child model adapts) without executing.
This is the headline open question (§8 Q1).

**Step 6 — `task_id` threading (G2). [Owned by `tool_registration` — cross-reference only]**
This tool is **where child `task_id` originates** (each child gets its own). Recommend `task_id` = the
child run's `Guid`. The actual signature change to `IPluginToolHandler.HandleToolCallAsync` (today
`FunctionCallContent` + `CancellationToken`) and to `PluginService.RouteToolCallAsync` belongs in the
**`tool_registration` host-layer plan** — `delegate_task` is a *consumer* that must pass a child `task_id`
into dispatch. State the dependency; do not design the signature here. (The spec stresses implementing
`task_id` day one because retrofitting is painful — flag that the host layer must land this before/with
`delegate_task`.)

**Step 7 — Roles, depth bound, and live limits (G6, G7).**
- Add `max_concurrent_children` and `max_spawn_depth` as `AppSettings` fields (mirror the
  `AssistantFilesFolder` precedent + `SettingsChanged`), defaulting to ~3 and a small depth (e.g. 2).
- Thread `role` and a `depth` counter alongside `task_id`. A `leaf` child gets `delegate_task` stripped
  (Step 1). An `orchestrator` child keeps `delegate_task` **only while `depth < max_spawn_depth`**; at the
  bound it is stripped too. Reject/clear-error on attempts beyond the bound.
- **Dynamic description (G7):** the spec wants the schema description rebuilt per `definitions()` call to
  show live `max_concurrent_children`/`max_spawn_depth`. Pia builds tools via `AIFunctionFactory` with
  static `[Description]` — **no rebuild mechanism exists**. This depends on the `tool_registration` plan's
  `dynamic_schema_overrides` (`tool_registration.md` §2 "Definition generation", item 3 "Dynamic
  overrides"). Until that lands, ship a **static** description
  noting "limits configurable in settings"; cross-reference the host-layer plan for the dynamic upgrade.

**Step 8 — Privacy-logging compliance (G9).**
`goal`, `context`, child results, and tool args/results are **payloads** per `CLAUDE.md` → must use
`_logger.SensitiveDebug(...)`, never plain `LogInformation`. Log child **counts**, `task_id` (Guid), role,
depth, and durations at `Info`; the goal/context/result text only via `SensitiveDebug`. Any URL goes
through `SafeUrl.Format`. Mirror the existing discipline at `ChatSession.cs:425-428` and
`ChatSessionManager.cs:283,506,237-239`.

**Step 9 — Register the `delegate_task` tool + its handler.**
Expose `delegate_task` as a tool (single + batch shapes; per-task `goal` required) whose handler invokes
the runner (Step 2), enforces the concurrency gate (Step 4), runs batch items concurrently (Step 3), and
returns the **distilled per-child results** concatenated into one tool result for the parent. Apply
**self-healing arg validation** (per `overview.md` cross-cutting #10): detect a missing `goal`, a
`tasks` array of dict-shaped items missing `goal`, etc., and return a precise corrective error rather than
throwing. Registration wiring (Bootstrapper/DI) is implementation, not this doc.

---

## 5. Regression / interaction risks

| Risk | Detail | Mitigation |
|------|--------|------------|
| **Parent provider timeout bounds child runtime** | The `delegate_task` handler is awaited *inside* `GetChatCompletionWithToolsAsync`, whose `timeoutCts` uses `provider.TimeoutSeconds` (**default 300s**, `AiClientService.cs:136`). Long-running children → `LlmTimeoutException` kills the **parent** turn. | Bound child wall-clock well under the parent timeout; consider a separate child timeout and a "partial result" return on child timeout; document the ceiling. |
| **Parent tool-round ceiling** | The parent loop is `maxToolRounds = 10` (`AiClientService.cs:160`). A parent that delegates across many rounds can exhaust it. | Encourage batch mode (one `delegate_task` call → N children) over many sequential single calls. |
| **UI-affinity inversion bug** | If the runner forgets `Task.Run`/`ConfigureAwait(false)`, child work re-enters the UI `SynchronizationContext` and serializes batch children (no real parallelism) and can starve the UI. | Enforce off-UI execution in the runner (Step 3); cover with a test asserting concurrency. |
| **Sandbox/UX regression via `PrepareTurn`** | Putting the deny-list in `PrepareTurn` would alter foreground/`@`-command tool selection. | Keep the filter in the child runner only (Step 1); leave `PrepareTurn` untouched. |
| **Action-card leakage** | If a child reuses `ChatSession.HandleToolCall`, an unwatched child blocks forever in `WaitingForTool`. | §3.1 bypass + Step 5 policy guarantee children never build/await cards. |
| **PII cross-talk** | Sharing a token map across concurrent children would mix namespaces. | Per-child `_tokenMapFactory()` + per-flow `TokenMapAmbient` (Step 2), exactly as `ChatSession` isolates turns. |
| **Reaper confusion** | Treating `max_concurrent_children` as the reaper would silently drop in-flight children. | Keep the concurrency gate separate (Step 4); the reaper never participates. |
| **`task_id` retrofit pain** | Shipping `delegate_task` before the host-layer threads `task_id` means per-child state (cwd, process registry, read-dedup) collides across concurrent children. | Land `tool_registration`'s `task_id` threading **before/with** this tool (Step 6). |

---

## 6. Settled cross-cutting question (native vs MCP)

**Cross-cutting #3 — native vs MCP: SETTLED, native by necessity.** An external shell/filesystem MCP
server can run commands, but it **cannot spawn a Pia subagent** — a child needs Pia's own provider/persona
resolution, Pia's tool registry, and a Pia per-session `ITokenMapService` for PII isolation. Delegation is
intrinsic to Pia's session model (`ChatSessionManager` + `AiClientService`), so `delegate_task` is built
natively. MCP remains the right answer for *individual* capability tools (e.g. a filesystem server backing
`search_files`), but not for the delegation orchestrator itself.

The other cross-cutting questions are out of scope for `delegate_task` and are deferred to their own plans:
- **#1 code-exec security model** → `execute_code`/`terminal` plans. Touches `delegate_task` only via
  stripping `execute_code` from children (Step 1) and the headless write policy (Step 5).
- **#2 filesystem scope (sandbox vs workspace root)** → `read_file`/`write_file`/`search_files` plans.
- **#4 `task_id` threading** → `tool_registration` plan (consumed here, Step 6).
- **#5 extend vs rebuild `FilesToolHandler`** → file-tool plans.
- **#6 Python runtime for `execute_code`** → `execute_code` plan.

---

## 7. Build/verify checklist (planning-level)

- [ ] Deny-list + toolset filter over `GetAllTools()`, in the runner (not `PrepareTurn`).
- [ ] Child runner: per-child token map, lean brief, `GetChatCompletionWithToolsAsync`, text capture.
- [ ] Off-UI execution (`Task.Run`/`ConfigureAwait(false)`) for true batch concurrency.
- [ ] `SemaphoreSlim` concurrency gate (`max_concurrent_children`), distinct from the reaper.
- [ ] Headless write-op policy decided (§8 Q1) and enforced in the child `toolHandler`.
- [ ] `role`/`depth`/`task_id` threaded; leaf strips `delegate_task`; orchestrator bounded by `max_spawn_depth`.
- [ ] `AppSettings` fields for the two limits; static description now, dynamic later (host-layer dep).
- [ ] `SensitiveDebug`/`SafeUrl` for all payloads; counts/ids/durations at `Info`.
- [ ] Self-healing arg validation for single + batch shapes.
- [ ] Tests: PII isolation across concurrent children; concurrency cap rejects/queues; child never blocks
      on an action card; deny-list strips the five tool families; depth bound stops recursion.

---

## 8. Open questions

1. **Headless write-tool policy (BLOCKING, §4 step 5).** For a privacy-first assistant with no user
   watching a detached child, do we **strip all write tools** from children (simplest, most conservative)
   or **keep them visible and auto-decline** on `PendingAction`? Auto-approve is unsafe and should not be
   the default. This drives Step 1's deny-list contents and the child `toolHandler` shape.
2. **`ChatSession` bypass vs detached `ChatSession` (decided §3.1, revisit if persistence wanted).** This
   plan bypasses `ChatSession` because children are ephemeral. If the product wants child transcripts
   persisted/resumable/browsable, switch to the §3.2 fallback (which reintroduces the action-card and
   run-to-completion problems). Confirm children are not meant to appear in history.
3. **Concurrency-cap-overflow behavior.** Queue (await the semaphore) vs reject-with-error. Spec allows
   either ("queue or reject … with a clear error"). Pick one default.
4. **`task_id` host-layer sequencing.** Confirm `tool_registration`'s `task_id` threading lands
   before/with `delegate_task`, or accept that early children share collidable per-task state.
5. **Provider for children.** Inherit the parent turn's resolved provider, or resolve a (cheaper)
   dedicated child provider? Affects the §5 timeout ceiling and cost.
6. **Dynamic description timing.** Ship a static description first (limits "configurable in settings") and
   upgrade to a live-rebuilt description once `tool_registration`'s `dynamic_schema_overrides` exists?
7. **Scope of the concurrency gate & settings.** Per assistant window (like `ChatSessionManager`) or
   per process? Determines where the `SemaphoreSlim` and the `AppSettings` limits are read.
