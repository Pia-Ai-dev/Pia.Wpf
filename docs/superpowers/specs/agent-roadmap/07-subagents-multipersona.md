# Batch 07 — Sub-agents / multi-persona

**Phase 3 · Size L · Work on `feature/agent-run-spine`** (see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

> **✅ SHIPPED 2026-07-31 as work groups G6–G10, `08e20ab` → `1d6cc15`** — and **read that range with the
> chronicle open, because the groups did not land in group order**: G6 (`08e20ab`), then G9 (`b2f46a2`), then
> G10's fan-out (`9c32999`), then G7 (`d09c71f`), then G8 (`3e12bcf`), then the rest of G10's own recorded debt
> (`1d6cc15`). Six commits inside that span belong to no batch or to Batch 06. The joint fix pass that follows
> (`29b6e3f` → `37a0410`) closes this batch's review findings and Batch 06's together. The **executable** spec is
> [`07-subagents-multipersona.impl.md`](07-subagents-multipersona.impl.md), which carries a BUILDER RECORD with
> **nine** recorded divergences plus in-place spec corrections from G8 and the fix pass; the measured seam map is
> in [`phase3-workflow-plan.md`](phase3-workflow-plan.md) §2, and §3.6/§3.8 are why this file's "attribution is
> already seamed" and "sweep tweak" framing understate the work. The prose below is the original scoping and is
> *not* the as-built record.

The spine reserved `AgentRun.ParentRunId` and `AgentStep.AssignedPersonaId` for sub-agents/multi-persona
(plan §2 line 82/112, §9 line 356-357). Today `ResolveActiveAsync` is single-persona-per-mode. This is the
"Council-for-work" batch: a run can spawn child runs / assign steps to distinct personas, each honoring its own
persona's provider (plan §5 line 270), with per-step attribution in the UI.

## Goal

Let a run delegate steps to sub-agents/personas: child runs linked via `ParentRunId`, steps tagged with
`AssignedPersonaId`, multi-persona resolution, and each persona running on its own provider — surfaced with
per-step avatars in the progress panel + timeline.

## Key seams

- `ResolveActiveAsync` (persona service) — extend from single-persona-per-mode to multi-persona resolution.
- `AgentRun.ParentRunId` / `AgentStep.AssignedPersonaId` — already persisted; wire the orchestrator to set/use them.
- `AgentRunOrchestrator` — spawn/await child runs; budget + ledger roll-up from children to parent.
- `RunProgressViewModel` / `StepRowViewModel.AssignedPersonaId` + `PiaPersonaAvatar` — attribution is already
  seamed (plan §15.1 line 933, §7.2 line 298); `AccentColor` multi-persona differentiation (§15.6 line 998).

## Decisions to resolve (design-heavy — expect a full design phase)

- **Topology:** static-first (Q4, plan line 26) — a fixed team shape before dynamic delegation.
- **Budget/ledger:** how child-run budgets nest under the parent envelope; how the ledger rolls up.
- **Cancellation/failure:** parent cancel propagates to children; a child failure's effect on the parent
  (feeds the parent's replan? fails the parent?).
- **Concurrency:** parallel sub-agents vs sequential; reuse the headless slot semaphore.

## Guardrails

- All prior guardrails compound: failure isolation, executor parity, off-thread `RunChanged`, privacy, budgets.
- No orphaned child runs; the startup crash sweep must handle parent/child correctly (interacts with the
  `WaitingForInput`/`Paused` parking from Batch 01/budget-pause).

## Acceptance

A run can delegate steps to distinct personas/child runs with correct attribution, budget roll-up, and
cancellation semantics; build green. (Re-scope seams at design time — this is the largest remaining batch.)
