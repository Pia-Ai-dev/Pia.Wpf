# Batch 03 — Audit timeline (per-tool decision trace)

**Phase 2 · Size M–L · Branch from the latest Phase-2 branch**

The run-progress panel is the live **plan tracker + ledger**, deliberately _not_ the full audit trail
(plan §15 line 925-926, §15.6 line 998: "The full audit timeline with per-tool decisions (Phase 2 §11)").
This batch adds the richer trace.

## Goal

A per-run **timeline** of what actually happened at tool granularity: each tool call, its approval decision
(gate: approved/denied/auto, from the MCP-gate + autonomy work), the outcome, and the step it belonged to —
so a finished run (especially a headless one) is auditable after the fact.

## Key seams

- `IAgentTurnExecutor.ExecuteStepAsync` / the per-step tool loop in `AiClientService` — where tool calls are
  dispatched; the point to emit timeline events.
- The approval gate (from `feature/agent-mcp-gate`) — the decision (approved/denied/auto/destructive-blocked) is
  the key audit fact.
- `AgentStep` + `AgentRun` persistence (`AgentRunService`) — where a timeline event store hangs off the run/step.
- `RunProgressViewModel` / a new detail view — render surface (read-only, like the panel).

## Decisions to resolve

- **Storage:** a new `AgentTimelineEvent` table (run/step FK, kind, decision, timestamp, sanitized payload ref)
  vs. structured entries in `ExtraJson`. Recommend a table — it's queryable and append-only.
- **Granularity/retention:** every tool call vs. only gated/decision events; a cap + eviction to keep run tables
  small (plan §2 keeps run tables lean).
- **Payload privacy:** tool args/results are SENSITIVE — the timeline stores **references/metadata**, not raw
  payloads, or gates raw payloads behind `#if DEBUG`. Never persist user-content payloads in release.

## Guardrails

- Privacy-first: no tool args/results/goal text in the persisted timeline or logs (only via `SensitiveDebug`).
- Best-effort, off the critical path — emitting a timeline event must never fail a step (Safe* wrapper).
- Executor parity — Live + Headless emit the same events.
- Off-thread safe (headless emits off the UI thread).

## Tests

- A run with N tool calls (some denied) produces N ordered events with correct decisions + step attribution.
- No sensitive payload leaks into the store (assert the persisted rows carry only metadata).

## Acceptance

A completed run exposes an ordered, privacy-safe, per-tool audit timeline; run tables stay bounded; build green.
