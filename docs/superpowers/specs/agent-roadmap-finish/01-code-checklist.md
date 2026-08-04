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

**§18 (Parallel job execution) was the last section of the plan still labelled "planned, not built".** All of it
has now shipped, T1-2 included, and §18's status line in the plan says so. Tier 1 below records what each of its
five design points actually became.

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

- [x] **T0-1 — a crashed scheduled firing never reconciled its job row.** Fixed: a startup health-only
  reconcile (`ScheduledFiringReconciler`/`IScheduledFiringReconciler.cs`) joins `ScheduledJobs` → `AgentRuns`
  on the indexed `TriggerRef` via `AgentRunService.GetLatestSettledFiringsAsync`, and books the health columns
  through a new non-advancing member, `IScheduledJobService.MarkFiringOutcomeAsync`, without recomputing
  `NextFireAt` (dispatch already advanced it). This checklist's original reason for not reusing
  `MarkRunFailedAsync` was wrong — it recomputes `NextFireAt` only on the recurring branch; the real
  objection, recorded at `IScheduledJobService.cs:129`-`:134`, is that on a one-off it would overwrite
  dispatch's `Status='Completed'` with `'Failed'` and burn a strike on a job that will never fire again. A
  parked-then-resumed run is covered too, via `IHeadlessRunLauncher.ResumedRunSettled`.
  UI symptom to re-check: `02-ui-check-plan.md` **H7-6**.
- [x] **T0-2 — the tick was held hostage by an unanswered dialog.** Fixed: the missed-run ask moved off the
  tick into its own tracked task (`ScheduledJobBackgroundService.AskThenRunMissedAsync`), gated so two late
  jobs cannot open two dialogs on the one host, and cancelled on shutdown.

---

## Tier 1 — plan §18 remainder (controlled parallel jobs)

**Already shipped (do not rebuild):** §18.3(1). The scheduler's serializing `_runLock` is gone and settle-time
bookkeeping moved into a continuation (`ScheduledJobBackgroundService.BookkeepAgentRunAsync`), so the launcher
pool really is the bound it claims to be.

- [x] **T1-1 — `MaxParallelBackgroundRuns` setting** (default 2, clamp 1..8), live-resizable via a new
  `RunSlotPool` (`RunSlotPool.cs`) wrapping `HeadlessRunLauncher`'s slots: widening admits queued waiters at
  once, narrowing never preempts an in-flight run. `_childSlots` stays fixed at 2, unaffected by this setting
  — the child pool mirrors the parent pool by separate construction, not by sharing this number
  (`HeadlessRunLauncher.cs:66`–`:75`).
- [x] **T1-2 — per-provider throttle.** Fixed: `IProviderRequestThrottle`/`ProviderRequestThrottle.cs` keys one
  live-resizable `RunSlotPool` per `AiProvider.Id` — no new identity field was needed — and `AiClientService`
  takes a permit around every outbound round-trip through one helper, `AcquireProviderPermitAsync`
  (`AiClientService.cs:69`). PER ROUND, not per call: the tool loop's streaming round holds it from the stream
  start to the enumerator's disposal (`:250`, released `:344`), its non-streaming twin releases before it yields
  (`:358`), and `GetChatResponseAsync` (`:539`), `SendRequestAsync` (`:689`) and `StreamChatCompletionAsync`
  (`:753`) are single round-trips. **The release point is the load-bearing part** (`:464`): a permit held across
  the tool dispatch would be held across an interactive approval card, so one open dialog would stop every
  background run on that provider. The three `Test*` probes are deliberately excluded — a "Test" button that
  queues behind two background runs reads as a hung dialog. Setting: `MaxParallelRequestsPerProvider`
  (`AppSettings.cs:316`, default 4, clamp 1..24), JSON-only by decision, and chosen so an install that never
  touched a setting is unaffected (default pool 2 + one person typing = 3). **Queue time is charged against the
  request timeout**, so the exception stays `LlmTimeoutException` but its message and log line say the timeout
  elapsed in the queue and nothing was sent. Two stale comments were corrected in the same change:
  `AppSettings.cs:281` (which said this was not in place) and `RunSlotPool.cs:116` (whose "no production caller"
  remark is now stated as the per-INSTANCE rule it was protecting — throttle pools never take tickets).
- [x] **T1-3 — fairness: admit due jobs oldest-`NextFireAt`-first.** Narrower than this checklist framed it:
  nothing was ever dropped, and `GetDueJobsAsync`'s SQL already ordered `NextFireAt ASC`. What was lost sat
  between launch and queue — both dispatch paths waited inside a detached `Task.Run`, so arrival order was
  thread-pool scheduling, not enqueue order. Fixed by taking a ticket synchronously on the launching thread, so
  creation order equals enqueue order.
- [x] **T1-4 — ratified: `Research`/`SingleTurn` keeps its own `_researchSlots(1,1)` permit**, not the shared
  launcher pool. §18.3(5) left this open; the tree had already taken option (b) with a stated reason, so this
  is a ratification pinned by a peak-concurrency test, not a design gap
  (`ScheduledJobBackgroundService.cs:67`–`:77`). Moving it behind the shared pool still needs the SingleTurn
  path to gain workspace handling consistent with §17.2 first — that precondition stands, unchanged.

---

## Tier 2 — the hermes tail, each verified against the tree

Not defects. Recommendations and named gaps that no batch has taken. Verified absent on 2026-08-03 rather than
assumed absent.

- [x] **T2-7b — consume MCP `ToolAnnotations`.** Fixed: `McpPluginToolHandler.IsServerDeclaredDestructive`
  (`:177`) reads the hint off the tool the name lookup already resolved (`:116`) and it travels as
  `PluginToolCall.ServerDeclaredDestructive` (`IPluginToolHandler.cs:29`) to a new REQUIRED member of
  `ToolGateInput` (`ToolAutonomy.cs:58`) — required, so a fourth gate cannot be added without answering whether
  it knows what the server declared. One line consumes it (`ToolAutonomy.cs:169`): `IsDeleteLike` gained an
  OR-ed second argument (`ToolPermissionService.cs:102`), so the declaration reaches the floor, the policy arm,
  the session tier and the park at once. **Only an explicit `DestructiveHint == true` counts — deliberately NOT
  the spec's "null ⇒ assume true" default**, which would reclassify every tool of every annotation-less server
  as delete-like and refuse all unattended MCP. `ReadOnlyHint` IS consumed, and the consumption is that it
  cannot move the answer: `false` says only what Pia already assumes (every MCP call is a write), and `true` is
  the self-declaration of safety the item forbids honouring — pinned, with the contradictory
  `readOnlyHint:true`+`destructiveHint:true` pair, in `McpToolAnnotationHintTests`. The same flag widens the
  CARD (`ActionCardBuilder.cs:67`) and both grant-offer rules, because a card offering "Always allow" for a tool
  the floor will never auto-run is a button that does nothing.
- [x] **T2-13b — SQLite integrity-check on open.** Fixed: `SqliteContext.CheckIntegrity`
  (`SqliteContext.cs:134`) runs `PRAGMA integrity_check(1)` once, on the shared connection's first open only
  (`:62`), and records the answer on `IntegrityStatus` (`:83`) beside an Information/Error log line — the support
  log is the surface, the property is what a test (and a future affordance) can read without a second full scan.
  **The ORDER is the item, and it is stricter than "before `EnsureSchema`":** `Open()` does not read page 1, so
  the first statement that touches the file is the first that can throw — and with the check downstream of
  `PRAGMA journal_mode=WAL` the one damage class that makes a file unopenable was the only class it could never
  report (a review pass found this; a header-damaged file produced no verdict and no log line at all). The
  sequence is now `busy_timeout` → check → WAL → schema: `busy_timeout` reads nothing, so it cannot fail on a
  damaged file, and it gives the check its 3 s tolerance. `SqliteContextIntegrityTests` drives both an
  interior-page-damaged database (diagnosis lands even though the schema pass then throws) and a file that is not
  a database at all. **"repair" is deliberately NOT attempted, and that is the finding, not an omission:**
  SQLite's own remedy is a dump-and-reload — a decision about the user's history, not something to do silently at
  startup — and the one in-place move, `REINDEX`, is a write to the very file we have just established cannot be
  reasoned about, triggered by string-matching SQLite's diagnostic prose. Cost measured rather than assumed:
  11.6 ms on a real 1.02 MB `history.db`, linear in file size, with `quick_check` (2.4 ms on the same file) named
  in the code as the trade if a large profile ever makes it a visible startup delay.
- [x] **T2-14 — gated-call correlation.** Fixed: `ToolCallId`, `StepOrdinal`, `RequestedAt` and `DecidedAt`
  joined `AgentTimelineEvents` (`AgentTimelineEvent.cs`), all nullable so a pre-existing `SchemaVersion 1` row
  reads as "never recorded" rather than a lost fact on a `SchemaVersion 2` row. `Round` is carried from
  `AiClientService`'s tool loop through a new `ToolDispatchContext`, replacing the tool-dispatch callback's
  bare `Func<FunctionCallContent, Task<object?>>` with a named `ToolCallHandler` delegate
  (`IAiClientService.cs`) so a future dispatch-context need costs no further churn. `AgentTimelineScope.
  SanitizeCallId` bounds the provider's raw `CallId` to a tool-identifier charset before it reaches the table.
  **hermes #14's third clause — "drop the per-run cap" — is REJECTED by design, not outstanding:** the 500-row
  cap plus a single truncation marker is deliberate. Do not list it as work.
- [x] **T2-17a — a grounding digest in the plan turn: THE FILES LISTING ONLY.** Built:
  `AgentPlanner.TryBuildGroundingAsync` (`:336`) resolves the folder the run's file tools actually use —
  `ctx.WorkspaceRoot` → ambient → the settings folder, then narrowed by `ctx.WorkingSubpath`, the ladder Batch 06
  B3 established — and `ListWorkingFolder` (`:399`) renders a capped, `SandboxIgnore`-filtered top-level listing
  (an empty folder still says so; past the separate SCAN cap it says "and more" with no number, because a count
  the walk could not finish is a claim it cannot make). It is resolved once per plan (`:161`) and folded into the
  single USER message (`:733`), never the System prompt: these are file names out of the user's own folder and
  `TokenizeMessages` rewrites user text only. Mirrors `AgentVerifier`'s artifact probe — time-boxed off-thread
  walk, failure-isolated, names never above `SensitiveDebug`. No usable folder ⇒ nothing appended ⇒ the prompt is
  byte-identical to before. Plan turn only, not the replan (which already carries the completed steps and their
  declared artifacts).
  **Built inside the planner, not passed in from the orchestrator as this checklist's own grounding note
  suggested** — the digest needs only `RunContext` (already a `PlanAsync` argument) and `ISettingsService`
  (already a field), so a new parameter and the orchestrator's dozen positional test constructions buy nothing.
  **hermes #17's other two ingredients are NOT built, and not for effort:** a run's real tool set is per-run
  (envelope grants × persona `ToolScope` × plugin routes) and assembled behind `IAgentTurnExecutor`, which
  exposes no roster — listing whatever tools this process happens to hold would name capabilities the gate then
  refuses, and a plan built on tools that do not run is worse than one built on none; memory hits need a recall
  dependency plus an embedding round-trip per plan and a policy for putting the user's memory text in a prompt,
  which is a decision, not plumbing. Both reasons are recorded at `AgentPlanner.cs:327` so the next reader does
  not re-derive them as oversights.
- [x] **T2-G1 — live telemetry seam.** Fixed: `IRunObserver` (`IRunObserver.cs`) is a bystander on
  `IAgentTimelineService.Emit`'s accepted events. Zero registrations is the supported default — MS.DI resolves
  `IEnumerable<IRunObserver>` to an empty sequence — so a future OTel exporter or file trace adds itself
  additively in `Bootstrapper.cs` without becoming a second writer on the audit table. hermes §5.
- [ ] **T2-G2 — aggregate cross-run usage view** ("what did this month cost me"). **Tokens and active time only,
  never money** — the 2026-07-30 pricing withdrawal is settled and applies here.
- [ ] **T2-G3 — `AgentRunTrigger.Event` is reserved and owned by no batch.** A design note is the minimum ask.
  Written: [`../agent-roadmap/16-event-trigger-design-note.md`](../agent-roadmap/16-event-trigger-design-note.md).
- [ ] **T2-G4 — a one-page trust-model doc.** State plainly that MCP stdio subprocesses run with full user
  privileges *entirely outside* `SafeFolderPath`, and that containment is per-chokepoint, not per-process.
  hermes §5 and #18. Written: [`../agent-roadmap/17-trust-model.md`](../agent-roadmap/17-trust-model.md).
- [x] **T2-18 — the Nice tier**, one line each, all independent. Split into its five parts, because they landed
  separately:
  - [x] **Grace turn on budget pause.** Built: `IAgentTurnExecutor.RunGraceTurnAsync` — one TOOL-FREE wrap-up
    turn spent at the budget park, so a parked chat ends with "here is where I got to" instead of the last
    step's output. `HeadlessTurnExecutor` implements it through the existing exchange engine with
    `toolFree: true` (the cap must not become advisory) and `persistInterim: true` (a park never reaches
    `EndRunAsync`, so a wrap-up not written there is never written); the orchestrator calls it through
    `SafeGraceTurn`, which bills the round run-level, extends the transcript range, bounds it at 90 s
    separately from the run's own timeout, skips it on an already-cancelled token, and **parks anyway if it
    throws**. It is the codebase's FIRST default-interface member, and deliberately: that interface is
    implemented by both executors plus a hand-written fake in most orchestrator test files, and the right answer
    for all of them but `HeadlessTurnExecutor` — `LiveTurnExecutor` included, since its transcript is on screen —
    is to spend nothing. The members that must be answered out loud are required precisely because they are not
    this one. The durability facts that pin per-turn save counts moved by exactly one turn and one save, and say
    so where the number is.
  - [x] **Quiet mode for monitor jobs.** Built: `ScheduledJob.QuietOnSuccess` + a `QuietOnSuccess` column
    (`SqliteContext.cs`, `DEFAULT 0` so no existing job is quieted by a migration), honoured at ONE chokepoint —
    `ScheduledJobNotificationSurface.NotifySuccess`, which both producers come through — and authored by a
    checkbox in the jobs editor (`AssistantView.xaml`, en/de/fr) that reaches BOTH service calls — the editor is
    one panel for create and edit, and a review pass caught the create path dropping the flag silently.
    **SUCCESS ONLY: `NotifyFailure` ignores it by
    design**, because a monitor that silently stops working is worse than one that is noisy. It suppresses the
    PUSH, not the record: the chat is still written and the job row still carries `LastFiredAt`. Device-local —
    absent from `SyncScheduledJob` and from `UpsertFromSyncAsync`'s SET list, so a pull cannot switch a
    monitor's notifications back on (pinned by a test that lands a pull and re-reads the flag).
  - [x] **Per-job run-history list.** Built as a QUERY, not a table: `IAgentRunService.GetFiringsForTriggerAsync`
    seeks the existing `IX_AgentRuns_TriggerRef` and returns a job's recent settled firings newest-first. **The
    run rows already ARE the history** — both job kinds stamp `TriggerRef = job.Id` (the agent leg at launch, the
    research leg inside `BackgroundAssistantTurnRunner`), and `LastResultEntryId`, the single pointer this
    replaces, is overwritten every firing precisely because it was never meant to accumulate; a second store
    would be a copy that could disagree. Consumed by the jobs list: a one-line summary ("Last 5 runs: 4 ok,
    1 failed") with the firings themselves as its tooltip, hidden entirely for a job that has never fired, read
    failure-isolated so it cannot cost the list. SETTLED only — a parked firing has no settle instant and is
    still live — and a child run's null `TriggerRef` keeps a fan-out out of its parent job's history, both pinned.
  - [x] **`ILogger.BeginScope(runId/step ordinal)` correlation — and the sink that makes it visible.** The
    literal item would have shipped DEAD, and that is the finding: the file sink is `NReco.Logging.File`, whose
    assembly contains **zero** references to `ISupportExternalScope`/`IExternalScopeProvider` and whose
    `FormatLogEntry` cannot reach a scope — so `BeginScope(runId)` allocates and is discarded before it reaches
    `pia-*.log`, the one file a user attaches to a support request. Built: `ScopeRenderingLoggerProvider`
    (`Logging/`) wraps the file provider and prefixes every line with the scopes open on the writing async flow,
    registered by hand in `Bootstrapper.cs` in place of `AddFile`. The stack is `AsyncLocal` and STATIC, so a run
    scope opened by the orchestrator also labels the lines a tool handler writes through its own category — which
    is most of the log. `AgentRunOrchestrator.RunAsync` opens `run {RunId}`; each step nests `step {StepOrdinal}`
    (the ordinal, because that is what the plan, the panel and the audit table show) around the executor call.
    Why it matters now: T1-1/T1-2 let several unattended runs interleave their lines in one file, where
    "Round 1/10 starting" otherwise belongs to no run. **Scope state is IDs ONLY** — it reaches a release log
    verbatim, and the compile-time-erased `Sensitive*` family stays the only route for user content.
  - [x] **A small release-mode backstop for tool *output*.** Built as a LENGTH CAP, not as redaction:
    `LogMessageCapLoggerProvider` (`Logging/`) caps one formatted line at 2000 chars in RELEASE only, composed
    inside the scope renderer so the run/step prefix survives truncation (which keeps the head). **hermes §8¹'s
    "no redaction today" stays false and this does not change it** — user content leaves release logs by
    COMPILATION ([`../agent-roadmap/17-trust-model.md`](../agent-roadmap/17-trust-model.md) §4: the `Sensitive*`
    family is `[Conditional("DEBUG")]`, so in release there is no string to redact). What this covers is the
    residue erasure cannot: a line that is NOT `Sensitive*`-gated and carries a payload anyway — ours by mistake,
    or a third party's. Hence a content-AGNOSTIC bound: metadata lines are short, dumped payloads are not. It
    touches neither the exception (so a stack trace is never truncated), nor the state (a structured sink still
    sees the original values), nor DEBUG builds (the cap is unlimited there, since that log is the thing being
    diagnosed). The cap is a constructor parameter so truncation is exercised in either configuration.

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
