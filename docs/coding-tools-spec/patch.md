# Tool: `patch` — the edit tool (HIGHEST PRIORITY)

Targeted find-and-replace in files. Replaces `sed`/`awk`. The single most important and most
difficult tool to implement well. Its reliability comes almost entirely from **fuzzy matching** — do
not ship an exact-match-only version, it will fail constantly on LLM whitespace/indent/Unicode drift.

Two modes: `replace` (single-file find/replace, default) and `patch` (V4A multi-file bulk).

## JSON Schema (exact)

```json
{
  "name": "patch",
  "description": "Targeted find-and-replace edits in files. Use this instead of sed/awk in terminal. Uses fuzzy matching (9 strategies) so minor whitespace/indentation differences won't break it. Returns a unified diff. Auto-runs syntax checks after editing.\n\nREPLACE MODE (mode='replace', default): find a unique string and replace it. REQUIRED PARAMETERS: mode, path, old_string, new_string.\nPATCH MODE (mode='patch'): apply V4A multi-file patches for bulk changes. REQUIRED PARAMETERS: mode, patch.",
  "parameters": {
    "type": "object",
    "properties": {
      "mode":        {"type": "string", "enum": ["replace", "patch"], "default": "replace", "description": "Edit mode. 'replace' (default): requires path + old_string + new_string. 'patch': requires patch content only."},
      "path":        {"type": "string", "description": "REQUIRED when mode='replace'. File path to edit."},
      "old_string":  {"type": "string", "description": "REQUIRED when mode='replace'. Exact text to find and replace. Must be unique in the file unless replace_all=true. Include surrounding context lines to ensure uniqueness."},
      "new_string":  {"type": "string", "description": "REQUIRED when mode='replace'. Replacement text. Pass empty string '' to delete the matched text."},
      "replace_all": {"type": "boolean", "default": false, "description": "Replace all occurrences instead of requiring a unique match (default: false)"},
      "patch":       {"type": "string", "description": "REQUIRED when mode='patch'. V4A format patch content. Format:\n*** Begin Patch\n*** Update File: path/to/file\n@@ context hint @@\n context line\n-removed line\n+added line\n*** End Patch"},
      "cross_profile": {"type": "boolean", "default": false, "description": "Opt out of the cross-profile soft guard."}
    },
    "required": ["mode"]
  }
}
```

Registered with `max_result_size_chars = 100_000`.

## Return shape (conceptual)

- `success`: bool
- `diff`: unified diff of what changed (always return this on success — auditability)
- `files_modified`: list of paths
- `resolved_path`: absolute path actually edited (worktree debugging)
- `lint` / `lsp_diagnostics`: delta-filtered, same machinery as `write_file`
- `_warning`: staleness / divergence
- `_hint`: failure-mode guidance (escalates after repeated failures)
- `error`: on failure, with "Did you mean?" suggestions when no match found

---

## REPLACE mode: the 9-strategy fuzzy match chain

Signature: `fuzzy_find_and_replace(content, old_string, new_string) -> (new_content, match_count, strategy_name, error)`.

Try strategies **in order**; stop at the first that yields ≥1 match. Each strategy returns a list of
`(start_offset, end_offset)` character spans in the **original** content.

| # | Strategy | Method |
|---|----------|--------|
| 1 | `exact` | Direct substring search. |
| 2 | `line_trimmed` | Strip leading/trailing whitespace per line, then match line-blocks. |
| 3 | `whitespace_normalized` | Collapse runs of spaces/tabs to a single space (preserve `\n`); match in normalized space, map spans back to original. |
| 4 | `indentation_flexible` | Ignore all leading whitespace per line. |
| 5 | `escape_normalized` | Convert literal `\\n` `\\t` `\\r` in the pattern to real bytes, then exact-match. Skip if pattern has no such escapes. |
| 6 | `trimmed_boundary` | Trim only the first and last lines of the block; keep the middle verbatim. |
| 7 | `unicode_normalized` | Normalize smart quotes/dashes/ellipsis to ASCII (`"` `"` → `"`, `—` → `--`, `…` → `...`) on both sides; exact, then line-trimmed; map spans back. |
| 8 | `block_anchor` | See algorithm below — anchor on first+last line, similarity on the middle. |
| 9 | `context_aware` | Sliding window of `len(pattern_lines)`; per-line `SequenceMatcher.ratio()` on stripped lines; block matches if ≥50% of lines have similarity ≥ 0.80. |

### Strategy 8 (`block_anchor`) — exact algorithm

```
norm_pattern = unicode_normalize(pattern); norm_content = unicode_normalize(content)
pattern_lines = norm_pattern.split('\n')
if len(pattern_lines) < 2: return []          # needs a block
first = pattern_lines[0].strip(); last = pattern_lines[-1].strip()
n = len(pattern_lines)
candidates = [i for i in range(len(content_lines)-n+1)
              if content_lines[i].strip()==first and content_lines[i+n-1].strip()==last]
threshold = 0.50 if len(candidates)==1 else 0.70     # tighter when ambiguous
for i in candidates:
    if n <= 2: similarity = 1.0
    else: similarity = SequenceMatcher(None,
              '\n'.join(content_middle), '\n'.join(pattern_middle)).ratio()
    if similarity >= threshold:
        emit span computed from ORIGINAL (non-normalized) line offsets
```

NOTE the threshold values `0.50`/`0.70` are deliberate. Earlier `0.10`/`0.30` were dangerously loose
(a 10%-similar middle matched unrelated blocks). Do not loosen.

### Strategy 9 (`context_aware`) thresholds

Per-line similarity gate `0.80`; block accepted if `high_similarity_count / pattern_line_count >= 0.50`.

### After a NON-exact match: three correction passes

When the matching strategy is anything other than `exact`, the matched file region differs from
`old_string`, so writing `new_string` verbatim would be wrong. Apply, in order:

1. **Escape-drift guard (reject, don't write).** If `old_string` and `new_string` both contain `\'` or
   `\"` but the matched file region does **not**, the transport injected spurious backslashes around
   quotes. Return an error instead of writing literal `\'` into source.
2. **Reindent `new_string`.** Compute each `new_string` line's indent *relative to the shallowest line
   of `old_string`*, then re-anchor that relative indent onto the matched region's actual base indent.
   Preserves the model's intended nesting while adopting the file's real indent width (e.g. model sent
   2-space, file is 4-space → output is 4-space).
3. **Conditional control-char unescape in `new_string`.** Convert `\\t`→tab only if the matched region
   *contains* a real tab; convert `\\r`→CR only if it contains a real CR. **Never** convert `\\n`
   (newlines serialize fine through JSON). This preserves legitimate literals like `sep = "\t"` in source.

### Uniqueness / multiplicity

- 0 matches → error + "Did you mean?" (closest fuzzy candidate shown).
- >1 match and `replace_all=false` → error: "Found N matches. Add context to make it unique, or set
  replace_all=true." Do not guess.
- `replace_all=true` → replace all. Apply replacements **end-to-start** so earlier offsets stay valid.

---

## PATCH mode: V4A multi-file format

```
*** Begin Patch
*** Update File: path/to/file.py
@@ optional context hint @@
 unchanged context line   (leading space)
-removed line             (leading minus)
+added line               (leading plus)
*** Add File: path/to/new.py
+line 1 of new file
+line 2 of new file
*** Delete File: path/to/old.py
*** Move File: old/path.py -> new/path.py
*** End Patch
```

- Headers: `*** Update File:`, `*** Add File:`, `*** Delete File:`, `*** Move File: a -> b`.
  `*** Begin Patch` optional, `*** End Patch` recommended terminator.
- Hunk lines: ` ` context, `-` remove, `+` add. `@@ ... @@` is an optional locator hint.
- **Security: reject `..` traversal** in any V4A header path (these come from model-generated text and
  are an injection vector). NOTE: relative `..` *is* allowed in the `path=` arg of replace mode — that's
  legitimate worktree navigation; the distinction matters.
- **Per-file locks, sorted order.** When a V4A patch touches multiple files, acquire per-file locks in
  sorted path order before applying, so concurrent subagents can't interleave on the same file / deadlock.

---

## Shared post-edit machinery (both modes)

1. **Post-write verification.** Re-read the file after writing; compare against intended content
   (normalize line endings; strip BOM for the compare). Catches silent FS/truncation failures. Error if
   bytes don't match.
2. **Line-ending + BOM preservation** (same as `write_file`).
3. **Delta-filtered syntax check + optional LSP** (same as `write_file` — only NEW errors surface).
4. **Unified diff** in the result.
5. **Staleness / workspace-divergence `_warning`s.**
6. **Consecutive-failure escalation.** Track `(task_id, resolved_path) -> consecutive_failures`. After
   3+ failures on the same file, set `_hint`: (a) re-read the file fresh (`old_string` may be stale),
   (b) use a longer/more-unique `old_string` with surrounding context, (c) fall back to `write_file`.

## Does NOT require a prior read

`patch` reads the file itself; it does not hard-require that `read_file` was called first. It only
*warns* on staleness. (Contrast with some agents that block edits to unread files.)

## Implementation checklist

- [ ] `mode` dispatch; per-mode required-arg validation.
- [ ] Implement all 9 strategies in order; first non-empty wins.
- [ ] Non-exact post-processing: escape-drift reject → reindent → conditional unescape.
- [ ] Uniqueness rule + `replace_all` end-to-start application.
- [ ] V4A parser with `..` rejection + sorted per-file locking.
- [ ] Post-write re-read verification; line-ending/BOM preservation.
- [ ] Delta-filtered lint; unified diff; staleness warnings.
- [ ] `(task_id, path)` failure counter → escalating `_hint`.
- [ ] "Did you mean?" suggestion on zero matches.

## Related

Shares lint-delta + staleness with `write_file`. Anchors on line numbers from `read_file`. Fall back to
`write_file` when a region is too hard to anchor.
