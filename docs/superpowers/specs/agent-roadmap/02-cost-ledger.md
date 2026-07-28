# Batch 02 — Cost ledger (price table → `CostUsd`)

**Phase 2 · Size S · Work on `feature/agent-run-spine`** (the only ref this system was built on — see the
chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

The run ledger already accrues input/output tokens + wall-clock live (plan §5, Q7 transparency). Cost is the one
column left unpopulated — the panel renders `CostUsd` only when non-null, and today nothing sets it.

Two inputs got trustworthy in the hardening batch: `wallClockMs` is now accumulated **active** time (a parked
run no longer reports the hours it sat waiting), and plan/replan/verify/single-turn-fallback turns all accrue
run-level, so the token total is complete. What is still missing is per-phase attribution — every non-step turn
lands in the same run-level total with no marker distinguishing planning from verifying.

## Goal

Populate `Ledger.CostUsd` from a per-provider/per-model price table so the run-progress ledger strip shows a live
running cost, per-step and total.

## Key seams

- `RunProgressViewModel.cs:151` — `CostUsd = ledger.CostUsd; // TODO Phase 2: price table populates cost`
  (the line moved as the panel grew; it is the only `CostUsd` read in the UI).
- `AgentRunService.AddUsageAsync` / the `Ledger`/`StepLedger` DTOs — where token deltas are accrued; the natural
  place to also accrue a cost delta once a price lookup exists.
- The model label rides on `Finished(UsageDetails?, string Model, …)` (`Models/ChatStreamItem.cs`) — the per-round
  usage carries the model, so cost can be computed at accrual time.
- `AiProvider` — provider identity/type; the price table keys off provider + model.

## Decisions to resolve

- **Price source:** a static bundled table (per model, input/output $/1M tokens) vs. a user-editable setting.
  Recommend a static table with an override hook; note staleness.
- **Where cost is computed:** at accrual (`AddUsageAsync` stores a cost delta into the ledger) vs. at render
  (VM multiplies tokens × rate). Recommend at accrual so `LedgerJson` is self-describing and headless runs record
  cost too.
- **Unknown model/provider:** leave `CostUsd` null (panel already hides it) — never guess.

## Guardrails

- Additive only; `LedgerJson` stays backward-compatible (append fields, F5 camelCase).
- Cost is best-effort metadata beside the ledger, never on a run's critical path.
- No sensitive data (model names are fine; nothing user-content here).

## Tests

- Accrual computes the expected cost for a known model; unknown model → null cost, tokens still accrue.
- Round-trips through `LedgerJson` (writer/reader camelCase parity with `RunProgressViewModel`).

## Acceptance

The ledger strip shows a live cost for known providers; unknown providers show tokens/time only; build green.
