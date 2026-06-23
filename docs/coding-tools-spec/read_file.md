# Tool: `read_file`

Read a text file with line numbers and pagination. The agent's primary way to see file contents.
Replaces `cat`/`head`/`tail` — the model is steered here explicitly.

## JSON Schema (exact)

```json
{
  "name": "read_file",
  "description": "Read a text file with line numbers and pagination. Use this instead of cat/head/tail in terminal. Output format: 'LINE_NUM|CONTENT'. Suggests similar filenames if not found. Use offset and limit for large files. Reads exceeding ~100K characters are rejected; use offset and limit to read specific sections of large files. Jupyter notebooks (.ipynb), Word documents (.docx), and Excel workbooks (.xlsx) are auto-extracted to readable text. NOTE: Cannot read images or other binary files — use vision_analyze for images.",
  "parameters": {
    "type": "object",
    "properties": {
      "path":   {"type": "string",  "description": "Path to the file to read (absolute, relative, or ~/path)"},
      "offset": {"type": "integer", "description": "Line number to start reading from (1-indexed, default: 1)", "default": 1, "minimum": 1},
      "limit":  {"type": "integer", "description": "Maximum number of lines to read (default: 500, max: 2000)", "default": 500, "maximum": 2000}
    },
    "required": ["path"]
  }
}
```

Registered with `max_result_size_chars = 100_000`.

## Output format

Line-oriented, one line per source line:

```
1|import os
2|
3|def main():
4|    print("hi")
```

Format is `LINE_NUM|CONTENT` (pipe, **no padding** — padding wastes tokens). 1-indexed. The numbers are
the coordinate system the agent uses for `patch` anchors, diffs, and citing `file:line`.

## Parameter contract

- `path`: absolute, relative (to session cwd), or `~/`-expanded. Resolve and normalize before any I/O.
- `offset`: 1-indexed first line. Default 1.
- `limit`: max lines, default 500, hard max 2000. Output of the read is additionally capped at
  ~100K characters; if exceeded, reject with guidance to narrow via offset/limit.

## Return shape (conceptual)

Return a dict the dispatcher serializes (or a preformatted string). Minimum fields:

- `content`: the `LINE_NUM|CONTENT` block for the requested window.
- `total_lines`: total line count of the file (lets the agent page intelligently).
- `offset`, `limit`: echoed back.
- On not-found: an error string **plus suggestions** (see below).

## Required behaviors / invariants

1. **Binary detection BEFORE read.** Reject by extension/content sniff. For images, return a message
   redirecting to a vision tool. Prevents dumping binary garbage into context.
2. **Device-path blocklist.** Refuse paths that hang or leak: `/dev/zero`, `/dev/stdin`, `/dev/random`,
   `/proc/*/fd/*`, `/proc/*/environ`, etc. (host-OS dependent).
3. **UTF-8 BOM stripping.** Strip a leading BOM so the model never sees a phantom `U+FEFF` on line 1.
   (`write_file`/`patch` restore it on disk.)
4. **Structured-doc extraction.** `.ipynb`, `.docx`, `.xlsx` → extract to readable text rather than
   failing as binary.
5. **File-not-found suggestions.** On miss, scan the target directory and return ranked similar names
   (exact basename match > prefix > substring). Saves a wasted turn.
6. **Read-dedup guard (per `task_id`).** Cache `(resolved_path, offset, limit) -> mtime`. If the same
   key is read again and mtime is unchanged, return a short stub ("unchanged since last read") instead
   of the full content. Escalate to a hard block after ~2 stub returns on the same key.
7. **Consecutive-identical-read loop guard.** Count back-to-back identical reads; warn at 3, block at 4.
8. **Staleness bookkeeping.** Store mtime at read time per `task_id` + path. `write_file`/`patch` read
   this to warn if the file changed since the agent last saw it.
9. **Dedup reset on context compression.** Expose `reset_read_dedup(task_id)`; the host calls it after
   summarizing context so the agent can legitimately re-read full content it no longer has in context.
10. **Large-file hint.** If file > ~512KB and the caller didn't pass a narrow offset/limit, include a
    hint suggesting pagination.

## Why dedicated (not `cat`)

Line numbers, deterministic truncation, dedup/loop guards, binary safety, and mtime tracking for
staleness — none achievable by shelling out to `cat`. The dedup guard alone removes a very common
failure mode (re-reading the same file every turn).

## Implementation checklist

- [ ] Resolve path (`~`, relative→cwd), normalize.
- [ ] Device-path + binary rejection.
- [ ] Read window `[offset, offset+limit)`, enforce 2000-line / 100K-char caps.
- [ ] Emit `LINE_NUM|CONTENT`, strip BOM, report `total_lines`.
- [ ] Not-found → ranked suggestions.
- [ ] Per-`task_id` dedup cache + consecutive-read counter + stub/block escalation.
- [ ] Record mtime-at-read for staleness (shared with write/patch).
- [ ] `reset_read_dedup(task_id)` hook for post-compression.

## Related

`write_file` and `patch` consume the mtime-at-read state set here. `search_files` is the discovery
counterpart (find what to read).
