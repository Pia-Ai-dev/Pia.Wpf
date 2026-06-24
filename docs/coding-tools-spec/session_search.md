# Tool: `session_search`

Search past sessions stored in a local DB, or scroll inside one. FTS5 full-text retrieval over a SQLite
message store — **no LLM calls**, every shape returns real messages. Lets the agent recall prior work
("what did we decide about auth?") without carrying unbounded history in context.

## JSON Schema (exact, parameters)

```json
{
  "name": "session_search",
  "parameters": {
    "type": "object",
    "properties": {
      "query":              {"type": "string",  "description": "FTS5 query (DISCOVERY shape). Omit for browse/read/scroll."},
      "session_id":         {"type": "string",  "description": "Session to READ (alone) or SCROLL inside (with around_message_id)."},
      "around_message_id":  {"type": "integer", "description": "Message id to center a SCROLL window on."},
      "window":             {"type": "integer", "default": 5, "maximum": 20, "description": "Messages each side of the anchor (SCROLL)."},
      "limit":              {"type": "integer", "default": 3, "maximum": 10, "description": "Max sessions to return (DISCOVERY)."},
      "role_filter":        {"type": "string",  "description": "Comma-separated roles to include (default 'user,assistant')."},
      "sort":               {"type": "string",  "enum": ["newest", "oldest"], "description": "Temporal bias on FTS5 ranking (DISCOVERY)."},
      "profile":            {"type": "string",  "description": "Read from another profile's DB."}
    }
  }
}
```

## Four calling shapes (dispatch by which args are present)

1. **DISCOVERY** — `query` set. Run FTS5, dedupe hits by session lineage, return top `limit` sessions.
   Each result carries: `session_id`, `title`, `when`, `source`, `snippet` (highlighted excerpt),
   `bookend_start` (first 3 user+assistant msgs = the goal), `messages` (±5 around the match, anchor
   flagged), `bookend_end` (last 3 msgs = the resolution), `match_message_id`. Bookends + window let the
   agent reconstruct **goal → match → resolution** without reading the whole transcript.
2. **SCROLL** — `session_id` + `around_message_id` (+ `window`). Returns ±`window` messages around the
   anchor. No FTS5, no bookends. Scroll forward by passing `messages[-1].id` back; backward via
   `messages[0].id`. Boundary message appears in both windows as an orientation marker.
3. **READ** — `session_id` only. Dump the whole session (e.g. first 20 + last 10 when large).
4. **BROWSE** — no args. Recent sessions chronologically (titles, previews, timestamps) — for "what was
   I working on?".

## Behaviors / invariants

- **FTS5 semantics**: AND is the default between terms; support explicit `OR`. Snippet highlighting.
- **Source-first caveat** (baked into the description): this searches *conversation history only*, not
  live external sources. If the user gave a URL/file/account, inspect that source — don't conclude "not
  found" from session history alone.
- **Cheap**: pure DB/FTS5, zero model calls. Bounded output via windows + bookends, not full transcripts.
- `role_filter` defaults to `user,assistant` (skip tool/system noise). `profile` reads another profile's
  DB (drop if single-profile).

## Why it matters

Long-term recall without context bloat. The agent references concrete prior decisions/code by retrieving
just the relevant slice, instead of the host stuffing entire old conversations into the prompt.

## Implementation checklist

- [ ] SQLite message store + FTS5 index over message text.
- [ ] Shape dispatch by present args (query / id+anchor / id / none).
- [ ] DISCOVERY: dedupe by lineage; assemble snippet + bookends + ±window + match id.
- [ ] SCROLL: ±window slice with shared boundary message; forward/back via returned ids.
- [ ] READ: head+tail dump for large sessions. BROWSE: recent list.
- [ ] `role_filter`, `sort`, optional `profile`.

## Related

`memory` = distilled durable facts; `session_search` = raw recall of what was actually said/done.
