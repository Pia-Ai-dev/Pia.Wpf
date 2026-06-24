# Tool: `search_files`

Search file contents (grep) or find files by name (find/ls), ripgrep-backed. One tool replacing
`grep`/`rg`/`find`/`ls`. The agent's discovery primitive — how it locates what to `read_file`/`patch`.

## JSON Schema (exact)

```json
{
  "name": "search_files",
  "description": "Search file contents or find files by name. Use this instead of grep/rg/find/ls in terminal. Ripgrep-backed, faster than shell equivalents.\n\nContent search (target='content'): Regex search inside files. Output modes: full matches with line numbers, file paths only, or match counts.\n\nFile search (target='files'): Find files by glob pattern (e.g., '*.py', '*config*'). Also use this instead of ls — results sorted by modification time.",
  "parameters": {
    "type": "object",
    "properties": {
      "pattern":     {"type": "string", "description": "Regex pattern for content search, or glob pattern (e.g., '*.py') for file search"},
      "target":      {"type": "string", "enum": ["content", "files"], "default": "content", "description": "'content' searches inside file contents, 'files' searches for files by name"},
      "path":        {"type": "string", "default": ".", "description": "Directory or file to search in (default: current working directory)"},
      "file_glob":   {"type": "string", "description": "Filter files by pattern in grep mode (e.g., '*.py' to only search Python files)"},
      "limit":       {"type": "integer", "default": 50, "description": "Maximum number of results to return (default: 50)"},
      "offset":      {"type": "integer", "default": 0, "description": "Skip first N results for pagination (default: 0)"},
      "output_mode": {"type": "string", "enum": ["content", "files_only", "count"], "default": "content", "description": "Output format for grep mode: 'content' shows matching lines with line numbers, 'files_only' lists file paths, 'count' shows match counts per file"},
      "context":     {"type": "integer", "default": 0, "description": "Number of context lines before and after each match (grep mode only)"}
    },
    "required": ["pattern"]
  }
}
```

Registered with `max_result_size_chars = 100_000`. Accept legacy aliases on `target`:
`grep`→`content`, `find`→`files`.

## Two targets

- `target="content"` (grep): regex search inside files. `pattern` is a **regex**. Honors `file_glob`,
  `output_mode`, `context`.
- `target="files"` (find/ls): find files by name. `pattern` is a **glob** (`*.py`, `*config*`). Results
  **sorted by modification time** (newest first) so it doubles as a smart `ls`.

## Backend

- **Primary: ripgrep (`rg`).** Respects `.gitignore`, skips hidden dirs by default, parallel traversal.
  - content: `rg --line-number` (+ `-l` for files_only, `-c` for count, `-C <n>` for context,
    `--glob <file_glob>`).
  - files: `rg --files` (+ `--sortr=modified` on rg ≥13; fall back to unsorted on older).
- **Fallback: `grep`/`find`** when `rg` is unavailable. Replicate gitignore/hidden-dir behavior as best
  possible.
- Hidden-dir handling: exclude hidden dirs by default, **but** if the search root *is itself* hidden
  (e.g. `./.git`), don't filter that root out.

## Output modes (content target)

- `content`: matching lines with line numbers. For ≥5 matches, render as a **compact path-grouped text
  block** (group by file, list `line: text`) rather than a verbose JSON array — saves tokens. For <5,
  a small structured array is fine.
- `files_only`: just the file paths that matched.
- `count`: per-file match counts.

## Required behaviors / invariants

1. **Pagination.** `offset` + `limit`; when results are truncated, append a hint (`"showing 50 of N;
   pass offset=50"`).
2. **Timeout.** ~60s cap. On timeout (rg exit 124), return partial results and mark
   `limit_reason="search_timeout"`. Never hang the agent.
3. **Consecutive-search loop guard (per `task_id`).** Key on the full arg tuple
   `(pattern, target, path, file_glob, limit, offset)`. Warn at 3 identical consecutive searches,
   block at 4.
4. **Multiline-regex warning.** If `pattern` contains `\n`, warn that the backend is line-oriented
   (no cross-line matching) — prevents silent empty results.
5. **Diagnostics vs results separation.** rg/grep can exit non-zero (e.g. exit 2) while still producing
   valid matches. Keep the matches; report tool diagnostics in a separate field. Don't discard good
   output because of a stderr warning.
6. **Path-not-found suggestions.** If `path` doesn't exist, list similar entries (same as `read_file`).
7. **Shell-escape `file_glob`** and any interpolated arg if you invoke a subprocess.

## Why dedicated (not raw `grep`/`find`)

Gitignore-awareness, mtime sorting, token-compact output formatting, pagination, loop detection, and
partial-result-on-timeout — all controlled here, none reliably available by shelling out.

## Implementation checklist

- [ ] `target`/alias dispatch; `pattern` is regex (content) vs glob (files).
- [ ] rg primary (`--line-number`/`-l`/`-c`/`-C`/`--glob`/`--files`/`--sortr=modified`), grep/find fallback.
- [ ] Hidden-dir + gitignore handling, with hidden-root exception.
- [ ] output_mode rendering incl. compact path-grouped block for ≥5 matches.
- [ ] offset/limit pagination + truncation hint.
- [ ] 60s timeout → partial + `limit_reason`.
- [ ] Per-`task_id` consecutive-search loop guard (warn@3, block@4).
- [ ] Multiline-regex warning; diagnostics/results separation; path suggestions.

## Related

The front of the loop: `search_files` → `read_file` → `patch`/`write_file`.
