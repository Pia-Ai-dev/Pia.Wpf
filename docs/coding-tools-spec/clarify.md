# Tool: `clarify`

Ask the user a question when clarification, a decision, or feedback is needed before proceeding. Two
modes: multiple-choice (≤4 options) or open-ended (free text). The agent's only sanctioned channel for
blocking on human input mid-task.

## JSON Schema (exact)

```json
{
  "name": "clarify",
  "description": "Ask the user a question when you need clarification, feedback, or a decision before proceeding. Supports two modes:\n\n1. Multiple choice — provide up to 4 choices. The user picks one or types their own via a 5th 'Other' option.\n2. Open-ended — omit choices entirely. The user types a free-form response.\n\nCRITICAL: when offering options, put each option ONLY in the `choices` array — NEVER enumerate options inside `question` text. The UI renders `choices` as selectable rows; options written into the question render as dead prose the user can't pick. Right: question='Which deployment target?', choices=['staging','prod']. Wrong: question='Which target? 1) staging 2) prod', choices=[].\n\nUse when: the task is ambiguous and you need the user to choose an approach; you want post-task feedback; you want to offer to save a skill / update memory; a decision has meaningful trade-offs. Do NOT use for simple yes/no confirmation of dangerous commands (the terminal tool handles that). Prefer a reasonable default yourself when the decision is low-stakes.",
  "parameters": {
    "type": "object",
    "properties": {
      "question": {"type": "string", "description": "The question itself, and ONLY the question (e.g. 'Which deployment target?'). Do NOT embed the answer options here."},
      "choices":  {"type": "array", "items": {"type": "string"}, "maxItems": 4, "description": "REQUIRED whenever presenting selectable options: each distinct option is its own array element (up to 4). The UI renders them as pickable rows and auto-appends an 'Other (type your answer)' option. Omit entirely ONLY for a genuinely open-ended free-text question."}
    },
    "required": ["question"]
  }
}
```

## Contract / behaviors

1. **Two modes by presence of `choices`.** With `choices` (≤4): multiple-choice; the UI auto-appends a
   5th "Other (type your answer)" row. Without `choices`: open-ended free text.
2. **Question vs choices separation is enforced.** `question` holds ONLY the question; options go in
   `choices`. Reject/repair calls that enumerate options inside the question string (dead prose the user
   can't click). This is the single most common misuse.
3. **Blocking round-trip.** The tool blocks the agent loop until the user answers (or cancels), then
   returns the chosen/typed text as the tool result. The host needs a UI bridge for this (CLI arrow-key
   list, or numbered list on a messaging platform).
4. **Arg normalization.** Some models emit dict-shaped choices (`{"label": "..."}`) — flatten to the
   user-facing string. Coerce/validate before rendering.
5. **Usage discipline (in the description).** Use for genuine ambiguity / trade-off decisions / offering
   to save skill/memory. Do NOT use for dangerous-command confirmation (the terminal guard owns that),
   and prefer a sensible default for low-stakes choices instead of interrupting.

## Why it matters

Keeps the agent from silently guessing on ambiguous, high-stakes forks, while the usage rules stop it
over-asking on trivial decisions. Structured choices make answering one keystroke.

## Implementation checklist

- [ ] Mode by `choices` presence; ≤4 choices + auto "Other".
- [ ] Reject/repair options embedded in `question`.
- [ ] Blocking UI round-trip; return the answer string as the tool result.
- [ ] Normalize dict-shaped choices to strings.

## Related

For dangerous-command yes/no, rely on the `terminal`/`execute_code` approval guard, not `clarify`.
Subagents (`delegate_task`) should have `clarify` stripped — only the top-level agent talks to the user.
