# Tool: `process`

Manage background processes started with `terminal(background=true)`. This is the lifecycle controller:
poll status, read output, block-wait, kill, and drive stdin (answer prompts). Required if you support
background commands — without it, background processes are launch-and-forget.

## JSON Schema (exact)

```json
{
  "name": "process",
  "description": "Manage background processes started with terminal(background=true). Actions: 'list' (show all), 'poll' (check status + new output), 'log' (full output with pagination), 'wait' (block until done or timeout), 'kill' (terminate), 'write' (send raw stdin data without newline), 'submit' (send data + Enter, for answering prompts), 'close' (close stdin/send EOF).",
  "parameters": {
    "type": "object",
    "properties": {
      "action":     {"type": "string", "enum": ["list", "poll", "log", "wait", "kill", "write", "submit", "close"], "description": "Action to perform on background processes"},
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

## Actions

| action | requires | behavior |
|--------|----------|----------|
| `list` | — | All running + recently-finished processes for the `task_id`: `session_id`, status, uptime, `exit_code`, watch metadata. |
| `poll` | `session_id` | **Non-blocking** status + new output preview (e.g. last ~1000 chars) + `exit_code` if exited. Read-only: does **not** consume the completion notification (so host watchers still fire). |
| `log` | `session_id` | Full buffered output with `offset`/`limit` pagination; returns `total_lines`. Marks completion consumed. |
| `wait` | `session_id` | **Blocks** until exit, `timeout`, or interrupt; returns full output (capped). Marks completion consumed. |
| `kill` | `session_id` | Terminate the process **tree**. SIGTERM → grace (~2s) → SIGKILL. |
| `write` | `session_id`,`data` | Write raw bytes to stdin, **no** trailing newline. |
| `submit` | `session_id`,`data` | Write `data` + Enter — for answering interactive prompts. |
| `close` | `session_id` | Close stdin / send EOF without killing (after final input). |

## State per background process (the registry)

Keyed by `session_id` (`proc_<hex>`), scoped to `task_id`:

- `pid` (+ kernel start-time, to detect recycled-PID collisions on crash recovery)
- `status` (running / exited), `exit_code`, start time
- **rolling output buffer** — fixed window (~200KB), oldest evicted, ANSI stripped before store
- stdin handle (PTY handle, or Popen stdin pipe)
- watch_patterns metadata + notification bookkeeping
- completion notification queue entry

## Required behaviors / invariants

1. **Rolling buffer, bounded.** ~200KB window; evict oldest. `log` paginates over it; `poll` previews
   the tail. ANSI stripped, secrets redacted.
2. **Notification consumption rules** (subtle but important):
   - `poll` is read-only — must NOT suppress the host's autonomous completion watcher.
   - `wait`/`log` consume — suppress duplicate inline notification, but typically still let
     gateway/background watchers see it. Get this right or the agent gets duplicate or missing "done"
     signals.
3. **Watch rate limiting** (mirrors terminal.md): 1 notification / 15s / process; 3 strikes →
   auto-disable + promote to notify-on-complete; global circuit breaker across all processes.
4. **Tree kill.** Kill the whole process tree (psutil on POSIX, `taskkill /T` on Windows). A killed
   build must not orphan children.
5. **Crash recovery (optional but valuable).** Persist registry to disk (e.g. `~/.hermes/processes.json`)
   with PID + start-time validation so a restarted host can reattach or at least report status.
6. **stdin availability.** Local Popen/PTY: full stdin (write/submit/close). Remote/container backends
   often can't do interactive stdin — surface that clearly (tell the agent to use background +
   notify_on_complete instead).
7. **Arg coercion.** Some models send `session_id` as an integer — coerce to string. Missing
   `session_id` for any non-`list` action → precise error.

## Why dedicated vs `terminal`

Separation of concerns: `terminal` launches, `process` controls. Keeps both schemas small and makes the
8 lifecycle actions discoverable in one place. The stdin actions (`write`/`submit`/`close`) are what let
an agent drive an interactive installer or REPL it started in the background.

## Implementation checklist

- [ ] Registry keyed by `session_id`, scoped to `task_id`, with rolling buffer + status + stdin handle.
- [ ] All 8 actions; `list` needs no `session_id`, others require it (coerce int→str).
- [ ] `poll` non-blocking & non-consuming; `wait` blocking & consuming; `log` paginated & consuming.
- [ ] Tree-kill with SIGTERM→grace→SIGKILL.
- [ ] Watch rate-limit + auto-promote + circuit breaker.
- [ ] ANSI strip + secret redaction in buffer.
- [ ] Optional disk persistence with PID start-time validation.

## Related

Consumes `session_id`s produced by `terminal(background=true)` ([terminal.md](terminal.md)).
