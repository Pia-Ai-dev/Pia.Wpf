# Tool: `terminal`

Execute shell commands. Persistent working directory and exported env vars across calls within a
`task_id`. Supports foreground (default) and background execution.

## JSON Schema (exact)

```json
{
  "name": "terminal",
  "description": "<TERMINAL_TOOL_DESCRIPTION>",
  "parameters": {
    "type": "object",
    "properties": {
      "command":            {"type": "string",  "description": "The command to execute on the VM"},
      "background":         {"type": "boolean", "default": false, "description": "Run in background. Almost always pair with notify_on_complete=true. Two patterns: (1) long-lived never-exiting processes (servers/watchers) stay silent; (2) long bounded tasks (tests/builds/deploys) MUST set notify_on_complete=true. Short commands: prefer foreground with a generous timeout."},
      "timeout":            {"type": "integer", "minimum": 1, "description": "Max seconds to wait (default 180, foreground max ~600). Returns INSTANTLY when the command finishes — set high for long tasks without waiting unnecessarily. Foreground above the max is rejected; use background=true."},
      "workdir":            {"type": "string",  "description": "Working directory for this command (absolute path). Defaults to the session working directory."},
      "pty":                {"type": "boolean", "default": false, "description": "Run in a pseudo-terminal for interactive CLIs (REPLs, nested agents). Local/SSH backends only."},
      "notify_on_complete": {"type": "boolean", "default": false, "description": "When true (and background=true), notify exactly once when the process exits. Right choice for almost every long bounded task. MUTUALLY EXCLUSIVE with watch_patterns."},
      "watch_patterns":     {"type": "array", "items": {"type": "string"}, "description": "Strings to watch for in background output. HARD LIMIT 1 notification / 15s / process; after 3 dropped windows it auto-disables and falls back to notify_on_complete. Use ONLY for rare one-shot mid-process signals on long-lived processes (e.g. 'Application startup complete'). MUTUALLY EXCLUSIVE with notify_on_complete."}
    },
    "required": ["command"]
  }
}
```

Registered with `max_result_size_chars = 100_000`. Copy the long description verbatim — the
foreground/background/notify steering is the contract that stops the agent going blind on long jobs.

## Return shape (conceptual)

- Foreground: `{output, exit_code, duration}` — full output up to the 100K cap.
- Background: `{session_id, pid, status: "running"}` — returns *immediately*. Further interaction goes
  through the `process` tool ([process.md](process.md)).

## Core semantics (must reproduce)

1. **Persistent session per `task_id`.** Filesystem state, **cwd**, and **exported env vars** persist
   across calls. Maintain a `task_id -> cwd` override map (the cwd source-of-truth that `read_file`/
   `patch`/`search_files` relative paths resolve against). `workdir` overrides cwd for one command only.
2. **Timeout = "give up after N", not "wait N".** Foreground returns the instant the command exits even
   if `timeout` is large. So the agent can set a generous timeout on a possibly-slow command and still
   get fast results when it's fast. Default 180s; foreground hard max ~600s — reject above it and tell
   the agent to use `background=true`.
3. **Background launch.** `background=true` spawns a tracked process, returns a `session_id`
   (`proc_<hex>`) + `pid` immediately, keeps running independently. Output buffers in a rolling window
   (see process.md). Two correct patterns, both encoded in the schema:
   - Never-exiting (servers/watchers): silent is fine.
   - Bounded long task (build/test/deploy): **require** `notify_on_complete=true`.
4. **`notify_on_complete`**: queue exactly one completion event on exit; host delivers it to the agent.
5. **`watch_patterns`**: mid-stream string triggers with a **hard rate limit** (1 / 15s / process);
   after 3 consecutive dropped windows, auto-disable and promote to `notify_on_complete`. Add a global
   circuit breaker too (e.g. max 15 watch notifications / 10s across all processes; trip 30s). These
   limits exist because naive watch patterns spam on every loop iteration.
6. **PTY mode** for interactive CLIs (REPLs, nested coding agents). Local/SSH only; auto-disable when
   the command needs piped stdin (e.g. `... --with-token`).
7. **`workdir` validation.** Allowlist-validate against shell metacharacters before use.

## Backends

Hermes abstracts local / docker / modal / ssh / etc. For a generic implementation, **local subprocess
is enough**: `subprocess.Popen`, a reader thread draining stdout/stderr into the rolling buffer, host
PID tracking. Keep the backend behind an interface so containers/remote can be added later.

## Safety

- Route `command` through an approval/guard layer (dangerous-pattern detection) before execution if you
  have one. The same guard should gate `execute_code` (see that file).
- Strip ANSI escapes before storing/returning output.
- Redact secrets from output.

## Why dedicated vs `process`

`terminal` *launches* (foreground result or background handle). `process` *manages* what `terminal`
launched in the background (poll/log/wait/kill/stdin). Keep them separate — it keeps each schema small.

## Implementation checklist

- [ ] Persistent `task_id` cwd + env; `workdir` per-command override (validated).
- [ ] Foreground: instant-return-on-exit, default 180s / max ~600s, full output to 100K.
- [ ] Background: spawn tracked process, return `session_id`+`pid` immediately, rolling output buffer.
- [ ] `notify_on_complete` (one-shot) and `watch_patterns` (rate-limited + auto-promote + circuit breaker), mutually exclusive.
- [ ] Optional PTY (local/SSH), auto-disable on piped-stdin commands.
- [ ] ANSI strip + secret redaction; command guard.

## Related

`process` ([process.md](process.md)) operates on background `session_id`s. `execute_code`
([execute_code.md](execute_code.md)) is the programmatic alternative for multi-step logic.
