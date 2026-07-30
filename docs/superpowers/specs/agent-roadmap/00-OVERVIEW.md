# Agent System — Roadmap & Status

_Snapshot: 2026-07-30 — as-built at `c92dfdd`, the end of a run that shipped **Batch 04** (autonomy policy)
and then **Batch 03** (audit timeline), each with its own review fix pass. Those two were the last of Phase 2
apart from Batch 02, **so Phase 2 is now complete except for 02** — which is itself no longer a feature but a
deletion (pricing was withdrawn by decision the same day). Unlike the previous snapshot, this one **is** a
single linear range: 04 and 03 were authored in sequence off `dda6703`, 03 on top of 04, so reading the
chronicle down is correct again. **Batches 10, 11, 05, 12, 04 and 03 have shipped.** Build verified with
`dotnet build -t:Rebuild -v:n` (Debug **and** Release) → **0 errors, 0 warnings**. Measured on the final tree
`c92dfdd` twice by two agents; each of the run's four agents also measured it on its own tree, so the bar was
checked at six points, not asserted once at the end. **That bar
is absolute zero, not “no new warnings over 194”**: commit `6cdd4c9` took the build from 194 to zero and
`d9c052f` made zero a commit-ready gate in `CLAUDE.md`, so every “194 warnings, all pre-existing” figure
elsewhere in this file is **historical** — it described the tree before `6cdd4c9` and must not be read as a
target. The 194 were 8 in `src` plus 186 xUnit analyzer warnings in the test project, which is the trap a batch
that adds thirty tests walks into: new tests must add **zero**. This run adds **190 test cases** and held the
bar, so the trap is avoidable rather than theoretical — though it was not avoided by luck: one `xUnit1051` on
an untokened `Task.Delay` did fire during Batch 04's fix pass and was cleared before its commit. Read the count
off MSBuild's `N Warning(s)` summary line — at `-v:n` every warning prints twice, so grepping the log
double-counts. **The suite reaches `failed: 0` on the final tree — 2422 total / 0 failed / 1 skipped**, and
the **three recorded runs of the final tree are identical** (two consecutive by Batch 03's fix pass, one
independent by the roadmap pass that wrote this line), with no re-run needed to get a clean one. Scoped that way
on purpose: two intermittents *did* fire earlier in the run on earlier trees (both recorded below), so "clean on
the final tree" is a claim about `c92dfdd` and **not** an aggregate over the run. Across the whole run the four
agents executed roughly **30** full gates, which is what makes those two intermittents measurements rather than
anecdotes — and re-reading them is the better guide to what a fresh run will do than this line is. See the callout below for the numbers and what is still owed._ Living index
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
| 13 | `1dced2f` → `cac8251` | **[Batch 12](12-ui-dispatcher-abstraction.md)** — `IUiDispatcher` injected into the 4 remaining ViewModels, the exemption list 4 → 1, and the **first `View` in the repo parsed by a test**. Includes its own 2-commit review fix pass | ✅ done |
| 14 | `9a8a639` → `cd13c1a` | **[Batch 04](04-autonomy-policy.md)** — one `ToolClassifier` + one `ToolAutonomy.Resolve` for both run gates, a per-run `RunAutonomyPolicy` in the existing `PolicyJson` v1 envelope, an `AppSettings` default for built-in writes, and voice-mode writes routed through the gate. Includes its own review fix pass | ✅ done |
| 15 | `50d2054` → `c92dfdd` | **[Batch 03](03-audit-timeline.md)** — the per-run audit timeline: an append-only metadata-only `AgentTimelineEvents` store (per-run `Seq`, a 500-row cap + one truncation marker, retention prune), a per-step `AgentTimelineScope` carried to BOTH run gates, and a read-only "Tool activity" trace on the run panel. Includes its own 10-commit review fix pass — see “Opened by Batch 03” below | ✅ done |

**Rows 12 and 13 are siblings, not a sequence — the only place in this table where reading down is misleading.**
Batch 05 and Batch 12 were authored independently from the same base (`73e15e8`) on two machines, so neither is
built on the other, and they met in merge commit **`d2e56e6`**. The rows are ordered by **batch number, not ship
order**: Batch 12 reached `origin` first, as `b32ca14`. The merge has no row of its own because the table
chronicles batches and a merge is not one; what it did is recorded here instead. It touched exactly one file,
this one — **no source or test file was modified by both sides**, which is why the code merged with zero
conflicts. Consequence worth keeping: **no single commit range covers this tree**, so any "measured at `X`"
claim below that spans both batches is measured across the merge, not across a range.

**The chronicle no longer skips Batch 05, and the paragraph that said it did was deleted here.** `b32ca14`
carried a note explaining that Batch 05 had shipped as `7a41a68` → `73e15e8` but had no row, and that the
"Upcoming" table below still listed it at Rank 3. Both halves are now false — row 12 is the row, and the rank
table carries 05 as shipped — so the note was **dropped in the merge** rather than carried forward. It was
accurate when written: the Batch-05 roadmap pass it was describing existed only on the other machine. Recorded
because a reader who saw `b32ca14` will look for it.

**Rows 14 and 15 ARE a sequence — but four commits in that span belong to no batch, and one pair is inside a
range rather than before it.** Batch 03 was authored on top of Batch 04, so unlike rows 12/13 reading down is
correct. What the two ranges do **not** own: `c45f792` and `2c3e661` are the Design step, **two impl specs in
one pair of commits covering BOTH batches**, so neither range can claim them and they sit before row 14's
start; and `30956c5` → `790defd` (the Batch 02 pricing withdrawal — docs-only, three `.md` files then two) sit
**inside** row 15's span, between Batch 03's build pass and its fix pass, because the owner re-scoped 02 while
03 was in review. Same treatment as `30ebb52` below: docs-only, belongs to no batch, does not break the range
it interrupts. A `git log 50d2054..c92dfdd` therefore lists two commits that are not Batch 03's — check the
subject line, not the position.

**Git position, re-measured 2026-07-30 at the end of the 04/03 run — and this paragraph's own warning caught
it:** `origin/feature/agent-run-spine` is at **`5e1d793`** ("add new icon templates"), *not* the `b32ca14` this
paragraph claimed an edit ago. Every value it has held — `73e15e8`, `1c49b08`, `e7df175`, `b32ca14` — went
stale, and so did every hardcoded *count* printed beside them ("17" was already stale when it was read, then
29, then 6, then 2). **So the local-only tail stays described, not counted.** As described: Batch 05's polish
pass and its roadmap passes, the Batch 12 merge (`d2e56e6`), **the zero-warning work itself** (`6cdd4c9`,
`d9c052f`), a second merge (`91e3ea5`), the branding pass (`dda6703`) — which was the **base for the 04/03
run** — and then all of Batches 04 and 03 plus the Batch 02 re-scope. Worth naming explicitly because it is
easy to misread: **the zero-warning bar this file now states is itself local-only**, so a reader who fetches
`origin` and builds will still see 194. Read the position from git, always:
`git rev-list --count origin/feature/agent-run-spine..HEAD` for the number, `git log --oneline
origin/feature/agent-run-spine..HEAD` for *which* — and the second is the part that actually matters when
deciding whether a push is safe. `git branch -vv` prints `[ahead N, behind M]`, and **`behind` is not
hypothetical on this branch**: an unnoticed `behind 7` is what produced the Batch 12 merge. **Still unpushed by
owner decision, and the 04/03 run did not push, merge or rebase** — it was instructed not to and did not.
Build check everywhere: `dotnet build -t:Rebuild -v:n`, **and again with `-c Release`** — the bar is
**0 errors, 0 warnings in BOTH configurations**, measured at `dda6703` before the 04/03 run and again at
`c92dfdd` after it, both configurations both times. Two of the run's agents also checked that the rebuild was a
*genuine* one rather than a skip, by counting `CoreCompile`/`Csc` invocations in the `-v:n` log (4 on the Batch
03 tree: `Pia.Shared`, `Pia.Wpf`, the `Pia.Wpf_<hash>_wpftmp` XAML markup pass, `Pia.Wpf.Tests`). That check is
worth copying — a 7-second "rebuild" looks exactly like an incremental no-op from the summary line alone.

**Superseded, kept because the reasoning still applies.** Up to `cac8251` this paragraph read “0 errors, 194
warnings, all pre-existing” and the bar was *adds zero new warnings*: 3× `CS8602` in
`Helpers/DroppedFileReader.cs`, 2× `MVVMTK0034` in `ViewModels/Flow/FlowViewModel.cs`, 3× `MSB3568` for a
duplicate `Memory_Refresh` key present twice in each of the three resx files, and 186 xUnit analyzer warnings
in the test project. Commit `6cdd4c9` cleared all 194 and `d9c052f` wrote the zero bar into `CLAUDE.md`, so
**194 is now a historical number and never a target**. The measurement discipline is unchanged and still
load-bearing: an **incremental** build skips `CoreCompile` and therefore does not re-emit analyzer warnings, so
always rebuild (`-t:Rebuild`) when quoting a warning count, and read the count off MSBuild's `N Warning(s)`
summary line rather than grepping the log — at `-v:n` every warning prints twice.

> **Two things that are not batches, and outrank every batch below.**
>
> **✅ Resolved 2026-07-30 by the merge run. `b32ca14` said "no executed run describes the current tree, and a
> fresh Windows run is owed" — that run has now happened, and Batch 12's never-executed facts pass.** Measured
> on the merged tree with the standard gate command, **three full runs**: `2232 / 1 failed`, `2232 / 1 failed`,
> then **`2232 total / 0 failed / 1 skipped`**. So the branch's `failed: 0` bar is **met**. The failure in the
> first two runs was `TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` both times — the pre-existing
> wall-clock flake documented below in “Deliberately open”, **4/4 green when its class is run in isolation**, in
> a file neither batch touched. Not a merge regression, but **note the rate**: 2 of 3 full runs here, against the
> “~1 run in 8” that entry claims. Do not read a single red run on this test as a real failure, and do not read a
> single green run as proof the gate is clean either. Every item on `b32ca14`'s watch list came back
> green, checked individually as well as in the full run: `AssistantViewParseTests` **2/2** — the first `View`
> ever parsed by this suite, and the first execution of `WpfStaHost` and of the process's first
> `System.Windows.Application`; `UiDispatcherServiceTests` **5/5**; `WindowManagerServiceTests` **1/1**, i.e.
> `ShowAgentRun_MissingRun_RetractsStaleItem_AndDoesNotThrow` green rather than **hung** (its failure mode under
> a non-pumping host is a hang, not a red test, so this was the run's real risk); `MeetingAttendeeViewModelTests`
> **67/67**, which means Batch 12's `InlineUiDispatcher` double is right. The whole suite finished in 24 s —
> nothing hung.
>
> **One number in `b32ca14` was wrong, and it was the acceptance criterion.** It asked for "`failed: 0` at a
> total **7** above the previous run". The real delta is **+8**: 2 facts in `AssistantViewParseTests` + 5 in
> `UiDispatcherServiceTests` + **1 more in `DependencyInjectionTests`** (the narrower dispatcher-ban `[Fact]`),
> which the "7" overlooked by counting only the two new files. 2224 (`d3c8c61`) + 8 = 2232, exactly as measured,
> so the arithmetic closes with nothing unaccounted for. **Verified by execution, not by counting `[Fact]` lines
> in a diff:** `DependencyInjectionTests` reports **6** cases on the merged tree against **5** at `d3c8c61`, and
> the two *modified* ViewModel test files are unchanged at 22 and 48, so the whole +8 is accounted for.
> Full ordered list in [`12-ui-dispatcher-abstraction.md`](12-ui-dispatcher-abstraction.md) §9, **which now
> carries the green result at its head** — it was written in the future tense of a run that has since happened.
>
> **What this does not resolve:** the **manual Windows smoke round** is still untouched, and it is still the top
> open item (Rank 1 below). A green unit suite is not a smoke test.
>
> 1. ~~**Run the tests.**~~ **DONE 2026-07-29 — and this was the branch's largest risk, so read the result.**
>    The suite executes on Windows; the "net10.0-windows cannot run here" premise was a property of the
>    authoring sessions (macOS), not of the code. Measured with
>    `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`:
>    **2149 total, 0 failed, 2148 passed, 1 skipped** at `8add90c`, **2157 / 0 failed** after the
>    residual-hazard pass, **2194 total / 0 failed / 1 skipped** re-measured on a clean tree at `7815ce1`, and
>    **2224 total / 0 failed / 1 skipped** at `d3c8c61` after Batch 05 (**+30** cases),
>    **2232 total / 0 failed / 1 skipped** on the **merged tree** after Batch 12 (**+8**; reached on the third
>    of three full runs, the other two costing only the known `TaskExtensionsTests` flake — see the ✅ callout
>    above), and **2422 total / 0 failed / 1 skipped** at `c92dfdd` after Batches 04 and 03. That last figure
>    closes arithmetically across four measured stops, which is why it is quoted as a chain rather than as a
>    total: 2232 → **2342** (Batch 04's build pass, **+110**) → **2356** (its fix pass, **+14**, matching the 14
>    new facts exactly) → **2410** (Batch 03's build pass, **+54**) → **2422** (its fix pass, **+12**). Nothing
>    is unaccounted for, and no step was inferred from a diff — every number was read off a run. The `7815ce1`
>    figure is
>    still the correct *pre*-Batch-05 baseline even though the batch starts at `7a41a68`: the only commit
>    between them is `30ebb52`, which is docs-only (two `.md` files) and belongs to no batch. **`d3c8c61` is
>    likewise the correct pre-Batch-12 baseline**, because Batch 12 branched from `73e15e8` and never saw Batch
>    05's two polish commits — the +8 is measured across the merge, not across a linear range, and no single
>    commit range covers it.
>    So the ~240 assertions across those commits **do** hold, including the two Batch 11
>    assertions flagged as fixture-sensitive — no threshold or fixture tuning was needed.
>    **What this does NOT cover, and still outranks the batches below:** the entire **manual Windows smoke
>    list** (Batch 11) is undone. A green unit suite is not a smoke test — the two package-bump behaviour
>    concentrations (streamed tool-call coalescing, the seven `OPENAI001` pragma sites) need a real provider
>    round. **Batch 05 lengthened that list, and Batch 12 did not shorten it** — a premise here changed on
>    2026-07-30 without changing the conclusion, so read the distinction. Batch 05's entry used to say "no test
>    in this suite parses a `View`". That is now false in general (`AssistantViewParseTests` parses one) but
>    still true *where it matters here*: the view Batch 12 parses is `Pia.Views.AssistantView`, the **chat**
>    view, whereas Batch 05's CheckBox lives in `Pia.Views.SettingsViews.AssistantView` — a **different type in
>    a different namespace** that happens to share a file name. So the checkbox→settings wiring and a real
>    two-call plan are **both still unverified** (see “Opened by Batch 05”), for the same reason as before.
>    Also unproven: whether the W1
>    concurrency tests would go red on a revert of `78e16dd` (asserted by reasoning, not demonstration).
>    **Corrected 2026-07-29:** this callout used to add "and the image-attachment hazard is *expected* to fail
>    when smoked". It is not — hazard C was **closed by `b59cfe5`** in the Tier-2 pass, so that smoke item is now
>    the primary *regression check* for that fix, not a known break to confirm.
> 2. **Push — the 2026-07-29 hold is void, and the two sides of this merge disagreed about that. Read why one
>    won.** `origin/feature/agent-run-spine` is at `b32ca14`; local-only is just Batch 05's polish pass, its
>    roadmap pass, and the merge (see “Git position” above — read it from git, never from this file). The hold
>    being void is a fact about **what happened**, not a decision anyone made: the owner pushed Batch 05's range
>    to survive a machine shutdown, and Batch 12's six commits went to `origin` as well. **Neither push waited
>    for the smoke round.** So the 2026-07-29 decision — "hold the push until the manual Windows smoke round is
>    done, so a provider regression can be fixed before it reaches `origin`" — was overtaken by events on both
>    machines independently, and is recorded here only as history.
>
>    `b32ca14` re-asserted the hold and **extended** it to a second concern: Batch 12's acceptance test had never
>    been executed anywhere and fails by *hanging*, so pushing it unrun risked putting a suite-blocking test on
>    `origin`. That was a sound reason, it was written before the push it was trying to gate, and it is now
>    **closed on the merits rather than dismissed**: the suite has been run on Windows, those tests pass, and
>    nothing hung (✅ callout above). The risk it named was real and is retired by measurement.
>
>    **What is unchanged: the smoke obligation itself.** It no longer *gates* anything — it cannot, the code is
>    already on `origin` and pullable — but it is still owed, still untouched, and still outranks every batch
>    below. What the pushes moved is the **cost of a failure**: a provider regression the round finds is now a
>    fix-forward on a shared ref others may have pulled, not a private rebase, and the round can no longer keep
>    such a regression off `origin` at all — only off `main`. `74f964c` remains the commit to revert first if
>    provider behaviour regresses.

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
  to runs created before D1 or with an unreadable envelope. **Two corrections from Batch 04, which had to read
  this column closely.** (i) “**Both** producers” is **three**: the launcher, the interactive create, and
  `SingleTurn`, which writes NULL — so a column-shape claim must allow for the null. (ii) The interactive-origin
  resume was **wider than its own launch** whenever the serializer faulted, because the fault degraded to `null`
  and `null` takes the `{write_file}` floor — the exact escalation this bullet says is closed. 04 replaced the
  degrade with a hardcoded envelope *literal* (an empty grant list, not an absent one), pinned by both a shape
  test and a round-trip test. **Still open:** the floor itself remains **origin-blind**, so a pre-D1 row or a
  corrupted column still takes `{write_file}` — see “Opened by Batch 04”.
- **MCP behind the gate** — interactive approval + unattended grant gate + a destructive-tool guard that
  now covers **both** paths: interactively it never auto-approves a destructive MCP call, and unattended it
  refuses a *granted* tool that is both delete-like and external (fail-closed if MCP-ness can't be derived).
  The delete-like rule covers the whole destructive stem family, not just "delete".
  **“Fail-closed” was too flat, corrected by Batch 04:** mapping a derivation fault to *external* is closed for
  the **floor** and **open for grantability** — a non-delete-like *built-in* misread as external would be
  offered “Always allow”. Both doc comments now state the direction instead of the word. The plumbing to close
  it was **declined, not refuted** (04 §13.6): the path needs a null tool name, which a pending action that
  supplied one cannot produce, and the fix would have put a second auto-approval expression inside the very file
  the architecture rule exists to keep at one.
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
- **A ViewModel's threading behaviour is a constructor argument** (Batch 12) — `IUiDispatcher`
  (`Post`/`PostAsync`/`PostOrRun`) is injected into the four ViewModels that used to reach for
  `App.Current.Dispatcher`, and `UiDispatcherService` is now the **only** place in the ViewModel layer's reach
  that reads it. `git grep "Application\.Current" -- src/Pia.Wpf/ViewModels/` returns **nothing**. The prize is
  not the deletion, it is that threading became **substitutable, greppable and reviewable**: Batch 10's
  `ForeignRunActive` marshaling and Batch 11's compaction both had to *reason* about "which thread is this on",
  and that reasoning is now a parameter you can see in a ctor signature, swap in a test, and pin with a rule.
  Which it is: the blanket "ViewModels must not reference `System.Windows`" rule went from **4** hand-maintained
  exemptions to **1** (`AssistantViewModel`, and only for `BitmapSource` + `ICommand` — both roots measured),
  with a second narrower `[Fact]` keeping the dispatcher ban enforced for even that one. And because the
  process-global static is gone, **the first `View` in this repo is now parsed by a test**
  (`AssistantViewParseTests` over `AssistantView`), which is what the callout below used to say was impossible
  — the 42 failures it costs no longer exist to be paid.
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
- **How much a run may do without asking is one decision, taken in one place** (Batch 04) — every gated tool
  call on every surface now resolves through a single pure function, `ToolAutonomy.Resolve`, fed by a single
  `ToolClassifier`. What each gate used to decide for itself (is this external? is it delete-like? is it
  allowlisted? is it granted?) is now an input computed by whoever already owned that answer, so no comparer,
  allowlist or stem table changed and every pre-existing permission test passes unmodified. The **destructive
  floor is structural rather than remembered**: it is evaluated *before* any policy branch, a class grant can
  never cover a delete-like name at all, and a scoped architecture rule pins the three gate files at an exact
  count of decision calls each — so a *removed* check goes red, not just a duplicated one. On top of that floor
  sits an **additive** authority: a per-run `RunAutonomyPolicy` (a list of tool *classes*, never names —
  authored from a settings preset, resolved at launch, and stored in the run's existing `v:1` grant envelope) is
  the only thing that can widen a run, and it can only widen it over non-destructive built-ins. Class-keyed
  because a name list cannot work here: three name sets with three different comparers already disagree,
  tool-name routes are last-wins with no collision detection, and a list authored when a job is created cannot
  know what MCP tools will exist when it fires. **The envelope is the run's authority of record** — a resume
  reads the policy from the run, never from settings, so flipping the setting while a run is parked cannot
  widen it on Continue. Two user-visible consequences: an interactive Planned run with the setting on shows a
  *pre-resolved accepted* card for a covered write (never nothing — silence would mean the card-before-execute
  ordering was lost), and **voice mode lost a capability it should never have had** — every write it made was
  previously ungated, and now goes through the same gate as everything else.
- **A run records what it was allowed to do** (Batch 03) — `AgentTimelineEvents` is an append-only,
  **metadata-only** store with one row per *gated* tool call, written after the outcome is known: the decision,
  the rule behind it, the surface, the tool name and class, argument/result *lengths*, duration. **No hash and
  no payload, deliberately** — a hash of `{"path":…}` is a brute-forceable confirmation oracle, so a reference
  here is only ever an id, a count, or a name already safe at `Information`. Attribution reaches the gate on a
  per-step sink carried by the turn spec, which is the only carrier that knows both the run and the step. The
  table is **bounded on both axes** — a 500-row per-run cap that appends exactly one truncation marker and
  survives a restart without appending a second, plus a retention prune on each row's own `CreatedAt` (a
  crash-swept run's `CompletedAt` is NULL forever, so it could not be the key). Both run gates emit through one
  shared bracket, so Live and Headless record the *same* decision for the same reason; bookkeeping is
  failure-isolated, and the store's allocator lock is split from its connection lock so a UI-thread emit cannot
  queue behind a writer's INSERT or the prune's DELETE. Surfaced as a read-only, collapsed **"Tool activity"**
  expander on the run panel that re-reads on every expand and distinguishes *"nothing was recorded"* from
  *"this could not be read"* — it never renders a failed read as a positive claim of emptiness, and never
  renders an errored call as a success. **Per-device and gates-only** — see “Opened by Batch 03”.

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
| **1** | — | **Manual Windows smoke round.** The unit half is DONE — 2422 / **0 failed** / 1 skipped at `c92dfdd`, 2026-07-30. **Batches 04 and 03 both lengthened this list and neither shortened it**, and 04's share is the sharpest kind: a **user-visible capability removal** (a write in voice mode now declines) that no test can confirm looks right. What a unit suite cannot cover remains: a real provider round, a real MCP server, and the DE/FR render — see the callout above and both “Opened by” sections | — | S | a Windows runner + a live provider |
| 2 | 02 | [Remove `CostUsd`](02-cost-ledger.md) — pricing **withdrawn** by decision 2026-07-30; the batch now deletes the half-built seam. **The last of Phase 2**, and the only batch below that is a deletion rather than a feature | 2 | XS | — |
| 3 | 06 | [Run workspace isolation](06-run-workspace-isolation.md) — run-aware file-tool base root + promotion | 3 | M | Milestone B |
| 4 | 07 | [Sub-agents / multi-persona](07-subagents-multipersona.md) — `ParentRunId`/`AssignedPersonaId` + attribution | 3 | L | Batch 11 ✅ shipped |
| 5 | 09 | [Scheduler UI](09-scheduler-ui.md) — create/edit/list agent jobs; **now also owes a re-arm surface + unknown-status handling, see below** | 4 | M | Milestone B ✅; **Batch 04 ✅ shipped** — the autonomy policy it needs to render now exists |
| 6 | 08 | [Live steering](08-live-steering.md) — plan mutation / nudge / pause / resume | 4 | L | budget-pause ✅, sub-agents (07) |
| — | 01 | [Budget-pause polish](01-budget-pause-polish.md) — **empty**: every item closed by the hardening batch + its fix-up; the file keeps only open assumptions | 2 | — | — |
| — | 05 | [Planner reason-then-emit](05-planner-reason-then-emit.md) | 2 | S–M | ✅ **shipped** `7a41a68`→`d3c8c61` |
| — | 10 | [Durability & lifecycle](10-durability-and-lifecycle.md) | 2 | M | ✅ **shipped** `e4ad6bf`→`630c2c2` |
| — | 11 | [Context compaction](11-context-compaction.md) | 2 | S–M | ✅ **shipped** `74f964c`→`a06358d` |
| — | 12 | [UI-dispatcher abstraction](12-ui-dispatcher-abstraction.md) | 2 | M | ✅ **shipped** `1dced2f`→`cac8251` — tests **run green 2026-07-30** on the merged tree |
| — | 04 | [Autonomy policy](04-autonomy-policy.md) | 2 | M–L | ✅ **shipped** `9a8a639`→`cd13c1a` — spec'd in `c45f792`, see “Opened by Batch 04” |
| — | 03 | [Audit timeline](03-audit-timeline.md) | 2 | M–L | ✅ **shipped** `50d2054`→`c92dfdd` — **device-local**, see “Opened by Batch 03” |

**Why the manual smoke round now outranks every batch** (this paragraph said “run the tests” until the suite
was executed on 2026-07-29). Batches 10 and 11 were ranked 1 and 2 because two of Batch
10's items were live data-loss paths. They have shipped, so the top risk is no longer a known bug — it is that
the *fix* for those data-loss paths (a semaphore-gated dedicated SQLite connection, a merge write, a WAL
switch) has never been exercised against a real provider or a real user session, only by the unit suite. A wrong
gate is a worse failure than the gap it closed. **Batch 05 has now shipped too**, and it was only ever ahead of
03/04 because it was S–M and unblocked — so with it gone, 03 (audit timeline) and 04 (autonomy policy) move up
behind 02, and both are M–L. Batch 05 also *added* to the smoke list rather than shortening it, which is one
more reason the callout above still outranks all of them. **04 and 03 have since shipped as well, and the
argument survives them intact — in fact it strengthened.** Both add a *gate* or a *record of a gate*, which is
the same category as Batch 10's: a wrong autonomy decision is worse than the friction it removed. Between them
they added **seven** irreducibly manual items (a real MCP server, a real provider round, the two settings
CheckBoxes' binding paths, a park→flip→Continue round, the voice-mode refusal, a cross-restart trace, and the
DE/FR render — now the longest agent-settings string in three locales), so the gap between Rank 1 and Rank 2
is wider today than it was this morning.

**Batch 12 briefly put “run the tests” back at Rank 1, and that has now been paid.** `b32ca14` argued the point
literally: 7 (in fact 8) new facts had never run, one creates the process's first `Application`, and the
neighbour most likely to notice fails by *hanging* — so the cheapest item on the list was also the only one that
could say whether those commits were safe. The merge run did exactly that and came back green, which is why
Rank 1 is once again the **manual smoke round alone**. Keep the argument, though: it is the general case. Any
batch that adds tests which cannot be executed where they are authored re-earns Rank 1 until someone runs them.

~~Phase 2 now completes at Batch 03/04.~~ **It has: 03 and 04 both shipped 2026-07-30, so Phase 2 is complete
except for Batch 02** — and 02 is a **deletion**, not a feature, since pricing was withdrawn the same day. Read
that as the phase being done in capability terms with one cleanup outstanding. Batches 06–09 are Phase 3/4;
their seams may shift — re-scope at the design step.

**Two rank changes, both consequences of Batch 04 rather than reprioritisation.** 09 moved ahead of 08 because
its blocker cleared: 09 owes a job-creation surface whose payload is "goal + schedule + budget + **autonomy
policy**" (`09-scheduler-ui.md:20`/`:25`, which names the dependency on Batch 04 explicitly), and that policy
now exists as `RunAutonomyPolicy` with a resolved class list already persisted in `AgentRuns.PolicyJson` and a
settings preset to author it from — so 09 has something real to bind to, whereas 08 still waits on 07. Nothing
about 08 got worse. **Note that rank now deliberately crosses phase**: 09 is a Phase-4 batch sitting between two
Phase-3 ones. That is not a typo — per the note above the table, `#` is a file ID and **Rank is the only priority
column**, so a cleared dependency can move a later-phase batch ahead of an earlier-phase one. It is the first
time on this branch that it has, which is why it is called out rather than left to be read as an error.

~~`PolicyJson` is no longer NULL — it carries the launch grant envelope — so Batch 04 must *extend* that
document, not claim the column.~~ **Done, and the instruction was right about the constraint but the batch
found it stricter than stated.** Batch 04 added `policy` as an **additive member of the existing `v:1`
envelope** and deliberately did **not** bump `GrantEnvelopeVersion`, because `envelope.V != 1` is an *exact
equality* check: a bump makes every already-persisted envelope unreadable at once. `GrantEnvelopeJsonOptions`
sets no `UnmappedMemberHandling`, so additive members interoperate in **both** directions for free. Proved by
mutation, not argued: bumping to `v:2` reds five new policy facts *and* the pre-existing
`GrantEnvelope_IsVersionedCamelCase` tripwire. One correction to the column's own description while we are here
— **`PolicyJson` has three producers, not two**: the headless launcher, the interactive Agent-mode create, and
`SingleTurn`, which writes NULL. There is no `UPDATE` path anywhere, which is what makes the envelope the run's
authority of record.

**Batch 09 picked up two obligations from Batch 10's W3:** it must render
an unknown/out-of-range `ScheduledJobStatus` safely (an older peer receives the new ordinal `3` over the sync
wire and stores it as the string `"3"`, unvalidated at `SyncMapper.cs:953`/`:974`), and it owns the missing
re-arm surface for a settled one-off (see “Deliberately open”). **Batch 04 adds a third, and it is the same
shape:** if 09 ever lets a job carry its *own* policy rather than inheriting the global setting, that class
list becomes model- or peer-authored input and needs `ParseGrantedTools`' treatment — `SyncScheduledJob`'s
`GrantedTools` is peer-writable and stored unvalidated today (04 §13.2).

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

#### ⚠️ ~~NO TEST IN THIS SUITE PARSES A `View`, and it cannot without breaking 42 others~~ — HALF CLOSED by Batch 12

**Corrected 2026-07-30. Read the correction first, then the original, which is kept because its diagnosis is
what made the fix possible.**

**One view is now parsed:** `AssistantViewParseTests` parses `AssistantView`, asserts the composer hint is
located by its rendered EN text and that its `Visibility` tracks `ForeignRunActive`, and sweeps the parsed tree
for unresolved `loc:Str` keys. So the headline claim ("no test parses a `View`") is **false as of `aca30bd`**,
and the "it cannot without breaking 42 others" clause is false too — **the 42-failure cost this callout
records is exactly what [Batch 12](12-ui-dispatcher-abstraction.md) removed.** The diagnosis below was right
about the mechanism and right about the fix: the blocker was never the view test, it was that the ViewModel
layer's threading depended on a process-global static. `IUiDispatcher` made that a ctor argument, the test
project injects an inline double, and the live `Application` stopped being able to change any ViewModel's
behaviour. Both "further traps" below were also load-bearing and both were reproduced verbatim in the shipped
design: **one** shared never-torn-down STA thread (a thread-per-test host really does die on the second test),
and a `Pump()` to `SystemIdle` before every bound read.

**Three things stay true, and they are why this callout is only *half* closed:**

1. **`AssistantView` is the first view parsed, not the last.** Every other `View` in the repo still carries the
   full silent-misspelled-binding hazard, unchanged. What Batch 12 bought is that the *next* view test is a
   ~20-line file reusing `WpfStaHost`, instead of a batch — so the remaining exposure is now a chore, not a
   blocker.
2. **The sweep is narrower than it sounds.** It sees `TextBlock.Text` only: 4 of `AssistantView.xaml`'s 22
   `loc:Str` usages. `ToolTip=` (11), `Content=` (5), `PlaceholderText=` and `Value=` need template
   application, which the test deliberately never triggers.
3. ~~**The new test has never been executed.**~~ **EXECUTED 2026-07-30, green.** It was authored on macOS, where
   the suite cannot run at all, and its worst failure mode was a *hang* in `WindowManagerServiceTests` rather
   than a red test. Both are now settled by measurement on the merged tree: `AssistantViewParseTests` 2/2,
   `WindowManagerServiceTests` 1/1 (not hung), whole suite 2232 / 0 failed in 24 s. So **"one view is
   parsed" is now a claim about a green result, not just about the code** — which is what item 1 and item 2
   above are scoped against, and neither of them changed.

**XAML changes outside `AssistantView` still need manual smoke.** The original entry follows, unedited — its
last paragraph is the prediction this batch cashed:

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

**Gate as measured THEN: 0 errors, exactly 194 warnings (`--no-incremental`), 2194 tests, 0 failed** — a
historical record of this pass, not a bar; `6cdd4c9` later took the build to zero warnings. Every behavioural fix was
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
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` makes **two** wall-clock assumptions about a task
containing `Task.Delay(200)`, and either can fail under parallel load: `Assert.False(completed)` at `:75`, that
it has *not* finished immediately after being fired, and `Assert.True(completed)` at `:78`, that it *has*
finished after a further `Task.Delay(300)`. Not a product defect either way. **Rate corrected 2026-07-30:** this
entry said “~1 run in 8”; on the merge run it fired in **2 of 3** full runs, and the observed failure was `:78`
(300 ms not enough for a 200 ms delay, i.e. hard scheduling starvation) — not the `:75` direction this entry used
to describe as the only one. In isolation the class is **4/4 green**. Practical rule: this test failing alone
does not fail the gate — re-run it isolated to confirm — but because it is this frequent, a clean full run is
worth repeating before quoting `failed: 0`.

**Rate corrected AGAIN 2026-07-30, and this entry's own lesson now applies to itself.** Across the 04/03 run
the four agents executed roughly **30** full gates and it fired **twice**, both in the same agent's series
(Batch 04's build pass, 2 of 7; then 0 of 3, 0 of 5, 0 of ~14, and 0 of 1 in the roadmap pass). So neither “~1
in 8” nor “2 of 3” was a rate — the first was a guess and the second was three observations, which is exactly
the “a single red observation is not a rate” caution recorded above, read against the entry that carries it.
Best current statement: **low single-digit percent, bursty, load-dependent**, and consistent with a wall-clock
assumption rather than a defect. The practical rule is unchanged and is the only part worth trusting.

**A SECOND intermittent appeared in this run, and nobody proved it pre-existing — read the uncertainty, it is
the useful part.** `AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
failed **once in Batch 03's build pass (5 runs)** and **once in its fix pass (~14 runs)**, and is **3/3 then
4/4 green when its class runs isolated**. Both agents declined to call it pre-existing, for the same honest
reason: **neither measured it at base**, and Batch 03 named *its own* tests as the most plausible load source —
the three cap facts each emit 600 events through a serial writer that commits **one row per auto-commit
transaction**, adding roughly **1500 individual commits** to a parallel run, against a test whose own comment
says its detection window is “**PROBABILISTIC**, not guaranteed” and microseconds wide at `busy_timeout=100`.
A base run has weak power (one run against a ~1-in-5 rate) *and* an unresolvable confound (the cap tests do not
exist at base), which is why neither spent one — a defensible call, and better recorded than papered over. If
it becomes a nuisance the reachable fix is to **batch the cap tests' emits**; note that the builder's own
proposed fix — “lower `MaxEventsPerRun` in the fixture” — is **not reachable**, because it is a `public const`
baked into the test assembly at compile time, and making it an instance property would change the render
surface, which reads it statically.

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
  downgrade warnings against the zero-warnings bar (written when that bar was “no new warnings over 194”; since
  `6cdd4c9` it is absolute zero, which makes this objection *stronger*), on packages nobody uses. **(4)**
  `ExcludeAssets="all"` would turn a future compile error into a runtime `FileNotFoundException` inside a
  shipped single-file build. **(5)** There is no consumer of the benefit: no trimming (publish is
  `--self-contained -p:PublishSingleFile=true`, **untrimmed**), no SBOM, no CodeQL/Trivy/Snyk, no dependabot,
  and `dotnet list package --vulnerable --include-transitive` reports **no vulnerable packages**. Revisit only
  if a CVE lands on one of them, or if an SBOM/scanner is adopted — then the correct minimal form is a direct
  ref with `ExcludeAssets="runtime"` (not `"all"`), which still would not propagate across the test
  `ProjectReference`.

### Opened by Batch 12 (2026-07-30) — known, reasoned, not closed

- ~~**Batch 12's own tests have never been executed** — 7 facts, on any machine.~~ **CLOSED 2026-07-30 by the
  merge run**, and it was the branch's highest-priority open item until then. The concern was specific and
  sound: one fact creates the process's first `System.Windows.Application`, and a neighbour it perturbs
  (`WindowManagerServiceTests.ShowAgentRun_MissingRun_RetractsStaleItem_AndDoesNotThrow`) fails by **hanging**
  rather than by going red, so an unrun push risked a suite-blocking test. Measured on the merged tree:
  `AssistantViewParseTests` 2/2, `UiDispatcherServiceTests` 5/5, `WindowManagerServiceTests` 1/1 not hung,
  `MeetingAttendeeViewModelTests` 67/67, suite 2232 / **0 failed** / 1 skipped in 24 s. **The count was
  8, not 7** — the eighth is the new `[Fact]` in `DependencyInjectionTests`; see the ✅ callout at the top.
  The remaining bullets in this section are **not** closed by that run: they are design consequences of a
  process-wide `Application`, not predictions about one suite execution.
- **A live `Application` now exists for every collection scheduled after `WpfApplicationStatic`** — and xunit,
  not us, decides whether the serial group runs before or after the parallel group. If before, the exposure is
  the whole suite. Inspection narrows the blast radius a long way (no test constructs any of the ~13
  `TryFindResource` converters, `OutputService`, `ThemeService` or `TrayIconService`; the notification surfaces
  are entered through internal seams that bypass their dispatcher reads), but `EmojiInlineBuilderTests` under a
  live cross-thread resource dictionary remains a genuine unknown. **Narrowed, not closed, by the 2026-07-30
  run:** the full suite went green apart from the known `TaskExtensionsTests` flake, so on *that* ordering
  nothing in the blast radius broke. This bullet's whole point, though, is that the ordering is xunit's choice
  and not ours — so one green observation is not a proof over orderings, which is exactly the "a single red
  observation is not a rate" lesson recorded below, read in the other direction. Treat an
  `EmojiInlineBuilderTests` failure that appears without a source change as this, not as a flake.
- **The STA host is a process-wide singleton that can never be torn down**, so a future test needing a
  *different* `Application` configuration cannot have one. `Application.Current` is not nullable once set.
- **Category (d): 11 service sites in 7 files still read `Application.Current` directly**, and
  `OutputService.cs`'s is **still unguarded** — which this batch made *more* dangerous, not less: with a live
  `Application` in the test process, that blocking `Invoke` becomes a hang rather than an NRE if a test ever
  reaches it. Adopting `IUiDispatcher` there is now mechanical.
- **`AssistantViewModel`'s exemption needs TWO refactors, not one.** Measured: its complete `System.Windows`
  set is `{System.Windows.Input.ICommand, System.Windows.Media.Imaging.BitmapSource}`. Moving the
  clipboard→attachment conversion out of the VM (the story the exemption comment used to tell on its own) is
  necessary but **not sufficient** — two `ICommand.Execute(null)` call sites remain, and `Execute` is declared
  on `ICommand`, so casting to the toolkit's `IRelayCommand` does not help.
- **`MeetingAttendeeViewModel`'s exemption was vestigial for three batches, because NetArchTest 1.3.2 does not
  resolve base-type dependencies transitively.** The ViewModel rule is a ratchet on the type that *physically
  names* the dependency, so a future ViewModel can inherit a `System.Windows` dependency and stay green. Worth
  knowing before trusting that rule as a boundary.
- **The ViewModel-level `Post`-vs-`PostOrRun` choice is unpinned.** `UiDispatcherServiceTests` pins the
  service's three semantics against a real pumping dispatcher, but the test double collapses all three to an
  inline call by design — so no ViewModel test would notice if `VoiceModeViewModel`'s silence-timer site were
  switched from `Post` (queue) to `PostOrRun` (which would run `TransitionToProcessingAsync` inside
  `Timer.Elapsed`).
- **Views other than `AssistantView` are still unparsed**, and the loc-key sweep covers `TextBlock.Text` only —
  see the corrected callout above.

**Promoted out of this list on 2026-07-28 and now shipped:** the `Once`-job relaunch loop, two writers on one
chat row, the missing write gate on the shared `SqliteContext` connection (all → Batch 10), and
context/trajectory compression (→ Batch 11, whose design step collapsed when `Microsoft.Agents.AI` 1.15.0
shipped `Microsoft.Agents.AI.Compaction` on 2026-07-22 with the atomic tool-group logic already solved).
hermes-comparison §5/rec #5.

### Opened by Batch 04 (2026-07-30) — known, reasoned, not closed

Numbered `§13.N` references are to [`04-autonomy-policy.impl.md`](04-autonomy-policy.impl.md), where each of
these carries its full reasoning and its escape hatch.

- **RELEASE NOTES: voice mode lost a capability, on purpose.** This is the batch's one user-facing *removal*
  and the only item here a user will notice unprompted. Voice mode used to execute **every** write tool with no
  gate at all — no card, no grant, no transcript entry, on the one surface that has nowhere to show a card.
  Verified against `dda6703` rather than taken from a report: `AssistantViewModel.cs:1496` is
  `var actionResult = await pendingAction.Execute();`, sitting under a comment that says it in words —
  *“Auto-approve write operations in voice mode (no dialog)”* — with no condition between them. It now goes through the same resolver as everything else, so
  asking Pia out loud to write a file **declines and names the chat window** unless the agent-write setting is
  on, while the four curated additive tools (todo, reminder, list-append, object-create) still work. Belongs in
  the release notes for the same reason the `Once`-job backfill does: it is correct, it is deliberate, and a
  user who relied on it will experience it as a regression unless told.
- **The resume grant floor is still origin-blind** (§13.1). D12 removed the only *reachable* path by which an
  interactive-origin resume could end up wider than its launch, but a row created before the policy landed, or
  one with a corrupted `PolicyJson`, still takes the `{write_file}` floor regardless of which surface created
  it. **Not implementable from today's signals, and that is the finding**: the interactive Planned create and
  the “Run in background” detach both persist `TriggerKind = User` + `RunShape.Planned`, so nothing on the row
  distinguishes them. Closing it needs a new **append-only** `AgentRunTrigger` ordinal — deliberately out of
  scope, since inventing a persisted ordinal to satisfy a fallback is a schema decision, not a fix. Whoever
  takes it should also make `ResumeFloorGrants` *reference* `DefaultGrantedWrites` rather than duplicate its
  value, which is how the two could silently diverge.
- **A model-authored or per-run policy would need `ParseGrantedTools`' treatment** (§13.2). Today the class list
  is authored **only** from settings, so nothing untrusted ever reaches it and the reader can be permissive. The
  moment a per-run editor or a `create_scheduled_research` parameter can author one, that list must be filtered
  the way tool *names* already are — and must reckon with `SyncScheduledJob.GrantedTools` being **peer-writable
  and stored unvalidated**. Recorded now because the safety of the current reader is a property of who writes
  it, not of the reader.
- **The curated allowlist is honoured interactively and in voice, but NOT unattended** (§13.3) — `IsAllowlisted`
  is always `false` on the headless path, because `IToolPermissionService` is injected into neither headless
  file. That is today's behaviour restated rather than a change, and it is now **pinned by a test**
  (`ToolAutonomyTests.Unattended_TheAllowlistIsNotHonoured`) precisely so a future tidy-up is a deliberate
  decision with a red test in front of it instead of a quiet widening. Whether those four additive tools
  *should* be free on a scheduled job is a real question and deserves its own batch.
- **`ExecutePendingActionAsync` is dead surface on all seven handler interfaces, and “a pending action implies a
  gated call” is not universally true** (§13.4). Three handlers (`Todo`, `Reminder`, `ScheduledJob`) convert a
  pending action into an immediate result on their `TargetId`-null error paths, so such a call executes upstream
  of any gate. Unchanged by this batch, and load-bearing for Batch 03: it is one reason the audit trace is a
  record of *gated calls*, not of every effect.
- **Tool-name route collisions are still silent** (§13.5) — `PluginService`'s `_toolNameRoutes` is **last-wins
  with no collision detection**, and `IsAutoApproveEligible` is name-only with no `PluginId` restriction.
  **Partly closed:** a shadowing MCP server can no longer inherit the allowlist in voice mode, because that
  branch now also requires the tool's *class* not to be external — the discriminator is the route, not the name.
  The underlying registration is still silent, though, and still deserves a `RegisterHandler` collision warning.
- **Three review findings were DECLINED rather than refuted, and the distinction is the point.** Each premise
  was **accepted** and the fix judged wrong for a stated reason; none is a disagreement about facts.
  (i) §13.6, threading a `routeKnown` flag so the derivation-fault path is closed for grantability too: the
  premise is right and both doc comments were corrected because of it, but the path needs a **null tool name**,
  which a pending action that supplied one cannot produce — and the fix would have placed a *second*
  auto-approval expression inside the one file the architecture rule exists to keep at exactly one, where the
  rule bans tokens and would not have seen it. Trading a live structural invariant for an unreachable path is
  the wrong direction. (ii) §13.7, relaying the policy into the `SingleTurn` background path: **a named
  executor-parity gap**, and the direction is *restrictive* — with the setting on, a scheduled `AgentTask` will
  auto-approve a covered write while a scheduled `Research` job still refuses the identical tool. Recorded at
  the call site in a 12-line comment naming both job kinds and the exact refusal string, because **widening an
  unattended write path off a review nit, with no decision behind it, is not a call a fix pass should make**. A
  future batch should decide it deliberately rather than read the missing argument as an oversight.
  (iii) §13.8, intersecting the resume reader's class list against the settings preset: declined because the
  preset is *the settings preset*, not “everything an envelope may legally carry”, so pinning the reader to it
  would silently narrow the first per-run policy a later batch authors, with no failing test to explain why.
  The finding's own reachability paragraph agrees it is defence-in-depth. **The other half of that same finding
  WAS implemented** — both halves of the reader now apply the same readability test, because without it the
  documented “an unreadable envelope loses the policy before it loses the grant list” asymmetry *inverted* for
  one document shape.
- **A premise was DISPROVED empirically, and it is the most useful thing this batch produced.** The classifier
  was briefly given `_ => ToolClass.External` as its fallback, on the reasonable-sounding grounds that a genuine
  MCP tool's card should keep offering “Always allow”. That is **wrong at a gate**, and it was caught by a red
  test rather than by reasoning: an existing fact's fake pending action has a plugin literally named `plugin`,
  so a **built-in `delete_file`** classified as external and tripped the destructive floor. The lesson
  generalises past the test that found it — **externality at a gate is a property of the ROUTE and of nothing
  else**, because otherwise a built-in renamed via `ApplyServerMetadata` becomes grantable-as-external by name.
  Fixed by restoring `_ => Unknown`, adding a separately-documented name-only guess for the *card* alone, and
  **banning that method by name from both gate files** in the architecture rule. The one commit-1 test that
  asserted the old fallback was flipped: the invariant was right, the fallback was not.
- **The persisted gate vocabulary grew past what the specs said, and both specs were stale in a way that would
  have landed in the next batch.** `ToolGateDecision` runs **0–11** (`AutoApprovedAllowlist = 11`, appended
  because voice mode must keep running the four allowlisted tools and no existing value said why) and
  `ToolClass` runs **0–8** (`Ingest = 8` — `ingest` is in `BuiltInPluginDefaults`, so the classifier must map it
  or the *exact* scheduled-research-as-external bug recurs the day ingest starts gating; it is deliberately
  absent from the preset). Both specs said 10 and 7. Corrected in place, and Batch 03's theory was pointed at
  `Enum.GetValues<ToolGateDecision>()` rather than a literal range so a thirteenth member cannot be missed the
  way the eleventh was. These are **append-only from `Unknown = 0`** and now mechanized by a golden name→ordinal
  map, not just a shape check.
- **Manual smoke debt, none of it automatable — seven items.** (1) The settings CheckBox's `Binding` path:
  nothing parses `Views/SettingsViews/AssistantView.xaml` and no test constructs `AssistantSettingsViewModel`,
  so a typo renders a toggle that silently never persists — toggle it, restart, confirm it stuck. (2) A real
  interactive `Planned` run with the setting **on**: a covered write must show a **pre-resolved accepted** card
  (never *nothing* — silence would mean the card-before-execute ordering was dropped) while `delete_file` must
  still show a live Decline/Allow-once pair with **no** Always-allow. (3) The `scheduled-research` card: titled
  “Create Scheduled job”, **two** buttons not three, detail rows as label/value pairs. (4) Park → flip the
  setting → Continue: the resumed run must still card every write. The mechanism is pinned by test; only a live
  round proves envelope → reader → `Initialize` → gate end to end. (5) Voice mode with the setting off (the
  removal above). (6) **DE/FR without clipping — now MORE relevant, not less**, because the fix pass
  *lengthened* the longest agent-settings string in all three locales; the German label in particular is very
  long. `LocalizationTests` proves key parity only, which passes either way. (7) A real MCP server: nothing in
  the suite routes through a live `McpPluginToolHandler`, so `ToolClass.External` is **only ever faked** —
  confirm an external tool still prompts with the full triad and that Always-allow still persists. **And the
  most valuable single check:** with a server exposing a tool named exactly `create_todo`, confirm voice mode
  now **refuses** it. That was a real must-fix, and its whole chain is faked in tests.

### Opened by Batch 03 (2026-07-30) — known, reasoned, not closed

- **The trace is DEVICE-LOCAL, and the product does not say so.** `03`'s §0.4 and §12.1 both instruct this file
  to record it, so: `AgentTimelineEvents` never crosses the sync wire. There is no `SyncAgentRun` DTO and runs
  do not replicate, so a user with a desktop and a laptop who opens the same synced chat on both sees a
  complete-looking "Tool activity" trace on one machine and an empty one on the other, with nothing in the UI
  distinguishing "this device recorded nothing" from "nothing happened". Closing it needs that DTO **plus** a
  merge policy for `Seq` across devices — the sequence is allocated per device, so two devices' rows for one run
  would collide on the value the reader orders by. **Not attempted**: a per-device audit trail that is honest
  about being per-device is better than a merged one whose ordering is invented.
- **The trace covers the two RUN gates, not every tool call in the app** (`03` D5 asks for this to be stated).
  Voice mode emits **nothing** — a voice turn has no run, so there is no `RunId` to attach a row to and the
  enforced FK would reject one; `ToolGateDecision.AutoApprovedAllowlist` is therefore in the vocabulary test's
  `NotEmittedByDesign` set. Reads emit nothing either (no decision, unbounded count, and the only interesting
  thing about a read is its target, which this table must not store). And three handlers
  (`Todo`/`Reminder`/`ScheduledJob`) convert a pending action into an immediate result on their `TargetId`-null
  error paths, so such a call executes upstream of any gate and produces no row. An audit trail that quietly
  omitted a write path would overstate itself, which is why this is written down rather than left implicit.
- **`AgentRuns` / `AgentSteps` for `Planned` runs are still retained forever.** This batch bounds only the
  newest table (a 500-row per-run cap plus a retention prune on each row's own `CreatedAt`). The chat-eviction
  path that could reach the other two deliberately exempts chats bearing a `Planned` run — precisely the runs a
  timeline is for — so the two older tables have no bound at all. Unchanged by design: giving them one is a
  retention decision about a user's own run history, not a fix.
- **Manual smoke debt, none of it automatable — five items.** (1) **A real headless run's trace across a
  restart**, which is the one that matters most: launch a background run, let it park at its budget, **quit and
  relaunch**, click Continue, expand — rows from both segments present, in order, with no duplicate `Seq`. That
  is the only live proof of the cross-process seeding; the failed-seed regression test covers it in-process
  only. (2) A real MCP server, for the same reason as Batch 04 — `ToolClass.External` is only ever faked.
  (3) The panel must not stutter during a run with many tool calls (the lock split is reasoned, not measured).
  (4) The prune actually runs: set the retention to 1 day, hand-age a row, confirm the `Information` line reports
  a non-zero delete. (5) DE/FR without clipping in the narrow decision column — **narrower now**, since the fix
  pass grew the row from three columns to five, and the German decision label is the long one.
- **DISCOVERED, and it belongs to Batch 12: `WpfStaHost` does not tolerate another test.** The host is one
  process-wide STA thread whose `Dispatcher.Run()` is *re-entered* when an exception escapes a queued operation
  (its own comment says so), while every test drives it through `Dispatcher.PushFrame`. Adding an eighth
  frame-pushing test to the `WpfApplicationStatic` collection took the full gate from **0/3 to 2/3 failing**;
  a clean teardown in the new test only got it to 1/3. The signature is always identical — a **60 s timeout
  inside `Pump()`**, i.e. the queue never reaching `SystemIdle` — and the victim is whichever test pumps next,
  most often one of `UiDispatcherServiceTests`' two deliberately-throwing facts. **It is not caused by the new
  test:** with that test SKIPPED the collection still failed 1 run in 3, on a fact the skipped test cannot
  influence. Batch 03 therefore **withdrew** its row-render fact rather than ship a 2-in-3 red gate (see
  `03-audit-timeline.impl.md` §9.1), which is why that manual-smoke item is still open. The file's own header
  already warned that a hang would make `Dispatcher.Run()` the first suspect; this is that warning coming true.
  Fixing it — one frame per test, or a host that does not re-enter `Run()` — unblocks both the withdrawn fact and
  any future `View` test.
- **Nothing measures UI-thread blocking, and no test can go red for it.** The store's two-lock split closes the
  mechanism by which a steady-state emit could stall the message pump, and the first emit of each run is now
  documented as the exception it always was (one indexed aggregate, on the caller's thread). But "Emit is cheap
  on the UI thread" remains *argued*, not measured. The manual smoke round owns it. The first-touch seed was
  **deliberately not moved** onto the writer thread, twice proposed and twice declined for one reason: seeding
  on the writer means allocating `Seq` before knowing where a parked segment stopped, which is the single
  correctness property the seed exists for and which ~15 emit-then-observe facts sit on. What changed instead is
  that the two contract statements which *claimed* the split removed the exposure were corrected, and the bound
  is now stated. One consequence to know: a permanently broken store retries the aggregate once **per emit**
  rather than once per run — each retry is one indexed query that fails fast, and the alternative reintroduces
  duplicate `Seq`. Cap the retries if it ever matters.
- **A PREMISE WAS DISPROVED, and it was the batch's own.** Both the build pass's open item and the spec's §9.1
  asserted that **nothing parses `RunProgressPanel.xaml`**. False: `AssistantView.xaml:50` places the panel as a
  **plain element** inside a `StackPanel` with no `Template` ancestor, so `AssistantView.InitializeComponent()`
  constructs it and runs its own `InitializeComponent()` — the Expander's non-deferred markup, its header and its
  `TextBlock`s have been parsed by the existing `AssistantViewParseTests` since the day they landed. The second
  half fell too: the shipped row `DataTemplate` contains **no `StaticResource` at all** (only `DynamicResource`,
  which yields `null` rather than throwing), so the smoke item's stated failure mechanism has no instance in it.
  Both are annotated in the spec in place. **The residue is real, though:** the row template is still deferred,
  so the five row binding paths and the `loc:Str` header (bound to `Header`, invisible to a logical walk) remain
  uncovered — and the fact written to cover them was **withdrawn** rather than shipped, because it raised the
  full gate from 0/3 to 2/3 failing via the `WpfStaHost` defect above. Re-land it once that host is fixed.
- **`Round` is not recorded, so the trace cannot say "these three calls were in the same round."** Recoverable
  later only by touching the tool-handler delegate signature — six closures, five with nothing to emit — which is
  why the decision is written down here rather than left implicit for someone to rediscover as a gap.
- **A tool call in flight when the process dies leaves NO row**, by the one-row-after-the-outcome design.
  Accepted rather than fixed: the run dies with it and the startup crash sweep settles it `Cancelled`, and a
  half-written row claiming "approved" for a call whose effect is unknown would be **worse than no row** on an
  audit surface. A two-row (intent + outcome) design would close it at double the rows and double the cap
  pressure.
- **Two spec statements were wrong and the code was right; both are annotated rather than silently edited.**
  §8.2 asked for an "exact expected 15-name set" over a DDL that prescribes **16** columns — the spec simply
  miscounted, and the shipped test asserts the real 16. §8.3 said to extend `ChatSessionStateMachineTests`; that
  suite drives `RunTurnAsync`, which takes no turn spec, so **no sink can reach it** — the same wall Batch 04 hit
  for the policy, and the reason all of that suite still passes unmodified. Both now carry a “CODE RIGHT, SPEC
  WRONG” note so the next reader does not “fix” a correct test to match stale prose.

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
- **Manual-smoke debt, and the toggle is XAML. Still open after Batch 12 — the premise moved, the conclusion did
  not.** The CheckBox's two `Binding` paths (`AgentPlanReasoningTurnEnabled`) and the `AssistantView.xaml`
  relocation in `d3c8c61` resolve only at runtime, so a typo renders a checkbox that silently never persists.
  This bullet used to justify that with "**no test in this suite parses a `View`**", and **that sentence is
  false as of the merge** — `AssistantViewParseTests` parses one. It does not help *here*, because the two files
  share a name and nothing else: Batch 12 parses `Pia.Views.AssistantView` (`Views/AssistantView.xaml`, the
  **chat** view), while this CheckBox lives in `Pia.Views.SettingsViews.AssistantView`
  (`Views/SettingsViews/AssistantView.xaml`, the **settings** view) — a different type in a different namespace,
  never constructed by any test. Batch 12 did make closing this cheap rather than blocking: per its own callout
  the next view test is a ~20-line file reusing `WpfStaHost`. Until someone writes that one, the debt stands.
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

**The 03 and 04 verdicts have now been tested by building both, and both held** (2026-07-30). Batch 03 shipped a
queryable per-run decision table with a 500-row cap and a retention prune — not spans — and 04's per-run policy
is a class list evaluated *underneath* a structural destructive floor, which is precisely the boundary the
Harness's own shell docs decline to be ("a UX pre-filter, not a security boundary"). Neither batch imported
anything. Recorded so the assessment reads as confirmed rather than merely asserted.

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

### Measuring the architecture rules without a Windows runner (learned in Batch 12 — reusable, use it)

If you are working where `dotnet test` cannot run (macOS: it fails with *"To install missing framework …
`Microsoft.WindowsDesktop.App` … osx-arm64"*, **0 tests executed**), you can still **execute** most of this
repo's architecture rules rather than reasoning about them. **`NetArchTest.Rules` 1.3.2 targets
`netstandard2.0`, is built on `Mono.Cecil` (pure metadata analysis, no runtime loading), and exposes
`Types.FromFile(string)`.** So a throwaway **`net10.0`** console project — which *does* run — referencing
`NetArchTest.Rules` 1.3.2 + `Mono.Cecil` 0.11.5 can be pointed at the built
`src/Pia.Wpf/bin/Debug/net10.0-windows*/Pia.Wpf.dll` and run the real rule bodies verbatim, with the selection
chain copied character for character out of the test file and only `InAssembly(...)` swapped for
`FromFile(...)`. Build first with `--no-incremental` so the DLL is fresh.

That works for `DependencyInjectionTests`, `NamingConventionTests`, `LayerDependencyTests`, `MvvmPatternTests`
and `AsyncSafetyTests`. Three things to know before you trust the output, all learned the hard way:

- **A rule over an EMPTY type set returns `IsSuccessful = true`.** Any `HaveName(...)`-scoped fact needs a
  non-vacuity guard (`Assert.Single(target)`) or a rename silently turns it green — and every probe run should
  carry a control expression (e.g. `ShouldNot().HaveDependencyOn("System.Object")`) proving the selection
  resolved at all.
- **Rules that end in `.GetTypes()` and then LINQ over reflection `Type` objects cannot be run as written** —
  reflection over `Pia.Wpf` outside the Windows test host throws `FileNotFoundException`. Either run the
  NetArchTest half of the selection and **invert** the assertion so `FailingTypeNames` enumerates the scanned
  set, or re-implement the predicate over Cecil (`IsInitOnly` for the readonly-field rule,
  `AsyncStateMachineAttribute` + `void` for `async void`). Say which one you did.
- **`DiRegistrationTests` and `BootstrapperGraphValidationTests` cannot be measured at all** — they *invoke*
  `Bootstrapper.ConfigureServices` by reflection. Verify by inspection and label it as inspection.

Cecil also answers questions the rules only hint at: dumping a type's **complete** `System.Windows` dependency
set (member signatures *and* IL operands) is what caught an exemption comment naming one of two real roots.
Batch 12 §1 has the worked example.

**Standing guardrails (every batch):** failure-isolated bookkeeping (Safe* wrappers); no interactive regression
(the Live terminal settle stays correct); executor parity (Live + Headless); off-thread `RunChanged` stays
marshaled (G3); privacy-first logging (user content → `SensitiveDebug`, Flow Title/Body generic); append-only
persisted enums/ordinals; a new user-visible string lands in `ViewStrings.resx` **and** `.de.resx` **and**
`.fr.resx`. See CLAUDE.md + plan §12.5/§13.10/§16.
