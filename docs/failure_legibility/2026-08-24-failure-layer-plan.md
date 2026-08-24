# Failure layer + recovery actions — the plan for #2 slice 2

**Status:** **G2, G3 and G4 landed 2026-08-24**; gate `G-Q1` **answered 2026-08-25** and `G5` is
**withdrawn as specified** — see §4. **Owner:** Marco Altmann.
**Written:** 2026-08-24.
**Origin:** recommendation **#2** of
[`../hermes_checkup/2026-08-22-hermes-update-review.md`](../hermes_checkup/2026-08-22-hermes-update-review.md)
(§3.2, row 2 of the table). Slice 1 shipped as `3c90aa74`; slice 2 is the remainder, and it is the last
`High` left in that review's *not yet planned* table. `G1`
([`2026-08-24-export-diagnostics.md`](2026-08-24-export-diagnostics.md)) shipped the other half of §3.2
and is a **dependency of `G4`'s diagnostics action**, not a sibling.

Executable cold. Everything needed is here — you should not have to re-read the review.

---

## 1. Where this starts from

Three pieces already exist and this plan connects them rather than replacing any of them.

**`AgentRunService.FailAsync` records a reason on every failure.** It serialises `{"error": …}` into
`AgentRuns.ExtraJson` (`AgentRunService.cs:302`). Slice 1 taught `RunProgressViewModel` to read it, so a
failed run's card now says *why*.

**Slice 1's vocabulary is deliberately OPEN**, and that is the single most important thing not to break.
`DescribeFailureReason` localizes **five** app-owned constants and lets **everything else through
unchanged**, because an `ex.Message` or the model's own summary is the *informative* case, not a fallback.
Its own doc comment says so. A closed enum is the opposite shape, so see §2.

**`ScheduledJobService.IsPreModelFailure` is already the retry verdict**, for exactly one value:

```csharp
public const string NoProviderFailureReason = "NoProvider";
private static bool IsPreModelFailure(string reason) =>
    string.Equals(reason, NoProviderFailureReason, StringComparison.Ordinal);
```

and its doc comment states the gap this plan closes, plus the rule for closing it:

> **KNOWN GAP, accepted:** `IHeadlessRunLauncher.LaunchAsync` can also fail genuinely pre-model (its own
> provider resolve, the stub-chat save, workspace setup), and that arrives here as a bare message, so such a
> one-off still dies on the first strike. **Widening needs a reason value the CALLER can vouch for — never a
> substring match on provider error text.**

That sentence is the acceptance criterion for the whole mapper: **key on exception type, never on message
text.**

## 2. The two decisions that shape everything

### 2.1 The descriptor is ADDITIVE. It does not replace the string.

`PiaFailure` travels **alongside** the existing free-text reason, never instead of it. Slice 1's argument
holds: the open arm is what makes an unrecognised provider error readable at all, and a closed taxonomy that
swallowed it would be a regression dressed as a feature.

Concretely: `FailAsync` keeps writing `{"error": …}` exactly as today, and the descriptor goes somewhere
else (§2.3). **A test must pin that an unmapped `ex.Message` still reaches the card unchanged** — that
assertion is the guard on this whole decision, and it is the first one to write.

### 2.2 "Retryable" means something different here than it does in hermes. Use the Pia meaning.

hermes's `error_surface.py` bool asks *"could the API call succeed if repeated?"*. `IsPreModelFailure` asks
*"can we prove this run spent nothing and wrote nothing?"*. **They are not the same question**, and treating
them as one ships a duplicate-write bug: a provider 503 on step 7 is transient by hermes's meaning and
emphatically unsafe to re-dispatch by Pia's, because a step may already have written to the vault. The
existing doc comment spells the risk out and it is the reason `IsPreModelFailure` is one value wide.

**Decision: Pia's meaning wins, and the member is named so the confusion cannot recur.** Not `Retryable`:

```csharp
namespace Pia.Models;                       // NOT Pia.Services - see §6, and verified: ActionCardInfo,
                                            // AiCompletionResult and ChatStreamItem are records there already

public enum FailureLayer { App, Workspace, Provider, Endpoint, Tool, Policy, Cancelled }

/// <summary>SafeToReRun is "provably nothing spent and nothing written", NOT "the call might work if
/// repeated" - re-dispatching a mid-run provider fault is the duplicate-write risk.</summary>
public sealed record PiaFailure(FailureLayer Layer, string Code, bool SafeToReRun);
```

`Code` stays a `string`, not an enum: it is the stable machine token (`NoProvider`, `WorkspaceSetup`,
`Timeout`, `Truncated`, `BrowserLaunch`, `Superseded`, `EmptyResponse`, `Undetailed`, `Interrupted`,
`Unclassified`), and the five constants slice 1 already localizes **are** those tokens. Reuse them by name;
do not re-spell them.

### 2.3 Storage: its own column, and the precedent is already in the schema

The discriminating question is *does this value have to survive a transition that runs `ExtraJson=NULL`?*
It does — a Retry (`G5`) is exactly that transition, and knowing what failed is the point of offering one.
`AgentRun.ClarificationsJson` faced the same question and the answer is on the field:

> Its own column rather than part of `ExtraJson` because both resume claims `SET ExtraJson=NULL`, which
> would destroy an answer kept there.

So: **`AgentRuns.FailureJson TEXT NULL`**, added the way this repo adds columns — a `PRAGMA table_info`
existence check then an `ALTER TABLE` in `SqliteContext.MigrateSchema` (`SqliteContext.cs:583` onward; there
are a dozen worked examples, `ScheduledJobs.BlueprintKey` being the most recent).

Doing this in `G2` rather than deferring it to `G5` avoids writing the descriptor into `ExtraJson` and then
migrating it out.

## 3. Where the mapper actually goes — smaller than it looks

`FailAsync` is called from **11 sites**, and there is a **sixth constant on a second path**. Three groups,
and only one of them needs a mapper:

| Group | Where | What it passes |
|---|---|---|
| **Agent-run taxonomy, already named** | `AgentRunOrchestrator.SupersededFailureReason`, `AgentStepTools.EmptyResponseFailure` / `.UndetailedFailure`, `HeadlessRunLauncher.WorkspaceSetupFailure` / `.ShutdownInterruptedFailure` → `FailAsync` → `AgentRuns` | one of **five** constants — the same five slice 1 localizes |
| **Scheduled-job taxonomy, already named** | `ScheduledJobService.NoProviderFailureReason` → `MarkRunFailedAsync` → the scheduled-job run table | **one** constant, and the only one `IsPreModelFailure` reads today |
| **Needs classifying** | `AgentRunOrchestrator.cs:585`, `BackgroundAssistantTurnRunner.cs:276`, `HeadlessRunLauncher.cs:523` and `:945` | `ex.Message` |

(A user cancel passes plain `null` and is not a constant; it selects itself out of slice 1's rendering
already.)

The first two groups get a **static descriptor beside the constant** — no classification, no guessing, the
caller vouches for it, which is precisely what the `IsPreModelFailure` comment demands. **They are two
tables with two different writers**, which is why `G2` persists the descriptor on the agent-run side while
`G3` is where the scheduled-job side actually consumes one.

The second group is where a mapper is needed, it sees the **exception object** (not the message), and it is
therefore a `switch` on type: `LlmTimeoutException`, `LlmTruncatedException`, `BrowserLaunchException`,
`HttpRequestException`, `TaskCanceledException`, `IOException`/`UnauthorizedAccessException`, everything else
→ `App`/`Unclassified`/`SafeToReRun: false`.

**The mapper must sit at the `catch`, not in `AgentRunService`.** By the time `SafeFail(runId, ex.Message,
…)` runs, the exception is gone and only text remains — and text is exactly what the repo's own comment
forbids keying on. This means `IAgentRunService.FailAsync` gains an optional `PiaFailure?` parameter and each
`catch` passes one.

**`Unclassified` is a first-class outcome, not a hole.** A run whose exception maps to nothing still shows
its `ex.Message` through slice 1's open arm, exactly as today — the descriptor adds a layer name when it can
and stays quiet when it cannot.

## 4. The rows

Ratings use the checklist's scales. These land as `G2`–`G5` in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md);
this plan does **not** get its own tracking file.

- **G2 · `PiaFailure` + the type-keyed mapper + `AgentRuns.FailureJson`.** The descriptor, the static
  descriptors beside the seven named constants, the exception-type mapper at the four `catch` sites, the
  additive column, and `FailAsync` persisting it. **No UI.** First test written is §2.1's guard.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- **G3 · Widen `IsPreModelFailure` to read `SafeToReRun`.** Closes the KNOWN GAP quoted in §1: a
  `HeadlessRunLauncher` failure that provably happened before the model was called stops dying on the first
  strike. The narrowing stays — a mid-run fault is still terminal — but it is now decided by a value the
  caller vouched for rather than by one string comparison.
  *Deps:* G2 · *Effort:* **XS** · *Value:* **Med**

- **G4 · Layer name + recovery action on the failure card.** Renders the layer beside slice 1's reason line
  and offers the matching action. Both actions are **already built, and the navigation seam is verified**:
  *Export diagnostics* (`G1`) for `App`/`Workspace`, and for `Provider`/`Endpoint`
  `_navigationService.NavigateTo<SettingsViewModel, int>((int)SettingsTab.Providers)` — the exact call
  `MainWindowViewModel.NavigateToSettings` already makes, with a tuple overload used by the meeting overlay
  for a deep-linked inner tab. Needs an `AutomationProperties.AutomationId` per new control; **no
  `[InlineData]` bump** — `RunProgressPanel` is already covered and its count is a floor asserted with `>=`.
  *Deps:* G2 · *Effort:* **S** · *Value:* **High**

- **G5 · Retry on the failure card, honouring `SafeToReRun`. WITHDRAWN as specified 2026-08-25** — the gate
  below shows that a Retry so gated can never enable. What a Retry would actually cost is in the prerequisite
  list at the end of this section.
  *Deps:* G2, G4, **G-Q1** · *Effort:* **M** · *Value:* **Med**

### Decision gate G-Q1 — ANSWERED 2026-08-25

**Closes:** `G5`. **Question:** does Retry re-dispatch the whole run from its goal, or resume from the failed
step?

**Answer: resume from the failed step — and it is not buildable today.** Re-dispatch is dead on arrival: a
Retry gated on `SafeToReRun` can never enable, because both descriptors carrying `true` are produced where no
failure card exists. Resume-from-step is the only shape that does not duplicate writes, and it needs a step
ledger that a failed run does not leave behind. `G5` as specified is therefore **withdrawn**, and the
prerequisite list below replaces it.

#### Every descriptor and its verdict

`FailureMapper` (`src/Pia.Wpf/Services/FailureMapper.cs`) constructs **15** descriptors — 14 classifying arms
plus the `Unclassified` fallback. **Two carry `true`.**

`ForReason`, matched by value on an app-owned constant:

| Constant | Line | Layer / Code | `SafeToReRun` |
|---|---|---|---|
| `AgentStepTools.UndetailedFailure` | 25 | Tool / Undetailed | false |
| `AgentStepTools.EmptyResponseFailure` | 26 | Provider / EmptyResponse | false |
| `HeadlessRunLauncher.WorkspaceSetupFailure` | 27 | Workspace / WorkspaceSetup | false |
| `HeadlessRunLauncher.ShutdownInterruptedFailure` | 28 | Cancelled / Interrupted | false |
| `AgentRunOrchestrator.SupersededFailureReason` | 29 | Cancelled / Superseded | false |
| `ScheduledJobService.NoProviderFailureReason` | 30 | Provider / NoProvider | **true** |

`ForException`, matched on exception type through the unwrapped inner chain:

| Type | Line | Layer / Code | `SafeToReRun` |
|---|---|---|---|
| `PreModelLaunchException` | 79 | Provider / NoProvider | **true** |
| `LlmTimeoutException` | 80 | Provider / Timeout | false |
| `LlmTruncatedException` | 81 | Provider / Truncated | false |
| `BrowserLaunchException` | 82 | Tool / BrowserLaunch | false |
| `HttpRequestException` | 83 | Endpoint / Transport | false |
| `TaskCanceledException` / `OperationCanceledException` | 84 | Cancelled / Cancelled | false |
| `UnauthorizedAccessException` | 85 | Workspace / AccessDenied | false |
| `IOException` | 86 | Workspace / Io | false |
| *nothing matched* | 53 | Unclassified / Unclassified | false |

The pair is pinned by `FailureMapperTests.OnlyTheProviderResolveFailure_IsSafeToReRun`, which asserts all
fifteen — both `true` arms and every one of the thirteen `false` ones, the `Unclassified` fallback included.
The "only" in its name is therefore enforced: flipping any arm to `true` turns it red.

#### Neither `true` can reach a card

The card has one data path: `AgentRuns.FailureJson`, written only by `AgentRunService.FailAsync` (`:324`) and
read only by `RunProgressViewModel.ReadFailureLayer` (`:1269`). It is the only such column in the schema.

- **The string arm never goes near it.** Both raisers (`ScheduledJobBackgroundService.cs:494`, `:679`) hand
  the descriptor to `MarkRunFailedAsync`, which uses it once — at `ScheduledJobService.cs:359`, which *is*
  `G3` — and persists it nowhere. A scheduled job has no descriptor column and renders no failure card.
- **The exception arm fires before the row exists.** `PreModelLaunchException` is thrown at exactly one
  place, `HeadlessRunLauncher.cs:323`, ahead of the stub chat (`:328`) and of `CreateAsync` (`:368`). It
  escapes to `ScheduledJobBackgroundService.cs:531` (into `MarkRunFailedAsync`, above) and to
  `ChatSessionManager.cs:1415`, which propagates to its awaiting caller. Neither has an `AgentRuns` row.
- **No `FailAsync` site sees it second-hand.** `HeadlessRunLauncher.cs:540` and `:967` are gated on
  `started`, hence past `:323` in the same dispatch. `AgentRunOrchestrator.cs:585` sees planner and step
  faults only — the one in-run launch (`LaunchChildAsync`, `:1209`) has its own catch that settles the step
  with a fixed string. `BackgroundAssistantTurnRunner.cs:285` launches nothing.

So a Retry gated on `SafeToReRun` is enabled **never** — with one qualifier. `ForReason` matches by string
*value*, not "by reference to its declaration" as its own doc comment claims (`FailureMapper.cs:19`), and
`SafeFail`'s fallback (`AgentRunOrchestrator.cs:1899`) feeds it arbitrary reason text from sites that *do*
have a run row (`:298`, `:506`, `:698`, `:704`). A reason byte-identical to the token `"NoProvider"` would
therefore classify `true` on a real card. No raiser produces that string: the constant is only ever passed
by name, and `PreModelLaunchException`'s message is the sentence at `HeadlessRunLauncher.cs:323`.

#### Why resume-from-step is not buildable yet

A failed run's ledger cannot be drained. The in-flight step goes `Running` at `AgentRunOrchestrator.cs:405`;
it is settled by `SafeRecordStep` on the success path (`:494`) and restored to `Pending` on the pause path
(`:565-566`), and the fail path (`:585`) does **neither** — so the step is left `Running`.
`NextPendingStepAsync` (`AgentRunService.cs:1197`) selects `Status=Pending` only, and the sole repair in the
codebase — statement 1b of `FailInterruptedRunsAsync` (`AgentRunService.cs:705-728`) — is scoped to
`State=WaitingForChildren` and never touches a Failed run. Its own comment states the cost of draining an
unrepaired ledger:

> a step left Running is INVISIBLE to it: without this statement a re-parked parent would skip its whole
> delegated group, execute the steps AFTER it out of order against inputs that were never produced, and
> settle Completed while the panel still rendered those steps as active — permanently and silently.

#### What a Retry would require

1. A `Failed → Running` claim that first resets that run's `Running` steps to `Pending` — statement 1b's
   rule, handed to the fail path.
2. That claim must **not** `SET ExtraJson = NULL`. `TryBeginResumeAsync` (`AgentRunService.cs:443`) and
   `TryResumeFromPauseAsync` (`:544`) both do, and are safe only because they fire from
   `WaitingForInput`/`Paused`. A Retry written in their shape would wipe the reason slice 1 reads.
   `FailureJson` (§2.3) survives it; `{"error": …}` does not.
3. A card reader that keeps more than the layer. `ReadFailureLayer` (`RunProgressViewModel.cs:1267-1270`)
   discards `Code` and `SafeToReRun`, so nothing in the VM can gate on the verdict today. The button itself
   would live in `RunProgressPanel.xaml`.

Together these put the work **above** `G5`'s `M`.

**Hand-off:** `G-Q1` and `G5` still read *Unanswered* / open in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md);
a separate pass rewrites that file and carries this answer over. Until it does, this section is authoritative.

## 5. Suggested order

```
G2 → G3          # the enabler, then the cheap gap-closure it unlocks
G2 → G4          # the user-visible half; can run in parallel with G3
G-Q1 → G5        # gate ANSWERED 2026-08-25; G5 withdrawn as specified (§4)
```

`G2 → G3` is under two days and closes a gap the repo has already written down. `G4` is where the value is.

## 5a. What the build actually found

Three of the four §6 traps held. Two more turned up that reading could not have produced:

- **The mapper had to walk the inner exception chain.** A refused connection reaches the orchestrator as
  `AggregateException` → `ClientResultException` → `HttpRequestException` → `SocketException`. Matching only
  the outermost type classified **every real transport failure** as `Unclassified`, and the card named no
  layer — found by pointing a provider at a dead port and watching the card, not by any test.
- **A codec split across two files drifts silently.** `AgentRunService` serialises camelCase; the panel
  deserialised with default (Pascal) options, so every descriptor read back as `Unclassified` — which the
  reader reports as "no layer", not as an error. `PiaFailure` now owns `ToJson`/`FromJson` and both sides go
  through it.

One design call made while building, recorded rather than left implicit: an unrecognised exception still
persists a descriptor (Unclassified) rather than leaving the column null. It renders identically, and it buys
the difference between "this build classified it and had no arm" and "written before the column existed".

And one prediction was wrong in the safe direction: the optional parameter did **not** leave the test doubles
compiling silently. An interface member must match exactly, defaults included, so all seven broke loudly.

## 6. Traps, found by reading before writing

- **`Classifier` is not an approved suffix.** `NamingConventionTests.ServiceClasses_MustFollowNamingConvention`
  holds a closed list (`Service`, `Handler`, **`Mapper`**, `Parser`, `Detector`, … ), and `Classifier` is not
  on it. Name the thing `FailureMapper`. `Pia.Consent` and `Pia.Services.Exceptions` are excluded from that
  rule; `Pia.Services.*` is not — the same prefix behaviour that made `Pia.Services.Diagnostics` inherit it
  during `G1`.
- **A record may not live in the `Pia.Services` root namespace.**
  `RecordTypes_MustNotLiveInTheServicesRootNamespace` fails it outright. `PiaFailure` goes in `Pia.Models`,
  next to `AgentRun` and `AgentEnums`.
- **`IAgentRunService` has one production implementation and four test doubles** — `SpyRunService` in both
  `AgentRunClarificationResumeTests` and `AgentRunResumeNoRePlanPremiseTests`, `FaultyRunService` in
  `AgentRunOrchestratorTests`, `ThrowingAgentRunService` in `BackgroundAssistantTurnRunnerRunSpineTests`.
  Adding a parameter to `FailAsync` touches all five. An optional parameter keeps them compiling — which is
  the hazard, not the relief: this repo has already shipped a green gate over an unstubbed mock. Update each
  double deliberately.
- **Do not add a second checklist file.** #3 was *promoted out* of the review's not-yet-planned table into
  group `G` rows on the existing checklist; #2 slice 2 follows the same precedent, and the not-yet-planned row
  gets a pointer here the way #3's got one.
- **Every optional dependency of `RunProgressViewModel` goes LAST and DEFAULTED.** It is hand-constructed
  with a **positional** argument list in production and in its tests, and the file says so four times over —
  each added service is trailing, defaulted, and documented with what a null means ("the panel is
  byte-identical to before"). `G4`'s navigation dependency follows the same discipline or it breaks every
  positional construction at once.
- **The letter `G` is overloaded in this code.** Comments inside `RunProgressViewModel.cs` say "G4" and "G7"
  meaning *agent-roadmap batches*, which is not what row `G4` on the checklist means. Say "row `G4`" in prose,
  and per `CLAUDE.md` put no new task id in the source at all.
- **`RunProgressViewModel` gates the reason on the Failed FAMILY**, which `MapState` folds `Cancelled` into.
  Check what `G4`'s layer line does on a run cancelled because a *child* failed — it carries the child's
  reason today — before assuming the new line inherits the right gating.

## 7. What this deliberately does not do

- **No taxonomy for chat turns.** Scope is agent runs and scheduled jobs, the two surfaces with a durable
  failure record. A chat-turn error has no row to hang a descriptor on.
- **No upload.** `G1` is Export, by the owner's 2026-08-24 decision; the review's *Send* half stays closed.
- **No retry budget.** Review #9 (empty-response guard with a cost-aware retry budget) is a separate row and
  stays where it is.
