# Batch 18 — implementation session prompt

Paste the whole of this file as the opening message of the implementation session. It is written **to** that
session, not about it.

---

Implement **Batch 18 — Goal grounding & mid-run clarification** on `feature/agent-run-spine`.

The spec is [`18-goal-grounding-and-clarification.md`](18-goal-grounding-and-clarification.md) in this folder.
**Read it in full before anything else** — including its §4, which the spec itself marks as "the section to read
first". It was written from a verified read of the tree at `139b377d` and it deliberately leaves several things
open rather than guessing. Do not re-derive its reasoning; do re-check its line numbers, because several of the
files it points at have been under active edit on this branch and it says so.

The one-line problem: typing `ggg` and clicking **Run in background** produced a four-step plan that executed to
completion while the model was on record in the chat asking what the goal meant. The signal existed and was
discarded in three independent places.

**I am explicitly authorising multi-agent orchestration for this task — use the `Workflow` tool.** Details and
constraints in Phase 2 below.

---

## Phase 0 — clarify the open points FIRST. Do not write code, and do not launch the workflow, until this is done.

Use `AskUserQuestion`. Group them however makes sense; two or three calls is fine (the tool caps at four
questions per call). For each, form your own options from what you find in the code — the spec gives you the
seams but deliberately does not pre-answer these.

**Q1 is the important one.** It is the batch's only genuinely unsolved corner and it is quasi-blocking for G5:

1. **The delegated-child hole (spec §4.5).** `AgentRunNotificationSurface.cs:170-171` returns early for any run
   with a `ParentRunId`, so a fan-out **child** publishes no card — its comment states that a
   `ContinueRunAction` on a child run id is "a transition nothing supports". So a mid-plan ask (`18 D3`) raised
   inside a delegated child is unreachable by the card surface. Three candidate answers, each with different
   scope: the ask is **refused** for delegated steps; the park **propagates to the parent**; or the **chat
   surface alone** carries it. Verify the filter still reads that way, work out what each answer costs, and ask.
   **Settle this before G5 is built, not during** — choosing afterwards means building the tool twice.

Then the five from the spec's §10, plus its §10.6:

2. **What layer 1 of the goal gate actually tests** (`18 D1`) — length, single-token-no-whitespace, non-alpha
   ratio, some combination. The spec's own guidance: layer 1 should be *deliberately narrower* than layer 2,
   with the asymmetry written into its comment, because two layers can disagree about "too thin".
3. **Whether the clarification answer persists.** `IAgentRunResumeService.ResumeAsync`'s `nudge` is documented
   as transient and never persisted (`IAgentRunResumeService.cs:18-21`). A re-plan reads the goal; if the answer
   only rides the nudge, a **second** park loses the first answer. Folding it into the persisted goal vs.
   storing it beside the goal have different consequences for what the run panel shows as the goal — say which,
   then ask.
4. **One reason token or two** — `needs-goal` (plan time) and `needs-input` (mid-plan) are different resume
   behaviours, which argues for two. Both need copy distinct from `Flow_Run_ToolApproval` either way.
5. **What the mid-plan ask tool is called, and whether it may be offered on non-step turns.**
   `StepOutcomeSignal.cs:120` argues the equivalent scoping decision for `emit_step_result` at length — cite
   that argument, do not re-derive it.
6. **Is a `needs-goal` run the user never answers ever terminal?** A parked run holds a slot and a workspace.
   `10-durability-and-lifecycle.md` owns the startup-sweep rules this state would have to satisfy.
7. **What form the "question is never plain-logged" check takes** — grep-shaped acceptance fact, Roslyn
   analyzer, or review-checklist line. Spec §8.6 explains why it cannot be a sink test: `SensitiveDebug` is
   `[Conditional("DEBUG")]` and the suite runs Debug, so no assertion over sink output distinguishes it from a
   plain `LogInformation`.

**Also re-verify before you ask, and tell me if any of these has moved** — each one changes the plan if it has:

- `AgentRunOrchestrator.cs:186-191` — the `if (!resume)` guard and the "a resume must NOT re-plan" comment
  (**08 D1**; do not conflate it with `18 D1`). Spec §4.1.
- `AgentRunNotificationSurface.cs:186` — "the run Goal + pause reason are SENSITIVE, never in the Flow item",
  and `:194`'s `PausedBody`. This is what forbids the question on the card. Spec §4.4.
- `PlanResult` (`IAgentPlanner.cs:14-24`) — still two outcomes, and `FallBackToSingleTurn` still routes to
  `RunSingleTurnFallbackAsync`. Spec §4.2, including why that degrade is the *worst* branch for an ungroundable
  goal.

---

## Phase 1 — plan the groups, then confirm the split with me

The spec's §7 defines six work groups, ordered so the half that closes the observed repro lands first. After
Phase 0, restate them with the answers folded in, and tell me:

- which groups the workflow will run **in parallel** and which are **sequenced**, and why. G1 and G2 are
  independent; G3 depends on G2; G4 breaks a shipped invariant; G5 depends on Q1; G6 depends on G2 and must
  **start by reproducing the interactive defect** (`18 D7`'s premise is recorded as untested, by the owner).
- **G4 gets its premise pinned by a test before any code depends on it.** This branch has the precedent and the
  reason: Batch 08's G1 was test-only and landed ahead of the code that relied on it, precisely because the
  premise had been *read* rather than measured. Same situation here.

Do not start the workflow until I have seen this.

---

## Phase 2 — the workflow

Author and run a `Workflow` script. Constraints:

**Shape.** Per work group, a `pipeline` of **implement → simplify → review → fix**, so a group reaches review as
soon as its implementation lands rather than waiting on a barrier. Use a barrier (`parallel`) only where a stage
genuinely needs all prior results at once — e.g. a final cross-group consistency pass over the new reason tokens
and loc keys, which does need to see all of them together.

**Model split** — this is deliberate, do not flatten it:

| Stage / group | Model | Why |
|---|---|---|
| G2, G4, G5, G6 implement | **opus** | each turns on a shipped invariant or an unobserved premise (§4.1, §4.2, Q1, `18 D7`) |
| G1, G3 implement | **sonnet** | mechanical: a bounded predicate + its false-positive test; a reason token, three resx entries, a token-keyed body |
| every **simplify** stage | **sonnet** | quality-only pass over the group's own diff |
| every **review** stage | **opus** | adversarial: prompted to *refute* that the group met its acceptance fact, defaulting to "not met" when uncertain |
| **fix** stages | same model as the group's implement | it holds the context the review is arguing with |

**Adversarial review, not confirmatory.** Every review agent gets the group's acceptance fact from spec §8 and
is told to try to break it. Two findings that must each be checked by name, because both are cases where a
plausible-looking implementation is wrong:

- A decline **must not** reach `RunSingleTurnFallbackAsync`. Assert on the **absence of that call**, not on the
  run's end state — the two branches can produce similar-looking end states (§8.2).
- A `needs-goal` resume re-plans; a mid-plan resume does **not**. Both directions, in one test class, because
  §4.1's whole risk is that one guard now has to distinguish them (§8.4).

**Scale.** Stay near this session's default workflow-size guideline (under ~15 agents) unless Phase 1 shows a
group needs more; if it does, say so and why before launching rather than silently exceeding it.

**No worktree isolation.** Groups touch overlapping files — `AgentPlanner`, `AgentRunOrchestrator`,
`AgentRunNotificationSurface` are each edited by more than one group — so parallel worktrees would conflict.
Work in the main checkout on the branch.

---

## Repo rules that apply, and traps this batch will hit

Follow `CLAUDE.md`. These are the ones that specifically bite here:

- **Zero-Warning Policy is blocking.** `dotnet build -t:Rebuild` must report `0 Warning(s) / 0 Error(s)` in
  **both** Debug and Release. An incremental build does not re-emit warnings from skipped projects. Read the
  count off MSBuild's summary line — at `-v:n` every warning prints twice, so grepping the log double-counts.
- **Test gate.** MTP runner, not VSTest. Exclude the live-network namespace:
  `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`. The baseline on this branch is **zero
  failures** — gate on `failed: 0`. If you need a baseline, measure it by stash → rerun; never take it from a
  count in a doc.
- **Localization**: new keys go in the **three** resx files (`ViewStrings.resx`, `.de.resx`, `.fr.resx`) — en/de/fr
  parity is enforced by the suite. Do **not** hand-edit the generated `Designer.cs`; it has already drifted.
- **CRLF**: source and docs in this repo are CRLF. The `Write` tool emits LF — convert any new file before
  committing, or byte-comparison tests and diffs get noisy.
- **Privacy-first logging**: the model's clarification question is user-derived payload. `SensitiveDebug` only,
  never a plain `LogInformation` argument. The reason **token** stays loggable; the question does not.
- **Do not drive or verify the app through winwright.** Build and test.

## Two invariants of this batch that mean the design drifted if you break them

- **`18 D6`: `emit_step_result`'s outcome stays a bool.** If you find yourself editing `StepOutcomeSignal`,
  `StepTurnResult`, `AgentRunOrchestrator.ReplanAsync`, `AgentVerifier`'s `[declared]` tag vocabulary, or the
  panel's step chips — **stop and re-read D6**. The spec's §2 explains why a third outcome would not have fixed
  the repro at all: `emit_step_result`'s own description already tells the model that prose is not a report, and
  the model ignored it. The failure mode is the *absence* of a call.
- **`18 D4`: no cap on how many times a run may ask.** The owner was shown the stall risk and chose no cap
  anyway (spec §5). Do not add one. You **may** make repeat parks observable — the Batch 03 audit timeline is
  the natural place — so a cap could later be a *measured* follow-up. Counting is not capping.

## When the code is done

1. Run the gate and the Debug **and** Release rebuilds, and report the actual numbers — not "should be clean".
2. Update the spec file in place: mark the groups shipped with their commit range, and **amend
   `AgentRunOrchestrator.cs:186`'s comment in the same commit as G4** if that guard changed. Leaving a comment
   that says "a resume must NOT re-plan" next to code where one resume flavour does is exactly how the next
   reader is misled.
3. Update `00-OVERVIEW.md`: the Rank-2 row for `18`, and **what this batch adds to the Rank-1 manual round**.
   Spec §9 names four items and says it shortens nothing — carry them over by name, with the reason each is
   unautomatable, in the "Opened by" style that file uses.
4. Commit. Do not push, and do not open a PR, unless I ask.
