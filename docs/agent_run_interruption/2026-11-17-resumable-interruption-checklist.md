# Resumable interruption for agent runs — checklist

**Status:** open — all gates closed 2026-11-17, no step landed · **Owner:** Marco Altmann ·
**Written:** 2026-11-17
**Origin:** [2026-11-17-resumable-interruption-plan.md](2026-11-17-resumable-interruption-plan.md), which
implements GitHub issue [Pia-Ai-dev/Pia.Wpf#95](https://github.com/Pia-Ai-dev/Pia.Wpf/issues/95).

Tick each box in the commit that lands it. Step numbers are the plan's task numbers.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

The work as a whole is `M`.

## Decision gates — all settled 2026-11-17 (owner)

Lettered `Q` so they never collide with step numbers. Every gate is closed; the answer is binding on the
steps in its Blocks column.

| # | Question | Issue wording | Recommended | Answer | Blocks |
|---|---|---|---|---|---|
| Q1 | Which `HttpRequestException` parks as a provider interruption? | Any that is not offline. | Only status null, 408, 429 or 500 and above. Pia Cloud raises this type with the status for every other non-2xx, so a 400 would park on every Continue and never fail or replan. | **Closed 2026-11-17 (owner): as recommended.** | 2, 5 |
| Q2 | Do failures from providers other than Pia Cloud park? | Not mentioned, so they stay Failed. | Yes, same rule as Q1. | **Closed 2026-11-17 (owner): they need the same coverage as Pia Cloud.** That is both other SDK families: the OpenAI SDK's `ClientResultException` and the Anthropic SDK's exception types. A 429 there parks as provider, not credit. | 2 |
| Q3 | Does a scheduled routine park on an interruption? | Yes: a park with a Flow card instead of a failure strike. | Yes, knowingly. A parked run does not block the job's next firing, so a long outage parks one run per firing and the five-strike valve never trips. | **Closed 2026-11-17 (owner): same coverage as interactive runs.** | 3, 4 |
| Q4 | How far does the comment ratchet reach in this PR? The touched members (`RunAsync`, `RunExchangeStepAsync`, `RunStepTurnAsync`, `StepTurnResult`) carry hundreds of lines of long, history-laden comments. | Not mentioned. | The lines edited plus the summary of a member whose behaviour changes. The ten touched members hold about 540 comment lines, 450 of them in those four. | **Closed 2026-11-17 (owner): edited lines only.** The other options were a separate comment-only commit in this PR, a follow-up PR, or cleaning each member in full inside its task. | 2–9 |

## Steps

- [x] **1 — `CreditLimitException` at the three 429 sites.** The credit limit is raised as its own
  exception type with the unchanged message, so it can be told apart without reading text.
  *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **2 — Interruption contract and classifier.** `StepTurnResult` gains the defaulted `Interruption`
  member, one static classifier maps an exception chain to credit, timeout, offline, provider or none for
  all three provider client families, and an exception that leaves a tool handler is marked so it never
  counts.
  *Deps:* 1, Q1, Q2, Q4 · *Effort:* S · *Value:* Enabler
- [ ] **3 — The orchestrator parks on an interrupted step.** The step returns to Pending and the run waits
  with one of four reason tokens, with no replan, no grace turn and no terminal settle. A delegated child
  parks the same way and holds its parent at `children-parked`.
  *Deps:* 2, Q3, Q4 · *Effort:* S · *Value:* High
- [ ] **4 — An interrupted replan parks.** A replan call cut off after a step failure or a failed verify
  parks the run, and the failed step goes back to Pending.
  *Deps:* 3, Q3 · *Effort:* S · *Value:* High
- [ ] **5 — The headless executor names the interruption.** A background or resumed step sets the member,
  behind the approval and question parks and never while the run is being cancelled.
  *Deps:* 3, Q1 · *Effort:* XS · *Value:* High
- [ ] **6 — The live step names the interruption and drops its error bubble.** An interactive step sets the
  member and removes the error text the resumed step would otherwise read as its own answer.
  *Deps:* 3 · *Effort:* S · *Value:* High
- [ ] **7 — The card and the Flow item say why.** Four reasons in three languages on the run card and the
  Flow body.
  *Deps:* 3 · *Effort:* XS · *Value:* High
- [ ] **8 — A failed resume keeps the interruption token.** A continuation that fails before dispatch
  re-parks with the same reason instead of `resume-interrupted`.
  *Deps:* 3 · *Effort:* XS · *Value:* Med
- [ ] **9 — A typed Agent-mode message continues the run.** A send while the run is parked on an
  interruption resumes it with the text as the note and creates no new run.
  *Deps:* 3, 8 · *Effort:* S · *Value:* High
- [ ] **10 — Gate.** Zero-warning rebuild in Debug and Release, the full test exe at `Failed: 0`, the manual
  check on the throwaway stack, and the PR notes.
  *Deps:* 1–9 · *Effort:* XS · *Value:* Enabler

## Not yet planned

- A Retry for runs that are already Failed. `docs/failure_legibility/2026-08-24-failure-legibility.md`
  section 15 lists what it needs.
- The parent's wording for an interrupted sub-agent. The child parks (step 3) and the parent shows
  `children-parked`, whose text speaks of a budget. Continue on the parent restarts the group.
- The credit reset time on the card. The pause envelope carries tokens only.
- An interruption of the first plan call or of the single-turn fallback. Neither has a step row to resume.
- Routines during a long outage: skip a firing while an earlier run of the same job is parked on an
  interruption. Until then every firing parks one more run.
- A rate-limit wording of its own. A 429 from a provider other than Pia Cloud parks with the general
  "The AI service had a problem" text.
- An error event inside an open stream from an OpenAI-SDK provider. Its exception shape is not known yet;
  step 2's real-client test covers a status and a dropped stream, not this.
- A send with an image or file during an interruption park. The park-answer path takes text only, so that
  send still starts a new run.
- A sign-out mid-run. Pia Cloud's 401 is a plain `InvalidOperationException` and still fails the run.
- `HeadlessTurnExecutor`'s existing `LogError(ex, …)` in the step catch writes the exception message, which
  can contain a response body, to the release log.
- `FailureMapper` files a dropped stream (`IOException`) under Workspace.

## Suggested order

Cheapest decisive work first, then the vertical slices.

1. **1, then 2.** Small, no behaviour change yet, and step 2's loopback tests settle what .NET and the two
   provider SDKs really raise.
2. **3.** The park itself. After it, an interruption reported by a stub executor already parks.
3. **5, then 7.** The background path end to end: a routine or a resumed run parks and says why.
4. **6.** The interactive path, which is what the issue's reporter hit.
5. **4.** The replan sites, the less frequent way into the same failure.
6. **8, then 9.** The typed-message continuation and the token it depends on.
7. **10.**
