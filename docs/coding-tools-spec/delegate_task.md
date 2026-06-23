# Tool: `delegate_task`

Spawn one or more subagents in **isolated contexts**. Each child gets a fresh conversation (no parent
history), its own terminal/`task_id`, a restricted toolset, and a focused goal. Use for big tasks that
fan out (e.g. "fix these 5 independent bugs" → 5 parallel workers) or to keep a heavy subtask's tokens
out of the parent context.

## JSON Schema (exact, abridged — the dynamic parts are noted)

```json
{
  "name": "delegate_task",
  "description": "Spawn one or more subagents in isolated contexts. <rebuilt per definitions() call to show the user's current concurrency / depth limits>",
  "parameters": {
    "type": "object",
    "properties": {
      "goal":     {"type": "string", "description": "What the subagent should accomplish. Be specific and self-contained — the subagent knows nothing about your conversation history."},
      "context":  {"type": "string", "description": "Background the subagent needs: file paths, error messages, project structure, constraints. More specificity = better results."},
      "toolsets": {"type": "array", "items": {"type": "string"}, "description": "Toolsets to enable for this subagent. Default: inherit parent's. e.g. ['terminal','file'] for code, ['web'] for research."},
      "tasks":    {"type": "array", "description": "BATCH/parallel mode: array of {goal, context?, toolsets?, role?}. Runtime caps concurrency (configurable, default ~3).",
                   "items": {"type": "object", "properties": {
                     "goal":     {"type": "string"},
                     "context":  {"type": "string"},
                     "toolsets": {"type": "array", "items": {"type": "string"}},
                     "role":     {"type": "string", "enum": ["leaf", "orchestrator"]}
                   }, "required": ["goal"]}},
      "role":     {"type": "string", "enum": ["leaf", "orchestrator"], "description": "leaf (default): subagent cannot delegate further. orchestrator: may spawn its own children (bounded by max_spawn_depth)."}
    }
  }
}
```

Two shapes: **single** (`goal` [+`context`,`toolsets`,`role`]) or **batch** (`tasks: [...]` run in
parallel). The description is rebuilt per call to reflect the user's actual `max_concurrent_children`
(default ~3) and `max_spawn_depth`.

## Contract / behaviors

1. **Isolation.** Each child = fresh context (no parent transcript), own `task_id`, own terminal/cwd
   state, own background-process registry. The parent blocks until children finish, then receives their
   final outputs to incorporate.
2. **Self-contained goals.** Child sees only `goal` + `context`. The schema forces the agent to write
   standalone briefs — the most common failure is under-specified goals.
3. **Toolset restriction.** Default inherit parent's enabled toolsets; can narrow per task.
   **Always strip dangerous-in-children tools**: `delegate_task` (unless `role=orchestrator`),
   `clarify`, `memory`, `send_message`, `execute_code`. (Prevents runaway recursion, child UI prompts,
   and shared-state writes.)
4. **Roles.** `leaf` (default) cannot delegate further → bounds the tree. `orchestrator` may spawn
   grandchildren up to `max_spawn_depth`.
5. **Concurrency cap.** Enforce `max_concurrent_children`; queue or reject beyond it with a clear error.

## Why it matters

Parallelism + context hygiene. Big mechanical jobs (multi-file migration, N independent fixes) run
concurrently; each child's verbose intermediate work stays in *its* context, and only the distilled
result returns to the parent — keeping the parent's context clean and the wall-clock low.

## Implementation checklist

- [ ] Single + batch shapes; per-task `goal` required.
- [ ] Fresh `task_id` + isolated state per child; parent blocks then collects results.
- [ ] Toolset inherit/restrict; strip `delegate_task`(non-orch)/`clarify`/`memory`/`execute_code`/messaging.
- [ ] `leaf`/`orchestrator` roles + `max_spawn_depth`; `max_concurrent_children` cap.
- [ ] Dynamic description reflecting live limits.

## Related

`execute_code` for *scripted* fan-out (deterministic, no per-item agent). `todo` to track the items
being delegated.
