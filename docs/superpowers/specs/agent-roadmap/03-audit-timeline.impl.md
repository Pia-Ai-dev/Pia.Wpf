# Batch 03 — Audit timeline (per-tool decision trace) · IMPLEMENTATION SPEC

Executable spec derived from [`03-audit-timeline.md`](03-audit-timeline.md) plus a full re-read of the code it
touches. Branch: `feature/agent-run-spine`. **Design step only — no production code was written for this
document.**

Gate for the implementing agent:

```
dotnet build -t:Rebuild -v:n                 # 0 Error(s), 0 Warning(s)
dotnet build -t:Rebuild -c Release -v:n      # 0 Error(s), 0 Warning(s)
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
                                             # failed: 0
```

Read the warning count off MSBuild's `N Warning(s)` summary line (at `-v:n` every warning prints twice).
**186 of the historical 194 warnings were xUnit analyzer warnings in `tests/Pia.Wpf.Tests`**, and this batch
adds ~40 tests: `Assert.Equal(0, list.Count)` → xUnit2013 (use `Assert.Empty`), `.Result`/`.Wait()` in a test
body → xUnit1031. New tests must add **zero**. Known flake, do not chase:
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock`.

> ## ⚠ This batch lands SECOND, on top of Batch 04
>
> **Read §0.1 below before you open a file, and read
> [`04-autonomy-policy.impl.md`](04-autonomy-policy.impl.md) §2 D15 for the decision vocabulary.** Batch 04 has
> already landed on this tree. Everything below assumes it. §0.1 is the authoritative inventory of what
> changed, because several of 03's seams are the ones 04 just rewrote.

---

## 0. Corrections and inherited state (read this first)

### 0.0 The build bar is ABSOLUTE ZERO

`00-OVERVIEW.md` still says "194 warnings, all pre-existing". Stale: `6cdd4c9` took the build to zero and
CLAUDE.md makes zero a commit-ready gate. Both configurations were measured at **0 Error(s), 0 Warning(s)**.

### 0.1 What Batch 04 already changed (do not fight a stale premise)

| Landed by 04 | Consequence for 03 |
|---|---|
| `src/Pia.Wpf/Models/ToolGateEnums.cs` — `ToolClass`, `ToolGateDecision`, `ToolGateSurface` (all **append-only, `Unknown = 0`**) and `ToolGateOutcome` (**not** persisted) | **This is 03's persisted vocabulary. Do not define a second decision enum.** 03 persists these three ordinals verbatim. §5 names the alignment. |
| `src/Pia.Wpf/Services/ToolAutonomy.cs` — the one resolver, `Resolve(in ToolGateInput) → ToolGateVerdict(Outcome, Decision)` | **The `Decision` 03 stores is already computed** for every gated call. 03 does not derive a decision; it records the one the resolver returned. |
| `src/Pia.Wpf/Services/ToolClassifier.cs` — `Classify(pluginName, isExternalRoute) → ToolClass` | Both gates already hold a `ToolClass` local. 03 stores it; no new derivation. |
| Both gates rewritten around one `Resolve` call: `ChatSession.HandleToolCall` and `BackgroundAssistantTurnRunner.HandleToolCallAsync` | **The emission points are the `switch`/`if` arms 04 wrote**, not the pre-04 expressions the batch brief describes. |
| `ChatSession` gained a private fail-closed `IsExternalTool(toolName)` (try/catch → `true`) | Reuse it; do not add a second one. |
| `IActionCardBuilder.Build(...)` gained a trailing optional `ToolClass? toolClass = null` | Signature already shifted once; 03 does not touch it. |
| `ActionCardCategory.Scheduled` appended; `scheduled-research` no longer renders as an external tool | The timeline's class column and the card now agree. |
| `StepTurnSpec` gained a trailing `RunAutonomyPolicy? Policy = null` | **03 appends AFTER it**: the member order becomes `…, UseGoalVerbatim = false, Policy = null, StepId = null, Timeline = null`. |
| `BackgroundAssistantTurnRunner.RunExchangeAsync` gained a trailing optional `RunAutonomyPolicy? policy = null` | **03 appends after it**: `…, RunAutonomyPolicy? policy = null, AgentTimelineScope? timeline = null`. |
| `HeadlessTurnExecutor.Initialize` gained a trailing optional `RunAutonomyPolicy? policy = null` | 2 call sites (`HeadlessRunLauncher.cs` launch + resume) and `HeadlessTurnExecutorTests.cs:215/:297/:677` already compile against it because the param is trailing-optional. 03 adds no further param here (§4 threads the sink per **step**, not per **executor**). |
| `LiveTurnExecutor`'s ctor gained a **trailing optional** `RunAutonomyPolicy? policy = null` | **03 appends `IAgentTimelineService? timeline = null` after it** — one more defaulted argument at the one hand-construction site (`ChatSessionManager.cs:768`). Keep it trailing-optional for the same reason 04 did: the ctor is called positionally in production **and** in `LiveTurnExecutorPlannedRunTests`. |
| `HeadlessRunLauncher`: `GrantEnvelope.Policy`, `TryRestorePolicy`, `InteractiveEmptyEnvelopeJson`, `WhenWritingNull` on `GrantEnvelopeJsonOptions` | 03 does not touch `PolicyJson` at all. |
| `AssistantViewModel.HandleVoiceModeToolCall` now runs the resolver on `ToolGateSurface.Voice` | Voice has **no run**, so it emits **nothing** (§3, D5). Explicit, not an oversight. |
| `AppSettings.AgentRunAutoApproveBuiltInWrites` + its CheckBox + 4 resx keys ×3 | 03 adds 9 more resx keys ×3; insert them in their own contiguous block. |

If commit 6 of 04 (voice mode) was dropped, nothing in 03 changes — 03 never emits for voice either way.

### 0.2 "Run tables are kept lean" is FALSE — there is no mechanism

`03-audit-timeline.md:29` cites plan §2's *"run tables stay small"* as if a bound existed. Verified: it does
not. `DELETE FROM AgentRuns` appears **nowhere** in the repo; `DELETE FROM AgentSteps` appears **once**, inside
`ReplaceStepsAsync` (`AgentRunService.cs:442`) as replan churn, not pruning. There is no Delete/Purge/Prune API
on `IAgentRunService`. The only eviction path is indirect — `AssistantChatRetentionService` (24 h timer,
`ChatHistoryRetentionDays` default 30) → `AssistantChatService.EvictOlderThanAsync:601` → FK cascade — and that
path **explicitly skips chats bearing a `Planned` run** (`:627-638`, `ChatHasPlannedRunAsync`), i.e. it exempts
exactly the multi-step runs a per-tool timeline is *for*. So a bound has to be **built** (D6), not inherited.

### 0.3 `IAgentTurnExecutor.ExecuteStepAsync` is NOT the dispatch point

`03-audit-timeline.md:17` names it as *"where tool calls are dispatched"*. It is 3–4 frames above. There is
exactly **one** dispatch line — `AiClientService.cs:398` `var result = await toolHandler(toolCall);` inside the
sequential `foreach` at `:395` inside the bounded round loop at `:165` — and **six** handler closures reach it:

| Closure | Gates? | Has a run? |
|---|---|---|
| `ChatSession.cs:550` (live: step turns **and** ordinary chat turns) | yes | only for step turns |
| `BackgroundAssistantTurnRunner.cs:324` (headless steps **and** SingleTurn) | yes | only for the headless path |
| `AssistantViewModel.cs:1403` (voice) | yes, since 04 | **no** |
| `AgentPlanner.cs:230`, `AgentVerifier.cs:98` | **no** — pure `emit_plan`/`emit_verdict` capture, no tool executed | yes |
| `TextOptimizationService.cs:177` | n/a — passes `null` | no |

Two consequences that shape the whole design. **(a)** At `:398` the only thing in scope is `object? result`; the
decision is not there and a denial arrives as *prose* (`"Denied: '…' is a write action not granted…"`). **(b)**
Emitting at `:398` would produce **phantom rows for the planner and verifier turns**. So the emission point is
inside each gate, and the ids have to be carried there (D3/D4).

### 0.4 `AgentRuns`/`AgentSteps` are device-local — and so is this table

No `SyncAgentRun`/`SyncAgentStep` DTO exists in `src/Pia.Shared/Models/`; `SyncPushRequest` has no `AgentRuns`
member; `grep AgentRun` over `SyncMapper.cs` / `SyncClientService.cs` / `Services/Sync/` returns nothing. Plan
§2.1 states the design: the **chat** is the thing that syncs; a run adds plan/state/policy/ledger over its
slice. **The audit timeline inherits that: it is per-device and dies with the machine.** Device B sees the
synced transcript and no timeline. Say so in the roadmap note — an audit trail that silently does not replicate
is worse than one documented as local.

### 0.5 `TaskAmbient.TaskId` is a trap, not a shortcut

It is the **chat** id for interactive turns (`ChatSession.cs:307`) and the **run** id for agent steps
(`ChatSession.cs:660`, `HeadlessTurnExecutor.cs:267`) — one `Guid?` field, two meanings, no discriminator — and
it never carries a step id. Reading a run id off it would file every ordinary chat's tool calls under a run id
that is actually a chat id, and would create timeline rows for turns that are not runs. Rejected in D3.

---

## 1. Verified recon (re-read 2026-07-30; cite these)

| # | Fact | Location |
|---|---|---|
| R1 | The single dispatch line, sequential, inside a bounded 10-round loop. The `FunctionResultContent` is appended to `workingMessages` and discarded with it — **nothing persists a tool result today**. | `AiClientService.cs:395-406`, `:165` |
| R2 | Tool args are `SensitiveDebug`-only at the dispatch point, truncated to 500 chars. The established privacy stance for this data. | `AiClientService.cs:380-386` |
| R3 | `TokenizingAiClientService.WrapToolHandler` decorates **every** handler (detokenize write args in, tokenize result out) and is the DI-registered `IAiClientService`. So metadata captured at `:398` is **post**-tokenization; metadata captured inside the handler is **pre**-tokenization. | `TokenizingAiClientService.cs:288-311`, `Bootstrapper.cs:399` |
| R4 | `AgentRuns` DDL + indexes live inside `EnsureSchema`'s single `CREATE TABLE IF NOT EXISTS …` command string, which runs on **every** open — so a new table needs no `MigrateSchema` entry. `MigrateSchema` contains ALTER-a-column idioms plus **one** defensive presence-check create (`Personas`, `:549-582`), explicitly labelled defensive. | `SqliteContext.cs:283-332`, `:340`, `:549-582` |
| R5 | `AgentRuns.ChatId → AssistantChats(Id) ON DELETE CASCADE` and `AgentSteps.RunId → AgentRuns(Id) ON DELETE CASCADE`. Enforcement is real but **implicit**: `grep -rn "foreign_keys" src/ tests/` returns nothing — it relies on Microsoft.Data.Sqlite's default `PRAGMA foreign_keys=1`, which `AssistantChatService`'s delete paths already depend on. The pragma is **per-connection** and three connections write these tables. | `SqliteContext.cs:303`, `:327` |
| R6 | `ReplaceStepsAsync` = `DELETE FROM AgentSteps WHERE RunId=@RunId` then re-INSERT, in one transaction, called on **every** replan (`AgentRunOrchestrator.cs:123`, `:194`, `:228`). `KeepDoneAsync` (`:279-290`) keeps **only** `Done` steps, so a **Failed** step's row is deleted and never re-inserted. | `AgentRunService.cs:437-479` |
| R7 | Run-level `ExtraJson` is **clobbered** wholesale by `CompleteAsync` (`:238`), `FailAsync` (`:268`) and `PauseAsync` (`:295`), and set to NULL by `TryBeginResumeAsync` (`:321`). It already carries the truncation envelope `{truncated,reason}` that `RunProgressViewModel.ReadTruncation` (`:226-243`) parses. | as cited |
| R8 | `AddUsageAsync(runId, stepId, usage, ct)` is the exact shape a per-step bookkeeping write takes: `lock (_gate)`, `Task.CompletedTask` return, ids/counts-only Information log, `RunChanged` raised **after** the write. | `AgentRunService.cs:165-202` |
| R9 | `AgentRunService` uses its own dedicated `SqliteConnection` (not the shared `SqliteContext` one) with `PRAGMA busy_timeout=3000`, opened lazily, guarded by a plain `object _gate`. The ctor forces `context.GetConnection()` at composition time so `EnsureSchema` has run before it opens. | `AgentRunService.cs:38-74` |
| R10 | `AssistantChatService`'s class remarks exist specifically to document that a `lock` on a UI-thread persistence path blocks the WPF message pump — which is why that class uses an awaited `SemaphoreSlim` instead. | `AssistantChatService.cs:38-51` |
| R11 | `ChatSession.HandleToolCall` runs on the **UI thread** (its own comments at `:844`, `:915`). Its bracket is `SetState(WaitingForTool)` → `await WaitForUserDecisionAsync()` → `finally` → `Running`; a `TaskCanceledException` maps to `Decline`. | `ChatSession.cs:824`, `:924-940` |
| R12 | `StepTurnSpec`'s last member is defaulted, and **both** construction sites use **named** arguments (`LiveTurnExecutor.BuildSpec:121`, `ChatSessionStepTurnTests.cs:39`). Nothing asserts spec equality. So appending defaulted members is safe. | as cited |
| R13 | `HeadlessTurnExecutor.ExecuteStepAsync` is the last frame where `step.Id` exists headless — it is discarded there; `RunExchangeStepAsync` holds only `_runId`, and its own comment at `:250-253` records that the ordinal reaches the instruction **string** and nothing else. | `HeadlessTurnExecutor.cs:214-259` |
| R14 | `LiveTurnExecutor.ExecuteStepAsync` **has** the `AgentStep`; `BuildSpec(run, step.Ordinal, …)` throws the id away. | `LiveTurnExecutor.cs:61-65`, `:121` |
| R15 | `AgentRunOrchestrator.cs:160` is the **only** frame holding both `run.Id` and `step.Id`, bracketed by `SafeSetStepStatus` and `SafeRecordStep`. The `Safe*` wrapper pattern (`:362-432`) is one `try` + one `LogWarning`, always. | as cited |
| R16 | `RunProgressViewModel` is a read-only projection, hand-constructed at `AssistantViewModel.cs:387` (**not** DI-registered), already takes `IAgentRunService`, captures `SynchronizationContext.Current` at `:119` (**not** `IUiDispatcher`), hydrates via `RunChanged` → `RefreshAsync` → `_uiContext.Post(_ => Project(run))`, and uses **no** `Application.Current`. | `RunProgressViewModel.cs:34`, `:113-136` |
| R17 | `RunProgressPanel.xaml` — ledger strip in the header `Grid` (`:13-39`), step list `ItemsControl` at `:49`; attached at `Views/AssistantView.xaml:50-52` via `DataContext="{Binding ActiveRunProgress}"`. | as cited |
| R18 | `AssistantChatRetentionService` is a `BackgroundService`: 5 s initial delay, 24 h `PeriodicTimer`, reads `ChatHistoryEnabled` + `ChatHistoryRetentionDays` (clamped 1–365), computes one `cutoff`, calls `EvictOlderThanAsync`, and wraps everything in one `try` that rethrows `OperationCanceledException` and logs anything else. | `AssistantChatRetentionService.cs:29-90` |
| R19 | `AgentStep.ExtraJson` **does** round-trip a replan for `Done` steps (`MapStep` index 14, `ReplaceStepsAsync:477`), but nothing in the codebase ever writes a non-null value, and a `Failed` step's row is dropped (R6). | as cited |

---

## 2. Decisions

### D1 — Storage: a new `AgentTimelineEvents` **table**, not `ExtraJson`

The batch file recommends a table (`:26-28`). Confirmed — for reasons stronger than *"it's queryable"*:

- **Run-level `ExtraJson` is clobbered, not merged** (R7). A run that parks at its budget and resumes would
  lose its whole timeline, and a careless merge would break the truncation chip `RunProgressViewModel` already
  parses out of that column.
- **Step-level `ExtraJson` loses exactly the interesting steps.** It survives a replan for `Done` steps but a
  **Failed** step's row is deleted and never re-inserted (R6/R19) — and a failed step is the most
  audit-relevant one there is.
- A JSON array in a column has no bound and no index; a table gets `(RunId, Seq)` for free.

**DDL — goes inside `EnsureSchema`'s existing command string** (R4), directly after the `AgentSteps` index at
`SqliteContext.cs:330`. It needs **no** `MigrateSchema` entry: that string runs on every open, so existing
databases get the table on next launch. Do **not** add a second, defensive create in `MigrateSchema` — the
`Personas` block (`:549-582`) is explicitly labelled defensive and is the exception, not the pattern.

```sql
            CREATE TABLE IF NOT EXISTS AgentTimelineEvents (
                Id                  TEXT PRIMARY KEY,
                SchemaVersion       INTEGER NOT NULL DEFAULT 1,
                RunId               TEXT    NOT NULL,
                -- StepId is deliberately NOT a foreign key. ReplaceStepsAsync DELETEs every AgentSteps row for
                -- the run and re-inserts on EVERY replan, keeping only the Done ones: a CASCADE would wipe the
                -- audit trail of the steps that already ran, and a non-cascading FK would make that DELETE
                -- throw into a swallowing Safe* wrapper, leaving the run executing a stale plan. A dangling
                -- StepId is the correct outcome here — the trail outlives the plan row it points at.
                StepId              TEXT    NULL,
                -- Monotonic per RUN, allocated in memory at emit time. NOT a timestamp: DateTime.UtcNow has
                -- ~1 ms resolution on Windows and several tool calls in one round finish faster than that.
                Seq                 INTEGER NOT NULL,
                Kind                INTEGER NOT NULL,
                Surface             INTEGER NOT NULL,
                Decision            INTEGER NOT NULL,
                Outcome             INTEGER NOT NULL,
                ToolName            TEXT    NOT NULL,
                ToolClass           INTEGER NOT NULL,
                PluginId            TEXT    NULL,
                -- METADATA ONLY (§3): lengths, never content. No args, no results, no paths, no hashes.
                ArgsChars           INTEGER NULL,
                ResultChars         INTEGER NULL,
                DurationMs          INTEGER NULL,
                CreatedAt           TEXT    NOT NULL,
                FOREIGN KEY (RunId) REFERENCES AgentRuns(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AgentTimelineEvents_RunId ON AgentTimelineEvents(RunId, Seq);
            CREATE INDEX IF NOT EXISTS IX_AgentTimelineEvents_CreatedAt ON AgentTimelineEvents(CreatedAt);
```

The `RunId` FK keeps the chat-delete cascade chain intact (R5): deleting a chat still reaps runs → steps → and
now timeline rows. `IX_…_CreatedAt` exists for the prune (D6), which is a range delete on that column.

**There is deliberately no `ExtraJson` column.** A free-text column on an audit table is where payloads go to
hide; T-PRIV-2 asserts the column list exactly, so adding one fails a test rather than passing review.

### D2 — Granularity: one row per **gated** tool call, written **once**, after the outcome

The batch file's own test says *"a run with N tool calls … produces N ordered events"* (`:42`). Restated in
this batch's vocabulary: **N *gated* tool calls → N ordered events.** A read (a route that returned a non-null
`result`) emits **nothing**.

Why: reads are unbounded in count (`search_files`, `read_file`, `recall` in a loop over 10 rounds), they carry
**no decision** — the audit question is *what did this run change, and who said yes* — and the one genuinely
interesting thing about a read is its **target**, which §3 forbids persisting. A read row would therefore be
`("read_file", Ok, 1 of 40)`: high row count, near-zero audit value, and it would make D6's cap bite on the
wrong events. `ToolGateDecision` reserves no `AllowedRead` member; a later batch that wants reads appends one
(the enum is append-only precisely so that is cheap).

**One row, not two.** The row is written after `Execute()` returns (or after the refusal is composed), carrying
`Decision` **and** `Outcome` **and** `DurationMs` together. Rejected: a decision row plus an outcome row —
doubles the count, needs a correlation id, and doubles the cap pressure. Accepted cost: if the process dies
**during** a tool call, that call leaves no row. That is acceptable because the run dies with it (the startup
crash sweep settles it `Cancelled`), and a half-written audit row claiming "approved" for a call whose effect is
unknown would be worse than a missing one. Record it in the class doc.

`UnknownTool` **is** emitted (a route that returned null): *"the model called a tool that does not exist, 12
times"* is a real audit fact, it is one line in each gate's existing null-route branch, and it cannot flood
(the round loop is bounded at 10 and the model gets the error text back).

### D3 — The ids reach the gate via a **per-step sink object**, not an ambient and not the dispatch point

New `sealed class AgentTimelineScope` — an immutable `(IAgentTimelineService service, Guid runId, Guid? stepId)`
triple with one method, `Emit(...)`, which is `void`, never throws, and returns immediately (D7). The executor
creates one per step and hands it down; the gate closure captures it; a `null` scope means *emit nothing*, which
is what every non-run turn passes.

Rejected, each for a specific reason:

- **`TaskAmbient.TaskContext`.** §0.5: `TaskId` is a chat id *or* a run id with no discriminator, and never a
  step id. Extending it means a fourth meaning on a field that already has two, plus `TaskAmbientTests` to
  renegotiate.
- **Emit at `AiClientService.cs:398`.** §0.3: no decision, no ids, denial-as-prose, and phantom rows for the
  planner and verifier closures. It is the right place for a *round number* and nothing else — which is why
  there is no `Round` column (D8).
- **New parameters on the tool-handler delegate** (`Func<FunctionCallContent, Task<object?>>`). Six closures
  and one public streaming signature would change; five of the six have nothing to emit.
- **A per-run "current step" slot on the orchestrator** (`AgentRunOrchestrator.cs:160` is the one frame holding
  both ids, R15). Tempting and cheap, but it makes correctness depend on a mutable slot being cleared on every
  exit path of a loop that has six of them, and the interactive tool gate runs on the UI thread while the
  orchestrator loop does not — so the slot would be cross-thread mutable state guarding an audit trail.
  Rejected on those grounds, not on effort.

### D4 — Executor parity: the sink rides `StepTurnSpec` (Live) and `RunExchangeAsync` (Headless)

Both executors already receive `(AgentRun run, AgentStep step, …)` from `AgentRunOrchestrator.cs:160`. The
asymmetry is below that, and each side needs exactly one plumbing hop:

**Live.** `LiveTurnExecutor`'s ctor takes `IAgentTimelineService` (added by `ChatSessionManager`, which has DI —
the same one-argument change 04 already made to that ctor for the policy). `ExecuteStepAsync` passes `step.Id`
into `BuildSpec`, which sets two appended `StepTurnSpec` members:

```csharp
    …,
    RunAutonomyPolicy? Policy = null,     // Batch 04
    Guid? StepId = null,                  // Batch 03: the step this turn belongs to (audit attribution)
    AgentTimelineScope? Timeline = null); // Batch 03: null ⇒ emit nothing (ordinary chat turns, tests)
```

> **AS BUILT: `StepTurnSpec.StepId` does NOT exist — the scope is the only carrier of the step id.** It shipped in
> commit 2, was found to be **written and never read** (attribution comes from the scope's own `StepId`), and was
> **deleted** by the review fix pass along with the unused `AgentTimelineScope.ForStep`. Two sources of truth for
> one fact, one of them dead, is the worse failure mode: the *documented* one was the dead one, so a later
> executor could reasonably construct a spec with `StepId: step.Id` and a run-level scope, and every row for that
> step would persist `StepId = NULL` with nothing failing — the parity fact already built exactly that mismatched
> pair and passed. `AgentTimelineEvents.StepId` (the **column**) is unaffected and still the attribution key;
> every other mention of `StepTurnSpec.StepId` in this file (§2's insertion-order note, §6's file table, §11's
> commit 2 row) should be read as "the scope only". T-EMIT-3's red-mutation instruction changes accordingly:
> drop the scope's `stepId`, not a spec member.

`ChatSession.RunStepTurnAsync` forwards `spec.Timeline` into `RunModelExchangeAsync` (one new trailing optional
parameter) and thence into the handler closure at `:550`. **`ChatSession`'s constructor is not touched** —
that is the point of putting the sink on the spec rather than injecting the service: `RunTurnAsync`, the
ordinary interactive path, passes nothing and emits nothing, with no null-service field to reason about.

**Headless.** `HeadlessTurnExecutor.ExecuteStepAsync` builds its own scope (it has `IAgentTimelineService` by
DI — it is already a scoped service) and passes it into `RunExchangeStepAsync` → `RunExchangeAsync`'s new
trailing optional `AgentTimelineScope? timeline = null` → the closure at `:324`. `RunSingleTurnFallbackAsync`
passes a scope with `stepId: null` (the R10 degrade turn belongs to the run but to no step). The SingleTurn
background path (`RunAsync`) passes `null`.

Because `HeadlessTurnExecutor` and the SingleTurn runner share `RunExchangeAsync` (R23 of 04), one parameter
covers both, and the emission code lives once in `HandleToolCallAsync`. **Parity is then structural on the
headless side and one-hop on the live side**, and T-PARITY-1 asserts that the same tool call through the two
executors produces rows differing only in `Surface`.

`AgentTimelineScope` is a reference-typed member on a record — as `AiProvider` and `IList<AITool>` already are
(R12), and nothing asserts spec equality.

### D5 — Voice mode emits nothing, and that is stated

Batch 04 gave voice mode the gate; it has **no run**, so there is no `RunId` to attach a row to and the FK would
reject one. `ToolGateSurface.Voice` therefore never appears in the table on this build. It exists in the enum
so a later batch that gives voice turns a run does not need a new ordinal, and so the render surface's
surface-label switch is total. **Say this in the roadmap note**: the timeline covers the two run gates, not
every tool call in the app.

### D6 — Two bounds: a per-run cap of 500, and a retention prune on the row's own age

**Cap.** `AgentTimelineService.MaxEventsPerRun = 500`. The service keeps a per-run in-memory slot
`(long NextSeq, int Count, bool CapNoted)` seeded on first touch from one query
(`SELECT COALESCE(MAX(Seq),0), COUNT(*) FROM AgentTimelineEvents WHERE RunId=@r`), so the cap check is free and
survives a process restart. On the 501st event: write **one** synthetic row with `Kind = TraceTruncated`, set
`CapNoted`, and drop every later event for that run. Per-run rows are therefore ≤ 501, hard. The render surface
reads that row and says so (§7).

**Prune.** `IAgentTimelineService.PruneOlderThanAsync(DateTime cutoff, CancellationToken)` →
`DELETE FROM AgentTimelineEvents WHERE CreatedAt < @cutoff` (an indexed range delete), returning the row count.
Called from `AssistantChatRetentionService.RunCleanupAsync` (R18) with the **same** `cutoff` it already computes
from `ChatHistoryRetentionDays` — so the trail lives exactly as long as the chat history it describes, and the
batch adds **no new setting**.

**Prune on the row's `CreatedAt`, not the run's `CompletedAt`.** A crash-swept or cancelled run can leave
`CompletedAt` NULL forever, which would make its rows immortal; and a row-local column needs no join and has no
null case. Placed after the `EvictOlderThanAsync` call, inside the same `try`, and guarded by the same
`ChatHistoryEnabled` check — a user who has turned history off should not accumulate a growing audit table.

Rejected: **prune at terminal settle.** That deletes the trail at the exact moment it becomes useful.
Rejected: **rely on the chat cascade alone.** §0.2: the one eviction path exempts `Planned`-run chats, i.e.
precisely the runs with timelines.

### D7 — Emission is fire-and-forget, ordered, and off the UI thread

`Emit(...)` is `void`. Inside, under one `lock`:

1. allocate `Seq` from the per-run slot (**synchronously** — this is what makes ordering correct even though
   the write is not);
2. apply the cap;
3. hand the fully-built row to a serial writer (one `SemaphoreSlim(1,1)`-chained task, or a
   `Channel<AgentTimelineEvent>` with a single reader — either is fine; say which in the class doc) and return.

A **synchronous** DB write here would block the WPF message pump: `ChatSession.HandleToolCall` runs on the UI
thread (R11) and `AgentRunService` does all its work under a plain `lock` — which is exactly the hazard
`AssistantChatService`'s class remarks were written to document (R10). Allocating `Seq` under a lock is a
handful of instructions on an in-memory dictionary and is not that hazard.

Every path is failure-isolated: `Emit` wraps its own body in `try/catch` → `LogWarning`, and the writer task
does the same per row. **Emitting a timeline event must never fail a step** — so the gate calls `Emit` with no
`await`, no `SafeFireAndForget` (there is no task to forget) and no result to check.

Rejected: **write through `AgentRunService`.** Its `RunChanged` event is consumed by `RunProgressViewModel` and
`ChatSessionManager`; raising it ~500 times per run would turn each into a `GetAsync` + full re-projection
storm. The timeline service raises **no** event; the surface loads on demand (§7).

### D8 — No `Round`, no args hash, no `ExtraJson`

- **No `Round` column.** Only `AiClientService` knows the round, and getting it to the gate means a new
  parameter on the tool-handler delegate — six closures, five of which have nothing to emit (D3). `Seq` already
  gives a total order, which is what an audit needs.
- **No args/result hash.** A `SHA256` of `{"path":"C:\\Users\\marco\\notes\\salary.md"}` is not anonymous: the
  arg space for a file tool is low-entropy and enumerable, so a hash is a **confirmation oracle** — anyone with
  the DB can test a guessed path for a match. Rejected on privacy grounds, and it buys nothing an audit needs
  ("was this the same call twice?" is answerable from `ToolName` + `Seq`).
- **Metadata captured pre-tokenization**, i.e. inside the handler, not at `:398` (R3). `ArgsChars` is therefore
  the length of what the model sent and `ResultChars` the length of what the tool returned — before
  `TokenizingAiClientService` rewrites either. State it in the doc comment so nobody reconciles the two numbers
  against a log line captured on the other side of the wrapper.

### D9 — Render surface: **ships**, as a read-only expander on the existing panel

A collapsed `Expander` inside `RunProgressPanel.xaml` under the step list, bound to a `Timeline` collection on
`RunProgressViewModel`, loaded **on expand** (not on every `RunChanged` — D7's whole point is that the timeline
does not participate in live projection).

Decision vocabulary is rendered as a **category**, not per-member: `AutoApproved` / `Approved` / `Denied` /
`Blocked` / `Unknown` — 5 strings instead of 11, and the 11 ordinals are still queryable in the DB. An ordinal
this build does not know renders as `Unknown` and never throws (the append-only guardrail's other half).

Ships in this batch because *"a completed run **exposes** an ordered, privacy-safe, per-tool audit timeline"* is
the acceptance sentence, and a service method is not an exposure a user can reach. It is the **last** commit
group; if it must be cut, the store and the emission are complete and the acceptance is met minus the word
"exposes".

### D10 — Two new non-persisted-vocabulary enums; everything else comes from Batch 04

```csharp
/// <summary>What a timeline row IS. PERSISTED → APPEND-ONLY (Unknown = 0 renders as unknown, never throws).</summary>
public enum AgentTimelineEventKind
{
    Unknown = 0,
    /// <summary>One gated tool call: its decision, its outcome, and the step it belonged to.</summary>
    ToolCall = 1,
    /// <summary>The per-run cap was reached; later events for this run were dropped (03 D6).</summary>
    TraceTruncated = 2,
}

/// <summary>What happened AFTER the decision. PERSISTED → APPEND-ONLY.</summary>
public enum AgentTimelineOutcome
{
    Unknown = 0,
    /// <summary>Authorized and Execute() returned.</summary>
    Ok = 1,
    /// <summary>Authorized and Execute() threw. The exception TYPE is logged, never stored.</summary>
    Error = 2,
    /// <summary>Not authorized — nothing ran. The Decision says why.</summary>
    NotExecuted = 3,
}
```

`Surface`, `Decision` and `ToolClass` are Batch 04's enums, stored as ordinals (§5). **Do not introduce a
second decision enum**; if a decision value is missing, it is missing from 04's and belongs there.

---

## 3. Payload privacy — what a "reference" concretely is

The batch file says the timeline stores *"references/metadata, not raw payloads"* (`:30-31`). Concretely, a
**reference** in this table is exactly one of three things:

1. **An id** — `RunId`, `StepId`, `PluginId`, `Id`. Opaque GUIDs.
2. **A count or a duration** — `Seq`, `ArgsChars`, `ResultChars`, `DurationMs`. Numbers, not fingerprints.
3. **A name that is already safe at `Information`** — `ToolName` only, whose precedent is both gates logging it
   at Information today (`ChatSession.cs:917`, `BackgroundAssistantTurnRunner.cs:398`). A tool name is schema
   (built-in constants, or an MCP server's declared tool list) — never user content.

**Never persisted, in any column, in any build:** tool arguments (`PluginToolCall.Details` is the serialized
argument JSON for MCP — `McpPluginToolHandler.cs:113`), tool results, `pendingAction.Description` (user-facing
text), file paths (`pendingAction.TargetPath`), the run goal, a step title or intent, any provider error string,
and **any hash of any of the above** (D8).

There is no `#if DEBUG` escape hatch. The batch file offers one (`:31`, *"or gates raw payloads behind
`#if DEBUG`"*) and it is declined: a DEBUG-only column would still be a column, `T-PRIV-2` would have to
special-case it, and the existing mechanism for looking at a payload while developing is already right there —
`SensitiveDebug`, which erases the call **and its argument evaluation** from release IL.

**Logging.** New log lines carry run id, step id, `Seq`, tool name, and the three enum **values**. Never the
counts of a specific payload alongside its name at Information? — counts are fine (they are already logged:
`AiClientService.cs:400` logs a result length at `SensitiveDebug`, and lengths alone appear at Information in
several services). Never a rejected class name, never a provider error string.

---

## 4. The pieces to write

### 4.1 `src/Pia.Wpf/Models/AgentTimelineEvent.cs` — new (CRLF)

```csharp
/// <summary>
/// One row of a run's audit timeline: a single GATED tool call, its approval decision, its outcome, and the
/// step it belonged to. METADATA ONLY — see 03 §3. Ordered by (RunId, Seq); Seq is allocated in memory at emit
/// time, never derived from a timestamp (DateTime.UtcNow's ~1 ms resolution is coarser than one tool call).
/// </summary>
public sealed record AgentTimelineEvent(
    Guid Id, Guid RunId, Guid? StepId, long Seq,
    AgentTimelineEventKind Kind, ToolGateSurface Surface, ToolGateDecision Decision, AgentTimelineOutcome Outcome,
    string ToolName, ToolClass ToolClass, Guid? PluginId,
    int? ArgsChars, int? ResultChars, long? DurationMs, DateTime CreatedAt)
{
    public int SchemaVersion { get; init; } = 1;
}
```

### 4.2 `src/Pia.Wpf/Services/Interfaces/IAgentTimelineService.cs` — new (CRLF)

```csharp
    /// <summary>Append one row. NEVER throws, NEVER blocks: Seq is allocated synchronously (so ordering is
    /// correct) and the write is queued to a serial background writer. Emitting an audit event must never be
    /// able to fail a step (03 D7).</summary>
    void Emit(AgentTimelineEvent e);

    /// <summary>The run's rows in (RunId, Seq) order. Read on demand by the render surface — this service
    /// raises no change event, deliberately (03 D7).</summary>
    Task<IReadOnlyList<AgentTimelineEvent>> GetForRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Delete rows older than <paramref name="cutoff"/> by the ROW's own CreatedAt (never the run's
    /// CompletedAt, which a crash-swept run leaves NULL forever). Returns rows deleted. Never throws.</summary>
    Task<int> PruneOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
```

`AgentTimelineScope` lives in the same file as the interface (it is part of the contract), holding
`(IAgentTimelineService, Guid RunId, Guid? StepId)` and one convenience `Emit(...)` that fills `Id`,
`CreatedAt`, `Kind = ToolCall` and the two ids from the scope.

### 4.3 `src/Pia.Wpf/Services/AgentTimelineService.cs` — new (CRLF)

Mirror `AgentRunService`'s infrastructure verbatim (R9): its own `SqliteConnection` from
`SqliteContext.ConnectionString`, `PRAGMA busy_timeout=3000`, lazy open, `context.GetConnection()` forced in the
ctor so `EnsureSchema` has run, `IDisposable`, a `_disposed` flag every public method checks. Registered
`AddSingleton<IAgentTimelineService, AgentTimelineService>()` in `Bootstrapper` next to `IAgentRunService`.

Per-run slot: `Dictionary<Guid, RunSlot>` where `RunSlot { long NextSeq; int Count; bool CapNoted; }`, seeded
lazily from the one `MAX(Seq)/COUNT(*)` query under the same lock. **The seeding is a correctness case, not an
optimization**: a run parked in one process and resumed in another must continue its `Seq`, or ordering breaks
and `Id` uniqueness is the only thing left. T-SEQ-2 pins it.

### 4.4 The two emission points

**Interactive** — in the arms Batch 04 wrote in `ChatSession.HandleToolCall`. One local
`var startedAt = Stopwatch.GetTimestamp();` around `pendingAction.Execute()` inside `ExecuteAndReport`, and one
`timeline?.Emit(...)` per terminal arm:

| Arm (post-04) | `Decision` | `Outcome` |
|---|---|---|
| route returned null | `UnknownTool` | `NotExecuted` |
| `verdict.Outcome == AutoRun` | `verdict.Decision` (`AutoApprovedStandingGrant` \| `AutoApprovedPolicy`) | `Ok` / `Error` |
| `case ToolDecision.AllowOnce` | `ApprovedOnce` | `Ok` / `Error` |
| `case ToolDecision.AlwaysAllow` | `ApprovedAlways` | `Ok` / `Error` |
| `default` (declined) | `DeclinedByUser` | `NotExecuted` |
| `catch (TaskCanceledException)` | `CardCancelled` | `NotExecuted` |

`CardCancelled` must be distinguishable from `DeclinedByUser`: `ChatSession.cs:930-934` maps a cancelled card
(new chat / retry / scope dispose — `AssistantViewModel.CancelPendingActionCards`) to `ToolDecision.Decline`,
and recording that as *"the user declined"* would be a false audit statement. Emit before the mapping.

**Unattended** — in the arms 04 wrote in `BackgroundAssistantTurnRunner.HandleToolCallAsync`: `AutoRun` →
`verdict.Decision` + `Ok`/`Error`; `Refuse` → `DeniedDestructiveFloor` + `NotExecuted`; `default` →
`DeniedNotGranted` + `NotExecuted`; null route → `UnknownTool` + `NotExecuted`.

`Outcome = Error` comes from wrapping `pendingAction.Execute()` in a `try/catch` that **rethrows** — the emit
happens in the catch, then the exception continues to its existing handler. Do **not** swallow: the gates'
current behaviour on a throwing tool is untouched by this batch.

---

## 5. The shared vocabulary (the explicit 04 ↔ 03 alignment)

Batch 04 was built first in this run **so that 03's persisted enum is complete on the first try**. The shared
vocabulary is:

| Type | Defined in | Persisted by 03 as | Members 03 must handle |
|---|---|---|---|
| `ToolGateDecision` | `Models/ToolGateEnums.cs` (Batch 04, D15) | `Decision INTEGER` | **all 12 ordinals (0–11)**; 03 **writes** 1–11, and renders 0 (`Unknown`) for anything else |
| `ToolGateSurface` | same | `Surface INTEGER` | writes `Interactive`(1) and `Unattended`(2); `Voice`(3) exists but is never written (D5) |
| `ToolClass` | same | `ToolClass INTEGER` | **all 9 members (0–8)**; `Ingest = 8` is never produced today (the ingest handler runs inline and returns no pending action) but still needs a column mapping and a label, because it is a member of an append-only PERSISTED enum |
| `AgentTimelineEventKind`, `AgentTimelineOutcome` | this batch (D10) | `Kind`, `Outcome` | — |

Coverage check, per the run brief's requirement that 03's enum cover *every* decision Batch 04 can produce —
walk 04's gates and map each terminal arm:

- interactive auto path → `AutoApprovedStandingGrant`, `AutoApprovedPolicy` ✔
- interactive card path → `ApprovedOnce`, `ApprovedAlways`, `DeclinedByUser`, `CardCancelled` ✔
- unattended → `GrantedByName`, `AutoApprovedPolicy`, `DeniedNotGranted`, `DeniedDestructiveFloor` ✔
- either, pre-gate → `UnknownTool` ✔
- voice (04 D13) → produces `GrantedByName`-shaped and refusal-shaped verdicts, **not recorded** (D5) ✔

Nothing 04 can decide is unrepresentable. T-VOCAB-1 mechanizes this: it reflects `ToolGateDecision` and asserts
every member except `Unknown` is either written by one of the six emission arms (a hardcoded expected set) or
listed in a documented `NotEmittedByDesign` set — so **adding a decision to 04 without deciding what 03 does
with it fails a test**.

---

## 6. Files to touch

| File | Change |
|---|---|
| `src/Pia.Wpf/Infrastructure/SqliteContext.cs` | the `AgentTimelineEvents` DDL + 2 indexes, inside `EnsureSchema`'s command string (D1) |
| `src/Pia.Wpf/Models/AgentTimelineEvent.cs` | **new (CRLF)** — the record + `AgentTimelineEventKind` + `AgentTimelineOutcome` |
| `src/Pia.Wpf/Services/Interfaces/IAgentTimelineService.cs` | **new (CRLF)** — the interface + `AgentTimelineScope` |
| `src/Pia.Wpf/Services/AgentTimelineService.cs` | **new (CRLF)** — store, Seq allocation, cap, serial writer, prune |
| `src/Pia.Wpf/Bootstrapper.cs` | `AddSingleton<IAgentTimelineService, AgentTimelineService>()` |
| `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` | `StepTurnSpec` gains `Guid? StepId = null, AgentTimelineScope? Timeline = null` (after 04's `Policy`) |
| `src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs` | ctor gains a **trailing optional** `IAgentTimelineService? timeline = null`; `ExecuteStepAsync` passes `step.Id`; `BuildSpec` builds the scope (null service ⇒ null scope ⇒ no rows) |
| `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` | pass `IAgentTimelineService` into `LiveTurnExecutor` (`:768`) |
| `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` | `RunModelExchangeAsync` + `HandleToolCallWithStatus` + `HandleToolCall` gain a trailing `AgentTimelineScope? timeline = null`; 6 emit calls; a `Stopwatch` timestamp around `Execute()` |
| `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs` | `RunExchangeAsync` + `HandleToolCallAsync` gain a trailing `AgentTimelineScope? timeline = null`; 4 emit calls |
| `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` | takes `IAgentTimelineService`; builds a per-step scope; relays it through `RunExchangeStepAsync` |
| `src/Pia.Wpf/Services/AssistantChatRetentionService.cs` | takes `IAgentTimelineService`; one `PruneOlderThanAsync(cutoff, ct)` call inside the existing `try` |
| `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` | `Timeline` collection + `TimelineRowViewModel` + `IsTimelineExpanded` + load-on-expand; ctor gains a **trailing optional** `IAgentTimelineService? timeline = null` |
| `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml` | the read-only `Expander` |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | pass `IAgentTimelineService` into the hand-constructed `RunProgressViewModel` (`:387`) |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 9 keys each |

**Every new parameter in this batch is trailing and defaulted** — on `StepTurnSpec`, on `RunExchangeAsync`, on
`RunModelExchangeAsync`, on both tool handlers, **and on both hand-constructed types' constructors**
(`LiveTurnExecutor`, `RunProgressViewModel`; `null ⇒ emit/render nothing`). Both of those are constructed with
**positional** argument lists in production *and* in `LiveTurnExecutorPlannedRunTests` /
`RunProgressViewModelTests`, so a required parameter would force test edits into commit 2 — whose entire proof is
that no existing test needed editing. A *forgotten* argument at either production call site is caught by
T-EMIT-1 (live rows appear) and T-UI-1 (the panel loads rows).

---

## 7. Render surface

`RunProgressViewModel` additions — read-only, no new mutation:

```csharp
    /// <summary>Rows of the run's tool-decision trace. Loaded ON EXPAND, not on every RunChanged: the
    /// timeline deliberately does not participate in live projection (03 D7/D9).</summary>
    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = new();

    [ObservableProperty] private bool _isTimelineExpanded;
    [ObservableProperty] private bool _isTimelineTruncated;
    [ObservableProperty] private string? _timelineNote;

    /// <summary>Drives the "nothing recorded" line. A BOOL the VM owns, not an inverse converter: the panel
    /// already uses BooleanToVisibilityConverter, and an unresolved StaticResource inside a DataTemplate throws
    /// at TEMPLATE INSTANTIATION — i.e. the first time a user expands this — which no test reaches.</summary>
    [ObservableProperty] private bool _hasNoTimeline = true;

    partial void OnIsTimelineExpandedChanged(bool value)
    {
        if (value && Timeline.Count == 0) LoadTimelineAsync().SafeFireAndForget(_logger);
    }
```

> **AS BUILT — this block was superseded by the review fix pass; the shipped code is the authority.** Two
> changes, both because the prescription above lets the panel make a claim it has not checked.
>
> 1. **Reload on EVERY expand**, not on an empty collection and not behind a load-once latch:
>    `if (!value) return;` then load. Nothing in the session can ever re-read otherwise — `RunChanged`
>    deliberately skips the timeline, `SyncRunProgress` keeps the same VM for the run's whole life,
>    `ChatSession.ActiveRunId` is stamped once, and there is no refresh command — so a trace expanded while step
>    1 was still planning would keep rendering *"no tool decisions were recorded"* for the rest of the session on
>    a run that went on to record dozens. One indexed read per user click is the cheaper mistake.
> 2. **A failed read is NOT the empty state.** A second bool, `HasTimelineReadError`, plus a
>    `Run_Timeline_ReadFailed` key in all three locales. `HasNoTimeline = !readFailed && Timeline.Count == 0`, so
>    a store that cannot be read says so instead of asserting that nothing happened. Commit `3e06bbff` spends its
>    whole message arguing that a cancelled card must not be *stored* as a user denial because it would be a
>    false statement; the render surface is held to the same standard.
>
> Both mutations of bound state — including the null-service and read-failure arms — now go through one
> `ApplyTimelineAsync` that posts to the UI context, so no `[ObservableProperty]` in this method is ever assigned
> off-thread (G3 by one path rather than three). The read itself is wrapped in `Task.Run`, because
> `GetForRunAsync`'s first `await` does **not** suspend when the writer tail is already complete — the normal case
> for a finished run — which would otherwise put the store's connection lock and the mapping of up to 501 rows on
> the dispatcher.

`LoadTimelineAsync` reads `GetForRunAsync`, then marshals the collection fill through the **same
`_uiContext.Post`** the existing `RefreshAsync` uses (R16) — G3 by the same mechanism, not a new one. Inside
that post: a `TraceTruncated` row sets `IsTimelineTruncated` + `TimelineNote`
(`Format("Run_Timeline_Truncated", MaxEventsPerRun)`) and is **not** added as an ordinary row; every other row is
projected; then `HasNoTimeline = Timeline.Count == 0` — **as built, `!readFailed && Timeline.Count == 0`, see the
note above**. A null `_timeline` service (the trailing-optional ctor argument, §6) short-circuits to
`HasNoTimeline = true` and reads nothing.

`TimelineRowViewModel` is a plain projection: `ToolName`, `DecisionLabel` (the 5-way category, §8),
`OutcomeSuffix` (localized *"failed"* when `Outcome == Error`, else null), `StepLabel` (`"Step {n}"` derived by
matching `StepId` against the already-projected `Steps`, or null), `TimeLabel` (`CreatedAt.ToLocalTime()`
short time). **No path, no args, no result text** — there is none to project.

`RunProgressPanel.xaml`, after the step-list `ItemsControl` (`:49`):

```xml
      <!-- Audit timeline (Batch 03): read-only, collapsed, loaded on expand. Metadata only — the store holds
           no tool args, results or paths, so there is nothing here to reveal. -->
      <Expander Header="{loc:Str Run_Timeline_Header}"
                IsExpanded="{Binding IsTimelineExpanded}"
                Margin="0,6,0,0" FontSize="12">
        <StackPanel>
          <TextBlock Text="{Binding TimelineNote}" Margin="0,2,0,4" FontSize="11" TextWrapping="Wrap"
                     Foreground="{DynamicResource TextMutedBrush}"
                     Visibility="{Binding IsTimelineTruncated, Converter={StaticResource BooleanToVisibilityConverter}}" />
          <TextBlock Text="{loc:Str Run_Timeline_Empty}" Margin="0,2,0,2" FontSize="11"
                     Foreground="{DynamicResource TextMutedBrush}"
                     Visibility="{Binding HasNoTimeline, Converter={StaticResource BooleanToVisibilityConverter}}" />
          <ItemsControl ItemsSource="{Binding Timeline}">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <Grid Margin="0,1">
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" />
                  </Grid.ColumnDefinitions>
                  <TextBlock Grid.Column="0" Text="{Binding TimeLabel}" FontSize="11" Margin="0,0,8,0"
                             Foreground="{DynamicResource TextSubtleBrush}" />
                  <TextBlock Grid.Column="1" Text="{Binding ToolName}" FontSize="11"
                             TextTrimming="CharacterEllipsis" />
                  <TextBlock Grid.Column="2" Text="{Binding DecisionLabel}" FontSize="11" Margin="8,0,0,0"
                             Foreground="{DynamicResource TextMutedBrush}" />
                </Grid>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </StackPanel>
      </Expander>
```

Every `StaticResource` / `DynamicResource` used above is **already used in this same file**
(`BooleanToVisibilityConverter`, `TextMutedBrush`, `TextSubtleBrush` — R17), which is deliberate: an unresolved
`StaticResource` inside a `DataTemplate` throws when the template is first instantiated, i.e. the first time a
user expands the trace, and no test in this suite reaches that. **Introduce no new converter** — that is why the
empty-state binds the VM's own `HasNoTimeline` bool rather than an inverse converter.

### 7.1 resx — 9 keys, all three files, one contiguous block after the Batch 04 block

> **AS BUILT: 11 keys × 3, not 9.** Two were added for reasons this section could not have known.
> `Run_Timeline_Step` — §7 prescribes `StepLabel` as `"Step {n}"` but names no key for it and no existing `Run_*`
> key fits. `Run_Timeline_ReadFailed` — the fix pass split *"could not be read"* from *"nothing was recorded"*
> (see the note in §7). Every other count of “9 keys” in this file (§4's table, §6's file list, §10's guardrail
> line, §11's commit 5 row) reads 11 as built. The **rule** is unchanged and was honoured: en + de + fr, real
> German and French, no hand-edited `Designer.cs`.

`ViewStrings.resx` (en):
```xml
  <data name="Run_Timeline_Header" xml:space="preserve"><value>Tool activity</value></data>
  <data name="Run_Timeline_Empty" xml:space="preserve"><value>No tool decisions were recorded for this run.</value></data>
  <data name="Run_Timeline_Truncated" xml:space="preserve"><value>Trace shortened — only the first {0} decisions of this run were recorded.</value></data>
  <data name="Run_Timeline_Decision_AutoApproved" xml:space="preserve"><value>Auto-approved</value></data>
  <data name="Run_Timeline_Decision_Approved" xml:space="preserve"><value>Approved</value></data>
  <data name="Run_Timeline_Decision_Denied" xml:space="preserve"><value>Denied</value></data>
  <data name="Run_Timeline_Decision_Blocked" xml:space="preserve"><value>Blocked</value></data>
  <data name="Run_Timeline_Decision_Unknown" xml:space="preserve"><value>Unknown</value></data>
  <data name="Run_Timeline_Outcome_Failed" xml:space="preserve"><value>failed</value></data>
```

`ViewStrings.de.resx`:
```xml
  <data name="Run_Timeline_Header" xml:space="preserve"><value>Werkzeug-Aktivität</value></data>
  <data name="Run_Timeline_Empty" xml:space="preserve"><value>Für diese Ausführung wurden keine Werkzeug-Entscheidungen aufgezeichnet.</value></data>
  <data name="Run_Timeline_Truncated" xml:space="preserve"><value>Protokoll gekürzt – nur die ersten {0} Entscheidungen dieser Ausführung wurden aufgezeichnet.</value></data>
  <data name="Run_Timeline_Decision_AutoApproved" xml:space="preserve"><value>Automatisch freigegeben</value></data>
  <data name="Run_Timeline_Decision_Approved" xml:space="preserve"><value>Freigegeben</value></data>
  <data name="Run_Timeline_Decision_Denied" xml:space="preserve"><value>Abgelehnt</value></data>
  <data name="Run_Timeline_Decision_Blocked" xml:space="preserve"><value>Blockiert</value></data>
  <data name="Run_Timeline_Decision_Unknown" xml:space="preserve"><value>Unbekannt</value></data>
  <data name="Run_Timeline_Outcome_Failed" xml:space="preserve"><value>fehlgeschlagen</value></data>
```

`ViewStrings.fr.resx`:
```xml
  <data name="Run_Timeline_Header" xml:space="preserve"><value>Activité des outils</value></data>
  <data name="Run_Timeline_Empty" xml:space="preserve"><value>Aucune décision d'outil n'a été enregistrée pour cette exécution.</value></data>
  <data name="Run_Timeline_Truncated" xml:space="preserve"><value>Trace raccourcie — seules les {0} premières décisions de cette exécution ont été enregistrées.</value></data>
  <data name="Run_Timeline_Decision_AutoApproved" xml:space="preserve"><value>Approuvé automatiquement</value></data>
  <data name="Run_Timeline_Decision_Approved" xml:space="preserve"><value>Approuvé</value></data>
  <data name="Run_Timeline_Decision_Denied" xml:space="preserve"><value>Refusé</value></data>
  <data name="Run_Timeline_Decision_Blocked" xml:space="preserve"><value>Bloqué</value></data>
  <data name="Run_Timeline_Decision_Unknown" xml:space="preserve"><value>Inconnu</value></data>
  <data name="Run_Timeline_Outcome_Failed" xml:space="preserve"><value>échec</value></data>
```

The decision→label mapping (5 categories over 11 ordinals): `AutoApprovedStandingGrant`/`AutoApprovedPolicy`/
`GrantedByName` → **AutoApproved**; `ApprovedOnce`/`ApprovedAlways` → **Approved**;
`DeclinedByUser`/`CardCancelled`/`DeniedNotGranted`/`UnknownTool` → **Denied**;
`DeniedDestructiveFloor` → **Blocked**; anything else (including `Unknown` and any ordinal from a future build)
→ **Unknown**. Write it as a `switch` with an explicit `_ =>` arm, never an array index.

Terminology checked against the existing files: de says **Ausführung** for a run, fr **exécution**
(`Settings_Agent_MaxReplans_Description` in each). `{0}` is a `_localizationService.Format` arg, matching
`Run_*` precedent. No `&`, `<` or `>` in any value.

---

## 8. Test plan

Every behavioural change carries a **neutralization**. Restore with `git checkout --`, never by copying a
backup — a preserved older mtime makes MSBuild skip the recompile and the "restored" run silently exercises the
mutated binary (`05-…impl.md` §12 records that trap).

### 8.1 `tests/Pia.Wpf.Tests/Services/AgentTimelineServiceTests.cs` — NEW (CRLF)

Use the same in-memory/temp-file `SqliteContext` fixture `AgentRunServiceTests` uses.

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-STORE-1 | `Emit_ThenGetForRun_ReturnsTheRowsInSeqOrder` | 5 emits → `Seq` 1..5 in order, ids distinct. Must **await a drain** (see §8.6) before reading. | — |
| T-STORE-2 | `Emit_AllocatesSeqSynchronously_EvenUnderConcurrentCallers` | 200 emits from 8 threads → the 200 `Seq` values are exactly 1..200 with no duplicate | remove the lock around the slot → red (duplicate Seq) |
| T-SEQ-2 | `SeqContinuesAcrossAProcessBoundary` | emit 3, **dispose the service**, construct a second instance on the same DB, emit → `Seq == 4`. The parked-run-resumed-in-a-new-process case. | seed `NextSeq` from 0 instead of `MAX(Seq)` → red |
| T-STORE-3 | `Emit_NeverThrows_WhenTheStoreIsBroken` | dispose first, then `Emit` → no exception, no row | remove `Emit`'s try/catch → red |
| T-CAP-1 | `PerRunCapIsEnforced_AndTheTruncationIsRecordedOnce` | 600 emits for one run → exactly 501 rows, the last is `Kind == TraceTruncated`, and a further 100 emits add nothing | raise/remove the cap → red on the count |
| T-CAP-2 | `TheCapIsPerRun_NotGlobal` | 600 for run A + 5 for run B → B still has 5 | key the slot on nothing / globally → red |
| T-CAP-3 | `TheCapSurvivesARestart` | 501 rows in the DB, new service instance, emit → still 501 (`Count` seeded from the DB) | seed `Count` as 0 → red |
| T-PRUNE-1 | `PruneOlderThan_DeletesByTheRowsOwnCreatedAt` | rows at cutoff−1d and cutoff+1d, **both** runs' `CompletedAt` NULL → only the old row goes. **The D6 red-before-green.** | prune by a join on `AgentRuns.CompletedAt` → red (nothing is deleted) |
| T-PRUNE-2 | `PruneReturnsTheDeletedCount_AndNeverThrows` | count matches; a disposed service returns 0 | — |
| T-FK-1 | `DeletingTheChatCascadesTheTimelineAway` | insert chat → run → 3 events, **`await DrainAsync()`, assert 3 rows PRESENT**, then `DELETE FROM AssistantChats`, then assert 0 (R5's chain, extended). **The pre-assert is mandatory, not belt-and-braces:** `Emit` is fire-and-forget and the `RunId` FK is enforced, so an undrained queue means the delete cascades nothing *and* the later insert fails the FK — 0 rows, green, and the test proves nothing. | drop the `RunId` FK from the DDL → red on the post-delete assert |
| T-FK-2 | `AReplanThatReplacesStepsLeavesTheTimelineIntact` | 3 events with `StepId` set, then `ReplaceStepsAsync` with a fresh plan → all 3 rows still there, `StepId` unchanged (now dangling). **The D1 hazard, as a test.** | add `FOREIGN KEY (StepId) … ON DELETE CASCADE` → red (rows gone) |

### 8.2 `tests/Pia.Wpf.Tests/Unit/SqliteContextTests.cs` — extend

| # | Test | Asserts |
|---|---|---|
| T-DDL-1 | `EnsureSchema_CreatesAgentTimelineEvents_Idempotently` | open twice on the same file → no throw, the table exists |
| T-PRIV-2 | `AgentTimelineEvents_HasExactlyTheMetadataColumns` | `PRAGMA table_info(AgentTimelineEvents)` name set **equals** the exact expected **16**-name set. **Adding any column — an `ExtraJson`, a `Path`, an `ArgsHash` — fails this test rather than passing review.** |

> **CODE RIGHT, SPEC WAS WRONG — the column count.** This row said “15-name set”. The DDL §D1 prescribes
> **sixteen** columns (`Id`, `SchemaVersion`, `RunId`, `StepId`, `Seq`, `Kind`, `Surface`, `Decision`, `Outcome`,
> `ToolName`, `ToolClass`, `PluginId`, `ArgsChars`, `ResultChars`, `DurationMs`, `CreatedAt`) and the shipped test
> asserts sixteen. No column was added or dropped; the spec miscounted. Corrected in place so the next reader
> does not "fix" the test to match the prose.

### 8.3 Emission — extend the two existing gate suites (do not fork them)

> **CODE RIGHT, SPEC WAS WRONG — test placement.** This section says to extend
> `ChatSessionStateMachineTests`. That suite drives `RunTurnAsync`, which takes no `StepTurnSpec`, so no
> `AgentTimelineScope` can reach it and every fact below would have been vacuous there — the same wall Batch 04
> hit for the policy. T-EMIT-1/2/4/5/6/7 shipped in a new `ChatSessionTimelineTests` driving
> `RunStepTurnAsync` (modelled on `ChatSessionPolicyGateTests`), which is exactly why all of
> `ChatSessionStateMachineTests` still passes unmodified — the property §11's commit table demands. T-EMIT-3's
> ordinary-chat-turn half does live on the `RunTurnAsync` path, in that same new file.

`tests/Pia.Wpf.Tests/ViewModels/ChatSessionStateMachineTests.cs`:

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-EMIT-1 | `NGatedToolCalls_SomeDenied_ProduceNOrderedEvents_WithTheRightDecisions` | 4 gated calls (auto-approved, allow-once, always-allow, declined) → 4 rows in `Seq` order with `AutoApprovedStandingGrant`/`ApprovedOnce`/`ApprovedAlways`/`DeclinedByUser`. **The batch file's acceptance test, restated for gated calls (D2).** | remove one emit arm → red naming the missing decision |
| T-EMIT-2 | `ReadsEmitNothing` | a route returning a non-null `result` → 0 rows (D2) | emit for reads → red |
| T-EMIT-3 | `EveryEventCarriesTheStepId` | a step turn's rows all have `StepId == step.Id`; an **ordinary chat turn** (no spec, no scope) produces 0 rows | drop `StepId` from `BuildSpec` → red |
| T-EMIT-4 | `ACancelledCardIsRecordedAsCancelled_NotAsAUserDenial` | cancel the card → `CardCancelled`, not `DeclinedByUser`. §4.4's false-audit-statement guard. | emit after the `ToolDecision.Decline` mapping → red |
| T-EMIT-5 | `AThrowingToolIsRecordedAsError_AndTheExceptionStillPropagates` | `Outcome == Error` **and** the existing exception path is unchanged | swallow in the catch → the propagation half reds |
| T-EMIT-6 | `AnUnknownToolIsRecorded` | an unrouted name → `UnknownTool` + `NotExecuted` | — |
| T-EMIT-7 | `AFailingTimelineServiceDoesNotFailTheStep` | an `IAgentTimelineService` whose `Emit` throws → the turn completes normally and the tool still ran. **The failure-isolation guardrail, executable.** | remove `Emit`'s try/catch **and** make the gate `await` it → red |

`tests/Pia.Wpf.Tests/Unit/BackgroundAssistantTurnRunnerTests.cs`:

| # | Test | Asserts |
|---|---|---|
| T-EMIT-8 | `UnattendedDecisionsAreRecorded` | granted → `GrantedByName`+`Ok`; ungranted → `DeniedNotGranted`+`NotExecuted`; destructive external → `DeniedDestructiveFloor`+`NotExecuted` |
| T-EMIT-9 | `NoScope_MeansNoRows` | the SingleTurn path (`timeline: null`) → 0 rows |
| T-PARITY-1 | `LiveAndHeadlessRecordTheSameCall_DifferingOnlyInSurface` | the same tool + the same decision through both executors → rows equal on `Decision`, `Outcome`, `ToolName`, `ToolClass`; differ on `Surface`. **The executor-parity guardrail, executable.** | emit on one executor only → red |

### 8.4 `tests/Pia.Wpf.Tests/Services/AgentTimelinePrivacyTests.cs` — NEW (CRLF)

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-PRIV-1 | `NoCanaryFromArgsResultsOrPathsReachesAnyPersistedColumn` | drive a real gated call whose **arguments**, **result**, `Description` and `TargetPath` all contain the canary `"CANARY-9f3a1c"`; then `SELECT *`, stringify **every** column of **every** row, and assert none contains the canary. Deliberately **not** in the tool name (§3 permits names). | add `ArgsChars`→`ArgsJson`, or store `TargetPath` → red |
| T-PRIV-3 | `ArgsAndResultCharsAreCapturedPreTokenization` | with PII tokenization on, `ResultChars` equals the **inner** handler's result length, not the tokenized one (D8/R3) | move the capture to `AiClientService:398` → red |

### 8.5 Vocabulary, retention, and the render surface

| # | Test | File | Asserts |
|---|---|---|---|
| T-VOCAB-1 | `EveryToolGateDecision_IsEitherEmittedOrDocumentedAsNotEmitted` | `Architecture/AgentTimelineVocabularyTests.cs` (**new**) | reflect `ToolGateDecision`; each member ≠ `Unknown` is in the emitted set **or** in `NotEmittedByDesign`. Adding a decision to 04 without deciding 03's handling **fails**. |
| T-VOCAB-2 | `PersistedTimelineEnumsStartAtUnknownZero_AndNeverCollide` | same | `AgentTimelineEventKind` + `AgentTimelineOutcome`: `Unknown == 0`, no duplicate values |
| T-RET-1 | `RetentionCleanup_PrunesTheTimelineWithTheSameCutoff` | `Services/AssistantChatRetentionServiceTests.cs` (extend or new) | `PruneOlderThanAsync` received **once** with the same `cutoff` `EvictOlderThanAsync` got |
| T-RET-2 | `RetentionCleanup_SkipsThePruneWhenHistoryIsDisabled` | same | `ChatHistoryEnabled = false` → `DidNotReceive` |
| T-RET-3 | `AFailingPruneDoesNotStopTheTimer` | same | `PruneOlderThanAsync` throws → `RunCleanupAsync` returns normally (R18's outer `try` covers it — assert it, do not assume it) |
| T-UI-1 | `TimelineLoadsOnFirstExpandOnly` | `ViewModels/RunProgressViewModelTimelineTests.cs` (**new**) | `GetForRunAsync` `Received(0)` before expand, `Received(1)` after, still `Received(1)` after collapse+re-expand |
| T-UI-2 | `TimelineIsNotLoadedByRunChanged` | same | 5 `RunChanged` raises → `GetForRunAsync` `Received(0)`. D7's "no live projection". |
| T-UI-3 | `EveryDecisionOrdinalMapsToALabel_IncludingUnknownAndOutOfRange` | same | `[Theory]` over **all 12 ordinals (0–11)** — better, drive it from `Enum.GetValues<ToolGateDecision>()` so a 13th member cannot be missed the way 11 was — **plus** `(ToolGateDecision)99` → a non-empty label, never a throw. The append-only render guarantee. |
| T-UI-4 | `ATruncatedTraceSetsTheNote_AndIsNotRenderedAsARow` | same | a `TraceTruncated` row → `IsTimelineTruncated` true, `Timeline.Count` excludes it |
| T-UI-5 | `TimelineRowsCarryNoPathAndNoPayload` | same | `TimelineRowViewModel`'s public property set is exactly the 5 projected names — a reflection assert, so a later `FilePath` property fails here |
| — | `LocalizationTests` (existing) | — | catches all 9 `loc:Str` keys and their en/de/fr parity — no new test needed |
| — | `DiRegistrationTests` (existing) | — | `IAgentTimelineService` lives in `Pia.Services.Interfaces`, so it **must** be registered or this goes red. Expect it to be the first thing that fails if the `Bootstrapper` line is forgotten. |

### 8.6 One testing hazard the implementer must handle deliberately

`Emit` is fire-and-forget (D7), so **every** test that emits and then observes must synchronize — and
"observes" includes *mutating* (T-FK-1 deletes the chat, T-CAP-3 re-opens the DB). Do **not** sleep. Expose
`internal Task DrainAsync()` on `AgentTimelineService` (the serial writer's tail task, or a
`Channel.Reader.Completion`-style barrier), reachable through the existing
`InternalsVisibleTo Pia.Wpf.Tests` (`Pia.Wpf.csproj:69`), and `await` it. A `Task.Delay`-based test here would
be a second `SafeFireAndForget_SlowTask_DoesNotBlock`-class flake, and this batch adds ~15 tests that would all
have it.

**A drained-but-unasserted precondition is the vacuity trap for this whole file.** Any test whose expected
result is *"zero rows"* — T-EMIT-2, T-EMIT-9, T-FK-1, T-UI-2 — passes for free if the emission never happened.
Each of those must either assert a **non-zero** control case in the same test (T-FK-1, T-EMIT-2) or pair with a
sibling that proves the same code path does emit (T-EMIT-9 ↔ T-EMIT-8, T-UI-2 ↔ T-UI-1). State the pairing in
each test's comment.

---

## 9. Manual-smoke debt (no automated coverage exists)

1. ~~**The `Expander`'s three `Binding` paths and the `loc:Str` header.**~~ **PREMISE DISPROVED — mostly
   covered now.** This item claimed "no test parses `RunProgressPanel.xaml`". False:
   `AssistantView.xaml` places `<assistant:RunProgressPanel>` as a **plain element** with no `Template`
   ancestor, so `AssistantView.InitializeComponent()` constructs the panel and runs its own
   `InitializeComponent()` — the Expander's non-deferred markup, its `Header` and its two `TextBlock`s have been
   parsed by the existing `AssistantViewParseTests` since they landed. The genuinely uncovered half is the
   **deferred row template**. The review fix pass wrote that fact
   (`RunProgressPanel_RendersATimelineRow_WithItsStepOutcomeAndDecision` — `LoadContent()` over the real
   `ItemTemplate`, pinning `TimeLabel`/`StepLabel`/`ToolName`/`OutcomeSuffix`/`DecisionLabel` plus
   `HasNoTimeline`) and then **withdrew it**: it passes on its own but raised the full-gate failure rate from
   0/3 to 2/3 by amplifying a pre-existing `WpfStaHost` fragility (a 60 s timeout inside `Pump()`, victim =
   whichever test pumps next; it fires without this fact too). **So this item stays open**, narrowed: the row
   template's five paths, the `loc:Str` header (which binds `Header`, invisible to a logical walk — the same
   limitation the parse suite records for its 18 non-`Text` usages), and the end-to-end "rows appear for a real
   run" check. Re-landing the fact needs the host fixed first.
2. ~~**Every `StaticResource` in the new `DataTemplate` resolves.**~~ **DOES NOT APPLY to the template that
   shipped.** The row `DataTemplate` contains **no `StaticResource` at all** — only `DynamicResource` (which
   yields `null` rather than throwing) and `Binding`. The template-instantiation hazard this item was written
   against therefore has no instance here. Retained only as the rule for the NEXT change to this file: a
   `StaticResource` added inside the template throws when a user first opens the expander, so either avoid one
   or land the item-1 fact first.
3. **A real headless run's trace, after a restart.** Launch a background run, let it park at its budget, **quit
   and relaunch the app**, click *Continue*, then expand the trace. The rows from both segments must be present
   and in order with no duplicate positions — the live proof of T-SEQ-2's cross-process `Seq` seeding.
4. **A real MCP server.** Nothing in the suite exercises a live `McpPluginToolHandler` route, so
   `ToolClass.External` is only ever faked. Confirm a real external tool's row shows the right class and the
   right decision.
5. **The panel does not stutter during a run.** D7 claims the emit is off the critical path and off the UI
   thread. A run with many tool calls is the only way to see whether the spinner and streaming stay smooth —
   watch for the composer or the step list hitching at each tool call.
6. **Prune actually runs.** The retention timer's first tick is 5 s after startup (R18); with
   `ChatHistoryRetentionDays` temporarily set to 1 and a hand-aged row, confirm the `Information` line reports a
   non-zero delete and the row is gone.
7. **DE/FR** render without clipping, especially `Run_Timeline_Decision_AutoApproved`
   (*"Automatisch freigegeben"* / *"Approuvé automatiquement"*) in the narrow right-hand column.

---

## 10. Guardrails, instantiated for this batch

- **Failure-isolated bookkeeping.** `Emit` is `void`, wraps its own body, and is called with **no** `await`;
  the serial writer wraps each row; `PruneOlderThanAsync` never throws and the retention caller is already
  inside a `try` (R18). No emission path can fail a step, and no gate arm changes its return value because of
  one. T-EMIT-7, T-STORE-3, T-RET-3.
- **No interactive regression.** The gate's `SetState(WaitingForTool)` → `finally` → `Running` bracket, the
  card-before-execute ordering and `WaitForUserDecisionAsync` are untouched; the emit calls are added **after**
  each terminal decision, never inside the await. Nothing synchronous is added to the UI thread beyond a `lock`
  on an in-memory dictionary (D7, and R10 is why that distinction matters). All existing
  `ChatSessionStateMachineTests` facts must pass **unmodified**.
- **Executor parity.** Live emits via `StepTurnSpec.Timeline`; Headless via `RunExchangeAsync`'s scope, which
  covers `HeadlessTurnExecutor` **and** would cover the SingleTurn runner if it ever gained a run. T-PARITY-1
  asserts the rows match on everything but `Surface`. A feature on one executor only is a defect.
- **Off-thread `RunChanged` stays marshaled (G3).** The timeline service raises **no** event (D7), so it adds
  no marshaling obligation. `RunProgressViewModel`'s timeline load reuses the **existing** `_uiContext.Post`
  path. **On the VM's shape:** it captures `SynchronizationContext.Current` (`:119`) rather than taking
  `IUiDispatcher`, and it is hand-constructed at `AssistantViewModel.cs:387` — **left as is, deliberately**.
  The guardrail's literal requirement (no `Application.Current`/`App.Current.Dispatcher` in a ViewModel) is
  already satisfied, this VM was outside Batch 12's four, and converting it is a separate refactor with its own
  test fallout. `git grep "Application\.Current" -- src/Pia.Wpf/ViewModels/` must keep returning nothing.
- **Append-only persisted enums and ordinals.** `AgentTimelineEventKind` and `AgentTimelineOutcome` start at
  `Unknown = 0` and are never renumbered; Batch 04's three enums are stored as ordinals and **not** modified
  here. Every read path renders an unknown ordinal as *unknown* and never throws (T-UI-3, T-VOCAB-2).
  `SchemaVersion` on the row defaults to 1 so a future shape change is detectable rather than misread.
- **Privacy-first logging and storage.** §3 is the contract: ids, counts and the tool name only. `Emit`'s own
  log line (if any) is `Debug`-level with run id + `Seq` + tool name; the writer's failure log carries the
  exception **type**, never a provider or tool payload. T-PRIV-1 and T-PRIV-2 are the enforcement.
- **A new user-visible string lands in all three resx files** — 9 keys × 3 files, real DE and FR (§7.1).
  `ViewStrings.Designer.cs` stays untouched.
- **Code style.** 4-space C#, `_camelCase` fields, `var` for apparent types, `[ObservableProperty]`, logic in
  the ViewModel not the View, namespaces `Pia.*`. New `.cs` files **CRLF**.

---

## 11. Commit plan (each independently buildable and green)

| # | Commit | Contents | Green means |
|---|---|---|---|
| 1 | `Timeline: an append-only per-run tool-decision store` | the DDL, `AgentTimelineEvent` + 2 enums, `IAgentTimelineService` + `AgentTimelineScope`, `AgentTimelineService` (Seq, cap, serial writer, prune, `DrainAsync`), the `Bootstrapper` line; T-STORE-*, T-SEQ-2, T-CAP-*, T-PRUNE-*, T-FK-*, T-DDL-1, T-PRIV-2, T-VOCAB-2 | nothing calls it yet → the existing suite is untouched; `DiRegistrationTests` proves the registration |
| 2 | `Timeline: both executors carry a per-step sink to the gate` | `StepTurnSpec.StepId`/`.Timeline`, `LiveTurnExecutor` + `ChatSessionManager`, `HeadlessTurnExecutor`, the trailing params on `RunModelExchangeAsync` / `RunExchangeAsync` / both handlers — **plumbing only, no emit calls** | `LiveTurnExecutorPlannedRunTests`, `ChatSessionStepTurnTests`, `HeadlessTurnExecutorTests`, `ChatSessionStateMachineTests` and `BackgroundAssistantTurnRunnerTests` all pass **unmodified** — which holds only because every new parameter, **`LiveTurnExecutor`'s ctor argument included**, is trailing and defaulted (§6, R12). If one needs an edit, a parameter was made required; fix the parameter, not the test. |
| 3 | `Timeline: both gates record one event per gated tool call` | the 6 + 4 emit calls, the `Stopwatch` bracket, the rethrowing `Error` catch; T-EMIT-*, T-PARITY-1, T-PRIV-1, T-PRIV-3, T-VOCAB-1 | the 18 pre-existing gate facts still pass unmodified |
| 4 | `Timeline: prune the trace with chat retention` | `AssistantChatRetentionService`; T-RET-* | — |
| 5 | `Timeline: a read-only tool-activity trace on the run panel` | `RunProgressViewModel`, `RunProgressPanel.xaml`, `AssistantViewModel`'s construction site (`:387`), 9 resx keys ×3; T-UI-* | `LocalizationTests` green, and the existing `RunProgressViewModelTests` pass **unmodified** — the ctor's new `IAgentTimelineService?` is trailing-optional for exactly that reason (§6). **Droppable**: cutting it leaves the store and the emission complete, and the acceptance met minus the word "exposes". |

---

## 12. Open questions (none blocking)

1. **The trace is device-local** (§0.4). A user with two machines gets a partial audit history and no
   indication that it is partial. Syncing it needs a `SyncAgentRun` DTO that does not exist and a merge policy
   for `Seq` across devices — a Phase-3-sized question. Until then the roadmap must say "per-device".
2. **A `Planned`-run chat is still never evicted** (§0.2). D6 bounds the *timeline*, not the runs and steps
   themselves. `AgentRuns`/`AgentSteps` for `Planned` runs are still retained forever by design (§16 R17), so
   this batch reduces the growth rate of the newest table and leaves the older two alone.
3. **No structured step-result signal.** Step success is still `!string.IsNullOrWhiteSpace(exchange.Visible)`
   (`HeadlessTurnExecutor.cs:256`), so a step that politely explains its own failure records `Done`. The
   timeline now shows *which tools were denied*, which is a strong hint at why such a step "succeeded" — but it
   is a hint for a human, not a signal for the replan loop. Roadmap's "Deliberately open" item, unchanged.
4. **A tool call in flight when the process dies leaves no row** (D2). Accepted; the run dies with it. A
   two-row (decision, then outcome) design would close it at double the rows and double the cap pressure —
   revisit only if a real crash investigation is blocked by it.
5. **`Round` is not recorded** (D8), so the trace cannot show *"these three calls were in the same round"*.
   Recoverable later only by touching the tool-handler delegate signature, which is why the decision is written
   down rather than left implicit.
6. **`ExecutePendingActionAsync` is still dead surface** and a handler can still `Execute()` on an error path
   before any gate (04 §13.4). Such a call produces **no** timeline row, so the trace is a record of *gated*
   calls, not of every effect. Stated, not hidden.
