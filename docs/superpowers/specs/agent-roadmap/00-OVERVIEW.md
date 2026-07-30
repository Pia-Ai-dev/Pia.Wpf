# Agent System — Roadmap & Status

_Snapshot: 2026-07-30 — as-built at the head of **Batch 05** (planner reason-then-emit), which shipped on top
of the residual-hazard pass and the Tier-2 decision pass.
**Batches 10, 11 and 05 have shipped** (10 and 11 plus a joint review fix pass); 10 and 11 were promoted out of
“Deliberately open”, 05 out of “Upcoming batches”, and all three are now in the chronicle. Build verified with
`dotnet build -p:EnableWindowsTargeting=true --no-incremental` → **0 errors, 194 warnings** (all pre-existing —
8 in `src` in files untouched by these batches, 186 xUnit analyzer warnings in the test project; the older
“0 warnings” claim in this file was an incremental-build artifact and has been corrected). **The suite has now
been executed, on Windows, and is green** — the earlier “zero tests have been executed on this Mac” note
described the authoring session, not the code; see the callout below for the measured counts._ Living index
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
| 12 | `7a41a68` → `d3c8c61` | **[Batch 05](05-planner-reason-then-emit.md)** — opt-in reason-then-emit planning: `IAiProviderHandler.DropsReasoningEffortWithTools` on all eight handlers, a global `AppSettings` toggle (default OFF) + its CheckBox, and a tool-FREE reasoning turn ahead of the constrained `emit_plan` turn on the three handlers that drop the effort under tools. Includes the review-fix commit and the two polish commits; this roadmap commit records it | ✅ done |

**Git position:** the branch **is** pushed and tracks `origin/feature/agent-run-spine`, which is now at
`73e15e8` — both the earlier "`1c49b08` / 29 local-only" and the older "`e7df175` / 50 local-only" figures are
stale. As of 2026-07-30 there are **2 local-only commits** at `d3c8c61` (`ba2c266` + `d3c8c61`, Batch 05's
polish pass), i.e. **3 once this roadmap commit lands** (do not trust a hardcoded count here; it goes stale on
the next commit — read it from git; the "17" printed here earlier was already stale when read). Check with
`git rev-list --count origin/feature/agent-run-spine..HEAD` and `git branch -vv`.
Build check everywhere: `dotnet build -p:EnableWindowsTargeting=true --no-incremental`. Re-measured at
`87fa403`, at `7815ce1` and again at `d3c8c61`: **0 errors, 194 warnings**, all pre-existing and unchanged
across every pass since — 3× `CS8602` in `Helpers/DroppedFileReader.cs`, 2× `MVVMTK0034` in `ViewModels/Flow/FlowViewModel.cs`, 3× `MSB3568` for a
duplicate `Memory_Refresh` key present twice in each of the three resx files, and 186 xUnit analyzer warnings
in the test project. **The “0 warnings” figure used earlier in this file was wrong** — it came from an
incremental build, which skips `CoreCompile` and therefore does not re-emit analyzer warnings. The real bar
these batches held is *adds zero warnings*, verified with `--no-incremental` before and after. Always pass
`--no-incremental` when quoting a warning count.

> **Two things that are not batches, and outrank every batch below.**
> 1. ~~**Run the tests.**~~ **DONE 2026-07-29 — and this was the branch's largest risk, so read the result.**
>    The suite executes on Windows; the "net10.0-windows cannot run here" premise was a property of the
>    authoring sessions (macOS), not of the code. Measured with
>    `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`:
>    **2149 total, 0 failed, 2148 passed, 1 skipped** at `8add90c`, **2157 / 0 failed** after the
>    residual-hazard pass, **2194 total / 0 failed / 1 skipped** re-measured on a clean tree at `7815ce1`, and
>    **2224 total / 0 failed / 1 skipped** at `d3c8c61` after Batch 05 (**+30** cases). The `7815ce1` figure is
>    still the correct *pre*-Batch-05 baseline even though the batch starts at `7a41a68`: the only commit
>    between them is `30ebb52`, which is docs-only (two `.md` files) and belongs to no batch.
>    So the ~240 assertions across those commits **do** hold, including the two Batch 11
>    assertions flagged as fixture-sensitive — no threshold or fixture tuning was needed.
>    **What this does NOT cover, and still outranks the batches below:** the entire **manual Windows smoke
>    list** (Batch 11) is undone. A green unit suite is not a smoke test — the two package-bump behaviour
>    concentrations (streamed tool-call coalescing, the seven `OPENAI001` pragma sites) need a real provider
>    round. **Batch 05 lengthened that list**: its opt-in toggle is XAML, and no test in this suite parses a
>    `View`, so the checkbox→settings wiring and a real two-call plan are both unverified (see “Opened by
>    Batch 05”). Also unproven: whether the W1
>    concurrency tests would go red on a revert of `78e16dd` (asserted by reasoning, not demonstration).
>    **Corrected 2026-07-29:** this callout used to add "and the image-attachment hazard is *expected* to fail
>    when smoked". It is not — hazard C was **closed by `b59cfe5`** in the Tier-2 pass, so that smoke item is now
>    the primary *regression check* for that fix, not a known break to confirm.
> 2. **Push — DONE 2026-07-29, and that changes the risk posture. Read why it happened.**
>    `origin/feature/agent-run-spine` is at `73e15e8`, so everything through Batch 05's review-fix commit is on
>    `origin` and pullable by anyone; only Batch 05's polish pass is local (2 commits at `d3c8c61`, 3 once this
>    roadmap commit lands — read the count from git). **The owner pushed it to preserve the work across a
>    machine shutdown, NOT because the smoke round was done.** Do not read this line as the smoke list having
>    been completed: it is untouched, and it still outranks every batch below.
>    The 2026-07-29 owner decision recorded here until 2026-07-30 — "hold the push until the manual Windows
>    smoke round is done, so a provider regression can be fixed before it reaches `origin`" — is therefore
>    **void**, overtaken by the push rather than by the smoke round. Plainly: **the smoke round no longer gates
>    the push, and the obligation is unchanged.** What moved is the cost of a failure. The round now validates
>    code that is *already* on `origin`, so a provider regression it finds is a fix-forward on a shared ref that
>    others may have pulled, not a private rebase — and it can no longer stop such a regression from reaching
>    `origin` at all, only from reaching `main`. `74f964c` remains the commit to revert first if provider
>    behaviour regresses.

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
- **A plan turn can think before it is constrained** (Batch 05, **opt-in, default OFF**) — a plan turn always
  attaches `emit_plan`, and three of the eight provider handlers (`AzureOpenAI`, `Ollama`, `Mistral`, declared
  by `IAiProviderHandler.DropsReasoningEffortWithTools`) omit the configured reasoning effort as soon as tools
  are present, so on those providers the turn that most needs deliberation reasons at the model default. With
  `AppSettings.AgentPlanReasoningTurnEnabled` on, `AgentPlanner.PlanAsync` first spends a **tool-FREE**
  free-form turn — `tools: null` is what makes `AiClientService` compute `hasTools:false`, the only shape that
  still carries the effort — and folds that analysis into the constrained turn: capped at 4000 chars, head
  kept, appended **after** the goal on the **user** message, never the System prompt, because
  `TokenizingAiClientService` rewrites only `ChatRole.User` text and hands the reply back *detokenized* — in the
  System prompt the analysis would ship restored PII past the tokenizer. It can never hard-fail
  planning: an empty answer, any throw, even a gate that cannot be *evaluated* degrades to today's single
  constrained turn, while cancellation propagates; every round reaches `PlanResult.Usage` (I1) and the firm
  retry reuses the one analysis. Default OFF because it doubles a plan-turn cost that is already ≥2 rounds.
  `ReplanAsync` stays single-turn by decision (D3). Not every enabled run is *boosted* — see “Opened by
  Batch 05”.

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
| ~~**1**~~ | — | ~~**Run the test suite on Windows/CI**~~ **DONE 2026-07-29: 2157 total, 0 failed.** What remains is the **manual Windows smoke list**, which a unit suite cannot cover — see the callout above | — | S | a Windows runner |
| 2 | 02 | [Cost ledger](02-cost-ledger.md) — price table populates `CostUsd` | 2 | S | — |
| 3 | 03 | [Audit timeline](03-audit-timeline.md) — per-tool decision trace (plan §11) | 2 | M–L | — |
| 4 | 04 | [Autonomy policy](04-autonomy-policy.md) — `PolicyJson` per-run approval policy | 2 | M–L | MCP gate |
| 5 | 06 | [Run workspace isolation](06-run-workspace-isolation.md) — run-aware file-tool base root + promotion | 3 | M | Milestone B |
| 6 | 07 | [Sub-agents / multi-persona](07-subagents-multipersona.md) — `ParentRunId`/`AssignedPersonaId` + attribution | 3 | L | Batch 11 ✅ shipped |
| 7 | 08 | [Live steering](08-live-steering.md) — plan mutation / nudge / pause / resume | 4 | L | budget-pause, sub-agents |
| 8 | 09 | [Scheduler UI](09-scheduler-ui.md) — create/edit/list agent jobs; **now also owes a re-arm surface + unknown-status handling, see below** | 4 | M | Milestone B |
| — | 01 | [Budget-pause polish](01-budget-pause-polish.md) — **empty**: every item closed by the hardening batch + its fix-up; the file keeps only open assumptions | 2 | — | — |
| — | 05 | [Planner reason-then-emit](05-planner-reason-then-emit.md) | 2 | S–M | ✅ **shipped** `7a41a68`→`d3c8c61` |
| — | 10 | [Durability & lifecycle](10-durability-and-lifecycle.md) | 2 | M | ✅ **shipped** `e4ad6bf`→`630c2c2` |
| — | 11 | [Context compaction](11-context-compaction.md) | 2 | S–M | ✅ **shipped** `74f964c`→`a06358d` |

**Why the manual smoke round now outranks every batch** (this paragraph said “run the tests” until the suite
was executed on 2026-07-29). Batches 10 and 11 were ranked 1 and 2 because two of Batch
10's items were live data-loss paths. They have shipped, so the top risk is no longer a known bug — it is that
the *fix* for those data-loss paths (a semaphore-gated dedicated SQLite connection, a merge write, a WAL
switch) has never been exercised against a real provider or a real user session, only by the unit suite. A wrong gate is a worse failure than the gap it closed. **Batch 05 has now shipped
too**, and it was only ever ahead of 03/04 because it was S–M and unblocked — so with it gone, 03 (audit
timeline) and 04 (autonomy policy) move up behind 02, and both are M–L. Batch 05 also *added* to the smoke
list rather than shortening it, which is one more reason the callout above still outranks all of them.

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

### Residual-hazard pass (2026-07-29) — `045edea` → `87fa403`

Closed six of the items Batches 10 and 11 logged and left open. Five commits, one audit that deliberately
produced no commit, and **the first executed test run on this branch**.

| Commit | What |
|---|---|
| `045edea` | `Compaction: skip only the system prefix we pinned` — role filter → reference identity |
| `42802e0` | `ViewModels: unsubscribe the foreign-run event on dispose` |
| `a5a34a7` | `Chats: pin the non-deferred BeginTransaction the delete-all relies on` — **no behaviour change; the premise was false** |
| `6895b89` | `Tests: pin the MAAI001 containment premise` |
| `87fa403` | `Composer: explain the paused Send while a background run writes the chat` |
| *(none)* | Transitive-package audit → **do not exclude**, recorded above |

**The headline is the test run.** Both batch files claimed "Tests: written, never executed — net10.0-windows
cannot run on macOS, execution deferred to Windows/CI". That was a property of the authoring session, not of
the code. Executed on Windows 11: **2149 total, 0 failed, 2148 passed, 1 skipped** at `8add90c`, rising to
**2157 / 0 failed** after this pass (+8). Two consequences worth recording: the two shrink assertions Batch 11
flagged as possibly-red-on-CI **pass**, so the fix-pass fixtures are sound and the thresholds were never
suspect; and the 19-failure baseline noted mid-branch was closed by the `fedb86c`…`8add90c` test-repair run.

**Two premises turned out to be false, and that is the pass's most useful output.** (i) The
`SQLITE_BUSY_SNAPSHOT` hazard did not exist — `BeginTransaction()` is already `BEGIN IMMEDIATE` because
non-deferred is Microsoft.Data.Sqlite's default (verified empirically on the pinned 10.0.9). (ii) The
transitive-package exclusion would have made the CVE reporting *worse*, not better, by promoting two clean
packages to top-level. Both were investigated before being implemented, which is why neither produced a
wrong change.

Each fix's test was checked for red-before/green-after where the fix was behavioural: the compaction and
dispose regressions were both demonstrated red on a stashed revert, and the containment test was demonstrated
to catch an injected `static ContextWindowCompactionStrategy` field that the **build bar cannot see** (zero
`MAAI001` warnings). The MAAI001 source-scan and the `BeginTransaction` premise pin are guards, not
regressions, and say so in their own comments.

#### ⚠️ NO TEST IN THIS SUITE PARSES A `View`, and it cannot without breaking 42 others

Worth knowing before anyone tries to close it. Every test works against ViewModels, so in `AssistantView.xaml`
an unresolvable `StaticResource`, a missing `loc:Str` key, or a **misspelled `Binding` path** is invisible to a
green build *and* a green suite — markup compilation catches malformed XAML and unknown types/properties, but
resource-key resolution and binding paths are runtime concerns, and a wrong binding path fails **silently**.

A view-parse test was built and then **withdrawn**, because parsing a View requires an `Application` whose
`Resources` carry App.xaml's converters, and `Application.Current` is **process-wide**. Creating it makes
`App.Current.Dispatcher` real — a dispatcher owned by the test's STA thread — so every ViewModel that marshals
through `App.Current.Dispatcher.InvokeAsync` stops running its work inline (today it takes a null-`App.Current`
synchronous fallback). Measured: **42 `MeetingAttendeeViewModelTests` failures**, e.g. state updates never
applying before the assertion. Two further traps found on the way: a thread-per-test design dies with
"Initialization of `Wpf.Ui.Controls.Button` threw an exception" on the *second* test, because the App's merged
control styles are owned by the thread that built them; and binding values do not transfer until the Dispatcher
queue is drained to `SystemIdle`, so an unpumped test asserts the property default.

Closing this properly needs a dispatcher abstraction injected into the ViewModels (so `App.Current` is never
touched directly), or a separate test process. **Until then, XAML changes need manual smoke.** The composer hint
in `87fa403` was nevertheless verified empirically with the throwaway test before it was withdrawn: the view
parses, the `loc:Str` key resolves (the hint was located *by its rendered text*), the hint is `Collapsed` at
`ForeignRunActive == false` and `Visible` at `true`, and a deliberately misspelled binding path was confirmed to
produce the silent always-visible failure **with the build still at 0 errors**.

### Tier-2 decision pass (2026-07-29) — `cbe90a2` → `0784c69`

The five hazards the residual-hazard pass deliberately left for an owner decision were presented as options,
decided, and implemented. Six commits.

| Commit | Hazard | What |
|---|---|---|
| `cbe90a2` + `ee6a2e2` | **B** | A `Once` job retries **only on a pre-model failure** (2 attempts, ~10 min) |
| `b59cfe5` | **C** + **D-A** | Image-bearing turns pinned and charged; outcome logs promoted to release-visible |
| `9022980` | **D-A′** | Each executor seam names WHICH run (and, on the live path, which step) was shrunk |
| `a62ba69` | **A** | The composer gates from a launch-bracket index, closing the ungated `SingleTurn` hole |
| `0784c69` | **E** | A context overflow is named instead of being reported as a tool-support problem |
| `5a6f196` | — | The UI-dispatcher abstraction spec'd as [Batch 12](12-ui-dispatcher-abstraction.md) |

**Gate: 0 errors, exactly 194 warnings (`--no-incremental`), 2194 tests, 0 failed.** Every behavioural fix was
demonstrated red before green by neutralising the fix and re-running — the activation seed, the `SingleTurn`
bracket, image detection, and pre-model classification each fail their own tests when disabled. Three items
carry a guard rather than a regression test and say so in their own comments.

**Two decisions narrowed a recommendation, and both were right to.** B was scoped to **pre-model** failures
only, which removes the duplicate-vault-write risk a whole-run retry carries (a scheduled `AgentTask` runs with
`write_file` granted and attempt 2 replans from scratch). A's index biases toward a **missing** entry rather
than a stale one, because the failure modes are not symmetric: a stale entry is a permanently dead composer
(re-activation takes the live-attach branch and does not re-seed), while a missing one is only the pre-existing
race, which `SaveMergedAsync` already bounds.

**Two claims in the hazard register were wrong and are corrected in place:** the oversized goal fails at
**planning**, not at step 1, and the compactor's pinned-cost comment had its direction **backwards**.

**A flake was NOT pre-existing after all, and chasing it found a real bug — `4ddb281`.** The first version of
this entry called it "observed, not introduced", which was an assumption, not a measurement. Measuring it (6
full-suite runs at `a62ba69` versus 6 at `9022980`) gave **2/6 failures against 0/6**, i.e. A2 caused it. Two
distinct defects were behind it:

- **A product bug.** The A2 recompute skipped any session with no chat id, on the stated grounds that "a
  first-turn chat has no id yet, so no run can be writing it". False: `StartPlannedTurnAsync` attaches a run to
  a brand-new session and the id is only assigned when the first turn *persists*, so a run can be attached to
  an id-less chat — and the pre-A2 handler covered that by matching `ActiveRunId` with no id requirement. The
  race between the persist and the event is what made the loss intermittent instead of obvious.
- **A fixture defect, latent before A2 and exposed by it.** A bare `SynchronizationContext` forwards `Post` to
  the ThreadPool, which guarantees **no ordering**, so two `RunChanged` events raised in quick succession could
  be handled out of order. A2's heavier handler widened the window. The test context now runs callbacks inline
  and in order — faithful, not merely convenient, since that is what the WPF dispatcher does for a post
  originating on the UI thread.

After both fixes: **0/15 isolated and 0/8 full-suite runs fail.** The lesson is worth keeping: a single red
observation is not a rate, and "pre-existing" is a claim that needs two measurements.

**One genuinely pre-existing flake remains, in a file this work never touched:**
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` asserts a task containing `Task.Delay(200)` has
not completed immediately after being fired, so any descheduling longer than 200 ms under parallel load fails
it (~1 run in 8). It is a wall-clock assumption in the test, not a product defect.

### Opened by Batch 10 (2026-07-28) — known, reasoned, not closed

- ~~**`ActivateAsync` races the composer against `RestoreActiveRunAsync`.**~~ **CLOSED `a62ba69`** (2026-07-29)
  by a third option neither of the two known fixes suggested: `IExecutingRunStore`, a lock-free in-process
  index of runs that are actually executing, populated from the **launch brackets** rather than from run rows,
  so activation seeds `ForeignRunActive` **synchronously** before the composer goes live — no await, no lock,
  no flicker, no swallowed Enter. It also closed a hole that was never recorded and had **no bound at all**: a
  `RunShape.SingleTurn` background turn was gated nowhere (the handler matched `session.ActiveRunId` and
  `RestoreActiveRunAsync` filtered `RunShape == Planned`), so `BackgroundAssistantTurnRunner`'s single plain
  `SaveAsync` deleted the user's message outright with no `SaveMergedAsync` to heal it. `Release` is idempotent
  and runs from both the `RunChanged` handler and the launcher's `finally`, because `RunChanged` is raised
  before that `finally`; the reverse lookup is read *before* the release or it erases the answer; registration
  sits after the slot wait, which is deliberately fail-open. `AgentRunBracketTests` pins the bracket premise —
  and its **second** version is the one that does. Keyed on *implementing* an executor contract
  (`IHeadlessRunLauncher` / `IBackgroundAssistantTurnRunner`), because the first version keyed on *depending on*
  `AgentRunOrchestrator` and was wrong in both directions: it MISSED `BackgroundAssistantTurnRunner`, which
  never references the orchestrator — so the SingleTurn bracket sat outside the very rule meant to pin it — and
  it FLAGGED `ScheduledJobBackgroundService`, which only dispatches by delegation and correctly owns no bracket
  of its own. Verified by adding a forgetful executor temporarily and watching the rule name it,
  which was the owner's stated argument against this design. The original description follows for context.
  ~~Activating a hydrated chat returns the session (composer live) *before* the fire-and-forget run lookup can
  set `ForeignRunActive`~~
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
- ~~**No composer hint explains the disabled Send.**~~ **CLOSED `87fa403`** (2026-07-29).
  `Assistant_BackgroundRunActive_Hint` in all three resx files with real German and French, plus a collapsed
  composer `TextBlock` bound to `ForeignRunActive` alone so it never shows for the streaming or empty-composer
  disabled states. No new converter; `Designer.cs` untouched (`loc:Str` resolves via `ResourceManager.GetString`).
- ~~**`WAL` adds one failure mode `busy_timeout` does not cover.**~~ **CLOSED `a5a34a7`** (2026-07-29) — and
  **the premise was false, so nothing needed fixing.** `DeleteAllUnderGateAsync` is indeed the only read-first
  transaction, but `BeginTransaction()` already emits `BEGIN IMMEDIATE`: **non-deferred is
  Microsoft.Data.Sqlite's default.** Verified empirically on the pinned 10.0.9 — a transaction with zero
  statements executed already refuses another connection's write (SQLITE_BUSY 5/5), `deferred: true` lets it
  through, and `ReadUncommitted` is the only isolation level mapping to a deferred BEGIN. The commit writes the
  load-bearing default down and pins it with a test that *demonstrates* the real 517 error via a deliberately
  deferred transaction. The other eight `BeginTransaction()` sites all write first, `SaveMergedAsync` included
  (its read is untransacted, before the transaction opens).
- **A settled `Once` job has almost no re-arm surface.** `UpdateAsync` re-arms `Completed`→`Active` only when
  the recomputed `NextFireAt` lands in the **future**, and it has no `specificDate` parameter — so a settled
  one-off whose date is in the past cannot be moved at all. `ScheduledJobToolHandler` exposes only
  list/create/update/delete (no enable), and there is no scheduled-job ViewModel. **Batch 09 owns this.**
- **No backfill for existing rows** (deliberate): every existing `Once` job with a past `SpecificDate` and
  `Status='Active'` will fire exactly **one more time** on the next 30 s tick before it settles. Real tokens for
  real users — **belongs in the release notes.** Silently retiring them would have swallowed one-offs someone is
  still waiting for. Settled rows are also never garbage-collected (a `Completed` one-off's
  `LastResultEntryId` links to user-visible chat history).
- ~~**`MarkRunFailedAsync` retires a `Once` job on its *first* failure.**~~ **CLOSED `cbe90a2`** (+ `ee6a2e2`
  for a third stale doc). A one-off now gets two attempts ~10 min apart, but **only when the failure is
  provably pre-model** — `NoProvider`, where no `AgentRuns` row exists, no tokens were spent and nothing was
  written. That scoping is the decision, not a simplification: a whole-run retry is not idempotent, because a
  scheduled `AgentTask` runs with `write_file` granted and attempt 2 replans from scratch, so it could silently
  duplicate vault writes. Reasons derived from `run.State` or a caught message describe a run that already
  exists and still settle terminally on the first strike. Zero storage cost — `ConsecutiveFailures` already
  exists, is absent from `SyncScheduledJob`, and survives a pull. Still one statement and one round-trip, with
  all three conditional writes chosen by `CASE` off the same atomic `ConsecutiveFailures + 1`. The `UpdatedAt`
  trap is handled explicitly: a settle bumps it (or the first pull reverts the settle), a re-arm must not (it
  moves only device-local state). The `ELSE Status` asymmetry noted below is fixed too. **Known gap, recorded
  in code:** `LaunchAsync` can also fail genuinely pre-model but arrives as a bare message, so such a one-off
  still dies on the first strike.

### Opened by Batch 11 (2026-07-28) — known, reasoned, not closed

- ~~**An image attachment is the first thing evicted on the Live agent path.**~~ **CLOSED `b59cfe5`**
  (2026-07-29). Image-bearing turns are withheld from the compacted range and re-attached immediately before
  the pinned instruction, in original relative order. The "real image token estimate" that made this look like
  its own batch collapsed into a compile-time constant: `ImageAttachmentProcessor` caps the long edge at
  **1568**, so the largest image that can reach a provider is ~3278 tokens at `w*h/750`, and
  `ImageTokenCharge = 3500` bounds it. Admitted newest-first under a sub-cap (half the remaining input budget,
  floored at one image) with a hard stop refusing any image that would push the window to `MaxOutputTokens` —
  otherwise a many-image request would trip the skip path and be sent **uncompacted**, a provider 400 instead
  of a shrink. Tool content is never withheld, so a function call can never be separated from its result. The
  eviction *unit* was also confirmed: `ToChatMessage` fuses `[TextContent, DataContent]` into one message, so
  what is pinned is a whole turn, never a bare image.
- **`bytes/4` token accounting is wrong in both directions and unfixable from Pia.**
  `CompactionMessageIndex.Create` is `internal`, so no tokenizer can be injected even though
  `Microsoft.ML.Tokenizers` is already a dependency. Dense JSON *under*-counts (absorbed by lowering the
  thresholds to 0.45/0.70); `DataContent` massively over-counts (above). Revisit if `Create` becomes public.
- **`ToolEvictionThreshold = 0.45` is close to inert.** The library's default `ToolCallFormatter` inlines the
  entire tool result into its `[Tool Calls]` summary, so “eviction” is really *tool-group collapse* and the only
  mechanism that actually reduces tokens is truncation at 0.70. A truncating formatter was rejected: it makes
  the model lose data it just fetched and invites a re-call spiral inside a 10-round cap.
- ~~**A sync pull silently disables compaction.**~~ **CLOSED `1c49b08`** — `SyncMapper` now carries
  `MaxContextWindowTokens`/`MaxOutputTokens` over from the existing local row on a pull
  (`SyncMapper.cs:342-343`), so the fields stay device-local without the pull erasing them.
- ~~**The in-step tool-loop insertion has no test at any level.**~~ **CLOSED `261410f`.**
- **The step-1 request is never compacted**, by design and by library behaviour, so a run whose *goal alone*
  overflows still fails. **Mis-sited, corrected 2026-07-29:** it fails **at planning, not at step 1** —
  `AgentPlanner` passes no `contextBudget` at all (`AgentPlanner.cs:118-119`, correctly: two messages, nothing
  to drop), the provider 400s, and `AgentRunOrchestrator` settles the run `Failed` at Planning so step 1 never
  runs. The framed "step 1 fails" case exists only in the middle band where planning succeeds. Amplifier:
  `IsToolNotSupportedError` (`AiClientService.cs:846-859`) returns true for *any* 400, so an overflow logs
  "retrying without tools" and re-sends the same oversized list — a wrong top-line diagnosis. The real cause
  survives only as `ExtraJson`, which nothing renders. **Diagnosis fixed 2026-07-29.** The compaction
  boundary is *right* and was not touched: pinning the goal is correct, and truncating it would let
  `AgentVerifier` PASS a run against a goal the user never gave. So the defect addressed is only that the
  failure lied about itself. `IsContextLengthError` now sits beside its sibling and is consulted FIRST in both
  tool-not-supported catch bodies of `GetChatCompletionWithToolsAsync`, emitting one metadata-only
  `LogWarning` — provider *type* (not the user-named provider), round, message count, whether a budget was
  configured and its numbers; never the provider's raw error string, never the messages. Both executors share
  that method (Headless via `HeadlessTurnExecutor.cs:263` → `BackgroundAssistantTurnRunner.cs:293`, Live via
  `ChatSession.cs:548`), so one insertion per provider path covers the whole fleet. **Scope, precisely:** only
  when `useTools && round == 0`, because that is the only condition reaching those catch bodies, so a provider
  with `SupportsToolCalling` off still overflows undiagnosed. Substring matching, so it will miss provider
  phrasings that are not in the list; a miss degrades to exactly the old behaviour, never to worse.
  **Control flow is untouched by design** — the tool-disabled retry still fires and still costs its round
  trip, which is what keeps the interactive-regression risk at nil. **Still deferred: a machine-readable
  reason code on the run.** Re-verified, not assumed: the classifier is internal to `AiClientService` and
  unreachable from `AgentRunOrchestrator`, which sees only `ex.Message` and swallows it into `ExtraJson`
  (`AgentRunOrchestrator.cs:269` → `AgentRunService.FailAsync`), while `ScheduledJobBackgroundService` reads
  `run?.State.ToString()` (`:245`) and never sees the exception at all.
- ~~**Compaction is invisible to the user**~~ — **CLOSED for diagnosability** by `b59cfe5` + `9022980`, and the
  hazard was *sharpened* on the way: the real defect was not the absence of a Flow card but that both outcome
  lines were `LogDebug` while the log level is **compile-time only** (`Bootstrapper.cs:307`/`:317` read
  `IsDevMode`, which is `#if DEBUG`; no `AddFilter`, no `Logging` config), so a release build could never report
  whether compaction ran and the user could not raise the level to find out. Now: the success line is
  `Information`, the skip line is `Warning` with its numbers (the send that follows it is very likely a provider
  400), and each executor seam adds one line naming **which run** — and, on the live path, **which step** — was
  shrunk, because the compactor holds neither id. Counts and ids only; never message content.
  **Still open, deliberately: there is no USER-VISIBLE surface.** "Why did step 7 forget what step 3 found" is
  now answerable from a release log, which is what a support ticket needs, but not from the app. The persisted
  option (an ambient tally through `StepTurnResult` into the `StepLedger`, ~9 files, breaking two hand-written
  `IAgentRunService` fakes, plus a resx decision) was costed and queued rather than taken.
- ~~**Two smaller leaks**~~ **BOTH CLOSED** (2026-07-29): the `System`-message drop by `045edea` (skip by
  reference identity over the pinned instances, not by role; red-before/green-after test), and
  `AssistantViewModel.Dispose` by `42802e0` (plus a symmetric guard on the sibling event).
- ~~**Nothing enforces the `MAAI001` containment premise.**~~ **CLOSED `6895b89`**
  (`ExperimentalApiContainmentTests`): a reflection walk over every declared surface comparing **namespace
  strings** (so the test project stays free of the `Microsoft.Agents.AI` reference, itself part of the
  containment), carrying a positive control so an empty result cannot read as a pass; plus a source scan pinning
  the pragma to exactly one site and asserting no csproj/`Directory.Build.props`/`.editorconfig` mentions
  `MAAI001`. **The build bar provably cannot catch this** — injecting a `static ContextWindowCompactionStrategy`
  field into the pragma'd file builds with **zero** `MAAI001` warnings; the test names the field.
- ~~**`AgentContextCompactor`'s pinned-cost comment is backwards.**~~ **CLOSED `b59cfe5`**, in the same commit
  that made the charge correct — which is why it was folded in rather than fixed as a one-liner a later commit
  would immediately edit again. The replacement states the real direction: `pinnedCost` is *subtracted* from
  the window, so under-charging leaves a larger input budget and therefore *less* compaction, erring toward
  silently overflowing.
- **The package bump's two behaviour-sensitive concentrations are unverified beyond compiling**: streamed
  tool-call coalescing at `AiClientService.cs:263` (`updates.ToChatResponse()`), and the seven `OPENAI001`
  pragma sites riding the OpenAI 2.10.0 pin that `Microsoft.Extensions.AI.OpenAI` moves. Both need a real
  provider round on Windows. **`74f964c` is the commit to revert first if provider behaviour regresses.**
  ~~Unaudited transitive weight came with it: `Microsoft.Extensions.AI.Evaluation` 10.6.0 and
  `Microsoft.Extensions.VectorData.Abstractions` 9.7.0 are in the restore graph and nothing in Pia uses them.~~
  **AUDITED 2026-07-29 — verdict: DO NOT EXCLUDE, no change made.** Both edges come from
  `Microsoft.Agents.AI` 1.15.0 and from nothing else (swept every node in `project.assets.json`); neither
  `Microsoft.Extensions.AI` nor `.AI.OpenAI` pulls them. Nothing in Pia references either — the only
  `Microsoft.Agents.AI` usage in the tree is `AgentContextCompactor.cs`, and a raw scan of the shipped
  `Microsoft.Agents.AI.dll` finds no reflective-load path. Five reasons the exclusion fails its own goal:
  **(1)** asset exclusion does not touch the restore graph — both stay in `--include-transitive` and
  `--vulnerable --include-transitive` and in any graph-derived SBOM, and the only mechanism that prunes a
  *transitive* package's assets is an explicit **direct** `PackageReference`, which **promotes** them from
  transitive to top-level in exactly those reports. In this repo a direct ref is the *CVE-remediation* pattern
  (see the `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 comment), so using it to hide clean packages inverts the
  signal. **(2)** Payload is 67,424 + 81,952 = **149,376 bytes of a 237,428,184-byte single-file installer =
  0.063%**. **(3)** Pinning them as direct refs makes the next `Microsoft.Agents.AI` bump emit **NU1605**
  downgrade warnings against the zero-new-warnings-over-194 bar, on packages nobody uses. **(4)**
  `ExcludeAssets="all"` would turn a future compile error into a runtime `FileNotFoundException` inside a
  shipped single-file build. **(5)** There is no consumer of the benefit: no trimming (publish is
  `--self-contained -p:PublishSingleFile=true`, **untrimmed**), no SBOM, no CodeQL/Trivy/Snyk, no dependabot,
  and `dotnet list package --vulnerable --include-transitive` reports **no vulnerable packages**. Revisit only
  if a CVE lands on one of them, or if an SBOM/scanner is adopted — then the correct minimal form is a direct
  ref with `ExcludeAssets="runtime"` (not `"all"`), which still would not propagate across the test
  `ProjectReference`.

**Promoted out of this list on 2026-07-28 and now shipped:** the `Once`-job relaunch loop, two writers on one
chat row, the missing write gate on the shared `SqliteContext` connection (all → Batch 10), and
context/trajectory compression (→ Batch 11, whose design step collapsed when `Microsoft.Agents.AI` 1.15.0
shipped `Microsoft.Agents.AI.Compaction` on 2026-07-22 with the atomic tool-group logic already solved).
hermes-comparison §5/rec #5.

### Opened by Batch 05 (2026-07-30) — known, reasoned, not closed

- **`Minimal` / `Low` / `Medium` buy nothing, or less than nothing.** The gate fires for **any** non-`None`
  `AiProvider.ReasoningEffort`, but `ReasoningEffortMapping` collapses the ladder: `Minimal` *and* `Low` both map
  to `Low`, `Medium` to `Medium`, and `High` *and* `XHigh` both to `High` — while the constrained turn, which
  **omits** the field, runs at whatever that provider's own default is. Wherever that default sits at or above
  the requested rung, the extra round recovers **no boost at all**. Azure OpenAI's o-series is the concrete
  case: its default is `medium`, so with effort=`Low` the run pays a full extra provider round to produce an
  analysis reasoned at a **lower** effort than the turn it is meant to improve, and with `Medium` it pays a round
  to request exactly the default. Ollama's default is model-dependent (so it is unknown, not benign), and
  Mistral's is the `none`|`high` ON rung (next bullet). Only `High`/`XHigh` are a real boost — and since they are
  the same rung, `XHigh` is not a further one. **NOT narrowed, deliberately:** `73e15e8`'s committed Mistral
  rationale states that reason-then-emit is *itself* the mechanism — a free-form decomposition the constrained
  turn consumes — and that the boosted effort is an amplifier, not the whole benefit. Narrowing the gate to
  `High`/`XHigh` would contradict that sentence and would deny the decomposition to every `Low`/`Medium` user.
  Recorded instead, which was the reviewer's own minimum ask. Revisit together with D7: both are the same
  question (should a transport flag answer "is the boost worth it?") asked from opposite ends.
- **Mistral gets the split but never the boost** — on *both* halves of its model list, not just one. Already
  spelled out in `MistralProviderHandler`'s comment; it belongs here too, because the roadmap is where someone
  decides whether to enable the toggle. A model **not** in `ReasoningCapableModels` never gets the field on
  either turn (the model-list check runs before the `hasTools` check), so the tool-free turn is at default effort
  as well. A model **in** it keeps reasoning **on** when the field is absent, and Mistral's ladder is `none` |
  `high` only — so the tool-using turn already sits on the one ON rung that exists and the tool-free turn's
  explicit `high` is the same rung. Net: on Mistral the opt-in buys one extra provider round for the
  decomposition and nothing else.
- **One review fix was refused and one polish nit was skipped; both with reasons, neither an oversight.**
  (i) A reviewer asked for `MistralProviderHandler.DropsReasoningEffortWithTools => false`. `73e15e8` accepted
  the **analysis** (above) and rejected the **fix**: the flag states a **transport** fact the handler
  demonstrably implements (the field *is* omitted under tools) and the conformance test reads it off an
  uninitialised instance, so it must stay a constant — `false` would contradict the request the handler builds.
  What changed instead was the contract: the `IAiProviderHandler` XML doc is now explicitly transport-only and
  says `AgentPlanner` reads it as an approximation. Narrowing it honestly needs a model-aware member taking
  `AiProvider`, which D7 rejected. **Do not flip that flag without reopening D7.** (ii)
  `ProviderEditContentDialog.xaml:96` — the dialog that actually sets the `AiProvider.ReasoningEffort` this gate
  reads — carries a **hardcoded English** “Reasoning effort” label with no `loc:Str`. Found while fixing the new
  toggle's German string (`Denkstufe` → `Denkaufwand`, `d3c8c61`) and left alone as out of scope: it is a
  pre-existing gap in a different dialog and a new key needs en/de/fr parity. The polish pass **refuted none** of
  the five nits it examined — all five were real and all five are fixed.
- **Manual-smoke debt, and the toggle is XAML.** The CheckBox's two `Binding` paths
  (`AgentPlanReasoningTurnEnabled`) and the `AssistantView.xaml` relocation in `d3c8c61` resolve only at
  runtime, and **no test in this suite parses a `View`** (see the callout above — closing that needs Batch 12's
  dispatcher abstraction or a separate test process), so a typo renders a checkbox that silently never persists.
  `LocalizationTests` *does* cover the three `loc:Str` keys and their en/de/fr parity, and
  `AppSettingsAgentPlanningTests`' camelCase JSON round-trip is the automated proof that the flag **can**
  persist — the untested part is the wiring between them, plus the relocation itself. There is also no
  `AssistantSettingsViewModel` test at all (four concrete sub-VM dependencies, judged disproportionate for a
  checkbox), so a toggle→restart→still-on check is its only coverage. Two things need a real provider round and
  no unit test can substitute: that a two-call plan against Ollama or Azure OpenAI with an effort configured
  still validates (the run goes `Planned`, not SingleTurn-degraded) and logs its doubled-cost `Information` line
  exactly once with the toggle ON and not at all with it OFF; and **whether reason-then-emit actually produces
  better plans**, which nothing here proves — the suite proves the boosted round happens, degrades safely and is
  paid for. Full list: `05-planner-reason-then-emit.impl.md` §9.

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
