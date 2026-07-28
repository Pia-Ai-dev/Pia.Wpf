# Agent System — Roadmap & Status

_Snapshot: 2026-07-28 — as-built at `601090e`. **Batches 10 and 11 have shipped** (plus a joint review fix
pass); they were promoted out of “Deliberately open” earlier the same day and are now in the chronicle. Build
verified here: `dotnet build -p:EnableWindowsTargeting=true` → **0 errors, 194 warnings** (all pre-existing —
8 in `src` in files untouched by these batches, 186 xUnit analyzer warnings in the test project; the older
“0 warnings” claim in this file was an incremental-build artifact and has been corrected). **Zero tests have
been executed** on this Mac at any point._ Living index
of what the Agent System has shipped and what is left to build. Authoritative design:
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
| 8 | `19c7a03` → `f1267b3` | **Hardening batch** + its review fix-up pass — see “What the hardening batch closed” below | ✅ done |
| 9 | `e4ad6bf` → `770fad3`, `d1c746d` → `630c2c2` | **[Batch 10](10-durability-and-lifecycle.md)** — dedicated gated chat connection + WAL/busy_timeout (W1), one-effective-writer per chat row (W2), `Once`-job settle (W3) | ✅ done |
| 10 | `74f964c` → `a06358d` | **[Batch 11](11-context-compaction.md)** — `Microsoft.Agents.AI.Compaction` behind one adapter, per-provider context budget | ✅ done |
| 11 | `aab9a06` → `601090e` | Joint review fix pass over 10 + 11 (4 must-fixes, 6 should-fixes; two should-fixes deliberately left open — see below) | ✅ done |

**Git position:** the branch **is** pushed — `origin/feature/agent-run-spine` exists and the branch tracks
it; the last pushed commit is `e7df175`. Everything from `19c7a03` onward (hardening + Batch 10 + Batch 11 +
the fix pass, plus this doc commit) is **local-only** — **50 commits** as of 2026-07-28 (do not trust a
hardcoded count here; it goes stale on the next commit — read it from git). Check with
`git rev-list --count origin/feature/agent-run-spine..HEAD` and `git branch -vv`.
Build check everywhere: `dotnet build -p:EnableWindowsTargeting=true`. At `601090e` this is **0 errors, 194
warnings**, all pre-existing: 3× `CS8602` in `Helpers/DroppedFileReader.cs`, 2× `MVVMTK0034` in
`ViewModels/Flow/FlowViewModel.cs`, 3× `MSB3568` for a duplicate `Memory_Refresh` key present twice in each
of the three resx files, and 186 xUnit analyzer warnings in the test project. **The “0 warnings” figure used
earlier in this file was wrong** — it came from an incremental build, which skips `CoreCompile` and therefore
does not re-emit analyzer warnings. The real bar these batches held is *adds zero warnings*, verified with
`--no-incremental` before and after.

> **Two things that are not batches, and outrank every batch below.**
> 1. **Run the tests.** The unexecuted surface has *grown*, not shrunk. The ~147 agent-related
>    `[Fact]`/`[Theory]` from the hardening batch have still never been executed by any runner
>    (`AgentRunService` 31, `AgentRunOrchestrator` 32, `AgentVerifier` 20, `AgentRunNotificationSurface` 16,
>    `RunProgress` 15, `HeadlessRunLauncher` 14, `AgentPlanner` 11, `HeadlessTurnExecutor` 8) — and Batches 10
>    and 11 added **90 more** `[Fact]`/`[Theory]`, also never executed. Roughly **240 assertions across 20
>    commits rest on code nothing has run.** This is by far the largest risk on the branch, and two of the new
>    assertions are *known* to be fixture-sensitive rather than production-sensitive (see Batch 11).
> 2. **Push.** Everything from `19c7a03` onward is local-only — `git rev-list --count
>    origin/feature/agent-run-spine..HEAD` (50 at the time of writing).

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
- **Chat writes are arbitrated** (Batch 10) — `AssistantChatService` owns a dedicated SQLite connection plus a
  `SemaphoreSlim(1,1)` gate covering **every** public method, reads included; the shared connection runs
  `journal_mode=WAL` + `busy_timeout=3000`. The auto-title rename is a title-only `UPDATE`, and a headless
  step's write goes through `SaveMergedAsync`, which reads the persisted rows, absorbs foreign ones by `Id` and
  writes — all under one gate hold. `ForeignRunActive` blocks Send / Regenerate / SwitchToAgent while a run
  attached to the chat executes under a foreign executor.
- **A `Once` scheduled job fires once** (Batch 10) — a new terminal `ScheduledJobStatus.Completed` (ordinal 3,
  append-only) is written by all three settle doors; `NextFireAt` is deliberately **not** clamped, and
  `RecurrenceCalculator` is untouched. An `UpdateAsync` that re-schedules forward re-arms a settled one-off.
- **Long-run context is bounded** (Batch 11) — `AgentContextCompactor` wraps
  `ContextWindowCompactionStrategy` behind one Pia-only signature (so `MAAI001` is contained to one file),
  pins the leading system run + the run goal + the step instruction, and degrades to *send uncompacted* on any
  fault including a bad-config ctor throw. Opt-in per provider (`MaxContextWindowTokens` /
  `MaxOutputTokens`, both null = off, which is what every existing provider upgrades into). Wired into the
  Headless step request, the Live step request, and the in-step tool loop. The interactive chat path is
  deliberately never compacted.

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

The `#` is a **stable file ID, not a rank** — batch files are never renumbered, because 01–09 cross-reference
each other by number. Read the **Rank** column for priority.

| Rank | # | Batch | Phase | Size | Depends on |
|---|---|-------|-------|------|-----------|
| **1** | — | **Run the test suite on Windows/CI** — not a batch; see the callout above. ~240 assertions across 20 local-only commits have never been executed | — | S | a Windows runner |
| 2 | 02 | [Cost ledger](02-cost-ledger.md) — price table populates `CostUsd` | 2 | S | — |
| 3 | 05 | [Planner reason-then-emit](05-planner-reason-then-emit.md) — boosted planning effort on Chat-Completions | 2 | S–M | — |
| 4 | 03 | [Audit timeline](03-audit-timeline.md) — per-tool decision trace (plan §11) | 2 | M–L | — |
| 5 | 04 | [Autonomy policy](04-autonomy-policy.md) — `PolicyJson` per-run approval policy | 2 | M–L | MCP gate |
| 6 | 06 | [Run workspace isolation](06-run-workspace-isolation.md) — run-aware file-tool base root + promotion | 3 | M | Milestone B |
| 7 | 07 | [Sub-agents / multi-persona](07-subagents-multipersona.md) — `ParentRunId`/`AssignedPersonaId` + attribution | 3 | L | Batch 11 ✅ shipped |
| 8 | 08 | [Live steering](08-live-steering.md) — plan mutation / nudge / pause / resume | 4 | L | budget-pause, sub-agents |
| 9 | 09 | [Scheduler UI](09-scheduler-ui.md) — create/edit/list agent jobs; **now also owes a re-arm surface + unknown-status handling, see below** | 4 | M | Milestone B |
| — | 01 | [Budget-pause polish](01-budget-pause-polish.md) — **empty**: every item closed by the hardening batch + its fix-up; the file keeps only open assumptions | 2 | — | — |
| — | 10 | [Durability & lifecycle](10-durability-and-lifecycle.md) | 2 | M | ✅ **shipped** `e4ad6bf`→`630c2c2` |
| — | 11 | [Context compaction](11-context-compaction.md) | 2 | S–M | ✅ **shipped** `74f964c`→`a06358d` |

**Why “run the tests” now outranks every batch.** Batches 10 and 11 were ranked 1 and 2 because two of Batch
10's items were live data-loss paths. They have shipped, so the top risk is no longer a known bug — it is that
the *fix* for those data-loss paths (a semaphore-gated dedicated SQLite connection, a merge write, a WAL
switch) has never been executed. A wrong gate is a worse failure than the gap it closed. Batch 05 remains
ahead of 03/04 only because it is S–M and unblocked.

Phase 2 now completes at Batch 03/04. Batches 06–09 are Phase 3/4; their seams may shift — re-scope at the
design step. `PolicyJson` is no longer NULL — it carries the launch grant envelope — so Batch 04 must *extend*
that document, not claim the column. **Batch 09 picked up two obligations from Batch 10's W3:** it must render
an unknown/out-of-range `ScheduledJobStatus` safely (an older peer receives the new ordinal `3` over the sync
wire and stores it as the string `"3"`, unvalidated at `SyncMapper.cs:953`/`:974`), and it owns the missing
re-arm surface for a settled one-off (see “Deliberately open”).

---

## Deliberately open (known, not oversights)

Each of these was seen and left; the reason is the point.

- **Scheduler head-of-line block.** `ScheduledJobBackgroundService.ExecuteAgentTaskAsync` still `await`s
  `handle.Completion` (`:199`) inside the tick loop, so one long agent job delays every other due job for up
  to its wall-clock budget. **Owner-deferred, not missed** — the fix is a continuation-based dispatch
  (bookkeeping moved off the tick), which changes the job-completion contract; the hardening batch only made
  a *parked* run stop re-launching every tick. See hermes-comparison §4(b)(2)/§8² and rec #2.
- **No structured step-result signal.** Step success is still `!string.IsNullOrWhiteSpace(exchange.Visible)`
  (`HeadlessTurnExecutor.cs:256`), so a step that politely explains its own failure records `Done` and the
  failure-only replan never fires. `RunContext.Scratchpad` (`RunContext.cs:85`) is declared and read/written
  nowhere — the seam for a real `emit_step_result{succeeded, artifactRef}` already exists. The H1 artifact
  probe narrows the blast radius (a missing declared artifact now reaches the critic) but does **not** make a
  step's own verdict structured. hermes-comparison §5/rec #9.
- **Read-through workspace isolation + promotion (Batch 06).** Unattended runs still write real deliverables
  into the shared assistant folder; the per-run dir stays scratch. A1 narrowed the default grant to
  `{write_file}` and B2 blocks destructive external tools, which lowers the risk but does not isolate.
  hermes-comparison §4(b)(3)/rec #6.
### Opened by Batch 10 (2026-07-28) — known, reasoned, not closed

- **`ActivateAsync` races the composer against `RestoreActiveRunAsync`.** Activating a hydrated chat returns the
  session (composer live) *before* the fire-and-forget run lookup can set `ForeignRunActive`
  (`ChatSessionManager.cs:432`, marked `KNOWN OPEN WINDOW` in code). **Needs an owner decision, not code** —
  both fixes are visible interactive regressions: awaiting the lookup stalls every history click behind
  `AgentRunService`'s `lock` (which the executing run holds), and pessimistically disabling the composer is a
  flicker-disable that can silently swallow an Enter press. What now bounds the damage: `SaveMergedAsync`
  restores the run's rows on its next write, so only a run that has already made its **terminal** write is
  still exposed.
- **W2's residual two-writer window.** A live turn *already streaming* when the user clicks the Flow “Continue”
  card: the live full replace still wins. `SaveMergedAsync` covers the reverse direction only. Closing it needs
  the deferred incremental/merge write below.
- **The incremental chat write (would retire the W2 bug class).** Rejected at design time as a batch of its
  own, not because it is wrong: `AssistantViewModel.RegenerateCore` (`:854`) deletes a message *suffix* by
  relying on the next full replace, and that intent is mechanically indistinguishable from a headless run's
  append. An append/upsert writer resurrects regenerated-away messages. It needs a truncate-or-tombstone API,
  an `Ordinal`-renumbering rule and an exemption for `SaveFromRemoteAsync`.
- **A deleted chat can resurrect itself.** `HeadlessRunLauncher.OnChatsChanged` (`:419`) deletes the workspace
  but never cancels the in-flight run, so the next interim persist re-UPSERTs the chat row whose `AgentRuns` row
  is already FK-cascade-gone. Same root as W2, different failure (resurrection, not loss). The fix is to
  **cancel** the run, which means deciding cancellation semantics for Clear-all
  (`AssistantSettingsViewModel.cs:408`/`:437`) — a lifecycle decision no spec raises yet.
- **No composer hint explains the disabled Send.** Deliberate: keeping a data-loss fix off the localisation
  path. The reason is inferred from the on-screen `RunProgress` panel, so if that panel is ever collapsed the
  disabled Send reads as a bug. Costs three resx files + XAML.
- **`WAL` adds one failure mode `busy_timeout` does not cover.** A deferred transaction that READS before its
  first write can get `SQLITE_BUSY_SNAPSHOT`, for which SQLite does **not** invoke the busy handler.
  `AssistantChatService.DeleteAllUnderGateAsync` is exactly that shape (SELECT `Id`, then DELETE). Unhandled.
- **A settled `Once` job has almost no re-arm surface.** `UpdateAsync` re-arms `Completed`→`Active` only when
  the recomputed `NextFireAt` lands in the **future**, and it has no `specificDate` parameter — so a settled
  one-off whose date is in the past cannot be moved at all. `ScheduledJobToolHandler` exposes only
  list/create/update/delete (no enable), and there is no scheduled-job ViewModel. **Batch 09 owns this.**
- **No backfill for existing rows** (deliberate): every existing `Once` job with a past `SpecificDate` and
  `Status='Active'` will fire exactly **one more time** on the next 30 s tick before it settles. Real tokens for
  real users — **belongs in the release notes.** Silently retiring them would have swallowed one-offs someone is
  still waiting for. Settled rows are also never garbage-collected (a `Completed` one-off's
  `LastResultEntryId` links to user-visible chat history).
- **`MarkRunFailedAsync` retires a `Once` job on its *first* failure.** One transient provider error kills a
  one-off the user asked for (they do get the failure toast). Chosen over five unattended re-runs in 150 s; it
  is deliberately its own line in the diff so a reviewer can drop it without reopening W3. Its `Once` branch
  also writes `Status='Failed'` unconditionally where the recurring branch preserves a non-Active status —
  unreachable in practice (only `Active` jobs are fired) but wrong for a direct caller.

### Opened by Batch 11 (2026-07-28) — known, reasoned, not closed

- **An image attachment is the first thing evicted on the Live agent path.** Measured, not theorised: a
  `DataContent` message is scored at *raw bytes / 4*, so a 300 KB JPEG reports ~76k phantom tokens where a
  provider would count ~1–2k. The pin protects the system prefix, the goal and the step instruction — so the
  step keeps a goal that refers to an image it can no longer see. Fixing it needs pinning arbitrary mid-list
  `DataContent` *plus* a real image token estimate. Recorded as `KNOWN OPEN HAZARD` on the shipped test and
  deliberately not asserted in either direction.
- **`bytes/4` token accounting is wrong in both directions and unfixable from Pia.**
  `CompactionMessageIndex.Create` is `internal`, so no tokenizer can be injected even though
  `Microsoft.ML.Tokenizers` is already a dependency. Dense JSON *under*-counts (absorbed by lowering the
  thresholds to 0.45/0.70); `DataContent` massively over-counts (above). Revisit if `Create` becomes public.
- **`ToolEvictionThreshold = 0.45` is close to inert.** The library's default `ToolCallFormatter` inlines the
  entire tool result into its `[Tool Calls]` summary, so “eviction” is really *tool-group collapse* and the only
  mechanism that actually reduces tokens is truncation at 0.70. A truncating formatter was rejected: it makes
  the model lose data it just fetched and invites a re-call spiral inside a 10-round cap.
- **A sync pull silently disables compaction.** `SyncMapper.FromSyncProvider` does not map
  `MaxContextWindowTokens`/`MaxOutputTokens` (verified: they appear nowhere in `SyncMapper.cs`) and the pull path
  replaces the whole local provider row, so a pull resets a configured window to `null`. The fields were
  deliberately kept out of `SyncProvider` as device-local; that decision did not account for the pull
  overwriting them.
- **The in-step tool-loop insertion has no test at any level.** `DurabilityHarness` substitutes
  `IAiClientService` outright, so the real round loop never executes in any harness. A wrong guard, wrong
  variable or wrong round index at `AiClientService.cs:189` is invisible until runtime on an agent step with a
  configured window. Named follow-up: a scripted fake `IChatClient` that can drive multi-round tool loops.
- **The step-1 request is never compacted**, by design and by library behaviour, so a run whose *goal alone*
  overflows the window still fails its first step exactly as today. Compaction cannot fix an oversized goal —
  but it means the acceptance criterion holds for *accumulated* context, not for every overflow.
- **Compaction is invisible to the user** — no Flow event, no audit entry, no cost-ledger annotation. “Why did
  step 7 forget what step 3 found” requires Debug logs. Left out to avoid new strings + a persisted-enum change;
  most likely support complaint.
- **Two smaller leaks**: `AgentContextCompactor` drops **every** `System` message the library returns, not just
  the pinned prefix, so a non-leading system message is silently deleted; and `AssistantViewModel.Dispose`
  (`:1547`) unsubscribes `ActiveRunChanged` but not the new `ForeignRunActiveChanged`.
- **Nothing enforces the `MAAI001` containment premise.** Verified by full rebuild that it appears zero times,
  but a future contributor caching a `ContextWindowCompactionStrategy` field outside the adapter re-opens the
  pressure for a project-wide `NoWarn`, which would then hide experimental-API adoption everywhere. The build
  check is the only guard; there is no test.
- **The package bump's two behaviour-sensitive concentrations are unverified beyond compiling**: streamed
  tool-call coalescing at `AiClientService.cs:263` (`updates.ToChatResponse()`), and the seven `OPENAI001`
  pragma sites riding the OpenAI 2.10.0 pin that `Microsoft.Extensions.AI.OpenAI` moves. Both need a real
  provider round on Windows. **`74f964c` is the commit to revert first if provider behaviour regresses.**
  Unaudited transitive weight came with it: `Microsoft.Extensions.AI.Evaluation` 10.6.0 and
  `Microsoft.Extensions.VectorData.Abstractions` 9.7.0 are in the restore graph and nothing in Pia uses them.

**Promoted out of this list on 2026-07-28 and now shipped:** the `Once`-job relaunch loop, two writers on one
chat row, the missing write gate on the shared `SqliteContext` connection (all → Batch 10), and
context/trajectory compression (→ Batch 11, whose design step collapsed when `Microsoft.Agents.AI` 1.15.0
shipped `Microsoft.Agents.AI.Compaction` on 2026-07-22 with the atomic tool-group logic already solved).
hermes-comparison §5/rec #5.

---

## External framework assessment — Microsoft Agent Framework Harness (2026-07-28)

Microsoft shipped `Microsoft.Agents.AI.Harness` 1.15.0 on 2026-07-22 — the same category of thing this spine
is. Assessed and **not adopted**, with one exception. Recorded here so it is not re-litigated.

**Do not adopt `HarnessAgent`.** Its durability story is per-service-call chat history plus a todo list; ours is
a persisted run/step spine with a state machine, CAS-guarded transitions, a crash sweep, budget parking, and
Flow deep-links. It has no durable equivalent — its nearest analogue is Agent Framework *Workflows*
checkpointing (`RequestPort`, `CosmosCheckpointStorage`, Durable Task), which is server/Cosmos-shaped and wrong
for a local WPF app. Adopting it would mean trading the differentiated parts away. Two specific collisions: its
approval model is *looser* than our M3 destructive-MCP guard (heuristic auto-approval; its own shell docs call
the deny-list "a UX pre-filter, not a security boundary"), and it defaults OpenTelemetry + file memory +
hosted web search **on**, which cuts against §12.7 privacy-first logging.

**Per-batch overlap:** 02 none · 03 weak (OTel spans ≠ our queryable decision trace) · 04 none useful (its
flags are coarse) · 05 none (its mode provider is prompt-based; our tool-constrained `emit_plan` is more
reliable) · 06 marginal (`ConfineWorkingDirectory` overlaps confinement we have; it has no *promotion*) ·
07 **best reference** — `BackgroundAgents` is a shipped delegation shape worth reading, but it is flagged
not-production-ready and has no `ParentRunId` persistence, budget roll-up, per-persona provider routing, or
parent/child crash sweep · 08 weak · 09 none.

**The one exception is compaction — and it does not live in the Harness package.** It shipped as
[Batch 11](11-context-compaction.md) (`74f964c`→`a06358d`): `Microsoft.Agents.AI` 1.15.0's
`Microsoft.Agents.AI.Compaction` namespace, consumed as one static method behind one Pia-owned adapter. No
`AIAgent`, no `AgentSession`, no `ChatHistoryProvider`, no `HarnessAgent`.

---

## How we implement a batch (the working pattern)

1. **Work on `feature/agent-run-spine`** (or a fresh `feature/agent-<name>` off it / off `main` once it has
   merged). Do **not** branch from a batch label — the batch "branches" in the chronicle above never existed.
2. **Read the as-built code first** — every batch fills a seam that already exists; the plan marks them.
   Where the plan and the code disagree, the code wins; the plan carries dated “As-built at `<sha>`” notes.
3. **Author + run a workflow** (opus; fable is no longer used): Ground (map seams, read-only) → Design (opus,
   one spec) → Build (one or two sequential builders, commit per logical group, keep the build green) → Verify
   (opus attacks the guardrails, opus checks conventions + coverage, fix must-fixes) → Synthesize.
4. **Independently verify** — after the workflow, confirm the build green and spot-check the top guardrails
   yourself; fix any clear correctness gap the workflow left open.
5. **Commit per group, don't push.** Present decisions/assumptions/open items at the end.

**Standing guardrails (every batch):** failure-isolated bookkeeping (Safe* wrappers); no interactive regression
(the Live terminal settle stays correct); executor parity (Live + Headless); off-thread `RunChanged` stays
marshaled (G3); privacy-first logging (user content → `SensitiveDebug`, Flow Title/Body generic); append-only
persisted enums/ordinals; a new user-visible string lands in `ViewStrings.resx` **and** `.de.resx` **and**
`.fr.resx`. See CLAUDE.md + plan §12.5/§13.10/§16.
