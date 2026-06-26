# Tool: `memory`

Save durable facts that survive across sessions. Memory is injected into every future turn's system
context, so entries must be compact and high-signal. Two stores: `memory` (agent's own notes) and
`user` (user profile).

## JSON Schema (exact)

```json
{
  "name": "memory",
  "description": "Save durable facts to persistent memory that survive across sessions. Memory is injected into every future turn, so keep entries compact and high-signal.\n\nHOW: make ALL changes in ONE call via an 'operations' array (each item: {action, content?, old_text?}). The batch applies atomically and the char limit is checked only on the FINAL result — so one call can remove/replace stale entries to free room AND add new ones. Use bare action/content/old_text only for a single lone change.\n\nWHEN: save proactively on a stated preference, correction, or stable fact about environment/conventions/workflow. Priority: user preferences & corrections > environment facts > procedures.\n\nIF FULL: an add is rejected with current entries shown; reissue as ONE batch that removes/shortens enough and adds together.\n\nTARGETS: 'user' = who the user is. 'memory' = your notes (environment, conventions, tool quirks, lessons).\n\nSKIP: trivial/obvious info, easily re-discovered facts, raw data dumps, task progress, completed-work logs, temporary TODO state. Reusable procedures belong in a skill, not memory.",
  "parameters": {
    "type": "object",
    "properties": {
      "action":   {"type": "string", "enum": ["add", "replace", "remove"], "description": "Single-op shape. Omit when using 'operations'."},
      "target":   {"type": "string", "enum": ["memory", "user"], "description": "'memory' = personal notes, 'user' = user profile."},
      "content":  {"type": "string", "description": "Entry content. Required for add/replace (single-op shape)."},
      "old_text": {"type": "string", "description": "Required for replace/remove (single-op): a short unique substring identifying the existing entry."},
      "operations": {"type": "array", "description": "Batch shape: list of operations applied atomically against the FINAL char budget. Preferred for multiple changes or consolidation.",
                     "items": {"type": "object", "properties": {
                       "action":   {"type": "string", "enum": ["add", "replace", "remove"]},
                       "content":  {"type": "string"},
                       "old_text": {"type": "string"}
                     }, "required": ["action"]}}
    }
  }
}
```

## Two stores (back with plain Markdown files)

- `memory` → e.g. `MEMORY.md`: agent's own notes (environment facts, tool quirks, conventions, lessons).
- `user` → e.g. `USER.md`: user profile (name, role, preferences, communication style).

Both are loaded as a **frozen snapshot into the system prompt at session start** (so the prefix stays
cache-stable mid-session; new writes take effect next session / next snapshot).

## Contract / behaviors

1. **Two shapes.** Single op (`action`+`target`+`content`/`old_text`) for one change; **`operations[]`
   batch** (preferred) for multiple. The batch applies **atomically** and the char-limit check runs
   only on the **final** result — so a single call can `remove` stale entries to free budget *and* `add`
   a new one even when the add alone would overflow.
2. **Char budget.** Each store has a max size. On overflow of a lone `add`, reject and return the
   current entries so the agent can reissue as a consolidating batch.
3. **`replace`/`remove` by substring.** `old_text` is a short unique substring identifying the target
   entry. Ambiguous/no match → error.
4. **Selectivity is the whole game.** The description hard-codes what to SKIP (task progress,
   completed-work logs, transient state, easily re-discovered facts, raw dumps) — because memory costs
   tokens on *every* turn forever. Reusable procedures belong in a skill, durable facts in memory.

## Why it matters

Stops the user repeating themselves and lets the agent carry project conventions / environment quirks
across sessions. The `user` vs `memory` split keeps "who the user is" separate from "what I learned."

## Implementation checklist

- [ ] Two Markdown-backed stores; load as frozen system-prompt snapshot at session start.
- [ ] Single-op and atomic `operations[]` batch; final-result char-limit check.
- [ ] `replace`/`remove` by unique `old_text` substring; ambiguity errors.
- [ ] Overflow → reject + echo current entries for consolidation.

## Related

NOT for task state — use `todo`. NOT for reusable procedures — those are skills. `session_search`
recalls past *conversations*; `memory` stores distilled *facts*.
