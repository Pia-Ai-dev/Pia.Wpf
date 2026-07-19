# Agent System — Roadmap & Status

_Snapshot: 2026-07-19._ Living index of what the Agent System has shipped and what is left to build.
Authoritative design: [`../2026-07-18-agent-system-phase1-plan.md`](../2026-07-18-agent-system-phase1-plan.md)
(referenced below as “the plan §N”).

Each remaining batch has its own file in this folder (`01-…` first). A batch is one workflow-sized unit:
branch from the prior batch's branch, implement behind the plan's guardrails, keep the build green, ship.

---

## Branch chain (each builds on the previous)

| Branch | Delivered | Status |
|--------|-----------|--------|
| `feature/agent-run-spine` | Phase 1.1 — persisted run/step spine (`AgentRun`/`AgentStep`, `IAgentRunService`) | ✅ done |
| `feature/agent-orchestration-loop` | Phase 1.2–1.4 — plan→act→replan loop, chat/agent lever, `suggest_agent_mode`, progress UI + `FlowAction.OpenRun` | ✅ done |
| `feature/agent-headless-runs` | Milestone B (plan §17) — headless/background runs, per-run workspace, scheduler emission, crash recovery | ✅ done |
| `feature/agent-mcp-gate` | Phase 2 — MCP through the approval gate (M1 interactive gate, M2 unattended grant gate, M3 destructive-MCP guard) | ✅ done |
| `feature/agent-verify-pass` | Phase 2 — `Verifying` is now a real terminal critic feeding the shared replan loop | ✅ done (this session) |
| `feature/agent-budget-pause` | Phase 2 — budget cap now pauses into `WaitingForInput` + working resume (both executors) + Flow | ✅ done (this session), **open follow-ups → Batch 01** |

**Nothing is pushed.** Build check everywhere: `dotnet build -p:EnableWindowsTargeting=true`.
Tests are **written but not run** on this Mac (net10.0-windows can't execute here) — defer `dotnet test` to Windows/CI.

---

## What's done (capability view)

- **Runs are first-class + persisted** — plan/act/replan loop, live progress panel + ledger, Flow `OpenRun` deep-link.
- **Headless/background runs** — detach a goal, per-run scratch workspace, scheduler emission, startup crash sweep.
- **MCP behind the gate** — interactive approval + unattended grant gate + a guard that never auto-approves destructive MCP calls.
- **Verify/critic pass** — a completed run is judged against its goal; a FAIL feeds the shared `MaxReplans` loop; exhaustion settles `Completed`+truncated `"unverified"`; degrade-safe (accept on fault).
- **Budget-pause → resume** — hitting the step/wall-clock cap parks the run `WaitingForInput` (both executors); a working Continue (panel button + Flow `ContinueRun` card) resumes it with a fresh budget grant; the ledger carries across; parked runs survive app restart.

---

## Upcoming batches (priority order)

| # | Batch | Phase | Size | Depends on |
|---|-------|-------|------|-----------|
| 01 | [Budget-pause polish](01-budget-pause-polish.md) — restart reachability, D2 test, nits | 2 | S | budget-pause |
| 02 | [Cost ledger](02-cost-ledger.md) — price table populates `CostUsd` | 2 | S | — |
| 03 | [Audit timeline](03-audit-timeline.md) — per-tool decision trace (plan §11) | 2 | M–L | — |
| 04 | [Autonomy policy](04-autonomy-policy.md) — `PolicyJson` per-run approval policy | 2 | M–L | MCP gate |
| 05 | [Planner reason-then-emit](05-planner-reason-then-emit.md) — boosted planning effort on Chat-Completions | 2 | S–M | — |
| 06 | [Run workspace isolation](06-run-workspace-isolation.md) — run-aware file-tool base root + promotion | 3 | M | Milestone B |
| 07 | [Sub-agents / multi-persona](07-subagents-multipersona.md) — `ParentRunId`/`AssignedPersonaId` + attribution | 3 | L | — |
| 08 | [Live steering](08-live-steering.md) — plan mutation / nudge / pause / resume | 4 | L | budget-pause, sub-agents |
| 09 | [Scheduler UI](09-scheduler-ui.md) — create/edit/list agent jobs | 4 | M | Milestone B |

Phase 2 completes at Batch 05. Batches 06–09 are Phase 3/4; their seams may shift — re-scope at the design step.

---

## How we implement a batch (the working pattern)

1. **Branch** `feature/agent-<name>` from the prior batch's branch (or `main` if it has merged).
2. **Read the as-built code first** — every batch fills a seam that already exists; the plan marks them.
3. **Author + run a workflow** (opus/sonnet/fable): Ground (map seams, read-only) → Design (opus, one spec) →
   Build (one or two sequential builders, commit per logical group, keep the build green) → Verify (opus attacks
   the guardrails, fable checks conventions + coverage, fix must-fixes) → Synthesize.
4. **Independently verify** — after the workflow, confirm the build green and spot-check the top guardrails
   yourself; fix any clear correctness gap the workflow left open.
5. **Commit per group, don't push.** Present decisions/assumptions/open items at the end.

**Standing guardrails (every batch):** failure-isolated bookkeeping (Safe* wrappers); no interactive regression
(the Live terminal settle stays correct); executor parity (Live + Headless); off-thread `RunChanged` stays
marshaled (G3); privacy-first logging (user content → `SensitiveDebug`, Flow Title/Body generic); append-only
persisted enums/ordinals. See CLAUDE.md + plan §12.5/§13.10/§16.
