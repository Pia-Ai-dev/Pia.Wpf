# Agent System — Roadmap & Status

_Snapshot: 2026-07-28 (as-built at the hardening batch's fix-up pass, on top of `54e1f43`)._
Living index of what the Agent System has shipped and
what is left to build. Authoritative design:
[`../2026-07-18-agent-system-phase1-plan.md`](../2026-07-18-agent-system-phase1-plan.md)
(referenced below as “the plan §N”). The open items in the last section come from
[`hermes-comparison.md`](hermes-comparison.md) — the external review that drove the hardening batch.

Each remaining batch has its own file in this folder (`01-…` first). A batch is one workflow-sized unit:
implement behind the plan's guardrails, keep the build green, ship.

---

## Batch chronicle (all linear on `feature/agent-run-spine`)

Earlier revisions of this file presented six *branches*. That was never true: **`feature/agent-run-spine`
is the only ref that ever existed** — every batch below is a commit range on it, not a branch, and nothing
was ever branched from `feature/agent-orchestration-loop` / `-headless-runs` / `-mcp-gate` / `-verify-pass`
/ `-budget-pause`. Those names exist only in older prose (and in a plan §17.4 note), so any instruction to
"branch from" one of them is unactionable — read them as batch labels.

| # | Batch (first → last commit, inclusive) | Delivered | Status |
|---|----------------------|-----------|--------|
| 1 | `76da0ee` → `3900510` | Phase 1.1 — persisted run/step spine (`AgentRun`/`AgentStep`, `IAgentRunService`) | ✅ done |
| 2 | `ed3c01d` → `96b2347` | Phase 1.2–1.4 — plan→act→replan loop, chat/agent lever, `suggest_agent_mode`, progress UI + `FlowAction.OpenRun`, configurable budgets | ✅ done |
| 3 | `092b4e0` → `7be1f59` | Milestone B (plan §17) — headless/background runs, per-run scratch dir, scheduler emission, crash recovery | ✅ done |
| 4 | `ed030f2` → `c62bc97` | Phase 2 — MCP through the approval gate (M1 interactive gate, M2 unattended grant gate, M3 destructive-MCP guard) | ✅ done |
| 5 | `d0e1227` → `8a11de4` | Phase 2 — `Verifying` is a real terminal critic feeding the shared replan loop | ✅ done |
| 6 | `1a819b9` → `093fe18` | Phase 2 — budget cap parks the run into `WaitingForInput` + working resume (both executors) + Flow | ✅ done |
| 7 | `e7df175` | This roadmap folder | ✅ done |
| 8 | `19c7a03` → `HEAD` | **Hardening batch** (this one) + its review fix-up pass — see “What the hardening batch closed” below | ✅ done |

**Git position:** the branch **is** pushed — `origin/feature/agent-run-spine` exists and the branch tracks
it; the last pushed commit is `e7df175`. Everything from `19c7a03` onward (the hardening batch) is
**local-only**. Check with `git rev-list --count origin/feature/agent-run-spine..HEAD` and `git branch -vv`.
Build check everywhere: `dotnet build -p:EnableWindowsTargeting=true`. Tests are **written but not run** on
this Mac (net10.0-windows can't execute here) — defer `dotnet test` to Windows/CI.

---

## What's done (capability view)

- **Runs are first-class + persisted** — plan/act/replan loop, live progress panel + ledger, Flow
  `OpenRun` deep-link. Ledger wall-clock is accumulated **active** time (parked time is not billed).
- **Headless/background runs** — detach a goal, scheduler emission, startup crash sweep. The per-run
  `%LOCALAPPDATA%\Pia\runs\<runId>\` directory is an **ephemeral scratch dir that nothing currently
  writes into**: by owner decision (`d1bf62d`, plan §17.2 amendment) unattended runs write their real
  deliverables to the **shared assistant files folder**, and both launch and resume pass
  `HeadlessTurnExecutor.Initialize(workspaceRoot: null, …)`. Isolation + promotion is still Batch 06.
- **Unattended writes are narrow by default** — `HeadlessRunRequest.DefaultGrantedWrites` is `{write_file}`
  (no `delete_file`); a resume restores the launch's own grant list from the envelope persisted in
  `AgentRuns.PolicyJson`, so parking cannot widen what the launch granted. **Both** producers write one: the
  headless launcher stores its resolved set, and the interactive Agent-mode create stores an *empty* set
  (an interactive run holds no standing grant — every write is a card the user clicks), so an interactive-origin
  resume grants nothing rather than picking up the fallback floor. The `{write_file}` floor now applies only
  to runs created before D1 or with an unreadable envelope.
- **MCP behind the gate** — interactive approval + unattended grant gate + a destructive-tool guard that
  now covers **both** paths: interactively it never auto-approves a destructive MCP call, and unattended it
  refuses a *granted* tool that is both delete-like and external (fail-closed if MCP-ness can't be derived).
  The delete-like rule covers the whole destructive stem family, not just "delete".
- **Verify/critic pass** — a completed run is judged against its goal, with each step's declared
  `ExpectedArtifact` probed against the effective file root (the assistant files folder, narrowed by the
  chat's working subpath the same way the file tools narrow it) and the found/NOT-FOUND facts fed into the
  verify prompt as app-established facts. Both free-text fields interpolated into a fact line (the
  declaration and the step title) are flattened + capped, so planner/model text cannot forge a fact line.
  A FAIL feeds the shared `MaxReplans` loop; exhaustion settles
  `Completed`+truncated `"unverified"` (the panel now says *“Result not verified”*, not “Stopped at
  budget”); degrade-safe (accept on fault).
- **Budget-pause → resume** — hitting the step/wall-clock cap parks the run `WaitingForInput` (both
  executors); a working Continue (panel button + Flow `ContinueRun` card) resumes it with a fresh budget
  grant; the ledger carries across. Parked runs **survive app restart and are reachable again**: a headless
  run keeps its durable Flow card, and activating a hydrated chat re-attaches its newest non-terminal
  `Planned` run so the panel + Continue come back.
- **A resumed run sees its own history** — each completed step's transcript is durable (both executors), and
  a resume seeds the pre-pause steps into the run context so the critic and any replan see the whole run.

---

## What the hardening batch closed (`19c7a03` → `HEAD`)

Driven by [`hermes-comparison.md`](hermes-comparison.md) §4(b)/§7. Closed here: the resume
grant-escalation (persisted grant envelope on both producers + narrowest fallback), the ledger wall-clock
inflation (accumulated active time), the narrow unattended default grant, the broadened destructive-tool rule
applied to the unattended gate, the parked-scheduled-job relaunch loop **for recurring jobs** (a
`RecurrenceType.Once` job still cannot advance — see below), planner/replan/fallback token accrual, the
evidence-anchored verdict, per-step transcript durability + resume context seeding, parked-run reachability
after restart, the truncation copy lie, and the DE/FR string gap.

A follow-up fix-up pass then closed the review findings on that batch: the artifact-fact block sanitizes the
step title as well as the declaration, its 2 s probe budget now covers the root resolution (a dead network
share used to block outside it), the probe honours the chat's working subpath (it was reporting confident
false NOT-FOUNDs for every artifact of a subpath-scoped chat), refused destructive grant *names* moved from
`LogWarning` to `SensitiveDebug`, the interactive create persists its grant envelope, `_inflight` teardown is
keyed per dispatch, and the never-executed H1 test assertions were corrected.

---

## Upcoming batches (priority order)

| # | Batch | Phase | Size | Depends on |
|---|-------|-------|------|-----------|
| 01 | [Budget-pause polish](01-budget-pause-polish.md) — residual nits (most items closed by the hardening batch) | 2 | XS | — |
| 02 | [Cost ledger](02-cost-ledger.md) — price table populates `CostUsd` | 2 | S | — |
| 03 | [Audit timeline](03-audit-timeline.md) — per-tool decision trace (plan §11) | 2 | M–L | — |
| 04 | [Autonomy policy](04-autonomy-policy.md) — `PolicyJson` per-run approval policy | 2 | M–L | MCP gate |
| 05 | [Planner reason-then-emit](05-planner-reason-then-emit.md) — boosted planning effort on Chat-Completions | 2 | S–M | — |
| 06 | [Run workspace isolation](06-run-workspace-isolation.md) — run-aware file-tool base root + promotion | 3 | M | Milestone B |
| 07 | [Sub-agents / multi-persona](07-subagents-multipersona.md) — `ParentRunId`/`AssignedPersonaId` + attribution | 3 | L | — |
| 08 | [Live steering](08-live-steering.md) — plan mutation / nudge / pause / resume | 4 | L | budget-pause, sub-agents |
| 09 | [Scheduler UI](09-scheduler-ui.md) — create/edit/list agent jobs | 4 | M | Milestone B |

Phase 2 completes at Batch 05. Batches 06–09 are Phase 3/4; their seams may shift — re-scope at the design
step. `PolicyJson` is no longer NULL — it carries the launch grant envelope — so Batch 04 must *extend* that
document, not claim the column.

---

## Deliberately open (known, not oversights)

Each of these was seen and left; the reason is the point.

- **Scheduler head-of-line block.** `ScheduledJobBackgroundService.ExecuteAgentTaskAsync` still `await`s
  `handle.Completion` (`:199`) inside the tick loop, so one long agent job delays every other due job for up
  to its wall-clock budget. **Owner-deferred, not missed** — the fix is a continuation-based dispatch
  (bookkeeping moved off the tick), which changes the job-completion contract; the hardening batch only made
  a *parked* run stop re-launching every tick. See hermes-comparison §4(b)(2)/§8² and rec #2.
- **No structured step-result signal.** Step success is still `!string.IsNullOrWhiteSpace(exchange.Visible)`
  (`HeadlessTurnExecutor.cs:247`), so a step that politely explains its own failure records `Done` and the
  failure-only replan never fires. `RunContext.Scratchpad` (`RunContext.cs:75`) is declared and read/written
  nowhere — the seam for a real `emit_step_result{succeeded, artifactRef}` already exists. The H1 artifact
  probe narrows the blast radius (a missing declared artifact now reaches the critic) but does **not** make a
  step's own verdict structured. hermes-comparison §5/rec #9.
- **Read-through workspace isolation + promotion (Batch 06).** Unattended runs still write real deliverables
  into the shared assistant folder; the per-run dir stays scratch. A1 narrowed the default grant to
  `{write_file}` and B2 blocks destructive external tools, which lowers the risk but does not isolate.
  hermes-comparison §4(b)(3)/rec #6.
- **A `RecurrenceType.Once` job still re-launches while its run is parked.** `AdvanceMissedRunAsync` recomputes
  `NextFireAt` via `RecurrenceCalculator.ComputeNextFireAt`, which for `Once` returns the same
  `specificDate + timeOfDay` — still in the past — so the job stays due and the next 30 s tick fires it again
  (F closed this for recurring jobs only). Nothing deactivates a `Once` job after it fires either, so the same
  hole predates the batch on the `MarkRunComplete` path. Not fixed here because the honest fix is a lifecycle
  change (deactivate a fired `Once` job, as `ReminderService` already does for `Once` reminders), which needs
  its own decision — a "never return a past instant" clamp would only convert the loop into a silent re-fire.
- **Two writers on one chat row.** A hydrated live `ChatSession` and a headless resume executor each do a FULL
  chat replace from their own private message list, so whichever writes last deletes the other's rows and
  leaves `AgentStep.First/LastMessageId` pointing at nothing (no FK, so nothing complains). Reachable since C2
  made a parked run re-attach to a hydrated chat: the user can type in a chat whose attached run is in-flight
  headlessly. The fix is either routing a resume back into the live session or refreshing/queueing the live
  session's writes while its run is in flight — a design step, not a patch.
- **The shared `SqliteContext` connection has no write gate, and E2 raised the collision rate.**
  `AssistantChatService` wraps its upsert + DELETE-all + re-INSERT in `connection.BeginTransaction()` on the
  single process-wide connection `SqliteContext.GetConnection()` hands out, and Microsoft.Data.Sqlite rejects a
  nested `BeginTransaction` *and* any command whose `Transaction` differs from the pending one. Per-step interim
  persistence moved that window from once per run to once per completed step, on pool threads, with a slot cap
  of 2 concurrent headless runs — so a concurrent chat write/read (the user's own Send, a history search) can
  throw and be swallowed as "failed to persist". `AgentRunService` is NOT exposed: it has its own dedicated
  connection + lock. The real fix is a write gate owned by `SqliteContext` (or a dedicated connection for
  `AssistantChatService`, as `FlowPersistenceStore` has), which affects every service on that connection.
- **Context/trajectory compression — absent from the code AND from all nine batch specs**, even though the
  design doc ([`../2026-07-18-agent-system-design.md`](../2026-07-18-agent-system-design.md)) §12 makes it the
  gate on long runs: "only with compaction does *run for 40 steps* become reachable without blowing context".
  Today a provider context overflow surfaces as a generic failed step that burns a replan. Pia's per-step
  exchange isolation is genuine implicit compression, so the fix is a lightweight batch (per-exchange token
  estimation, in-step tool-result truncation, a running brief of prior-step visibles) — **not** a port of a
  3,500-line engine. It has no batch file yet, deliberately, because it needs a design step first; it should
  land before Batch 07 multiplies the surface. hermes-comparison §5/rec #5.

---

## How we implement a batch (the working pattern)

1. **Work on `feature/agent-run-spine`** (or a fresh `feature/agent-<name>` off it / off `main` once it has
   merged). Do **not** branch from a batch label — the batch "branches" in the chronicle above never existed.
2. **Read the as-built code first** — every batch fills a seam that already exists; the plan marks them.
   Where the plan and the code disagree, the code wins; the plan carries dated “As-built at `<sha>`” notes.
3. **Author + run a workflow** (opus/sonnet/fable): Ground (map seams, read-only) → Design (opus, one spec) →
   Build (one or two sequential builders, commit per logical group, keep the build green) → Verify (opus attacks
   the guardrails, fable checks conventions + coverage, fix must-fixes) → Synthesize.
4. **Independently verify** — after the workflow, confirm the build green and spot-check the top guardrails
   yourself; fix any clear correctness gap the workflow left open.
5. **Commit per group, don't push.** Present decisions/assumptions/open items at the end.

**Standing guardrails (every batch):** failure-isolated bookkeeping (Safe* wrappers); no interactive regression
(the Live terminal settle stays correct); executor parity (Live + Headless); off-thread `RunChanged` stays
marshaled (G3); privacy-first logging (user content → `SensitiveDebug`, Flow Title/Body generic); append-only
persisted enums/ordinals; a new user-visible string lands in `ViewStrings.resx` **and** `.de.resx` **and**
`.fr.resx`. See CLAUDE.md + plan §12.5/§13.10/§16.
