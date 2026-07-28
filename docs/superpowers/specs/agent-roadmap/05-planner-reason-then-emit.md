# Batch 05 — Planner reason-then-emit (boosted planning effort)

**Phase 2 · Size S–M · Work on `feature/agent-run-spine`** (see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

Planning uses a constrained `emit_plan` **tool** call. On **Chat-Completions** providers, `ReasoningEffortMapping`
(`ToOpenAi`) omits the reasoning-effort param when tools are present — so the plan turn reasons at *default* effort,
not boostable (plan §4 line 199-205, §13.3 line 678-679). Responses-API providers already get both. The plan flags
a two-call **reason-then-emit** as the Phase-2 optimization to recover boosted effort on Chat-Completions.

## Goal

On Chat-Completions providers, optionally run planning as **two turns** — a free-form reasoning turn at boosted
effort, then a constrained `emit_plan` turn — so weak/local providers plan better, without changing the
tool-constrained reliability the current single-turn path guarantees.

## Key seams

- `AgentPlanner.cs` — `BuildPlanMessages` / `TryCaptureAsync` (the single constrained turn today); `PlanAsync`
  is where the two-call branch lives.
- `ReasoningEffortMapping` / `ToOpenAi` — the reason the single tool-turn can't boost effort on Chat-Completions.
- Provider capability (`IProviderCapabilityService`, Responses-API vs Chat-Completions) — gates which path runs.
- Note plan §16 R6: the constrained turn already costs ≥1 extra provider round; two-call **doubles** plan-turn
  cost — so this is opt-in / capability-gated, not the default for cheap providers.

## Decisions to resolve

- **When to two-call:** only Chat-Completions providers where boosted effort matters; Responses-API keeps the
  single combined turn. A setting/threshold to enable it (cost is real).
- **Degrade:** if the reasoning turn is empty/fails, fall back to today's single constrained turn — never hard-fail
  planning (mirrors the existing R10 degrade to `SingleTurn`).
- Applies to `ReplanAsync` too, or plan-only this batch? Recommend plan-only first.

## Guardrails

- No reliability regression: the constrained `emit_plan` turn + its validation + R10 degrade path are unchanged.
- Cost-aware: two-call is opt-in/gated and `log()`s that it doubled the plan-turn cost.
- Sensitive: plan text still only via `SensitiveDebug`.

## Tests

- Chat-Completions + enabled → two turns; the emitted plan still validates.
- Responses-API → single turn (unchanged).
- Reasoning turn fails → single-turn fallback still yields a valid plan.

## Acceptance

Chat-Completions plans reason at boosted effort when enabled; single-turn reliability + degrade unchanged; build green.
