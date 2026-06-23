# Coding Tools Specification — derived from the Hermes agent

> AUDIENCE: an LLM coding agent (Claude) implementing an equivalent toolset in another application.
> These documents are implementation specs, not tutorials. Each file is a self-contained contract:
> exact JSON Schema, parameter semantics, return shape, required invariants, and an implementation
> checklist. Reproduce the *behavior contract*, not the Python.

## What this set covers

Hermes exposes one explicit `coding` posture toolset. Stripped of messaging/audio/vision extras, the
irreducible coding core is **7 tools**, plus **5 workflow-support tools** that make long autonomous
sessions reliable. Implement the 7 first.

| Tier | Tool | File | One-line role |
|------|------|------|---------------|
| Essential | `read_file` | [read_file.md](read_file.md) | Line-numbered, paginated file read (replaces `cat/head/tail`) |
| Essential | `write_file` | [write_file.md](write_file.md) | Atomic full-file overwrite + syntax check (replaces `echo`/heredoc) |
| Essential | `patch` | [patch.md](patch.md) | Fuzzy find-and-replace + V4A multi-file patch (replaces `sed/awk`) |
| Essential | `search_files` | [search_files.md](search_files.md) | Ripgrep content + glob file search (replaces `grep/rg/find/ls`) |
| Essential | `terminal` | [terminal.md](terminal.md) | Persistent shell, foreground + background |
| Essential | `process` | [process.md](process.md) | Manage background processes started by `terminal` |
| Essential | `execute_code` | [execute_code.md](execute_code.md) | Python script that calls tools programmatically (power tool) |
| Support | `todo` | [todo.md](todo.md) | Session task list, survives context compression |
| Support | `delegate_task` | [delegate_task.md](delegate_task.md) | Spawn isolated subagents (single or parallel) |
| Support | `memory` | [memory.md](memory.md) | Durable facts across sessions |
| Support | `session_search` | [session_search.md](session_search.md) | FTS5 recall over past sessions |
| Support | `clarify` | [clarify.md](clarify.md) | Ask the user a question / choice |
| Infra | *(host layer)* | [tool_registration.md](tool_registration.md) | Registry, dispatch, output budgeting, schema sanitization, approval guard — the connective tissue all tools plug into |

`read_terminal` exists in Hermes but is desktop-GUI-only (reads the embedded terminal pane); skip it
for a generic implementation.

## Build order (recommended)

1. `read_file` → 2. `search_files` → 3. `patch` → 4. `write_file` → 5. `terminal`
6. `process` → 7. `todo` → 8. `clarify` → then `execute_code`, `delegate_task`, `memory`, `session_search`.

`patch` is the highest-leverage and hardest to get right. Budget the most effort there.

## Tool-registration contract (host-side)

Each tool is registered with a uniform record. Reproduce this shape so tools are uniform to dispatch:

```
register(
  name,                    # str, stable id the model calls
  toolset,                 # str, group key (e.g. "file", "terminal")
  schema,                  # dict: {name, description, parameters{JSON Schema}}
  handler,                 # fn(args: dict, **ctx) -> str | dict  (ctx carries task_id etc.)
  check_fn=None,           # fn() -> bool|reason  gates availability (env/feature flags)
  max_result_size_chars=None,  # truncation cap applied to the tool result
  dynamic_schema_overrides=None,  # fn() -> partial schema, rebuilt per definitions() call
)
```

Key registry rules to copy:
- **Anti-shadow guard**: a registration that would overwrite an existing tool from a *different*
  toolset is rejected unless `override=True`. Prevents plugins from silently replacing built-ins.
- **`task_id` threading**: every handler receives a `task_id` (default `"default"`). All per-session
  state (read-dedup caches, file mtime tracking, background processes, cwd overrides) is keyed by it.
  This is what makes parallel subagents safe. Implement `task_id` from day one — retrofitting is painful.
- **`max_result_size_chars`** (the file/terminal tools use `100_000`): the dispatcher truncates tool
  output to this cap before it re-enters the model context.

## Schema conventions

- Schema shape is `{"name", "description", "parameters": <JSON Schema object>}`. The `description`
  string is part of the contract — it is the only instruction the model gets about *when/how* to use
  the tool. Copy the descriptions; they encode hard-won steering (e.g. "use this instead of cat").
- Descriptions explicitly redirect the model away from shell equivalents. This is deliberate: dedicated
  tools control output format, truncation, line numbers, and safety in ways raw shell cannot.

## Cross-cutting design principles (the actual lessons)

These recur across tools and are the reason the toolset is reliable. Implement them globally.

1. **Line-numbered reads are the coordinate system.** `read_file` emits `LINE_NUM|CONTENT`. Every
   edit, citation, and diff anchors on those numbers.
2. **Fuzzy matching on edits is mandatory, not optional.** Exact-match `old_string` fails constantly
   on LLM whitespace/indent/Unicode drift. See [patch.md](patch.md) for the 9-strategy chain.
3. **Delta-filter diagnostics.** After write/patch, run a syntax/lint check but surface only errors the
   edit *introduced*. Pre-existing errors are noise and derail the agent.
4. **Loop / dedup guards.** Reads and searches track recent identical calls; return a stub or block
   after N repeats. Prevents the agent thrashing on the same read/search.
5. **Staleness tracking.** Record file mtime at read time; warn (don't hard-block) on write/patch if the
   file changed underneath. Cross-agent registry catches concurrent subagent edits.
6. **Return a diff** from edits for auditability; **verify the write persisted** by re-reading.
7. **Truncate with head+tail, not head-only**, for command/script output — the tail usually has the
   error and the result.
8. **Pagination everywhere** (`offset`/`limit`) so a single huge file or result set never blows context.
9. **Atomic writes** (temp file + rename), preserve line-endings (CRLF/LF) and BOM.
10. **Self-healing arg validation.** Models drop args under context pressure; detect missing `content`,
    integer `session_id`, dict-shaped choices, etc., and return a precise corrective error.

## State each tool needs (host-side, keyed by `task_id`)

- Read-dedup cache: `(resolved_path, offset, limit) -> mtime`, + consecutive-identical-read counter.
- Per-task read timestamps: `resolved_path -> mtime_at_read` (for staleness warnings).
- Cross-agent file registry: reads/writes across subagent tasks (concurrent-edit detection).
- Patch failure counter: `(task_id, resolved_path) -> consecutive_failures` (escalates hints at 3+).
- Background process registry: `session_id -> {pid, buffer, status, exit_code, ...}` (see process.md).
- Working-directory override: `task_id -> cwd` (persistent shell cwd).

These are the difference between a toy and a tool that survives a 200-step session.
