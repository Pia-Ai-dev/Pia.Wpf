# Agent roadmap — code left to write

**This folder is two files and nothing else.** They exist because
[`../agent-roadmap/00-OVERVIEW.md`](../agent-roadmap/00-OVERVIEW.md) grew to 302 KB and became a *provenance
record* rather than a worklist.

- **This file** — the code still owed to finish the roadmap.
- [`02-ui-check-plan.md`](02-ui-check-plan.md) — the manual round to run in the UI now.

**Rule for both files: they carry pointers, not arguments.** Every item names where its reasoning lives. Do not
copy reasoning in here; do not re-adjudicate a Tier 3 item without reading its source first. No commit hashes,
no test counts, no "N items" totals — those are the four things the overview had to correct about itself.

---

## The headline: against the initial plan, the spine is built

[`../2026-07-18-agent-system-phase1-plan.md`](../2026-07-18-agent-system-phase1-plan.md) §9 is the plan's own
list of what was *seamed, not built*. Checked line by line, all six are now built:

| Plan §9 deferred item | Built by |
|---|---|
| Verify/critic pass | hardening batch + the H1 artifact probe |
| Budget enforcement / pausing into `WaitingForInput` | hardening batch (budget-pause → resume) |
| MCP gate fix (deferred `PluginToolCall`) | shipped; §9 is annotated as-built in the plan itself |
| Sub-agents / Council-for-work | Batch 07 |
| Out-of-folder per-run workspace + promotion | Batch 06 |
| Plan editability / live steering (nudge/pause/resume) | Batch 08 |

**§18 (Parallel job execution) is the only section of the plan still labelled "planned, not built"** — and half
of it shipped on 2026-08-03. That is Tier 1 below.

Everything else on this list comes from two places that are *not* the plan: the
[`hermes-comparison.md`](../agent-roadmap/hermes-comparison.md) §5/§7 review, and defects opened by batches as
they shipped.

**Two stale statements in the overview to ignore, both verified here.** (i) Its "Deliberately open" section's
first two bullets — the scheduler head-of-line block and the missing structured step result — were closed or
narrowed by the 2026-08-03 sweep; read the 2026-08-03 addendum, not that section. (ii) Every "local-only,
unpushed, this pass did not push" paragraph is stale: on 2026-08-03,
`git rev-list --count origin/feature/agent-run-spine..HEAD` is **0** on a clean tree. Read the position from
git, always.

---

## Tier 0 — confirmed defects, blocking "done"

Both are the residue of hermes #2. They are the only items on this whole page that are known-wrong behaviour
rather than unbuilt scope.

- [ ] **T0-1 — a crashed scheduled firing never reconciles its job row.** `FailInterruptedRunsAsync`
  (`AgentRunService.cs:569`) settles crash-recoverable runs to `Cancelled` and touches only
  `AgentRuns`/`AgentSteps`. Now that the schedule advances at *dispatch* time, a recurring job silently fails
  to increment `ConsecutiveFailures` and a **one-off reads `Status == Completed` with `LastFiredAt` null
  forever**, having produced nothing. **The fix is already sketched and the sketch is the point:** a
  startup **health-only** reconcile joining `ScheduledJobs` → `AgentRuns` on the now-indexed `TriggerRef`.
  Two constraints it must respect — the dispatch-time write must not change, and `MarkRunFailedAsync` cannot be
  reused because it recomputes `NextFireAt`, which dispatch already advanced. Cost: a new non-advancing write on
  `IScheduledJobService` plus a matching edit in every hand-written fake. **Batch-sized, on the riskiest surface
  in that sweep** — not a tail-end patch. Source: 00-OVERVIEW 2026-08-03 addendum (hermes #2 paragraph).
  UI symptom to observe meanwhile: `02-ui-check-plan.md` **H7-6**.
- [ ] **T0-2 — the tick is still held hostage by an unanswered dialog.** No longer by run completion, but two
  due jobs and one unanswered grace/late-run prompt still dispatch neither. Pre-existing; fixing it changes
  `ExecuteOnceAsync`'s contract. Source: same addendum.

---

## Tier 1 — plan §18 remainder (controlled parallel jobs)

**Already shipped (do not rebuild):** §18.3(1). The scheduler's serializing `_runLock` is gone and settle-time
bookkeeping moved into a continuation (`ScheduledJobBackgroundService.BookkeepAgentRunAsync`), so the launcher
pool really is the bound it claims to be.

- [ ] **T1-1 — `MaxParallelBackgroundRuns` setting** (default 2, clamp 1..8) over `HeadlessRunLauncher._slots`,
  surfaced beside the scheduled-budget knobs. **It must stay a separate number from `_childSlots`**, which is
  fixed at 2 by a settled decision recorded in its own doc comment (`HeadlessRunLauncher.cs:66`–`:75`) — the
  child pool mirrors the parent pool so a delegating build's worst case on a provider is a fixed 2+2.
- [ ] **T1-2 — per-provider throttle** (a keyed semaphore). §18.4 states the dependency plainly: raising
  parallelism above 1 is only safe *with* this in place.
- [ ] **T1-3 — fairness: admit due jobs oldest-`NextFireAt`-first**, and a job that cannot get a slot waits its
  turn rather than being skipped. Read what `GetDueJobsAsync`' SQL already orders by before writing anything.
- [ ] **T1-4 — ratify or revisit `Research`/`SingleTurn`.** §18.3(5) left this open; the tree has already taken
  option (b) **with a stated reason**, so this is a ratification, not a design gap. `_researchSlots(1,1)` is one
  permit because that is the concurrency the leg already had, and its doc comment
  (`ScheduledJobBackgroundService.cs:67`–`:77`) corrects hermes #2 on the way: `BackgroundAssistantTurnRunner`
  never touches `IHeadlessRunLauncher`, so "bounded only by the launcher slot semaphore" is **false for this
  leg** — dropping the old lock without this permit would have turned N due research jobs into N concurrent
  provider turns. If you do move it behind the shared pool, §18.3(5)'s precondition stands: the SingleTurn path
  must first gain workspace handling consistent with §17.2.

---

## Tier 2 — the hermes tail, each verified against the tree

Not defects. Recommendations and named gaps that no batch has taken. Verified absent on 2026-08-03 rather than
assumed absent.

- [ ] **T2-7b — consume MCP `ToolAnnotations.DestructiveHint` / `ReadOnlyHint`**, in the **more-restricted
  direction only** (a server must not be able to declare itself safe). The name-stem heuristic half of hermes #7
  shipped; this half needs the hint plumbed out of `McpPluginToolHandler`. Documented as a future upgrade at
  `ToolPermissionService.cs:88`–`:96`.
- [ ] **T2-13b — SQLite integrity-check / repair-on-open.** WAL + `busy_timeout` shipped with Batch 10; there is
  no `PRAGMA integrity_check` anywhere in `src/`. hermes #13's second half.
- [ ] **T2-14 — gated-call correlation.** `toolCallId` + a per-step ordinal on `AgentTimelineEvents`, and
  `requestedAt`/`decidedAt` (including timeout) per gated call. Recording `Round` belongs here too and costs a
  change to the tool-handler delegate signature (six closures). **hermes #14's third clause — "drop the per-run
  cap" — is REJECTED by design, not outstanding:** the 500-row cap plus a single truncation marker is
  deliberate. Do not list it as work.
- [ ] **T2-17a — a grounding digest in the plan turn.** hermes #17 asks for tool names, a files listing and
  memory hits injected into `BuildPlanMessages`. Verified absent: the signature at `AgentPlanner.cs:483` takes
  goal, persona, `firm`, analysis and roster only.
- [ ] **T2-G1 — live telemetry seam.** Route Batch 03's emission through a small in-proc **fail-open**
  `IRunObserver`, so one event stream serves both the audit table and a future OTel/file-trace consumer. No
  `IRunObserver` exists in `src/`. hermes §5.
- [ ] **T2-G2 — aggregate cross-run usage view** ("what did this month cost me"). **Tokens and active time only,
  never money** — the 2026-07-30 pricing withdrawal is settled and applies here.
- [ ] **T2-G3 — `AgentRunTrigger.Event` is reserved and owned by no batch.** A design note is the minimum ask.
  Written: [`../agent-roadmap/16-event-trigger-design-note.md`](../agent-roadmap/16-event-trigger-design-note.md).
- [ ] **T2-G4 — a one-page trust-model doc.** State plainly that MCP stdio subprocesses run with full user
  privileges *entirely outside* `SafeFolderPath`, and that containment is per-chokepoint, not per-process.
  hermes §5 and #18. Written: [`../agent-roadmap/17-trust-model.md`](../agent-roadmap/17-trust-model.md).
- [ ] **T2-18 — the Nice tier**, one line each, all independent: grace turn on budget pause; quiet mode for
  monitor jobs; per-job run-history list; `ILogger.BeginScope(runId/stepId)` correlation plus a small
  release-mode redacting sink as a backstop for tool *output* (hermes §8¹'s "no redaction today" is false —
  [`../agent-roadmap/17-trust-model.md`](../agent-roadmap/17-trust-model.md) §4; a sink may still earn its place
  as defence in depth, but not on that ground).

---

## Tier 3 — named non-goals

**Every item here was seen, reasoned about, and left.** This section exists so the next reader does not
re-derive them as new work. One line each; the reasoning is in the named source. If you want to move one out of
Tier 3, read its source section first — several are premise-errors waiting to happen, and the overview says so
about itself.

**Autonomy / gating** (00-OVERVIEW "Opened by Batch 04")
- Resume grant floor is origin-blind — closing it needs a new append-only `AgentRunTrigger` ordinal (§13.1).
- The curated allowlist is not honoured unattended — pinned by a test *on purpose* (§13.3).
- `SingleTurn` never gets the run policy — a named executor-parity gap, declined deliberately (§13.7).
- Tool-name route collisions are silent — a `RegisterHandler` collision warning is still deserved (§13.5).
- `ExecutePendingActionAsync` is dead surface on all seven handler interfaces (§13.4).
- A model- or peer-authored policy would need `ParseGrantedTools`' treatment — a prerequisite for per-job
  policy, not work on its own (§13.2).

**Audit trail** (00-OVERVIEW "Opened by Batch 03")
- The trace is device-local — a merged one needs a sync DTO *plus* a cross-device `Seq` merge policy.
- `AgentRuns`/`AgentSteps` for `Planned` runs are unbounded — a retention decision about user history.
- A tool call in flight when the process dies leaves no row — one-row-after-the-outcome by design.

**Delegation & workspace** (00-OVERVIEW "Opened by Phase 3")
- No merged parent+child timeline (R14) — needs a new cross-run ordering key designed as its own work.
- Worktree mode cannot see uncommitted/untracked work (R16) — **a release note is owed**, not a fix.
- The worktree LEFTOVER arm has no UI path — a design question about what the app should *do* with ignored run
  output.
- A pre-`branchCommittedAtUtc` metadata document reads as "not committed" — accepted rather than migrated.

**Steering** (00-OVERVIEW "Opened by Batch 08")
- Q1 a cascade-paused child's tokens never reach the parent ledger · Q2 `WaitForUserDecisionAsync` takes no
  `CancellationToken`, so Live's pause is two mechanisms · Q5 the sweep's set-vs-threshold asymmetry (a
  `[Theory]` over `Enum.GetValues<AgentRunState>()` would close it) · Q7 `IsPausing` has no timeout · F15 a
  `Skipped` row hoists above the pending tail (cosmetic, an owner call) · F17 declined by name · the
  pause-request ownership rule awaits an owner ruling (one `if`) · a `Failed` step outside a **budget** park
  still never re-runs, and the owed replan is not persisted.

**Scheduler UI** (00-OVERVIEW "Opened by Batch 09")
- Per-job budget/policy is global by decision — per-job is a batch, because the class list becomes peer-writable
  unvalidated input the moment it crosses the sync wire.
- The jobs-list load is a knowing N+1; an unknown `ScheduledJobStatus` is rendered inert and not normalised.

**View coverage** (00-OVERVIEW "Opened by Batch 13/14" + the 2026-08-02 addendum)
- `FirstRunWizardWindow` and everything under `Views/WizardSteps/` are **unparseable** under the shared STA
  host: an authority-only pack URI resolves against `Application.ResourceAssembly`, which latches to the test
  assembly. A fix was attempted and **reverted on evidence**. Manual-smoke debt, not work.
- The residue is now a set of *shapes*, not files: `DataTemplate` content, `Style.Triggers`, bindings carrying
  `RelativeSource`/`ElementName`/explicit `Source`, `loc:Str` reached through `Content=`/`ToolTip=`/`Header=`,
  and everything under `Dialogs/`, `Dialogs/Overlay/`, `WizardSteps/`, `Views/Controls/`.
- Batch 14 Q1 (the walker's `Resolves` should be tri-state), Q3, the fifth `AncestorType=ItemsControl` binding
  at `AssistantView.xaml:221`, and `ResolvePathType` not checking settability.

**Standing decision**
- The Microsoft Agent Framework Harness assessment is a standing *decision*, not an open item.

---

## Before you tick anything

The [Zero-Warning Policy](../../../../CLAUDE.md) is the gate: `dotnet build -t:Rebuild -v:n` at
**0 Warning(s) / 0 Error(s)** in Debug **and** Release, and the suite at `failed: 0`. Measure the baseline on
the clean tree yourself before starting — do not read a count out of a commit body or out of the overview. That
is the one discipline that file has had to repair most often.
