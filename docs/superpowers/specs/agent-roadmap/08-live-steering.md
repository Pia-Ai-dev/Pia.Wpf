# Batch 08 — Live steering (plan mutation / nudge / pause / resume)

**Phase 4 · Size L · Branch from the latest branch (after budget-pause + sub-agents)**

The run-progress plan is **read-only** in Phases 1–3; the mutation API shape is reserved for Phase 4
(plan §2 Q2 line 20, §7.2 line 300, §9 line 363-364, §15.6 line 1000). This batch makes the plan steerable.

## Goal

Let the user steer a live run: edit/reorder/insert/skip pending steps, nudge the agent mid-run, and
user-initiate **pause/resume** — turning the reserved `AgentRunState.Paused` (4) into a driven state (distinct
from the system-driven `WaitingForInput` budget-pause from the budget-pause batch).

## Key seams

- `RunProgressViewModel` / `RunProgressPanel.xaml` — today read-only; add the mutation commands (the Continue
  button from the budget-pause batch is the first sanctioned interaction — extend that surface).
- `AgentRunState.Paused` (4) — already persisted + rendered (inert since the budget-pause batch); wire a
  user-initiated pause/resume that drives it (reuse `TryBeginResume`/`PauseAsync` from budget-pause).
- `IAgentRunService.ReplaceStepsAsync` / step mutation — a user-facing, validated mutation API over pending
  steps (Done steps stay immutable, per §13.2 KeepDone).
- The orchestrator loop — honor mid-run mutations on the next R2 re-query; a "nudge" injects context.

## Decisions to resolve

- **`Paused` vs `WaitingForInput`:** `Paused` = user-initiated hold; `WaitingForInput` = system awaiting input
  (budget). Keep them distinct; both resume through the same claim machinery.
- **Mutation safety:** only pending steps mutable; concurrent orchestrator loop vs UI edit needs the same
  CAS/atomicity discipline as resume (no double-run, no torn plan).
- **Nudge shape:** a transient user-context injection vs a plan edit.

## Guardrails

- Reuse the resume-once CAS + Safe* discipline; a steer must never corrupt or double-run a live loop.
- No interactive regression; off-thread safety; privacy (nudge text is user content).

## Acceptance

A user can pause/resume and safely edit the pending plan of a live run; `Paused` is a real driven state; build green.
