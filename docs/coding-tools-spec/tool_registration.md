# Spec: Tool Registration, Dispatch & the Host Tool Layer

> The connective tissue all 12 tools plug into. This is host-side infrastructure, not a model-callable
> tool. Implement this layer first or in parallel with `read_file` — every other tool depends on it.
> Source: `tools/registry.py`, `tools/tool_result_storage.py`, `tools/tool_output_limits.py`,
> `tools/budget_config.py`, `tools/schema_sanitizer.py`, `tools/approval.py`.

## Responsibilities

1. **Registry** — one place every tool declares itself (schema + handler + gating + limits).
2. **Definition generation** — turn the enabled tool set into provider-format schemas, gated by
   availability checks, with per-call dynamic overrides and schema sanitization.
3. **Dispatch** — route a model tool-call to its handler, thread per-session context, catch+sanitize errors.
4. **Output budgeting** — a three-layer defense so tool output never overflows the context window.
5. **Approval guard** — gate dangerous commands (`terminal`/`execute_code`) before they run.

---

## 1. Registry

### ToolEntry record (one per tool)

```
name                     str    stable id the model calls
toolset                  str    group key ("file", "terminal", "code_execution", ...)
schema                   dict   {name, description, parameters: <JSON Schema>}
handler                  fn     (args: dict, **ctx) -> str | dict
check_fn                 fn?    () -> bool   availability gate (env/feature/binary present)
requires_env             list   env vars that must be set
is_async                 bool   handler is a coroutine (bridged on dispatch)
description              str    defaults to schema["description"]
emoji                    str    UI affordance (optional)
max_result_size_chars    int?   per-tool output cap → budgeting layer 2 (file/terminal use 100_000)
dynamic_schema_overrides fn?    () -> partial schema dict, applied at definition time
```

### register() rules to reproduce

- **Module-level self-registration.** Each tool file calls `register(...)` at import. The host
  auto-discovers tool modules (AST-scan for a top-level `registry.register(...)` call, then import) so
  adding a tool is "drop a file in `tools/`". Helper modules that only call register *inside a function*
  are not picked up.
- **Anti-shadow guard.** If `name` already exists under a *different* `toolset`, reject the registration
  unless `override=True` (explicit plugin opt-in, logged at INFO). Prevents plugins/MCP silently
  replacing built-ins. (Exception: MCP-toolset→MCP-toolset overwrites are allowed for server refresh.)
- **`deregister(name)`** removes a tool and, if it was the last in its toolset, drops the toolset's
  `check_fn` and aliases. Used for MCP "nuke-and-repave" on `tools/list_changed`.
- **Generation counter.** Bump a `_generation` int on every register/deregister so definition caches
  upstream can invalidate cheaply.
- **Thread-safe.** Guard the tool map with a lock; registration happens at import, dispatch happens
  concurrently across subagents.

---

## 2. Definition generation — `get_definitions(tool_names) -> [provider schema]`

For each requested name, in sorted order:

1. **Availability gate.** If the entry has a `check_fn`, call it; skip the tool if it returns falsey.
   Examples: `terminal`'s check probes docker/modal availability; browser checks Playwright; desktop-only
   tools check an env flag. **Cache `check_fn` results ~30s** (probes are expensive: container/network).
   TTL is short enough that `enable foo` takes effect in near-real-time. Also memoize within a single
   definitions pass (same `check_fn` may back many tools).
2. **Ensure `name`.** Force `schema["name"] = entry.name`.
3. **Dynamic overrides.** If `dynamic_schema_overrides` is set, call it and merge the returned dict over
   the static schema. Use for fields that depend on live config — e.g. `delegate_task`'s description must
   show the user's current `max_concurrent_children` / `max_spawn_depth`; `execute_code`'s description
   lists the currently-enabled sandbox tools. Wrap in try/except → fall back to static schema on error.
4. **Emit provider shape.** `{"type": "function", "function": schema_with_name}` (OpenAI-style). Adapt
   to your provider (Anthropic wants `{name, description, input_schema}`).
5. **Sanitize** (see §5) before sending.

**Caching:** the caller memoizes the whole definitions list keyed on `(enabled tools, config.yaml mtime
+ size, registry generation)` so schemas rebuild only when something actually changed.

---

## 3. Dispatch — `dispatch(name, args, **ctx) -> str`

```
entry = get_entry(name)
if not entry: return json({"error": f"Unknown tool: {name}"})
try:
    result = run_async(entry.handler(args, **ctx)) if entry.is_async else entry.handler(args, **ctx)
except Exception as e:
    log.exception(...)
    return json({"error": sanitize_tool_error(f"Tool execution failed: {type(e).__name__}: {e}")})
return result
```

Invariants:
- **Uniform error envelope.** Every failure (unknown tool, handler exception) returns
  `{"error": "..."}` as a string — never raises into the agent loop. One predictable shape the model
  can reason about.
- **`ctx` threading.** Pass `task_id` (and session/observability context) into every handler. This is
  the key that scopes all per-session state (read-dedup, cwd, mtime tracking, process registry). Default
  `task_id="default"`.
- **Async bridging.** Async handlers are awaited on the loop transparently so callers don't care.
- **Error sanitization.** Run exception strings through the schema/text sanitizer so framing tokens,
  code fences, or CDATA in an error message don't reach the model as structural noise (prompt-injection
  and parser-confusion hygiene).

---

## 4. Output budgeting — three layers (the anti-overflow system)

Defaults (`budget_config.py`): per-result `100_000` chars, per-turn `200_000` chars, preview `1_500`
chars. Optionally **auto-scale to the model's context window**: `window_chars = context_length * 4`
(≈4 chars/token), per-result = 15% of window, per-turn = 30%, clamped to `[8_000, 100_000]` and
`[16_000, 200_000]` respectively. Some tools are **pinned** (a `PINNED_THRESHOLDS` map / `inf` = never
persist).

**Layer 1 — per-tool self-truncation.** Each tool caps its own output before returning (the only layer
the tool author controls). `read_file`: line + 100K-char caps. `terminal`/`process`: rolling 50K–200K
window. `search_files`: result limit + compact formatting. Centralize the knobs (`tool_output.max_bytes`
/ `max_lines` / `max_line_length` in config, with hard defaults 50000 / 2000 / 2000) so they're tunable
without patching tools.

**Layer 2 — per-result persistence (`maybe_persist_tool_result`).** After a tool returns, if its output
exceeds `registry.get_max_result_size(tool_name)`, write the **full** output to a sandbox temp file
(`{tmpdir}/hermes-results/{tool_use_id}.txt`, via the env so it works on docker/ssh/modal too) and
replace the in-context content with a `<persisted-output>` block: size, file path, and a `preview_size`
preview truncated at the last newline. The model then `read_file`s the path with offset/limit to drill
in. Push content over **stdin**, not the command string (Linux `MAX_ARG_STRLEN` ≈128KB caps argv).
Fall back to inline truncation if the write fails or no env is available.

**Layer 3 — per-turn aggregate budget (`enforce_turn_budget`).** After ALL tool results in one assistant
turn are collected, if their combined size exceeds `turn_budget` (200K), spill the **largest
non-persisted** results to disk (same persistence path) until under budget. Catches the case where many
medium results individually pass layer 2 but collectively overflow.

Always truncate **head+tail** (or "truncate at last newline within max"), never blind head-only — the
tail usually carries the error/result. Preserve a clear truncation marker so the model knows to page.

---

## 5. Schema sanitization (provider compatibility)

Walk the final schema tree (after dynamic rebuilds and MCP normalization) on a deep copy and fix
known-hostile constructs in-place — conservatively, only shapes a strict backend couldn't use anyway:

- `{"type": "object"}` with **no `properties`** → add `properties: {}` (and handle unconstrained
  `additionalProperties`). Bare grammar generators (llama.cpp GBNF) reject it outright.
- A schema value that is the **bare string `"object"`** instead of a dict (malformed MCP output) → fix.
- **Array `type`** like `["string", "null"]` → collapse to the single non-null branch.
- **`anyOf`/`oneOf` nullable unions** (Pydantic/MCP optional shape) → collapse to the non-null branch
  (Anthropic rejects these at the top of `input_schema`).
- **Sibling keywords next to `$ref`** (e.g. `{"$ref": ..., "default": null}`) → strip the siblings
  (draft-07 validators reject them).

This runs once per definitions pass, after overrides, before the request. Without it, one bad MCP tool
schema can 400 the entire request for strict/local backends.

---

## 6. Approval guard (dangerous-command gating)

`terminal` and `execute_code` route their command/script through a guard **before** execution:

- **`detect_hardline_command(cmd)`** — patterns that are *always* blocked (e.g. fork bombs, disk wipes).
  Cannot be approved away.
- **`detect_dangerous_command(cmd)`** — a compiled regex list (`DANGEROUS_PATTERNS`, ~47 patterns:
  `rm -rf`, `chmod -R 777`, `curl | sh`, etc.) returning `(matched, description)`. These prompt for
  user approval (or are allowed under an explicit "yolo"/auto-approve mode).
- **Normalization before matching.** Resolve `$HOME`/home-dir rewrites, collapse whitespace, etc., so
  trivial obfuscation doesn't slip patterns past detection. Compile patterns once at import (hot path:
  every terminal call).
- **Session-scoped approvals.** Once the user approves a pattern, remember it for the session (keyed by
  a session key set via context-vars) so the agent isn't re-prompted every call. Approval context is
  held synchronously during dispatch (important for `execute_code`, which runs in the caller thread).
- **Sudo-stdin guard.** Special-case piping passwords into `sudo -S`.

For a generic implementation: ship at least hardline + dangerous-pattern detection with an
allow/deny/ask decision and session-scoped remembered approvals. `clarify` is NOT the channel for this —
the guard owns command confirmation (see [clarify.md](clarify.md)).

---

## Implementation checklist

- [ ] `ToolEntry` + thread-safe registry; module-level `register()`; AST auto-discovery of tool modules.
- [ ] Anti-shadow guard (`override=True` opt-in); `deregister` with toolset cleanup; generation counter.
- [ ] `get_definitions`: `check_fn` gating with ~30s cache; dynamic overrides; provider-shape emit.
- [ ] Definitions memo keyed on (enabled tools, config mtime+size, generation).
- [ ] `dispatch`: uniform `{"error"}` envelope, `task_id`/ctx threading, async bridge, error sanitization.
- [ ] Budget layers 1–3 with the 100K / 200K / 1.5K defaults (+ optional context-window auto-scaling).
- [ ] Sandbox persistence over stdin; `<persisted-output>` preview + `read_file`-able path.
- [ ] Schema sanitizer for the 6 hostile shapes.
- [ ] Approval guard: hardline + dangerous patterns, normalization, session-scoped approvals.

## Related

Every tool file in this set assumes this layer. `max_result_size_chars` here feeds budgeting layer 2;
`task_id` threading here scopes the per-session state listed in [overview.md](overview.md).
