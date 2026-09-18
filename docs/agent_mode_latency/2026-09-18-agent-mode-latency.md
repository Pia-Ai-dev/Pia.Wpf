# Agent mode: where the wait comes from, and what to do about it

**Status:** Batch 1 landed — see [the checklist](2026-09-18-agent-mode-latency-checklist.md)
**Owner:** Marco Altmann
**Written:** 2026-09-18
**Origin:** User report — "users complain about the time they need to wait in assistant agent mode",
tried several models with and without guardrails. Asked for measurement, speed-up options, better
progress display, and a way to shortcut work that is not agent work.

## How this was measured

Not with WinWright. The app writes an HTTP-level trace per run — `RequestStart` / `RequestEnd` per
LLM round, each line carrying the run-id scope — so `%LOCALAPPDATA%\Pia\Logs\pia-*.log` already
holds better timing data than a UI stopwatch can produce, on real user goals rather than synthetic
ones. Two complete runs were decomposed. A WinWright A/B was prepared but not run: it would have
re-measured what the trace already states exactly, and the trace separates model latency from
client-side overhead, which a UI stopwatch cannot.

**Caveat on the absolute numbers.** Both runs are the owner's own, against Pia Cloud on a local dev
server (`https://localhost:8081/api/ai/`), not customer telemetry. The *structural* findings —
the throttle, the wasted rounds, the sticky lever — do not depend on that. The absolute model-wait
figures do, and would differ against production.

The reusable demo prompts are in the appendix, with the script that decomposes any run.

## Run A — a question that was not agent work

Goal: `@Memory:SpaceX - what is this about?`, run `8e0f1c87`, 2026-09-18 08:19, 1-step plan.

| Phase | Wall clock | LLM rounds | Note |
|---|---|---|---|
| Workspace provision | 0.11 s | — | 22 files, 429 KB copied |
| **Plan turn** | **12.27 s** | 2 | round 2 (4.50 s) is discarded — see below |
| Step 0 | 21.41 s | 4 | incl. 1.98 s cold embedding-model load on first `recall` |
| **Verify turn** | **≥ 4.6 s** | 2 | round 2 was still in flight when the log ends |
| **Total** | **≥ 38.5 s** | 7 | 31.0 s of it waiting on the model |

Chat mode would *not* have been one round — with tools attached it would have made the same `recall`
and `read_topic` calls. It would have skipped the plan turn, the `emit_step_result` round and the
verify turn: roughly step 0 minus 6.7 s, so **≈ 15 s**.

**So the spine cost ≈ 28–30 s on a question that needed none of it** — plan 12.3 s, plus the 6.7 s
`emit_step_result` round, plus a verify turn that run B measured at 11.9 s.

## Run B — real agent work

Goal: a German shopping-trip research task, run `45918bf8`, 2026-09-12 12:07, 5 steps after a replan.

- **Total 14 min 52 s** (12:07:32 → 12:22:24).
- Of that, **≈ 3 min 50 s was the app waiting on the user** — 19 s at plan approval, then 15 s and
  3 min 16 s at two tool-approval prompts.
- System time ≈ **11 min**, across **30 LLM rounds** totalling **644 s** of model wait.

Quote the 11 min, not the 15 — the difference is the user's own think time at the approval gates,
and reporting it as latency hides the real lever, which is *how many gates there are*.

## The four findings

### 1. Every LLM round pays a fixed 500 ms client-side delay

`RateLimitRetryHandler` (`src/Pia.Wpf/Infrastructure/`) enforces `MinRequestInterval = 500 ms`
between requests to the same host —
**proactively**, whether or not the server has ever rate-limited, and measured from the **previous
response's completion** (`_lastRequestTime[host]` is stamped after `base.SendAsync` returns), so it
is a full 500 ms of dead air between rounds rather than a floor on request *start* spacing.

Measured cost: **3.5 s on run A (7 rounds), 14 s on run B (28 throttled rounds)** — 11 % and 2 % of
the respective totals. It is also a `static` semaphore keyed by host, so concurrent runs to Pia
Cloud serialise against each other at 2 requests/second.

It is not agent-specific: the handler sits on every Pia Cloud request, so **chat mode pays the same
500 ms per round** — agent mode just has many more rounds to pay it on.

This is the cheapest win available and is invisible to every model/guardrail experiment already run.

### 2. The plan turn and the verify turn each pay one wasted LLM round

`AiClientService`'s loop completes only at "no tool calls" (`AiClientService.cs:483`). The planner
and the verifier both want exactly one structured call — `emit_plan`, `emit_verdict` — and read
their result out of the captured arguments. But after the tool result goes back, the loop issues
**another full round**, whose output nothing reads. The verifier even hands that round the string
`"Only emit_verdict is available here."` (`AgentVerifier.cs:115`) and then pays for the reply.

Measured on run A: plan round 2 = **4.50 s**, 260 output tokens, all discarded. Run A's verify
round 2 was not captured (the log ends inside it), but run B's full verify turn was **11.9 s over
2 rounds**, so the wasted verify round is of similar size — call the pair **~9 s per run**.

Step turns are different — their last round produces the user-visible prose, so it stays.

### 3. A one-step plan still pays the full spine

The planner produced a single step ("Find and read the SpaceX memory"). That run still paid a 12.3 s
plan turn to decide on one step and an ~8.5 s verify turn to judge whether that step's own claim was
true. `plan.FallBackToSingleTurn` / `RunDegradedSingleTurnAsync` already exist as a degrade path, but
only for a *failed* plan — a plan that succeeds with one trivial step never reaches them.

### 4. Agent mode is sticky — that is why "everyone uses agent"

Flipping the Chat/Agent lever writes `AssistantAgentModeDefault` to settings
(`AssistantViewModel.PersistAgentModeDefaultAsync`, line 1045), and `SeedAgentModeOnChatLoadAsync`
seeds **every new chat** from it. When a run settles, `OnRunProgressSettled` flips the lever back to
Chat but is deliberately guarded against persisting that (`_isSettlingAgentMode`), so the stored
default stays `true`.

So one curious "let me try agent" click silently makes every future new chat start in Agent mode.
Users are not choosing agent each time because it is cool — most of them never chose it twice.
Evidence that this misfires is in the same logs: run `46fc855b` at 06:28 today burned a 7.4 s plan
turn on the goal `d as asd as` before declining it.

## Why the model and guardrail experiments did not help

Both experiments were aimed at surfaces that do not carry the cost.

**Changing the model only touches step turns.** `AgentTurnRouting` sends the plan, replan, reasoning
and verify turns with `pia_persona_type = "fast"` — hard-coded, deliberately, because emitting one
structured tool call does not need the flagship model. So swapping the persona's model changes what
runs the *steps* and leaves the 12–42 s plan turn and the verify turn pinned to whatever the server
maps `"fast"` to. If the plan turn is the complaint, the lever is the server-side `"fast"` mapping,
not the persona.

**Guardrails add tokens, not rounds.** `Persona.Guardrails` is prompt text, injected by
`AssistantPromptComposer` (line 107). It lengthens a request; it cannot add or remove an LLM
round-trip, and round count is what the wall clock tracks here. Toggling it cannot move the number
meaningfully.

## What to do

Ranked by value/effort. Progress and the settled decisions are tracked in
[2026-09-18-agent-mode-latency-checklist.md](2026-09-18-agent-mode-latency-checklist.md).

### Do first — pure latency, no behaviour change

1. **Make the proactive throttle per-provider-config, defaulting to 0 for Pia Cloud.** Keep
   `RateLimitRetryHandler`'s 429 retry and `Retry-After` honouring untouched — only the *pre*-delay
   on requests the server never complained about goes. Config rather than deletion because
   `git log -S MinRequestInterval` surfaces no commit tying the 500 ms to a specific 429 incident,
   so the safe move is to make it tunable per provider rather than to assume no provider needs it.
   *Effort:* XS · *Value:* High — 11 % off a short run, it compounds with every added round, and it
   speeds up chat mode too.
2. **Short-circuit the loop after a terminal tool call.** Give `GetChatCompletionWithToolsAsync` an
   opt-in "this tool ends the turn" set, and pass `emit_plan` / `emit_verdict`. Saves a full round on
   the plan turn and on the verify turn.
   *Effort:* S · *Value:* High — ~8.5 s off run A.

Those two together are **~12 s off a 38 s run, with no model change and no guardrail change** —
which is why the experiments so far did not move the number.

### Do next — stop running the spine on non-agent work

3. **Skip verify on a single-step run that declared no artifact.** The critic is judging one step's
   self-report against the goal that produced it; on run A it added ~8.5 s to agree. Check
   `AgentVerifier` for an existing skip condition before adding one.
   *Effort:* XS · *Value:* High
4. **Route a 1-step plan to the existing single-turn path.** If `emit_plan` returns exactly one step
   that declares no artifact and needs no write tool, run it as one ordinary chat turn via
   `RunDegradedSingleTurnAsync` instead of plan→step→verify. Still pays the plan turn, but drops the
   rest.
   *Effort:* S · *Value:* High
5. **Triage before the plan turn.** The cheaper shape: one fast, tool-less classification call
   (~2–3 s; `AgentTurnRouting.ModelType` is already `"fast"`) that answers "does this goal need a
   plan?" and sends everything else straight to chat. Costs 2–3 s on real agent work, saves ~20 s on
   the rest. The alternative — an `emit_plan` member the model sets to say "this needs no plan" — is
   free but only fires *after* the 12 s plan turn. **Owner decision: which one.**
   *Effort:* S–M · *Value:* High

### Do next — the mode lever

6. **Stop persisting Agent as the new-chat default.** Make the lever per-chat, defaulting to Chat; or
   keep the preference but let `OnRunProgressSettled` persist the fall-back too, so the stickiness at
   least decays after one completed run.
   *Effort:* XS · *Value:* High — this is the one that changes how many users are in agent mode at all.
7. **Offer the downgrade in the UI instead of guessing.** When triage (5) says "no plan needed", the
   composer already has the shape for it — the weak-provider banner and the agent-context offer both
   render inline above the composer. A one-line "This looks like a question — answer it directly?
   [Yes] [Run as agent]" reuses that pattern.
   *Effort:* S · *Value:* Med

### Progress display

The card is not silent today: `ShowPlanSkeleton` renders skeleton rows and `Run_Activity_Planning`
renders a "building a plan" line (`RunProgressViewModel.cs:1115`, `:1451`). The problem is that both
are **static for the whole 12–42 s plan turn** — nothing changes, so it reads as hung.

8. **Stream the plan turn's text into the activity line.** The planner's optional reasoning turn
   sends `tools: null` (`AgentPlanner.PlanAsync`), so its text streams and is currently consumed only
   as an internal `analysis` string. Surfacing it is free content for exactly the dead-air window.
   *Effort:* S · *Value:* High
9. **Show elapsed time and the round counter on the card.** "Planning… 14 s" is a different
   experience from a frozen skeleton, and the data is already in hand.
   *Effort:* XS · *Value:* Med
10. **Name the wait at the approval gates.** Run B sat 3 min 16 s at one tool-approval prompt.
    Whatever else changes, a run blocked on the user should say so loudly enough that the 3 minutes
    are not later remembered as Pia being slow.
    *Effort:* XS · *Value:* Med

## Appendix — demo prompts for re-measuring

Four goals that exercise the spine at different depths. Run each with the Chat/Agent lever on Agent,
then decompose from the log.

1. **Not agent work (control).** `@Memory:SpaceX - what is this about?` — should become a chat turn
   under proposal 4/5. Baseline: ≥ 38.5 s.
2. **One tool, one step.** `List the files in my working folder and tell me which is the largest.`
3. **Multi-step with a write.** `Research the three biggest EV charging networks in Germany and write
   a comparison table to ev-networks.md in my working folder.` — crosses the tool-approval gate, so
   it also measures gate cost.
4. **Ungroundable (declines).** `d as asd as` — measures the cost of rejecting junk input (7.4 s
   today).

Decompose any run by passing the run-id prefix and the log path to
`scripts/Measure-AgentRun.mjs` (see that file). Phase boundaries come from the state lines:
`→ state Planning`, `→ state Running`, `→ state Verifying`, `→ Completed`.
