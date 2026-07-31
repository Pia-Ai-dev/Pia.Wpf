# Phase 3 — Implementation Plan & Workflow Design

**Status: PLANNED, not started.** Authored 2026-07-30 against `53cd552` on `feature/agent-run-spine`
(49 commits ahead of `origin`, unpushed by owner decision). Phase 3 = **[Batch 06](06-run-workspace-isolation.md)**
(run workspace isolation) + **[Batch 07](07-subagents-multipersona.md)** (sub-agents / multi-persona).
Roadmap context: [`00-OVERVIEW.md`](00-OVERVIEW.md). Authoritative design:
[`../2026-07-18-agent-system-phase1-plan.md`](../2026-07-18-agent-system-phase1-plan.md).

This file is the **input to the workflow**, not its output. The workflow's own detail-planning phase writes
`06-run-workspace-isolation.impl.md` and `07-subagents-multipersona.impl.md`; this file exists so that phase
starts from measured facts and settled decisions instead of re-deriving them. Everything in §2–§4 was
**measured against the tree**, not read out of the batch specs — see §3 for the nine places those specs are
wrong.

---

## §0 What this is not

Phase 3 does **not** retire Rank 1. The manual Windows smoke round still outranks it, and Phase 3
**lengthens that list** rather than shortening it: 06 changes where every unattended run's files land (a
user-visible relocation), 07's per-step avatars land inside a deferred `ItemTemplate` no test materializes, and
both new UI affordances (publish-on-failure, the persona roster) are XAML that nothing parses. §8 enumerates
what Phase 3 adds. Read that section before reading Phase 3 as progress against the top of the queue.

---

## §1 Scope — the nine decisions, resolved

Owner-resolved 2026-07-30 after the grounding pass. The **consequence** column is the part that matters: three
of these answers each add a work group that the batch specs never anticipated.

| # | Decision | Answer | Consequence |
|---|----------|--------|-------------|
| **D1** | Phase 3 scope | **Both batches in full** | Child runs are in scope, so D7 is live and a new persisted run state must be invented. ~10 commit groups, not the 5 Batch 06 alone would need. |
| **D2** | Where the run workspace lives | **Keep `%LOCALAPPDATA%\Pia\runs`, carve it out of the guard** | `SensitivePathGuard.BuildAllowedExceptions` grows from one island to two, behind a shared constant so guard and launcher cannot drift. Promotion is copy+delete, not an atomic rename. |
| **D3** | Which runs promote | **Completed auto, else offer to publish** | Net-new UI affordance + three resx files + a retention rule so an unanswered offer cannot pin a workspace forever. |
| **D4** | Do interactive runs isolate | **Isolate both** | Net-new directory lifecycle on the interactive path (today it is a bare `CreateAsync`), and the open-file chips now point at a moving target — hence the chip decision below. |
| **D5** | Git inside an isolated run | **Worktree when the root is a repo, else copy** | Two workspace provisioning modes. Git-tool parity comes free in worktree mode (the worktree *is* a repo) and needs the ambient read in copy mode. Teardown must `git worktree remove`/`prune`, not just `rmdir`. |
| **D5b** | What "promote" means for a worktree | **The branch is the deliverable** | No automatic merge, so no conflict handling in an unattended path. The publish affordance from D3 becomes "review / merge this branch". The panel must say plainly that the output is on a branch, or the user asks "where is my file?". |
| **D6** | Who assigns personas to steps | **Planner picks from a roster** | A per-mode roster settings surface + loc keys ×3, a planner prompt/schema change, and a fallback for when the model names a persona outside the roster. |
| **D7** | How a child run executes | **Separate child slot pool** | The heaviest answer. Siblings run in parallel and the parent awaits them, so the parent must park in a state that survives the startup sweep — which no existing state can do (§3, correction 8). Forces a new **appended** run state plus sweep and resume logic, and doubles effective provider concurrency. |
| **D8** | How file chips survive promotion | **Resolve on open** | Chip opening falls back: if the recorded absolute path is missing and sits under the runs dir, resolve the same relative path under the assistant folder. No persisted chat content is rewritten — which keeps this out of Batch 10's write-arbitration territory. |

**One decision was NOT escalated and is recorded here as mine, so it can be overridden in one line.** Phase 3
adds **no new WPF `View` test**. `WpfStaHost` holds exactly 7 frame-pushing facts and the 8th previously took
the full gate from 0/3 to 2/3 failing — 1/3 even with the new test skipped — and the documented fix (one frame
per test, or a host that does not re-enter `Dispatcher.Run()`) is **not implemented**
(`00-OVERVIEW.md:1028`, `tests/Pia.Wpf.Tests/Views/WpfStaHost.cs:34`). The roadmap assigns that fix to Batch 12,
so Phase 3 covers its UI at the ViewModel level and books the XAML as manual-smoke debt, matching what Batch 03
already did when it withdrew its row-render fact. **If you want the host fixed as a prerequisite group instead,
say so** — it would unlock View coverage for the publish affordance, the roster surface and the avatar row at
once, and retire Batch 03's withdrawn fact. It is deliberately not in the group list below.

---

## §2 Seam map (measured, not quoted)

Every anchor below was read during the grounding pass. Where an anchor here disagrees with a batch spec, this
file is right and §3 says why.

### 06 — the file-tool base root

- **One dispatch point resolves the root for all five file tools.** `FilesToolHandler.cs:170-171`:
  `ambientRoot = TaskAmbient.Current?.WorkspaceRoot; baseRoot = ambientRoot is not null ? NormalizeWorkspaceRoot(ambientRoot) : _currentFolder`, then `root = ResolveEffectiveRoot(baseRoot, TaskAmbient.Current?.WorkingSubpath)` (`:182`). Reads and writes share it — **no read/write divergence to reconcile.**
- **The subpath layer is orthogonal.** `ResolveEffectiveRoot` takes `baseRoot` as a parameter and does not care
  which source produced it. 06 changes only which value lands in `baseRoot`; the narrowing needs no change.
- `TaskContext` is `readonly record struct(Guid? TaskId, string? WorkingSubpath, Action<FileTouch>? OnFileTouched, string? WorkspaceRoot)`
  on an `AsyncLocal` (`TaskAmbient.cs:34`) — per-turn isolation is already correct.
- Two deliberate non-ambient consumers **stay as they are**: `ListRelativeFiles` (`@Files` autocomplete,
  `:319`) and `ReadPromptPreviewAsync` (`:787`) run outside any turn and use `_currentFolder` by design.
- MCP/plugin-routed file calls wrap the same `IFilesToolHandler` singleton (`BuiltInPluginHandler.cs:159`), so
  they inherit 06 automatically — **no separate MCP work.**

### 06 — ship-blocker 1: the guard blocks the runs directory

- `SensitivePathGuard.BuildBlockedRoots()` blocks `%LOCALAPPDATA%\Pia` **wholesale** via
  `AddEnv("LOCALAPPDATA", "Pia")` (`:74`); `BuildAllowedExceptions()` carves out exactly one island —
  `AssistantWorkspace.LegacyWorkdir` (`%LOCALAPPDATA%\Pia\workdir`) — and nothing else (`:103`).
- `_runsBaseDir` defaults to `Path.Combine(LocalApplicationData, "Pia", "runs")` (`HeadlessRunLauncher.cs:107`)
  — inside the blocked root, not an allowed exception.
- `IsBlocked` runs **after** containment resolves, at six file-tool sites (`FilesToolHandler.cs:689`, `:803`,
  `:984`, `:1050`, `:1230`, `:1253`), and carve-outs are checked before the denylist
  (`SensitivePathGuard.cs:39-46`).
- **Therefore the naive change the spec describes cannot work**: `Initialize(workspaceRoot: runRoot)` makes
  every read/write/delete inside the run workspace pass containment and then get rejected by the guard. The
  carve-out must land **before** the flip. `runsBaseDirOverride` (`HeadlessRunLauncher.cs:97`) is how a test
  exercises the real shape without writing to the user's `LOCALAPPDATA`.

### 06 — ship-blocker 2: the verifier probes the wrong root

- The ambient is set **per step** and restored in the step's `finally` (`HeadlessTurnExecutor.cs:289-291` set,
  `:314-315` restore). The comment says why: *"Set here — not in `BeginRunAsync` — so the `AsyncLocal` is live
  inside THIS exchange's flow."*
- Verify runs from the orchestrator **outside** any step flow (`AgentRunOrchestrator.cs:211-212` → `SafeVerify`
  → `AgentVerifier.VerifyAsync`), so `TaskAmbient.Current` is null there and `AgentVerifier.cs:210`'s
  `ambientRoot ?? settings.AssistantFilesFolder` falls back to the settings folder.
- **So once steps write into the workspace, every declared `ExpectedArtifact` probes the wrong root**, reports
  missing, the verdict fails, the shared replan budget burns (`AgentRunOrchestrator.cs:216-225`), and the run
  terminates `Completed` + `"unverified"` — **on every single run.** This is 06's most likely silent failure.
- The fix shape is already precedented: `RunContext.WorkingSubpath` is a settable `{ get; set; }`
  (`RunContext.cs:58`) assigned in `BeginRunAsync` (`HeadlessTurnExecutor.cs:130`) for exactly this reason.
- Forced ordering, which also dissolves the "Completed but not yet promoted" crash window without touching the
  sweep: **drain steps → verify (against the run root) → promote → `CompleteAsync`.**

### 06 — workspace lifecycle

- Created at launch (`Path.Combine(_runsBaseDir, run.Id)` + `CreateDirectory` + canonicalize,
  `HeadlessRunLauncher.cs:164-166`) and **idempotently re-created on resume** (`:314-315`) — a budget-paused run
  resumes into the same workspace with no extra work.
- Workspace-setup failure already settles the run via `FailAsync` (`:169-174`), so it never dangles non-terminal.
- The sweep predicate is exactly `remove = run is null || Directory.GetLastWriteTimeUtc(dir) < UtcNow - 30d`
  (`:451`) — **zero** `AgentRunState` awareness, **zero** promotion awareness.
- `OnChatsChanged` (`:480-498`) deletes a run's workspace synchronously when its chat is deleted, but only for
  run ids **this session** launched (`_runsByChat` is in-memory, never reloaded from the DB).
- `FailInterruptedRunsAsync` is one bulk `UPDATE AgentRuns SET State=Cancelled WHERE State < WaitingForInput`
  (`AgentRunService.cs:357-360`) — no disk touch, no per-row `RunChanged`, no `ParentRunId` awareness.

### 06 — interactive runs have no workspace at all

- The interactive `Planned` run is a bare `_agentRunService.CreateAsync(...)` (`ChatSessionManager.cs:772`). No
  directory is created anywhere on that path, so **D4 is net-new work, not a flag flip.**
- The interactive per-step `TaskContext` carries a file-touch sink that builds `FileRef(touch.AbsolutePath, …)`
  chips into the assistant message (`ChatSession.cs:663`) — the reason D8 exists.
- A headless run creates its own stub chat with no `WorkingDirectory` (`HeadlessRunLauncher.cs:128-138`) and
  `BeginRunAsync` deliberately sets `ctx.WorkingSubpath = null` (`HeadlessTurnExecutor.cs:130`). **Settled:**
  the promotion target is the `AssistantFilesFolder` root, preserving today's destination byte-for-byte;
  relative paths inside the run are unchanged.

### 06 — git is a second, independent root consumer

- `GitToolHandler` carries its own copy of the resolution pattern: `baseRoot = _currentFolder` (`:138`), reads
  `TaskAmbient.Current?.WorkingSubpath` for narrowing (`:148`), its own `ResolveEffectiveRoot` (`:675`) — and
  **never reads `WorkspaceRoot`.**
- It already injects `IGitProcessRunner` with an `IsGitInstalled` gate (`:46`, `:74`) and already runs
  `git rev-parse --show-toplevel` on **every** call as its is-repo check (`:532`), with a ceiling directory for
  containment (`:673`). So "is this root a repo?" is a question the code already answers — which is what makes
  D5's worktree mode cheap. There is **no `git worktree` in the agent tool surface** (`:150-160`): provisioning
  is app-side, so D5 adds no new agent capability.
- Today files and git agree. **06 creates the incoherence** — without parity the agent writes into the
  workspace and commits the interactive folder's stale tree.

### 07 — persona and provider are fixed once per run

- `RunAsync(AgentRun, IAgentTurnExecutor, Persona, AiProvider, RunProfile, CancellationToken, bool resume)`
  (`AgentRunOrchestrator.cs:35`) fixes one `(Persona, AiProvider)` pair for the whole run and threads the same
  objects into `PlanAsync`/`ReplanAsync`/`VerifyAsync`.
- `HeadlessTurnExecutor` resolves `_provider` **once** in `BeginRunAsync` (`:139-154`, honouring
  `_providerOverride` → `persona.PreferredProviderId` → mode default) and reuses the field at every step
  (`:299`).
- **`IAiClientService` is already provider-per-call and needs no change.** The only two fixed points are the
  orchestrator signature and that cached field.
- `AgentPlanner.BuildSteps` hardcodes `AssignedPersonaId = null` (`:295`) at the only step-construction site.
  `IPersonaService.ResolveActiveAsync(WindowMode, UserOperatingMode)` takes no run/step/chat id, and the in-chat
  picker writes the same global per-mode setting (`AssistantViewModel.cs:547`).
- Personas already carry `PreferredProviderId` + `ReasoningEffort`, and the executor already clones the provider
  to apply the effort (`:151-153`) — **that clone-per-persona logic is reusable per step verbatim.**

### 07 — persistence and the state machine

- `ParentRunId` and `AssignedPersonaId` are **real columns**, present since `1ceb9a40` and fully round-tripped
  (`AgentRunService.cs:108-123`/`:454-467` insert, `:599`/`:622` read). **No migration needed.** But there is no
  FK, no index, and no non-null value is ever written.
- `AgentRunCreateRequest` (`IAgentRunService.cs:16-23`) has **no `ParentRunId` parameter** — the producer side is
  absent, not merely unwired.
- `SetStateAsync` is an unconditional blind `UPDATE` (`:146-163`). The **only** CAS in the service is
  `TryBeginResumeAsync` (`WHERE Id=@Id AND State=@Expected`, `:309-333`), and it unconditionally sets
  `ExtraJson=NULL` on the claim (`:321`).
- **No existing state can hold a parent waiting on children** — the single hardest fact in Phase 3.
  `Planning`/`Running`/`Verifying` are swept to `Cancelled` at every startup (`State < WaitingForInput`,
  `:357-360`); `WaitingForInput`'s claim CAS destroys `ExtraJson`, so a "waiting on N children" marker cannot
  survive a resume trigger; and `Paused(4)` is **reserved for Batch 08** live-steering
  (`08-live-steering.md:12`, `RunProgressViewModel.cs:225`).
- `AgentRuns`/`AgentSteps`/`AgentTimelineEvents` are **local-only** — zero references in `src/Pia.Shared`, no
  sync DTO. Nothing here crosses the wire.

### 07 — concurrency

- `_slots = new SemaphoreSlim(2, 2)` on a singleton launcher (`HeadlessRunLauncher.cs:26`), waited inside the
  dispatch `Task.Run` before the DI scope and orchestrator are built (`:199` launch, `:333` resume), released in
  the `finally` after `orchestrator.RunAsync` returns.
- **A nested acquire on the same pool deadlocks**: two parents each hold 1 of 2 slots while blocked on a child
  that needs a slot from the same pool, and neither can release. D7's separate pool is not a preference — it is
  the only shape that works if a parent awaits a child through the launcher.
- `ScheduledJobBackgroundService` holds `_runLock` (`SemaphoreSlim(1,1)`, shared with `ExecuteResearchAsync`)
  across `await handle.Completion` (`:166` → `:202`), so scheduled jobs of both kinds are **already** strictly
  serialized. Nested child work extends that hold by every descendant's wall clock.
- `ExecutingRunStore` is a reverse map runId → chatId (`:6-11`), so multiple concurrent runs on **one** chat
  already work with zero change — a child sharing the parent's `chatId` needs no store change.
- `AgentRunOrchestrator.RunAsync` already creates a linked CTS from the caller's token (`:46`).

### 07 — ledger, timeline, roll-up

- Ledger shape post-`CostUsd`-removal: `{InputTokens, OutputTokens, WallClockMs, ActiveMs?, SegmentStartedAt?, PerStep[]}`.
  `WriteLedger` (`AgentRunService.cs:778-786`) is the single writer; all three call sites run under `_gate`.
- `AddUsageAsync` only ever touches the run named by `runId` — **there is no cross-run method.** Parent roll-up
  is either a push (a second `AddUsageAsync` per child write) or aggregate-on-read via `WHERE ParentRunId=@p`,
  which is a full table scan today.
- **Two distinct budget concepts already coexist** and any nesting design must say which one nests: an
  ephemeral per-dispatch `RunContext` (steps + wall clock, deliberately reset on every resume,
  `RunContext.cs:89-92`) that gates pausing, and a persisted ledger that accrues forever.
- Timeline `Seq` is monotonic only within a `RunId`, capped at 500 real rows per run
  (`AgentTimelineService.cs:60`) with a synthetic `TraceTruncated` marker after. A child gets its own fresh
  `Seq` space **and** its own 500 budget.
- **There is no total order across runs.** `CreatedAt` is explicitly rejected as an ordering source
  (`SqliteContext.cs:342-343`, ~1 ms resolution against sub-ms tool calls). An interleaved parent+child timeline
  cannot be sorted without new plumbing — **do not promise one.**

### 07 — panel attribution

- `StepRowViewModel.AssignedPersonaId` exists (`{ get; init; }`, `RunProgressViewModel.cs:495`), is populated in
  `From(AgentStep)` (`:514`), and `RunProgressPanel.xaml:66-68` already binds it to `PiaPersonaAvatar`. The read
  side is genuinely pre-existing.
- **Two latent defects sit under that binding today**: `AssignedPersonaId` is `Guid?` while `PersonaIdProperty`
  is `typeof(Guid)` (default `Guid.Empty`), and `Emoji` is never bound — so **every step row already draws an
  empty 20×20 shadowed box.** 07 widens a seam that is already cosmetically broken.
- `PiaPersonaAvatar`/`PersonaGlyph` have only `PersonaId` + `Emoji` DPs. **There is no `AccentColor` path** in
  either control or in `StepRowViewModel` — accent differentiation is net-new.
- `RunProgressViewModel` is hand-constructed **positionally, outside DI** (`AssistantViewModel.cs:397`), takes
  no `IPersonaService` and no `IUiDispatcher` (it captures a raw `SynchronizationContext` at `:176`). A persona
  lookup needs a new **tail** ctor param with a default, the way `IAgentTimelineService` was added.
- `AssignedPersonaId` is `init`-only and `SyncSteps` only mutates `Status` (`:431`) — fine today because the
  planner mints fresh step ids per replan, so rows are replaced, never mutated in place. **A panel-side
  assignment surface would break that**, which is one reason D6 chose planner-side.

### Guardrails that will go red if Phase 3 is careless

- **`ToolAutonomy.Resolve` is provably path-independent**: `ToolGateInput` has no path/root field and
  `ToolClassifier` switches on plugin **name** only. 06 changes *where* `write_file` lands, never *whether* it
  is gated — **no gate work in 06.**
- `AgentRunBracketTests` (`:38`) scans types assignable to `IHeadlessRunLauncher` or
  `IBackgroundAssistantTurnRunner`, asserts ≥ 2 exist, and asserts each injects `IExecutingRunStore`. A third
  executor type in 07 must satisfy that or grow the `ExecutorContracts` array.
- `ToolAutonomyRuleTests` (`:34`) pins the **exact count** of `ToolAutonomy.Resolve` / `IsMcpTool` /
  `IsAutoApproveEligible` calls per gate file (1 each in `ChatSession.cs`,
  `BackgroundAssistantTurnRunner.cs`, `AssistantViewModel.cs`). A child-run gate adding a second `Resolve` call
  fails that theory row.
- `NamingConventionTests`' `allowedSuffixes` (`:32-36`) does **not** contain `Promoter`. Name the promotion type
  `…Service`/`…Handler`/`…Store`. Do not grow the allowlist for one type.
- `FilesToolHandlerWorkspaceEscapeTests` roots `_runRoot` under `Path.GetTempPath()` (`:137-141`), which is
  outside every blocked root — **the existing regression suite structurally cannot see the guard collision.**

---

## §3 Where the batch specs are wrong

Nine corrections, all measured. The batch specs were written 2026-07-29 and describe code "as built"; this
repo has a recorded history of logged hazards turning out to be premise errors, so the detail-planning phase
must treat spec prose as a hypothesis.

1. **06's anchors drifted.** `Initialize` is at `HeadlessTurnExecutor.cs:104-116`, not `:91`; the two
   `workspaceRoot: null` call sites are `HeadlessRunLauncher.cs:209` (launch) and `:339` (resume), not `:181`
   and `:289` — `:181` is a CTS construction and `:289` is a grant-envelope restore. The substance (both pass
   null) is right. *Cosmetic — but do not let a builder grep `:181`, find a CTS, and conclude the spec is wrong
   about the whole batch.*
2. **06's "Key seams" omits both ship-blockers** — the guard carve-out and the verifier root. Consequence: the
   batch is **five** groups, not two, and the `Initialize` flip is the **third**. Doing the flip first produces
   a run where every file tool errors; doing it without the verifier fix produces a run that always ends
   `Completed + unverified`.
3. **"Backward-compat with the Milestone-B ephemeral-scratch behavior" is not a decision** — there is no
   behaviour to be compatible with. The directory is created and swept, but nothing has ever written a file-tool
   operation into it, and a repo-wide grep for promotion logic returns zero hits. *Dropped from the decision
   list; it would have cost a slot.*
4. **06's own recommendation on interactive runs should have been reversed.** The spec says "recommend isolate
   both for uniformity"; the code says interactive runs create no directory at all and their chips are built
   from the absolute path. The owner chose to isolate both anyway (D4) — **with eyes open**, which is why D8
   exists.
5. **06's guardrail sentence is false as written.** "A crashed/cancelled run's un-promoted workspace is cleaned
   by the startup sweep" — the sweep's only predicates are `run is null` and a 30-day age
   (`HeadlessRunLauncher.cs:451`). `Cancelled` is not a deletion trigger, so a crashed run's workspace persists
   up to 30 days. Sequencing promotion before `CompleteAsync` removes the *need* for a promotion-aware sweep but
   does not make the sentence true.
6. **07 understates the `ParentRunId` gap.** The columns round-trip, but `AgentRunCreateRequest` has no
   `ParentRunId` parameter, so **no code path can create a child at all**. Adding it changes `IAgentRunService`,
   which breaks two hand-written 16-member fakes (`AgentRunOrchestratorTests.cs:142`,
   `BackgroundAssistantTurnRunnerRunSpineTests.cs:290`). "Wire the orchestrator" is not the shape of the work.
7. **"Reuse the headless slot semaphore" deadlocks** — see §2. Never offered as an option; D7 chose a separate
   pool.
8. **"The startup crash sweep must handle parent/child correctly" is not a sweep tweak.** No existing state can
   represent a parent waiting on children (§2). With D7 = separate pool, Phase 3 **must append a new persisted
   run state**, and `Paused(4)` is taken by Batch 08.
9. **07's "attribution is already seamed" is true but inert and already defective** — the `Guid?`/`Guid` DP
   mismatch and the unbound `Emoji` mean every step row renders an empty avatar today. The UI half of 07 is
   "fix two pre-existing defects and add a service dependency to a positionally hand-constructed VM", and it
   cannot be covered by a View test.

---

## §4 Risk register

Ordered by how quietly each one fails. Every builder prompt carries the risks touching its own files.

| # | Risk | Anchor | Mitigation |
|---|------|--------|------------|
| R1 | **A broken 06 can ship green.** The existing workspace-escape suite roots its fixture under `GetTempPath()`, outside every blocked root, so the guard collision has never been exercised. | `FilesToolHandlerWorkspaceEscapeTests.cs:137` | Any 06 test must root at the **real** shape (`LocalApplicationData\Pia\runs\<guid>`) or drive the launcher through `runsBaseDirOverride`, and must assert a **successful write**, not only that escapes are rejected. |
| R2 | The verifier silently probes the wrong root once steps write into the workspace → every run ends `Completed + unverified` and burns the replan budget first. | `AgentVerifier.cs:210` | Add `RunContext.WorkspaceRoot`, assign it in `BeginRunAsync`, have `TryBuildArtifactFactsAsync` prefer ctx over the ambient. **Land before the flip**, with a test asserting a workspace-written artifact is found. |
| R3 | `AgentVerifier`'s doc comment asserts "`WorkspaceRoot` is null in production and the settings folder IS the root the step writes landed in". 06 falsifies it. | `AgentVerifier.cs:262` | Update the comment in the same commit that carries the root. These ownership comments are load-bearing in this repo. |
| R4 | `OnChatsChanged` deletes a run's workspace synchronously on chat deletion. Today that directory is empty; after 06 it is the **only copy** of un-promoted work. | `HeadlessRunLauncher.cs:480` | Promote before `CompleteAsync` so a completed run has nothing left; for a non-terminal run, cancel first rather than deleting under a live writer. |
| R5 | The sweep has no state and no promotion awareness, so a crashed run's workspace lingers up to 30 days — and in worktree mode leaves a **stale worktree registration**, not just a directory. | `HeadlessRunLauncher.cs:451` | Teardown goes through the provisioner (`git worktree remove`/`prune` in worktree mode); either accept the 30-day retention explicitly or add a state-aware predicate. |
| R6 | Naming the promotion type `RunWorkspacePromoter` fails an architecture test. | `NamingConventionTests.cs:32` | Use an allowlisted suffix. |
| R7 | **There is no DEBUG-erased Error-severity log helper.** `SafeLog` exposes `SensitiveTrace/Debug/Information/Warning` only. An author reaching for `LogError` with a path leaks user-named content into a support-attachable release log — and 06 logs a *lot* of paths. | `src/Pia.Wpf/Logging/SafeLog.cs` | Route path/filename interpolation through `SensitiveWarning` (the highest DEBUG-erased severity) or a scoped `#if DEBUG`. Keep `Information`-and-above to counts, booleans and ids: `"promoted {Count} files"`, never `"{Path}"`. `SafeUrl` does not apply — it is scheme+host shaped. |
| R8 | Adding `ParentRunId` to `AgentRunCreateRequest` breaks two hand-written full-surface fakes — a **compile failure** in the test project, not a soft skip. | `AgentRunOrchestratorTests.cs:142` | Budget the fake migration into the same commit; prefer an optional trailing parameter so unrelated call sites are untouched. |
| R9 | A dedicated sub-agent runner type trips the bracket-ownership rule (≥2 executors, each injecting `IExecutingRunStore`). | `AgentRunBracketTests.cs:38` | Prefer executing children through the existing launcher/executor. If a new type is unavoidable, implement one of the two contracts and Register/Release with the **child's** run id. |
| R10 | A delegated-run UI action adding a second `ToolAutonomy.Resolve` call to any gate file fails the exact-count theory. | `ToolAutonomyRuleTests.cs:34` | Route child tool calls through the existing unattended gate. If a new `ToolGateSurface` value is genuinely needed, **append** it and update the golden name→ordinal map plus the emitted/not-emitted classification in the same commit. |
| R11 | **No new View test is available.** 07's avatar work lands entirely inside a deferred `ItemsControl.ItemTemplate` the existing parse tests never materialize. | `WpfStaHost.cs:34` | Cover at ViewModel level (`StepRowViewModel.From` + a persona-lookup unit test); rely on the static loc-key scan for resource typos. **Do not** add a frame-pushing test to the `WpfApplicationStatic` collection. |
| R12 | `RunProgressViewModel` is hand-constructed positionally in production and in tests; its own ctor comment flags this as a break-everything-silently-until-compile hazard. | `RunProgressViewModel.cs:165` | New param at the **tail** with a null default; update the single production call site (`AssistantViewModel.cs:397`). Do not introduce `System.Windows` references while in there — the ViewModel ratchet exempts only `AssistantViewModel`. |
| R13 | **A child run inherits none of the parent's grant envelope.** `AgentRunCreateRequest` takes an opaque `PolicyJson` string the service never parses, and the envelope helpers are internal to `HeadlessRunLauncher`. A naive child-spawn creates a NULL policy, which the resume floor then widens to the `{write_file}` default. | `HeadlessRunLauncher.cs:682` | Expose a narrow-for-child helper that re-serializes a **subset** of the parent's grants, plus a test asserting a child's envelope is never wider than its parent's. Never let a child fall through to the default. |
| R14 | 07 **cannot** deliver an interleaved parent+child timeline: `Seq` is per-run, `CreatedAt` is explicitly rejected as an ordering source, and each child gets its own 500-event cap. | `SqliteContext.cs:342` | Scope to per-run views with a parent→child drill-down. A merged view needs a new cross-run ordering key designed as its own work. |
| R15 | Child work nested in a scheduled job extends a process-wide head-of-line block: `_runLock` is held from before `LaunchAsync` across `await handle.Completion`, so no scheduled job of either kind can dispatch for the parent's wall clock **plus every descendant's**. | `ScheduledJobBackgroundService.cs:202` | Factor into the budget defaults — a delegating run must fit inside the envelope one scheduled job may occupy. Do not add nested delegation to scheduled jobs without revisiting this lock. |
| R16 | **Worktree mode mutates the user's repo** (`.git/worktrees/<id>` + a branch ref) even though the working tree is untouched, and a worktree starts from a **commit** — uncommitted and untracked files are invisible to the run. | new work (D5) | Provisioner owns create *and* teardown symmetrically; gate on `IsGitInstalled` and on `rev-parse --show-toplevel` succeeding, and **degrade to copy mode on any fault** rather than failing the run. The invisible-uncommitted-work behaviour is a release-note item, not a bug. |

---

## §5 Work groups

Ten groups, each one commit-shaped and independently green. **Strictly sequential** — both batches touch
`AgentRunOrchestrator`, `HeadlessRunLauncher` and `HeadlessTurnExecutor`, so parallel builders would collide
(and going parallel would force worktree isolation per agent, which then owes a commit-and-remove step). The
model column is the intended builder tier.

| G | Group | Batch | Model | Content | Depends on |
|---|-------|-------|-------|---------|-----------|
| **G1** | Guard carve-out + verifier root | 06 | sonnet | Promote the runs base dir to a shared constant beside `AssistantWorkspace.LegacyWorkdir`; add it to `BuildAllowedExceptions`. Add `RunContext.WorkspaceRoot` (settable, symmetric with `WorkingSubpath`), assign in `BeginRunAsync`, have `AgentVerifier` prefer ctx over the ambient, correct the now-false comment (R3). **No behaviour change** — the root is still null. Tests at the real shape (R1). | — |
| **G2** | Flip both `Initialize` call sites | 06 | opus | Pass `runRoot` at `HeadlessRunLauncher.cs:209` and `:339`; rewrite the two doc comments describing null as the intended production value; re-root the escape tests. **First commit where behaviour changes.** | G1 |
| **G3** | Workspace provisioning: worktree \| copy | 06 | opus | New provisioner owning both modes (D5): `rev-parse --show-toplevel` + `IsGitInstalled` → `git worktree add` on `pia/run/<runId>`, else plain directory. Symmetric teardown (`worktree remove`/`prune`, R5/R16), degrade to copy on any fault. `GitToolHandler` reads the ambient `WorkspaceRoot` so git and files agree in both modes. | G2 |
| **G4** | Promotion + publish affordance | 06 | opus | Promotion service (allowlisted suffix, R6) invoked from the terminal-settle path **after verify, before `CompleteAsync`**. Copy mode: copy to the `AssistantFilesFolder` root. Worktree mode: **the branch is the deliverable** (D5b) — no merge; the panel says so. D3's publish-offer for failed/cancelled runs + loc keys ×3 + a retention rule. Counts/ids at `Information`, paths only via `SensitiveWarning` (R7). Register in `Bootstrapper` or `DiRegistrationTests` fails. | G3 |
| **G5** | Interactive isolation + chip resolution | 06 | opus | D4: directory lifecycle for interactive `Planned` runs (net-new — `ChatSessionManager.cs:772` is a bare `CreateAsync`). D8: chip opening falls back from a missing runs-dir path to the same relative path under the assistant folder. Tests for both phases (during the run, after promotion). | G4 |
| **G6** | Per-step persona + provider resolution | 07 | opus | `AgentPlanner` emits a real `AssignedPersonaId` per step from the roster (D6); orchestrator resolves `(Persona, AiProvider)` **per step** instead of closing over one run-level pair; `HeadlessTurnExecutor` stops caching `_provider` in `BeginRunAsync` and resolves per step, reusing the existing clone-for-`ReasoningEffort` logic verbatim. Falls back to the run persona when a step's id is null or unresolvable (out-of-roster included). Executor parity: the Live path too. | G5 |
| **G7** | Roster settings + panel attribution | 07 | sonnet | The per-mode roster surface + loc keys ×3. `IPersonaService` as a **tail** ctor param with a null default on `RunProgressViewModel` (R12); project emoji + accent onto `StepRowViewModel`; bind `Emoji` and fix the `Guid?`/`Guid` DP mismatch so the always-empty avatar box stops rendering (§3.9). VM-level coverage only (R11). | G6 |
| **G8** | A run state for a parent awaiting children | 07 | opus | **Appended** ordinal (never inserted; `Paused(4)` belongs to Batch 08). Must survive the startup sweep — today `State < WaitingForInput` sweeps to `Cancelled` — and must carry "waiting on N children" through a claim that unconditionally nulls `ExtraJson` (`AgentRunService.cs:321`). Sweep + resume + CAS. **Highest-risk group in Phase 3.** | G7 |
| **G9** | `ParentRunId` producer + child envelope | 07 | opus | `ParentRunId` on `AgentRunCreateRequest` as an optional trailing param; migrate **both** 16-member fakes in the same commit (R8); add `IX_AgentRuns_ParentRunId`; narrow-for-child grant-envelope helper with a test asserting a child is never wider than its parent (R13). | G8 |
| **G10** | Child slot pool + roll-up | 07 | opus | D7: a separate `SemaphoreSlim` so siblings run in parallel while the parent awaits (the shared pool deadlocks, §3.7). Cascade cancellation via the existing linked CTS; no orphaned children. Ledger roll-up — state explicitly **which** budget nests (persisted ledger, not the ephemeral per-dispatch `RunContext`). Per-run timeline with a parent→child drill-down; **no merged ordering** (R14). Budget defaults must fit the scheduled-job lock envelope (R15). | G9 |

**Stop-clean boundary:** G1–G5 are all of Batch 06 and leave the tree shippable. If Phase 3 has to be cut short,
cut it after G5 — G6 onward is Batch 07 and G8–G10 are the irreversible-ish part (a persisted ordinal and an
interface change).

---

## §6 The workflow

One `Workflow` invocation, five phases, after the grounding pass that produced §2–§4. Grounding is **outside**
the five steps because its output had to reach the owner as questions first — `AskUserQuestion` cannot fire from
inside a background workflow.

```
[done] Ground ── 5 sonnet seam-mappers ─→ 1 opus synthesis ─→ AskUserQuestion ─→ this file
   1.  Detail planning   2 opus spec authors (06, 07) ─→ 1 opus reconciler   [writes 2 .impl.md files]
   2.  Implement         10 sequential builders, G1…G10, opus/sonnet per §5  [commit per group]
   3.  Simplify          2 sonnet passes (06 diff, 07 diff), sequential      [commit per pass]
   4.  Review            3 opus reviewers, distinct lenses, parallel
                           ─→ adversarial verify: 1 sonnet skeptic per finding, capped at 12
   5.  Fix               1 opus fix pass over CONFIRMED findings only
                           ─→ 1 opus roadmap pass (00-OVERVIEW + both batch files + "Opened by Phase 3")
```

**Phase 1 — Detail planning.** Two opus agents write `06-run-workspace-isolation.impl.md` and
`07-subagents-multipersona.impl.md` in parallel (separate files, no write conflict), each reading **this file**
for the seam map, the resolved decisions and its own risks. A third opus **reconciler** then edits both for
consistency and emits the authoritative ordered group list as structured output — it exists because 06 and 07
both touch `HeadlessTurnExecutor` and `AgentRunOrchestrator`, and someone has to own that collision before a
builder meets it.

**Phase 2 — Implement.** A sequential loop over the ten groups. Each builder receives its group spec, the
`.impl.md` files, its own risk rows, and the standing constraints in §7; it implements, runs the gate, and
commits. A builder that cannot get the gate green **stops and reports** rather than committing red — the loop
carries the failure forward so the next builder knows the tree state.

**Phase 3 — Simplify.** Two sonnet passes over the accumulated diff, one per batch, so neither sees a diff too
large to reason about. Quality only: reuse, naming, altitude, dead code. Each re-runs the gate and commits
separately.

**Phase 4 — Review.** Three opus reviewers in parallel, each with one lens: (a) **guardrails and correctness**
— the §4 risks, the architecture rules, executor parity, failure isolation; (b) **conventions and coverage** —
CLAUDE.md, privacy-first logging, the resx trio, test quality and non-vacuity; (c) **spec conformance** — does
the tree do what the two `.impl.md` files and §1's decisions say, and where does it silently diverge. Then
**every finding is handed to a fresh sonnet skeptic prompted to refute it**, defaulting to refuted when
uncertain. This is not ceremony: false-premise findings are a *recorded* failure mode on this branch — the
`SQLITE_BUSY_SNAPSHOT` hazard that did not exist, and the transitive-package exclusion that would have made CVE
reporting worse. Only CONFIRMED findings reach the fix phase; the count refuted is logged.

**Phase 5 — Fix.** One opus pass over confirmed findings, sequential, gate green before each commit. Then a
final opus roadmap pass: update `00-OVERVIEW.md`'s chronicle and rank table, mark both batch files shipped, and
write an **"Opened by Phase 3"** section in the house style — known, reasoned, not closed — including everything
Phase 3 adds to the Rank-1 smoke list (§8).

**Agent count: 3 + 10 + 2 + 3 + ≤12 + 2 = up to 32.** Well above this session's "under 15" guideline, which the
owner's full-scope answer to D1 makes unavoidable — ten commit groups is the honest size of 06 + full 07. The
verify fan-out is the only variable part and it is capped at 12 with the drop `log()`ed. If the count matters
more than the coverage, the lever is D1, not the workflow.

**Resumability.** The script is saved so a failed run resumes from the last unchanged `agent()` call rather than
restarting. Because builders commit as they go, a resume also inherits real tree state — which is exactly why
each builder must leave the tree green or say plainly that it did not.

---

## §7 Standing constraints (every builder, simplifier and fixer prompt carries these)

- **Zero warnings, absolute, in Debug *and* Release.** `dotnet build -t:Rebuild -v:n`, then again with
  `-c Release`. An **incremental** build skips `CoreCompile` and does not re-emit analyzer warnings, so a
  7-second "rebuild" looks exactly like a real one from the summary line. Read the count off MSBuild's
  `N Warning(s)` line — at `-v:n` every warning prints twice, so grepping double-counts. Sanity-check that the
  rebuild was genuine by counting `CoreCompile`/`Csc` invocations (expect 4: `Pia.Shared`, `Pia.Wpf`, the
  `Pia.Wpf_<hash>_wpftmp` XAML pass, `Pia.Wpf.Tests`). Phase 3 is test-heavy, which is exactly the trap: 186 of
  the historical 194 warnings were xUnit analyzer warnings in the test project. New tests must add **zero**.
- **Test gate:** `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`.
  The bar is `failed: 0` (2424 total at `df0841a`). **Two known intermittents** — re-run the class isolated
  before calling either a regression: `TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` (wall-clock
  assumptions, low single-digit %, bursty) and
  `AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
  (probabilistic detection window, never measured at base).
- **New `.cs` and `.md` files must be CRLF.** The repo is CRLF throughout and the Write tool emits LF; that has
  already broken byte-identical raw-string tests here.
- **Do not push, merge or rebase.** The branch is unpushed by owner decision and 49 commits ahead.
- **Append-only persisted enums and ordinals.** G8's new run state is appended. `envelope.V != 1` is an *exact
  equality* check — a version bump makes every persisted envelope unreadable at once, so grant-envelope changes
  stay additive members of `v:1`.
- **Privacy-first logging.** Paths and filenames are user content. See R7: there is no `SensitiveError`, so the
  highest DEBUG-erased severity available is `SensitiveWarning`.
- **A new user-visible string lands in `ViewStrings.resx` *and* `.de.resx` *and* `.fr.resx`** (parity is
  test-enforced). Do not hand-edit `Designer.cs`.
- **Standing guardrails:** failure-isolated bookkeeping (`Safe*` wrappers); no interactive regression; executor
  parity (Live **and** Headless); off-thread `RunChanged` stays marshaled; ViewModels do not reference
  `System.Windows`.

---

## §8 What Phase 3 adds to the Rank-1 smoke list

None of it is automatable, and the final roadmap pass must fold it into `00-OVERVIEW.md`:

1. **A real headless run writing into an isolated workspace and promoting on success** — the whole point of 06,
   and the one item no unit test substitutes for.
2. **Worktree mode against a real repo**: the run branch exists, the agent's commits are on it, the working tree
   is untouched, and teardown leaves no stale registration (`git worktree list` after the run).
3. **Copy mode against a non-repo folder**, i.e. the ordinary case, plus the degrade path when git is absent.
4. **A failed run's publish offer** — decline it, confirm the workspace is retained and then swept; accept it,
   confirm the files land.
5. **An interactive run's file chips** — clicked *during* the run and again *after* promotion (D8's two phases).
6. **The persona roster** — the settings surface persists across restart, and a plan really does assign
   different personas to different steps with the right provider each.
7. **Per-step avatars render** — the currently-empty box actually shows something, in the deferred row template
   no test can reach.
8. **A parent with parallel children**: cancellation cascades, no orphans, the ledger rolls up, and the parent
   survives an app restart in its new waiting state.
9. **DE/FR without clipping** for every new string — the publish offer, the roster surface, the panel's
   "output is on branch X" line.

---

## §9 Deliberately left to detail planning

Not escalated because the code or the conventions decide them, but named so nobody re-opens them as questions:
the promotion type's exact name (any allowlisted suffix), whether the provisioner is one type with two
strategies or two types behind one interface, where the roster is stored in `AppSettings`, the branch naming
scheme beyond the `pia/run/<runId>` sketch, and whether G10's roll-up pushes on each child write or aggregates
on read (the index in G9 makes either viable).
