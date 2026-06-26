# Implementation Plan — `process` tool (Pia.Wpf)

> **Status:** FROM-SCRATCH build. `bucket = scratch`.
> **Scope of this doc:** planning only. No C#/.csproj/.xaml changes are proposed for *immediate* application —
> this describes what to build, where, and in what order.
> **Spec source:** [`../process.md`](../process.md) (+ cross-cutting rules in [`../overview.md`](../overview.md) and
> [`../tool_registration.md`](../tool_registration.md)).

## 0. TL;DR / classification rationale

`process` is the **lifecycle controller** for background commands launched by `terminal(background=true)`. It
**cannot exist on its own** — it operates on `session_id`s that `terminal` produces. None of the required
substrate exists in Pia today:

| Required substrate | Exists in Pia? | Evidence |
|---|---|---|
| Background-process registry (`session_id -> {pid, buffer, status, stdin, …}`) | **No** | No type in `src/Pia.Wpf/Services` matches; `McpPluginToolHandler` spawns a stdio process via `StdioClientTransport` but hides the `Process`/streams entirely. |
| A terminal launch path producing `session_id` | **No** | Only `Process.Start` for browser/`expand.exe`/`where.exe`/`node --version` exists (`PluginService`, `CabManagerService`, `AuthService`). None is a tracked, buffered, long-lived launch. |
| Interactive stdin (write/submit/close) | **No** | `StdioClientTransport` owns stdin internally; not exposed. |
| Rolling output buffer | **No** | — |
| Tree kill | **No** | — |
| `task_id` threading into handlers | **No** | `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, CancellationToken)` and `PluginService.RouteToolCallAsync(toolCall)` carry **no** session/task id. `ChatSession.Id` exists but is never passed down (`ChatSession.HandleToolCall` → `_pluginService.RouteToolCallAsync(toolCall)`). |

Therefore: build new, native. Do **not** try to deliver this via the MCP plugin path (see §7, Q3).

**Hard dependency:** this plan assumes a sibling `terminal.plan.md` that (a) introduces the shared
`IBackgroundProcessRegistry` and (b) launches background processes into it. `process` is the *consumer*. The
registry is the shared spine; build it once, inject it into both handlers.

---

## 1. Tool contract (restated from the spec)

### 1.1 Schema (exact — copy `description` verbatim)

```json
{
  "name": "process",
  "description": "Manage background processes started with terminal(background=true). Actions: 'list' (show all), 'poll' (check status + new output), 'log' (full output with pagination), 'wait' (block until done or timeout), 'kill' (terminate), 'write' (send raw stdin data without newline), 'submit' (send data + Enter, for answering prompts), 'close' (close stdin/send EOF).",
  "parameters": {
    "type": "object",
    "properties": {
      "action":     {"type": "string", "enum": ["list","poll","log","wait","kill","write","submit","close"], "description": "Action to perform on background processes"},
      "session_id": {"type": "string", "description": "Process session ID (from terminal background output). Required for all actions except 'list'."},
      "data":       {"type": "string", "description": "Text to send to process stdin (for 'write' and 'submit' actions)"},
      "timeout":    {"type": "integer", "minimum": 1, "description": "Max seconds to block for 'wait'. Returns partial output on timeout."},
      "offset":     {"type": "integer", "description": "Line offset for 'log' action (default: last 200 lines)"},
      "limit":      {"type": "integer", "minimum": 1, "description": "Max lines to return for 'log' action"}
    },
    "required": ["action"]
  }
}
```

### 1.2 Action semantics

| action | requires | blocking? | consumes completion? | behavior |
|--------|----------|-----------|----------------------|----------|
| `list` | — | no | no | All running + recently-finished processes **for this `task_id`**: `session_id`, status, uptime, `exit_code`, watch metadata. |
| `poll` | `session_id` | **no** | **no** (read-only — must NOT suppress host's completion watcher) | status + new-output preview (tail ~1000 chars) + `exit_code` if exited. |
| `log` | `session_id` | no | **yes** | full buffered output, `offset`/`limit` paginated, returns `total_lines`. |
| `wait` | `session_id` | **yes** | **yes** | block until exit / `timeout` / interrupt; return full output (capped). Partial output on timeout. |
| `kill` | `session_id` | no | — | terminate the process **tree** (SIGTERM → ~2s grace → SIGKILL; Windows `taskkill /T /F`). |
| `write` | `session_id`, `data` | no | — | write raw bytes to stdin, **no** trailing newline. |
| `submit` | `session_id`, `data` | no | — | write `data` + Enter — answers an interactive prompt. |
| `close` | `session_id` | no | — | close stdin / send EOF; do **not** kill. |

### 1.3 Return shape

A single string (Pia tool handlers return `object?` that becomes the function-result string). Recommended:
human/JSON-ish text the model can parse, e.g.

- `list` → table/lines: `session_id  status  uptime  exit_code  watch`.
- `poll` → `status=running, new_output:\n<tail>` or `status=exited, exit_code=0, new_output:\n<tail>`.
- `log` → `total_lines=N\n<lines offset..offset+limit>` with a truncation marker.
- `wait` → final status + exit_code + full (capped, head+tail) output.
- `kill`/`write`/`submit`/`close` → short confirmation or precise error.

### 1.4 Required invariants (from `process.md`)

1. **Rolling buffer, bounded** ~200 KB; evict oldest; ANSI stripped before store; secrets redacted. `log`
   paginates over it, `poll` previews the tail.
2. **Notification consumption rules:** `poll` read-only/non-consuming; `wait`+`log` consume. (See §6 — Pia has
   no autonomous completion watcher yet, so this is partially aspirational; documented as a fidelity gap.)
3. **Watch rate limiting** (mirrors `terminal`): 1 notification / 15 s / process; 3 dropped windows →
   auto-disable + promote to notify-on-complete; global circuit breaker across all processes.
4. **Tree kill** — never orphan children.
5. **Crash recovery (optional):** persist registry with PID + kernel-start-time validation.
6. **stdin availability:** local Popen/PTY = full write/submit/close. Surface clearly when a backend can't.
7. **Arg coercion:** some models send `session_id` as integer → coerce to string. Missing `session_id` on any
   non-`list` action → precise corrective error.

---

## 2. Placement in Pia.Wpf

### 2.1 New components

| Component | Path (proposed) | Role |
|---|---|---|
| `IBackgroundProcessRegistry` | `src/Pia.Wpf/Services/Interfaces/IBackgroundProcessRegistry.cs` | **Shared spine.** `session_id -> ProcessSession`. Owned by terminal.plan.md, consumed here. Singleton. |
| `BackgroundProcessRegistry` | `src/Pia.Wpf/Services/Process/BackgroundProcessRegistry.cs` | Tracks `Process`, rolling buffer, status, stdin writer, watch metadata; tree-kill; `task_id` scoping. |
| `ProcessSession` (record/class) | `src/Pia.Wpf/Services/Process/ProcessSession.cs` | Per-process state (see §3). |
| `RollingOutputBuffer` | `src/Pia.Wpf/Services/Process/RollingOutputBuffer.cs` | ~200 KB ring; ANSI strip + redaction on append; line-indexed for pagination. |
| `IProcessToolHandler` | `src/Pia.Wpf/Services/Interfaces/IProcessToolHandler.cs` | Handler contract (matches the `IFilesToolHandler` shape). |
| `ProcessToolHandler` | `src/Pia.Wpf/Services/ProcessToolHandler.cs` | The model-facing tool: `GetTools()`, `HandleToolCallAsync()`. |

> **Naming note (project convention, CLAUDE.md):** interfaces are `IName`; the task suggested
> `IprocessToolHandler` — that violates PascalCase. Use **`IProcessToolHandler`** / **`ProcessToolHandler`**.

### 2.2 Reusable Pia patterns — what maps and what does NOT

| Pattern (from existing handlers) | Use it here? | Notes |
|---|---|---|
| `GetTools()` via `AIFunctionFactory.Create(schema, name, description)` + private `[Description]` schema method | **Yes** | Single `process` tool. The 8 actions are *one* tool with an `action` enum, not 8 tools — matches the spec schema. |
| Dispatch via `HandleToolCallAsync(FunctionCallContent, ct)` returning `(object? Result, PendingAction?)` | **Yes** | All 8 actions are immediate (no deferred write) → return `(result, null)`. |
| **Pending-action approval guard** (`FilesToolCall`/ActionCard / `WaitForUserDecisionAsync`) | **Mostly NO** | `list/poll/log/wait/write/submit/close` are read/control ops executed immediately — not write-confirm. At most **`kill`** *may* warrant a confirmation card (open question §8). Do **not** contort the lifecycle actions through `ActionCardInfo` just to reuse the pattern. The dangerous-command consent gate belongs at **`terminal`/`execute_code`**, not `process` (see §7 Q1). |
| **Sandbox / `SafeFolderPath.TryResolveInside`** | **N/A** | `process` keys on `session_id`, never a path. No filesystem path is taken from the model here. |
| **Privacy logging** (`SensitiveDebug` / `SafeUrl`) | **Yes, hard requirement** | The rolling buffer holds arbitrary process output (could contain secrets, tokens, file contents, URLs). Log buffer/preview/`data` only via `SensitiveDebug`; never `LogInformation` raw output. Redact secrets *before store* (not just before log). Wrap any URL in `SafeUrl.Format`. |
| **Availability gating** (`IsAvailable` like Files) | **Yes** | Gate the whole coding toolset behind a feature flag / configured workspace (see §7 Q2). `BuiltInPluginHandler` already supports an `isAvailable` lambda (`FromFilesHandler`). |
| `BuiltInPluginHandler.From…` adapter factory | **Yes** | Add `FromProcessHandler(IProcessToolHandler, SyncPlugin)` mirroring `FromFilesHandler`. |
| `BuiltInPluginDefaults` registration + stable plugin GUID | **Yes** | Add a `process`/coding-tools entry; allocate a new GUID in the `10000000-0000-0000-0000-0000000000xx` built-in range. |
| `PluginService.InitializeBuiltInPlugins()` switch arm | **Yes** | Add `"process" => BuiltInPluginHandler.FromProcessHandler(...)`. |
| Settings-change route rebuild (`RebuildToolNameRoutes`) | **Yes (if availability is settings-driven)** | Matches the Files plugin which rebuilds routes on `SettingsChanged`. |

### 2.3 DI wiring (Bootstrapper)

Register, following the singleton pattern the other handlers use:

- `IBackgroundProcessRegistry` → `BackgroundProcessRegistry` **singleton** (state must outlive turns; shared with
  the terminal handler).
- `IProcessToolHandler` → `ProcessToolHandler` **singleton**.
- Inject `IProcessToolHandler` into `PluginService`'s constructor (sibling of `IFilesToolHandler`).
- The registry is also injected into the future terminal handler — single instance, two consumers.

### 2.4 Where it plugs into registration/dispatch

```
LLM tool call "process"
  → AiClientService tool loop (FunctionCallContent)
  → ChatSession.HandleToolCall(toolCall, …)
  → PluginService.RouteToolCallAsync(toolCall)            [route by tool name "process"]
  → BuiltInPluginHandler.FromProcessHandler adapter
  → ProcessToolHandler.HandleToolCallAsync(toolCall, ct)
  → IBackgroundProcessRegistry (the shared state)
```

---

## 3. `task_id` threading — the load-bearing change (propose, do not implement)

The spec is emphatic: implement `task_id` **day one**; retrofitting is painful. For `process` it is not a nicety
— `list` is *defined* as "processes **for the `task_id`**", and the registry is keyed `session_id` **scoped to
`task_id`**. Without it, `list` either leaks processes across concurrent chats or returns nothing.

**Current reality (confirmed):** no id is threaded. `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent,
CancellationToken)` and `PluginService.RouteToolCallAsync(FunctionCallContent)` take no session id, even though
`ChatSession.Id` exists and Pia runs **multiple concurrent background chats**.

**Proposed change (spans the dispatch layer — this is a feature of the plan, not a wart):**

1. Thread a `taskId` (source = `ChatSession.Id.ToString()`, default `"default"`) through:
   - `ChatSession.HandleToolCall` → pass `Id` into the route call.
   - `IPluginService.RouteToolCallAsync(FunctionCallContent, string taskId)`.
   - `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, string taskId, CancellationToken)`.
2. `ProcessToolHandler` keys all registry lookups by `(taskId, session_id)`; `list` filters by `taskId`.
3. **This belongs in the `tool_registration` work and affects every handler signature.** Adding an optional
   parameter (default `"default"`) keeps existing handlers source-compatible while unblocking `process`,
   `terminal`, and `delegate_task`. Cross-reference `tool_registration.plan.md` so this is done once.

> If a full signature change is deferred, a stopgap is an `AsyncLocal<string> CurrentTaskId` set in
> `ChatSession.RunTurnAsync` (Pia already uses `AsyncLocal` for `TokenMapAmbient`). This is inferior (ambient,
> harder to test) but unblocks `process` without touching every signature. Prefer the explicit parameter.

---

## 4. Per-process state (the registry)

`ProcessSession`, keyed `session_id` (`proc_<hex>`), scoped to `task_id`:

| Field | Purpose |
|---|---|
| `SessionId` (`proc_<hex>`) | stable id returned by terminal; coerce int→str on lookup. |
| `TaskId` | owning chat/task; `list` filters on it. |
| `Pid` + `StartTimeUtc` (kernel start time) | detect recycled-PID collisions on crash recovery. |
| `Status` (Running/Exited), `ExitCode`, `StartedAt` | lifecycle. |
| `Buffer` (`RollingOutputBuffer`, ~200 KB) | ANSI-stripped, secret-redacted output; line-indexed for `log` pagination; tail for `poll`. |
| `StdinWriter` | `Process.StandardInput` (or ConPTY handle, phase 2) for write/submit/close. |
| `WatchPatterns` + notification bookkeeping | rate-limit state (1/15 s, 3-strike auto-disable, circuit breaker). |
| `CompletionConsumed` flag | non-consuming `poll` vs consuming `wait`/`log` (see §6). |

`BackgroundProcessRegistry` responsibilities: register on launch (called by terminal handler), lookup, `list` by
task, tree-kill, dispose. Thread-safe (lock-guarded dict, like `PluginService._handlers`). A reader task drains
stdout/stderr into the buffer.

---

## 5. Cross-cutting invariants (from `overview.md`) — applicability

| # | Principle | Applies to `process`? | How |
|---|---|---|---|
| 1 | Line-numbered reads | Indirect | `log` is line-indexed for pagination; output lines aren't `LINE_NUM\|CONTENT` (that's `read_file`). |
| 2 | Fuzzy matching on edits | **N/A** | no edits. |
| 3 | Delta-filtered diagnostics | **N/A** | no syntax check. |
| 4 | Loop/dedup guards | Minor | a tight `poll` loop is possible; rely on watch rate-limit + circuit breaker rather than read-dedup. |
| 5 | Staleness tracking | **N/A** | no files. |
| 6 | Return a diff / verify write | **N/A** | — |
| 7 | **Head+tail truncation** | **Yes** | `wait`/`log` cap output → truncate head+tail (tail carries the error/result), never head-only; clear truncation marker. |
| 8 | **Pagination** | **Yes** | `log` honors `offset`/`limit`; default last 200 lines; return `total_lines`. |
| 9 | Atomic writes / CRLF/BOM | **N/A** for output; **relevant** if persisting registry to disk (§8). |
| 10 | **Self-healing arg validation** | **Yes** | coerce int `session_id`→str; missing `session_id` on non-`list` → precise error; missing `data` on write/submit → precise error; unknown `action` → enumerate valid actions. |

Plus the registry-state list from `overview.md` §"State each tool needs": `process` owns the **background
process registry** entry (`session_id -> {pid, buffer, status, exit_code, …}`), keyed by `task_id`.

---

## 6. Notification-consumption fidelity gap (the subtlest point)

The spec's `poll`(non-consuming) vs `wait`/`log`(consuming) distinction assumes a **host that re-invokes the
model when a background process exits** (an autonomous completion watcher). **Pia has no such plumbing today.**
The tool loop in `AiClientService` is *intra-turn* (≈10 rounds); when a turn ends there is no
"process exited → re-invoke model" path.

Consequences for this plan:

- **Within a turn,** `wait` is the realistic completion mechanism (block up to `timeout`, return output). `poll`
  and `log` work as specified for inspecting state mid-turn.
- **`notify_on_complete` re-entering the model after the turn ends is NEW PLUMBING** — not in scope for the
  first `process` build; it is a `terminal` + host-loop concern. Track as open question §8.
- **Do NOT silently route completion through `BackgroundChatNotificationSurface`.** That surface drives
  *user-facing* toasts/snackbars (it fires on `WaitingForTool/Completed/Error`), not model re-invocation. Note it
  only as a *candidate* surface if/when a "process done → notify" path is built; it does not satisfy the spec's
  agent-facing consumption semantics.
- Implement the `CompletionConsumed` flag and watch rate-limiting now (cheap, correct), so the semantics are
  ready when the host loop gains re-invocation.

---

## 7. Cross-cutting questions (answers)

**Q1 — Code-execution security model.** `process` itself does **not** launch arbitrary commands — `terminal`
does. `process`'s only *incremental* risk surface is (a) `write`/`submit` injecting stdin into a running process
and (b) `kill`. The dangerous-command consent gate (hardline + dangerous-pattern detection, session-scoped
approvals — `tool_registration.md` §6) belongs at the **launch** point (`terminal`/`execute_code`), not here.
Recommendation: do **not** extend the per-write ActionCard guard to `process` lifecycle actions; reserve any
confirmation for `kill` (optional, §8). Re-litigate code-exec gating in `terminal.plan.md`.

**Q2 — Filesystem scope.** Not directly relevant to `process` (no paths). But the whole coding toolset needs a
**workspace root** broader than the single `AssistantFilesFolder` sandbox. Recommendation: introduce a
"workspace root" config (separate from `AssistantFilesFolder`) that gates the coding toolset's availability
(`IsAvailable` lambda) and serves as `terminal`'s default cwd. Privacy: workspace paths and any URL in output go
through `SensitiveDebug`/`SafeUrl`. (Detail belongs in `read_file.plan.md`/`terminal.plan.md`; flagged here for
the availability gate.)

**Q3 — Native vs MCP delegation.** **Build native.** A generic shell/filesystem MCP server can launch a command,
but it will **not** reproduce `process`'s lifecycle contract: rolling 200 KB buffer with eviction, non-consuming
`poll` vs consuming `wait`/`log`, watch-pattern rate-limiting + circuit breaker, tree-kill, and interactive
`write`/`submit`/`close`. Decisively, **`McpPluginToolHandler` hides stdio** (`StdioClientTransport` owns the
child's streams), so the stdin actions are unreachable through MCP. Native also matches the user's
minimal-dependency preference. MCP remains the right path for *third-party* tools, not the core coding lifecycle.

**Q4 — task_id threading.** Covered in §3. Pia threads **no** id today; `ChatSession.Id` is the natural source.
Do it day one as part of `tool_registration`.

**Q5 — Extend vs rebuild FilesToolHandler.** Out of scope for `process` (it shares no tool names with Files). For
the toolset overall: recommend a **separate richer coding-file toolset** (line-numbered `read_file`, paginated,
fuzzy `patch`) alongside the existing simple Files sandbox tools, to avoid regressing the current sandbox UX.
Decided in `read_file.plan.md`/`write_file.plan.md`/`patch.plan.md`.

**Q6 — Python runtime.** Not relevant to `process` (it manages OS processes, not a Python runtime). Decided in
`execute_code.plan.md`. (Process's `write`/`submit`/`close` *do* enable driving a Python REPL once one is
launched via `terminal`.)

---

## 8. Open questions

1. **task_id mechanism:** explicit parameter through the handler signatures (preferred) vs `AsyncLocal`
   ambient stopgap? Coordinate with `tool_registration.plan.md`.
2. **`notify_on_complete` re-invocation:** does Pia want a host loop that re-wakes the model when a background
   process exits? Until then, is `wait` sufficient? (Biggest fidelity gap.)
3. **`kill` confirmation:** show an ActionCard for `kill`, or execute immediately (consistent with the
   "lifecycle is immediate" stance)? Leaning immediate, with a clear log line.
4. **Disk persistence (crash recovery):** persist to `%LOCALAPPDATA%\Pia\processes.json`? **Persisting the
   buffer risks writing secrets to disk** — lean **metadata-only** (pid, start-time, status, exit_code), validate
   PID+start-time on restart, and report-only (don't reattach streams). Atomic write + CRLF if implemented.
5. **PTY:** ship plain `Process` stdin first; defer **ConPTY** (interactive REPL/installer fidelity) to phase 2.
   The spec's PTY auto-disable-on-piped-stdin rule lives in `terminal`.
6. **Secret redaction strength:** what redaction ruleset runs before store (token/key regexes, env values)?
   Reuse/extend any existing redaction; coordinate with privacy-logging owners.
7. **`session_id` format:** confirm `proc_<hex>` and that `terminal` mints it (registry should mint to keep the
   format in one place).

---

## 9. Build / implementation checklist

**Spine (shared with `terminal`)**
- [ ] `IBackgroundProcessRegistry` + `BackgroundProcessRegistry` (singleton, thread-safe, `(taskId, sessionId)` keyed).
- [ ] `ProcessSession` state record (§4); `RollingOutputBuffer` (~200 KB ring, line-indexed).
- [ ] ANSI strip + secret redaction **on append** (before store).
- [ ] Tree-kill: Windows `taskkill /T /F`; SIGTERM→grace→kill abstraction behind a method for future POSIX.

**`task_id` threading (tool_registration coordination)**
- [ ] Thread `taskId` (from `ChatSession.Id`) through `RouteToolCallAsync` + `HandleToolCallAsync` (or AsyncLocal stopgap).

**Handler**
- [ ] `IProcessToolHandler` + `ProcessToolHandler`; single `process` tool via `AIFunctionFactory.Create` + `[Description]` schema method matching §1.1 verbatim.
- [ ] All 8 actions; `list` needs no `session_id`, others require it; **coerce int→str**; precise missing-arg errors.
- [ ] `poll` non-blocking & non-consuming; `wait` blocking & consuming (honor `timeout`, partial on timeout); `log` paginated (`offset`/`limit`, default last 200 lines, return `total_lines`) & consuming.
- [ ] `write`/`submit`/`close` drive stdin (no newline / +Enter / EOF); surface clearly if stdin unavailable.
- [ ] Head+tail truncation on `wait`/`log` output with a truncation marker.
- [ ] Watch rate-limit (1/15 s/process) + 3-strike auto-promote + global circuit breaker (state on `ProcessSession`).
- [ ] Privacy: all buffer/preview/`data`/path/URL logging via `SensitiveDebug`/`SafeUrl`; **no raw output at LogInformation**.

**Wiring**
- [ ] `BuiltInPluginHandler.FromProcessHandler(...)` adapter (mirror `FromFilesHandler`).
- [ ] `BuiltInPluginDefaults` entry + new built-in GUID; `PluginService.InitializeBuiltInPlugins()` switch arm.
- [ ] DI registrations in `Bootstrapper` (registry + handler as singletons; inject handler into `PluginService`).
- [ ] Availability gate via `isAvailable` lambda (workspace root / coding-tools feature flag).

**Deferred / optional**
- [ ] ConPTY (phase 2). [ ] Disk persistence metadata-only (§8.4). [ ] `notify_on_complete` host re-invocation (§8.2).

---

## 10. Test strategy (xunit.v3, matching the repo)

Conventions (mirror `tests/Pia.Wpf.Tests/Unit/ScheduledJobToolHandlerTests.cs`): `using Xunit;`,
`NullLogger<T>.Instance`, fakes (not Moq), `new FunctionCallContent("call-1", toolName, args)`, plain
`Assert.*`. No FluentAssertions (removed). New `.cs` files must be **CRLF** (per MEMORY).

**Registry unit tests** (`Unit/BackgroundProcessRegistryTests.cs`) — the highest-value target:
- [ ] Rolling buffer evicts oldest at the ~200 KB window; `total_lines`/`offset`/`limit` pagination correct across eviction.
- [ ] ANSI escapes stripped before store; redaction applied to a known secret pattern.
- [ ] `list` filters by `taskId` (process under task A invisible to task B).
- [ ] Tree-kill marks status `Exited`/sets exit handling (use a short-lived real child process, or an injectable launcher abstraction so the test doesn't depend on a specific binary).

**Handler unit tests** (`Unit/ProcessToolHandlerTests.cs`):
- [ ] `session_id` int→str coercion (`["session_id"]=123` resolves).
- [ ] Missing `session_id` on `poll`/`log`/`wait`/`kill`/`write`/`submit`/`close` → precise corrective error; `list` works without it.
- [ ] Missing `data` on `write`/`submit` → precise error.
- [ ] Unknown `action` → error enumerating valid actions.
- [ ] `poll` does NOT set `CompletionConsumed`; `wait`/`log` DO.
- [ ] `log` honors `offset`/`limit` and reports `total_lines`.
- [ ] `wait` returns partial output + a timeout marker when `timeout` elapses before exit.
- [ ] Unknown `session_id` → precise "no such process for this task" error.

**Integration** (optional, mirror `Integration/ToolPipelineTestBase.cs`): a fake/fast launcher → `process` poll →
`submit` → `wait`, asserting buffer + lifecycle end-to-end without a heavy real binary.

---

## 11. References (files that exist today)

- `src/Pia.Wpf/Services/FilesToolHandler.cs` — handler shape to mirror (GetTools/HandleToolCallAsync, arg parsing, `SensitiveDebug`).
- `src/Pia.Wpf/Services/Interfaces/IFilesToolHandler.cs`, `.../IPluginToolHandler.cs` — interface conventions.
- `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs` — `FromFilesHandler` adapter + `isAvailable` gating to copy.
- `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs` — built-in GUID range + default config.
- `src/Pia.Wpf/Services/Plugins/PluginService.cs` — `InitializeBuiltInPlugins()` switch, `RouteToolCallAsync`, `RebuildToolNameRoutes` (no task_id today).
- `src/Pia.Wpf/Services/Plugins/McpPluginToolHandler.cs` — evidence that stdio is hidden (Q3); `SensitiveDebug`/`Truncate` precedent.
- `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` — `HandleToolCall`/`RouteToolCallAsync` call site; `ChatSession.Id` (task_id source); ActionCard flow.
- `src/Pia.Wpf/Bootstrapper.cs` — DI registration site.
- `src/Pia.Wpf/Logging/SafeLog.cs`, `.../SafeUrl.cs` — privacy helpers (mandatory for buffer output).
- `src/Pia.Wpf/Services/BackgroundChatNotificationSurface.cs` — *candidate-only* surface (§6); not the agent-facing consumption mechanism.
- `tests/Pia.Wpf.Tests/Unit/ScheduledJobToolHandlerTests.cs` — xunit.v3 handler-test template.
