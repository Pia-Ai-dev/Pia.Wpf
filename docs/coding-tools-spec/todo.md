# Tool: `todo`

A per-session task list the agent maintains for multi-step work. Critical property: it is **re-injected
into context after compression**, so the agent never loses the plan or repeats finished work on a long
task.

## JSON Schema (exact)

```json
{
  "name": "todo",
  "description": "Manage your task list for the current session. Use for complex tasks with 3+ steps or when the user provides multiple tasks. Call with no parameters to read the current list.\n\nWriting:\n- Provide 'todos' array to create/update items\n- merge=false (default): replace the entire list with a fresh plan\n- merge=true: update existing items by id, add any new ones\n\nEach item: {id: string, content: string, status: pending|in_progress|completed|cancelled}\nList order is priority. Only ONE item in_progress at a time.\nMark items completed immediately when done. If something fails, cancel it and add a revised item.\n\nAlways returns the full current list.",
  "parameters": {
    "type": "object",
    "properties": {
      "todos": {
        "type": "array",
        "description": "Task items to write. Omit to read current list.",
        "items": {
          "type": "object",
          "properties": {
            "id":      {"type": "string", "description": "Unique item identifier"},
            "content": {"type": "string", "description": "Task description"},
            "status":  {"type": "string", "enum": ["pending", "in_progress", "completed", "cancelled"], "description": "Current status"}
          },
          "required": ["id", "content", "status"]
        }
      },
      "merge": {"type": "boolean", "default": false, "description": "true: update existing items by id, add new ones. false (default): replace the entire list."}
    },
    "required": []
  }
}
```

## Contract

- **No args** → read: return the full current list.
- **`todos` provided** → write:
  - `merge=false` (default): replace the entire list with the provided plan.
  - `merge=true`: upsert by `id` (update matching ids, append new ones).
- **Always return the full current list** after any write (the agent reasons over the whole plan).
- Item: `{id, content, status ∈ pending|in_progress|completed|cancelled}`.

## Behavioral rules (encoded in the description — enforce softly)

- List **order is priority**.
- **Exactly one** item `in_progress` at a time.
- Mark `completed` immediately on finish; on failure, `cancel` and add a revised item.

## Why it matters

Two jobs: (1) keeps the agent focused and ordered on 3+ step tasks; (2) **survives context compression**
— the host re-injects the current list post-summary so the agent resumes exactly where it was. State is
in-memory, scoped to `task_id`.

## Implementation checklist

- [ ] In-memory list per `task_id`; read on empty args.
- [ ] Replace vs `merge` (upsert by id); always return full list.
- [ ] Validate item shape + status enum.
- [ ] Host: re-inject the list into context after compression/summarization.

## Related

`delegate_task` for farming items out to subagents. `memory` is for *durable* facts, NOT task state —
todos are ephemeral.
