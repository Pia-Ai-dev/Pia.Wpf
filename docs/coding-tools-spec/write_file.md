# Tool: `write_file`

Write content to a file, completely replacing existing content. Creates parent dirs. For *targeted*
edits use `patch` instead — `write_file` overwrites the whole file.

## JSON Schema (exact)

```json
{
  "name": "write_file",
  "description": "Write content to a file, completely replacing existing content. Use this instead of echo/cat heredoc in terminal. Creates parent directories automatically. OVERWRITES the entire file — use 'patch' for targeted edits. Auto-runs syntax checks on .py/.json/.yaml/.toml and other linted languages; only NEW errors introduced by this write are surfaced (pre-existing errors are filtered out).",
  "parameters": {
    "type": "object",
    "properties": {
      "path":    {"type": "string",  "description": "Path to the file to write (will be created if it doesn't exist, overwritten if it does)"},
      "content": {"type": "string",  "description": "Complete content to write to the file"},
      "cross_profile": {"type": "boolean", "default": false, "description": "Opt out of the cross-profile soft guard. Set true ONLY after explicit user direction to edit another profile's config."}
    },
    "required": ["path", "content"]
  }
}
```

Registered with `max_result_size_chars = 100_000`. (`cross_profile` is Hermes-specific config-safety;
drop it if your app has no multi-profile concept.)

## Return shape (conceptual)

- `success`: bool
- `path` / `resolved_path`: absolute path written
- `bytes_written` / `lines`
- `lint`: result of the post-write syntax check (only **new** errors — see below), or null
- `lsp_diagnostics`: optional semantic diagnostics, separate field
- `_warning`: staleness / workspace-divergence warnings (non-blocking)
- `error`: present on failure

## Required behaviors / invariants

1. **Atomic write.** Write to a temp file in the *same directory*, fsync, then `rename` over the target.
   Preserve the original file mode. Remove the temp file on any error. Never leave a half-written file.
2. **Auto-create parent dirs** (`mkdir -p` equivalent).
3. **Preserve line endings.** Detect the existing file's dominant ending (CRLF vs LF); normalize the
   written content to match. New files: use platform/repo default.
4. **Preserve BOM.** If the original had a leading BOM, restore it (read strips it; write puts it back).
5. **Stream large content** (over stdin / chunked) to avoid arg-length limits — relevant if you shell
   out; with native FS APIs this is automatic.
6. **Post-write syntax check with delta filtering** — the key feature:
   - Run a language-appropriate check by extension. Fast path: in-process parse for structured formats
     (`json.loads`, YAML safe-load, TOML load, Python `ast.parse`). Fallback: shell linters
     (`py_compile`, `node --check`, `tsc`, `go vet`, `rustfmt --check`, etc.).
   - **Before writing**, lint the *old* content to get a baseline error set. **After writing**, lint the
     new content. **Surface only errors absent from the baseline** (i.e. introduced by this write).
     Pre-existing errors are filtered out so the agent isn't derailed by unrelated noise.
   - Optionally attach LSP diagnostics in a separate field, only when syntax passes.
7. **Sensitive-path blocklist.** Reject writes to `/etc/`, `/boot/`, system service dirs, credential
   stores, and your app's own config files. Return a clear refusal.
8. **Staleness warning (non-blocking).** If the file was modified since the agent's last `read_file`
   (per-`task_id` mtime) or by another agent (cross-agent registry), prepend a `_warning`. Do not block —
   the agent may legitimately be overwriting.
9. **Workspace-divergence warning.** If a relative `path` resolves outside the task's workspace root
   (e.g. a worktree-cwd mismatch), warn.
10. **Internal-content guard.** Reject content that is obviously `read_file` display text (lines starting
    with `N|`) or a dedup stub message — that means the model echoed a tool result back instead of real
    content. Return a corrective error.
11. **Missing-arg self-healing.** If `path` present but `content` missing → return a precise error
    ("dropped-arg bug under context pressure; re-emit with full content, or use execute_code for very
    large files"). If `content` is not a string → type error. Do **not** silently write empty.

## Why dedicated (not `echo`/heredoc)

Atomicity, dir creation, line-ending/BOM preservation, and — most importantly — the delta-filtered
syntax check that catches the agent's own mistakes immediately without surfacing unrelated lint debt.

## Implementation checklist

- [ ] Resolve + normalize path; sensitive-path rejection.
- [ ] Missing/!string `content` → corrective error (no silent write).
- [ ] Baseline lint of old content (for delta).
- [ ] Atomic temp-write + rename; preserve mode, line-endings, BOM; mkdir -p.
- [ ] Post-write lint; surface only NEW errors; optional LSP field.
- [ ] Staleness + workspace-divergence `_warning`s (non-blocking).
- [ ] Update cross-agent file registry (this path was written by this task).

## Related

Pairs with `read_file` (staleness mtime source) and `patch` (use `patch` for targeted edits; both share
the lint/delta + staleness machinery).
