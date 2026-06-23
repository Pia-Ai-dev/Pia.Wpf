# Implementation Plan — `terminal` coding tool (Pia.Wpf)

> **Status:** PLANNING ONLY. No code in this doc is implemented. Build artifacts are markdown.
> **Classification:** `bucket=scratch` — no existing Pia component covers the defining contract of
> `terminal` (persistent `task_id`-scoped shell session, background process tracking with rolling
> buffers, notify/watch engine, optional PTY). Build new behind a backend interface, reusing only the
> `IPluginToolHandler` / `BuiltInPluginHandler` registration plumbing and the `ProcessStartInfo`
> capture template from `CabManagerService.ExtractCabAsync`.
> **Spec source:** `docs/coding-tools-spec/terminal.md` (+ `process.md`, `tool_registration.md`,
> `overview.md`).

---

## 0. What this plan owns vs references

`terminal` touches several cross-cutting concerns that are *shared* with other tools. To keep scope
honest:

| Concern | Ownership |
|---------|-----------|
| `terminal` handler + interface (`ITerminalToolHandler`) | **Owned here** |
| Backend abstraction (`ITerminalBackend`, local-subprocess first) | **Owned here** |
| Foreground/background semantics, notify/watch engine | **Owned here** |
| Background-process **registry** (`session_id → {process, rolling buffer, status, watch meta}`, keyed by `task_id`) | **Defined here, consumed by `process`** (see `process.md`) |
| Command **guard** (hardline + dangerous-pattern + session-scoped approvals) | **Defined here, shared with `execute_code`** |
| `task_id` retrofit into dispatch | **Belongs to registration infra** (`tool_registration.md`); affects every tool; sequenced first here because `terminal` cannot function without it |
| Workspace-root / cwd map (`task_id → cwd`) | **Defined here as the cwd source-of-truth** that `read_file` / `patch` / `search_files` resolve against |
| Extend-vs-rebuild `FilesToolHandler` (Q5) | **Referenced, decided in `read_file.plan.md` / `write_file.plan.md`** |
| Python runtime for `execute_code` (Q6) | **Referenced, decided in `execute_code.plan.md`** |

---

## 1. Tool contract (restated from spec)

### 1.1 Identity & registration

- **Name:** `terminal`
- **Toolset / group key:** `terminal`
- **Registered with `max_result_size_chars = 100_000`.** This feeds budgeting layer 2 (see
  `tool_registration.md` §4). The dispatcher truncates output to this cap before it re-enters the model
  context.
- **Description:** copy verbatim from spec. The foreground/background/notify steering *is* the contract;
  paraphrasing it makes the agent go blind on long jobs.

### 1.2 JSON-Schema parameters (exact)

```json
{
  "name": "terminal",
  "description": "<TERMINAL_TOOL_DESCRIPTION — copy verbatim>",
  "parameters": {
    "type": "object",
    "properties": {
      "command":            {"type": "string",  "description": "The command to execute on the VM"},
      "background":         {"type": "boolean", "default": false, "description": "Run in background. Almost always pair with notify_on_complete=true. ..."},
      "timeout":            {"type": "integer", "minimum": 1, "description": "Max seconds to wait (default 180, foreground max ~600). Returns INSTANTLY when the command finishes ..."},
      "workdir":            {"type": "string",  "description": "Working directory for this command (absolute path). Defaults to the session working directory."},
      "pty":                {"type": "boolean", "default": false, "description": "Run in a pseudo-terminal for interactive CLIs. Local/SSH backends only."},
      "notify_on_complete": {"type": "boolean", "default": false, "description": "When true (and background=true), notify exactly once when the process exits. MUTUALLY EXCLUSIVE with watch_patterns."},
      "watch_patterns":     {"type": "array", "items": {"type": "string"}, "description": "Strings to watch for in background output. HARD LIMIT 1 notification / 15s / process; after 3 dropped windows it auto-disables and falls back to notify_on_complete. MUTUALLY EXCLUSIVE with notify_on_complete."}
    },
    "required": ["command"]
  }
}
```

### 1.3 Parameter semantics

| Param | Type | Default | Semantics |
|-------|------|---------|-----------|
| `command` | string | — (required) | Shell command to run in the session shell. |
| `background` | bool | `false` | If true, spawn tracked process, return handle immediately; further interaction via `process`. |
| `timeout` | int (≥1) | 180 | **"Give up after N", not "wait N".** Foreground returns the instant the command exits. Foreground hard max ~600s — reject above and tell the agent to use `background=true`. |
| `workdir` | string | session cwd | Absolute working dir for **this command only**; does not mutate the session cwd. Allowlist-validate against shell metacharacters. |
| `pty` | bool | `false` | Pseudo-terminal for interactive CLIs. Local/SSH only. Auto-disable when the command needs piped stdin (e.g. `... --with-token`). |
| `notify_on_complete` | bool | `false` | With `background=true`, queue exactly one completion event on exit. **Mutually exclusive with `watch_patterns`.** |
| `watch_patterns` | string[] | — | Mid-stream string triggers, rate-limited (1/15s/process), 3-strike auto-promote to `notify_on_complete`, global circuit breaker. **Mutually exclusive with `notify_on_complete`.** |

### 1.4 Return shape

- **Foreground:** `{ output, exit_code, duration }` — full output up to the 100K cap (head+tail truncation).
- **Background:** `{ session_id, pid, status: "running" }` — returns *immediately*. `session_id` is
  `proc_<hex>`. Further interaction goes through the `process` tool.

### 1.5 Required invariants (must reproduce)

1. **Persistent session per `task_id`.** Filesystem state, **cwd**, and **exported env vars** persist
   across calls. Maintain a `task_id → cwd` override map (the cwd source-of-truth that `read_file` /
   `patch` / `search_files` relative paths resolve against). `workdir` overrides cwd for one command only.
2. **Timeout = "give up after N".** Foreground returns the instant the command exits. Default 180s;
   foreground hard max ~600s — reject above and tell the agent to use `background=true`.
3. **Background launch.** Spawns a tracked process, returns `session_id` + `pid` immediately, keeps
   running independently. Output buffers in a rolling window (see `process.md`). Two correct patterns:
   never-exiting (silent OK), bounded long task (require `notify_on_complete=true`).
4. **`notify_on_complete`:** queue exactly one completion event on exit; host delivers it to the agent.
   (See §3 — **delivery into the agent turn loop is an open architectural gap in Pia.**)
5. **`watch_patterns`:** hard rate limit (1/15s/process); after 3 consecutive dropped windows
   auto-disable and promote to `notify_on_complete`. Global circuit breaker (e.g. max 15 watch
   notifications / 10s across all processes; trip 30s).
6. **PTY mode** for interactive CLIs. Local/SSH only; auto-disable when piped stdin is needed.
7. **`workdir` validation.** Allowlist-validate against shell metacharacters before use.
8. **ANSI strip + secret redaction** before storing/returning output.
9. **Command guard** (dangerous-pattern detection) before execution — shared with `execute_code`.

---

## 2. Placement in Pia.Wpf (following existing conventions)

### 2.1 New types

| Type | Kind | Path (proposed) | Role |
|------|------|-----------------|------|
| `ITerminalToolHandler` | interface | `src/Pia.Wpf/Services/Interfaces/ITerminalToolHandler.cs` | Mirrors `IFilesToolHandler`: `IsAvailable`, `GetTools()`, `HandleToolCallAsync(...)`, `ExecutePendingActionAsync(...)`. |
| `TerminalToolHandler` | implementation | `src/Pia.Wpf/Services/TerminalToolHandler.cs` | Dispatches `terminal`; builds schema via `AIFunctionFactory`; routes to the backend + registry. |
| `ITerminalBackend` | interface | `src/Pia.Wpf/Services/Terminal/ITerminalBackend.cs` | Backend abstraction (local subprocess first; docker/ssh later). |
| `LocalSubprocessBackend` | implementation | `src/Pia.Wpf/Services/Terminal/LocalSubprocessBackend.cs` | `ProcessStartInfo` + reader-thread draining stdout/stderr into rolling buffer. |
| `BackgroundProcessRegistry` | service | `src/Pia.Wpf/Services/Terminal/BackgroundProcessRegistry.cs` | `session_id → {pid, start-time, status, exit_code, rolling buffer, stdin handle, watch meta}`, keyed by `task_id`. **Also consumed by the `process` tool.** |
| `CommandGuard` | service | `src/Pia.Wpf/Services/Terminal/CommandGuard.cs` | `detect_hardline_command` + `detect_dangerous_command` + session-scoped approvals. **Shared with `execute_code`.** |
| `ShellSessionState` | model | `src/Pia.Wpf/Services/Terminal/ShellSessionState.cs` | Per-`task_id` cwd + exported env vars. |
| `OutputSanitizer` | helper | `src/Pia.Wpf/Services/Terminal/OutputSanitizer.cs` | ANSI-escape strip + secret redaction + head+tail truncation. |

> File/interface naming follows the existing `IFilesToolHandler` / `FilesToolHandler` /
> `FilesToolCall` triad. The task names the interface `IterminalToolHandler`; Pia's PascalCase
> convention makes it **`ITerminalToolHandler`** — use that.

### 2.2 Reusable patterns to follow (cite real files)

- **Registration via `GetTools()`** — return `AITool[]` from `AIFunctionFactory.Create` with
  `[Description]`-annotated private schema methods, exactly as `FilesToolHandler` does
  (`src/Pia.Wpf/Services/FilesToolHandler.cs`).
- **Adapter factory** — add `BuiltInPluginHandler.FromTerminalHandler(ITerminalToolHandler, SyncPlugin)`
  mirroring `FromFilesHandler` in `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs` (lines 185–202),
  including an `isAvailable: () => handler.IsAvailable` gate so the tool is suppressed when terminal
  access is not enabled.
- **Registration + dispatch infra** — register in `PluginService.InitializeBuiltInPlugins()` switch
  (`src/Pia.Wpf/Services/Plugins/PluginService.cs` lines 79–88) via a new `"terminal"` handler-id branch;
  add a `BuiltInPluginDefaults` entry with a stable `TerminalPluginId` GUID. Dispatch flows through
  `PluginService.RouteToolCallAsync` (lines 265–284) unchanged **except** for the `task_id` thread (§4).
- **Subprocess capture template** — start from `CabManagerService.ExtractCabAsync`
  (`src/Pia.Wpf/Services/Plugins/CabManagerService.cs` lines 172–192): `ProcessStartInfo` with
  `CreateNoWindow=true`, `UseShellExecute=false`, `RedirectStandardOutput/Error=true`. Extend it from
  one-shot `WaitForExitAsync` to a reader-thread + rolling-buffer + instant-return model.
- **Pending-action approval guard** — `FilesToolCall` / `PluginToolCall` two-phase pattern
  (`HandleToolCallAsync` returns `(result, pendingAction?)`; `ExecutePendingActionAsync` runs after
  confirmation). Used here as the **UI surface for the guard's "ask" decision** (see §2.4).
- **Availability gating** — `IsAvailable` property suppresses both `GetTools()` and the system-prompt
  addition (`FilesToolHandler` + `BuiltInPluginHandler` `_isAvailable` lambda).
- **Privacy logging** — `_logger.SensitiveDebug(...)` for command text, cwd, env-var values, and
  command output (all user-content / payload per `CLAUDE.md`); `SafeUrl.Format(...)` for any URL that
  appears in a command or output line. Non-sensitive: tool-name routing, `session_id`, `pid`, exit code,
  duration.

### 2.3 DI wiring (Bootstrapper)

Register in `src/Pia.Wpf/Bootstrapper.cs` alongside the other tool handlers:

- `ITerminalBackend` → `LocalSubprocessBackend` (singleton).
- `BackgroundProcessRegistry` (singleton — shared with the future `process` handler).
- `CommandGuard` (singleton — shared with `execute_code`).
- `ITerminalToolHandler` → `TerminalToolHandler` (singleton), injected into `PluginService`'s ctor list
  next to `IFilesToolHandler`.

> Do **not** modify any `.cs` / `.csproj` in this task. The above is the target wiring for the
> implementation phase.

### 2.4 Security / consent model (Q1)

- **The existing pending-action ActionCard guard is NOT sufficient on its own.** It is a per-write-op
  confirmation with no command-pattern detection. `terminal` runs arbitrary shell input.
- **New layer required** (`tool_registration.md` §6):
  - `detect_hardline_command(cmd)` — always blocked (fork bombs, disk wipes); cannot be approved away.
  - `detect_dangerous_command(cmd)` — compiled regex list (`rm -rf`, `chmod -R 777`, `curl | sh`, …)
    returning `(matched, description)`; prompts for approval.
  - **Normalization before matching** (resolve `$HOME`, collapse whitespace) so trivial obfuscation does
    not slip past. Compile patterns once (hot path).
  - **Session-scoped approvals** — once approved for a pattern, remember for the session (key off the
    `task_id`); don't re-prompt every call.
- **ActionCard is the *ask* surface only.** When the guard returns `ask`, surface it as an
  `ActionCardInfo` (blocking the turn via `WaitForUserDecisionAsync`, exactly like `FilesToolCall`).
  `clarify` is NOT the channel for command confirmation.
- **OPEN QUESTION (product):** *is arbitrary shell execution even in scope for a privacy-first
  assistant?* This is a real product decision, not just an engineering one. Design the guard regardless;
  flag the scope decision (see §7).

### 2.5 Filesystem scope (Q2)

- **Do NOT extend `SafeFolderPath`.** `SafeFolderPath.TryResolveInside`
  (`src/Pia.Wpf/Infrastructure/SafeFolderPath.cs`) deliberately **rejects rooted/absolute/UNC paths**.
  A terminal cwd and `workdir` are absolute by definition — that gate is the wrong tool here.
- **Introduce a "workspace root"** configuration (new `AppSettings` field, e.g. `WorkspaceRootFolder`)
  distinct from `AssistantFilesFolder`. The session cwd defaults to the workspace root; `workdir` must
  resolve to an absolute path inside the workspace root (or be rejected). This is a *different scope gate*
  than the files sandbox: it permits absolute paths but constrains them to the workspace tree.
- **Logging interaction:** workspace paths, cwd, command text, and command output are sensitive →
  `SensitiveDebug`; URLs in output → `SafeUrl.Format`. ANSI-strip and secret-redact output **before** it
  is stored in the rolling buffer or returned, so neither the buffer nor logs leak secrets in release.

### 2.6 Native vs MCP delegation (Q3)

- **Decision: build native, behind `ITerminalBackend` (local subprocess first).**
- **Why not an MCP shell server:** a generic MCP filesystem/shell server will not honor Pia's
  load-bearing contract — persistent `task_id`-scoped cwd+env, rolling output buffers, watch
  rate-limiting + auto-promote + circuit breaker, and notify-into-the-turn-loop. The MCP
  `StdioClientTransport` path (`McpPluginToolHandler`) is SDK-internal long-lived stdio *for MCP
  servers*, not a reusable general shell. Delegating would mean re-implementing the same invariants on
  top of an opaque transport.
- **The fork is real** — naming it: *integrate an off-the-shelf shell MCP server* vs *build native*.
  Native wins because the spec's invariants are Pia-side behaviors the host must own. Keep the backend
  behind an interface so docker/ssh/modal can be added later (as the spec recommends).

---

## 3. Cross-cutting invariants that apply (from `overview.md`)

| # | Principle | Applies to `terminal` as… |
|---|-----------|---------------------------|
| 1 | Line-numbered reads are the coordinate system | N/A directly, but `read_file` resolves paths against the session **cwd** this tool owns. |
| 2 | Fuzzy matching on edits | N/A (patch concern). |
| 3 | Delta-filter diagnostics | N/A (write/patch concern). |
| 4 | Loop / dedup guards | Optional: guard against the agent re-running an identical failing command in a tight loop. |
| 5 | Staleness tracking | N/A (mtime is a file-tool concern); cwd persistence is the analogous session-state here. |
| 6 | Return a diff / verify write persisted | N/A. |
| 7 | **Truncate head+tail, not head-only** | **REQUIRED** for command output — the tail carries the error/result. Enforce in `OutputSanitizer` and at the 100K cap. |
| 8 | **Pagination everywhere** | Foreground output capped at 100K; full background output paginated via `process(action="log", offset, limit)`. |
| 9 | Atomic writes, preserve CRLF/BOM | N/A (file-write concern). |
| 10 | **Self-healing arg validation** | **REQUIRED**: missing `command` → precise corrective error; `notify_on_complete` + `watch_patterns` both set → reject with explanation (mutually exclusive); foreground `timeout > ~600` → reject and steer to `background=true`; coerce stray arg shapes. |
| — | **`task_id`-keyed state** | **REQUIRED** — see §4. Session cwd+env, background registry, and guard approvals all key off `task_id`. |

### 3.1 `notify_on_complete` delivery — OPEN ARCHITECTURAL GAP

The spec says the host "delivers the completion event **to the agent**." In Pia today:

- The turn loop is a single UI-thread-affine stream
  (`AiClientService.GetChatCompletionWithToolsAsync`, driven by `ChatSession.RunTurnAsync`).
  Note: `ChatSession` lives at `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` (not under `Services/`);
  all `ChatSession.cs` line references below are to that file.
- `BackgroundChatNotificationSurface` notifies the **user** (toast/snackbar) — it has no path to inject a
  background-process completion as a **new agent turn**.

There is **no mechanism today** to re-enter the agent loop from a background process exit. This is a
genuine gap that intersects the background-chats infrastructure. Options to evaluate in implementation
(do not decide here): (a) on completion, enqueue a system/tool message and start a follow-up turn on the
owning `ChatSession`; (b) deliver only to the user (toast) and require the agent to `process(poll/wait)`;
(c) gate `notify_on_complete` behind background-chats. **Flagged as open question §7.**

---

## 4. `task_id` threading (Q4) — DO THIS FIRST

The spec is explicit: **implement `task_id` from day one; retrofitting is painful.** `terminal` cannot
function without it (cwd, env, background registry, and approvals are all keyed by it).

### 4.1 The blocker (verified against source)

- `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, CancellationToken)` carries **no**
  session/task id (`src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs` line 19).
- `FunctionCallContent` carries no task/session id (only `Name` / `Arguments` / `CallId` are relevant here).
- `PluginService.RouteToolCallAsync(FunctionCallContent, CancellationToken)` calls the handler with no id
  (`PluginService.cs` lines 265–284).
- `ChatSession.HandleToolCall` has `this` (the session) but does not pass any id down
  (`ChatSession.cs` lines 404–412).

### 4.2 The retrofit (registration-infra change, not terminal-specific)

Thread a `string taskId` through the dispatch chain:

1. Add `string taskId` parameter to `IPluginToolHandler.HandleToolCallAsync` (and the
   `BuiltInPluginHandler` delegate signature). Default `"default"` for callers that don't supply one.
2. `PluginService.RouteToolCallAsync` accepts and forwards `taskId`.
3. `ChatSession.HandleToolCall` supplies the session's task id when routing.

### 4.3 Where `task_id` comes from — PITFALL

- **Do NOT key off `ChatSession.Id`.** It is `Guid?` and is set **only at first persist** (see
  `ChatSession.SetIdentity`, `ChatSession.cs` lines 102–109) — it is `null` for a fresh, unsent session
  and assigned late. Keying `task_id` off it would change the key mid-session and orphan the shell
  session / background processes.
- **Recommendation:** assign a **stable runtime id at `ChatSession` construction** (a `Guid`/string set
  in the ctor, independent of the persisted `Id`), and map `task_id = runtimeId.ToString()`, with
  `"default"` as the fallback for non-session callers (tests, tools invoked outside a chat).
- Because background chats / multi-assistant sessions already give each conversation its own
  `ChatSession` with isolated `TokenMap`, a per-session stable runtime id makes parallel sessions safe
  exactly as the spec intends for parallel subagents.

---

## 5. Build / implementation checklist

Sequence matters — the `task_id` retrofit is first because retrofitting later is painful.

- [ ] **(infra) `task_id` retrofit** — add `taskId` to `IPluginToolHandler.HandleToolCallAsync`,
      `BuiltInPluginHandler` delegate, `PluginService.RouteToolCallAsync`, `ChatSession.HandleToolCall`;
      stable runtime id on `ChatSession` ctor; `"default"` fallback.
- [ ] **`ITerminalToolHandler`** interface (mirror `IFilesToolHandler`) + `TerminalToolCall` record.
- [ ] **`ITerminalBackend`** interface; **`LocalSubprocessBackend`** (ProcessStartInfo + reader thread +
      rolling buffer; instant-return-on-exit).
- [ ] **`ShellSessionState`** (`task_id → cwd + exported env`); workspace-root resolution; `workdir`
      per-command override with shell-metachar allowlist validation.
- [ ] **Foreground execution** — default 180s / max ~600s; instant-return-on-exit; full output to 100K
      with **head+tail** truncation; return `{output, exit_code, duration}`.
- [ ] **`BackgroundProcessRegistry`** — `session_id (proc_<hex>)` + `pid` + start-time + status +
      exit_code + rolling buffer (~200KB, oldest evicted) + stdin handle + watch meta; keyed by `task_id`.
      Background launch returns `{session_id, pid, status:"running"}` immediately.
- [ ] **`notify_on_complete`** — one-shot completion event on exit (delivery mechanism = open question,
      §3.1).
- [ ] **`watch_patterns`** engine — 1/15s/process rate limit; 3-strike auto-promote to
      `notify_on_complete`; global circuit breaker (≈15/10s, trip 30s). **Mutually exclusive** with
      `notify_on_complete` (reject if both set).
- [ ] **`OutputSanitizer`** — ANSI-escape strip + secret redaction + head+tail truncation; applied
      before buffer store and before return.
- [ ] **`CommandGuard`** — hardline + dangerous-pattern detection, normalization, session-scoped
      approvals; ActionCard as the "ask" surface.
- [ ] **PTY** (optional, local only) — auto-disable on piped-stdin commands. See §6.
- [ ] **Self-healing arg validation** — missing `command`; both notify+watch set; foreground
      `timeout > ~600`; coerce stray arg shapes.
- [ ] **Registration** — `BuiltInPluginHandler.FromTerminalHandler`; `BuiltInPluginDefaults` entry +
      `TerminalPluginId`; `"terminal"` branch in `PluginService.InitializeBuiltInPlugins()`;
      system-prompt addition (copy spec description verbatim).
- [ ] **DI** — `Bootstrapper.cs` registrations (§2.3).
- [ ] **Privacy logging audit** — command/cwd/env/output via `SensitiveDebug`; URLs via `SafeUrl.Format`.
- [ ] New `.cs` files use **CRLF** line endings (repo convention; Write tool emits LF).

---

## 6. PTY note (minimal-dependency stance)

The spec marks PTY **optional**, local/SSH only. The user prefers hand-rolled/lightweight over heavy
NuGet libraries. On Windows the native path is **ConPTY** (`CreatePseudoConsole`) via P/Invoke. Two
acceptable plans: (a) hand-roll a thin ConPTY interop wrapper, or (b) **defer PTY** to a later milestone
and ship background + `notify_on_complete` for interactive flows first. **Do not pull a third-party PTY
library** without explicit sign-off. Flag as open question §7.

---

## 7. Test strategy (repo-faithful)

Repo runs **xunit.v3 with plain `Xunit.Assert` — no FluentAssertions** (see project memory). MTP via
`global.json`. Test against a **fake `ITerminalBackend`** so tests are deterministic and don't spawn real
shells. **Do not propose winwright or running the app** (both banned per project memory).

Test matrix:

| Area | Assertions |
|------|------------|
| Foreground instant-return | Returns the moment the (fake) command exits, even with a large `timeout`. |
| Timeout = give-up | Returns partial result after `timeout` when the command is still running. |
| Foreground max | `timeout > ~600` rejected with a steer-to-background message. |
| Output cap | Output > 100K truncated **head+tail** with a clear marker. |
| ANSI strip | Escape sequences removed before store/return. |
| Secret redaction | Known secret shapes redacted before store/return. |
| Background launch | Returns `{session_id (proc_<hex>), pid, status:"running"}` immediately; process registered. |
| Rolling buffer | Oldest output evicted past the ~200KB window. |
| Watch rate limit | ≤ 1 notification / 15s / process. |
| Watch auto-promote | 3 dropped windows → watch disabled, promoted to `notify_on_complete`. |
| Watch circuit breaker | Global cap trips and recovers. |
| Mutual exclusion | `notify_on_complete` + `watch_patterns` both set → rejected. |
| cwd persistence | `cd`-style state persists across calls within a `task_id`; isolated across task_ids. |
| `workdir` override | Applies for one command only; does not mutate session cwd. |
| `workdir` validation | Shell metacharacters / outside-workspace paths rejected. |
| Guard — hardline | Always blocked, not approvable. |
| Guard — dangerous | Returns `(matched, description)`; ask-decision surfaces an ActionCard. |
| Guard — approval memory | Approved pattern not re-prompted within the same `task_id`. |
| Self-healing args | Missing `command` → precise corrective error; coercion works. |
| `task_id` threading | Two sessions get isolated cwd/env/registry; `"default"` fallback works. |

---

## 8. Open questions

1. **Arbitrary shell exec scope (product).** Is arbitrary `terminal` execution in scope for a
   privacy-first assistant at all, or only behind an explicit opt-in (workspace configured + per-session
   consent)? The guard is designed either way, but the default-enabled question is a product call.
2. **`notify_on_complete` delivery into the agent loop (§3.1).** No mechanism today re-enters the agent
   turn from a background process exit. Pick: (a) enqueue a follow-up turn on the owning `ChatSession`,
   (b) user-toast only + require `process(poll/wait)`, (c) gate behind background-chats.
3. **Workspace root model (Q2).** New `AppSettings.WorkspaceRootFolder`? Single root vs per-session root?
   How does it relate to the existing `AssistantFilesFolder` sandbox — separate setting, or unified
   workspace concept the file tools also migrate to?
4. **PTY (Q6-adjacent).** Hand-roll ConPTY interop now, or defer PTY entirely to a later milestone?
5. **Secret-redaction source of truth.** Reuse `PrivacySettings.PiiKeywords`, or a separate
   command-output redaction ruleset (env-var values, tokens, `Authorization:` headers)?
6. **Crash recovery (`process.md` §5).** Persist the background registry to disk
   (`%LOCALAPPDATA%\Pia\...`) with PID + start-time validation, or accept loss of background processes on
   host restart for v1?
7. **`task_id` runtime id (Q4).** Confirm the stable-runtime-id-on-construction approach over reusing the
   late-assigned persisted `ChatSession.Id` — and whether the id should be exposed on `IChatSessionManager`
   for the `process` tool / `delegate_task` to correlate.
8. **Referenced, decided elsewhere:** Q5 (extend-vs-rebuild `FilesToolHandler`) → `read_file.plan.md` /
   `write_file.plan.md`; Q6 (Python runtime) → `execute_code.plan.md`. The cwd source-of-truth defined
   here is a hard dependency of those.

---

## 9. Related specs

- `process.md` — operates on the background `session_id`s this tool produces; consumes the
  `BackgroundProcessRegistry` defined here.
- `execute_code.md` — shares the `CommandGuard`; programmatic multi-step alternative.
- `tool_registration.md` — owns the `task_id` retrofit, output budgeting, schema sanitization, and the
  approval-guard contract this plan plugs into.
