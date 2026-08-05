# Batch 18 — Goal grounding & mid-run clarification — ✅ SHIPPED

**Phase 3 · `feature/agent-run-spine` · `e3938d7` (single commit)**

This file is now **half design record, half build record.** §0–§6 describe the problem and the seams as they
were *before* the build and are left as written — deleting the reasoning would invite someone to re-litigate
choices that were made with a reason. §7 and §10 have been updated in place with what shipped and what the
owner answered. The build record is the table under "What shipped" immediately below.

> **Build:** `dotnet build -t:Rebuild -p:EnableWindowsTargeting=true` → **0 Warnung(en) / 0 Fehler**, and the
> same under `-c Release`. Measured on the finished tree, then re-measured after the line-ending normalisation
> that followed it. The baseline before the batch was also 0/0, so no warning is inherited or excused.
>
> **Tests: WRITTEN, NOT EXECUTED.** `net10.0-windows` tests cannot run on macOS, which is where this batch was
> built. What was verified is that the whole suite *compiles* in both configurations. **Nine new test classes
> have never been executed on any platform** — `GoalGroundingReproTests`, `AgentRunResumeNoRePlanPremiseTests`,
> `AgentRunClarificationResumeTests`, `MidPlanAskTests`, `UserInputRequestSignalTests`,
> `ChatSessionMidPlanAskTests`, `GoalClarificationLoggingRuleTests`, `GoalPreflightTests`,
> `AssistantViewModelGoalPreflightTests` — and thirteen existing classes gained unexecuted facts. No pass/fail
> is claimed anywhere in this file. The first Windows/CI run is the first real signal; the highest-risk
> classes to watch are named in the "What CI gates on" note below.

**The original spec's framing, kept:** *Spec only. No code was written or changed in the session that produced
this file.* Every `src/` reference below was re-read at `139b377d` (the tip of `feature/agent-run-spine`), not
carried over from an earlier note. **`src/` and `tests/` were still byte-identical to `139b377d` when the
build session opened**, so every line number in §6 was re-verified and held, with one drift:
`CanExecuteRunInBackground` was at `:819-820`, not `:818-819`.

---

## What shipped

| Group | What landed | Note |
|---|---|---|
| **G1** | `GoalPreflight` — refuses a goal with **no whitespace AND ≤ 8 chars**, plus `Assistant_GoalTooShort_Hint` in the composer | A conjunction, so any multi-word goal passes unconditionally. Layer 1 is deliberately narrower than layer 2 and its comment says so (§10.1) |
| **G2** | `emit_plan` gains a decline member; a **third** `PlanResult` outcome; orchestrator routing | The decline **short-circuits the firm retry** — see §10 Q&A below. Never reaches `RunSingleTurnFallbackAsync` (§8.2) |
| **G3** | `needs-goal` + `needs-input` tokens, 4 loc keys × 3 resx, token-keyed card body, panel activity strings, question posted to the run's chat | `RunPauseEnvelope` gained **no** `ReadPauseQuestion` sibling — §4.6's argument beat §6's suggestion |
| **G4** | The `if (!resume)` guard becomes reason-aware; `AgentRuns.ClarificationsJson` column + `MigrateSchema` entry | The `:186` comment **amended in this same commit**, per §4.1. `Goal` column left exactly as the user typed it |
| **G5** | `request_user_input` — step turns only, pre-route intercept in **both** executors, **refused for delegated steps** | No cap (`18 D4`). `emit_step_result`'s outcome bool untouched (`18 D6`), verified across the whole diff |
| **G6** | The interactive path, opened by reproducing the defect **as a test** | Targets the run-progress **panel**, not the Flow card — §4.5's `:176-178` filter suppresses the card for exactly the chat being watched |

**Two premise-pinning test classes landed ahead of the code that relies on them**, following Batch 08 G1's
precedent, and one of them changed the plan:
`AgentRunResumeNoRePlanPremiseTests` **measured that `08 D1` read literally was already false** — a
`resume: true` dispatch reaches `ReplanAsync` *and* `ReplaceStepsAsync` today on a failed verdict. The
invariant constrains the **planning block**, not the planner. It also pinned what a zero-step resume does
today: it drains nothing, passes the critic on an empty completed-step list, and settles **Completed** — i.e.
it would answer the user's clarification by declaring their goal done. Both facts are now cited in the amended
`:186` comment. This is exactly the value the spec predicted at §7 G4 from a premise that had been *read*
rather than measured.

**What CI gates on, highest risk first:** (1) `MidPlanAskTests` and `ChatSessionMidPlanAskTests` — end-to-end
through real SQLite + the real launcher + the real orchestrator on polling helpers;
`ARunMayParkToAskMoreThanOnce_ThereIsNoCap` makes the strongest assumption in the batch. (2)
`AgentRunClarificationResumeTests.AnExistingDatabase_GainsClarificationsJson_AndKeepsItsRuns`, which depends on
`ALTER TABLE … DROP COLUMN` behaviour in this Microsoft.Data.Sqlite build. (3) The §8.4 conjunction — this
batch's central behavioural claim. (4) The WPF/STA classes.

**The `#` is `18` because it is the next free file ID in this folder, not because Batches 16 and 17 exist** —
`16-event-trigger-design-note.md` and `17-trust-model.md` are not batches, and per
[`00-OVERVIEW.md`](00-OVERVIEW.md)'s "Upcoming batches" preamble the number is a stable file ID, never a rank.
This batch's decisions are cited from other files as **`18 D<n>`**, matching `07 D13`/`04 D12`. One of them
collides by name with a *shipped* decision and §4.1 says so explicitly.

**Rank: 2** — behind the Rank-1 manual Windows smoke round
([`../agent-roadmap-finish/02-ui-check-plan.md`](../agent-roadmap-finish/02-ui-check-plan.md)), by owner
decision (**D8**). It is new scope, and it lengthens that round rather than shortening it (§9).

---

## 0. The repro, and the one thing it proves

Typed `ggg` into the Assistant composer, clicked **Run in background**. The chat surfaced the model asking
*"what do u mean with ggg?"*. The run panel showed a four-step plan, and that plan **executed to completion**.

What that proves is narrower and worse than "the plan was bad": **the run had the model's own statement that it
did not understand the goal, and advanced anyway.** The signal existed and was discarded. Everything below is
about where it was discarded, and there are two independent places — which is why §2 exists.

*(No step count, token count or duration is recorded here. The run was observed once, by hand, and this file
does not invent numbers it did not measure — see this folder's habit of pinning gates only where one was run.)*

---

## 1. The three places the signal is lost

### 1.1 Nothing checks the goal before planning starts

`AssistantViewModel.CanExecuteRunInBackground` (`AssistantViewModel.cs:818-819`) is the whole gate:

```csharp
private bool CanExecuteRunInBackground() =>
    !IsStreaming && !string.IsNullOrWhiteSpace(InputText);
```

`ChatSessionManager.StartBackgroundRunAsync` (`ChatSessionManager.cs:1149-1150`) then hands the string
straight through:

```csharp
public Task StartBackgroundRunAsync(string goal) =>
    _headlessRunLauncher.LaunchAsync(new HeadlessRunRequest(goal, AgentRunTrigger.User));
```

There is no triviality, length or plausibility test at either site, nor anywhere downstream in
`src/Pia.Wpf/Services` — confirmed by grep across that tree, and the absence is the point rather than an
oversight: the method's own comment (`AssistantViewModel.cs:815-817`) explains why the button requires *real
text* (attachments alone must not enable it) and stops there, because "real text" was the only property anyone
had needed.

### 1.2 The `emit_plan` contract gives the model no way to decline

`AgentPlanner.BuildPlanMessages` (`AgentPlanner.cs:705`) writes the plan turn's system prompt. The three lines
that matter (`:712-714`) instruct the model to decompose the goal, to *"Call the emit_plan tool exactly once"*,
and to *"Keep the plan tight"*. On a turn where the model called nothing, the retry is firmer still
(`:716-717`, `firm: true`):

> "You did not call emit_plan. You MUST respond by calling the emit_plan tool now — do not write prose."

And the schema has no room for a refusal. `PlanStepArg` (`AgentPlanner.cs:129-142`) is exactly
`Title`, `Intent`, `ExpectedArtifact?`, `PersonaKey?`, `ParallelGroup?` — five members, all about *how to do
the work*, none about *whether the work is understood*. `EmitPlanSchema` (`:120-122`) takes only
`PlanStepArg[] steps`.

**So the model faced with `ggg` is not misbehaving when it fabricates four steps. It is complying.** It was
told to emit a plan, told again more firmly, and given a schema in which "I cannot ground this" is unsayable.
That framing matters for the fix: the cheapest correct change is to make declining *sayable*, not to make the
instruction sterner.

### 1.3 A step's prose question reads as a declared success

Per-step outcome, `HeadlessTurnExecutor.cs:529-530`:

```csharp
var claim = outcomeStore?.Claim;
var succeeded = claim?.Succeeded ?? !string.IsNullOrWhiteSpace(exchange.Visible);
```

The fallback after `??` is what advanced the run: **any** non-empty visible text — including a rhetorical
question — is read as success when the step never called `emit_step_result`.

**This fallback is deliberate and must not simply be inverted.** Its own comment (`:532-538`) states the
reason: a step runs on whatever provider the run resolved, a `SupportsTools=false` provider is offered no
tools at all, and treating silence as failure would *"fail-closed on every non-tool-calling provider."* The
comment also records the mitigation already in place — silence is recorded as **unconfirmed** (`Outcome` stays
null) and the critic is told so, so the run does not pretend the model vouched for the step.

---

## 2. The finding that reshapes the fix: the two halves are asymmetric

It is tempting to read §1.3 as "add a third outcome to `emit_step_result`". **That would not have fixed this
repro,** and the reason is worth stating before any implementation session re-derives it.

`AgentStepTools.BuildEmitStepResultTool` (`StepOutcomeSignal.cs:157-167`) already tells the model, in the tool
description it ships on every step turn:

> "Explaining a failure in prose is NOT a failure report — a step whose outcome you do not declare here is
> recorded as unconfirmed, and a step you declare succeeded=false is recorded as failed no matter what else
> you wrote."

The model was told, in the tool's own description, and still wrote prose instead of calling it. **The failure
mode is the absence of a call.** Adding an enum member to a tool the model does not call changes nothing.

Hence the asymmetry, and it should drive the work-group order in §7:

| Half | Failure mode | Fixable by |
|---|---|---|
| **Plan time** | The contract makes declining unsayable (§1.2) | A schema/prompt change at one seam — cheap, high confidence |
| **Step time** | The model had a channel and did not use it (§1.3) | Nothing at the schema layer. Only a *new, differently-shaped* channel, and even then the model may skip it |

The plan-time half is the one that closes the observed repro. The step-time half is a separate capability the
owner asked for anyway (**D3**), and it should be built and judged on its own terms — not as "the rest of the
same fix".

---

## 3. Owner decisions

Resolved directly with the owner in the session that produced this file. Cited elsewhere as `18 D<n>`.

| # | Decision | Owner's words / chosen option |
|---|---|---|
| **D1** | **Two layers of goal gating**, not one. A cheap local pre-flight test refuses blatant junk before any run is created, *and* the planner can still decline what survives it. | "Both layers" |
| **D2** | A plan turn that cannot ground the goal **parks the run at `WaitingForInput`** with its question, and a user answer **resumes it into re-planning**. It does not end the run. | "Park at WaitingForInput, resume on answer" |
| **D3** | A run **may** park mid-plan — after steps have started — to ask a question, **"but only park for critical mid plan questions."** | free-text note |
| **D4** | What counts as critical is **the model's own declaration, with no per-run cap** on how many times it may ask. | "Model declares, no cap" |
| **D5** | The parked run reaches the user on **both surfaces**: an actionable card *and* the question posted into the run's own chat. | "Both surfaces" |
| **D6** | **No third outcome on `emit_step_result`.** The ask tool is the "blocked, needs input" channel; `succeeded`/`failed` stays a bool. | "No — the ask tool is that channel" |
| **D7** | The interactive **Send** path is in scope: **"interactive should work the same way"** — noted by the owner as **untested**, i.e. the interactive symptom is inferred from shared code, not observed. | free-text note |
| **D8** | This batch is a **new numbered spec in this folder, ranked behind the Rank-1 manual round.** | "New numbered spec, queued after Rank-1" |

**D6 is what keeps this batch small.** Because the outcome bool is untouched, nothing ripples into
`StepOutcomeSignal`, `StepTurnResult`, `AgentRunOrchestrator.ReplanAsync`, the panel's step chips, or
`AgentVerifier`'s digest tag vocabulary (`AgentVerifier.cs:191`, where `[declared]` is defined as "the step
called emit_step_result and this is its own structured verdict"). An implementer who finds themselves editing
any of those five should stop and re-read D6 — it means the design drifted.

---

## 4. What this collides with, and what it inherits

This is the section to read first in an implementation session. Each item is a decision someone already made,
with a reason, that **D2**–**D5** cannot be built without breaking or extending. §4.1–4.3 are the three that
**collide** — something must give. §4.4–4.5 are standing rules that **constrain** the design (and one of them
shrinks it). §4.6 is what comes for free.

### 4.1 `AgentRunOrchestrator` D1: "a resume must NOT re-plan"

`AgentRunOrchestrator.cs:186-191` — quoted in full because **D2** contradicts it head-on:

> "D1: a resume must NOT re-plan. `ReplaceStepsAsync` writes the plan verbatim and does not preserve Done
> steps, so re-planning here would wipe the persisted Done+Pending steps and re-run the whole goal from
> scratch. On resume we skip Planning/PlanAsync/ReplaceSteps and drop straight into the outer verify/drain
> loop…"

and the code is a bare `if (!resume)` around the entire planning block (`:191-195`).

*(Name collision, flagged so nobody conflates them: that `D1` is **Batch 08's** D1. This file's D1 is the
two-layer goal gate in §3. When citing either, qualify it — `08 D1` vs `18 D1`.)*

**Why it is a real collision and not a technicality.** `IAgentRunResumeService.ResumeAsync`
(`IAgentRunResumeService.cs:22`) is the single resume entry point, documented as re-launching *"headless-style
on the EXISTING run id"*, and `RunAsync(resume: true)` is what it reaches. A `needs-goal` park has **zero
persisted steps**, so the invariant's own stated hazard — wiping Done steps — does not apply to it; but the
`if (!resume)` guard does not know that, and a resume of such a run would drop into the drain loop with an
empty plan.

**What an implementation session must decide (not settled here).** Either the guard becomes conditional on
*why* the run parked (re-plan only when the park reason is `needs-goal` **and** no step rows exist — two
conditions, because either alone is a weaker guarantee than the invariant's author relied on), or `needs-goal`
gets its own resume path that never touches `RunAsync(resume: true)`. Whichever is chosen, the invariant's
comment at `:186` must be amended in the same commit — leaving a comment that says "a resume must NOT re-plan"
next to code where one resume flavour does is exactly how the next reader is misled.

### 4.2 `PlanResult` has two outcomes, and its second one is the wrong neighbour

`PlanResult` (`IAgentPlanner.cs:14-24`) is `(Steps, FallBackToSingleTurn, Usage?)`. Its doc comment states the
binary directly: *"Either an ordered set of steps to execute, or a signal to fall back to the `SingleTurn`
path (§16 R10)."*

**The hazard is specific.** `FallBackToSingleTurn` makes the orchestrator run the goal *as one ordinary turn*
(`AgentRunOrchestrator.cs:199-201`, `RunSingleTurnFallbackAsync`). For an ungroundable goal that is **the
worst available branch** — `ggg` would be sent as a single chat turn and whatever came back would be the run's
result. So a declined plan must be a **third** outcome, and the R10 degrade must not be reused as a
convenient existing exit. `PlanResult.Fallback` is a shared static instance (`:23`) with a documented
`with { Usage = … }` pattern; a third outcome should follow that shape rather than inventing another.

Two things carried by every existing plan path that a decline path must not drop:

- **Usage accrual.** `AgentRunOrchestrator.cs:196-198` accrues plan-turn usage **before** branching,
  deliberately (`I1`), so the degrade cannot bill as zero tokens. A decline spends the same tokens.
- **The firm retry.** `BuildPlanMessages(firm: true)` exists for a model that called nothing. A decline is
  *also* "called nothing that emitted a plan", so the implementer must decide whether a decline short-circuits
  the firm retry or arrives through it. Getting this wrong either burns a second turn on every decline, or
  turns the firm retry into a way to bully a declining model into fabricating.

### 4.3 No `FlowAction` can carry a typed answer

`FlowAction` (`Models/Flow/FlowAction.cs:25-84`) is an abstract record whose every subtype carries an id and a
label and nothing else — `OpenChatAction`, `OpenRunAction`, `ContinueRunAction(Guid RunId, string Label)`,
`OpenTodoAction`, `ReminderSnoozeAction`, `ReminderDismissAction`, `InvokeAction`. **There is no input-bearing
variant, and no card renders a text box.**

This is why **D5**'s two surfaces are not redundant — but the division of labour between them is **forced**, not
chosen, by the rule in the next paragraph.

### 4.4 The Flow card carries no user content, by rule — so it cannot show the question

`AgentRunNotificationSurface.cs:186` is explicit, on the item that a parked run already publishes:

> "Generic title/body — the run Goal + pause reason are SENSITIVE, never in the Flow item."

and `:188-193` explains the one narrow licence: the **reason token** is app-owned and never user content, so
*"it is safe to key the body on it"*, which is how hermes #16's approval card names its tool. `Body` is
`PausedBody(_localizationService, run)` (`:194`) — a localized string selected by token, not composed text.

**A clarification question is model-generated text derived from user input.** It is therefore exactly what this
rule excludes, and `PausedBody` is not a place to put it. Combined with §4.3 (no `FlowAction` carries input),
the surfaces resolve with no ambiguity left for the implementer:

- **The chat carries the question and receives the answer.** It already has a composer, and a headless run
  already owns a real chat row — `HeadlessRunLauncher.cs:332` mints `chatId`, `:350-353` persists a stub
  explicitly so *"a Failed run's ChatId still resolves."* No new input control, and no user content in a Flow
  item.
- **The card only says a run is waiting, and routes there.** It is the discoverability half — a background
  run's chat is one the user may never have open — and its body stays a token-keyed localized string like
  every other park's.

*(This is a constraint, not a defect. It is also the answer to the objection that D5 doubles the work: the
card half is a new reason token plus three resx entries, because the expensive part — composing user-visible
text — is forbidden.)*

### 4.5 Three publish filters, and two of them silently remove the card

`PublishForRunStateAsync` filters before it publishes anything, and **a spec that assumes "parked ⇒ card" is
wrong on two of this batch's paths.** In order:

| `AgentRunNotificationSurface.cs` | Filter | Consequence for this batch |
|---|---|---|
| `:157-158` | `RunShape != Planned` → return | harmless; both paths in scope are `Planned` |
| `:170-171` | `ParentRunId is not null` → return | **G5 hole.** A *delegated child* publishes no card at all |
| `:176-178` | foreground **and** this run's chat is the active session → return (`R18`) | **G6's normal case.** An interactive park publishes no card |

- **`:170` is not a preference and must not be relaxed.** Its comment states the mechanism: a child card would
  carry a `ContinueRunAction` on the **child** run id, *"a transition nothing supports"* — answering it
  resumes the child on the child slot pool "with nothing linking it back to the parent's step, so the parent
  then re-runs that same work in-process." So **a mid-plan ask (G5) raised inside a fan-out child is
  unreachable by the card surface**, and G5 must decide what happens: the ask is refused for delegated steps,
  or the park propagates to the parent, or the chat surface alone carries it. None of the three is free, and
  this is the sharpest unsolved corner in the batch.
- **`:176` is why D5 means different things on the two paths.** The suppression exists because *"the
  run-progress panel already reflects the state (incl. the `WaitingForInput` Continue button)"*, and the
  comment notes *"a headless run's chat is never the active session, so it always publishes."* So on the
  **interactive** path (D7/G6) the two surfaces are **panel + chat**, not card + chat — the card appears only
  if the user navigates away. G6 must target the panel, not assume it inherits the card.

### 4.6 What the park machinery gives this batch for free

- `AgentRunNotificationSurface.cs:80` — `WaitingForInput` is already a publishing state; `:180`
  (`AgentRunStates.IsParked`) is already the branch.
- `:96-107` — the **reason token → localization key** map (`ToolApprovalReason => "Flow_Run_ToolApproval"`).
  New tokens need new keys, and `Flow_Run_ToolApproval` lives in **three** resx files
  (`Resources/Strings/ViewStrings.resx`, `.de.resx`, `.fr.resx`) with en/de/fr parity enforced by the suite —
  add keys to the resx files only, never to the generated `Designer.cs`.
- `RunPauseEnvelope.cs` — the **cheapest** part of this whole batch, and it already documents the extension
  pattern: the reason vocabulary is a fixed set of **app-owned tokens** (never user content, so a consumer
  "may key copy on it and may log it"), and `ReadApprovalTool` (`:67`) is deliberately a **sibling** reader
  rather than a widened return, *"because a reader that only wants to know why a run parked must not be made
  to carry a tool name it will not use."*

**The one place the envelope's licence stops.** Every existing member is app-owned — reason tokens are
literals, `tool` is a tool name. If the question is stored in the envelope at all, it is the **first member
that is not safe to log**: `RunPauseEnvelope`'s "may log it" clause covers the token, not the question. Under
CLAUDE.md that text is a payload — `SensitiveDebug` only. Which is a second reason to prefer keeping the
question in the chat message (§4.4) and the envelope carrying, at most, a pointer.

---

## 5. The risk the owner accepted (D4), recorded rather than argued

**D4 is "model declares, no cap".** The concern was put to the owner before the choice and the owner chose it
anyway, so it is the decision — this section exists so no implementer or reviewer re-opens it as if it were an
oversight, and so the failure mode has a name if it shows up.

The failure mode: an unattended run may park for a question **any number of times**. Combined with **D3**
(mid-plan parks allowed), a model that is merely unsure — rather than genuinely blocked — can stall a
background run indefinitely, one question at a time. That is the same class of outcome the feature exists to
prevent, arrived at from the other direction: not a fabricated plan, but a plan that never finishes.

Two things follow, neither of which contradicts D4:

- **The prompt is the only bound**, so the ask tool's description carries the entire weight of "critical only".
  §2 is the reason to be pessimistic about that: `emit_step_result`'s description already tells the model that
  prose is not a report, in plain words, and the model ignored it. A description is a request.
- **Make the behaviour observable rather than capped.** Repeated parks should be visible — the audit timeline
  (Batch 03) already records per-step decisions and is the natural place — so that if this does misbehave, the
  evidence exists and a cap can be added as a *measured* follow-up rather than a guess. Counting is not
  capping, and D4 forbids only the cap.

---

## 6. Seams

Every line number re-read at `139b377d`. Several of these files were under active edit on this branch; **grep
the member name, not the number.**

| Seam | Where | What it is to this batch |
|---|---|---|
| `CanExecuteRunInBackground` | `AssistantViewModel.cs:818` | D1 layer 1 lives here or in the launcher; today `!IsStreaming && !IsNullOrWhiteSpace` |
| `StartBackgroundRunAsync` | `ChatSessionManager.cs:1149` | the pass-through with no check |
| interactive `Planned` create | `ChatSessionManager.cs:825-826` | D7's other path — `RunShape.Planned`, `Goal: userText`, executed in-session by `LiveTurnExecutor` |
| `BuildPlanMessages` | `AgentPlanner.cs:705`, prompt at `:712-714`, firm retry `:717` | where declining becomes sayable |
| `EmitPlanSchema` / `PlanStepArg` | `AgentPlanner.cs:120-142` | the schema with no refusal member |
| `PlanResult` | `IAgentPlanner.cs:14-24` | needs a third outcome; §4.2 |
| planning block + R10 branch | `AgentRunOrchestrator.cs:191-201` | where a decline is routed, and where it must **not** fall into `RunSingleTurnFallbackAsync` |
| the `if (!resume)` guard | `AgentRunOrchestrator.cs:186-191` | §4.1, the invariant D2 breaks |
| `ToolApprovalReason` park | `AgentRunOrchestrator.cs:60`, `:1389`, `:1401` | the precedent to copy: `PauseAsync(runId, reason, ct, approvalTool:)` |
| `RunPauseEnvelope` | `RunPauseEnvelope.cs:32` (`ReadReason`), `:67` (`ReadApprovalTool`) | new token + a `ReadPauseQuestion` sibling |
| reason → loc key | `AgentRunNotificationSurface.cs:96-107` | new keys, three resx files |
| card body composer | `AgentRunNotificationSurface.cs:114-127`, `:186-194` | token-keyed only — **must not** carry the question (§4.4) |
| the three publish filters | `AgentRunNotificationSurface.cs:157`, `:170`, `:176` | two of them remove the card on this batch's paths (§4.5) |
| `FlowAction` | `Models/Flow/FlowAction.cs:25-84` | no input variant exists; §4.3 |
| headless run's chat | `HeadlessRunLauncher.cs:332`, `:350-353` | the stub chat that makes D5's chat surface possible |
| `ResumeAsync(runId, nudge)` | `IAgentRunResumeService.cs:22` | the resume entry; its `nudge` is **transient and never persisted**, so it is not on its own a place to keep a clarification answer a re-plan must read |
| step-success fallback | `HeadlessTurnExecutor.cs:529-530` | **unchanged by D6** — listed so a reviewer can confirm it was left alone |
| `emit_step_result` schema | `StepOutcomeSignal.cs:138` (name), `:157-167` (`BuildEmitStepResultTool`) | **unchanged by D6**; its description is §2's evidence |
| `[declared]` tag vocabulary | `AgentVerifier.cs:191` | **unchanged by D6** |

---

## 7. Work groups — ✅ all six shipped in `e3938d7`

**What each one actually landed is the "What shipped" table at the top of this file.** The group text below is
kept as written, because it is what the build was judged against.

Two things the build settled that this section left open, recorded here so the next reader does not think they
were overlooked:

- **§4.2's firm-retry question: a decline short-circuits the firm retry.** `BuildPlanMessages(firm: true)`
  says *"You did not call emit_plan. You MUST respond by calling the emit_plan tool now — do not write
  prose."* A model that declined **via** `emit_plan` did call it. Routing a decline through the retry would
  burn a turn on every decline *and* is precisely the failure §4.2 names — "turning the firm retry into a way
  to bully a declining model into fabricating". The retry exists for **silence**, and a decline is not silence.
- **The decline rides `emit_plan` as an added member**, not prose and not a second tool. Prose is
  indistinguishable from the no-call case and would hit the firm retry (§2: the failure mode *is* the absence
  of a call); a second tool contradicts the plan prompt's own "call `emit_plan` exactly once". A member keeps
  the model calling one tool once, which the prompt already demands.

The order below was kept — the half that closes the observed repro landed first and was judged alone (§2).
Sizes were deliberately absent and are left absent; this folder does not retrofit estimates onto built work.

- **G1 — the local pre-flight (D1 layer 1).** A refusal at the composer/launcher boundary with an inline
  hint, no run created, no model turn spent. Ship it *with* its own false-positive fact: a test that a
  legitimately terse goal (a short imperative sentence) is **not** refused. Layer 1 exists to catch blatant
  junk; a layer 1 that refuses real goals is worse than no layer 1, because the user has no recourse.
- **G2 — the planner may decline (D1 layer 2).** The schema/prompt change at `AgentPlanner.cs`, the third
  `PlanResult` outcome, and the orchestrator routing — including the explicit negative fact that a decline
  does **not** reach `RunSingleTurnFallbackAsync` (§4.2), and the firm-retry interaction decided one way or
  the other with the reason written down.
- **G3 — the `needs-goal` park.** New reason token, loc keys in three resx files, the token-keyed card body,
  and the question posted into the run's **chat** (D5) — because §4.4 forbids putting it in the card. Question
  text under `SensitiveDebug` discipline per §4.6's closing note.
- **G4 — resume into re-planning (D2), the group that breaks `08 D1`.** §4.1's decision, made explicitly,
  with the `:186` comment amended in the same commit. This is the group most likely to need its premise
  **pinned by a test before any code depends on it** — this branch has a precedent for exactly that
  (Batch 08's G1 was test-only and landed ahead of the code that relied on it) and it applied because the
  premise was *read* rather than measured. Same situation here.
- **G5 — the mid-plan ask tool (D3/D4/D6).** A new step-turn tool that parks the run, intercepted pre-route
  the way `emit_step_result` is (`StepOutcomeSignal.cs:120` explains why that scoping is per-executor and not
  in `AssistantPromptComposer.PrepareTurn`), a second reason token, and resume re-entering a **partially
  executed step** — territory no existing resume path covers. Explicitly **not** capped (D4), and explicitly
  **not** a change to the outcome bool (D6). **Settle §4.5's delegated-child hole first** (`:170-171`): a child
  run publishes no card, and the three candidate answers differ in scope, so choosing after the tool is built
  means building it twice.
- **G6 — the interactive path (D7).** G2 is inherited for free because interactive `Planned` runs share
  `AgentPlanner`. What is *not* free is the park/resume half: the session is live, `IsStreaming` gates Send,
  and there is already a `WaitingForTool` session concept in `ChatSessionManager`. **Target the run-progress
  panel, not the Flow card** — §4.5's `:176-178` filter suppresses the card for exactly the chat the user is
  watching, by design. And **begin this group by reproducing the defect interactively**: the owner recorded
  D7's premise as untested, and building on an unobserved symptom is how a group ships something that fixes
  nothing.

---

## 8. Acceptance

Facts, not counts — the gate number belongs in the commit that runs it, not in a spec written before it.

1. **The repro, driven end to end.** A run launched with a goal the model cannot ground reaches a parked state
   with the model's question attached, and **creates no steps**. The current behaviour — a fabricated
   multi-step plan executing to completion — is the negative half, and it must be a *failing* assertion before
   G2, or the test does not check what it claims.
2. **The declined plan is not the R10 degrade.** A decline never calls `RunSingleTurnFallbackAsync`. Assert on
   the absence of that call, not on the run's end state — the two branches can produce similar-looking end
   states (§4.2).
3. **Plan-turn usage is accrued on the decline path**, matching `I1`'s treatment of every other plan outcome.
4. **A resumed `needs-goal` run re-plans; a resumed mid-plan run does not.** Both directions, in one test
   class, because §4.1's whole risk is that one guard now has to distinguish them.
5. **Layer 1 does not refuse a legitimately terse goal** (G1's false-positive fact).
6. **The question never reaches a Flow item.** Assert that a `needs-goal`/`needs-input` card's `Title` and
   `Body` are the token-keyed localized strings and contain none of the question text — this is the §4.4 rule,
   and it *is* observable from a test because the Flow item is data.
   **The sibling fact — "the question is never plain-logged" — is NOT in this list, deliberately.**
   `SensitiveDebug` is `[Conditional("DEBUG")]` and the suite runs Debug, where the call emits like any other,
   so no assertion over sink output can tell a `SensitiveDebug` from a `LogInformation`. It is a **source-level**
   fact instead: either a grep/analyzer that the question variable never appears as an argument to a
   non-`Sensitive*` logger call, or a review-checklist item. Writing it as a sink test produces something
   vacuous — see §10.6.
7. **en/de/fr parity** for every new loc key — already enforced by the existing suite, listed so it is not
   discovered at the end.
8. **Zero-Warning Policy**: `dotnet build -t:Rebuild` clean in **both** Debug and Release, per CLAUDE.md.

---

## 9. What this adds to the Rank-1 manual round

Stated because this folder tracks that number in both directions, and **this batch moves it up.** It adds at
least the following, each unautomatable for the reason given:

- **A real provider declining to plan.** Whether a real model, given a decline path, actually uses it on thin
  input — or still fabricates — cannot be established by a stubbed planner. This is the batch's central
  premise and only a live provider tests it.
- **The parked card and the question, rendered.** Whether a user reading the card understands what is being
  asked and where to answer. §4.3 makes this sharper than the tool-approval card: the answer is typed
  somewhere other than the card.
- **DE/FR render** of the new copy.
- **Whether a real run that parks mid-plan is recoverable by a real user** — D4 permits repeated parks, and
  what that *feels* like is exactly what no unit test observes.

It shortens nothing.

---

## 10. Open questions — ✅ ALL SETTLED before the build started

**Every question below was answered by the owner in Phase 0 of the build session, before any code was
written.** The original text of each is kept, with the answer and its consequence appended. One question the
spec did not have — the delegated-child hole of §4.5 — was raised first and settled first, because choosing
after the tool was built would have meant building it twice; it is recorded as Q0.

**Q0 (§4.5, the sharpest unsolved corner). The mid-plan ask is REFUSED for delegated steps.** A blocked child
declares `succeeded=false` via `emit_step_result` and the parent replans — an existing, tested path. This
mirrors the shipped precedent the spec did not cite: `HeadlessRunLauncher.cs:170-191`,
`CanParkForApproval(Guid? parentRunId) => parentRunId is null`, where hermes #16 answered the *same* question
for the approval park. The precedent transfers only **half**: its primary reason (an approval park *acquires
authority*, so a delegate would end up wider than its delegator) does **not** apply to a question, which
acquires nothing. It is the *supporting* reason that carries — a parked child has nowhere to ask, so its
parent re-parks behind it under `ChildrenParkedReason`, "a run stuck on a question nobody was asked". G5's
comment states this split rather than inheriting the precedent wholesale.

The five below are the spec's own, plus §10.6.

1. **What layer 1 actually tests (D1).** Length? Single token with no whitespace? Non-alphabetic ratio? The
   two layers can disagree about "too thin" — that was named as this option's cost when the owner chose it,
   and the resolution should be that **layer 1 is deliberately narrower** than layer 2, with the asymmetry
   written into its comment.
2. **Does the clarification answer persist?** `ResumeAsync`'s `nudge` is documented as transient and never
   persisted (`IAgentRunResumeService.cs:18-21`). A re-plan reads the goal; if the answer only rides the
   nudge, a second park loses the first answer. Either the answer is folded into the persisted goal, or it is
   stored beside it, and the two have different consequences for what the panel shows as the run's goal.
3. **One reason token or two?** `needs-goal` (plan time) and `needs-input` (mid-plan) are different resume
   behaviours (§4.1), which argues for two. Both need distinct copy from `Flow_Run_ToolApproval` regardless.
4. **What the mid-plan ask tool is called, and whether it may be offered on non-step turns.**
   `StepOutcomeSignal.cs:120-133` argues the scoping decision for `emit_step_result` at length; the same
   argument applies and should be cited, not re-derived.
5. **Is a `needs-goal` run that the user never answers terminal, ever?** A parked run holds a slot and a
   workspace. `10-durability-and-lifecycle.md` owns the startup-sweep and lifecycle rules that this state
   would have to satisfy; nothing in this batch settles it.
6. **What form the "question is never plain-logged" check takes** — a grep-shaped acceptance fact, a Roslyn
   analyzer, or a review-checklist line. §8.6 explains why it cannot be a sink test. The grep route is cheaper
   and this batch touches few call sites; an analyzer is only worth it if the same rule is wanted repo-wide,
   which is a bigger decision than this batch.

### The answers, and what each cost

1. **Layer 1 tests `no whitespace` AND `≤ 8 chars`** — a conjunction. Chosen *because* it is a conjunction:
   any multi-word goal passes unconditionally, so "Fix CI" and "Ship it" can never be refused and the
   false-positive fact of §8.5 is **structural rather than tuned**. `ggg` is refused; `aaaaaaaaaaaa` is not,
   and catching it is layer 2's job. That asymmetry is written into the predicate's comment, as §10.1 asked.
2. **The answer persists BESIDE the goal, in a new `AgentRuns.ClarificationsJson` column** — accumulating, so
   the second park does not lose the first answer (`18 D4` permits unlimited parks). The `Goal` column is
   **not** modified, so the panel and `ChildRunRowViewModel` keep showing what the user typed.
   **This could not be `ExtraJson`, and the reason was found during Phase 0, not at build time:**
   `TryBeginResumeAsync` (`AgentRunService.cs:387`) and its sibling (`:487`) both `SET ExtraJson=NULL` on the
   resume claim, deliberately, so anything kept there is destroyed by the very resume that carries the answer.
   A dedicated column also keeps user content out of `RunPauseEnvelope`, whose doc licenses a consumer to
   **log** every member — see §4.6's closing note.
   *Recorded, not hidden:* `RunClarifications.MaxAnswers = 8` is enforced destructively at write time, so a run
   parked more than eight times permanently loses its earliest answers. It is **not** a cap on asking, so
   `18 D4` holds — but it is a data decision the owner may want to revisit.
3. **Two tokens**, `needs-goal` and `needs-input`. They are different resume behaviours and G4's guard reads
   the token to tell them apart. Each got its own card body *and* its own panel activity string; the
   cross-group pass confirmed all seven tokens in the pause vocabulary now have both, in the same order, in
   both switches — a token with a card body but no panel string would be a user-visible gap on the interactive
   path, where §4.5's `:176-178` filter makes the panel the only surface.
4. **`request_user_input`, step turns only.** Scoping is `StepOutcomeSignal.cs:117-134`'s argument applied
   unchanged and cited, not re-derived: `AssistantPromptComposer.PrepareTurn`'s only narrowing axes are
   turn-shape blind, so scoping there would leak the tool into chat, voice, MCP and @-command turns. It is
   appended per-executor at the choke point where the step's persona is already resolved, and intercepted
   pre-route in **both** executors — a tool added to only one would silently lack it on the other path.
5. **Never terminal — matching today, and adding no lifecycle rule.** The startup sweep is
   `WHERE State < @Terminal` with `@Terminal = WaitingForInput(3)` (`AgentRunService.cs:591-594`), so a parked
   run **already** survives every restart indefinitely; the shipped tool-approval park has exactly this
   property. A `needs-goal` park is strictly cheaper than that one because it holds zero step rows. So this
   batch inherits the debt rather than introducing it, and `10-durability-and-lifecycle.md` still owns it.
6. **An Architecture source-scan test**, `GoalClarificationLoggingRuleTests` — not a sink test (§8.6 explains
   why one is vacuous) and not an analyzer (repo-wide is a bigger decision than this batch). The precedent it
   copies already existed: `RunWorkspaceRuleTests.cs:15` resolves `src/Pia.Wpf` off `AppContext.BaseDirectory`
   and reads the source. **Its limits are known and recorded rather than assumed:** the body extractor counts
   parentheses without skipping string literals, so a message template containing an unbalanced `)` would
   truncate the scanned body and hide a leak. That was *measured* — 0 of the 134 plain `Log*` call sites in the
   scanned files are affected today — so nothing is currently hidden, but the guard is narrower than it looks.
   It is also blind to a payload renamed to a non-matching identifier, and to a logger receiver not named
   `_logger`.
