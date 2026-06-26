# Implementation Plan — `execute_code` (Pia.Wpf)

> **Status:** Planning only. No code in this repo executes arbitrary Python, captures
> arbitrary-subprocess stdout/stderr, or proxies tools over an RPC channel. This tool is a
> **from-scratch build** plus a **hard dependency** on tools that do not exist yet.
> **Bucket:** `scratch` (the core has no Pia equivalent to reuse; only the registration/dispatch
> *scaffolding* and the approval/path-safety/logging helpers are reusable to plug into).
>
> Spec source: [`../execute_code.md`](../execute_code.md) · cross-cutting:
> [`../overview.md`](../overview.md) · [`../tool_registration.md`](../tool_registration.md) ·
> reused guard: [`../terminal.md`](../terminal.md)

---

## 0. TL;DR — read this first

`execute_code` runs a **Python script** that calls the agent's other tools programmatically over an
**RPC channel**. Per spec **invariant 8** it must NOT reimplement those tools — it **proxies** them
back into the host so every guard (path safety, fuzzy patch, lint-delta, loop guards) still applies.

**The spine of this plan is a sequencing blocker.** `execute_code` proxies
`terminal`, `patch`, `search_files`, `read_file`, `write_file`, `web_search`, `web_extract`.
In Pia today only `read_file`/`write_file` exist (as the sandbox-gated `FilesToolHandler`), and they
are simpler than the spec versions (no line numbers, pagination, or fuzzy matching). `terminal`,
`patch`, `search_files`, and `web_*` **do not exist**. Therefore:

> **`execute_code` cannot be authored standalone. It is blocked on the core 7 tools being built
> first.** The spec itself says: build it *after* the core 7 are solid.

This document separates **(A) `execute_code`'s own net-new core** — which we *can* spec now — from
**(B) the external prerequisites it merely routes into** — which are other plans.

---

## 1. Tool contract (restated from spec)

### 1.1 Name & schema

| Field | Value |
|-------|-------|
| Model-facing name | `execute_code` (snake_case — do **not** rename) |
| Toolset / group | `code_execution` |
| Parameters | single required `code` (string) |
| `max_result_size_chars` | `100_000` (matches file/terminal tools; feeds budgeting layer 2) |
| Description | **built dynamically per session** (see §1.4) |

```json
{
  "name": "execute_code",
  "description": "<built per session: lists enabled sandbox tools + active mode + helpers>",
  "parameters": {
    "type": "object",
    "properties": {
      "code": {
        "type": "string",
        "description": "Python code to execute. Import tools with `from hermes_tools import <names>` and print your final result to stdout."
      }
    },
    "required": ["code"]
  }
}
```

### 1.2 Parameter semantics

- **`code`** — a complete Python script. It imports a safe tool subset from the in-sandbox
  `hermes_tools` module, does data-flow work (loops/branches/filtering), and `print()`s its final
  result to stdout. **That stdout is the tool result returned to the model.**

### 1.3 Return shape

- The script's captured **stdout** (head+tail-capped, see §3) is returned to the model as the tool
  result. stderr is captured separately (head-only cap) and surfaced on failure.
- On host-side failure (timeout, call-cap exceeded, dispatch error) return the uniform
  `{"error": "..."}` envelope (per `tool_registration.md` §3) — never throw into the turn loop.

### 1.4 The `hermes_tools` sandbox API (shipped into the child)

A thin RPC stub — each function is a round-trip back to the host, **not** a reimplementation:

```python
web_search(query, limit=5)                  -> dict   # {data.web: [...]}
web_extract(urls: list)                      -> dict   # {results: [...]}
read_file(path, offset=1, limit=500)         -> dict   # {content, total_lines}
write_file(path, content)                    -> dict
search_files(pattern, target="content", path=".", file_glob=None, limit=50) -> dict  # {matches}
patch(path=None, old_string=None, new_string=None, replace_all=False, mode="replace") -> dict
terminal(command, timeout=None, workdir=None) -> dict  # {output, exit_code}  (foreground only)
```

Built-in helpers (no import): `json_parse(text)` (lenient `json.loads`), `shell_quote(s)`
(`shlex.quote`), `retry(fn, max_attempts=3, delay=2)` (exponential backoff).

The description is **rebuilt each definitions pass** to list exactly the sandbox tools the current
toolset exposes, the active mode, and the helpers (spec; `tool_registration.md` §2.3
`dynamic_schema_overrides`).

### 1.5 Required invariants (verbatim from spec §"Required behaviors")

| # | Invariant | One-line |
|---|-----------|----------|
| 1 | **Env scrubbing** | Drop env vars matching `KEY\|TOKEN\|SECRET\|PASSWORD\|CREDENTIAL\|WEBHOOK`; allow only safe prefixes (`PATH`,`HOME`,`LANG`,`LC_`,`TERM`,`PYTHON*`,`VIRTUAL_ENV`,`CONDA`,`XDG_`) + declared passthrough + Windows OS essentials |
| 2 | **Execution mode** | `project` (default; session cwd + active venv) vs `strict` (isolated tmpdir + host python) |
| 3 | **Resource limits** | ~300s per-script timeout, ~50 max tool calls/script — enforced **host-side** (parent), un-bypassable by sandbox |
| 4 | **Output caps** | stdout ~50KB **head+tail** (≈40% head / 60% tail, omit middle w/ marker); stderr ~10KB head-only; ANSI strip + secret redaction |
| 5 | **Deadlock-safe capture** | Separate reader threads for stdout & stderr |
| 6 | **Cooperative interruption** | `is_interrupted()` flag checked during RPC; on new user message kill child tree (SIGTERM→grace→SIGKILL) |
| 7 | **Same approval guard as `terminal`** | Vet the script for dangerous patterns before dispatch; runs synchronously in caller thread holding session approval context |
| 8 | **Every RPC re-enters the real tools** | Proxy, do not reimplement; all host guards apply to sandbox calls |

---

## 2. Placement in Pia.Wpf

### 2.1 What is reusable scaffolding (plug into) vs net-new (build)

| Concern | Reuse? | Pia mechanism |
|--------|--------|---------------|
| Tool registration | ✅ reuse | `IPluginToolHandler.GetTools()` → `AIFunctionFactory.Create` ([`FilesToolHandler.cs`](../../../src/Pia.Wpf/Services/FilesToolHandler.cs)) |
| Dispatch / routing | ✅ reuse | `PluginService.RouteToolCallAsync` by tool name ([`PluginService.cs`](../../../src/Pia.Wpf/Services/Plugins/PluginService.cs)) |
| Built-in registration glue | ✅ reuse | `BuiltInPluginHandler.FromXxx` adapter + `BuiltInPluginDefaults` GUID/config ([`BuiltInPluginHandler.cs`](../../../src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs)) |
| Approval gate (UI) | ✅ reuse (with caveat — see §2.4) | `ActionCardInfo.WaitForUserDecisionAsync()` + `ChatSession.HandleToolCall` block ([`ChatSession.cs`](../../../src/Pia.Wpf/ViewModels/Models/ChatSession.cs):435) |
| Path safety | ✅ reuse | `SafeFolderPath.TryResolveInside` ([`SafeFolderPath.cs`](../../../src/Pia.Wpf/Infrastructure/SafeFolderPath.cs)) |
| Privacy logging | ✅ reuse | `SensitiveDebug` / `SafeUrl.Format` ([`SafeLog.cs`](../../../src/Pia.Wpf/Logging/SafeLog.cs), [`SafeUrl.cs`](../../../src/Pia.Wpf/Logging/SafeUrl.cs)) |
| **Python runtime** | ❌ build/decide | none present (open question, §6) |
| **`hermes_tools` stub** | ❌ build | none |
| **RPC transport (newline-JSON)** | ❌ build | none (MCP `StdioClientTransport` is *consume external server*, not host-as-RPC-server) |
| **Env scrub** | ❌ build | none |
| **project/strict mode (cwd / venv)** | ❌ build | no session-cwd concept exists |
| **host-side timeout + call-cap** | ❌ build | capability map: "NO process resource limits enforced by app" |
| **head+tail output cap + ANSI strip + redaction** | ❌ build | only 500-char log truncation exists |
| **command guard (dangerous patterns)** | ❌ build | depends on `terminal`'s guard, which does not exist |
| **child-tree kill + `is_interrupted()`** | ❌ build | none |

### 2.2 Proposed types & files (all NEW — none created by this plan)

| Artifact | Path (proposed) | Role |
|---------|-----------------|------|
| `IExecuteCodeToolHandler` | `src/Pia.Wpf/Services/Interfaces/IExecuteCodeToolHandler.cs` | Mirrors `IFilesToolHandler` shape: `IsAvailable`, `GetTools()`, `HandleToolCallAsync`, `ExecutePendingActionAsync`, dynamic-description hook |
| `ExecuteCodeToolHandler` | `src/Pia.Wpf/Services/ExecuteCodeToolHandler.cs` | Builds dynamic schema, vets script, spawns child, runs RPC dispatcher, caps output |
| `PythonRuntimeLocator` | `src/Pia.Wpf/Services/CodeExecution/PythonRuntimeLocator.cs` | Resolves project venv vs host python (mode), or absence → `IsAvailable=false` |
| `HermesRpcServer` | `src/Pia.Wpf/Services/CodeExecution/HermesRpcServer.cs` | Loopback-TCP newline-JSON server; per-frame routes back via `PluginService.RouteToolCallAsync`; enforces call-cap & interrupt |
| `EnvScrubber` | `src/Pia.Wpf/Services/CodeExecution/EnvScrubber.cs` | Deny-list + allow-prefix env construction |
| `OutputCapper` | `src/Pia.Wpf/Services/CodeExecution/OutputCapper.cs` | head+tail truncation, ANSI strip, secret redaction |
| `hermes_tools.py` | `src/Pia.Wpf/Assets/CodeExecution/hermes_tools.py` (embedded resource) | Shipped stub: RPC client + helpers |

> **Naming note:** the C# interface follows Pia's `IName` PascalCase convention →
> `IExecuteCodeToolHandler` (the literal `Iexecute_codeToolHandler` in the task brief was a
> placeholder). The **model-facing tool name stays `execute_code`** per spec.

### 2.3 DI wiring & registration (follow the files-plugin pattern exactly)

1. **Bootstrapper** ([`Bootstrapper.cs`](../../../src/Pia.Wpf/Bootstrapper.cs)): register
   `ExecuteCodeToolHandler` as `IExecuteCodeToolHandler` singleton (alongside `IFilesToolHandler`).
2. **`PluginService` ctor**: inject `IExecuteCodeToolHandler`, add a `"execute_code"` arm to the
   `InitializeBuiltInPlugins()` `switch` ([`PluginService.cs`](../../../src/Pia.Wpf/Services/Plugins/PluginService.cs):79).
3. **`BuiltInPluginHandler`**: add `FromExecuteCodeHandler(...)` factory (copy `FromFilesHandler`),
   gating `GetTools()`/system-prompt on `IsAvailable` so the tool is invisible when no Python
   runtime / workspace is configured.
4. **`BuiltInPluginDefaults`**: add a new well-known GUID
   (`10000000-0000-0000-0000-000000000007`) + `SyncPlugin` config with `handlerId":"execute_code"`
   and a `systemPromptAddition` paraphrasing the spec's "when to use" guidance.

This makes the tool uniform to `GetAllTools()` / `RouteToolCallAsync` with zero new dispatch path.

### 2.4 Invariant 8 → Pia dispatch (the cleanest reuse story)

Each `hermes_tools` RPC frame names a tool (`read_file`, `patch`, `terminal`, …). The
**`HermesRpcServer` must route each frame back through `PluginService.RouteToolCallAsync(toolCall)`
by tool name** — *not* call handlers directly — so path-safety, fuzzy patch, lint-delta, and loop
guards all apply automatically. This is the spec's "re-enter the real tools" expressed in Pia's
actual dispatch primitives, and it is the strongest argument for building those tools *first*.

> Today `RouteToolCallAsync(toolCall)` threads **no** session/task id (confirmed:
> [`ChatSession.cs`](../../../src/Pia.Wpf/ViewModels/Models/ChatSession.cs):412). See §3 task_id.

### 2.5 Design decision: per-write ActionCard vs script-level approval (CRITICAL FORK)

Spec **invariant 7** vets the *script* once and runs it "synchronously in the caller thread so it
holds the session's approval context." Pia's existing guard is **per-write**: one
`ActionCardInfo` + `WaitForUserDecisionAsync()` per mutation, blocking the UI thread.

These collide: a script that issues 50 proxied `write_file`/`patch`/`terminal` calls cannot raise
50 cards — the child is a blocking subprocess and per-RPC UI blocking deadlocks or is unusable.

**Recommended stance (to confirm with user — see security open question):**

- The script itself is gated by **one** approval before dispatch (the `terminal`-style dangerous-
  pattern guard, surfaced as a single `ActionCardInfo` showing the script + detected risks).
- **Approving the script grants blanket approval for the mutations it proxies during that run.**
  Proxied `write_file`/`patch`/`terminal` calls then auto-execute under that single approval (the
  RPC server calls `ExecutePendingActionAsync` directly instead of raising per-write cards).
- All proxied writes remain confined by `SafeFolderPath` / workspace root, and are logged.

This is the real architectural decision, not a footnote — it ties directly to the privacy-first
security model (open question 1).

---

## 3. Cross-cutting invariants (overview §"Cross-cutting design principles") → mapping

> `execute_code` mostly **inherits** these by proxying — the value of invariant 8. Where it owns
> behavior directly it is called out.

| Cross-cutting invariant | Owner | Pia status / plan |
|-------------------------|-------|-------------------|
| 1. Line-numbered reads (`LINE_NUM\|CONTENT`) | proxied `read_file` | **Inherited.** Pia's `read_file` has no line numbers yet → prerequisite gap |
| 2. Fuzzy matching on edits (9-strategy) | proxied `patch` | **Inherited.** `patch` does not exist → prerequisite |
| 3. Delta-filtered diagnostics | proxied `write_file`/`patch` | **Inherited.** prerequisite |
| 4. Loop / dedup guards | proxied `read_file`/`search_files` | **Inherited** via re-entry; plus `execute_code` adds its own **call-cap (~50)** as a coarse anti-thrash |
| 5. Staleness tracking (mtime) | proxied file tools | **Inherited.** prerequisite |
| 6. Return-a-diff + verify-by-reread | proxied edit tools | **Inherited.** prerequisite |
| 7. **head+tail truncation** | **owned** | `OutputCapper`: stdout ~50KB (≈40/60), stderr ~10KB head-only, mid-omission marker |
| 8. Pagination everywhere | proxied tools (`offset`/`limit`) | **Inherited** — sandbox stubs forward `offset`/`limit` |
| 9. **Atomic writes (temp+rename), preserve CRLF/BOM** | proxied `write_file`/`patch` | **Inherited.** Note: Pia `FilesToolHandler` currently does a direct `File.WriteAllText` (non-atomic) → prerequisite to fix in the file-tools plan |
| 10. **Self-healing arg validation** | **owned (partly)** | Validate `code` present & a string; emit corrective `{"error"}` if dropped. Per-stub arg repair lives in proxied tools |
| **`task_id`-keyed state** | **owned + infra** | Per-script state (timeout deadline, call-cap counter, `is_interrupted` flag, RPC session) keyed by `task_id` |

### 3.1 `task_id` threading (implement day one — spec is emphatic)

- **Confirmed gap:** `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent)` and
  `PluginService.RouteToolCallAsync(toolCall)` carry **no** session/task id today. `ChatSession.Id`
  exists at the session level but is never threaded into handlers.
- **Recommendation (affects `tool_registration` broadly):** extend the dispatch signature to carry
  `task_id` (default `"default"`), sourced from `ChatSession.Id`. `execute_code` keys its per-run
  state (timeout, call-cap, interrupt flag, RPC server instance, proxied-write approval grant) off
  it. This is what makes parallel/background subagent scripts safe — and Pia already has background
  + multi-assistant sessions, so the id is available; it just is not plumbed down.
- Retrofitting later is painful (spec). Do it when the core 7 land, before `execute_code`.

### 3.2 Privacy logging (must appear in the design, not be assumed)

- The `code` parameter and all proxied tool payloads are **sensitive** → log via
  `_logger.SensitiveDebug(...)`, never `LogInformation`. Stable, non-sensitive facts to log at
  Information: tool name, byte counts, exit code, call count, duration.
- Any proxied `web_search`/`web_extract` URL that is logged must be wrapped in `SafeUrl.Format(...)`.
- `OutputCapper` runs **secret redaction** before the result re-enters context or any log.

---

## 4. Build / implementation checklist

### 4.A External prerequisites (blockers — separate plans, build first)

- [ ] `read_file` (line-numbered, paginated) — replaces/extends `FilesToolHandler.read_file`
- [ ] `write_file` (atomic temp+rename, CRLF/BOM preserved, delta-lint)
- [ ] `patch` (fuzzy 9-strategy + V4A) — *highest leverage; budget most effort*
- [ ] `search_files` (ripgrep content + glob)
- [ ] `terminal` (foreground subprocess capture + **dangerous-command guard**) — the guard is
      reused by `execute_code`
- [ ] `web_search` / `web_extract` (or decide they are out of scope for the sandbox subset)
- [ ] **`task_id` threaded through dispatch** (`tool_registration` change)
- [ ] **Workspace-root scope** decided (sandbox vs new workspace root — open question 2)

### 4.B `execute_code`'s own net-new core (this tool)

- [ ] `IExecuteCodeToolHandler` + `ExecuteCodeToolHandler`; DI in `Bootstrapper`; `switch` arm in
      `PluginService.InitializeBuiltInPlugins`; `FromExecuteCodeHandler` factory; `BuiltInPluginDefaults` entry
- [ ] **`hermes_tools.py`** embedded resource: safe tool-subset stubs + `json_parse`/`shell_quote`/`retry`
- [ ] **RPC transport** — `HermesRpcServer`: loopback TCP on Windows, newline-delimited JSON frames,
      child connects via `HERMES_RPC_SOCKET` env var; (optional) file-based polling fallback for
      future sandboxes (50ms→250ms backoff)
- [ ] **RPC dispatch re-entry** — each frame → `PluginService.RouteToolCallAsync` by tool name (§2.4)
- [ ] **Env scrub** — `EnvScrubber`: deny `KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|WEBHOOK`, allow safe
      prefixes + declared passthrough + Windows OS essentials
- [ ] **project vs strict mode** — `PythonRuntimeLocator`: project=session cwd + active venv python;
      strict=isolated tmpdir + host python
- [ ] **Host-side limits** — per-script timeout (~300s) + max tool calls (~50), enforced on the
      *parent* RPC dispatcher (sandbox cannot bypass), keyed by `task_id`
- [ ] **Output capture** — `OutputCapper`: dual reader threads (deadlock-safe), stdout head+tail
      (~50KB), stderr head (~10KB), ANSI strip, secret redaction
- [ ] **Interruption** — `is_interrupted()` flag checked per RPC; on new user message kill child
      process tree (SIGTERM→grace→SIGKILL; Windows: `taskkill /T` or job-object kill)
- [ ] **Approval** — reuse `terminal`'s command guard to vet the script; single `ActionCardInfo`
      pre-dispatch; blanket-approve proxied mutations per the §2.5 decision
- [ ] **Dynamic per-session schema description** — `dynamic_schema_overrides`-style hook listing the
      currently-enabled sandbox tools + active mode + helpers, rebuilt each definitions pass
- [ ] **Privacy logging** — `SensitiveDebug` for `code`/payloads; `SafeUrl.Format` for web URLs;
      redaction in `OutputCapper`
- [ ] **Availability gating** — `IsAvailable=false` (tool hidden) when no Python runtime resolves or
      no workspace root configured

---

## 5. Test strategy (xunit.v3, matching the repo)

> Repo uses xunit.v3 + plain `Xunit.Assert` (no FluentAssertions), MTP via `global.json`; new files
> must be CRLF. Tests live in `tests/Pia.Wpf.Tests/`.

| Area | Test (no live Python / no real subprocess where avoidable) |
|------|------------------------------------------------------------|
| **EnvScrubber** | Pure unit: deny-list hits (`AWS_SECRET_KEY`, `GH_TOKEN`, `*_WEBHOOK`), allow-prefixes pass (`PATH`,`LC_ALL`,`PYTHONPATH`,`VIRTUAL_ENV`), declared passthrough honored, Windows essentials retained |
| **OutputCapper** | Pure unit: head+tail ratio (~40/60) + marker on >50KB stdout; stderr head-only ~10KB; ANSI escape stripping; secret redaction patterns; under-cap passthrough unchanged |
| **Dynamic schema** | Given a fake enabled-tool set + mode, the generated description lists exactly those sandbox tools + helpers; rebuilt when tool set changes |
| **Self-healing args** | Missing/non-string `code` → precise corrective `{"error"}`, never throws |
| **Availability gating** | No runtime resolved → `GetTools()` empty + system-prompt suppressed (mirror `FilesToolHandler` availability tests) |
| **RPC dispatch re-entry** | With a fake `IPluginService`, a stub RPC frame routes via `RouteToolCallAsync` by tool name; path-safety/guard verbs are exercised through the real handlers (not bypassed) |
| **Host-side limits** | Fake RPC server: 51st tool call rejected (cap=50); timeout deadline elapsed → child kill requested; both keyed by `task_id` so two `task_id`s don't share counters |
| **Approval / interruption** | Script vetting flags a dangerous pattern → single `ActionCardInfo` raised; decline → not dispatched; interrupt flag flips → kill path invoked. Use seams/fakes; do **not** drive the WPF UI (per repo memory: no winwright). |
| **task_id isolation** | Two concurrent fake runs with distinct `task_id` keep independent call-cap/interrupt state |

Integration (Python actually present) should be **opt-in / skippable** (e.g. skip when no runtime
resolves) so CI without Python stays green — consistent with availability-gated design.

---

## 6. Open questions (the cross-cutting forks)

1. **Code-execution security model (privacy-first).** Is arbitrary Python/command execution even in
   scope for a privacy-first desktop assistant? If yes, is the single pre-dispatch script approval
   (§2.5) sufficient, or is a stronger consent model required (e.g. global feature flag +
   per-session opt-in + workspace pinning)? **Decision needed before any build** — it gates the
   whole tool.
2. **Filesystem scope.** `FilesToolHandler` is gated on a single configured **sandbox folder**;
   coding scripts want **repo/workspace-wide** access. Expand the sandbox or introduce a distinct
   **workspace root**? How does the chosen root interact with `SafeFolderPath` and the privacy-
   logging rules (paths under `SensitiveDebug`, URLs under `SafeUrl`)?
3. **Native vs MCP delegation.** Could `terminal`/`search_files` (and thus much of what
   `execute_code` proxies) be delivered by an existing shell/filesystem **MCP server** via
   `McpPluginToolHandler`/`StdioClientTransport` instead of built natively? Note that the MCP path
   spawns **external** stdio servers (integrate-new), and `execute_code`'s RPC server is the host
   acting as an RPC *server* to its own child — a different shape. Build-vs-integrate is a real fork
   for the prerequisites; it does not by itself rescue `execute_code` into "reuse".
4. **`task_id` threading.** Confirmed absent from dispatch. Plumb `ChatSession.Id` →
   `RouteToolCallAsync` → handlers as `task_id` (default `"default"`). Belongs in
   `tool_registration` and affects every tool. Do it day one.
5. **Extend vs rebuild `FilesToolHandler`.** The proxied `read_file`/`write_file` need line numbers,
   pagination, fuzzy matching, atomic writes — none of which the current sandbox handler has.
   Extend in place (risk regressing current sandbox UX) or build a richer coding-file toolset
   alongside and have `execute_code` proxy that one?
6. **Python runtime.** Ship a CPython runtime (heavy — conflicts with the user's
   minimal-dependencies preference), depend on **system Python** (then `IsAvailable=false` when
   absent — leans on the availability-gating pattern), or reframe to **C# scripting**
   (no `hermes_tools`/Python contract, larger spec deviation)? This also bears on NOT pulling a
   heavy diff library for the prerequisite `patch`.
7. **Web tools in the sandbox subset.** `web_search`/`web_extract` are in the `hermes_tools` API but
   may be out of scope for Pia's coding posture. Decide whether to expose them or trim the stub.
