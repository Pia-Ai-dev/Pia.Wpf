# Batch 07 — Sub-agents / multi-persona · IMPLEMENTATION SPEC

Executable spec derived from [`07-subagents-multipersona.md`](07-subagents-multipersona.md) and
[`phase3-workflow-plan.md`](phase3-workflow-plan.md) (§1 decisions, §2 seam map, §4 risk register, §5 work
groups G6–G10, §7 standing constraints), plus a **full re-read of every seam it touches** on
`feature/agent-run-spine` at `53cd552`. Branch: `feature/agent-run-spine`. **Design step only — no production
code was written for this document.**

Batch 07 covers **G6, G7, G8, G9, G10** — §3 through §7 below, one section per group, in that order. Batch 06
(G1–G5) is a **separate, earlier** invocation and lands **first**, on the same working tree.

> **READ THIS IF YOU ARE PICKING THIS UP COLD.** This spec was authored *while Batch 06 was being designed in
> parallel*. The tree you will meet is **not** the tree measured here: it already carries a run-aware
> file-tool root, an `IRunWorkspaceService` with two provisioning modes (worktree | copy), a promotion step
> inside the terminal settle, and isolated interactive runs. Every place that matters is flagged
> **`06-DEPENDENT`** below. Re-read the flagged file before you write the line; do **not** re-derive the rest.
>
> **`06-run-workspace-isolation.impl.md` landed on disk before this file was finalized and the two were
> reconciled.** Its **§0.7 is the authoritative inventory of what 06 changed under 07** and its **§13.4** is
> written *to this batch's builder* about the child workspace. Read both. The reconciled facts are folded in
> here at §3.4, §3.5, §5.5, §7.3 and §7.6 — but if 06's own §0.7 and this file ever disagree, **06's §0.7
> wins**, because it was measured against the tree 06 actually left.
>
> **Each group section (§3…§7) opens with a `BUILDER NOTE (Gx) — from the reconciler` block. Read your
> group's note before you read the group.** Those notes are the reconciled record of everything Batch 06
> changed underneath this batch — ctor parameter ordering, the two `SafePromote` call sites, the second
> "terminal" predicate, the shifted resx and XAML line numbers — and they correct three statements in the
> body of this document that were written before 06 landed (§4.4's "7th ctor param", §4's "06-DEPENDENT: no",
> and §7.2's `LaunchCoreAsync`/`LaunchChildAsync` signatures, which are now reconciled in place).
>
> **START AT §0.10.** It is a later pass than the rest of this file: an **anchor audit re-measured against the
> live tree on 2026-07-31**, when 06's G1–G3 were committed, its G4 was uncommitted in the working tree and its
> G5 had not started. §0.10 says which `BUILDER NOTE` claims are now **measurements** (with the commit that
> made them true), which are **still predictions** waiting on 06's G5, and which line numbers in this document
> were **wrong all along** because they were inherited from `phase3-workflow-plan.md` §2. **§0.10 wins over any
> line number elsewhere in this file, including the `BUILDER NOTE`s.** And read its one rule before anything
> else: *every line number here is provenance, not an address — grep the symbol.*

Gate for the implementing agent:

```
dotnet build -t:Rebuild -v:n                 # 0 Error(s), 0 Warning(s)
dotnet build -t:Rebuild -c Release -v:n      # 0 Error(s), 0 Warning(s)
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
                                             # failed: 0   (2424 total / 0 failed at df0841a — BEFORE Batch 06,
                                             #  which added tests in all five of its commits. The bar is
                                             #  `failed: 0` measured by stash → rerun on the tree you were
                                             #  handed; the total is NOT 2424 any more.)
```

Read the warning count off MSBuild's `N Warning(s)` summary line — at `-v:n` every warning prints twice, so
grepping the log double-counts. Confirm the rebuild was genuine by counting `CoreCompile`/`Csc` invocations
(expect 4: `Pia.Shared`, `Pia.Wpf`, the `Pia.Wpf_<hash>_wpftmp` XAML pass, `Pia.Wpf.Tests`). This batch is
test-heavy and **186 of the historical 194 warnings were xUnit analyzer warnings in the test project** —
`Assert.Equal(0, x.Count)` → xUnit2013 (use `Assert.Empty`), `.Result`/`.Wait()` in a test body → xUnit1031.
New tests must add **zero**. Never pass `--nologo` to `dotnet test`.

**Two known intermittents — re-run the class isolated before calling either a regression:**
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` (wall-clock assumptions, low single-digit %) and
`AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
(probabilistic detection window). The bar is `failed: 0` measured by **stash → rerun**, never read off a past
count.

---

## 0. Corrections — read this first

The 07 batch file was written 2026-07-29 and describes code "as built". This repo has a **recorded history of
logged hazards turning out to be premise errors** (a `SQLITE_BUSY_SNAPSHOT` hazard that did not exist; a
transitive-package exclusion that would have made CVE reporting *worse*). Treat prose as a hypothesis. **The
code wins.** Nine corrections, all measured — **plus §0.10, which is not a correction but a later re-measurement
of every anchor in this file against a tree that had moved.** If you read only one subsection, read §0.10.

### 0.1 CODE RIGHT, SPEC WRONG — there are **FOUR** fixed points per run, not two

`phase3-workflow-plan.md` §2 ("07 — persona and provider are fixed once per run") says: *"The only two fixed
points are the orchestrator signature and that cached field."* Measured, there are **four**, and the two the
plan misses are the ones that carry the actual multi-persona content:

| # | Fixed point | Headless | Live |
|---|---|---|---|
| 1 | the run-level `(Persona, AiProvider)` pair | `AgentRunOrchestrator.RunAsync` params (`:35-42`) | same |
| 2 | the cached provider | `_provider` set in `BeginRunAsync` (`:139-154`), read at `:299` | `_provider` field (`LiveTurnExecutor.cs:21`), read in `BuildSpec` (`:143`) |
| 3 | **the cached persona** | `_persona` (`:134-135`), stamped onto every persisted message as `SyncMessagePersona` (`:332`) | `_persona` (`PersonaAttribution`), stamped onto every step's `AssistantMessage.Persona` (`ChatSession.cs:653`) |
| 4 | **the cached `AssistantTurnSetup` — i.e. the SYSTEM PROMPT and the tool list** | `_setup = _promptComposer.PrepareTurn(_persona, _provider, …)` (`:157-158`) | handed in as `request.TurnSetup`, built once at `ChatSessionManager.cs:681`, read as `_turnSetup.SystemPrompt` in `BuildSpec` (`:141`) |

**Point 4 is the headline.** `AssistantTurnSetup` is `(SystemPrompt, Tools, SupportsTools, WebSearchActive)`
(`IAssistantPromptComposer.cs:12-16`) and `PrepareTurn` takes the **persona** as its first argument. A
per-step persona that does not re-compose the turn setup changes only the *attribution glyph* and the
*provider* — the model still runs on the **run persona's system prompt**, which is the entire substance of
"assign steps to distinct personas". A builder who changes only `_provider` and `_persona` ships a feature
that looks right in the panel and is inert in the model. G6's cost centre is the re-composition, not the
provider.

### 0.2 CODE RIGHT, SPEC WRONG — `AgentRunOrchestrator` needs **no change at all** for G6

`phase3-workflow-plan.md` §5 G6 says: *"orchestrator resolves `(Persona, AiProvider)` **per step** instead of
closing over one run-level pair."* That is the wrong layer. `AgentRunOrchestrator.ExecuteStepAsync` already
hands the executor **the step itself** (`AgentRunOrchestrator.cs:160`:
`await executor.ExecuteStepAsync(run, step, ctx, cts.Token)`), and `AgentStep.AssignedPersonaId` is a real,
round-tripped member (`AgentStep.cs:27`, `AgentRunService.cs:467` insert / `:622` read). Both executors
therefore already hold everything they need: the step, plus their own persona/provider/setup fields. Per-step
resolution is **executor-local**.

Consequences, all wanted:

- `RunAsync`'s signature is **untouched**, so the 13 positional `new AgentRunOrchestrator(runs, planner,
  verifier, logger)` constructions in tests are untouched (`AgentRunOrchestratorTests.cs:835`, `:945`;
  `HeadlessTurnExecutorTests.cs:108`, `:227`, `:309`, `:382`; `LiveTurnExecutorPlannedRunTests.cs:197`,
  `:228`, `:299`, `:358`, `:395`, `:425`, `:454`).
- The run-level `persona`/`provider` params stay exactly what they are: the inputs to **plan / replan /
  verify** (`:91`, `:180`, `:212`, `:221`), which are deliberately run-level (one plan, one critic verdict for
  the whole goal — see D4).
- **The 06/07 collision the plan asked the reconciler to own does not land in G6. It lands in G10**, which is
  the only group in this batch that edits `AgentRunOrchestrator.RunAsync`. Sequence accordingly.

### 0.3 CODE RIGHT, SPEC WRONG — R8's premise is false: the record change breaks **neither** fake

`phase3-workflow-plan.md` R8 / §3.6: *"Adding `ParentRunId` to `AgentRunCreateRequest` breaks two hand-written
full-surface fakes — a **compile failure**."* Measured: **it does not.**

`AgentRunCreateRequest` is a **positional record** declared in `IAgentRunService.cs:16-23`. The two fakes
(`AgentRunOrchestratorTests.cs:142` `FaultyRunService`, `BackgroundAssistantTurnRunnerRunSpineTests.cs:290`
`ThrowingAgentRunService`) implement the **interface**, whose `CreateAsync(AgentRunCreateRequest request, …)`
signature does **not** change when a member is appended to the record. And all 45 construction sites in
`src/` + `tests/` pass the first three arguments positionally and everything else **by name**
(`Goal:`, `PolicyJson:`) — verified by `git grep -n AgentRunCreateRequest`. A trailing defaulted member is
invisible to every one of them.

The spec confusion is understandable: the record is declared *in the file named* `IAgentRunService.cs`. The
**file** changes; the **interface** does not.

**The fakes still must be migrated in G9's commit** — but for the correct reason: G8 and G10 add **three new
members to `IAgentRunService`** (D9), and *that* is the compile failure. Budget it there, and say so in the
commit message, or the next reader re-derives the wrong premise.

### 0.4 CODE RIGHT, SPEC WRONG — the "waiting on N children" marker does not need `ExtraJson` at all

§3 correction 8 frames G8 as: the new state *"must carry 'waiting on N children' through a claim that
unconditionally nulls `ExtraJson`"* (`AgentRunService.cs:321`). That is a real constraint on a design nobody
has to adopt. **The child rows ARE the marker.** With `ParentRunId` written (G9) and
`IX_AgentRuns_ParentRunId` in place, *"how many children is this parent still waiting on"* is
`SELECT … WHERE ParentRunId=@p AND State < 5` — a fact in the database, not a blob on the parent. So:

- nothing this batch writes to `AgentRuns.ExtraJson`;
- `TryBeginResumeAsync`'s unconditional `ExtraJson=NULL` is a **non-issue**, not an obstacle;
- the count is always *current*, whereas an `ExtraJson` counter would have to be decremented transactionally
  by every settling child — a second source of truth and a lost-update race.

### 0.5 The appended state trips the **one** ordinal-range comparison in production

`AgentRunState` is `Planning=0 … Cancelled=7` (`AgentEnums.cs:30-40`); `Paused(4)` is **reserved for Batch
08** (`08-live-steering.md:12`, `RunProgressViewModel.cs:225`). G8 therefore appends **`WaitingForChildren =
8`**. A repo-wide sweep for range comparisons over `AgentRunState`
(`git grep -nE "State >=|State <|>= AgentRunState|< AgentRunState" -- src/Pia.Wpf`) returns **exactly two**:

1. `AgentRunService.cs:713` — `var terminal = state >= AgentRunState.Completed;` inside `ApplyLedgerClock`.
   **`WaitingForChildren = 8 ≥ 5` ⇒ a waiting parent is treated as TERMINAL by the ledger clock**, which
   drops its open work segment and freezes `WallClockMs`. Fixed by D8c (explicit set, not a range).
2. `AgentRunService.cs:357` — `WHERE State < @Terminal` with `@Terminal = (int)WaitingForInput = 3`, i.e. the
   startup sweep. `8 < 3` is false ⇒ **a waiting parent survives the sweep for free.** That is the property
   §3.8 said had to be invented; appending above the terminal band delivers it by construction. The inline
   comment at `:352-354` enumerates "0..2 … 3/4 … 5-7" and must be corrected in the same commit — it is a
   load-bearing ownership comment in this repo.

Everything else that branches on `AgentRunState` is an explicit `is … or …` set (`AgentRunNotificationSurface.cs:69`,
`:86`; `ChatSessionManager.cs:183`, `:556-557`, `:574`) or a `switch` with a default arm
(`RunProgressViewModel.cs:218-230`, `:245-254`; `RunProgressConverters.cs:55-63`, `:76-84`, `:95-97`). Those
do not *break*; they silently **exclude** the new state. §5.4 enumerates each one, with a verdict.

### 0.6 CODE RIGHT, SPEC WRONG — the label converter's default arm makes a new state read "Completed"

`RunStateToLabelConverter` (`RunProgressConverters.cs:55-63`) ends `_ => "Run_State_Completed"`, with the
comment *"Completed + TruncatedCompleted both read Completed"*. A new `RunProgressState` member falling into
that arm renders a **parent that is still working as "Completed"** — the most expensive possible lie on this
panel. `RunStateToBrushConverter` (`:76-84`) defaults to `TextDefaultBrush` (harmless) and
`RunStateToSpinnerVisibilityConverter` (`:95-97`) is a positive `is Planning or Running` set (⇒ no spinner,
wrong but quiet). All three get explicit arms in G8, and the label mapping is **extracted to an
`internal static string LabelKey(RunProgressState)`** so a theory can pin it — the same seam
`RunProgressViewModel.DecisionLabelKey` (`:372`) already is, for the same reason.

### 0.7 The two pre-existing panel-attribution defects, confirmed

§3.9 is right, and here is the exact mechanism so nobody "fixes" the wrong half:

- `StepRowViewModel.AssignedPersonaId` is `Guid?` (`RunProgressViewModel.cs:495`);
  `PiaPersonaAvatar.PersonaIdProperty` is `typeof(Guid)` with default `Guid.Empty`
  (`PiaPersonaAvatar.xaml.cs:15-16`). Binding a `null` `Guid?` to a non-nullable DP fails the binding and
  leaves the DP at `Guid.Empty`.
- `PiaPersonaAvatar.xaml:13-16` forwards to `PersonaGlyph`, whose `UpdateGlyph()`
  (`PersonaGlyph.xaml.cs:55-68`) treats `Guid.Empty` as *not-Pia* → hides `PiaIcon`, shows `EmojiImage` with
  `Emoji = ""` — because `RunProgressPanel.xaml:66-68` **never binds `Emoji`**.
- Net effect today: **every step row draws an empty 20×20 shadowed box.** Both halves are needed; fixing only
  the `Guid?` mismatch yields an empty box with a working id.

### 0.8 CODE RIGHT, SPEC WRONG — the accent-colour **converter** already exists

§2 ("07 — panel attribution") says *"accent differentiation is net-new."* The **DP path** is net-new; the
brush conversion is not. `Converters/HexToBrushConverter.cs` already converts `#RRGGBB`/`#AARRGGBB` → a
`SolidColorBrush`, returns `Brushes.Transparent` for null/blank/unparseable, and is **already keyed globally**
at `App.xaml:78`. `Persona.AccentColor` is already `string?` hex (`Persona.cs:31-32`). So G7 adds one DP plus
one binding and introduces **no new resource lookup inside a `DataTemplate`** — which matters, because an
unresolved `StaticResource` inside a template throws at **template instantiation**, i.e. the first time a user
sees the row, and no test in the suite reaches that (`RunProgressPanel.xaml:82-87` says so in its own comment).

### 0.9 A parked child settles `handle.Completion` — a parent must not read that as "finished"

`IHeadlessRunLauncher.cs:52-58`: *"the returned `HeadlessRunHandle.Completion` settles when the run reaches a
terminal state **OR a budget pause** (`AgentRunState.WaitingForInput`, a non-terminal park)."* A parent that
`await`s `handle.Completion` and assumes terminality would mark its fan-out step **Done**, roll up a
**partial** ledger, and continue — while the child sits parked, resumable, and later resumes into a parent
that has already moved on. This is **not in the plan's risk register** and it is G10's most likely silent
failure. Closed by D13.

### 0.10 ANCHOR AUDIT — re-measured 2026-07-31 against the live tree, mid-Batch-06

**Provenance, so you can weigh every number in this file.** §0–§13 were measured at `53cd552` and then
reconciled against `06-run-workspace-isolation.impl.md` — which at that moment was a **spec, not code**. This
section is a **second, later pass over the same seams**, run while Batch 06 was mid-build. At the time of this
audit:

- **committed:** `70400aa` (06 G1 — guard carve-out + `RunContext.WorkspaceRoot` + the verifier root),
  `4092765` (06 G2 — the `Initialize` flip at both launcher call sites), `00198f6` (06 G3 — the
  worktree | copy provisioner);
- **uncommitted, in the working tree:** 06 **G4** (promotion in the terminal settle + the publish affordance),
  visible as edits to `AgentRunOrchestrator.cs`, `HeadlessRunLauncher.cs`, `RunWorkspaceService.cs`,
  `RunProgressPanel.xaml`, `RunProgressViewModel.cs`, `AssistantViewModel.cs` and all three `ViewStrings*.resx`;
- **not started:** 06 **G5** (interactive isolation + chip resolution).

**Later than the audit, added by the reconcile pass:** 06's G4 was **committed** as `3c28e84`
("Runs: promote a completed run's work, and offer to publish a failed one's"), so every A-row below that was
measured off the dirty working tree is now a committed fact, and the resx trio really does carry **six** new
`Run_*` keys (`Run_Publish_Failed` is the sixth — 06's B15 table has been corrected to match). G5 was still
outstanding at that point. Nothing in this section's substance changes; only its "uncommitted" provenance does.

**The one rule this section exists to give you: every line number in this document is provenance, not an
address. Grep the symbol.** Batch 06 rewrote or created 20 files and its G4/G5 moved more *after* this audit —
two files (`RunProgressViewModel.cs`, `RunProgressPanel.xaml`) demonstrably shifted **between two greps inside
this audit itself**. A number is only worth trusting for a file no batch has touched, and that list shrinks
every commit.

**Files Batch 06 churned** (`git diff --stat 53cd552 00198f6` plus the then-dirty working set) — treat every
anchor into these as provisional: `Bootstrapper.cs`, `Infrastructure/AssistantWorkspace.cs`,
`Infrastructure/SensitivePathGuard.cs`, `Services/AgentVerifier.cs`, `Services/AgentRunOrchestrator.cs`,
`Services/GitToolHandler.cs`, `Services/HeadlessRunLauncher.cs`, `Services/HeadlessTurnExecutor.cs`,
`Services/RunContext.cs`, `Services/Interfaces/IRunWorkspaceService.cs` (**new**),
`Services/RunWorkspaceService.cs` (**new**), `Controls/Assistant/RunProgressPanel.xaml`,
`ViewModels/RunProgressViewModel.cs`, `ViewModels/AssistantViewModel.cs`,
`Resources/Strings/ViewStrings{,.de,.fr}.resx`, and eleven test files. **`RunContext` lives at
`src/Pia.Wpf/Services/RunContext.cs`** — not under `Models/`, which is where a reader guesses.

#### 0.10.1 Predictions that are now MEASUREMENTS

Each row was a claim in this file's `BUILDER NOTE` blocks, sourced from 06's spec. Each is now read off the
tree. **Cite these, not 06's spec.**

| # | Claim | Measured 2026-07-31 | Verdict |
|---|---|---|---|
| A1 | 06 adds **no** ctor parameter to `HeadlessTurnExecutor`; `timelineService` stays last | `Initialize(string? workspaceRoot, IReadOnlyCollection<string> grantedWrites, AiProvider? providerOverride = null, RunAutonomyPolicy? policy = null)` at `HeadlessTurnExecutor.cs:113-125`; 06 changed the *argument value* at the launcher, not the signature | **CONFIRMED** (`4092765`). G6's resolver still appends after `timelineService`. |
| A2 | `BeginRunAsync` assigns `ctx.WorkspaceRoot` next to `ctx.WorkingSubpath` | `ctx.WorkspaceRoot = _workspaceRoot;` — `HeadlessTurnExecutor.cs:144`, the **only** production assignment in the tree | **CONFIRMED** (`70400aa`). |
| A3 | The verifier prefers the context over the ambient | `AgentVerifier.cs:213`: `var ambientRoot = ctx.WorkspaceRoot ?? TaskAmbient.Current?.WorkspaceRoot;`, and the ownership comment at `:269` is rewritten | **CONFIRMED** (`70400aa`). Phase 3 R2/R3 are closed. |
| A4 | `AgentRunOrchestrator` gains a trailing `IRunWorkspaceService? workspaces = null` | ctor is exactly `(IAgentRunService, IAgentPlanner, IAgentVerifier, ILogger<AgentRunOrchestrator>, IRunWorkspaceService? workspaces = null)`; field `_workspaces` at `:22`, assigned `:38` | **CONFIRMED**. G10's `IHeadlessRunLauncher? childLauncher = null` is therefore the **6th** parameter, after it (BUILDER NOTE G10 fact 4). |
| A5 | `SafePromote` sits on **two** terminal arms | `SafePromote` is `AgentRunOrchestrator.cs:450-476`; its call sites are exactly **two** — `:128` (the `PlanResult.Fallback` degrade arm, whose own comment says it settles *"in the opposite order to the main path"*) and `:266` (the main arm) | **CONFIRMED**. Both need §7.6's `run.ParentRunId is null` guard. |
| A6 | *(new — sharper than §7.6 as written)* what a child reaching `SafePromote` actually costs | `SafePromote` **also tears the workspace down**: `await _workspaces.TearDownAsync(run.Id, ct)` at `:470`, immediately after a successful `PromoteAsync`. `TearDownAsync` is keyed on the **run id it is handed** | **NEW MEASUREMENT.** An unguarded child does not merely double-promote — **it destroys the shared workspace while its siblings are still writing into it**, and in worktree mode that is a `git worktree remove`. §7.6 change 2 is a data-loss guard, not a tidiness guard. Put that in the comment. |
| A7 | `RunProgressViewModel` grows a publish parameter in 06 G4, and G7's persona map goes after it | ctor is now `(IAgentRunService, Guid, ILocalizationService, IAgentRunResumeService, ILogger, IAgentTimelineService? = null, IRunWorkspaceService? = null)` — **7 parameters**; the production site is `AssistantViewModel.cs:403-404` | **CONFIRMED**. G7's `IPersonaService?` is the **8th** and last. §4.4's "7th ctor param" is superseded — count the file. |
| A8 | Both launcher dispatch paths provision | `_workspaces.ProvisionAsync(run.Id, workingSubpath: null, ct)` at `HeadlessRunLauncher.cs:191` (launch) **and** `:367` (resume) — two separate methods, no shared core | **CONFIRMED.** There is **no `LaunchCoreAsync` in the tree**: §7.2's extraction is **07's own work**, and it must carry *both* blocks — which is why §7.6 change 3 is not optional. |
| A9 | `IHeadlessRunLauncher` is already registered, so `DiRegistrationTests` is unaffected | `Bootstrapper.cs:503`: `services.AddSingleton<IHeadlessRunLauncher>(sp => sp.GetRequiredService<HeadlessRunLauncher>());`; `IRunWorkspaceService` at `:501` | **CONFIRMED** (the anchor moved from `:498`). |
| A10 | `_slots` is waited inside the dispatch task and released only after `RunAsync` returns (§7.1's deadlock argument) | `_slots.WaitAsync` at `:237` (launch) and `:391` (resume); `if (acquired) _slots.Release();` at `:285` and `:440`, both in the dispatch `finally` | **CONFIRMED.** §7.1's `:199`/`:245` are the pre-06 numbers; the argument itself is unchanged. |
| A11 | §0.9 — `Completion` settles on a budget pause too | `IHeadlessRunLauncher.cs`, still verbatim: *"settles when the run reaches a terminal state **OR a budget pause** (`AgentRunState.WaitingForInput`, a non-terminal park)"* | **CONFIRMED.** D13 stands. |

#### 0.10.2 Still PREDICTIONS — 06's G5 had **not** landed when this was audited

These three are the Live half of §3.5 and of 06's own promotion story. **Verify before you write the line; if
G5 was cut, they never arrive.**

| # | Claim in the `BUILDER NOTE (G6)` block | Measured 2026-07-31 | What to do |
|---|---|---|---|
| B1 | *"`LiveTurnExecutor` gained trailing `string? workspaceRoot = null`"* | **Not present.** The ctor is `(ChatSession, Func<ChatSession,bool>, PersonaAttribution, AiProvider, AssistantTurnSetup, bool tokenizationEnabled, RunAutonomyPolicy? policy = null, IAgentTimelineService? timeline = null)` — 8 parameters, `timeline` last (`LiveTurnExecutor.cs:32-52`; construction at `ChatSessionManager.cs:788-790`) | **Count the parameters in the file.** G6 adds nothing to this ctor anyway (§3.5 resolves through `ChatSessionManager`'s own services), so no part of G6 depends on B1 being true. |
| B2 | *"`StepTurnSpec` has a trailing `string? WorkspaceRoot = null` and `BuildSpec` sets it — keep passing `WorkspaceRoot:`"* | **Not present.** `StepTurnSpec`'s trailing members are `UseGoalVerbatim = false`, `Policy = null`, `Timeline = null` (`IAgentTurnExecutor.cs:34-39`) | If it is there when you arrive, keep passing it — the note's warning is correct, because the member would be trailing and defaulted, so dropping it **compiles** and silently un-isolates every interactive step. If it is absent, do not invent it. |
| B3 | `SafePromote`'s doc comment claims *"it reads `ctx.WorkspaceRoot`, which **BOTH** executors assign"* (`AgentRunOrchestrator.cs:445`) | **Only `HeadlessTurnExecutor` assigns it** — `git grep "ctx.WorkspaceRoot" -- src/Pia.Wpf` returns one production write (A2) | **CODE RIGHT, COMMENT AHEAD OF ITSELF.** The comment is written for the tree G5 will leave. Until G5 lands, `SafePromote` is a **no-op for interactive runs** (`string.IsNullOrEmpty(ctx.WorkspaceRoot)` returns early). Do not "fix" the comment and do not touch the guard; if you land after G5, re-grep and the comment is simply true. |

#### 0.10.3 Corrected anchors — inherited from `phase3-workflow-plan.md` §2, and wrong in the tree

These are **not** 06 drift. The plan's §2 carried them, this file quoted them, and they never matched. The
substance of every claim below survives; only the address was wrong.

| Cited as | Actually | Used by |
|---|---|---|
| `RunProgressViewModel.cs:495` for `StepRowViewModel.AssignedPersonaId` | `public Guid? AssignedPersonaId { get; init; }` inside `public sealed partial class StepRowViewModel` — measured `:642`, and it **moved during this audit** | §0.7, R23, §4.3 — **use the symbol** |
| `RunProgressViewModel.cs:514` for `From(AgentStep)` | `public static StepRowViewModel From(AgentStep step) => new()` with `AssignedPersonaId = step.AssignedPersonaId` in its initializer — measured `:664`, also moving | §0.7, T-VM-* |
| `RunProgressViewModel.cs:225` for *"`Paused(4)` is reserved for Batch 08"* | the reservation is the state-map arm `AgentRunState.Paused => (RunProgressState.Paused, false),   // reserved user pause (Phase 4)` — measured `:379`, inside the `AgentRunState → (RunProgressState, bool)` switch | §0.5, D8 |
| `RunProgressViewModel.cs:163-179` / `:181-193` / `:218-230` for the ctor / `OnRunChanged` / the state map | ctor `:205`; `_uiContext = SynchronizationContext.Current ?? new SynchronizationContext();` `:222`; `OnRunChanged` `:227`; the marshaling `_uiContext.Post` `:238`; `Project` `:349`; `SyncSteps` `:563`; `DecisionLabelKey` `:526` — all in a file 06 is editing | R21, R22, R23, §4.4, §7.7 |
| `RunProgressPanel.xaml:66-68` for the avatar binding | `<chat:PiaPersonaAvatar Grid.Column="1" Width="20" Height="20" … PersonaId="{Binding AssignedPersonaId}" />` — measured `:91-93` after 06 G4 inserted the publish/branch `TextBlock`s above it | §0.7, §4.5 |
| `RunProgressConverters.cs:55-63` for the label switch | `RunStateToLabelConverter` is at `:49`; its switch is `:55-62` with the default arm `_ => "Run_State_Completed"` at **`:61`**; `RunStateToBrushConverter` `:72` (default `:83`); `RunStateToSpinnerVisibilityConverter` `:93` | §0.6, T-CONV-* |
| `Bootstrapper.cs:498` for the launcher registration | `:503` (see A9) | §7.2 |
| `AssistantViewModel.cs:397-398` for the `RunProgressViewModel` construction | `:397` is `private void SyncRunProgress(Guid? runId)`; the construction is `:403-404` | R21, R12 |
| `AgentRunService.cs:352-354` for the sweep's ordinal comment | the comment block is `:352-356`; the `UPDATE … WHERE State < @Terminal` is `:357`, exactly as cited | §0.5, D8, T-ST-4 |

#### 0.10.4 Re-confirmed unchanged (do not re-derive)

- **§0.5's two range comparisons are still the only two.** `AgentRunService.cs:713` `var terminal = state >= AgentRunState.Completed;`
  and `:357`'s `WHERE State < @Terminal` with `@Terminal = (int)AgentRunState.WaitingForInput`. `AgentRunState`
  is still `Planning 0 … Cancelled 7` (`AgentEnums.cs:30-40`), so **`WaitingForChildren = 8`** is still the
  correct appended ordinal and still survives the sweep by construction.
- **§0.6 holds verbatim**: the label converter's default arm really is `_ => "Run_State_Completed"`, with the
  comment *"Completed + TruncatedCompleted both read Completed"*.
- **§0.7 holds in every particular, re-read control by control**: `PiaPersonaAvatar.PersonaIdProperty` is
  `typeof(Guid)` (`PiaPersonaAvatar.xaml.cs:15`), its CLR property is `public Guid PersonaId` (`:21`), and it has
  an `EmojiProperty` (`:18`) / `public string Emoji` (`:27`) that **the panel never binds**;
  `PersonaGlyph.PersonaIdProperty` is `typeof(Guid)` (`PersonaGlyph.xaml.cs:21-22`) with `EmojiProperty`
  (`:25-26`); `StepRowViewModel.AssignedPersonaId` is `Guid?`. **The always-empty 20×20 box is real and both
  halves of the fix are needed.**
- **R24** — `AppSettings.ModePersonaDefaults` (`:86`) plus `GetPersonaForMode` (`:310-311`) /
  `SetPersonaForMode` (`:313-318`), where passing `null` does `Remove` — the exact precedent §4.1 copies,
  including T-SET-3's "setting `[]` removes the key".
- **R28** — the Agent-runs tab order is unchanged: `Settings_Agent_Planning_Section_Header` at
  `AssistantView.xaml:411`, the Batch 04 Autonomy block at `:424-426` (its comment records *why* a global toggle
  goes **before** the scheduled block), `<!-- Scheduled / background-run budget -->` at `:439`. 06 G4 edited the
  three `.resx` files but **not** this view.
- **R29/R30/R31** — `ToolAutonomyRuleTests.GateFiles` is still exactly the three files with the counts
  `(1,1,1) / (1,1,0) / (1,1,1)`; `AgentRunBracketTests.ExecutorContracts` is still the two interfaces;
  `NamingConventionTests.allowedSuffixes` still contains `Resolver` (so `StepPersonaResolver` passes) and now
  also `Provisioner`, `Session`, `Resampler` (06 G3 moved those three off the exempt-NAMES list) — and still has
  no `Coordinator`, `Promoter`, `Manager` or `Pool`.
- **R35** — `AgentRuns` still has exactly four indexes (`ChatId`, `State`, `UpdatedAt`, `TriggerRef` —
  `SqliteContext.cs:306-309`) and **none on `ParentRunId`**, and the DDL block is still re-issued on every open,
  so G9's `IX_AgentRuns_ParentRunId` still needs no migration block.

#### 0.10.5 One sentence in §0.3 was too strong — the conclusion survives

§0.3 says every `AgentRunCreateRequest` construction *"passes the first three arguments positionally and
everything else by name."* Measured: there are now **57** construction sites in `src/` + `tests/` (45 when §0.3
was written; Batch 06 added tests), and the **deepest positional call passes six** —
`new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.Schedule, jobId, deviceId, "goal")`
(`tests/Pia.Wpf.Tests/Services/AgentRunServiceTests.cs:92-93`), i.e. positional through `Goal`. One production
site passes five positionally (`HeadlessRunLauncher.cs:172-173`, then `Goal:` and `PolicyJson:` by name).

**`PolicyJson` is named at every one of the 57 sites, and nothing passes a seventh positional argument.** So a
`Guid? ParentRunId = null` appended **after** `PolicyJson` — the 8th member — is invisible to all 57, and R8's
"compile failure" premise stays refuted. The reason the two fakes still migrate inside G9's commit is unchanged:
**D9's three new `IAgentRunService` members**, not the record. Note the fakes' paths, since one is easy to
mis-guess — `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs:142` (`FaultyRunService`) and
`tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerRunSpineTests.cs:290` (`ThrowingAgentRunService`),
**both under `Services/`**.

---

## 1. Verified recon (re-read against the tree; cite these, not the batch brief)

| # | Fact | Location |
|---|---|---|
| R1 | `AgentPlanner.BuildSteps` hardcodes `AssignedPersonaId = null` at the **only** step-construction site. `PlanStepArg` is `(Title, Intent, ExpectedArtifact?)`. | `AgentPlanner.cs:281-299`, `:70-73` |
| R2 | `AgentPlanner`'s ctor is `(IAiClientService, AiProviderHandlerResolver, ISettingsService, ILogger)` and is constructed **positionally** in its tests. It already reads `ISettingsService` inside a swallow-everything gate (`ShouldReasonFirstAsync`, `:195-218`). | `AgentPlanner.cs:52-62`, `AgentPlannerTests.cs:63-66` |
| R3 | `AgentPlannerTests` has a full harness: a substituted `IAiClientService`, a **real** `AppSettings`, a `PlanStream(handler, emitArgs, usage)` driver that invokes the captured `emit_plan` handler, and a `Steps((title,intent,artifact)…)` args builder. New planner facts need no new fixture. | `AgentPlannerTests.cs:23-110` |
| R4 | `HeadlessTurnExecutor` injects `IPersonaService`, `IProviderService` **and** `IAssistantPromptComposer` — all three needed for per-step resolution — and already clones the provider to apply `persona.ReasoningEffort`. | `HeadlessTurnExecutor.cs:26-28`, `:139-154` |
| R5 | `LiveTurnExecutor` injects **none** of those three. It is hand-constructed on the UI thread by `ChatSessionManager` with a positional list ending in two trailing-defaulted params (`policy`, `timeline`). | `LiveTurnExecutor.cs:32-52`, `ChatSessionManager.cs:788-790` |
| R6 | `ChatSessionManager` **does** hold `IPersonaService`, `IProviderService` and `IAssistantPromptComposer`, and is DI-registered `Scoped`. | `ChatSessionManager.cs:30-32`, `:102`, `Bootstrapper.cs:583` |
| R7 | `StepTurnSpec` carries `SystemPrompt`, `Persona` (a `PersonaAttribution`), `Provider`, `Tools`, `SupportsTools`, `WebSearchActive` — every persona-derived value the step needs — and is built per step by `LiveTurnExecutor.BuildSpec` with **named** arguments. Its last three members are already trailing+defaulted. | `IAgentTurnExecutor.cs:35-67`, `LiveTurnExecutor.cs:134-155` |
| R8 | `ChatSession.RunStepTurnAsync` reads `spec.Persona` (message attribution, `:653`) and `spec.Provider` (three times: `:681`, `:683`, `:788`). Nothing else in it is persona-derived. | `ChatSession.cs:646-700` |
| R9 | `AgentRunState` = `Planning 0, Running 1, Verifying 2, WaitingForInput 3, Paused 4, Completed 5, Failed 6, Cancelled 7`. Documented *"append-only, never reorder"*. **No golden ordinal test exists** (`git grep "(int)AgentRunState" -- tests` = 0 hits). | `AgentEnums.cs:30-40` |
| R10 | `FailInterruptedRunsAsync` is ONE bulk `UPDATE … WHERE State < @Terminal` (`@Terminal = WaitingForInput`), no disk touch, **no per-row `RunChanged`** (deliberate: the Flow surface must not re-publish historical leftovers at startup), returns the affected count. | `AgentRunService.cs:343-367` |
| R11 | `SetStateAsync` is a blind unconditional `UPDATE`, logs `Run {RunId} → state {State}` and raises `RunChanged`. The **only** CAS in the service is `TryBeginResumeAsync` (`WHERE Id=@Id AND State=@Expected`), which also sets `ExtraJson=NULL` and opens a fresh ledger segment for the winner only. | `:146-163`, `:309-341` |
| R12 | `ApplyLedgerClock` is THE single place ledger wall-clock is computed; `PauseAsync`/`CompleteAsync`/`FailAsync` close the segment, `TryBeginResumeAsync` opens one. `MoveLedgerClock` swallows its own faults and requires `_gate`. | `:281-307`, `:222-279`, `:698-776` |
| R13 | `AddUsageAsync(runId, stepId, usage)` only ever touches the run named by `runId`. **There is no cross-run method.** It also `ApplyLedgerClock(Refresh)` and raises `RunChanged`. Ledger shape: `{InputTokens, OutputTokens, WallClockMs, ActiveMs?, SegmentStartedAt?, PerStep[]}` — no `CostUsd` (removed by Batch 02, `df0841a`). | `:165-202`, `:815-847` |
| R14 | `_slots = new SemaphoreSlim(2, 2)` on the **singleton** launcher, waited inside the dispatch `Task.Run` **before** the DI scope and orchestrator are built (`:199` launch, `:333` resume), released in the `finally` **after** `orchestrator.RunAsync` returns. | `HeadlessRunLauncher.cs:26`, `:193-256`, `:327-388` |
| R15 | `AgentRunOrchestrator.RunAsync` already creates the run's linked CTS from the caller's token (`:46`) and `cts.Token` is threaded into every step. Interactive passes `session.Cts.Token`, so `ChatSession.Cancel()` propagates. | `AgentRunOrchestrator.cs:44-46` |
| R16 | `ExecutingRunStore` is a reverse map runId → chatId, lock-free, cannot throw. Multiple concurrent runs on **one** chat already work with zero change. | `ExecutingRunStore.cs:19-38` |
| R17 | `ScheduledJobBackgroundService` holds `_runLock` (`SemaphoreSlim(1,1)`) from before `LaunchAsync` (`:189`) across `await handle.Completion` (`:202`) to `:253`. Scheduled jobs of both kinds are **already** strictly serialized. | `ScheduledJobBackgroundService.cs:34`, `:166-253` |
| R18 | `OnChatsChanged` deletes `Path.Combine(_runsBaseDir, runId)` for **every run id in `_runsByChat[chatId]`** when a chat is deleted — same-session launches only (`_runsByChat` is in-memory, never reloaded). | `HeadlessRunLauncher.cs:480-498` |
| R19 | The startup sweep predicate is exactly `remove = run is null \|\| Directory.GetLastWriteTimeUtc(dir) < UtcNow - 30d`. **Zero** `AgentRunState` awareness, **zero** promotion awareness. | `:424-459` |
| R20 | `AgentTimelineEvent.Seq` is monotonic **per RunId**, capped at `MaxEventsPerRun = 500` real rows with one synthetic `TraceTruncated` marker after. `GetForRunAsync(runId)` is the only read. `CreatedAt` is explicitly rejected as an ordering source (~1 ms resolution vs. sub-ms tool calls). | `AgentTimelineService.cs:60`, `:134-160`; `IAgentTimelineService.cs:42`; `SqliteContext.cs:342-343` |
| R21 | `RunProgressViewModel` is hand-constructed **positionally, outside DI**, at exactly one production site; its 6th param `IAgentTimelineService?` is trailing+defaulted precisely so that stays true. It captures a raw `SynchronizationContext` (`:176`) and marshals every mutation through `_uiContext.Post` (G3). | `AssistantViewModel.cs:397-398`, `RunProgressViewModel.cs:163-179` |
| R22 | `OnRunChanged` filters **`if (e.RunId != _runId) return;`** — an event for a *child* run is dropped. `RefreshAsync` is also called from the ctor, so the FIRST projection races any async data the VM loads. | `RunProgressViewModel.cs:181-193` |
| R23 | `SyncSteps` diffs by step id; the **existing-row** branch mutates **only** `Status` (`:431`). `AssignedPersonaId`/`Title` are `init`-only, so a row minted before some datum is available is never corrected — rows are replaced only when step **ids** change. | `:409-434` |
| R24 | `AppSettings` already persists a per-mode map with helper accessors: `ModePersonaDefaults` is a `Dictionary<WindowMode, Guid>` with `GetPersonaForMode`/`SetPersonaForMode`. Enum-keyed dictionaries round-trip through this codebase's `System.Text.Json` config today. | `AppSettings.cs:86`, `:310-319` |
| R25 | `UserOperatingMode` is `{ Personal, Business }`; `AppSettings.UserOperatingMode` is `UserOperatingMode?`. **Every** agent-run persona resolution keys on it: `ResolveActiveAsync(WindowMode.Assistant, settings.UserOperatingMode ?? Personal)` at `HeadlessTurnExecutor.cs:134`, `HeadlessRunLauncher.cs:120`, `:283`, `ChatSessionManager.cs:641`. | as cited |
| R26 | Agent knobs are deliberately **absent from `SyncSettings`** (Batch 04 R27/D9) — `AgentMaxSteps`, `AgentPlanReasoningTurnEnabled`, `AgentRunAutoApproveBuiltInWrites`, `Scheduled*` are all local-only. | `AppSettings.cs:164-205` |
| R27 | Settings shape precedent, four touch points: plain member on `AppSettings` + `[ObservableProperty]` + `OnXChanged` autosave guarded by `_isLoading` + load in `InitializeAsync` + mirror in `SaveSettingsAsync`, then XAML + 3 resx keys. `AssistantSettingsViewModel` owns `PersonasVm` (which has `IPersonaService` and an `ObservableCollection<Persona> Personas`) but has **no** `IPersonaService` of its own. | `AssistantSettingsViewModel.cs:13-53`, `:318-321`; `PersonaSettingsViewModel.cs:19`, `:28` |
| R28 | The Agent-runs settings tab already carries three sections in this order: budget sliders → "Planning" (`Settings_Agent_Planning_Section_Header`, ends `:422`) → "Autonomy" (Batch 04, `:426-437`) → "Scheduled" (`:439+`). The Autonomy block's own comment records **why** a global toggle goes *before* the scheduled block. | `Views/SettingsViews/AssistantView.xaml:411-441` |
| R29 | `ToolAutonomyRuleTests.GateFiles` is exactly **three** files — `ViewModels/Models/ChatSession.cs`, `Services/BackgroundAssistantTurnRunner.cs`, `ViewModels/AssistantViewModel.cs` — and the `IsDeleteLike` / `ClassifyPresumedExternal` bans plus the exact `Resolve`/`IsMcpTool`/`IsAutoApproveEligible` counts apply **per file, to those three only**. | `ToolAutonomyRuleTests.cs:34-87` |
| R30 | `AgentRunBracketTests` scans types **assignable to** `IHeadlessRunLauncher` or `IBackgroundAssistantTurnRunner`, asserts `>= 2` exist (anti-vacuity), and asserts each **injects** `IExecutingRunStore`. | `AgentRunBracketTests.cs:22-74` |
| R31 | `NamingConventionTests.allowedSuffixes` includes `Resolver`, `Launcher`, `Executor`, `Orchestrator`, `Service`, `Handler`, `Store`, `Runner`, `Provisioner`, `Context` — and does **not** include `Coordinator`, `Promoter`, `Manager`, `Pool`, `Delegate`. A companion rule bans **records** in the root `Pia.Services` namespace. | `NamingConventionTests.cs:32-39`, `:86-99` |
| R32 | Layer rules: `Models` must not depend on `Services` (**namespace-prefix match — `Pia.Services.Interfaces` counts**); `Services` must not depend on `ViewModels`; `ViewModels` must not depend on `Infrastructure`. ViewModels **may** depend on Services (precedent: `ChatSessionManager` calls `HeadlessRunLauncher.SerializeGrantEnvelope`). | `LayerDependencyTests.cs:9-58`, `ChatSessionManager.cs:765`, `:769` |
| R33 | `DiRegistrationTests` requires **every interface** in `Pia.Services.Interfaces` to be container-registered (short exemption list). Concrete-type registration needs no entry — precedent `services.AddTransient<HeadlessTurnExecutor>()`. | `DiRegistrationTests.cs:25-77`, `Bootstrapper.cs:488-489` |
| R34 | `ViewModels` must not reference `System.Windows` — enforced by NetArchTest with **`AssistantViewModel` as the only exemption**. `RunProgressViewModel` is **not** exempt. | `DependencyInjectionTests.cs:20-50` |
| R35 | `SqliteContext.EnsureSchema` issues its whole DDL block (`CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS`) on **every** open. An added index in that block therefore reaches existing databases with **no migration block**. `AgentRuns` already has four indexes; `ParentRunId` has **none** and no FK. | `SqliteContext.cs:283-330`, `:364-371` |
| R36 | `HeadlessRunLauncher` owns the grant-envelope helpers: `SerializeGrantEnvelope(grants, trigger, policy)` (`internal static`), `TryRestoreGrantEnvelope`, `TryRestorePolicy`, `ResumeFloorGrants = ["write_file"]`, `GrantEnvelopeVersion = 1` compared with **`!=`**. `InternalsVisibleTo Pia.Wpf.Tests` lets tests call them. | `HeadlessRunLauncher.cs:44-71`, `:532-676` |
| R37 | `AgentStep` carries two unused round-tripped TEXT columns: `DependsOnJson` (*"Reserved for a DAG"*) and `ExtraJson`. Both are written and read by `ReplaceStepsAsync`/`MapStep` today; `git grep` shows **no producer and no consumer** for either. | `AgentStep.cs:29-44`, `AgentRunService.cs:468`, `:474`, `:623`, `:629` |
| R38 | `ChatSessionManager.OnAgentRunChanged` computes `executing = e.State is Planning or Running or Verifying` (`:183`) and, when **not** executing, both releases the `_executingRuns` bracket (`:207`) **and retires the run's `_ownRunIds` ownership entry** (`:226`) — after which the same run is treated as a **foreign** full-chat writer for the rest of the process. | `ChatSessionManager.cs:181-227` |

---

## 2. Cross-cutting decisions

### D1 — The **roster is the opt-in**. No new feature toggle anywhere in this batch.

An **empty roster** (the default) ⇒ the planner is told about no personas ⇒ it emits no persona key ⇒ no step
carries an `AssignedPersonaId` ⇒ no per-step resolution and no fan-out ⇒ **today's behaviour, byte for byte,
including the planner's prompt text**. Configuring a roster in the G7 surface is the single act that turns on
multi-persona steps *and* (with a plan that marks siblings, D11) child runs.

This is the property that makes the batch safe to ship without a fourth `Agent*` checkbox, and it is the
answer to R15 (the scheduled-job head-of-line lock): a user who has not configured a roster cannot
accidentally double effective provider concurrency inside a scheduled job. It gets its own invariant test
(T-OPT-1/2/3).

Rejected: **a separate `AgentRunDelegateToChildren` bool.** It would be a second switch that can disagree
with the first (roster set, delegation off ⇒ personas resolve but never delegate — a state nobody asked for),
and it costs an `AppSettings` member, a VM property, a CheckBox and three resx values to express something
the roster already says.

### D2 — The planner picks personas from the roster by **NAME**, not by id and not by index (D6)

`emit_plan`'s `PlanStepArg` gains one optional member: `string? PersonaKey`. The plan prompt lists the roster
as human-readable lines and the model echoes the **name**; `AgentPlanner` maps name → `Guid` with
`OrdinalIgnoreCase` against the roster it just listed. No match ⇒ **`null`** ⇒ the run persona (D3).

- Rejected: **the persona `Guid`.** Models do not reproduce GUIDs reliably, and a single mistyped nibble is an
  unresolvable id — i.e. the fallback path — for a step the model *did* mean to assign.
- Rejected: **an index ("persona 2").** An off-by-one silently assigns the **wrong** persona, and there is no
  signal to detect it. A name mismatch fails **closed** to null, which is the run persona, which is today.
- Persona **names are user content.** They go into the *prompt* (which is fine — `persona.SystemPrompt`
  already does, `AgentPlanner.cs:309`), and they must **never** be logged. The unmatched key is logged as a
  **COUNT only**, precedent `TryRestorePolicy`'s dropped-class-name count (`HeadlessRunLauncher.cs:635`).

### D3 — Fallback ladder for a step's persona, in order, never throwing

1. `step.AssignedPersonaId` is `null` ⇒ the **run** persona/provider/setup (the fields the executor already
   holds). This is today's behaviour and the overwhelmingly common case.
2. non-null but `IPersonaService.GetPersonaAsync(id)` returns `null` (persona deleted between plan and
   execute) ⇒ the run persona, logged with the **id** at Information (a persona id is not user content;
   its name is).
3. resolved persona but **no provider resolvable** ⇒ **keep the assigned persona and its turn setup; borrow
   the RUN's provider**, logged. Not the whole run default: discarding the persona would throw away its
   **system prompt**, which §0.1 establishes is the *substance* of multi-persona. A persona running on a
   borrowed provider is still that persona; the run persona wearing the step's label is not. Never throw:
   `HeadlessTurnExecutor.BeginRunAsync` throws `InvalidOperationException` on no provider at all (`:147`) and
   that is correct **for the run**; a *step's* optional persona must never be able to fail a run that has a
   working run-level provider. **This is the one fallback arm that is partial** — arms 1, 2 and 4 return the
   run default whole. §3.2's ladder is the authoritative statement of it.
4. `PrepareTurn` throws ⇒ the run persona's setup, logged. (`PrepareTurn` is synchronous and composes strings;
   this arm is defence in depth, and it is the arm that keeps a persona-prompt bug from failing every step.)

Failure-isolated bookkeeping, instantiated: **a per-step persona is an enhancement; losing it degrades to the
run persona and never to a failed step.**

### D4 — Plan, replan and verify stay **run-level**

`PlanAsync`/`ReplanAsync`/`VerifyAsync` keep taking the run's `(Persona, AiProvider)`
(`AgentRunOrchestrator.cs:91`, `:180`, `:212`, `:221`). A plan is **one** decomposition and a verdict is
**one** critic judgement over the whole goal; making either per-step is meaningless (which persona plans the
plan that assigns personas?) and would multiply the replan budget's cost. Stated so nobody "completes" G6 by
threading personas into the verifier.

### D5 — The provider **override** applies to the run persona only

`HeadlessTurnExecutor._providerOverride` is the launcher's resolved provider and exists so *"the executor and
the orchestrator's planner run on the SAME provider (honors a scheduled job's `ProviderId`)"*
(`HeadlessTurnExecutor.cs:137-138`). For a **step assigned a different persona**, the override does **not**
apply: that persona's `PreferredProviderId` → the mode default → clone-for-`ReasoningEffort`, i.e. exactly
`ResolveProviderAsync`'s ladder minus the explicit id.

Reason: *"each persona running on its own provider"* is the batch's stated goal
(`07-subagents-multipersona.md:13`), and a roster persona was chosen **because of** its provider/effort. An
override that won everywhere would make the roster's provider column decorative on every scheduled job.

Risk, stated: an explicit `ProviderId` sometimes exists because the *default* provider is unusable (no key).
Mitigated by D3.3 — an unresolvable step provider falls back to the **run** provider, which is the override.

### D6 — One shared `StepPersonaResolver`, registered **transient**, concrete, no interface

```csharp
// src/Pia.Wpf/Services/StepPersonaResolver.cs           — namespace Pia.Services
public sealed class StepPersonaResolver { … }

// src/Pia.Wpf/Services/Interfaces/StepPersonaSetup.cs   — namespace Pia.Services.Interfaces
public sealed record StepPersonaSetup(Persona Persona, AiProvider Provider, AssistantTurnSetup TurnSetup);
```

- **`Resolver` is an allowlisted suffix** (R31). `Coordinator`/`Pool`/`Delegate` are **not** — do not reach
  for them anywhere in this batch, and do not grow the allowlist for one type.
- The record lives in `Pia.Services.Interfaces`, **not** `Pia.Models` and **not** the root `Pia.Services`:
  `Models` may not depend on `Services` and `AssistantTurnSetup` is a `Pia.Services.Interfaces` type (R32),
  while a record in the root `Pia.Services` namespace fails `RecordTypes_MustNotLiveInTheServicesRootNamespace`
  (R31). `StepTurnSpec` and `AssistantTurnSetup` are already there — same shelf, same reason.
- **Registered `AddTransient<StepPersonaResolver>()`**, concrete, next to `AddTransient<HeadlessTurnExecutor>()`
  (`Bootstrapper.cs:489`). Transient, **not** singleton: the resolver memoizes
  `(Persona, AiProvider, AssistantTurnSetup)` per persona id for the life of one run, and a singleton would
  pin a **stale system prompt** across a persona edit or a roster change until the app restarts — silently.
  One instance per run (or per turn) is the correct lifetime and matches the executor's.
- **Concrete, no `IStepPersonaResolver`**: an interface in `Pia.Services.Interfaces` obliges a DI registration
  (R33) and buys nothing — every consumer wants the real memoizing behaviour, and the two executor tests
  construct it directly.

Rejected: **duplicating the ladder in each executor.** It is ~25 lines including the clone-for-effort and the
four-arm fallback; two copies is how the two executors diverge, and executor parity is a standing constraint.

### D7 — The roster is keyed by **`UserOperatingMode`**, not by `WindowMode`

```csharp
// AppSettings
public Dictionary<UserOperatingMode, List<Guid>> AgentPersonaRoster { get; set; } = new();
public IReadOnlyList<Guid> GetAgentPersonaRoster(UserOperatingMode mode);   // capped, deduped, never null
public void SetAgentPersonaRoster(UserOperatingMode mode, IReadOnlyList<Guid> ids);
public const int MaxAgentPersonaRoster = 6;
```

D6's *"per-mode roster"* is ambiguous. Measured: **every** agent-run persona resolution keys on
`(WindowMode.Assistant, settings.UserOperatingMode)` (R25) — the `WindowMode` argument is the **constant** on
this path, so a `WindowMode`-keyed roster would have exactly one live key forever, while
`Personal`/`Business` is a distinction users actually make (a work roster and a home roster). Shape and helper
pair mirror `ModePersonaDefaults`/`GetPersonaForMode`/`SetPersonaForMode` verbatim (R24).

Local-only: **absent from `SyncSettings`**, like every other `Agent*` knob (R26). Capped at
`MaxAgentPersonaRoster = 6` and **clamped on read**, not only on write: a synced or hand-edited settings file
must not be able to put 40 persona names into every plan prompt. Ids that no longer resolve to a persona are
dropped on read (a deleted persona must not appear in a prompt as a blank line).

### D8 — `WaitingForChildren = 8`, appended; three code sites that must stop using ranges

a. **`AgentEnums.cs`** gains `WaitingForChildren = 8` with a doc comment stating it is (i) **non-terminal**,
   (ii) **above** the sweep threshold on purpose, and (iii) **not** `Paused(4)`, which belongs to Batch 08.
b. **`AgentRunService.cs:352-354`**'s sweep comment is rewritten to enumerate 0–2 / 3–4 / 5–7 / **8** and to
   say *why* 8 is not swept (D14 reconciles it instead).
c. **`AgentRunService.cs:713`** `state >= AgentRunState.Completed` becomes an explicit set:
   ```csharp
   // Explicit, NOT `state >= Completed`: WaitingForChildren(8) is appended ABOVE the terminal band
   // (the sweep's `State < WaitingForInput` requires it), so an ordinal range would freeze a waiting
   // parent's ledger and drop its open segment. "Terminal" here means "can never work again".
   var terminal = state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled;
   ```
   GUARD test T-LED-1 asserts the explicit set agrees with the old range for **every existing** member and
   disagrees for `WaitingForChildren` — that second half is what makes it non-vacuous.

### D9 — Three new `IAgentRunService` members, and only three

```csharp
/// Park a parent while its child runs execute: State → WaitingForChildren, CLOSES the ledger work
/// segment (the parent is not working — its children are, and each bills its own time). Blind UPDATE
/// like SetStateAsync: only the parent's own loop writes this. Raises RunChanged(WaitingForChildren).
/// childCount is logged as a COUNT and is NOT persisted — the child ROWS are the marker (§0.4).
Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default);

/// CAS WaitingForChildren → Running, re-opening a fresh ledger segment. Returns false when the parent
/// is no longer waiting (cascade-cancelled, or re-parked by the startup reconcile) — in which case the
/// caller must NOT continue the run. A blind write here would resurrect a Cancelled parent.
Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default);

/// The child runs of a parent, ordered by CreatedAt. Empty for a childless run. Indexed by
/// IX_AgentRuns_ParentRunId (G9); the Plan is NOT loaded (callers want state/ledger, not steps).
Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default);
```

`TryEndChildWaitAsync` is a **CAS** for the same reason `TryBeginResumeAsync` is: two writers can want the
parent (its own loop, and the cascade-cancel path), and `SetStateAsync` is a blind `UPDATE` that would happily
flip a `Cancelled` parent back to `Running` (R11). `GetChildRunsAsync` deliberately does **not** load
`Plan` — `GetAsync` does that with a second query per run (`:386`), and a 4-child roll-up does not need 4
plans.

**These three members are the compile break** in both hand-written fakes (§0.3). Migrate
`AgentRunOrchestratorTests.FaultyRunService` (delegate all three to `_inner`) and
`BackgroundAssistantTurnRunnerRunSpineTests.ThrowingAgentRunService` (throw from all three, matching its
style) **in the same commit**.

### D10 — `ParentRunId` is a trailing optional member of the record; the child never inherits by default

`AgentRunCreateRequest` gains `Guid? ParentRunId = null` **after** `PolicyJson`. `AgentRunService.CreateAsync`
already writes `@ParentRunId` from `run.ParentRunId` (`:123`) and `MapRun` already reads index 7 (`:599`) — so
the only production change is `run.ParentRunId = request.ParentRunId` in the object initializer at `:79-99`.
**No migration, no DDL change to the column.**

### D11 — Fan-out is declared by **one nullable scalar** on the plan step, and absence means sequential

`PlanStepArg` gains `int? ParallelGroup`. Steps sharing the **same non-null** value are **siblings** and run
as parallel child runs; an absent/null value means *sequential*, i.e. today.

- Persisted in `AgentStep.ExtraJson` as `{"parallelGroup":N}` — an existing, already round-tripped TEXT column
  with no producer and no consumer (R37), so **no DDL and no migration**. `DependsOnJson` is deliberately left
  alone: it is reserved for a real DAG, and a group marker is not one.
- The reader (`AgentStep` → `int?`) is a `TryGetProperty` on a `JsonDocument` inside a swallowing `try`;
  **any** parse failure ⇒ `null` ⇒ sequential. Precedent: `RunProgressViewModel.ReadTruncation` (`:389-406`).
- Rejected: **a general `dependsOn: [ordinals]` dependency graph.** It buys nothing D7 asks for (*siblings run
  in parallel and the parent awaits them*) and costs cycle detection, transitive closure, an `emit_plan`
  validation surface, and an eligibility computation that must be re-derived on **every replan** — where
  `KeepDoneAsync` **re-ordinals** the Done steps 0..k-1 (`AgentRunOrchestrator.cs:287-288`) and
  `ReplaceStepsAsync` writes ordinals verbatim, i.e. the ordinals a dependency list would name **move under
  it**. A group marker survives re-ordinaling untouched.
- Rejected: **deriving siblinghood from "consecutive steps with different personas".** A guess about
  independence that is wrong is a data-corrupting parallelism bug; a declaration that is wrong is a plan bug
  the user can see in the panel.
- **A group of one is not a fan-out.** A single-member group runs in-process like any other step (D3 still
  gives it its persona). Otherwise a model that stamps `parallelGroup: 1` on every step would turn a linear
  plan into N sequential child runs — all the cost of delegation, none of the parallelism.

### D12 — Delegation is available on **both** executors (executor parity, literally)

No capability flag, no `RunProfile` member, no `IAgentTurnExecutor` change. The fan-out loop lives in
`AgentRunOrchestrator`, which is the same type on both paths (`AddTransient<AgentRunOrchestrator>`,
`Bootstrapper.cs:488`), and both gates are already satisfied by D1+D11. A Live parent awaiting children keeps
its session `Running` (its `LiveTurnExecutor` is untouched by the wait), and `ChatSession.Cancel()` still
cascades through the existing linked CTS (R15 → D16).

This makes the three `ChatSessionManager` set additions (§5.4) **REGRESSION fixes**, not guards — in
particular `:183`, without which an interactive parent **retires its own `_ownRunIds` entry** the moment it
parks to wait (R38) and is then treated as a *foreign* full-chat writer on its own session for the rest of the
process: composer blocked, `Send` disabled, and no further event to correct it.

### D13 — A parked child is **not** a finished child (§0.9)

After `await handle.Completion`, the parent **re-reads the child's row** and branches on `child.State`:

| child state | fan-out step | parent |
|---|---|---|
| `Completed` (incl. truncated) | `Done` | continue; roll up once (D15) |
| `Failed` / `Cancelled` | `Failed` → the parent's ordinary step-failure path (replan budget, `AgentRunOrchestrator.cs:176-202`) | continue | 
| `WaitingForInput` (child parked at **its** budget) | **left `Pending`** | the parent **re-parks itself** `WaitingForInput` via the existing `SafePause(reason: "children-parked")`, and returns without `SafeEndRun` — exactly the budget-pause shape at `:143-154` |
| anything else / row missing | `Failed` with `"child run did not settle"` | as `Failed` |

Why re-park rather than treat a parked child as failed: the child's work is **durable and resumable** (E2
per-step persistence + the resume CAS), so failing the parent would throw away completed work and burn a
replan. Re-parking means **one user `Continue` on the parent** re-enters `RunAsync(resume: true)`, which
re-queries the persisted `Pending` remainder (R2/D1) — including the still-`Pending` fan-out steps — and
re-dispatches the parked children. The alternative (fail the parent, let the user Continue each child) is
strictly worse UX and leaves the parent's plan half-executed with no way to finish it.

Rejected: **`await` something narrower than `handle.Completion`.** There is no terminal-only signal on
`HeadlessRunHandle`, and inventing one means either a second task source in the launcher or polling. Reading
the row once after the await is one indexed query and is honest about what the handle means.

### D14 — Startup **reconcile**, not a startup sweep, for a parent whose children were swept away

`FailInterruptedRunsAsync` gains a **second statement in the same call, same `_gate` hold, still bulk, still
silent (no per-row `RunChanged`)**:

```sql
-- Statement 1 (unchanged): Planning/Running/Verifying → Cancelled.   WHERE State < 3
-- Statement 2 (new): a parent that was awaiting children when the process died. Its children were just
-- Cancelled by statement 1, so it can never be woken by a completing child. Re-park it as
-- WaitingForInput — the ONE state TryBeginResumeAsync can claim — so the existing panel Continue and the
-- existing resume path bring it back with no new resume vocabulary. Its post-fan-out steps are still
-- Pending, so a resume drains them (D1).
UPDATE AgentRuns SET State=@Waiting, UpdatedAt=@Now, ExtraJson=@Extra WHERE State=@WaitingForChildren
```
with `@Extra = {"paused":true,"reason":"children-interrupted"}` — the exact envelope `PauseAsync` writes
(`:292`), so `RunProgressViewModel`'s existing WaitingForInput projection and the Flow "continue?" card work
unchanged. The return value becomes the **sum** of both statements (it feeds one log line, `:365`).

- Rejected: **sweep the parent to `Cancelled`.** Its children's completed work is durable and its own earlier
  steps are `Done`; presenting that as a cancelled run throws away recoverable work and is the opposite of
  what the parked-survives-restart guarantee is for.
- Rejected: **teach `TryBeginResumeAsync` to claim `WaitingForChildren` too.** That widens the app's one CAS
  to a state whose meaning is *"another loop is working on this"*, so a live in-process wait could race a
  panel `Continue` and produce **two loops on one run** — precisely what guardrail 2 exists to prevent. Keep
  the CAS single-valued.
- **Ordering matters**: statement 1 must run **before** statement 2, or a parent re-parked first would be
  re-read as `WaitingForInput`… which statement 1 does not touch. Harmless either way, but write them in this
  order and say why in the comment.

### D15 — The **persisted ledger** nests; the ephemeral `RunContext` does not; the roll-up is **tokens only**

Two budget concepts coexist (`phase3-workflow-plan.md` §2) and this batch says explicitly which nests:

- **Nests: the persisted ledger.** When a child settles terminally, the parent gets **one**
  `AddUsageAsync(parentRunId, stepId: null, childTotals)` push. Pushed from the **parent's await site**, once
  per child run id (which is awaited exactly once), and **only** from the `Completed`/`Failed`/`Cancelled`
  branch of D13 — never from the parked branch, which is what keeps a later-resumed child from being counted
  twice.
- **Does not nest: `RunContext`.** Each child dispatch builds its own `RunContext` from its own `RunProfile`
  (`HeadlessRunLauncher.cs:176-177`), *"deliberately reset on every resume"* (`RunContext.cs:89-92`). The
  parent's step and wall-clock budget count the **fan-out step**, once — not the children's steps. A parent
  whose 4 children each ran 6 steps has executed **1** step against its own cap. Stated because the opposite
  (nesting the enforced budget) would make a fan-out unpredictably fatal to the parent.
- **Tokens only, never time.** `AddUsageAsync` takes a `UsageDetails` and lets `ApplyLedgerClock` own the
  clock (R13). The parent's `WallClockMs` stays **its own worked time** — and the parent is parked
  (segment closed, D9) for the whole wait, so the children's wall clock is neither double-counted nor lost:
  it is visible on the children, in the drill-down (D17).
- Rejected: **aggregate-on-read** (`WHERE ParentRunId=@p`). It makes every reader child-aware, re-runs on
  every `RunChanged`, and leaves the parent's persisted ledger a **lie** when read by anything that is not the
  panel (the Flow surface, a future export).
- **Stated loss**: a crash between a child's settle and the parent's push loses that roll-up. Acceptable —
  the child's own ledger still holds the truth, and the parent's number is an aggregate convenience, not an
  accounting record. Say so at the push site.

### D16 — Cascade cancellation rides the **existing** linked CTS; no orphans, by construction

`AgentRunOrchestrator.RunAsync` already owns `using var cts = CreateLinkedTokenSource(externalToken)` (R15).
The fan-out `await` is `Task.WhenAll(handles.Select(h => h.Completion)).WaitAsync(cts.Token)`… **no** — see
§7.3: `WaitAsync` would abandon the children. The correct shape is:

1. register a cancellation callback on `cts.Token` that calls `IHeadlessRunLauncher.CancelAsync(childRunId)`
   for each dispatched child (§7.3 specifies the launcher-side member);
2. then `await Task.WhenAll(completions)` **without** a `WaitAsync` timeout, so the parent cannot leave the
   `await` while a child is still writing.

**No-orphans guarantee, stated as three facts a reviewer can check:**
(i) every child is dispatched through the **existing launcher**, so it is in `_inflight` and `StopAsync`
cancels and bounded-awaits it at shutdown (`:404-422`);
(ii) the parent never returns from the fan-out `await` until every child's dispatch task has completed, so
there is no window in which a settled parent has a live child;
(iii) a process death leaves children `Running` (`< 3`) → swept `Cancelled` by statement 1, and the parent
`WaitingForChildren` → re-parked by statement 2 (D14). Every combination is covered.

### D17 — Per-run timeline views with a parent→child **drill-down**. No merged ordering. (R14)

`Seq` is monotonic **per run**, each child gets its own fresh `Seq` space **and** its own 500-event cap, and
`CreatedAt` is explicitly rejected as an ordering source (R20). **A merged parent+child timeline is not
implementable** without a new cross-run ordering key, which is its own work. So: the panel gains a **Children**
list; expanding a child row loads **that run's** trace through the same `GetForRunAsync(childRunId)`. Two
per-run views side by side, never one interleaved list. Say it in the code comment, not only here — this is
the promise a future reader will otherwise try to keep.

### D18 — `IsDeleteLike` in `HeadlessRunLauncher` is **legal**; three files stay untouched

Two containment claims this batch leans on, both checkable in one command each:

1. `ToolAutonomyRuleTests`' `IsDeleteLike` / `ClassifyPresumedExternal` bans and its exact
   `Resolve`/`IsMcpTool`/`IsAutoApproveEligible` counts apply **only** to the three files in `GateFiles`:
   `ViewModels/Models/ChatSession.cs`, `Services/BackgroundAssistantTurnRunner.cs`,
   `ViewModels/AssistantViewModel.cs` (R29). `HeadlessRunLauncher.cs` is **not** one of them, so G9's
   narrow-for-child helper may call `ToolPermissionService.IsDeleteLike` — the same authoring-time filtering
   `ScheduledJobToolHandler.ParseGrantedTools` already does. **Do not panic at the sight of `IsDeleteLike`.**
2. No G6–G10 change adds any of those five tokens to any of those three files. G7 touches
   `AssistantViewModel.cs` — at `:397`, adding one **argument** to a constructor call — which introduces none
   of them. Verify with
   `git grep -c "ToolAutonomy.Resolve(\|IsMcpTool\|IsAutoApproveEligible\|IsDeleteLike\|ClassifyPresumedExternal" -- src/Pia.Wpf/ViewModels/AssistantViewModel.cs`
   before and after: the counts must be identical.

### D19 — Not escalated, recorded here as **mine**, overridable in one line

**Batch 07 adds no new WPF `View` test.** `WpfStaHost` holds exactly 7 frame-pushing facts and the 8th
previously took the gate from 0/3 to 2/3 failing (`00-OVERVIEW.md:1028`,
`tests/Pia.Wpf.Tests/Views/WpfStaHost.cs:34`); the documented fix is assigned to Batch 12. Every UI change
here (the roster surface, the avatar row, the accent ring, the children list) is covered at **ViewModel and
converter level**, and the XAML is booked as manual-smoke debt in §10 — matching what Batch 03 did when it
withdrew its row-render fact. **If you want the host fixed first, say so**; it would unlock View coverage for
all four at once.

---

## 3. G6 — Per-step persona and provider resolution

**Depends on:** G5 (i.e. all of Batch 06). **Model tier:** opus. **06-DEPENDENT:** yes — this group edits
`HeadlessTurnExecutor.BeginRunAsync`/`RunExchangeStepAsync`, which Batch 06 also edits (G1 adds
`RunContext.WorkspaceRoot` and its `BeginRunAsync` assignment; G2 flips `Initialize`'s `workspaceRoot`
argument at both launcher call sites). **Re-read both methods before editing.** The two changes are disjoint
in substance (06 owns `_workspaceRoot` and the ambient; 07 owns `_persona`/`_provider`/`_setup`) but they sit
within a dozen lines of each other.

> **BUILDER NOTE (G6) — from the reconciler.**
> Batch 06 has landed. Four facts about the tree you will meet, each already reflected in the sections below
> but stated here because they are the ones that bite:
> 1. **Three ctors you append to already grew a Batch 06 parameter, and yours goes after it.**
>    `LiveTurnExecutor` gained trailing `string? workspaceRoot = null`; `ChatSessionManager` gained trailing
>    `IRunWorkspaceService? workspaces = null` after `IAgentTimelineService? agentTimelineService = null`.
>    `HeadlessTurnExecutor` gained **no** parameter (06 changed only `Initialize`'s argument value and added
>    one line to `BeginRunAsync`), so `timelineService` is still last there. **Count the parameters in each
>    file** — do not count them from any table in this document.
> 2. **`BeginRunAsync` (both executors) now assigns `ctx.WorkspaceRoot`** next to `ctx.WorkingSubpath`. That
>    line is what makes the run's file tools and its artifact verification agree on a root; leave it exactly
>    where it is. You are repurposing `_persona`/`_provider`/`_setup` in the same method (§3.4) — do not
>    reorder or "tidy" around 06's assignment.
> 3. **`StepTurnSpec` has a trailing `string? WorkspaceRoot = null`** and `LiveTurnExecutor.BuildSpec` sets
>    it. `BuildSpec` is exactly the method you rewrite for the resolved persona triple. **Keep passing
>    `WorkspaceRoot:`.** Dropping it compiles — the member is trailing and defaulted — and silently
>    un-isolates every interactive step, with no test to catch it.
> 4. **The gate's total test count is no longer 2424.** Batch 06 added five commits' worth of tests. The bar
>    is `failed: 0`, measured by **stash → rerun on the tree you were handed**, never read off a past count.
>
> **The plan's G6 row is wrong in TWO clauses and this file only refutes one of them by name. Here is the
> second.** `phase3-workflow-plan.md` §5 also says *"`HeadlessTurnExecutor` **stops caching `_provider`** in
> `BeginRunAsync` and resolves per step."* **Do not do that.** §3.4's instruction is *"do not delete the
> caching — repurpose it"*: `BeginRunAsync` keeps resolving `_persona`, `_provider` and `_setup` and they
> become the **run default** (`_runDefault = new StepPersonaSetup(_persona, _provider, _setup)`). Three things
> in the tree need a run-level triple and would break if the fields went away — D4's plan/replan/verify turns
> (run-level by decision), `RunSingleTurnFallbackAsync` (the R10 degrade turn passes **no** persona, so it
> resolves to `_runDefault`), and `BuildChatSnapshot`'s `ProviderId = _provider.Id`, which is the **chat row's**
> provider and not a step's. The plan file stays wrong on disk — it is not this workflow's to edit — so this
> note is the correction of record.
>
> One further correction against the **plan**, not against Batch 06: `phase3-workflow-plan.md` §5's G6 row says
> *"orchestrator resolves `(Persona, AiProvider)` per step instead of closing over one run-level pair."* That
> is the wrong layer, and it is still on disk in the plan. §0.2 of this file refutes it with the measurement:
> `AgentRunOrchestrator.ExecuteStepAsync` already hands the executor **the step itself**, and
> `AgentStep.AssignedPersonaId` is a real round-tripped member, so per-step resolution is **executor-local**
> and `AgentRunOrchestrator` needs **no change at all** in G6 (its `RunAsync` signature stays untouched, which
> is what keeps 13 positional test constructions compiling). **§0.2 wins over the plan's §5 row.** The
> orchestrator edit the plan was reaching for lands in G10.

### 3.1 What changes, and the one thing that must not

Per-step resolution happens **inside each executor**; `AgentRunOrchestrator` is not touched (§0.2). Four
values become per-step: `Persona`, `AiProvider`, `AssistantTurnSetup` (system prompt + tool list), and the
attribution stamped on the step's message. **The plan/replan/verify turns stay run-level** (D4).

### 3.2 `StepPersonaResolver` (new file, CRLF)

`src/Pia.Wpf/Services/StepPersonaResolver.cs`, namespace `Pia.Services`:

```csharp
public sealed class StepPersonaResolver
{
    // ctor: IPersonaService, IProviderService, IAssistantPromptComposer, ILogger<StepPersonaResolver>

    /// <summary>
    /// The (persona, provider, turn setup) a step runs on. <paramref name="assignedPersonaId"/> null, or
    /// naming a persona/provider this build cannot resolve, yields <paramref name="runDefault"/> — a
    /// per-step persona is an ENHANCEMENT and must never be able to fail a step (07 D3).
    /// <para>
    /// Memoized per persona id for the life of this instance, which is ONE RUN: recomposing the system
    /// prompt on every step of a 24-step run is pure waste, and a persona edit mid-run should not change
    /// the prompt half-way through a run. Registered TRANSIENT for exactly that reason — a singleton would
    /// pin a stale prompt across a persona edit until restart (07 D6).
    /// </para>
    /// </summary>
    public async Task<StepPersonaSetup> ResolveAsync(
        Guid? assignedPersonaId, StepPersonaSetup runDefault, bool tokenizationEnabled, CancellationToken ct);

    /// <summary>
    /// The personas the planner may assign, for the CURRENT operating mode: the configured roster,
    /// clamped to AppSettings.MaxAgentPersonaRoster, deduped, with ids that no longer resolve dropped.
    /// EMPTY when no roster is configured — which is the whole opt-in (07 D1): an empty roster means the
    /// plan prompt is byte-identical to today's and no step is ever assigned.
    /// </summary>
    public async Task<IReadOnlyList<Persona>> GetRosterAsync(CancellationToken ct);
}
```

Implementation notes the builder must honour:

- The provider ladder for a **non-default** persona, per D5 — and **this is the authoritative statement of
  D3 arm 3**: `persona.PreferredProviderId` → `GetDefaultProviderForModeAsync(WindowMode.Assistant)` →
  **`runDefault.Provider`** (borrow the run's provider, **keep the assigned persona and its turn setup**);
  then `if (persona.ReasoningEffort.HasValue) { provider = provider.Clone(); provider.ReasoningEffort = …; }`
  — **the existing clone logic, verbatim** (`HeadlessTurnExecutor.cs:148-152` / `HeadlessRunLauncher.cs:472-476`).
  Clone is mandatory: mutating a shared `AiProvider` would leak one persona's effort into every other run.
- `PrepareTurn(persona, provider, atCommands: [], tokenizationEnabled, suggestAgentModeEligible: false)` —
  the **exact** arguments both current call sites pass for a run turn (`HeadlessTurnExecutor.cs:157-158`;
  `ChatSessionManager.cs:681` passes `suggestAgentModeEligible: !planned && …`, i.e. **false** for a planned
  run). Never offer `suggest_agent_mode` inside a run.
- `GetRosterAsync` needs `ISettingsService`. Take it as a **fifth** ctor param (this type is new, so ordering
  is free).
- **BUILT, AND CORRECTED HERE (G6 builder, `8a4ec23`) — `AddTransient` is not enough on its own.** D6's
  "transient, one instance per run" is right, but *transient means per-run only where something resolves per
  run*. Measured: `HeadlessTurnExecutor` does (the launcher builds a fresh scope per launch **and** per
  resume, `Bootstrapper.cs` `AddTransient<HeadlessTurnExecutor>()`), so it takes the resolver directly. Its
  two other consumers do **not**: `ChatSessionManager` is `AddScoped` — **one instance per WINDOW** — and the
  `AgentPlanner` it reaches through its `AgentRunOrchestrator` is resolved **once** into that same scope, so
  both would have pinned one memo cache (⇒ one roster snapshot, one composed prompt per persona, one
  `_degraded` set) for as long as the window stayed open. A user who configured a roster in Settings would
  have seen **no specialists until restarting the app**, with nothing failing. Both therefore take
  `Func<StepPersonaResolver>` and build one per run (per **plan** for the planner, which is the grain the
  "resolved once per plan" rule wants), registered as
  `services.AddSingleton<Func<StepPersonaResolver>>(sp => sp.GetRequiredService<StepPersonaResolver>);` beside
  the transient. Safe from the root provider with `ValidateScopes = true`: all five dependencies are
  singletons or transients over singletons.
- Memo cache: a plain `Dictionary<Guid, StepPersonaSetup>`. **Not** thread-safe, and it does not need to be —
  one resolver per executor, and an executor's steps are strictly sequential. State that in a comment so
  nobody "fixes" it into a `ConcurrentDictionary` and implies concurrency that does not exist.
- Every fallback arm logs at Information with the **persona id** and the reason token
  (`"unresolvable-persona"`, `"no-provider"`, `"prepare-failed"`). **Never the persona name** — user-named
  content (CLAUDE.md).

### 3.3 `AgentPlanner` — the roster emission

- Ctor gains a **trailing defaulted** `StepPersonaResolver? personas = null` (R2: constructed positionally in
  its tests). `null` ⇒ `GetRosterAsync` is never called ⇒ empty roster ⇒ today.
- `PlanAsync` resolves the roster **once**, before the optional reasoning turn, and threads it into
  `BuildPlanMessages` **and** `BuildSteps`. `ReplanAsync` does the same — otherwise the first replan silently
  strips every persona assignment, which is a regression against G6's own point.
- The roster resolve is wrapped like `ShouldReasonFirstAsync`: any exception ⇒ empty roster ⇒ today's plan,
  logged at Warning with the exception **type** only.
- `BuildPlanMessages` / `BuildReplanMessages` append the roster block **only when the roster is non-empty**:

  ```
  You may assign each step to one of these specialists by setting personaKey to its exact name.
  Leave personaKey out to use the default assistant.
  Available: <Name> — <Tagline or the first three Expertise tags>
             …
  Steps that can run at the same time, independently of each other, may share the same parallelGroup
  number. Leave parallelGroup out unless the steps are genuinely independent.
  ```
  Placed on the **system** message, immediately after the existing "Keep the plan tight" line and before the
  `firm` retry line. Not on the user message: the analysis-fold comment at `AgentPlanner.cs:333-338` explains
  that the **user** message is the only role `TokenizingAiClientService` rewrites to PII placeholders, and a
  roster is app-owned configuration, not user turn text.
- `PlanStepArg` gains two trailing optional members with `[property: Description]` attributes (the schema is
  generated from them by `AIFunctionFactory`):
  `string? PersonaKey = null`, `int? ParallelGroup = null`.
- `BuildSteps(steps, roster)` sets `AssignedPersonaId = MatchRoster(s.PersonaKey, roster)` and
  `ExtraJson = s.ParallelGroup is { } g ? $"{{\"parallelGroup\":{g}}}" : null` (serialize with
  `JsonSerializer`, not string interpolation, so a hostile value cannot break the document).
- `MatchRoster` is `OrdinalIgnoreCase` on trimmed `Persona.Name`; unmatched non-blank keys are **counted** and
  logged as `"Plan assigned {DroppedCount} step(s) to an unknown persona; those steps use the run persona"`.
  **Never log the key** (D2).
- `ValidatePlan` is **unchanged**. An unknown persona key is not a plan defect — it degrades to the run
  persona. Adding it to validation would turn a cosmetic model slip into a `SingleTurn` degrade.

### 3.4 `HeadlessTurnExecutor` — per-step resolution

- New injected `StepPersonaResolver` — **trailing and defaulted** after `timelineService`, because
  `HeadlessTurnExecutorTests` constructs this type positionally. `null` ⇒ every step uses the run default
  (today). **06-DEPENDENT:** 06's §0.7 adds **no** ctor parameter to this type (it changes only `Initialize`'s
  `workspaceRoot` value and adds `ctx.WorkspaceRoot = _workspaceRoot` in `BeginRunAsync`), so
  `timelineService` is still the last parameter — confirm before appending.
- `BeginRunAsync` keeps resolving `_persona`, `_provider`, `_setup` exactly as it does now: they are the **run
  default** (`_runDefault = new StepPersonaSetup(_persona, _provider, _setup)`), and D4 needs them for the
  plan/verify turns anyway. **Do not delete the caching — repurpose it.**
- `ExecuteStepAsync` resolves before building the instruction and passes the resolved triple down:
  ```csharp
  public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
  {
      var step_ = _resolver is null
          ? _runDefault
          : await _resolver.ResolveAsync(step.AssignedPersonaId, _runDefault, _tokenizationEnabled, ct);
      return await RunExchangeStepAsync(BuildInstruction(...), persistInterim: true, ct, TimelineScope(step.Id), step_);
  }
  ```
- `RunExchangeStepAsync` gains a **trailing defaulted** `StepPersonaSetup? persona = null` and uses
  `var p = persona ?? _runDefault` for **all four** reads it currently makes off fields:
  `AgentContextBudget.From(p.Provider)` (`:268`), `_engine.RunExchangeAsync(request, p.Provider, p.TurnSetup, …)`
  (`:299`), the `SyncMessagePersona { Id = p.Persona.Id, Name = p.Persona.Name, Emoji = p.Persona.Emoji }`
  stamp (`:332`), and — **the one that is easy to miss** — the system message seeded in `BeginRunAsync`
  (`:189`, `_messages[0] = new ChatMessage(ChatRole.System, _setup.SystemPrompt)`).
- **The system message is the trap.** `_messages` is the accumulating transcript and its element 0 is the run
  persona's system prompt; `exchangeMessages` is a **copy** of it plus the step instruction (`:251-254`). A
  step whose persona differs must send **its own** system prompt. Fix: build the copy as
  `[new ChatMessage(ChatRole.System, p.TurnSetup.SystemPrompt), .. _messages.Skip(1), new(User, instruction)]`,
  with a comment saying `_messages[0]` stays the **run** persona's prompt so the accumulating transcript and
  every later step remain well-defined. Do **not** mutate `_messages[0]`.
- `RunSingleTurnFallbackAsync` (the R10 degrade turn) passes **no** persona ⇒ `_runDefault`. It belongs to the
  run, not to a step (its own comment says `stepId: null`).
- `BuildChatSnapshot`'s `ProviderId = _provider.Id` (`:442`) stays the **run** provider: it is the chat row's
  provider, not a step's. Say so at the line.

### 3.5 `LiveTurnExecutor` — per-step resolution (parity)

- Ctor gains a **trailing defaulted** `StepPersonaResolver? stepPersonas = null` — R5/R7: positional
  hand-construction, so trailing+defaulted is mandatory. **06-DEPENDENT, and re-measured — read this:** 06's
  §0.7 says its G5 appends `string? workspaceRoot = null` to this ctor, so the resolver would go **after that**.
  **As of the §0.10 audit (2026-07-31) that parameter was NOT in the tree** — the ctor was
  `(ChatSession, Func<ChatSession,bool>, PersonaAttribution, AiProvider, AssistantTurnSetup, bool, RunAutonomyPolicy? = null, IAgentTimelineService? = null)`,
  8 parameters with `timeline` last, because 06's G5 had not started (§0.10 B1). **Your resolver is simply the
  last parameter, whatever the last one currently is.** Count them in the file; do not count them from this
  table or from 06's §0.7.
- `ChatSessionManager` passes it at `:788-790`. `ChatSessionManager` gains the resolver as a **trailing
  defaulted** ctor param too, resolved from the container (R6, `Scoped`).
- `ExecuteStepAsync` (`LiveTurnExecutor.cs:72-78`) resolves **before** the UI `Post`, then `BuildSpec` takes
  the resolved triple:
  ```csharp
  BuildSpec(run, step.Ordinal, …) →
      SystemPrompt: p.TurnSetup.SystemPrompt,
      Persona:      PersonaAttribution.From(p.Persona),
      Provider:     p.Provider,
      Tools:        p.TurnSetup.Tools,
      SupportsTools:p.TurnSetup.SupportsTools,
      WebSearchActive: p.TurnSetup.WebSearchActive,
  ```
  Every other member is unchanged. **Conditional, re-measured (§0.10 B2):** 06 §0.7 says G5 appends a
  `string? WorkspaceRoot = null` to `StepTurnSpec` after `Timeline` and that `BuildSpec` must keep passing it
  (*"G6 resolves persona/provider per step and touches `BuildSpec`; keep the member"*). **At the 2026-07-31 audit
  that member did not exist** — `StepTurnSpec`'s trailing members were `UseGoalVerbatim`, `Policy`, `Timeline`.
  So: **`grep -n "WorkspaceRoot" src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` before you rewrite
  `BuildSpec`.** If it is there, keep passing it — dropping it **compiles**, because it is trailing and
  defaulted, and silently un-isolates every interactive step, with no test to catch it. If it is not there, G5
  was cut or is still pending: do not invent the member, and do not treat its absence as a sign this section is
  stale in any other respect. Either way `ChatSession.RunStepTurnAsync` needs **no change at all** — it already
  reads `spec.Persona` for attribution and `spec.Provider` three times (R8). That is why `StepTurnSpec` was
  the right carrier.
- **Resolve OUTSIDE the `Post`.** `PostAsync` marshals onto the captured UI context (`:157-170`) and
  `ResolveAsync` awaits `IPersonaService`/`IProviderService` I/O; resolving inside would run a DB read on the
  dispatcher. `ExecuteStepAsync` is already `async` at the orchestrator's call site, off the UI thread.
- `RunSingleTurnFallbackAsync` and `BeginRunAsync`/`EndRunAsync`/`OnPausedAsync` are untouched.

### 3.6 What G6 deliberately does not do

- Does not touch `IAgentPlanner`, `IAgentTurnExecutor`, `IAgentRunService`, `StepTurnSpec`'s member list, or
  `AgentRunOrchestrator`.
- Does not spawn a child run. G6 is **in-process, sequential, per-step persona**. Delegation is G10.
- Does not surface anything in the UI. That is G7.

---

## 4. G7 — Roster settings surface + panel attribution

**Depends on:** G6. **Model tier:** sonnet. **06-DEPENDENT:** the heading below said *no*; the reconciler
corrects that to **yes, in three places** — see the builder note.

> **BUILDER NOTE (G7) — from the reconciler.**
> Batch 06's G4 shipped a publish affordance into the **same two files** this group edits, so:
> 1. **`RunProgressViewModel` already has a trailing `IRunWorkspaceService? workspaces = null`** (06's 7th
>    param) plus `IsPublishing`, `PublishNote`, `OutputBranchName`, `HasOutputBranch`, `CanPublish` and a
>    `Publish` command. Append `IPersonaService? personaService = null` **last** (the 8th) — §4.4's "7th" was
>    written before 06 landed. In `RefreshAsync` you add a persona-map load; 06 put a **terminal-only**,
>    off-thread `DescribeAsync` read there whose result is applied through `_uiContext.Post`. Add beside it;
>    **do not** move that read into the unconditional path — it is a file read plus a directory enumeration
>    and must not run on every `RunChanged`.
> 2. **`AssistantViewModel.cs:397` already passes one extra argument** (06's workspace service). Yours is a
>    second argument on the same call — still *one call site, arguments only*, so D18.2's grep-count claim
>    about this file holds unchanged. Verify it with the command in D18.2 before and after anyway.
> 3. **`RunProgressPanel.xaml` gained a Publish button beside the Continue button and two note lines**, so
>    the step-row avatar is **no longer at `:66-68`**. Find it by markup (`<chat:PiaPersonaAvatar`), and check
>    that `BooleanToVisibilityConverter` is still used elsewhere in the file before relying on §4.5's claim
>    that you introduce no new resource lookup (it is — 06 added uses, it removed none).
> 4. **The resx files gained SIX keys ×3** — measured 2026-07-31 from the working-tree diff, and it is six, not
>    the five this note first predicted: `Run_Action_Publish`, `Run_Publish_Pending`, `Run_Publish_Done`,
>    `Run_Publish_Failed`, `Run_Publish_Conflicts`, `Run_Output_Branch`. **None of 07's planned keys collide**
>    (`grep -c "Run_Children_Header\|Run_State_WaitingForChildren\|Settings_Agent_Roster"` over `ViewStrings.resx`
>    returns 0), but every resx line number cited anywhere in this document is now stale: **anchor on a key
>    name**, not a line. Re-run that grep before you add yours — 06's G5 may add more.

### 4.1 `AppSettings` — the roster (D7) — **ALREADY LANDED IN COMMIT 1**

> **G6 builder note (`8a4ec23`): §8's commit table is wrong about this row, as an ordering error rather than a
> choice.** `StepPersonaResolver.GetRosterAsync` cannot compile without `MaxAgentPersonaRoster`,
> `AgentPersonaRoster` and `Get`/`SetAgentPersonaRoster`, and the resolver is commit 1's whole subject — so all
> four members and `tests/Pia.Wpf.Tests/Models/AppSettingsAgentRosterTests.cs` (T-SET-1..5, all five) shipped
> in commit 1. **G7 owns the rest of this section only**: `AssistantSettingsViewModel`,
> `AgentRosterOptionViewModel`, the `AssistantView.xaml` block and the three resx keys. Do not re-add the model
> members and do not re-write those tests — read them and extend if the VM needs more.


Placed directly under `AgentRunAutoApproveBuiltInWrites` (`:198`), with the same comment discipline:

```csharp
    // Batch 07 — the personas the PLANNER may assign to individual steps, per operating mode. EMPTY (the
    // default) is the whole opt-in: an empty roster means the plan prompt is byte-identical to pre-07, no
    // step is ever assigned a persona, and no run ever delegates to a child run (07 D1). Keyed by
    // UserOperatingMode — not WindowMode — because every agent-run persona resolution already keys on it
    // and the WindowMode is always Assistant on this path (07 D7). Capped and clamped on READ as well as
    // write, so a hand-edited file cannot put 40 persona names into every plan prompt. Global, like every
    // other Agent*/Scheduled* knob, and local-only (absent from SyncSettings).
    public const int MaxAgentPersonaRoster = 6;
    public Dictionary<UserOperatingMode, List<Guid>> AgentPersonaRoster { get; set; } = new();
```
plus `GetAgentPersonaRoster(UserOperatingMode)` (dedupe, `Take(MaxAgentPersonaRoster)`, never null) and
`SetAgentPersonaRoster(UserOperatingMode, IReadOnlyList<Guid>)` (empty ⇒ **remove the key**, mirroring
`SetPersonaForMode`'s `Remove` at `:318`, so an unconfigured roster leaves no residue in the settings file).

### 4.2 `AssistantSettingsViewModel` + XAML + resx

- Gains a **trailing defaulted** `IPersonaService? personaService = null` (R27: it owns `PersonasVm` but has
  no `IPersonaService`; going through `PersonasVm.Personas` would couple this surface to another VM's load
  ordering).
- New `ObservableCollection<AgentRosterOptionViewModel> AgentRosterOptions` — one row per persona, each with
  `Guid Id`, `string Name`, `string? Emoji`, `string? AccentColor`, `bool IsSelected`. Named `…ViewModel` in
  `Pia.ViewModels` because `NamingConventionTests.ViewModels_MustEndWith_ViewModel` requires it.
- Filled in `InitializeAsync` under the `_isLoading` guard from `GetPersonasAsync()` ∪ the persisted roster;
  `IsSelected` toggling calls back into the parent, which enforces the cap (a 7th selection is **refused**,
  and the refusal is silent-but-visible: the checkbox does not stick) and autosaves through the R27
  `_isLoading`-guarded path.
- The **operating mode** the surface edits is `settings.UserOperatingMode ?? Personal` — the same expression
  every consumer uses (R25). No mode picker in this surface.
- XAML: a new section **between** the Autonomy block (ends `:437`) and the `<!-- Scheduled … -->` comment
  (`:439`) — same reasoning R28 records for Autonomy: a **global** knob must not read as a fourth unattended
  option. Header + description + an `ItemsControl` over `AgentRosterOptions` whose item template is a
  `CheckBox` with `IsChecked="{Binding IsSelected}"` and a `chat:PiaPersonaAvatar` + name. **Every
  `StaticResource` in that template must already appear elsewhere in `AssistantView.xaml`** — an unresolved
  one throws at template instantiation, which no test reaches.
- **Three resx keys, all three files** (`ViewStrings.resx`, `.de.resx`, `.fr.resx`; parity is test-enforced;
  never hand-edit `Designer.cs`). Insert after `Settings_Agent_AutoApproveBuiltInWrites_Description`:

| key | en | de | fr |
|---|---|---|---|
| `Settings_Agent_Roster_Section_Header` | `Step specialists` | `Schritt-Spezialisten` | `Spécialistes d'étape` |
| `Settings_Agent_Roster_Description` | `Choose up to six personas the planner may assign to individual steps of an agent run. Each assigned step runs on that persona's own prompt and provider. Leave this empty to keep every step on your default assistant.` | `Wähle bis zu sechs Personas, die der Planer einzelnen Schritten einer Agenten-Ausführung zuweisen darf. Jeder zugewiesene Schritt läuft mit dem Prompt und dem Anbieter dieser Persona. Leer lassen, damit alle Schritte auf deinem Standard-Assistenten laufen.` | `Choisissez jusqu'à six personas que le planificateur peut affecter à des étapes individuelles d'une exécution d'agent. Chaque étape affectée utilise le prompt et le fournisseur de cette persona. Laissez vide pour garder toutes les étapes sur votre assistant par défaut.` |
| `Settings_Agent_Roster_Empty` | `No specialists selected — every step runs on your default assistant.` | `Keine Spezialisten ausgewählt – alle Schritte laufen auf deinem Standard-Assistenten.` | `Aucun spécialiste sélectionné – toutes les étapes utilisent votre assistant par défaut.` |

Terminology checked against the existing files: de uses **Ausführung** for a run
(`Settings_Agent_MaxReplans_Description`), fr uses **exécution**. No `&`, `<` or `>` in any value ⇒ no XML
escaping. The en/de/fr string count must come out equal — `LocalizationTests` enforces it.

### 4.3 `StepRowViewModel` — the attribution fix (§0.7)

The fix is in the **ViewModel**, not in the DP. Making `PiaPersonaAvatar.PersonaIdProperty` nullable would
change a control with other consumers (the live chat, the history inspector) and would still hand
`PersonaGlyph`'s non-nullable `Guid` DP a null one layer down.

```csharp
public sealed partial class StepRowViewModel : ObservableObject
{
    public Guid StepId { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>The persona the PLANNER assigned, or null. Kept as the raw fact; the render values below
    /// are the resolved projection.</summary>
    public Guid? AssignedPersonaId { get; init; }

    // SETTABLE, not init-only: SyncSteps' existing-row branch must be able to refresh them (07 D-G7.3).
    [ObservableProperty] private Guid _personaId;          // Guid.Empty ⇒ no avatar
    [ObservableProperty] private string? _personaEmoji;
    [ObservableProperty] private string? _personaAccent;   // #RRGGBB, straight to HexToBrushConverter

    public bool HasPersona => PersonaId != Guid.Empty;
    partial void OnPersonaIdChanged(Guid value) => OnPropertyChanged(nameof(HasPersona));
    …
}
```

- **`HasPersona` collapses the avatar when no persona was assigned.** That is deliberately *not* a fallback to
  the run persona: `AgentRun` has **no persona column** (verified against `MapRun`, `AgentRunService.cs:590-611`),
  so "the run persona" is not resolvable from the run row, and resolving *the current active persona* instead
  would be a guess that goes stale. An avatar that appears **only** when a step was genuinely delegated is a
  more honest signal than a box that is always there — and it is strictly better than today's always-empty
  box.
- **Settable, and refreshed in `SyncSteps`' existing-row branch** alongside `Status` (`:431`). This is
  load-bearing: `RefreshAsync` is called from the **constructor** (R21/R22), so the first projection can land
  before the persona map is loaded; with `init`-only members those rows would be minted persona-less and
  **never corrected**, because rows are replaced only when step **ids** change (R23). The same branch is what
  G10's child rows need.

### 4.4 `RunProgressViewModel` — the persona lookup

- **New trailing defaulted ctor param `IPersonaService? personaService = null`, appended LAST** (R12/R21).
  **06-DEPENDENT (reconciled):** at the time this was written the type had 6 parameters and this was the 7th.
  Batch 06's G4 appends `IRunWorkspaceService? workspaces = null` as the 7th (its publish affordance, 06 B15),
  so on the tree you will meet **`personaService` is the 8th**. Count the parameters in the file; the
  invariant is *last*, not a number. Update the one
  production call site, `AssistantViewModel.cs:397-398`, passing `_personaService` (already injected there,
  `:29`). **Do not** introduce a `System.Windows` reference while in this file — the ViewModel ratchet exempts
  only `AssistantViewModel` (R34).
- `RefreshAsync` loads the persona map **before** the `_uiContext.Post(Project)` (it is already async and
  off-thread):
  `_personas ??= (await _personaService.GetPersonasAsync()).ToDictionary(p => p.Id);` — once per VM, guarded
  by a `try/catch` that logs at Warning and leaves the map empty (an attribution read must never break the
  panel). `null` service ⇒ empty map ⇒ `HasPersona` false everywhere ⇒ **today's rendering minus the empty
  box**.
- `SyncSteps` sets `PersonaId`/`PersonaEmoji`/`PersonaAccent` from the map on **both** branches (new row and
  existing row).

### 4.5 `PiaPersonaAvatar` + `RunProgressPanel.xaml`

- `PiaPersonaAvatar` gains `AccentColorProperty` (`typeof(string)`, default `null`). The XAML wraps the
  existing shadowed `Border` in a 1.5 px accent ring:
  `BorderThickness="1.5" BorderBrush="{Binding AccentColor, ElementName=Root, Converter={StaticResource HexToBrushConverter}}"`
  — `HexToBrushConverter` already exists and is already keyed at `App.xaml:78` (§0.8), and it returns
  `Brushes.Transparent` for null/blank/garbage, so an unset accent renders exactly as today. **Background is
  not tinted**: it would fight emoji legibility and both themes.
- `RunProgressPanel.xaml:66-68` becomes:
  ```xml
  <chat:PiaPersonaAvatar Grid.Column="1" Width="20" Height="20" Margin="0,0,6,0"
                         VerticalAlignment="Center"
                         PersonaId="{Binding PersonaId}"
                         Emoji="{Binding PersonaEmoji}"
                         AccentColor="{Binding PersonaAccent}"
                         Visibility="{Binding HasPersona, Converter={StaticResource BooleanToVisibilityConverter}}" />
  ```
  `BooleanToVisibilityConverter` already appears **six times** in this file, so the template introduces no
  new resource lookup (the file's own comment at `:82-87` explains why that matters).
- The live chat's and history inspector's uses of `PiaPersonaAvatar` are untouched — the new DP is additive
  with a null default.

---

## 5. G8 — A persisted run state for a parent awaiting children

**Depends on:** G7. **Model tier:** opus. **Highest-risk group in Phase 3.** **06-DEPENDENT:** no, but see
§5.5 on the promotion ordering — and the builder note, which names a second "terminal" predicate 06 added.

> **BUILT 2026-07-31, LAST of Batch 07 (G9 and G10 shipped ahead of it). Six divergences, annotated by the
> builder.**
>
> 1. **D9 adds TWO members, not three.** `GetChildRunsAsync` was already on the tree: G10 needed it for
>    `SafeCancelStaleChildrenAsync` and the panel's children list, so it landed in `9c32999` with the exact
>    signature, SQL and no-`LoadSteps` comment §5.2 specifies. Only `BeginChildWaitAsync` and
>    `TryEndChildWaitAsync` are new here, and the two hand-written fakes were migrated 17→19 in this commit as
>    §0.3/D9 said they would have to be.
> 2. **`SafeBeginChildWait` reports its own failure; the park uses `dispatched.Count`, not
>    `siblings.Count`.** §7.3's pseudo-code parks on `siblings.Count`, but a sibling whose `LaunchChildAsync`
>    threw was never dispatched and is not being waited on — and with **zero** dispatched there is nothing to
>    wait for at all, so parking would leave a state the CAS below could not clear. The park is therefore gated
>    on `dispatched.Count > 0`, and the wrapper returns `bool` (unlike every other `Safe*`) so a **swallowed
>    park fault** cannot make the CAS read as "another writer owns this run" and abandon a healthy run.
> 3. **The un-park CAS runs BEFORE the parked/failed branches, not after the parked one.** §7.3 orders it
>    `if (anyParked) … return;` *then* the CAS. But the parked branch calls `PauseAsync`, which is a blind write
>    — so on that path a cascade-cancelled parent would be resurrected to `WaitingForInput`, which is the exact
>    R11 failure the CAS exists to stop. Every arm the caller can take writes this row, so the CAS is the single
>    gate in front of all of them. The pre-existing `cts.IsCancellationRequested` check still runs FIRST and
>    still owns its own case (this loop does own the run and must settle it `Cancelled` itself).
> 4. **The lost-CAS arm calls `SafeOnPaused`, which §7.3's "return without settling" does not mention.** A bare
>    `return` is wrong for a **Live** parent: only a release hook clears the session's `IsStreaming`, so the
>    foreground chat would sit wedged `Running` with a disabled Send forever. `SafeOnPaused` touches the session
>    and never the run, so the minimal-write premise of the arm survives. No `SafeFail`, no `PinRange`, no
>    promotion. Carried on a new `FanOutResult.Abandoned` member.
> 5. **`AgentRunNotificationSurface`'s filter was EXTRACTED, against §5.4's "no change".** §5.4 asks for a GUARD
>    test over the `is … or …` set at `:69`, but that set is inline in an event handler whose only other path is
>    a dispatcher marshal, so no non-racy test can reach it. It is now
>    `internal static bool IsPublishableState(AgentRunState)` with a row-per-state theory. The extraction earned
>    itself: `HandleRunStateAsync`'s last arm is the **terminal publish**, so any state that gets past that
>    filter without an arm of its own publishes a "run finished" Flow card for a run that is still working.
> 6. **T-CONV-1's assertion as written is arithmetically impossible.** "The key count matches the member count"
>    is false by exactly one: `Completed` and `TruncatedCompleted` deliberately share `Run_State_Completed`. The
>    test asserts N members ⇒ N−1 distinct keys, plus that those two are the *only* members reaching that key.
>
> Also, for the next reader: **T-ST-2 and T-LED-1 need a BACK-DATED open segment** or they are vacuous. A
> freshly created run's segment is microseconds old (`CreateAsync` opens one at `SegmentStartedAt = now`), so
> "the clock froze" and "the clock kept running" are the same number and the close/terminal-set neutralizations
> both pass. Both tests move the persisted timestamp 3 s back first, reusing `AgentRunServiceTests`'
> `BackdateOpenSegment` shape. **T-LED-1 is driven behaviourally** (does this state freeze the clock?) rather
> than by reflecting the private `ApplyLedgerClock`.

> **BUILDER NOTE (G8) — from the reconciler.**
> **There are now TWO predicates in this repo that mean "terminal", in two different files, and only one of
> them is yours to change.**
> - **Yours (D8c):** `AgentRunService.cs:713`'s `var terminal = state >= AgentRunState.Completed;` inside
>   `ApplyLedgerClock`. `WaitingForChildren = 8 >= 5` would freeze a parked parent's ledger and drop its open
>   work segment, so replace the range with the explicit set
>   `state is Completed or Failed or Cancelled`. **REGRESSION.**
> - **Not yours:** Batch 06's G4 rewrote `HeadlessRunLauncher.RunStartupSweepAsync`'s **workspace-retention**
>   predicate into an explicit terminal set as well — `run?.State is Completed or Failed or Cancelled` ⇒ a
>   7-day retention, and its comment says in as many words that *anything non-terminal, or a state this build
>   does not know, keeps the original 30-day floor.* So `WaitingForChildren` already gets the correct
>   non-terminal treatment there **for free**. **Do not add `WaitingForChildren` to that set.** Adding it
>   would put a live parent's workspace — the only copy of its and its children's un-promoted work — on a
>   7-day deletion clock. If you touch that predicate at all, touch only its comment.
> Nothing else in Batch 06 added a run-state range comparison, so §0.5's "exactly two range comparisons"
> survey still holds for `AgentRunState`. Re-run its grep to confirm before you rely on it.
> `RunProgressViewModel` now also carries 06's publish members (`IsPublishing`, `PublishNote`,
> `OutputBranchName`, `CanPublish`); your `RunProgressState.WaitingForChildren` arms must leave `CanPublish`
> and `CanContinue` alone — `CanContinue` stays **false** for the new state, as §5.4 says.

### 5.1 The enum member

```csharp
    /// <summary>
    /// Batch 07: a PARENT run parked while its child runs execute. NON-TERMINAL. Appended at 8 — never
    /// inserted, never renumbered — and deliberately ABOVE the terminal band, because the startup sweep is
    /// `WHERE State < WaitingForInput(3)` and a parent awaiting children must survive a restart rather than
    /// be cancelled out from under its children's completed work (07 D8/D14). NOT Paused(4), which is
    /// reserved for Batch 08 live-steering. "Waiting on N children" is NOT stored on the run — the child
    /// ROWS are the marker (07 §0.4), which is why the resume claim's unconditional `ExtraJson=NULL` is
    /// irrelevant here.
    /// </summary>
    WaitingForChildren = 8,
```

**Why no existing state works** — the four candidates, each ruled out by code:

| candidate | why not |
|---|---|
| `Planning`/`Running`/`Verifying` (0–2) | swept to `Cancelled` at **every** startup (`AgentRunService.cs:357`, `State < 3`). A parent awaiting children would be cancelled while its children ran on. |
| `WaitingForInput` (3) | it is the **claimable** state: `TryBeginResumeAsync` CASes 3 → `Running` (`:321`). A parent parked here is one panel `Continue` away from a **second loop on the same run** — guardrail 2's exact failure. It also nulls `ExtraJson` on the claim. |
| `Paused` (4) | **reserved for Batch 08** live-steering (`08-live-steering.md:12`, already rendered by `RunProgressViewModel.cs:225`). Taking it would collide with a batch that is already specced. |
| `Completed`/`Failed`/`Cancelled` (5–7) | terminal; `CompletedAt` is written and `ApplyLedgerClock` freezes the ledger. |

### 5.2 The three service members (D9)

Implemented in `AgentRunService` exactly like their siblings — inside `lock (_gate)`, `if (_disposed) return`,
`MoveLedgerClock` **inside** the hold, log **outside** it, `RunChanged` **outside** it:

- `BeginChildWaitAsync(runId, childCount, ct)` → `MoveLedgerClock(CloseSegment)` then
  `UPDATE AgentRuns SET State=@State, UpdatedAt=@Now WHERE Id=@Id`; logs
  `"Run {RunId} → WaitingForChildren ({ChildCount} child run(s))"` — a **count**, never a goal or a title;
  raises `RunChanged(WaitingForChildren)`.
- `TryEndChildWaitAsync(runId, ct)` → `UPDATE … SET State=@Running, UpdatedAt=@Now WHERE Id=@Id AND State=@Waiting`;
  on `affected > 0` only: `MoveLedgerClock(OpenSegment)` **in the same `_gate` hold** (mirroring
  `TryBeginResumeAsync:331-332`), log, raise `RunChanged(Running)`. Returns `affected > 0`.
  **It must NOT null `ExtraJson`** — unlike the resume claim, this transition is not a user "continue" and has
  no pause marker to clear.
- `GetChildRunsAsync(parentRunId, ct)` → `SELECT {RunColumns} FROM AgentRuns WHERE ParentRunId=@P ORDER BY CreatedAt ASC`,
  `MapRun` per row, **no `LoadSteps`**.

### 5.3 The ledger's terminal test (D8c) and the sweep reconcile (D14)

Both specified in §2. Two further notes for the builder:

- The sweep's **return value** becomes `affected1 + affected2`; the existing log line
  (`"Settled {Count} interrupted agent run(s) to Cancelled at startup"`) becomes two lines — one per statement
  — so a support log distinguishes *cancelled* from *re-parked*. Counts only.
- `FailInterruptedRunsAsync` still raises **no** `RunChanged` for either statement. The reason at `:354-356`
  applies unchanged to the re-park: these are historical leftovers, and the Flow surface must not publish a
  "continue?" card at startup for a run whose panel nobody has opened.

### 5.4 Every consumer of the state machine, with a verdict

| site | today | verdict | why |
|---|---|---|---|
| `AgentRunService.cs:357` sweep SQL | `State < 3` | **no change** | `8 < 3` is false ⇒ survives, which is the requirement. Comment corrected (D8b). |
| `AgentRunService.cs:713` ledger `terminal` | `>= Completed` | **REGRESSION fix** (D8c) | `8 >= 5` would freeze a waiting parent's ledger. |
| `ChatSessionManager.cs:183` `executing` | `is Planning or Running or Verifying` | **REGRESSION fix — add `WaitingForChildren`** | without it an interactive parent retires its own `_ownRunIds` entry (`:226`, R38) and is treated as a **foreign** writer on its own session for the rest of the process: composer blocked, no further event to correct it. Also keeps the `_executingRuns` bracket held (`:207`), which is correct — the parent **will** write this chat again. |
| `ChatSessionManager.cs:556-557` re-attach scan | `Planning/Running/Verifying/WaitingForInput/Paused` | **REGRESSION fix — add** | otherwise a restart does not re-attach the panel to a parent that is genuinely live. |
| `ChatSessionManager.cs:574` `SetForeignRunActive` | `Planning/Running/Verifying` | **REGRESSION fix — add** | a re-attached parent awaiting children **is** a live second full-chat writer. |
| `AgentRunNotificationSurface.cs:69`, `:86` | explicit `is … or …` filters | **no change (GUARD test)** | an appended state falls out of the filter ⇒ no toast. Correct: waiting-for-children is not user-actionable. Pin it so a later "helpful" addition is a deliberate one. |
| `RunProgressViewModel.MapState` (`:218-230`) | `switch`, default ⇒ `Running` | **add an explicit arm** | see below. |
| `RunProgressViewModel.ComputeActivity` (`:245-254`) | `switch`, default ⇒ `null` | **add an explicit arm** | `Run_Activity_WaitingForChildren`. |
| `RunStateToLabelConverter` (`:55-63`) | default ⇒ `Run_State_Completed` | **REGRESSION fix (§0.6)** | a working parent must not read **"Completed"**. |
| `RunStateToBrushConverter` (`:76-84`) | default ⇒ `TextDefaultBrush` | **add an explicit arm** | `TextDefaultBrush` is right; make it explicit so the next member is a decision. |
| `RunStateToSpinnerVisibilityConverter` (`:95-97`) | `is Planning or Running` | **add** | children **are** working; the spinner must be lit. |

`RunProgressState` gains **`WaitingForChildren`** — appended, and this enum is **not persisted** (it is a
view-facing projection, `RunProgressViewModel.cs:13-26`), so appending is free. `MapState` maps
`AgentRunState.WaitingForChildren → (RunProgressState.WaitingForChildren, false)`. **`CanContinue` must stay
false** for it (`:66` is `State == WaitingForInput && !IsResuming` — already correct; assert it, because a
`Continue` here would try to CAS a state `TryBeginResumeAsync` does not accept and silently no-op).

`RunStateToLabelConverter`'s key mapping is **extracted** to
`internal static string LabelKey(RunProgressState state)` so a theory can pin every member (§0.6, precedent
`RunProgressViewModel.DecisionLabelKey`).

**Two resx keys, all three files:**

| key | en | de | fr |
|---|---|---|---|
| `Run_State_WaitingForChildren` | `Delegating` | `Delegiert` | `Délégation` |
| `Run_Activity_WaitingForChildren` | `Waiting for the sub-agents to finish…` | `Warten auf die Unteragenten…` | `En attente des sous-agents…` |

### 5.5 06-DEPENDENT note — the fan-out park must not promote

Batch 06's **B8** puts `SafePromote` on the **two terminal arms only** — after verify and before
`CompleteAsync` on the clean arm, and before `SafeComplete` on the `PlanResult.Fallback` degrade arm — and its
**B7** states the invariant plainly: *"promotion is **terminal-only**, once per workspace"*, which is what lets
one `provisionedAtUtc` decide the promote set even across a park → resume.

A parent parking at `WaitingForChildren` (§7.3) returns through the **budget-pause shape** at
`AgentRunOrchestrator.cs:143-154` — `PinRange` → `SafePause` → `SafeOnPaused` → `return` — which is **before**
either terminal arm. So the fan-out park promotes nothing **by construction**, exactly like the existing
budget pause. Confirm the two `SafePromote` call sites are still on the terminal arms after 06 lands, then
leave the park path alone: **do not add a promote to it.** A parent that promoted mid-fan-out would consume the
single per-workspace timestamp and make its own later terminal promotion re-copy everything (06 B7).

---

## 6. G9 — `ParentRunId` producer + the narrow-for-child grant envelope

**Depends on:** G8. **Model tier:** opus. **06-DEPENDENT:** yes — §6.4.

> **BUILT 2026-07-31. Five divergences from the text below, annotated by the builder.**
>
> 1. **§6.3 DID NOT APPLY: neither fake was migrated, because G8 never landed.** G8 returned no report and
>    committed nothing (`git log` at build time topped out at `08e20ab`, G6), so `IAgentRunService` still has its
>    original 16 members and D9's three do not exist. §0.3/§0.10.5 are therefore vindicated twice over: the
>    record change alone breaks nothing. Re-measured on the tree — **59** `AgentRunCreateRequest` construction
>    sites, the deepest passing **six** positionally
>    (`AgentRunServiceTests.cs:92-93`), `PolicyJson:` named at every site that supplies it, and nothing passing a
>    seventh positional argument. The trailing `Guid? ParentRunId = null` is invisible to all 59 and the test
>    project compiled unmodified. **The fake migration is now owed by whichever group adds D9's three members
>    — G8 if it is retried, otherwise G10.**
> 2. **`NarrowForChild` takes a trailing `ILogger? logger = null`**, not the bare `(string?)` §6.4 declares. It
>    needs one to pass into `TryRestorePolicy` (rule 3) and to log the dropped-delete-like COUNT. Trailing and
>    defaulted, so §6.4's call shape still compiles verbatim.
> 3. **`TrySerializeChildEnvelope` returns a non-nullable `string`, not `string?`.** A serializer fault falls back
>    to `InteractiveEmptyEnvelopeJson` rather than to `null`, because `null` is exactly the value that makes a
>    resume apply `ResumeFloorGrants` (`{write_file}`) — which can be WIDER than the parent, i.e. the one
>    outcome §6.4 exists to prevent. That is the identical argument `InteractiveEmptyEnvelopeJson`'s own doc
>    comment already makes for `ChatSessionManager`. Its `"trigger":"User"` misreports a Schedule-parent's child;
>    acceptable because trigger is provenance and never widens a grant. The arm is unreachable in practice
>    (a `List<string>` + a class-name list cannot fault), so it is documented as a GUARD and carries no
>    red-before-green demo.
> 4. **T-ST-9 and T-ST-10 went into the EXISTING `AgentRunServiceTests.cs`, not the new
>    `AgentRunServiceChildWaitTests.cs`** of §9.6 — that file would have contained no child-wait facts, since
>    G8 owns T-ST-2..8. It still needs creating by whoever lands G8. T-ST-10 gained a second half,
>    `TheParentRunIdIndexIsAddedToAPreBatchDatabase`, covering the UPGRADE direction the "no migration block"
>    claim actually rests on (precedent: `SqliteContextTests.EnsureSchema_AddsAgentTimelineEvents_ToAPreBatchDatabase`).
> 5. **`HeadlessRunLauncherChildRunTests.cs` (§9.7) was created carrying ONLY T-CHILD-ENV-1..4**, as pure static
>    helper facts with no launcher fixture and no `runsBaseDirOverride`. **T-CHILD-1..4 belong to G10 and should be
>    appended to that file**, which is where its harness comment will need to arrive. The four ENV facts also
>    gained two small companions: a non-vacuity `Fact` (the theory's `Assert.Single` row, split out so the
>    theory itself stays a pure ⊆ claim) and a one-line pin that the theory's hand-copied interactive-envelope
>    row still equals `InteractiveEmptyEnvelopeJson`.
>
> One caveat on T-CHILD-ENV-1 for whoever reads it next: its parent-side comparand is
> `TryRestoreGrantEnvelope(parent) ?? DefaultGrantedWrites` — deliberately the FLOOR, the stricter comparand — so
> the unreadable rows (`"   "`, `"{}"`, `v:99`) compare an empty child against `{write_file}` and pass without
> weight. **The empty-set fact lives in T-CHILD-ENV-2**, which asserts it directly. Do not cite ENV-1's row count
> as coverage of the unreadable cases.

> **BUILDER NOTE (G9) — from the reconciler.**
> **The single most consequential line in this group is one assignment in an object initializer, and if you
> miss it nothing fails loudly.** Measured on the tree: `AgentRunService.CreateAsync`'s initializer
> (`:79-99`) sets `Id`, `ChatId`, `RunShape`, `State`, `TriggerKind`, `TriggerRef`, `OwnerDeviceId`, `Goal`,
> `PolicyJson`, `LedgerJson` and the timestamps — and **does not set `ParentRunId`**, even though the INSERT
> parameter (`:123`) and `MapRun` (`:599`) already handle the column. So `ParentRunId = request.ParentRunId`
> is what makes the **in-memory** `AgentRun` object correct, and that object is the one handed to
> `AgentRunOrchestrator.RunAsync` on a **fresh launch** (the DB row is never re-read first).
> Two of Batch 07's structural guarantees are `run.ParentRunId` reads inside the orchestrator: G10's **depth
> guard** (`if (run.ParentRunId is not null) → in-process`, which bounds the wall clock and the scheduled-job
> `_runLock` hold) and G10's **promote guard** (`if (run.ParentRunId is null)` around Batch 06's
> `SafePromote`, which stops a child from consuming its parent's one allowed workspace promotion). With the
> initializer line missing, both read `null` on every child: a child delegates further **and** promotes its
> parent's workspace mid-fan-out, and the only symptom is wrong files at the destination.
> So this group owes a **REGRESSION** test that `CreateAsync` returns a run whose `ParentRunId` equals the
> request's **and** that a subsequent `GetAsync` round-trips it — the in-memory half is the half no existing
> test covers.
> Batch 06 edited `HeadlessRunLauncher` heavily (provisioning at both dispatch sites, `TearDownAsync`
> replacing `TryDeleteDirectory` at three call sites, the state-aware sweep, `OnChatsChanged`). Your
> `NarrowForChild` / `TrySerializeChildEnvelope` helpers sit beside `SerializeGrantEnvelope`, which 06 did not
> touch — but re-read the file's ctor before you assume a parameter list: it gained a trailing
> `IRunWorkspaceService? workspaces = null` **after** `runsBaseDirOverride`.

### 6.1 The producer (D10)

- `AgentRunCreateRequest` gains `Guid? ParentRunId = null` **after** `PolicyJson`, with a doc comment:
  *"The parent run this run was delegated by, or null for a top-level run. Written once at create; there is
  deliberately no re-parent API. Indexed by `IX_AgentRuns_ParentRunId`."*
- `AgentRunService.CreateAsync`'s object initializer gains `ParentRunId = request.ParentRunId`. The INSERT
  parameter (`:123`) and `MapRun` (`:599`) are **already correct** — do not touch them.
- The `Created run …` log line (`:139-140`) gains `parent={HasParent}` as a **boolean**, matching the
  `policy={HasPolicy}` precedent. Not the id — an id is safe, but a boolean is what the line is for and it
  keeps the format stable.

### 6.2 The missing index (R35)

In `SqliteContext.EnsureSchema`, beside the four existing `AgentRuns` indexes (`:306-309`):

```sql
CREATE INDEX IF NOT EXISTS IX_AgentRuns_ParentRunId ON AgentRuns(ParentRunId);
```

That block runs on **every** open, so existing databases get it with **no migration block** — the same
mechanism the four existing indexes rely on. There is deliberately **no FK** on `ParentRunId`: the
parent/child cascade is `AssistantChats → AgentRuns` per chat (`:303`), a self-referencing cascade would
delete a child's history when a parent's chat is deleted, and `AgentTimelineEvents.StepId` already establishes
the house precedent for *"a deliberate non-FK reference, explained at the column"* (`:336-341`). Write that
comment.

### 6.3 The fake migration (§0.3)

Both hand-written fakes gain the three D9 members **in this commit**:
`AgentRunOrchestratorTests.cs:142` `FaultyRunService` delegates all three to `_inner`;
`BackgroundAssistantTurnRunnerRunSpineTests.cs:290` `ThrowingAgentRunService` throws from all three
(`throw new InvalidOperationException("boom")`), matching its existing style — which also means the runner's
isolation wrappers keep being exercised. The commit message must say the break came from the **interface**
members, not from the record (or the next reader re-derives R8's wrong premise).

**Measured: `IAgentRunService` is the ONLY interface this batch widens that has a hand-written implementor.**
Commit 5 also widens `IHeadlessRunLauncher` (`LaunchChildAsync`, `CancelAsync`, §7.2), and
`git grep -n ": IHeadlessRunLauncher" -- tests` returns **nothing**: all 16 test usages are
`Substitute.For<IHeadlessRunLauncher>()`, and NSubstitute absorbs new members silently. Same for
`IAgentRunResumeService` (8 usages, all substitutes). So **commit 5 needs no fake migration** — recorded here
so the next reader does not re-run the grep, and so a *future* hand-written launcher fake knows it owes two
methods.

### 6.4 The narrow-for-child grant envelope (R13)

New `internal static` helpers on `HeadlessRunLauncher`, beside `SerializeGrantEnvelope` (R36):

```csharp
    /// <summary>
    /// The grant set + policy a CHILD run inherits: a strict SUBSET of the parent's, never the default and
    /// never the resume floor. A child is a delegate — it does the work the parent asked for and it does not
    /// get to destroy anything, so every delete-like NAME is stripped even when the parent held it (the
    /// parent can still delete, in its own steps). An UNREADABLE parent envelope yields the EMPTY grant set,
    /// NOT HeadlessRunRequest.DefaultGrantedWrites and NOT ResumeFloorGrants: falling through to a default
    /// would let a child that inherits nothing readable end up WIDER than its parent, which is the one thing
    /// this helper exists to make impossible (Phase 3 R13). Pinned by T-CHILD-ENV-1..4.
    /// </summary>
    internal static (IReadOnlyList<string> Grants, RunAutonomyPolicy? Policy) NarrowForChild(string? parentPolicyJson);

    /// <summary>The child's PolicyJson: NarrowForChild's result through the existing v:1 serializer. The
    /// envelope version is NOT bumped — additive members only (envelope.V is compared with !=).</summary>
    internal static string? TrySerializeChildEnvelope(string? parentPolicyJson, AgentRunTrigger trigger, ILogger? logger = null);
```

`NarrowForChild` rules, exactly:

1. `grants = TryRestoreGrantEnvelope(parentPolicyJson) ?? []` — the **existing** reader (R36), so "readable"
   means the same thing here as at resume.
2. `grants = grants.Where(g => !ToolPermissionService.IsDeleteLike(g))` — allowed here; this file is **not** a
   `ToolAutonomyRuleTests` gate file (D18.1), and authoring-time name filtering is precedented by
   `ScheduledJobToolHandler.ParseGrantedTools`.
3. `policy = TryRestorePolicy(parentPolicyJson, logger)` — passed through **unchanged**. The policy is a class
   set that can never cover a delete-like tool (Batch 04 D6), so narrowing it further would only make a child
   unable to do the work it was delegated. It is by construction ⊆ the parent's.
4. The envelope's `trigger` for a child is the **parent's trigger kind**, passed by the caller — provenance,
   *"diagnostics only; never consulted to widen a grant"* (`HeadlessRunLauncher.cs:690`).

**The test that matters** (T-CHILD-ENV-1, GUARD): a theory over parent envelopes asserting **set containment
in both directions of failure** — child grants ⊆ parent grants (`OrdinalIgnoreCase`) **and** child policy
classes ⊆ parent policy classes — including the rows `null`, `""`, `"{not json"`, `{"v":99,…}`,
`{"v":1,"grantedWrites":["write_file","delete_file"]}` and the interactive empty envelope
`InteractiveEmptyEnvelopeJson`. Non-vacuity: at least one row must produce a **non-empty** child grant set
(otherwise "⊆" is trivially satisfied by emptiness everywhere and the test proves nothing) — assert that row
explicitly with `Assert.Single`.

---

## 7. G10 — Separate child slot pool, cascade cancel, ledger roll-up, per-run views

**Depends on:** G9. **Model tier:** opus. **06-DEPENDENT:** yes, heavily — §7.6.

> **BUILDER NOTE (G10) — from the reconciler.**
> This is the group where the two batches actually collide. The collision is **owned and already resolved
> here**; what follows is the resolution, so you do not have to rediscover it.
> 1. **Both dispatch paths in `HeadlessRunLauncher` now provision a workspace.** Batch 06 put a contiguous
>    *"ask the provisioner, else the legacy create"* block at the top of the launch dispatch **and** on the
>    resume path. Your `LaunchCoreAsync` extraction must **carry that block, not fork it**, and it takes
>    `string? workspaceRootOverride` (§7.2, reconciled): non-null ⇒ **skip `ProvisionAsync` entirely** and pass
>    the value to `executor.Initialize(workspaceRoot: …)`. `LaunchChildAsync` takes a `string?
>    parentWorkspaceRoot` and the orchestrator passes `ctx.WorkspaceRoot` — Batch 06 G1's `RunContext` member,
>    which both executors assign in `BeginRunAsync`. A child **never** provisions: Batch 06 allows exactly one
>    promotion per workspace, decided by a single `provisionedAtUtc` in the workspace metadata, and a second
>    workspace per child would also mean N `git worktree add` calls and N branches per fan-out.
> 2. **`ResumeAsync` is a separate method and needs the same rule** — §7.6 change 3. It re-creates the
>    workspace at its **own** run id and registers into `_runsByChat` in its own lock block. A parked child
>    owns a stub chat, so a user pressing **Continue** on it reaches this path. Resolve the parent's root
>    instead; never provision at a child's id.
> 3. **Batch 06's `SafePromote` sits on two terminal arms — and the child guard goes INSIDE `SafePromote`,
>    once, not around both call sites.** Measured: the two sites are the main arm (between `SafeEndRun` and
>    `SafeComplete`) and the `PlanResult.Fallback` degrade arm, which **returns early and settles
>    `SafeComplete` *before* `SafeEndRun`** — the opposite order, and the arm every launcher-harness test
>    exercises, so a guard missing *there* is invisible in the harness. A two-site wrap can therefore be
>    half-done; one early return cannot. So add it beside `SafePromote`'s existing
>    `if (_workspaces is null || string.IsNullOrEmpty(ctx.WorkspaceRoot)) return;`:
>    `if (run.ParentRunId is not null) return;` — `run` is already a parameter, and the method is already the
>    single funnel for both `PromoteAsync` **and** `TearDownAsync`. **§7.6 change 2 is reconciled to this
>    shape; keep its comment text verbatim** — it is the load-bearing explanation of why this is a data-loss
>    guard, not a tidiness guard. Do **not** also wrap the call sites: two guards for one rule is how one of
>    them later gets "simplified" away.
> 4. **Your `IHeadlessRunLauncher? childLauncher = null` goes after Batch 06's `IRunWorkspaceService?
>    workspaces = null`** on `AgentRunOrchestrator`'s ctor. 13 positional constructions in tests; both
>    batches' "existing suite passes unmodified" claims depend on every new parameter staying trailing and
>    defaulted.
> 5. **Teardown is now keyed on workspace ownership.** Measured shape, which is **not** the "three call sites"
>    06's own B12 predicted: 06 G3 centralized removal into **one private
>    `HeadlessRunLauncher.TearDownWorkspaceAsync(runId, ct)`** — it calls `_workspaces.TearDownAsync` when a
>    provisioner was injected and falls back to the documented `TryDeleteDirectory` otherwise — and that helper
>    has exactly **two** callers: the startup sweep and `OnChatsChanged`, which now cancels the run **before**
>    tearing down and does the teardown off the synchronous handler via `SafeFireAndForget`. (The orchestrator's
>    `SafePromote` calls `IRunWorkspaceService.TearDownAsync` directly; that is the third removal path and it is
>    not in the launcher.) Route any child-related teardown through that same helper. Your rule — *a run
>    dispatched with a non-null `workspaceRootOverride` is not
>    added to `_runsByChat`* — is what keeps that correct for children, on the launch path **and** the resume
>    path. Comment it at both registration sites.
> 6. **`RunProgressViewModel` has grown twice before you get there**: Batch 06 G4's publish affordance (7th
>    ctor param, terminal-only `DescribeAsync` in `RefreshAsync`) and G7's persona map (8th ctor param). Your
>    `Children` load is a **third** thing in `RefreshAsync`; keep 06's outcome read terminal-only and add
>    yours as its own guarded block. `RunProgressPanel.xaml` also grew twice — locate the timeline expander by
>    markup, not by line number.

> **BUILDER RECORD (G10) — written after the group landed. Where the tree diverges from §7 below, and why.**
> **G7 and G8 never landed.** The build order was G6 → G9 → G10; `WaitingForChildren`, D9's two park members and
> G7's roster surface do not exist on this tree. Eight divergences, all annotated at their line in code:
> 1. **Only ONE of D9's three members was added** — `GetChildRunsAsync`. The parent therefore stays `Running` for
>    the whole wait instead of parking at `WaitingForChildren`. Absorbing the park would have dragged in the
>    appended ordinal, the three `ChatSessionManager` sets (§5.4 — miss `:183` and an interactive parent is
>    treated as a foreign writer on its own session for the rest of the process), the three converter arms, the
>    D8c ledger-terminal fix, the D14 sweep reconcile and two more resx keys: all of G8, the group this file
>    calls the highest-risk in Phase 3, bolted onto the largest surface in the batch. An appended persisted
>    ordinal with no consumers is also worse than none. `AgentEnums.cs` is untouched. **G8's seam is commented in
>    place** in `TryFanOutAsync`, naming both call points.
> 2. **The `cts.IsCancellationRequested` check after the `WhenAll` stands in for `TryEndChildWaitAsync`'s CAS.**
>    Same defect either way (R11): without it the drain loop's next blind `SetStateAsync(Running)` resurrects a
>    run something else already settled. Pinned by T-FAN-8.
> 3. **§7.2's "non-null override ⇒ skip provisioning" is keyed on `parentRunId is not null` instead.** A child of
>    an unisolated parent legitimately gets a NULL root, and the literal reading would then provision at the
>    child's own id — isolating a child whose parent writes the assistant folder, so the two would not even share
>    a directory. Same rule for the `_runsByChat` non-registration. T-CHILD-6.
> 4. **§7.5's child budget is derived from the PARENT's `RunProfile`, not re-read from settings.** The parent's
>    profile already *is* `RunProfile.FromBudget(settings.Scheduled*)` on the launch path, so the numbers are
>    identical without giving the orchestrator a settings dependency — and it honours an explicit per-request
>    budget, which a settings read would silently discard. Wall clock halved, R15 cited at the seam. T-FAN-11.
> 5. **§7.3's parked-child branch must also put the step back to `Pending`.** The dispatch sets each sibling
>    `Running` (the panel highlights delegated steps), and the resume drain is `NextPendingStepAsync` — so
>    "leave the step Pending" is only true if the park RESETS it. Found red by T-FAN-6, not by reading.
> 6. **The stale-generation row settle is scoped to `WaitingForInput`.** §7.3's blanket
>    `FailAsync(oldId, …, cancelled: true)` for every non-terminal child would race a live dispatch's own
>    terminal write. `CancelAsync` covers everything this process is running; a parked child is the one shape it
>    cannot reach across a restart, and states ≥ 3 are never swept. T-FAN-10.
> 7. **The orchestrator gained a 7th ctor param, `IAssistantChatService? chats = null`** (trailing, defaulted),
>    for §7.6's "how the parent sees a child's answer". Null ⇒ the fan-out still works and the step says the work
>    ran elsewhere. T-FAN-14/15.
> 8. **`RunProgressViewModel` needed NO new ctor parameter** — the children load uses `_runService` and
>    `_timelineService`, both already injected. Fact 6's "8th param" was G7's, and G7 did not land.
>
> Also: the launcher-driving T-CHILD facts landed in **`HeadlessRunLauncherTests`**, which already owns the
> `BuildLauncher` harness, rather than in `HeadlessRunLauncherChildRunTests` (§9.7) — duplicating a 70-line
> harness to honour a file name was the worse trade. And **the step-failure replan branch was extracted into one
> local function** so the in-process and fan-out paths share one copy of the replan budget, the `KeepDone`
> re-ordinaling and the two terminal fails.

### 7.1 Why the shared `_slots` pool deadlocks (the argument, stated)

`_slots = new SemaphoreSlim(2, 2)` on the **singleton** launcher (R14). The wait is **inside** the dispatch
`Task.Run`, before the scope and the orchestrator are built (`:199`), and the release is in the `finally`
**after `orchestrator.RunAsync` returns** (`:245`).

So: parent A and parent B each hold 1 of the 2 permits. Each reaches its fan-out and awaits a child. Each
child's dispatch task calls `_slots.WaitAsync` — on the **same** semaphore, whose count is 0. Neither permit
can be released, because a permit is released only when its parent's `RunAsync` **returns**, and neither
parent's `RunAsync` can return until its child completes. This is a **permanent deadlock**, not a stall:
nothing in the process can break it, `StopAsync`'s 5-second bounded wait times out, and both runs dangle
`Running` until the next startup sweep. It needs only **two** concurrent parents, i.e. exactly the configured
cap. D7's separate pool is not a preference — it is the only shape that works.

### 7.2 `_childSlots`, and one dispatch path

```csharp
    /// <summary>
    /// Concurrency cap for CHILD runs — deliberately a SECOND semaphore, never `_slots`. A nested acquire on
    /// the shared pool DEADLOCKS: `_slots` is waited inside the dispatch task and released only after
    /// `orchestrator.RunAsync` RETURNS, so two parents holding the two permits while each awaits a child
    /// that needs a permit from the same pool can never release (07 §7.1). Consequence, stated: effective
    /// provider concurrency doubles to 2+2. That is why the roster is the opt-in (07 D1) and why a
    /// delegating run must still fit inside the envelope one scheduled job may occupy (Phase 3 R15).
    /// </summary>
    private readonly SemaphoreSlim _childSlots = new(2, 2);
```

Extract the existing launch dispatch into **one** private method and give it the pool and the parent as
parameters — do not fork it:

```csharp
    // The workspaceRootOverride parameter is what §7.6 requires: non-null ⇒ SKIP _workspaces.ProvisionAsync
    // entirely and pass the value straight to executor.Initialize(workspaceRoot: …), because a child run
    // SHARES its parent's workspace and 06 B7 allows exactly ONE promotion per workspace.
    private Task<HeadlessRunHandle> LaunchCoreAsync(
        HeadlessRunRequest req, Guid? parentRunId, SemaphoreSlim slots, string? childPolicyJson,
        string? workspaceRootOverride, CancellationToken ct);

    public Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct = default)
        => LaunchCoreAsync(req, parentRunId: null, _slots, childPolicyJson: null,
               workspaceRootOverride: null, ct);

    /// <summary>Dispatch a CHILD run of <paramref name="parentRunId"/> on the child pool, with a grant
    /// envelope narrowed from the parent's (G9) — never the launch default — and inside the PARENT's
    /// workspace (<paramref name="parentWorkspaceRoot"/>, the orchestrator's <c>ctx.WorkspaceRoot</c>; null
    /// when the parent runs unisolated, so parent and child are always in the same isolation regime, §7.6).
    /// </summary>
    public Task<HeadlessRunHandle> LaunchChildAsync(
        HeadlessRunRequest req, Guid parentRunId, string? parentPolicyJson, string? parentWorkspaceRoot,
        CancellationToken ct = default)
        => LaunchCoreAsync(req, parentRunId, _childSlots,
               TrySerializeChildEnvelope(parentPolicyJson, req.Trigger, _logger),
               workspaceRootOverride: parentWorkspaceRoot, ct);

    /// <summary>Cancel one in-flight run by id (the cascade's mechanism, 07 D16). No-op for a run this
    /// process is not running. Never throws.</summary>
    public Task CancelAsync(Guid runId);
```

`LaunchChildAsync` and `CancelAsync` go on **`IHeadlessRunLauncher`**. That interface is already registered
(`Bootstrapper.cs:503` after 06 — §0.10 A9; it was `:498` before) so `DiRegistrationTests` is unaffected, and **no new executor type is created** — so
`AgentRunBracketTests` (R30) passes unchanged: the existing launcher still owns the bracket, and
`_executingRuns.Register(chatId, run.Id)` inside `LaunchCoreAsync` now registers with the **child's** run id
for a child dispatch, which is exactly what R9 asks for.

`CancelAsync(runId)` looks up `_inflight[runId]` and `Cts.Cancel()` inside a try/catch — the same map
`StopAsync` uses, and `RemoveInflight` already guarantees a finishing dispatch cannot evict a live one
(`:508-512`).

### 7.3 The fan-out, in `AgentRunOrchestrator`

`AgentRunOrchestrator`'s ctor gains a **trailing defaulted** `IHeadlessRunLauncher? childLauncher = null`
(mandatory: 13 positional constructions, §0.2) — **after** 06's own trailing `IRunWorkspaceService? workspaces
= null` (06 §0.7). `null` ⇒ **no delegation, ever** ⇒ every existing test unchanged. No DI cycle: the launcher
is a singleton that resolves the orchestrator **lazily** from a scope (`:204`), so nothing is constructed
twice.

Inside the drain loop, **before** `SafeSetState(Running)` for a step:

```
group = ParallelGroupOf(step)                      // ExtraJson → int?, swallowing (D11)
if (childLauncher is null || group is null) → run the step in-process, exactly as today
if (run.ParentRunId is not null)          → in-process (§7.5's depth guard)
siblings = pending steps of THIS run with the same group value, ordinal order
if (siblings.Count < 2) → in-process (D11: a group of one is not a fan-out)

// D13's park leaves a child at WaitingForInput and its step Pending, so a resumed parent arrives here
// again with a Pending group. Nothing links a child to a STEP (only ParentRunId → parent), so the parent
// cannot tell that this group already has a parked child behind it — and states 3/4 are NEVER swept
// (T-ST-4), so an un-cancelled generation would sit parked forever, each owning a visible stub chat in
// the user's chat list. Cancel the previous generation before dispatching a new one. Failure-isolated.
foreach (var old in await SafeGetChildren(run.Id) where old.State is not (Completed or Failed or Cancelled)):
    await SafeCancelChild(childLauncher, old.Id)   // in-process: CancelAsync; already-parked: FailAsync(cancelled)

for each sibling:
    goal      = sibling.Intent (+ " Expected: " + ExpectedArtifact)   // BuildInstruction's own shape
    handle    = await childLauncher.LaunchChildAsync(
                    new HeadlessRunRequest(goal, run.TriggerKind, TriggerRef: null,
                        OwnerDeviceId: run.OwnerDeviceId, ProviderId: <the sibling's persona provider, or null>,
                        GrantedWrites: null, Budget: <a child profile, §7.5>),
                    parentRunId: run.Id, parentPolicyJson: run.PolicyJson,
                    parentWorkspaceRoot: ctx.WorkspaceRoot,   // 06 G1's RunContext member — §7.6, never re-provision
                    cts.Token)
    SafeSetStepStatus(sibling.Id, Running)
    record (sibling.Id, handle)

await BeginChildWaitAsync(run.Id, siblings.Count, cts.Token)          // D9: parks + closes the segment
using var reg = cts.Token.Register(() => { foreach (h) childLauncher.CancelAsync(h.RunId); })   // D16
await Task.WhenAll(handles.Select(h => h.Completion))                 // NO WaitAsync — see below
reg.Dispose()

for each (stepId, handle):                                            // D13, per child
    child = await _runService.GetAsync(handle.RunId)
    switch child?.State:
      Completed          → SafeRecordStep(stepId, Done);   SafeAddUsage(run.Id, childTokens)   // D15, once
      Failed/Cancelled   → SafeRecordStep(stepId, Failed); SafeAddUsage(run.Id, childTokens);  anyParked=false, anyFailed=true
      WaitingForInput    → leave the step Pending;                                             anyParked=true
      null/other         → SafeRecordStep(stepId, Failed) with "child run did not settle";     anyFailed=true

if (anyParked):  PinRange(); SafePause(run.Id, reason: "children-parked"); SafeOnPaused(...); return
if (!await _runService.TryEndChildWaitAsync(run.Id, cts.Token)):  return   // parent was cancelled/re-parked
if (anyFailed):  fall into the EXISTING step-failure branch (replan budget, :176-202)
else:            continue the drain loop
```

Notes that are not optional:

- **No `WaitAsync(cts.Token)` on the `WhenAll`.** `WaitAsync` returns to the caller while the children keep
  running — the parent would settle with live children writing its workspace (an orphan, and after 06 a
  concurrent writer into the run root). Cancellation is delivered **to the children** via the registration,
  and the parent still waits for their dispatch tasks to unwind. That is the no-orphans guarantee (D16.ii).
- `TryEndChildWaitAsync` returning **false** means the parent is no longer `WaitingForChildren` — cascade-
  cancelled, or re-parked by the startup reconcile in another process. **Return without settling**: whoever
  moved it owns its terminal state. A blind `SetStateAsync(Running)` here would resurrect a `Cancelled` run
  (R11).
- Every child interaction goes through a **`Safe*` wrapper or an explicit try/catch**. A launcher fault while
  dispatching sibling 3 of 4 must mark sibling 3 `Failed` and still await siblings 1–2 — never leave a
  dispatched child unawaited. Wrap the dispatch loop so a mid-loop throw falls through to the same wait.
- `ctx.RecordStep(sibling, result)` is called for each settled sibling so the replan/verify prompts see them
  — the in-process path does this at `:162` and the fan-out path must not skip it, or a replan re-plans work
  that already ran.
- **Step budget**: `ctx.RecordStep` increments `StepsExecuted` once per sibling. That is correct — they are
  the run's steps — and it is *not* the children's internal steps (D15).
- **The stale-generation cancel is not optional.** `CancelAsync` only reaches a run **this process** is
  running (`_inflight`); a child parked in a *previous* process is not there, so `SafeCancelChild` must fall
  back to `_runService.FailAsync(oldId, "superseded by a re-dispatched fan-out", cancelled: true)`. Without
  that fallback the leak survives exactly the restart path D14 exists to handle. REGRESSION test T-FAN-15.

### 7.4 Ledger roll-up (D15)

`childTokens` comes from the child's own persisted ledger. `AgentRun.LedgerJson` is a string and the `Ledger`
DTO is `private` to `AgentRunService` — but `RunProgressViewModel` already mirrors it
(`RunProgressViewModel.cs:471-484`, camelCase). **Do not add a third mirror.** Instead add to
`AgentRunService`:

```csharp
    /// <summary>Roll a settled CHILD run's token totals into its PARENT's persisted ledger — the ONE
    /// cross-run ledger write in the app (07 D15). TOKENS ONLY: a parent's WallClockMs stays its own worked
    /// time (it is parked for the whole wait, so the children's time is neither double-counted nor lost —
    /// it is visible on the children). Idempotence is the CALLER's (the parent awaits each child exactly
    /// once, and pushes only from a TERMINAL branch), which is why this is not a CAS. A crash between a
    /// child's settle and this push loses the roll-up: the child's own ledger still holds the truth and the
    /// parent's number is an aggregate convenience, not an accounting record.</summary>
    Task RollUpChildUsageAsync(Guid parentRunId, Guid childRunId, CancellationToken ct = default);
```

Wait — that is a **fourth** interface member, and D9 said three. **Decision: do not add it.** Read the child's
totals in the orchestrator by deserializing `child.LedgerJson` with a **local** minimal DTO and push through
the existing `AddUsageAsync(run.Id, stepId: null, new UsageDetails { InputTokenCount = …, OutputTokenCount = … })`
via the existing `SafeAddUsage` wrapper (`AgentRunOrchestrator.cs:345-350`). Rationale: `AddUsageAsync` is
already the run-level accrual seam the plan/replan/verify turns use (`stepId: null`), it already refreshes the
clock and raises `RunChanged`, and one more member on a 16-member interface costs both fakes another method
for no new capability. The local DTO is 3 properties and lives beside the orchestrator's other private
helpers; note at the declaration that it mirrors `AgentRunService.Ledger`'s camelCase and that
`RunProgressViewModel` has the same mirror for the same reason.

### 7.5 The child's budget, and the scheduled-job lock (R15)

A child gets `RunProfile.FromBudget(settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes)`
— the launcher's own default when `req.Budget` is null (`HeadlessRunLauncher.cs:176-177`) — with **wall clock
halved** (clamped by `FromBudget`'s own `MinWallClockMinutes = 1`). Reason: R15. `_runLock` is held from before
`LaunchAsync` across `await handle.Completion` (`ScheduledJobBackgroundService.cs:166-253`), so **no scheduled
job of either kind can dispatch for the parent's wall clock plus every descendant's**. Halving the child
envelope keeps a fan-out inside roughly the envelope one scheduled job already occupies. State the number at
the call site with R15 cited, and record in §13 that **nested delegation (a child that itself delegates) is
out of scope** — the depth guard below makes that structural, not hoped-for.

**Depth guard, mandatory:** a run whose own `ParentRunId` is non-null **never delegates**
(`if (run.ParentRunId is not null) → in-process`). One line, and it bounds the wall clock, the child pool
pressure and the `_runLock` hold to a single level. Without it a plan-shaped-like-a-tree multiplies R15 by its
depth. GUARD test T-FAN-8.

### 7.6 06-DEPENDENT: the child's workspace **and** its promotion

**Batch 06 §13.4 is addressed to this builder.** Quoting it, because it is the constraint and not an opinion:
*"A child run (Batch 07 G10) must **INHERIT the parent's workspace, not provision its own**. Nothing in 06
prevents `ProvisionAsync(childRunId, …)` from creating a second workspace whose promotion would race the
parent's. `RunContext.WorkspaceRoot` is the seam: a child's context takes the parent's value and **the child
never calls the provisioner**. … A child that promoted the shared workspace before its parent did would make
the parent's later promotion re-copy the child's output over anything the destination has accumulated since."*

Three reasons, all structural: (i) 06's **worktree** mode would otherwise mean N `git worktree add` calls and
N branches per fan-out (R5/R16); (ii) 06 **B7** allows exactly **one** promotion per workspace, decided by a
single `provisionedAtUtc` in the workspace metadata; (iii) a child promoting independently would publish half a
fan-out while its siblings still run, so a failed sibling leaves partial deliverables with no record.

**Two changes, both explicit, neither accidental:**

1. **The child never provisions.** `LaunchCoreAsync` takes a `string? workspaceRootOverride`; when non-null it
   **skips `_workspaces.ProvisionAsync` entirely** and passes the value straight to
   `executor.Initialize(workspaceRoot: …)`. `LaunchChildAsync` passes the parent's root, which the orchestrator
   reads from `ctx.WorkspaceRoot` (06 G1's new `RunContext` member). If the parent has **no** workspace
   (`ctx.WorkspaceRoot is null` — 06's no-isolation degrade, B6/B11 F10), the child gets `null` too and both
   write the assistant folder, which is coherent: parent and child are always in the same isolation regime.
2. **The child never promotes.** 06 put `SafePromote` in `AgentRunOrchestrator`'s terminal settle (B8) — and a
   child **is** a full run with its own orchestrator, so it would reach that line. Guard it:
   ```csharp
   // 06 B7/§13.4: promotion is TERMINAL-ONLY and ONCE PER WORKSPACE, decided by one provisionedAtUtc in the
   // workspace metadata. A child run SHARES its parent's workspace and must never consume that promotion —
   // the parent's own terminal settle promotes everything the whole fan-out wrote. Worse than a double
   // promotion: SafePromote TEARS THE WORKSPACE DOWN after a successful promote, so the FIRST sibling to
   // finish would delete the directory its still-running siblings are writing into (in worktree mode, a
   // `git worktree remove`). Explicit, not left to the metadata lookup missing at the child's run id: that
   // would work by accident and log a warning per child.
   if (run.ParentRunId is not null)
       return;
   ```
   **Reconciled placement: the guard is an early return INSIDE `SafePromote`, once** — beside its existing
   `if (_workspaces is null || string.IsNullOrEmpty(ctx.WorkspaceRoot)) return;` — **not two
   `if (run.ParentRunId is null)` wrappers at the two call sites.** `SafePromote` already takes `run`, and it
   is the single funnel for both `PromoteAsync` and `TearDownAsync`, so one return covers both arms and cannot
   be half-applied; the wrap can, and the arm most likely to be missed (`PlanResult.Fallback`) is the one every
   launcher-harness test drives. Pinned by T-FAN-13.

   **Measured 2026-07-31 (§0.10 A5/A6), and it raises the stakes of this guard from tidiness to data loss:**
   `SafePromote` is `AgentRunOrchestrator.cs:450-476` and its body is `PromoteAsync` → *then*
   `await _workspaces.TearDownAsync(run.Id, ct)` (`:470`), keyed on the run id it is handed. Its two call sites
   are `:128` (the `PlanResult.Fallback` degrade arm — which settles `SafeComplete` **before** `SafeEndRun`, the
   opposite order to the main path, and is the arm every launcher-harness test exercises) and `:266` (the main
   arm). **One guard inside the method covers both** (that is why the placement above is the reconciled one).
   T-FAN-13 should therefore assert two things, not one: `PromoteAsync` was never called
   for a child's run id, **and** `TearDownAsync` was never called for it either.

3. **`ResumeAsync` must obey the same rule — and this is a hole the rest of this section would otherwise
   leave open.** `HeadlessRunLauncher.ResumeAsync` is a **separate** dispatch method: it re-creates the
   workspace at **its own run id** (after 06 G3, by calling `ProvisionAsync(run.Id, …)` on the resume path)
   and it also registers into `_runsByChat` in its own `lock (_runsByChatLock) { … set.Add(run.Id); }` block.
   Every child owns a **stub chat**, so the path is reachable by an ordinary user act: open a parked child's
   chat, press **Continue** → `IAgentRunResumeService` → `ResumeAsync(childId)` → a **second workspace at the
   child's run id**, diverging from the parent's and outliving it until the sweep.
   That also **falsifies the `_runsByChat` argument made below in this same section** — which reasons that
   deleting a child's chat is a harmless no-op *because the child owns no directory at its own run id*. The
   resume path creates exactly that directory. So:
   - in `ResumeAsync`, when `run.ParentRunId is not null`, **do not call `ProvisionAsync`**. Resolve the
     override instead: `_workspaces?.RootFor(run.ParentRunId.Value)` when that directory exists, else `null`
     (the parent ran unisolated, or its workspace is already gone — in which case the child writes the
     assistant folder, the same coherent degrade as change 1). Then `Initialize(workspaceRoot: override)`.
   - apply the **same non-registration rule** as change 1: a run dispatched with a non-null
     `workspaceRootOverride` is **not** added to `_runsByChat`, on the resume path as well as in
     `LaunchCoreAsync`. Teardown is keyed on **workspace ownership**, not on run id — comment it at both
     registration sites.
   - the promote guard in change 2 already covers the resumed child (`run.ParentRunId is not null` ⇒ no
     promote), because a resume goes through the same `AgentRunOrchestrator` terminal arms.
   Pin with a REGRESSION test alongside T-CHILD-4: resume a run whose `ParentRunId` is set and assert no
   directory is created at the child's run id.

**No degrade path is offered.** Letting a child provision and promote its own workspace is the one shape 06
§13.4 names as broken, so if the override seam is awkward to add, **add the seam** — do not ship the variant.

**And the second door onto R4, which the plan does not list:** `OnChatsChanged` deletes
`Path.Combine(_runsBaseDir, runId)` for **every** run id in `_runsByChat[chatId]` (R18). Each child gets its
**own stub chat** (see below), so deleting a child's chat mid-run would delete a directory named after the
**child's** run id — which, because the child provisioned nothing, does not exist, so the delete is a no-op.
**That is true only as long as the child owns no directory at its own run id.** Make it deliberate: **do not
register a child's run id in `_runsByChat` when `workspaceRootOverride` is non-null**, and comment at the
registration that teardown is keyed on **workspace ownership**, not on run id. Pinned by T-CHILD-4.

**Why each child gets its own stub chat** rather than sharing the parent's: `ExecutingRunStore` already
supports multiple runs per chat (R16), so sharing *works* — but N concurrent children each doing
`SaveMergedAsync` full-chat replaces on one row is heavy write amplification, the interleaved transcript order
is arbitrary, and the child's chat is the natural drill-down target for D17. `LaunchCoreAsync` already creates
a stub chat per run (`:128-139`); reuse it unchanged.

**How the parent sees a child's answer:** after the await, read the child's chat
(`IAssistantChatService.GetAsync(handle.ChatId)`), take the **last non-empty assistant message**, truncate to
4000 chars (the cap `AgentPlanner.MaxAnalysisChars` already uses, `AgentPlanner.cs:46`), and feed it as the
sibling step's `VisibleText` into `ctx.RecordStep`. Without this the parent's replan and verify prompts see a
completed step with **empty** visible text and the critic judges the goal on nothing — the same failure mode
E2's `FromEarlierSegment` note was invented for. Failure-isolated: a read fault yields empty text and a
`CompletedStepSummary` that says the work ran elsewhere.

### 7.7 The panel: children list + drill-down (D17)

- `RunProgressViewModel` gains `ObservableCollection<ChildRunRowViewModel> Children` and
  `[ObservableProperty] bool _hasChildren`, filled in `RefreshAsync` from
  `_runService.GetChildRunsAsync(_runId)` — one indexed query per projection, inside a `try/catch` that logs at
  Warning and leaves the list as it was.
- `ChildRunRowViewModel` (in `Pia.ViewModels`, ends with `ViewModel` per R31): `Guid RunId`, `string Title`
  (the child's `Goal`, **SENSITIVE** — bound to UI only, never logged, exactly like `StepRowViewModel.Title`),
  `RunProgressState State`, `long InputTokens`/`OutputTokens`, `bool IsExpanded`, and its **own**
  `ObservableCollection<TimelineRowViewModel> Timeline` loaded on expand through the same
  `IAgentTimelineService.GetForRunAsync(childRunId)` the parent uses. Mutable members are
  `[ObservableProperty]` and refreshed in place by the same diff-by-id shape `SyncSteps` uses (§4.3's
  reasoning applies identically).
- **`OnRunChanged`'s filter must widen** (R22): `if (e.RunId != _runId && !_childRunIds.Contains(e.RunId)) return;`
  Without this a child's every state change is dropped and the children list never live-updates. REGRESSION
  test T-CHILD-VM-3.
- **`_childRunIds` must be an IMMUTABLE snapshot, assigned — never a mutated `HashSet`.**
  `RunChanged` fires **off** the UI thread (that is R22/G3's whole premise), so the filter **reads** this field
  from a pool thread while `Project` **writes** it on the UI thread. This repo already states the rule for the
  identical shape: `"_ownRunIds is written by the UI-thread Planned branch, so probing it from a pool thread
  would be a data race on a plain HashSet"` (`ChatSessionManager.cs:210`, R38). So:
  ```csharp
  /// Child run ids, as an IMMUTABLE snapshot REPLACED (never mutated) inside Project on the UI thread.
  /// OnRunChanged reads it from a pool thread — RunChanged fires off-thread — so a mutable HashSet here
  /// would be the exact data race ChatSessionManager.cs:210 documents for _ownRunIds. Reference assignment
  /// is atomic, so an off-thread reader always sees one consistent generation. The `e.RunId != _runId`
  /// term needs no such care: _runId is readonly.
  private ImmutableHashSet<Guid> _childRunIds = ImmutableHashSet<Guid>.Empty;
  ```
  `Project` does `_childRunIds = children.Select(c => c.Id).ToImmutableHashSet();` — one assignment, no
  mutation. GUARD test T-CHILD-VM-6.
- **No merged ordering.** State it at the `Children` declaration: `Seq` is per-run, each child has its own 500
  cap, `CreatedAt` is explicitly rejected as an ordering source (R20) — a merged view needs a new cross-run
  key designed as its own work.
- XAML: an `Expander` below the existing timeline expander, header `Run_Children_Header`, `ItemsControl` over
  `Children`, each row an `Expander` over its own `Timeline` reusing the **existing** five-column row template
  markup. Every `StaticResource` used must already appear in `RunProgressPanel.xaml`.
- **Two resx keys, all three files:** `Run_Children_Header` (en `Sub-agents` / de `Unteragenten` / fr
  `Sous-agents`) and `Run_Children_Count` (en `{0} of {1} finished` / de `{0} von {1} abgeschlossen` / fr
  `{0} sur {1} terminés`) — used via `_localization.Format`, precedent `Run_Timeline_Step` (`:361`).

---

## 8. Files to touch, by commit

| Commit | File | Change |
|---|---|---|
| **1 (G6)** | `src/Pia.Wpf/Services/Interfaces/StepPersonaSetup.cs` | **new (CRLF)** — the `(Persona, Provider, TurnSetup)` record (D6) |
| 1 | `src/Pia.Wpf/Services/StepPersonaResolver.cs` | **new (CRLF)** — the resolver, the memo cache, the four-arm fallback |
| 1 | `src/Pia.Wpf/Bootstrapper.cs` | `services.AddTransient<StepPersonaResolver>();` beside `AddTransient<HeadlessTurnExecutor>()` (`:489`) |
| 1 | `src/Pia.Wpf/Services/AgentPlanner.cs` | trailing `StepPersonaResolver?`; roster resolve in `PlanAsync`+`ReplanAsync`; roster block in both prompt builders; `PlanStepArg.PersonaKey`/`ParallelGroup`; `BuildSteps` writes `AssignedPersonaId` + `ExtraJson` |
| 1 | `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` | trailing `StepPersonaResolver?`; `_runDefault`; per-step resolve; `RunExchangeStepAsync`'s trailing `StepPersonaSetup?`; **the per-step system message** |
| 1 | `src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs` | trailing `StepPersonaResolver?`; per-step resolve **outside** the `Post`; `BuildSpec` takes the resolved triple |
| 1 | `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` | trailing ctor param; pass the resolver into `new LiveTurnExecutor(…)` (`:788`) |
| 1 | `src/Pia.Wpf/Models/AgentStep.cs` | doc-comment `ExtraJson` as the `{"parallelGroup":N}` carrier (D11); retire *"Reserved for Phase 3"* on `AssignedPersonaId` |
| **2 (G7)** | `src/Pia.Wpf/Models/AppSettings.cs` | `MaxAgentPersonaRoster`, `AgentPersonaRoster`, `Get`/`SetAgentPersonaRoster` |
| 2 | `src/Pia.Wpf/ViewModels/AssistantSettingsViewModel.cs` | trailing `IPersonaService?`; `AgentRosterOptions`; cap enforcement; load + autosave + save (R27) |
| 2 | `src/Pia.Wpf/ViewModels/AgentRosterOptionViewModel.cs` | **new (CRLF)** |
| 2 | `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml` | roster section between Autonomy (`:437`) and Scheduled (`:439`) |
| 2 | `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` | trailing `IPersonaService?`; persona map in `RefreshAsync`; `StepRowViewModel`'s three settable members + `HasPersona`; `SyncSteps` refreshes them on **both** branches |
| 2 | `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | one argument at `:397` — **and nothing else** (D18.2) |
| 2 | `src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml{,.cs}` | `AccentColorProperty` + the accent ring |
| 2 | `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml` | bind `PersonaId`/`Emoji`/`AccentColor`/`Visibility` (§4.5) |
| 2 | `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 3 keys each |
| **3 (G8)** | `src/Pia.Wpf/Models/AgentEnums.cs` | `WaitingForChildren = 8` |
| 3 | `src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs` | the three D9 members + docs |
| 3 | `src/Pia.Wpf/Services/AgentRunService.cs` | the three members; the explicit `terminal` set (`:713`); sweep statement 2 + comment (`:343-367`) |
| 3 | `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` | three `is … or …` sets (`:183`, `:556-557`, `:574`) |
| 3 | `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` | `RunProgressState.WaitingForChildren`; `MapState` + `ComputeActivity` arms |
| 3 | `src/Pia.Wpf/Converters/RunProgressConverters.cs` | `internal static LabelKey`; explicit arms in all three run-state converters |
| 3 | `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 2 keys each |
| **4 (G9)** | `src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs` | `AgentRunCreateRequest.ParentRunId` (trailing, defaulted) |
| 4 | `src/Pia.Wpf/Services/AgentRunService.cs` | `ParentRunId = request.ParentRunId`; `parent={HasParent}` in the create log |
| 4 | `src/Pia.Wpf/Infrastructure/SqliteContext.cs` | `IX_AgentRuns_ParentRunId` + the no-FK comment |
| 4 | `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` | `NarrowForChild`, `TrySerializeChildEnvelope` |
| 4 | `tests/…/Services/AgentRunOrchestratorTests.cs`, `tests/…/Services/BackgroundAssistantTurnRunnerRunSpineTests.cs` | migrate both 16→19-member fakes (§0.3) |
| **5 (G10)** | `src/Pia.Wpf/Services/Interfaces/IHeadlessRunLauncher.cs` | `LaunchChildAsync`, `CancelAsync` |
| 5 | `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` | `_childSlots`; `LaunchCoreAsync` extraction; `CancelAsync`; the `_runsByChat` ownership rule (§7.6) |
| 5 | `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` | trailing `IHeadlessRunLauncher?` (**after** 06's `IRunWorkspaceService?`); the fan-out block; the depth guard; the local ledger DTO; **`if (run.ParentRunId is null)` around `SafePromote` on both terminal arms** (§7.6) |
| 5 | `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` | `Children`, `HasChildren`, `_childRunIds` (an **immutable snapshot**, §7.7), the widened `OnRunChanged` filter |
| 5 | `src/Pia.Wpf/ViewModels/ChildRunRowViewModel.cs` | **new (CRLF)** |
| 5 | `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml` | the sub-agents expander + drill-down |
| 5 | `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 2 keys each |

Every new `.cs`/`.md` file must be **CRLF**. Never hand-edit `ViewStrings.Designer.cs` (it has drifted;
`loc:Str` resolves through `ResourceManager`).

**Every new parameter in this batch is trailing and defaulted** — on `StepPersonaResolver` (planner, both
executors, `ChatSessionManager`), on `RunProgressViewModel`, on `AssistantSettingsViewModel`, on
`AgentRunOrchestrator`, and on `AgentRunCreateRequest`. That is not tidiness: it is what makes each commit's
"the existing suite passes unmodified" claim true, and four of those types are hand-constructed with
**positional** argument lists in production and in tests (R2, R5, R21, §0.2).

---

## 9. Test plan / acceptance

Every test is labelled **REGRESSION** (it goes red without the change) or **GUARD** (it pins behaviour that is
already correct so a future change cannot quietly move it). Every REGRESSION carries a **neutralization** — how
to make it go red. Run each neutralization, watch it fail, then restore by `git checkout --` and **not** by
copying a backup: a preserved older mtime makes MSBuild skip the recompile and the "restored" run silently
exercises the mutated binary (`05-…impl.md` §12 records that trap).

**Non-vacuity is mandatory.** Any test whose subject is discovered by name, by reflection or by enumeration
needs an `Assert.Single`/count assertion or a positive control — a test over an empty type set passes, and a
rename silently turns it green.

### 9.1 `tests/Pia.Wpf.Tests/Services/StepPersonaResolverTests.cs` — NEW (CRLF), ns `Pia.Tests.Services`

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-SPR-1 | `NullAssignedId_ReturnsTheRunDefault_Unchanged` | REGRESSION | `ReferenceEquals` on all three members of the returned setup vs. the passed `runDefault`; `IPersonaService` received **no** call | make the null arm resolve anyway → red |
| T-SPR-2 | `AssignedPersona_GetsItsOwnSystemPromptAndTools` | REGRESSION | `PrepareTurn` was called with the **assigned** persona; the returned `TurnSetup.SystemPrompt` is the assigned persona's, **not** `runDefault`'s | drop the `PrepareTurn` call and reuse `runDefault.TurnSetup` → red. **This is the §0.1 fact.** |
| T-SPR-3 | `AssignedPersona_UsesItsPreferredProvider_NotTheRunOverride` | REGRESSION | persona has `PreferredProviderId = X`, `runDefault.Provider.Id = Y` → result provider id is `X` (D5) | return `runDefault.Provider` → red |
| T-SPR-4 | `ReasoningEffort_IsAppliedToACLONE_NeverToTheSharedProvider` | REGRESSION | result `ReasoningEffort` is the persona's **and** the provider instance fetched from `IProviderService` still has its original effort | drop `.Clone()` → red |
| T-SPR-5 | `UnresolvablePersona_FallsBackToTheRunDefault` | REGRESSION | `GetPersonaAsync` → null ⇒ `runDefault`; no throw | throw instead of falling back → red |
| T-SPR-6 | `UnresolvableProvider_BorrowsTheRunProvider_ButKeepsTheAssignedPersonaAndItsPrompt` | REGRESSION | persona resolves, both provider lookups return null ⇒ result `Provider` is `runDefault.Provider` **and** result `Persona`/`TurnSetup` are the **assigned** persona's (`PrepareTurn` was still called with it). The partial arm, D3.3/§3.2 | return the whole `runDefault` → the persona/prompt half reds |
| T-SPR-7 | `PrepareTurnThrows_FallsBackToTheRunDefault` | REGRESSION | composer throws ⇒ `runDefault`, one Warning, no propagation | remove the try/catch → red |
| T-SPR-8 | `ResolvesEachPersonaOnce_AcrossManySteps` | GUARD | 5 resolves of the same id ⇒ `PrepareTurn` called **exactly once** (`Assert.Single` on the received calls) | — |
| T-SPR-9 | `SuggestAgentModeIsNeverOfferedInsideARun` | GUARD | the `PrepareTurn` call's `suggestAgentModeEligible` argument is `false` | — |
| T-SPR-10 | `GetRoster_ClampsToSix_DedupesAndDropsUnknownIds` | REGRESSION | 9 configured ids incl. 2 duplicates and 1 unknown ⇒ exactly 6 distinct resolvable personas, in configured order | drop the `Take(Max…)` → red |
| T-SPR-11 | `GetRoster_IsEmptyWhenNothingIsConfigured` | GUARD | default `AppSettings` ⇒ `Assert.Empty` | — |

### 9.2 `tests/Pia.Wpf.Tests/Services/AgentPlannerRosterTests.cs` — NEW (CRLF)

Uses the existing `AgentPlannerTests` harness shape (R3): substituted `IAiClientService`, real `AppSettings`,
`PlanStream`, and a `Steps(...)` builder **extended** with `personaKey`/`parallelGroup` keys.

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-PLR-1 | `EmptyRoster_ProducesTheExactPrePhase3PlanPrompt` | REGRESSION | with no roster: the captured **system** message contains neither `personaKey` nor `parallelGroup`, and equals the string a run with a `null` resolver produces (compare the two captures) | emit the roster block unconditionally → red. **The D1 opt-in, as a test.** |
| T-PLR-2 | `NonEmptyRoster_ListsEveryPersonaNameOnce_InTheSystemMessage` | REGRESSION | 3-persona roster ⇒ each name appears exactly once; the block mentions `personaKey`; the **user** message is unchanged (D2's tokenizer note) | move the block to the user message → the user-message assert reds |
| T-PLR-3 | `AMatchedPersonaKey_LandsOnTheStepAsAssignedPersonaId` | REGRESSION | `emit_plan` with `personaKey: "Analyst"` ⇒ that step's `AssignedPersonaId` is the Analyst's id | keep `AssignedPersonaId = null` (today) → red |
| T-PLR-4 | `MatchingIsCaseAndWhitespaceInsensitive` | GUARD | `" analyst "` matches `Analyst` | — |
| T-PLR-5 | `AnUnknownPersonaKey_LeavesTheStepUnassigned_AndIsNeverLogged` | REGRESSION | `personaKey: "Gandalf"` ⇒ `AssignedPersonaId` null; the captured log lines contain a **count** and **not** the string `Gandalf` | log the key → the privacy assert reds |
| T-PLR-6 | `ParallelGroup_RoundTripsThroughStepExtraJson` | REGRESSION | `parallelGroup: 2` ⇒ `ExtraJson` parses to `{"parallelGroup":2}`; absent ⇒ `ExtraJson` is null | drop the write → red |
| T-PLR-7 | `ValidatePlan_IsUnaffectedByPersonaAndGroupFields` | GUARD | a plan whose every step names an unknown persona still returns steps (**not** `FallBackToSingleTurn`) | — |
| T-PLR-8 | `ReplanAlsoAssignsPersonas` | REGRESSION | `ReplanAsync` with a roster ⇒ the revised steps carry `AssignedPersonaId` | thread the roster into `PlanAsync` only → red |
| T-PLR-9 | `ARosterResolveFault_DegradesToTodaysPrompt` | REGRESSION | resolver throws ⇒ a valid plan, prompt without the block, one Warning | remove the swallow → red |

### 9.3 Executor parity for G6 — extend the two existing suites, do not fork them

`tests/Pia.Wpf.Tests/Services/HeadlessTurnExecutorTests.cs` (all existing facts pass **unmodified**):

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-EXH-1 | `AnAssignedStep_SendsThatPersonasSystemPrompt` | REGRESSION | the captured request's **first** message (`ChatRole.System`) is the assigned persona's prompt for the assigned step and the run persona's for an unassigned one, **within one run** | reuse `_messages[0]` for both → red. **§0.1/§3.4's trap.** |
| T-EXH-2 | `AnAssignedStep_RunsOnThatPersonasProvider` | REGRESSION | the provider handed to `RunExchangeAsync` per step | pass `_provider` always → red |
| T-EXH-3 | `AnAssignedStep_StampsThatPersonaOnItsPersistedMessage` | REGRESSION | the persisted row's `Persona.Id`/`Name`/`Emoji` | stamp `_persona` always → red |
| T-EXH-4 | `TheAccumulatingTranscriptKeepsTheRunPersonasSystemMessage` | GUARD | after a mixed-persona run, `_messages[0]` (observable through the resumed-run re-seed path) is still the run persona's prompt | — |
| T-EXH-5 | `NoResolver_IsByteIdenticalToTodaysBehaviour` | GUARD | the executor constructed without a resolver produces the same request shape as before the batch, for an **assigned** step too | — |
| T-EXH-6 | `TheDegradeTurnUsesTheRunPersona` | GUARD | `RunSingleTurnFallbackAsync` sends the run persona's prompt/provider | — |

`tests/Pia.Wpf.Tests/ViewModels/LiveTurnExecutorPlannedRunTests.cs` (all existing facts unmodified):

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-EXL-1 | `PlannedRun_CarriesThePerStepPersonaIntoTheStepSpec` | REGRESSION | a real orchestrator + real `LiveTurnExecutor` + real `ChatSession`: the step's `AssistantMessage.Persona.Id` is the assigned persona's, and the second (unassigned) step's is the run persona's | drop `BuildSpec`'s `Persona:`/`SystemPrompt:` change → red. **Executor parity — Batch 04's §3 correction records exactly this coverage gap being missed on the Live side.** |
| T-EXL-2 | `PerStepResolutionHappensOffTheUiThread` | GUARD | the substituted `IPersonaService` records the managed thread id of its call and it is **not** the test's UI-context thread | — |

### 9.4 `tests/Pia.Wpf.Tests/Models/AppSettingsAgentRosterTests.cs` — NEW (CRLF), ns `Pia.Tests.Models`

Mirrors `AppSettingsAgentPlanningTests`.

| # | Test | Kind | Asserts |
|---|---|---|---|
| T-SET-1 | `AgentPersonaRoster_DefaultsEmpty` | GUARD | `Assert.Empty(new AppSettings().AgentPersonaRoster)` — D1's default is a decision, not an accident |
| T-SET-2 | `RosterRoundTripsThroughCamelCaseJson_KeyedByOperatingMode` | REGRESSION | Personal + Business rosters survive serialize→deserialize independently. The **only** automated proof the surface *can* persist (§10 rules out a settings-VM test) |
| T-SET-3 | `SetEmptyRoster_RemovesTheKey` | REGRESSION | setting `[]` leaves `AgentPersonaRoster` without the key (no residue) |
| T-SET-4 | `GetRoster_ClampsAndDedupes` | REGRESSION | 9 ids with duplicates ⇒ 6 distinct, order preserved |
| T-SET-5 | `AgentPersonaRoster_IsAbsentFromSyncSettings` | GUARD | reflect `SyncSettings`: no member whose name contains `Roster` (R26). Non-vacuity: assert `SyncSettings` has > 10 members, so an empty/renamed type cannot pass |

### 9.5 `tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelPersonaTests.cs` — NEW (CRLF)

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-VM-1 | `AnAssignedStep_ProjectsPersonaIdEmojiAndAccent` | REGRESSION | the row's `PersonaId`/`PersonaEmoji`/`PersonaAccent` equal the persona's, `HasPersona` true | stop projecting → red. **Half of the §0.7 defect.** |
| T-VM-2 | `AnUnassignedStep_HasNoPersona_SoTheAvatarIsCollapsed` | REGRESSION | `PersonaId == Guid.Empty`, `PersonaEmoji` null, `HasPersona` **false** | leave the old `Guid?` projection → red. **The other half: no more empty box.** |
| T-VM-3 | `NoPersonaService_LeavesEveryRowPersonaLess_AndTheProjectionStillWorks` | GUARD | 6th/7th params default ⇒ steps/state/ledger all project; `HasPersona` false everywhere | — |
| T-VM-4 | `AStepRowMintedBeforeThePersonaMapLoads_IsCorrectedOnTheNextProjection` | REGRESSION | project once with a *slow* `GetPersonasAsync`, then again: the **same row instance** (assert `ReferenceEquals`) now has its persona. **The R21/R22/R23 first-projection race.** | make the three members `init`-only again, or skip them in the existing-row branch → red |
| T-VM-5 | `APersonaLookupFault_DoesNotBreakThePanel` | REGRESSION | `GetPersonasAsync` throws ⇒ steps still project, one Warning | remove the try/catch → red |

### 9.6 `tests/Pia.Wpf.Tests/Services/AgentRunServiceChildWaitTests.cs` — NEW (CRLF)

Real `SqliteContext` in a temp dir, the `Harness` shape `AgentRunOrchestratorTests` already uses (`:200-215`).

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-ST-1 | `AgentRunState_OrdinalsArePinned` | GUARD | the exact name→value map for all **nine** members, **plus** `Assert.Equal(9, Enum.GetValues<AgentRunState>().Length)` so an inserted member cannot pass, plus distinct values. **R9: no such test existed.** | — |
| T-ST-2 | `BeginChildWait_ParksTheParent_AndClosesItsLedgerSegment` | REGRESSION | state 8; `LedgerJson.segmentStartedAt` is null; `wallClockMs` frozen | omit `MoveLedgerClock(CloseSegment)` → the segment assert reds |
| T-ST-3 | `AWaitingParentSurvivesTheStartupSweep_AsWaitingForInput` | REGRESSION | parent 8 + two children `Running` → `FailInterruptedRunsAsync()` → children `Cancelled`, **parent `WaitingForInput`** with `ExtraJson` = `{"paused":true,"reason":"children-interrupted"}`, so `TryBeginResumeAsync` can then claim it (assert it returns **true**). **D14 end-to-end; §0.4's whole point.** | delete statement 2 → the parent stays 8 and the claim reds |
| T-ST-4 | `TheSweepStillCancelsOnlyStatesBelowWaitingForInput` | GUARD | one run in each of the nine states → after the sweep: 0/1/2 → `Cancelled`; 3/4 untouched; 5/6/7 untouched; 8 → `WaitingForInput`. A row per state, so a threshold change cannot hide | — |
| T-ST-5 | `TryEndChildWait_IsACAS` | REGRESSION | from 8 ⇒ true + state `Running` + a fresh open segment; from `Cancelled` ⇒ **false** and the state is still `Cancelled` | make it a blind UPDATE → the second half reds (a resurrected `Cancelled` run) |
| T-ST-6 | `TryEndChildWait_DoesNotClearExtraJson` | GUARD | a parent with `ExtraJson` set keeps it across the CAS (unlike `TryBeginResumeAsync`) | — |
| T-LED-1 | `TheLedgerTerminalTest_MatchesTheOldRangeForEveryPreExistingState_AndExcludesWaitingForChildren` | REGRESSION | theory over all nine members: `(state is Completed or Failed or Cancelled) == (state >= Completed)` for members 0–7, and **false vs. true** for 8 | restore `state >= Completed` → the `WaitingForChildren` row reds. **§0.5.** |
| T-ST-7 | `AddUsage_OnAWaitingParent_AccruesTokensWithoutReopeningTheClock` | REGRESSION | tokens accrue; `segmentStartedAt` stays null; `wallClockMs` unchanged. **D15's tokens-only rule.** | make the roll-up open a segment → red |
| T-ST-8 | `GetChildRuns_ReturnsOnlyTheChildren_InCreationOrder` | REGRESSION | 2 children + 1 unrelated run ⇒ exactly the 2, ordered; a childless parent ⇒ `Assert.Empty`; the returned rows carry an empty `Plan` (the deliberate no-`LoadSteps`) | drop the `WHERE ParentRunId` → red |
| T-ST-9 | `CreateAsync_RoundTripsParentRunId` | REGRESSION | create with `ParentRunId: p` → `GetAsync` returns it; null stays null | drop the initializer line → red |
| T-ST-10 | `TheParentRunIdIndexExists` | GUARD | `PRAGMA index_list(AgentRuns)` contains `IX_AgentRuns_ParentRunId`. Non-vacuity: assert the four pre-existing indexes are also present | — |

### 9.7 `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherChildRunTests.cs` — NEW (CRLF)

Drive the real launcher with `runsBaseDirOverride` pointed at a temp dir — never the user's `LOCALAPPDATA`.

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-CHILD-ENV-1 | `AChildEnvelopeIsNeverWiderThanItsParents` | GUARD | the containment theory of §6.4, **with** the `Assert.Single` non-vacuity row. **R13.** | — |
| T-CHILD-ENV-2 | `AnUnreadableParentEnvelopeYieldsNoChildGrants_NotTheDefaultAndNotTheFloor` | REGRESSION | `null`/garbage/`v:99` ⇒ `Assert.Empty(grants)`, and the serialized child envelope restores to an **empty-but-present** list (not null) | fall back to `DefaultGrantedWrites` → red |
| T-CHILD-ENV-3 | `DeleteLikeGrantsAreStrippedEvenWhenTheParentHeldThem` | REGRESSION | parent `["write_file","delete_file"]` ⇒ child `["write_file"]` | drop the `IsDeleteLike` filter → red |
| T-CHILD-ENV-4 | `TheChildEnvelopeStaysAtV1` | GUARD | the emitted JSON contains `"v":1`; `TryRestoreGrantEnvelope` of it is non-null. An exact-equality version check makes a bump catastrophic (R36) | — |
| T-CHILD-1 | `LaunchChild_PersistsTheParentRunId` | REGRESSION | the created run's `ParentRunId` | pass null → red |
| T-CHILD-2 | `LaunchChild_TakesTheChildPoolNotTheSharedPool` | REGRESSION | saturate `_slots` with 2 blocked top-level runs, then `LaunchChildAsync` and assert it **starts** within a bounded wait (`Task.WhenAny` + timeout, never an unbounded await — the shape `LiveTurnExecutorPlannedRunTests` adopted so a hang **fails** instead of wedging the suite). **§7.1's deadlock, as a test.** | point `LaunchChildAsync` at `_slots` → the bounded wait times out and the test reds |
| T-CHILD-3 | `CancelAsync_CancelsOneRunAndIsANoOpForAnUnknownId` | REGRESSION | the named run settles `Cancelled`; an unknown id neither throws nor disturbs the other run | — |
| T-CHILD-4 | `AChildWithAnInheritedWorkspaceIsNotRegisteredForChatDeleteTeardown` | REGRESSION | delete the **child's** chat mid-run ⇒ the **parent's** workspace directory still exists. **§7.6's second door onto R4.** | register the child in `_runsByChat` → red |

### 9.8 `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorFanOutTests.cs` — NEW (CRLF)

A substituted `IHeadlessRunLauncher` returning `HeadlessRunHandle`s whose `Completion` the test controls
(`TaskCompletionSource`), over the real `AgentRunService`. **Every await in this file is bounded** — a fan-out
bug hangs, and a hung suite is worse than a red one.

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-FAN-1 | `NoLauncher_NeverDelegates` | GUARD | a plan whose steps share a `parallelGroup`, orchestrator built **without** the launcher ⇒ every step runs in-process, the run completes, `LaunchChildAsync` never called | — |
| T-FAN-2 | `StepsWithoutAParallelGroup_RunSequentiallyInProcess` | GUARD | `LaunchChildAsync` never called (D11's absence-means-sequential) | — |
| T-FAN-3 | `AGroupOfOne_IsNotAFanOut` | REGRESSION | one step with `parallelGroup: 1` ⇒ in-process | drop the `< 2` check → red |
| T-FAN-4 | `TwoSiblings_LaunchInParallel_AndTheParentParks` | REGRESSION | both `LaunchChildAsync` calls happen **before** either completion resolves; the parent's state reaches `WaitingForChildren`; both steps are `Running` | await each child before launching the next → the parallelism assert reds |
| T-FAN-5 | `WhenEveryChildCompletes_TheStepsAreDone_TheParentResumes_AndTokensRollUp` | REGRESSION | steps `Done`; parent back to `Running` then `Completed`; parent ledger `InputTokens` == the sum of the children's, pushed **once** per child | drop `SafeAddUsage` → the ledger assert reds |
| T-FAN-6 | `AFailedChild_FailsItsStep_AndFeedsTheParentsReplanBudget` | REGRESSION | the step is `Failed`; `ReplanAsync` was called once; the replan's revised steps run | route a failure to `Done` → red |
| T-FAN-7 | `AParkedChild_LeavesItsStepPending_AndReParksTheParent` | REGRESSION | child ends `WaitingForInput` ⇒ that step is **still `Pending`**, parent is `WaitingForInput` with reason `children-parked`, `OnPausedAsync` was called, `EndRunAsync` was **not**. **§0.9/D13 — the highest-risk hole.** | treat `Completion` as terminal (mark the step `Done`) → red |
| T-FAN-8 | `AChildRunNeverDelegatesFurther` | GUARD | a run with `ParentRunId` set and a grouped plan ⇒ `LaunchChildAsync` never called (§7.5's depth guard) | — |
| T-FAN-9 | `CancellingTheParentCancelsEveryChild_AndTheParentWaitsForThemToUnwind` | REGRESSION | cancel the external token ⇒ `CancelAsync` called for every dispatched child id, **and** the parent's `RunAsync` has not returned until every `Completion` resolved (assert ordering with a flag set in the completion continuation). **D16's no-orphan property.** | replace the `WhenAll` with `WhenAll(...).WaitAsync(cts.Token)` → the ordering assert reds |
| T-FAN-10 | `WhenTheParentIsNoLongerWaiting_TheLoopStopsInsteadOfResurrectingIt` | REGRESSION | flip the parent to `Cancelled` while children run ⇒ `TryEndChildWaitAsync` false ⇒ `RunAsync` returns without writing `Running` or `Completed` | make it a blind `SetStateAsync(Running)` → red |
| T-FAN-11 | `ADispatchFaultMidFanOut_StillAwaitsTheAlreadyLaunchedSiblings` | REGRESSION | `LaunchChildAsync` throws on sibling 2 of 3 ⇒ sibling 1's completion is awaited, sibling 2's step is `Failed`, no unawaited child | remove the wrap → the "awaited" assert reds |
| T-FAN-12 | `AChildsVisibleAnswerReachesTheParentsContext` | REGRESSION | the child chat's last assistant text appears in the parent's replan prompt (via `ctx.CompletedSteps`) | skip the chat read → red. **§7.6's empty-critic failure mode.** |
| T-FAN-13 | `AChildRunPromotesNothing_AndTearsNothingDown_OnlyTheParentDoes` | REGRESSION | run a child (`ParentRunId` set) to `Completed` with a fake `IRunWorkspaceService`: **neither** `PromoteAsync` **nor** `TearDownAsync` was called for the child's run id, and **both** were called exactly once for a parentless run (`Assert.Single` on each — the positive control, without which the test passes on a build where promotion was deleted outright). The `TearDownAsync` half is the one that matters most: `SafePromote` tears the workspace down at `AgentRunOrchestrator.cs:470`, so an unguarded child deletes the directory its siblings are still writing into (§0.10 A6). Run it for **both** arms — the main one and the `PlanResult.Fallback` degrade one (`:128`), whose settle order is reversed; one in-method guard serves both, and driving both arms is what proves it. **06 B7/§13.4.** | drop the `run.ParentRunId is not null` early return inside `SafePromote` → the child half reds on both arms |
| T-FAN-14 | `AChildInheritsTheParentsWorkspaceRoot_AndNeverProvisions` | REGRESSION | the parent's `ctx.WorkspaceRoot` is what reaches the child's `executor.Initialize(workspaceRoot:)`, and the fake `IRunWorkspaceService.ProvisionAsync` was called **once** (for the parent), not once per run | let `LaunchCoreAsync` provision for a child ⇒ the call count reds |
| T-FAN-15 | `AReDispatchedFanOut_CancelsTheParkedGenerationFirst` | REGRESSION | park a child (`WaitingForInput`) ⇒ parent re-parks ⇒ resume the parent ⇒ the **old** child ends `Cancelled` and exactly two children exist for the parent (`GetChildRunsAsync` count == 2, one `Cancelled` + one live). Second row: the old child is **not** in `_inflight` (simulate a restart by disposing the launcher's tracking) ⇒ it still ends `Cancelled` via the `FailAsync` fallback. **§7.3's generation leak.** | drop the cancel loop → the count reaches 3 and a `WaitingForInput` child survives |

### 9.9 `tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelChildrenTests.cs` — NEW (CRLF)

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-CHILD-VM-1 | `ChildrenProject_WithStateAndTokens` | REGRESSION | two child rows, right state and tokens, `HasChildren` true; a childless run ⇒ `Assert.Empty` + false | drop the projection → red |
| T-CHILD-VM-2 | `ExpandingAChildLoadsThatRunsTimeline_NotTheParents` | REGRESSION | `GetForRunAsync` was called with the **child's** id; the parent's own `Timeline` is untouched. **D17's no-merge rule.** | pass `_runId` → red |
| T-CHILD-VM-3 | `AChildsRunChanged_RefreshesThePanel` | REGRESSION | raise `RunChanged(childRunId)` ⇒ the children list re-projects. **R22's dropped-event filter.** | restore the bare `e.RunId != _runId` filter → red |
| T-CHILD-VM-4 | `TheChildRowCarriesNoPayload` | GUARD | reflect `ChildRunRowViewModel`: no member whose name suggests a path/args/result — mirroring `RunProgressViewModelTimelineTests`' existing assert. Non-vacuity: assert the expected member count | — |
| T-CHILD-VM-6 | `TheChildIdSnapshotIsImmutable_SoTheOffThreadFilterCannotRace` | GUARD | reflect the `_childRunIds` field: its type implements `System.Collections.Immutable.IImmutableSet<Guid>`. A structural pin, because the race it prevents is not reproducible on demand — the behavioural half is T-CHILD-VM-3, and this is the half that stops a "simplification" back to `HashSet` (`ChatSessionManager.cs:210`, R38) | — |
| T-CHILD-VM-5 | `WaitingForChildren_ProjectsItsOwnStateAndDoesNotOfferContinue` | REGRESSION | state 8 ⇒ `RunProgressState.WaitingForChildren`, `CanContinue` **false**, `CurrentActivity` is the new key | map 8 through the default arm → red |

### 9.10 Converters + architecture

| # | Test | File | Kind | Asserts |
|---|---|---|---|---|
| T-CONV-1 | `EveryRunProgressState_HasItsOwnLabelKey_AndOnlyCompletedReadsCompleted` | `tests/Pia.Wpf.Tests/Converters/RunProgressConvertersTests.cs` **NEW (CRLF)** | REGRESSION | theory over `Enum.GetValues<RunProgressState>()` on `RunStateToLabelConverter.LabelKey`: `Run_State_Completed` is returned **only** for `Completed`/`TruncatedCompleted`; every other member gets a distinct key; the key count matches the member count. **§0.6 — neutralize by removing the `WaitingForChildren` arm.** |
| T-CONV-2 | `TheSpinnerIsLitWheneverWorkIsHappening` | same | REGRESSION | `RunStateToSpinnerVisibilityConverter`: `Visible` for `Planning`, `Running` **and `WaitingForChildren`**; `Collapsed` for the rest (a row per member) |
| T-CONV-3 | `EveryRunStateLabelKeyResolvesInAllThreeLocales` | extend `Architecture/LocalizationTests.cs` | GUARD | every key `LabelKey` can return is present in `ViewStrings.resx`, `.de.resx`, `.fr.resx`. Non-vacuity: assert the key set is non-empty and ≥ 7 |
| T-ARCH-1 | `TheThreeToolGateFilesAreUnchangedByThisBatch` | — | — | **not a new test**: `ToolAutonomyRuleTests`' three theory rows must pass **unmodified** (D18). If one goes red, a gate token was added to a gate file — fix the code, not the rule. |
| T-ARCH-2 | `AgentRunBracketTests` | — | — | **unmodified**: no new executor type (§7.2). |
| T-ARCH-3 | `DiRegistrationTests` | — | — | **unmodified**: `StepPersonaResolver` is registered as a **concrete** type and this batch adds no interface to `Pia.Services.Interfaces` (D6). |
| T-ARCH-4 | `DependencyInjectionTests` ViewModel ratchet | — | — | **unmodified**: nothing in `RunProgressViewModel`, `ChildRunRowViewModel`, `AgentRosterOptionViewModel` or `AssistantSettingsViewModel` may name `System.Windows` (R34). `HexToBrushConverter` lives in `Pia.Converters`, which is not a ViewModel. |
| T-ARCH-5 | `NamingConventionTests` | — | — | **unmodified**: `StepPersonaResolver` (allowlisted `Resolver`), `StepPersonaSetup` (a record in `Pia.Services.Interfaces`, not the root), three `…ViewModel`s (R31). |
| T-OPT-1 | `AnEmptyRoster_MeansNoStepIsEverAssigned` | `tests/Pia.Wpf.Tests/Services/AgentPlannerRosterTests.cs` | REGRESSION | already T-PLR-1's sibling: default settings ⇒ every produced step has `AssignedPersonaId == null` and `ExtraJson == null` |
| T-OPT-2 | `AnEmptyRoster_MeansNoRunEverDelegates` | `…/AgentRunOrchestratorFanOutTests.cs` | GUARD | a plan built by the real planner with default settings ⇒ `LaunchChildAsync` never called |
| T-OPT-3 | `AnEmptyRoster_LeavesEveryStepRowPersonaLess` | `…/RunProgressViewModelPersonaTests.cs` | GUARD | `HasPersona` false for every row (= T-VM-2 generalized) |

**Together T-OPT-1..3 are the D1 invariant**: *the batch is off until the user configures a roster.* Keep them
as three separate facts in three layers — one per seam that could turn it on by accident.

---

## 10. Manual-smoke debt (no automated coverage exists) — folds into Rank 1

Phase 3 **lengthens** the manual smoke list; it does not shorten it (`phase3-workflow-plan.md` §0/§8). Batch
07's additions:

1. **The roster surface persists across restart.** Settings → Assistant → Agent runs shows "Step specialists";
   check two personas, restart, still checked. A `Binding` path typo renders a checkbox that silently never
   persists, and **no test parses `Pia.Views.SettingsViews.AssistantView`** (Batch 12's
   `AssistantViewParseTests` parses the same-named *chat* view — a different type in a different namespace).
2. **A real plan really assigns different personas to different steps, with the right provider each.** Two
   roster personas with different `PreferredProviderId`s; run a two-part goal; confirm from the log's
   `provider={ProviderId}` lines that the steps used different providers, and from the transcript that the two
   replies read like different personas (the system-prompt half, §0.1 — the only end-to-end proof).
3. **Per-step avatars render.** The currently-empty 20×20 box shows a glyph for an assigned step and
   **nothing** for an unassigned one, with the accent ring visible on a persona that has an `AccentColor`. This
   lands inside a deferred `ItemsControl.ItemTemplate` that no test materializes (R11/D19).
4. **A parent with parallel children**, the batch's whole point: siblings run at the same time, the panel shows
   "Delegating" **and a lit spinner** (not "Completed" — §0.6), the sub-agents list fills in, expanding a child
   shows **that child's** trace, the parent's token total ends ≥ the sum of the children's, and cancelling the
   parent cancels every child with **no orphan left running** (`Task Manager` / the log's per-run settle lines).
5. **The parent survives an app restart in its new waiting state.** Kill the app mid-fan-out; on restart the
   children are `Cancelled` and the parent shows the WaitingForInput "continue?" affordance; click **Continue**
   and the fan-out steps re-dispatch (D14 end-to-end).
6. **A child parked at its own budget.** Set `ScheduledMaxSteps` very low so a child parks; confirm the parent
   also parks and that **one** Continue on the parent brings the whole thing back (D13 — the piece with the
   most machinery behind it and the least visible symptom when wrong).
7. **DE/FR without clipping** for all seven new strings — the roster description is the longest string in the
   Agent-runs pane, and `Run_State_WaitingForChildren` sits in a narrow header chip.
8. **06 × 07 interaction:** in **worktree** mode, a fan-out produces **one** branch (the parent's), not N, and
   `git worktree list` after the run shows no stale registration (§7.6). In **copy** mode, the promoted set
   contains the children's files as well as the parent's — one promotion, one destination, no duplicates
   (06 B7's single-timestamp invariant, which a child promotion would break).

---

## 11. Guardrails, instantiated for this batch

- **Failure-isolated bookkeeping (`Safe*`).** Every new failure site is swallowed: the four-arm persona
  fallback (D3); the roster resolve in the planner; the `parallelGroup` parse (D11); the persona-map load in
  the panel (§4.4); the children query and the child-timeline read (§7.7); the child-chat read for the
  parent's context (§7.6); the ledger roll-up (through the existing `SafeAddUsage`). **A per-step persona, a
  fan-out bookkeeping write and an attribution lookup must never fail a step or a run.** The only new
  **critical-path** additions are the fan-out `await` itself and `TryEndChildWaitAsync`'s false branch, which
  *stops* the loop rather than corrupting it.
- **No interactive regression.** `ChatSession.RunStepTurnAsync` is **not modified** (R8 — `spec` already
  carries everything). `SetState(WaitingForTool)` → `finally` → `Running` is untouched; the card-before-execute
  ordering is untouched; no gate file changes (D18). The three `ChatSessionManager` set additions exist
  precisely to keep an interactive parent from being mistaken for a foreign writer on its own session
  (§5.4/R38).
- **Executor parity, twice.** G6 lands on **both** executors and is tested on both (T-EXH-1..6 headless,
  T-EXL-1..2 live) — Batch 04's §3 post-review correction records exactly this parity gap being missed on the
  Live side, so the Live fact is not optional. G10's delegation is likewise available on both (D12), which is
  why §5.4's `ChatSessionManager` fixes are regressions rather than guards.
- **Off-thread `RunChanged` stays marshaled (G3).** `RunProgressViewModel` gains a widened `OnRunChanged`
  filter and two new collections; **every** mutation still goes through `_uiContext.Post` (`:192`, `:320`).
  `_childRunIds` is touched only inside `Project`, i.e. on the UI thread — say so at the field. New
  `RunChanged` raises come from `BeginChildWaitAsync`/`TryEndChildWaitAsync`, both **outside** `_gate`, matching
  every sibling.
- **Append-only persisted enums and ordinals.** `AgentRunState.WaitingForChildren = 8` is **appended**;
  `Paused(4)` is untouched (Batch 08). `RunProgressState` is **not persisted** (view projection) and appending
  is free. `ToolClass`/`ToolGateDecision`/`ToolGateSurface` are **not touched at all**. The grant envelope
  stays at **`v:1`** — additive members only, because `envelope.V != 1` is an exact-equality check and a bump
  makes every persisted envelope unreadable at once (R36).
- **Privacy-first logging.** New Information-and-above lines carry **counts, booleans, enum values and ids
  only**: `"{ChildCount} child run(s)"`, `"parent={HasParent}"`, `"Plan assigned {DroppedCount} step(s) to an
  unknown persona"`, `"Run {RunId} → WaitingForChildren"`. **Never logged**: a persona **name** (user-named),
  a model-emitted `personaKey` (echoes a user-named item), a child run's **Goal** or a step **Title** (already
  `SensitiveDebug`-only), a **path or filename** — and remember there is **no `SensitiveError`**: the highest
  DEBUG-erased severity is `SensitiveWarning`, and `SafeUrl` does not apply to paths.
- **A new user-visible string lands in all three resx files** — **7 keys × 3 files** with real DE and FR
  (§4.2, §5.4, §7.7). `ViewStrings.Designer.cs` stays untouched.
- **Code style.** 4-space C#, 2-space XAML, `_camelCase` fields, `var` for apparent types,
  `[ObservableProperty]`/`[RelayCommand]`, namespaces `Pia.*` (**not** `Pia.Wpf.*`). New `.cs` files **CRLF**.
- **Do not push, merge or rebase.** The branch is unpushed by owner decision and ~49 commits ahead of
  `origin`. Commit locally, nothing else.

---

## 12. Commit plan (each independently buildable and green)

| # | Commit | Contents | Green means |
|---|---|---|---|
| 1 | `Agent runs: resolve persona, provider and prompt per step` | G6 — §8's commit 1 rows; T-SPR-*, T-PLR-*, T-OPT-1, T-EXH-*, T-EXL-* | **every existing planner, executor and orchestrator test passes UNMODIFIED.** That holds only because every new parameter is trailing and defaulted (§8) and `AgentRunOrchestrator` is untouched (§0.2). If one of those files needs an edit, a parameter was made required — fix the parameter, not the test. |
| 2 | `Agent runs: a step-specialist roster and per-step attribution` | G7 — §8's commit 2 rows; T-SET-*, T-VM-*, T-OPT-3 | `LocalizationTests` green (3 keys × 3 locales); `RunProgressViewModelTests` and `AssistantViewModel`'s suites unmodified; the always-empty avatar box is gone |
| 3 | `Agent runs: a persisted state for a parent awaiting children` | G8 — §8's commit 3 rows; T-ST-1..7, T-LED-1, T-CONV-*, T-CHILD-VM-5 | `AgentRunServiceTests` (existing) unmodified; the sweep still cancels exactly 0/1/2; nothing yet writes state 8 — this commit is the **vocabulary and the machinery**, not the behaviour |
| 4 | `Agent runs: create a child run with a narrowed grant envelope` | G9 — §8's commit 4 rows **including both fake migrations** | the two fakes compile; `HeadlessRunLauncherTests`' seven envelope facts pass unmodified; T-CHILD-ENV-*, T-ST-8..10. Still nothing spawns a child |
| 5 | `Agent runs: parallel sub-agents on their own slot pool` | G10 — §8's commit 5 rows; T-CHILD-1..4, T-FAN-1..15, T-CHILD-VM-1..4 + T-CHILD-VM-6, T-OPT-2 | the **first** commit where a run can delegate. `AgentRunBracketTests`, `ToolAutonomyRuleTests`, `DiRegistrationTests`, `NamingConventionTests` and the ViewModel ratchet all pass **unmodified**, and **Batch 06's promotion facts pass unmodified** — T-FAN-13/14 are what prove a child neither provisions nor promotes (06 B7/§13.4) |

**Stop-clean boundary.** Commits 1–2 are shippable on their own: per-step personas with attribution, no child
runs, no new persisted ordinal. If Batch 07 has to be cut short, **cut after commit 2** — commit 3 introduces
a persisted ordinal and commit 4 an interface change, and both are the irreversible-ish part.

A builder that cannot get the gate green **stops and reports** rather than committing red.

---

## 13. Open questions (none blocking)

1. **Nested delegation is out of scope, structurally.** §7.5's depth guard means a child never delegates. That
   is a deliberate bound on R15 (the `_runLock` head-of-line hold spans the parent's wall clock **plus every
   descendant's**) and on child-pool pressure. Whoever lifts it must revisit that lock first, and must decide
   what `_childSlots` means at depth 2 — a third pool does not scale, and one shared child pool re-creates
   §7.1's deadlock one level down.
2. **A merged parent+child timeline needs a new cross-run ordering key.** `Seq` is per-run, `CreatedAt` is
   explicitly rejected (~1 ms vs. sub-ms tool calls, `SqliteContext.cs:342-343`), and each child has its own
   500-event cap. D17 ships two per-run views with a drill-down. A merged view is its own work: it needs a
   monotonic allocator shared across runs, which is a new durable counter with its own contention story.
3. **`AgentRun` has no persona column**, so the panel cannot say *which* persona ran an **unassigned** step
   (§4.3) — hence the collapse-the-avatar decision. Adding `AgentRuns.PersonaId` would fix it and would also
   let a resume restore the launch's persona instead of re-resolving the current default
   (`HeadlessRunLauncher.cs:283-286` flags that assumption today). That is a schema change plus a resume
   semantics change; it belongs in its own batch.
4. **The roster does not sync.** Deliberate (R26: every `Agent*` knob is local-only), but it means a
   multi-device user configures a roster twice, and a synced *plan* can name a persona the other device's
   roster does not include — which degrades to the run persona (D3) rather than failing. If `Agent*` knobs ever
   join `SyncSettings`, the roster's `Guid` list must be validated against local personas on read, exactly as
   `GetAgentPersonaRoster` already does.
5. **A model-authored `parallelGroup` is a trust boundary.** The planner's own model decides which steps run
   concurrently, and a wrong grouping means concurrent writes into one workspace. Today's containment is that
   all writes are contained (06) and the grant set is narrowed (G9), so the blast radius is "two steps write
   the same file". If write arbitration lands (Batch 10), this is one of its inputs.
6. **`AgentStep.DependsOnJson` is still unused.** D11 deliberately did not colonize it, so a real DAG remains
   possible later. Whoever builds it must reckon with `KeepDoneAsync` **re-ordinaling** Done steps 0..k-1 on
   every replan (`AgentRunOrchestrator.cs:287-288`) — ordinal-keyed dependencies move under a replan, which is
   why D11 used a group marker instead.
7. **The child pool size is a hardcoded 2**, mirroring `_slots`. It is not user-configurable and it interacts
   with provider rate limits (2 parents × 2 children × the in-step tool loop). If it ever becomes a setting, it
   belongs next to the `Scheduled*` budget knobs and needs R15 re-derived.
8. **A fan-out's promotion is all-or-nothing, and it is the parent's.** Because 06 B7 allows one promotion per
   workspace, a fan-out where three children succeed and one fails still promotes **everything the workspace
   contains** on the parent's terminal settle — including the failed child's partial output, if it wrote any.
   Per-child promotion sets would need a per-child timestamp, i.e. 06's metadata gaining a list, and a story
   for what "promote the successful half" means when siblings edited the same file. Named, not fixed; the
   publish affordance (06's D3 surface) is where a user sees the result either way.
9. **`ScheduledJobBackgroundService._runLock` is untouched.** Nested child work extends a **process-wide**
   head-of-line block by every descendant's wall clock (`:166-253`). §7.5 halves the child envelope to keep a
   fan-out inside roughly one job's existing occupancy, but the lock itself is unexamined. **Do not add
   delegation to a new scheduled-job kind without revisiting it.**
