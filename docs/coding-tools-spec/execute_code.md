# Tool: `execute_code`

Run a Python script that can call your other tools programmatically (RPC). A power-multiplier, not a
basic — implement it after the core 7 are solid. It exists to collapse many tool round-trips into one
turn and to filter large outputs *before* they enter the model's context.

## When the model should use it (from the description)

Use `execute_code` when you need: 3+ tool calls with processing logic between them; to filter/reduce a
large tool output before it enters context; conditional branching; or loops (N pages, N files, retry).
Use normal tool calls instead for: a single call with no processing, when you need to *see* the full
result and reason over it, or when the task needs interactive user input.

## JSON Schema (exact)

```json
{
  "name": "execute_code",
  "description": "<built per-session: lists the allowed sandbox tools + current mode + the helpers below>",
  "parameters": {
    "type": "object",
    "properties": {
      "code": {"type": "string", "description": "Python code to execute. Import tools with `from hermes_tools import <names>` and print your final result to stdout."}
    },
    "required": ["code"]
  }
}
```

The description is **built dynamically per session** so it lists exactly the tools the current toolset
exposes inside the sandbox, the active mode, and the built-in helpers. Rebuild it each time tool
definitions are generated.

## Sandbox API (`hermes_tools` module shipped into the sandbox)

A safe subset of the real tools, each a thin RPC stub back to the host:

```python
web_search(query, limit=5)                 -> dict   # {data.web: [...]}
web_extract(urls: list)                     -> dict   # {results: [...]}
read_file(path, offset=1, limit=500)        -> dict   # {content, total_lines}
write_file(path, content)                   -> dict
search_files(pattern, target="content", path=".", file_glob=None, limit=50) -> dict  # {matches}
patch(path=None, old_string=None, new_string=None, replace_all=False, mode="replace") -> dict
terminal(command, timeout=None, workdir=None) -> dict # {output, exit_code}  (foreground only — no background/pty/notify)
```

Built-in helpers (no import):

```python
json_parse(text)                # json.loads(strict=False) — tolerates control chars in tool output
shell_quote(s)                  # shlex.quote() — safe interpolation into shell commands
retry(fn, max_attempts=3, delay=2)  # exponential backoff for transient failures
```

The script `print()`s its final result to stdout; that stdout is the tool result returned to the model.

## Architecture (two transports)

- **Local**: parent opens a Unix domain socket (POSIX) / loopback TCP (Windows); child subprocess
  connects via an env var (`HERMES_RPC_SOCKET`). Requests/responses are newline-delimited JSON frames.
- **Remote/sandboxed**: ship `hermes_tools.py` into the sandbox; child writes request files to a tmpdir;
  a parent polling thread reads them (`env.execute`), dispatches to the real tools, writes response
  files. Adaptive backoff polling (e.g. 50ms→250ms).

## Required behaviors / invariants

1. **Env scrubbing.** Drop any env var whose name contains `KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|WEBHOOK`.
   Allow only safe prefixes (`PATH`, `HOME`, `LANG`, `LC_`, `TERM`, `PYTHON*`, `VIRTUAL_ENV`, `CONDA`,
   `XDG_`) + explicitly-declared passthrough vars + OS essentials on Windows.
2. **Execution mode.**
   - `project` (default): run in the session cwd with the project's active venv python (project deps +
     relative paths work).
   - `strict`: isolated tmpdir with the host's own python (reproducible; project deps won't resolve).
3. **Resource limits.** Per-script timeout ~300s; max tool calls per script ~50 (configurable). Both
   enforced by the host RPC dispatcher (the cap is on the *parent* side — the sandbox can't bypass it).
4. **Output caps with head+tail.** stdout ~50KB using a head+tail strategy (keep ~40% head + ~60% tail,
   omit middle with a marker — the error and the result usually bracket the noise). stderr ~10KB
   head-only (errors appear early). ANSI strip + secret redaction.
5. **Deadlock-safe capture.** Separate reader threads for stdout and stderr (single-pipe reads deadlock).
6. **Cooperative interruption.** Script checks an `is_interrupted()` flag during RPC; on a new user
   message, kill the child tree (SIGTERM→grace→SIGKILL).
7. **Same approval guard as `terminal`.** Vet the script for dangerous patterns before dispatch; it runs
   synchronously in the caller thread so it holds the session's approval context.
8. **Every RPC re-enters the real tools** — so all the guards (path safety, fuzzy patch, lint-delta,
   loop guards) apply to sandbox calls too. Do NOT reimplement the tools in the sandbox; proxy them.

## Why dedicated

Lets the agent do data-flow work (filter 500 search hits to the 3 relevant, loop-edit 20 files, retry a
flaky fetch) in ONE turn without round-tripping each intermediate result through the context window —
the single biggest token saver for batch coding work.

## Implementation checklist

- [ ] `hermes_tools` stub module: the safe tool subset + `json_parse`/`shell_quote`/`retry`.
- [ ] RPC transport (local socket; optional file-based for sandboxes), newline-JSON frames.
- [ ] Env scrub (deny secrets, allow safe prefixes).
- [ ] `project` vs `strict` mode.
- [ ] Timeout (~300s) + max-tool-calls (~50) enforced host-side.
- [ ] stdout head+tail (~50KB) / stderr head (~10KB), ANSI strip, redaction, dual reader threads.
- [ ] Interrupt flag + child-tree kill; reuse terminal's command guard.
- [ ] Dynamic per-session schema description listing available sandbox tools.

## Related

Proxies `read_file`/`write_file`/`patch`/`search_files`/`terminal`/`web_*`. `delegate_task`
([delegate_task.md](delegate_task.md)) is the alternative for parallel *agentic* work (vs scripted).
