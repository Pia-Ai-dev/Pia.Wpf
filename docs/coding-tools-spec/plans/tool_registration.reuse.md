# Plan: `tool_registration` — the host tool layer (REUSE / MODIFICATION INSTRUCTIONS)

> **Classification: `reuse`.** Pia already ships a working, load-bearing host tool layer
> (registry + dispatch + adapter + MCP nuke-and-repave + a write-approval guard). This plan extends it
> *in place* rather than rebuilding. The registry is already depended on by memory/todo/reminder/files/
> scheduled-research/research-history/MCP — a rebuild would be reckless.
>
> **Scope of this doc:** host-side infrastructure only. `tool_registration` is **NOT a model-callable
> tool** — there is no `input_schema` and the model never calls it. We describe the *host contract* and
> the precise edits to satisfy it. Per-tool specifics (read_file offset/limit, terminal command shape,
> patch fuzzy-matching, execute_code runtime) live in sibling docs and are referenced here only as
> *contracts this layer depends on or gates*.

---

## 0. TL;DR — what to build, in ripple order

Ordered so each step changes the fewest things the next step depends on:

1. **`task_id` / per-session context threading** (touches a core interface every handler implements — do it day one, per spec).
2. **Registry metadata** — a `ToolEntry`-equivalent (toolset, limits, check_fn, dynamic overrides), anti-shadow guard, `deregister` toolset cleanup, generation counter.
3. **Definition generation** — `check_fn` ~30s cache + per-pass memoization, dynamic schema overrides, definitions memo, **schema sanitizer** (6 hostile shapes).
4. **Dispatch hardening** — uniform `{"error": ...}` envelope, handler-exception catch, error-string sanitization.
5. **Output budgeting** — net-new 3-layer subsystem (per-tool self-truncation, per-result persistence, per-turn aggregate). *Biggest build.*
6. **Approval guard** — reuse `ActionCardInfo` blocking mechanism; add net-new decision logic (hardline + `DANGEROUS_PATTERNS`, normalization, session-scoped remembered approvals).

Cross-cutting product forks (code-exec consent, workspace scope, native-vs-MCP, Python runtime) are surfaced in §8 and carried into open questions — they are genuine decisions, not silently chosen.

---

## 1. The spec contract (host responsibilities)

Five host-side responsibilities. None is model-callable.

| # | Responsibility | Spec shape |
|---|----------------|-----------|
| R1 | **Registry** | One `ToolEntry` per tool: `name`, `toolset`, `schema`, `handler`, `check_fn?`, `requires_env`, `is_async`, `description`, `emoji?`, `max_result_size_chars?`, `dynamic_schema_overrides?`. Module-level self-registration; anti-shadow guard (`override=True` opt-in); `deregister` with toolset cleanup; `_generation` counter; thread-safe. |
| R2 | **Definition generation** | `get_definitions(tool_names) -> [provider schema]`: per-name availability gate via `check_fn` (cached ~30s + per-pass memo); force `schema["name"]`; apply `dynamic_schema_overrides`; emit provider shape; **sanitize**. Memoize the whole list on `(enabled tools, config mtime+size, generation)`. |
| R3 | **Dispatch** | `dispatch(name, args, **ctx) -> str`: unknown tool → `{"error": "Unknown tool: ..."}`; handler exception → caught, logged, `{"error": sanitize(...)}`; **never raises into the agent loop**. Thread `task_id` (+ session/observability ctx) into every handler. Async bridging. Error-string sanitization. |
| R4 | **Output budgeting** | 3 layers. Defaults: per-result 100K chars, per-turn 200K chars, preview 1.5K chars. L1 per-tool self-truncation; L2 per-result persistence to a temp file with `<persisted-output>` block (size + path + preview, model `read_file`s it back); L3 per-turn aggregate spill of largest non-persisted results. Always head+tail (or "truncate at last newline"), never blind head-only. Pinned thresholds map (`inf` = never persist). |
| R5 | **Approval guard** | `terminal`/`execute_code` route command/script through a guard **before** execution: `detect_hardline_command` (always blocked, cannot be approved away), `detect_dangerous_command` (~47 `DANGEROUS_PATTERNS` → ask), normalization before matching, session-scoped remembered approvals (keyed by a session key), sudo-stdin guard. Allow/deny/ask decision. `clarify` is NOT this channel. |

`get_definitions` and `dispatch` are the two host entrypoints. `max_result_size_chars` (R1) feeds budgeting L2 (R4); `task_id` threading (R3) scopes all per-session state.

---

## 2. What already exists in Pia (verified against source)

| Spec piece | Existing Pia code | What it does today |
|------------|-------------------|--------------------|
| Registry map | `PluginService._handlers : Dictionary<Guid, IPluginToolHandler>` + `_toolNameRoutes : Dictionary<string, IPluginToolHandler>` (`PluginService.cs:28-29`) | Lock-guarded (`lock (_handlers)`) plugin-id → handler and tool-name → handler routing. |
| register / deregister | `RegisterHandler` (`:196`), `UnregisterHandler` (`:209`), `RebuildToolNameRoutes` (`:631`) | Adds/removes a handler and its tool-name routes. Overwrites silently on name clash. |
| Definition generation | `PluginService.GetAllTools()` (`:222`) → each handler's `GetTools()` | Aggregates `AITool[]` from enabled handlers (`IsPluginEnabled`). No sanitization, no dynamic overrides, no memo. |
| Availability gate (`check_fn` analogue) | `BuiltInPluginHandler._isAvailable : Func<bool>?` (`:18,42`); `FromFilesHandler(..., isAvailable: () => handler.IsAvailable)` (`:201`) | Files plugin's tools + system prompt are suppressed when the sandbox folder is unconfigured. Called every pass, **uncached**. |
| Dispatch | `PluginService.RouteToolCallAsync(FunctionCallContent, CancellationToken)` (`:265`) | Tool-name → handler → `HandleToolCallAsync`. Returns `null` on unknown tool. No error envelope; handler exceptions propagate. |
| Tool loop | `AiClientService.GetChatCompletionWithToolsAsync` (multi-round, `maxToolRounds`) | Detects `FunctionCallContent`, invokes the handler callback, wraps the **full** result in `FunctionResultContent` at `AiClientService.cs:347`. |
| MCP nuke-and-repave | `McpPluginToolHandler` + `PluginService.ApplyServerPluginsAsync` (`:291`), `HandleNewServerPluginAsync` (`:343`) | Upsert/delete/refresh; preflight (`CheckCommandOnPathAsync`, `CheckNodeVersionAsync`, `PingUrlAsync`); cab extraction; `RebuildToolNameRoutes`. |
| Handler contract | `IPluginToolHandler` (`HandleToolCallAsync`, `ExecutePendingActionAsync`, `GetTools`, `GetSystemPromptAddition`, `Initialize/Shutdown`, `ApplyServerMetadata`) | The interface every handler implements. **Carries no session/task id.** |
| Adapter | `BuiltInPluginHandler` + 6 `From*Handler` factories | Wraps domain handlers; maps their pending-action records into `PluginToolCall`. |
| Approval guard (write ops) | `ChatSession.HandleToolCall` (`:404`) + `ActionCardInfo.WaitForUserDecisionAsync` (`ActionCardInfo.cs:64`) + `IActionCardBuilder` | Pending write ops (`pendingAction != null`) build an `ActionCardInfo`, block the turn in `ChatState.WaitingForTool`, execute on Accept, return a decline string on Decline. |
| Sandbox file tools | `FilesToolHandler` + `SafeFolderPath.TryResolveInside` (`SafeFolderPath.cs:18`) | `list/read/write/delete_file` gated to `AppSettings.AssistantFilesFolder`. Read cap 256 KB, write cap 512K chars, list cap 500. Rejects rooted/UNC/`..`/invalid-char paths. |
| Privacy logging | `SafeLog.SensitiveDebug/...` (`[Conditional("DEBUG")]`), `SafeUrl.Format` | Strips payloads/URLs from RELEASE IL; already used across `PluginService`/`McpPluginToolHandler`. |
| Per-session ambient state (the *only* one) | `TokenMapAmbient.Current` (AsyncLocal) set in `ChatSession.RunTurnAsync` (`:201`) | PII namespace isolation per turn. The pattern to mirror for `task_id`, but it carries PII map, not a task id. |

**Decoy alert:** the only "truncation" today — `Truncate(resultPreview, 500)` at `AiClientService.cs:343-344` and the `TruncateText(..., 500)` in `McpPluginToolHandler` — are **logging-only**. The real result string flows untrimmed into `FunctionResultContent` at `AiClientService.cs:347`. There is **no** output budgeting.

---

## 3. Gap analysis (spec requirement → current behavior → needed change)

| Spec requirement | Current Pia behavior | Needed change | Build size |
|------------------|----------------------|---------------|-----------|
| **R3** `task_id`/ctx into every handler | `HandleToolCallAsync(FunctionCallContent, CancellationToken)` — no id. `ChatSession.Id` exists but isn't threaded. Only PII map flows (AsyncLocal). | Add a `ToolCallContext` param (carries `TaskId` + session/observability) to the interface; thread from `ChatSession` → `RouteToolCallAsync` → all handlers + 6 factories + MCP. **Do first.** | Medium (wide, shallow) |
| **R1** `ToolEntry` record (toolset, limits, check_fn, overrides) | Just `AITool` list + tool-name→handler dict. No per-tool metadata. | Introduce a `ToolEntry`-equivalent registered alongside each tool (toolset key, `MaxResultSizeChars`, `check_fn`, `dynamic_schema_overrides`). | Medium |
| **R1** Anti-shadow guard / `override` opt-in | `RegisterHandler`/`RebuildToolNameRoutes` overwrite `_toolNameRoutes[name]` silently. | Reject a name registered under a *different* toolset unless `override=true` (logged at INFO). Allow MCP→MCP overwrite (server refresh). | Small |
| **R1** `deregister` with toolset cleanup | `UnregisterHandler` removes routes only. | On removing the last tool of a toolset, drop the toolset's `check_fn`/aliases. | Small |
| **R1** Generation counter | None. | Bump `_generation` on every register/deregister so the definitions memo can invalidate cheaply. | Small |
| **R1** Module-level self-registration / AST auto-discovery | Explicit DI + `InitializeBuiltInPlugins` switch (`PluginService.cs:79-91`). | **Do NOT port the Python AST/import-side-effect idiom** (see §5, gotcha 3). Keep explicit registration, or add assembly-scan / a registration attribute. | n/a (decision) |
| **R2** `check_fn` ~30s cache + per-pass memo | `_isAvailable()` called every `GetTools()`/`GetAllTools()`, uncached. | Cache `check_fn` results ~30s (TTL short enough that "enable foo" is near-real-time) + memoize within one definitions pass. | Small |
| **R2** `dynamic_schema_overrides` | None. No way to inject live config (e.g. max concurrent children, enabled sandbox tools) into a description at definition time. | Add an optional per-entry override fn merged over the static schema; try/except → fall back to static. | Small |
| **R2** Definitions memo | `GetAllTools` rebuilds every call. | Memoize keyed on `(enabled tools, config mtime+size, generation)`. | Small |
| **R2 / §5** Schema sanitizer (6 hostile shapes) | Schemas pass raw from `GetTools()`/`AIFunctionFactory` straight to the provider. | Deep-copy walk fixing: object-without-properties; bare-string `"object"`; array `type`; `anyOf`/`oneOf` null unions; `$ref` siblings. Run once per pass, after overrides, before request. | Medium |
| **R3** Uniform `{"error"}` envelope | Unknown tool → `RouteToolCallAsync` returns `null` → `ChatSession` returns the string `"Unknown tool."`. Handler exceptions propagate up the tool loop. | Return `{"error": "Unknown tool: <name>"}`; wrap handler exceptions in `{"error": sanitize(...)}` at the dispatch boundary; never raise into the loop. | Small |
| **R3** Error-string sanitization | None. | Run exception strings through the same sanitizer (strip framing tokens/code fences/CDATA) so errors don't reach the model as structural noise. | Small |
| **R4 L1** Per-tool self-truncation | Files tool has size caps (256K/512K/500) but they reject, not truncate-with-marker. Terminal/process/search_files don't exist yet. | Centralize knobs (`max_bytes`/`max_lines`/`max_line_length`, defaults 50000/2000/2000); each tool caps its own output head+tail with a marker. | Medium (per-tool, future) |
| **R4 L2** Per-result persistence | **Absent.** Full result → `FunctionResultContent` (`AiClientService.cs:347`). | If a result exceeds `GetMaxResultSize(tool)`, write full output to a temp file and replace context content with a `<persisted-output>` block (size + path + 1.5K preview at last newline). | **Large (net-new)** |
| **R4 L3** Per-turn aggregate budget | **Absent.** | After all tool results in a turn are collected (after the `foreach` over `toolCalls`), if combined > 200K, spill largest non-persisted to disk until under budget. | Large (net-new) |
| **R5** Hardline detector | None. | `detect_hardline_command` — always-blocked patterns (fork bombs, disk wipes), not approvable. | Medium |
| **R5** Dangerous-pattern detector | Only generic write-confirmation (`pendingAction != null`), heuristic `toolName.Contains("delete")`-style. | Compiled `DANGEROUS_PATTERNS` regex list (~47) → `(matched, description)` → ask. Compile once at startup. | Medium |
| **R5** Normalization before matching | None. | Resolve `$HOME`/home rewrites, collapse whitespace, before regex. | Small |
| **R5** Session-scoped remembered approvals | None — every write op re-prompts. | Remember approved patterns per session, keyed on `task_id` (ties to §5 gotcha 1). Held synchronously during dispatch. | Medium |
| **R5** Sudo-stdin guard | n/a (no terminal yet). | Special-case piping passwords into `sudo -S`. | Small (with terminal) |

---

## 4. Modification instructions (ordered, concrete, no C#)

### Step 1 — `task_id` / per-session context threading (do first)

Why first: it changes a signature every handler implements and every dispatch site calls. Retrofitting later means touching all the same files again plus any net-new tools.

- [ ] Define a small context carrier (e.g. `ToolCallContext { string TaskId; Guid? ChatId; /* room for observability */ }`). `TaskId` defaults to `"default"` (spec default).
- [ ] **Mint a stable runtime id at `ChatSession` construction** — do **not** reuse `ChatSession.Id`. `Id` is `Guid?`, assigned late (only on first persist via `SetIdentity`, `ChatSession.cs:102`). Using it raw gives null/changing keys and silently aliases per-task state to `"default"`. Mint a `Guid.NewGuid()`-style runtime id in the ctor; expose it as the `TaskId`. (See §5 gotcha 1.)
- [ ] Extend `IPluginToolHandler.HandleToolCallAsync` to accept the context. Thread it through:
  - `PluginService.RouteToolCallAsync` (`:265`)
  - all 6 `BuiltInPluginHandler.From*Handler` factories + the delegate field types (`BuiltInPluginHandler.cs:16-17`)
  - `McpPluginToolHandler.HandleToolCallAsync` (`:83`)
  - call sites `ChatSession.HandleToolCall` / `HandleToolCallWithStatus` (`:396-412`)
- [ ] Optionally also flow it as an AsyncLocal (mirroring `TokenMapAmbient`, `ChatSession.cs:201`) so deep helpers (budgeting persistence path, approval-guard session key) can read it without re-threading. Prefer the explicit param on the handler boundary; use AsyncLocal only for cross-cutting helpers.
- [ ] Per-task state that will key off `TaskId` later: read-dedup, cwd, mtime tracking, process registry, remembered approvals (R5). This step just makes the key available.

### Step 2 — Registry metadata, anti-shadow, deregister cleanup, generation counter

- [ ] Add a `ToolEntry`-equivalent recorded per tool at registration: `Name`, `Toolset`, `MaxResultSizeChars?` (file/terminal use 100_000), `CheckFn?`, `DynamicSchemaOverrides?`. Two reasonable shapes — pick in review:
  - **(a)** A parallel `Dictionary<string, ToolEntry>` in `PluginService` populated in `RegisterHandler`/`RebuildToolNameRoutes`; or
  - **(b)** Extend `IPluginToolHandler` to expose per-tool metadata so `GetAllTools` can read it. Lighter touch on built-ins is (a) — they already carry availability via the adapter's `_isAvailable`.
- [ ] **Anti-shadow guard** in the route-building path (`RegisterHandler` `:196`, `RebuildToolNameRoutes` `:631`): if a tool name is already routed under a *different* toolset, reject unless an explicit `override` flag is set (log at INFO). Allow MCP-toolset → MCP-toolset overwrites (server refresh). Today registration overwrites silently — this is the change.
- [ ] **`deregister` cleanup**: in `UnregisterHandler` (`:209`), when removing the last tool of a toolset, also drop that toolset's `check_fn`/aliases.
- [ ] **Generation counter**: add `_generation` (int), bump under `lock (_handlers)` on every register/deregister/rebuild. Feeds the definitions memo (Step 3).

### Step 3 — Definition generation: check_fn cache, dynamic overrides, memo, sanitizer

- [ ] **`check_fn` cache**: wrap availability checks (the `_isAvailable` analogue) in a ~30s TTL cache + per-pass memoization (same `check_fn` may back many tools). Today `_isAvailable()` runs every `GetTools()`. Cheap for the files-folder check, but mandatory once `terminal`'s check probes a process/binary.
- [ ] **Dynamic schema overrides**: add an optional per-entry fn returning a partial schema dict, merged over the static schema at definition time, wrapped in try/except → static fallback on error. Use cases the spec names: `delegate_task` description showing current `max_concurrent_children`/`max_spawn_depth`; `execute_code` listing currently-enabled sandbox tools.
- [ ] **Definitions memo**: memoize the assembled tool/definition list keyed on `(enabled tool names, config mtime+size, _generation)`. `GetAllTools` (`:222`) rebuilds on every call today.
- [ ] **Schema sanitizer (§5 of spec — 6 hostile shapes)**: a deep-copy tree walk run once per definitions pass, *after* overrides, *before* the request. Fix in-place, conservatively:
  - `{"type":"object"}` with no `properties` → add `properties: {}` (handle unconstrained `additionalProperties`).
  - bare string `"object"` where a dict is expected → fix.
  - array `type` like `["string","null"]` → collapse to the non-null branch.
  - `anyOf`/`oneOf` nullable unions → collapse to the non-null branch.
  - sibling keywords next to `$ref` (e.g. `{"$ref":..., "default":null}`) → strip the siblings.
  - Most relevant to **MCP** tools (`McpPluginToolHandler.GetTools` returns provider schemas verbatim). One bad MCP schema can 400 the whole request for strict/local backends.

### Step 4 — Dispatch hardening

- [ ] **Uniform error envelope**: `RouteToolCallAsync` should not return bare `null` for an unknown tool. Return a structured `{"error": "Unknown tool: <name>"}`. Update `ChatSession.HandleToolCall` (`:413-417`) to surface the envelope instead of the literal `"Unknown tool."`.
- [ ] **Catch handler exceptions at the dispatch boundary** and convert to `{"error": sanitize("Tool execution failed: <Type>: <msg>")}`. Today handler exceptions propagate up the tool loop. `McpPluginToolHandler` already catches and returns a string (`:120-125`) — normalize it to the same envelope.
- [ ] **Error-string sanitization**: run exception/error strings through the §3 sanitizer (strip framing tokens / code fences / CDATA) — prompt-injection and parser-confusion hygiene.
- [ ] **Async bridging**: Pia is already async end-to-end (`Task<...>`); no Python-style loop-bridge needed. Note in review.

### Step 5 — Output budgeting (net-new subsystem; biggest build)

Insertion points are precise — name them in the implementation:

- **Layer 2 (per-result persistence)** inserts between `AiClientService.cs:341` (`var result = await toolHandler(toolCall)`) and `:347` (the `FunctionResultContent` wrap). After the handler returns, if `result.Length > registry.GetMaxResultSize(toolName)` (default 100K), write the **full** output to a temp file and substitute a `<persisted-output>` block (size, file path, 1.5K preview truncated at the last newline within max). The model then `read_file`s the path with offset/limit. Fall back to inline truncation if the write fails.
- **Layer 1 (per-tool self-truncation)**: each tool caps its own output *before* returning. Centralize knobs (`max_bytes`/`max_lines`/`max_line_length`, defaults 50000/2000/2000) so they're tunable without patching tools. Applies to future `read_file`/`terminal`/`search_files`; the existing `FilesToolHandler` caps (256K/512K/500) are *rejections*, not truncate-with-marker — reconcile when the coding read_file lands.
- **Layer 3 (per-turn aggregate)**: after the `foreach (var toolCall in toolCalls)` loop in `GetChatCompletionWithToolsAsync` (around `:338-349`) collects all results for the turn, if combined size > 200K, spill the largest non-persisted results to disk (same persistence path) until under budget.
- Always truncate **head+tail** (or "truncate at last newline within max"), never blind head-only — the tail usually carries the error/result. Keep a clear truncation marker so the model knows to page.
- Honor a **`PINNED_THRESHOLDS`** map (`inf` = never persist) for tools whose output must stay inline.
- Optional context-window auto-scaling: `window_chars = context_length * 4`; per-result = 15% clamped `[8_000, 100_000]`, per-turn = 30% clamped `[16_000, 200_000]`.

**Hard dependency — the persisted path must be readable (see §5 gotcha 2).** The spec's "model `read_file`s the persisted file" collides with Pia's sandboxed `read_file`: `SafeFolderPath.TryResolveInside` (`SafeFolderPath.cs:27`) rejects rooted/absolute paths, so a file in `%LOCALAPPDATA%\Pia\…` is **unreadable** by the current files tool. This layer therefore depends on:
- **(a)** the *coding* `read_file` gaining `offset`/`limit`, and
- **(b)** that read_file being able to read the persisted path (either it lives inside the resolved workspace root, or the coding file tools allow a designated results dir).

This doc states the **contract required of read_file**; it does **not** spec read_file (sibling doc). It chains directly into cross-cutting Q2/Q5 (§8).

**Privacy (mandatory, not afterthought):** the persisted-results directory is a new on-disk **payload artifact**. CLAUDE.md notes users attach `%LOCALAPPDATA%\Pia\Logs\` to support; a results dir holds raw tool output.
- Place it deliberately (e.g. `%LOCALAPPDATA%\Pia\ToolResults\<taskId>\<callId>.txt`) and document retention: **delete on turn end, and on session end/reap** (`ChatSessionManager.ReapStaleSessions`).
- Log only the call id + byte count via `LogInformation`. Any path goes through `SafeUrl.Format` or `SensitiveDebug`; any preview/payload via `SensitiveDebug`. Never log raw output at info level.

### Step 6 — Approval guard (reuse blocking, add decision logic)

**Reuse:** the blocking/UI mechanism — `ActionCardInfo` + `WaitForUserDecisionAsync` (`ActionCardInfo.cs:64`) + `ChatState.WaitingForTool` + `IActionCardBuilder`. The turn-blocking pattern in `ChatSession.HandleToolCall` (`:435-484`) is exactly the gate to re-use.

**Add (net-new decision logic), running *before* execution for `terminal`/`execute_code`:**
- [ ] `detect_hardline_command(cmd)` — always-blocked patterns (fork bombs, disk wipes). Cannot be approved away; returns an error envelope, no card shown.
- [ ] `detect_dangerous_command(cmd)` — compiled `DANGEROUS_PATTERNS` regex list (~47: `rm -rf`, `chmod -R 777`, `curl | sh`, …) → `(matched, description)` → ask via the action card. Compile once at startup (hot path).
- [ ] **Normalization before matching** — resolve `$HOME`/home rewrites, collapse whitespace, so trivial obfuscation doesn't slip patterns.
- [ ] **Session-scoped remembered approvals** — once the user approves a pattern, remember it for the session, keyed on `TaskId` (Step 1; §5 gotcha 1). Held synchronously during dispatch (matters for `execute_code` running in the caller thread). Optional "yolo"/auto-approve mode.
- [ ] **Sudo-stdin guard** — special-case piping passwords into `sudo -S` (ships with terminal).
- [ ] A new approval category for command/code: `ActionCardCategory` currently has only `Memory/Todo/Reminder/Files` (`ActionCardInfo.cs:14-20`). A `Terminal`/`CodeExecution` value is needed. **This doc only specifies it; the edit happens in implementation.**
- [ ] `clarify` is NOT this channel — the guard owns command confirmation.

---

## 5. Four sharp gotchas (each verified)

1. **`ChatSession.Id` is the wrong `task_id`.** It is `Guid?` and assigned late (only on first persist, `ChatSession.cs:102`). Using it raw yields null/changing keys → per-task state (read-dedup, cwd, mtime, process registry, remembered approvals) silently aliases to `"default"` and cross-talks between sessions. **Mint a stable runtime id at ctor**, independent of the persisted Guid.

2. **Persisted-output (budget L2) collides with the sandboxed `read_file`.** Spec: write full output to a temp file, model reads it back. Pia's `read_file` (`FilesToolHandler` + `SafeFolderPath.TryResolveInside`, `SafeFolderPath.cs:18-49`) rejects absolute/rooted paths — a `%LOCALAPPDATA%` results file is unreadable. L2 has a hard dependency on (a) the coding read_file gaining offset/limit and (b) a readable persisted path. State that contract here; don't spec read_file. Chains to Q2/Q5.

3. **AST module-level self-registration does not map to C#.** "Drop a file in `tools/`, AST-scan for `register()`, import it" is a Python import-side-effect idiom. Do **not** port it literally. The C# equivalents: keep explicit DI + `InitializeBuiltInPlugins` (already there), or add assembly-scan / a `[ToolPack]` registration attribute. Pick in review; default to explicit registration (least magic, matches the existing pattern and Marco's minimal-deps preference).

4. **The persisted-results dir is a new privacy surface.** Users attach `%LOCALAPPDATA%\Pia\Logs\` to support; a results dir holds raw payloads. Bake retention (delete on turn/session end) and `SensitiveDebug`/`SafeUrl` logging into the design, not as cleanup later.

---

## 6. Regression risks to the existing sandbox / UX

- **Signature change to `IPluginToolHandler.HandleToolCallAsync` (Step 1)** touches every handler + 6 adapter factories + MCP + `ChatSession` call sites. A missed call site is a compile error (good) — but the *behavioral* risk is passing `"default"`/null where a real `TaskId` was intended, re-introducing gotcha 1. Verify the runtime id flows end-to-end, not just that it compiles.
- **Anti-shadow guard (Step 2)** changes register semantics from "last writer wins" to "reject on cross-toolset clash." If any built-in/MCP currently *relies* on silent overwrite (e.g. an MCP server exposing a `read_file` that today shadows the files plugin), it will now be rejected. Audit current tool-name overlaps before enabling; keep MCP→MCP refresh allowed.
- **Schema sanitizer (Step 3)** mutates schemas before sending. Over-aggressive collapsing of `anyOf`/array-type could change a legitimately-optional parameter's contract and break a working built-in/MCP tool. Deep-copy (never mutate the source `AITool`), fix only the 6 named shapes, and log at debug when a fix is applied.
- **`check_fn` ~30s cache (Step 3)** introduces staleness: toggling the files sandbox folder (or enabling `terminal`) may take up to ~30s to reflect in the tool list. The files-folder flow currently updates synchronously via `SettingsChanged → RebuildToolNameRoutes` (`PluginService.cs:70`). Keep an explicit cache-bust on `SettingsChanged` so the existing instant-reflect UX for the files folder is preserved.
- **Budgeting L2/L3 (Step 5)** changes what the model sees: large results become `<persisted-output>` stubs. If a built-in (e.g. memory `query_memory`, research-history search) returns a big-but-coherent blob the model used to consume inline, persistence could degrade answer quality. Mitigate with `PINNED_THRESHOLDS` (`inf`) for built-ins whose output is meant to stay inline, and tune the 100K default conservatively.
- **Dispatch error envelope (Step 4)** changes the decline/unknown string the model receives (`"Unknown tool."` → `{"error": ...}`). Prompts/tests asserting the old text need updating. The existing decline message ("User declined … Do not retry …", `ChatSession.cs:483`) is deliberate UX — keep it as-is; the envelope change is for *errors*, not user declines.
- **Approval guard (Step 6)** must not double-prompt: write ops already show an action card. A `terminal`/`execute_code` guard that *also* routes through a card needs a single decision path, not two stacked confirmations. Session-scoped remembered approvals reduce prompt fatigue but must be cleared on session reap (don't leak an approval across a reused session id — another reason gotcha 1 matters).
- **MCP result normalization (Step 4):** `McpPluginToolHandler` already returns error strings; converting to the uniform envelope must not break the existing `"Tool '<name>' not found"` / `"Tool call failed: <msg>"` paths that downstream code or prompts may key on.

---

## 7. Privacy-logging compliance checklist (CLAUDE.md)

- [ ] Tool args, tool results, command strings, scripts → `SensitiveDebug` only (already the pattern in `PluginService`/`McpPluginToolHandler`/`AiClientService`).
- [ ] Persisted-output file paths → `SafeUrl.Format` or `SensitiveDebug`; never raw at info level. Byte counts + call ids are fine at `LogInformation`.
- [ ] `DANGEROUS_PATTERNS` matches: log the *pattern description* (safe) at info; log the *normalized command* only via `SensitiveDebug`.
- [ ] Env-var gating (`requires_env`, `check_fn` probing env): log the var *name*, never the *value*.
- [ ] `Pia.Logging` may be imported by the registry/dispatch layer (it's not part of `Pia.Infrastructure`).

---

## 8. Cross-cutting forks (recommendations + dependencies; unresolved ones → open questions)

1. **Code-execution security model (Q1).** Arbitrary `terminal`/`execute_code`/`process` on the user's machine is the sharpest tension with "privacy-first." **Recommendation:** default-deny + explicit per-feature opt-in (Settings flag, like the files sandbox folder gates files), *then* the R5 approval guard (hardline block + dangerous-pattern ask + session-remembered approvals) on top of the existing card. The current write-confirmation card alone is **not sufficient** — it's a per-call yes/no with no pattern-level block and no "this is never allowed" tier. **Open:** is arbitrary code-exec even in scope for a privacy-first desktop assistant, or should the first cut ship `terminal` only (no `execute_code`) behind a default-off flag?

2. **Filesystem scope (Q2).** Coding tools need repo/workspace-wide access; `FilesToolHandler` is gated on one configured sandbox folder. **Recommendation:** introduce a **"workspace root"** concept (a separate, explicitly-chosen root for coding tools) rather than widening the existing `AssistantFilesFolder` sandbox — keeps the simple files-tool UX intact and avoids regressing its rejection rules. The workspace root reuses `SafeFolderPath.TryResolveInside` semantics. Interacts with privacy logging: workspace-relative paths are user-named items → `SensitiveDebug`/`SafeUrl`. **Open:** one workspace root or many; is it per-session (keyed on `TaskId`) or app-global?

3. **Native vs MCP delegation (Q3).** `terminal`/`process`/`search_files` could be delivered by an existing shell/filesystem MCP server (Pia already has `McpPluginToolHandler`, preflight, cab extraction). **Recommendation:** build the *registration/dispatch/budgeting/approval* layer natively (this doc) regardless; for the *tools themselves*, prefer integrating a vetted MCP server where one exists to honor minimal-deps — but route its commands through the **native approval guard** (don't trust an external server to gate dangerous commands). **Open:** which tools are MCP-delegated vs native, and can the approval guard intercept an MCP server's command before it executes (the guard runs host-side; MCP executes in the server process — interception point is unclear)?

4. **`task_id` threading (Q4).** Pia dispatch threads **no** session/task id today (only PII via AsyncLocal). It has background-chats + multi-assistant sessions, so a per-session id is natural — but `ChatSession.Id` is the wrong one (gotcha 1). **Recommendation (firm):** mint a stable runtime id at `ChatSession` ctor and thread it as `TaskId`; per-task state keys off it. Belongs in `tool_registration`, day one. *Not open* — this is decided.

5. **Extend vs rebuild `FilesToolHandler` (Q5).** Existing `read_file`/`write_file` share names with the spec but are simpler (no line numbers, pagination, fuzzy matching). **Recommendation:** build a **richer coding-file toolset alongside** the existing files plugin rather than extending it in place — extending risks regressing the current sandbox UX (the action-card write flow, the 256K/512K caps, the folder-not-configured messaging) that real users depend on. The coding toolset is workspace-rooted (Q2) and supplies the offset/limit `read_file` that budget L2 (§5 gotcha 2) depends on. Two `read_file`s with the same name → the anti-shadow guard (Step 2) must be reconciled (different toolsets; the coding one is opt-in/workspace-gated). **Open:** do the two file toolsets coexist (gated so only one is active per context) or does the coding toolset supersede the files plugin when a workspace is configured?

6. **Python runtime for `execute_code` (Q6).** **Recommendation:** do **not** bundle a Python runtime (heavy dependency — against Marco's minimal-deps preference) and do **not** silently depend on system Python (fragile, privacy/consent surface). Reframe toward **C# scripting** (e.g. Roslyn-based) or scope the first cut to `terminal` only and treat `execute_code` as a later, opt-in feature. The same minimal-deps lens applies to **`patch`**: prefer a hand-rolled diff/apply over pulling a heavy diff library. **Open:** is `execute_code` in scope at all for v1; if yes, C# scripting vs system-Python-if-present vs bundled runtime?

---

## 9. Implementation checklist (mirrors spec §"Implementation checklist", mapped to Pia)

- [ ] **Step 1** `ToolCallContext`/`TaskId` threaded through `IPluginToolHandler` → `RouteToolCallAsync` → adapters/MCP → `ChatSession`. Stable runtime id minted at ctor.
- [ ] **Step 2** `ToolEntry`-equivalent metadata; anti-shadow guard (`override` opt-in, MCP→MCP allowed); `deregister` toolset cleanup; `_generation` counter.
- [ ] **Step 3** `check_fn` ~30s cache + per-pass memo; `dynamic_schema_overrides`; definitions memo `(tools, config mtime+size, generation)`; schema sanitizer (6 shapes).
- [ ] **Step 4** Uniform `{"error"}` envelope; handler-exception catch; error-string sanitization.
- [ ] **Step 5** Budget L1/L2/L3 (100K/200K/1.5K); persistence over a readable path; `PINNED_THRESHOLDS`; head+tail truncation marker; retention + privacy logging.
- [ ] **Step 6** Approval guard: hardline + `DANGEROUS_PATTERNS`, normalization, session-scoped approvals keyed on `TaskId`; new `ActionCardCategory`; reuse `WaitForUserDecisionAsync`.
- [ ] Resolve the §8 forks before building `terminal`/`execute_code`/coding-file tools.

## Related
- Budgeting L2's `read_file`-back contract → sibling `read_file` doc (offset/limit + readable persisted path).
- Approval guard gates → sibling `terminal` / `execute_code` docs.
- `max_result_size_chars` (Step 2) feeds budgeting L2 (Step 5); `TaskId` (Step 1) scopes per-session state used by Steps 5–6.
