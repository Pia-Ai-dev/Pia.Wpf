# Agent mode latency — checklist

**Status:** In progress
**Owner:** Marco Altmann
**Written:** 2026-09-18
**Origin:** [2026-09-18-agent-mode-latency.md](2026-09-18-agent-mode-latency.md) — the measurement of
two real runs that this executes against.

Scales used below.

*Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.

*Value:* `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Decided |
|---|---|---|
| G-throttle | Delete the proactive pre-delay, make it per-provider config, or lower it? | Lower to 100 ms — keep the pacing net, recover 80 % of the loss, no new config surface |
| G-triage | Classify before the plan turn, or let `emit_plan` declare "no plan needed" after it? | Triage call before the plan turn (doc option A) — the after-the-fact member cannot recover the 12 s already spent |
| G-lever | Per-chat lever defaulting to Chat, or let the settle fall-back persist? | Per-chat, plus an explicit setting for the new-chat default. Persisting the fall-back is out: `bc846e00` added `_isSettlingAgentMode` because that silently overwrote a preference nobody touched |
| G-triage-accuracy | Does the classifier misroute real agent work to chat often enough to hurt? | CLOSED 2026-09-19 — all four appendix prompts run live, no misroute. Classification costs 1.7–2.6 s. See the validation section below |

## Batch 1 — pure latency, no behaviour change

- [x] **Lower the proactive request throttle to 100 ms.** `RateLimitRetryHandler.MinRequestInterval`
      pauses 500 ms before every request to a host that has never rate-limited us; the 429 retry and
      `Retry-After` honouring are untouched.
      *Deps:* — · *Effort:* XS · *Value:* High
- [x] **End the plan and verify turns at their terminal tool call.** `emit_plan` and `emit_verdict`
      each pay one extra LLM round whose output nothing reads; both handlers already receive a
      `ToolLoopStopSignal` they currently discard.
      *Deps:* — · *Effort:* XS · *Value:* High

## Batch 2 — stop running the spine on non-agent work

- [x] **Triage a goal before the plan turn.** One tool-less `fast` classification call answers "does
      this need a plan?"; anything that does not becomes an ordinary chat turn.
      *Deps:* — · *Effort:* M · *Value:* High
- [x] **Skip verify on a single-step run that declared no artifact.** The critic re-reads one step's
      own self-report against the goal that produced it and agrees, for ~8.5 s.
      *Deps:* — · *Effort:* XS · *Value:* High
- [ ] **Route a 1-step plan to the single-turn path.** A plan that succeeds with one step needing no
      write tool runs through `RunDegradedSingleTurnAsync` instead of plan→step→verify; this is the
      backstop for goals triage waves through.
      *Deps:* triage · *Effort:* S · *Value:* Med
- [x] **Say that the downgrade happened.** An "Answered directly" chip on the reply, built on the
      protected-route indicator's shape. Replaces the planned inline offer: the owner chose to
      downgrade silently and mark it afterwards rather than spend a click confirming it.
      *Deps:* triage · *Effort:* S · *Value:* Med

## Batch 3 — the mode lever

- [x] **Make the Chat/Agent lever per-chat.** Flipping it arms the current chat only; it stops
      writing a global default that every future new chat inherits from one curious click.
      *Deps:* — · *Effort:* S · *Value:* High
- [x] **Add an explicit new-chat default setting.** A user who genuinely wants every chat to start in
      Agent mode sets it deliberately in Settings instead of acquiring it as a side effect.
      *Deps:* per-chat lever · *Effort:* XS · *Value:* Med

## Batch 4 — progress display

- [ ] ~~**Stream the plan turn's reasoning into the activity line.**~~ DROPPED — the premise is false on
      two counts. The reasoning turn is gated behind `AgentPlanReasoningTurnEnabled` (default OFF) **and**
      a provider whose handler drops reasoning effort under tools, so it never runs on Pia Cloud at all;
      and it calls `GetChatResponseAsync`, which returns a complete string rather than streaming. There
      is nothing to surface in the window that reads as hung. The ticking clock below answers that
      complaint instead.
      *Deps:* — · *Effort:* S · *Value:* High
- [x] **Make the elapsed time on the run card tick.** The band's sub-line already carried an elapsed
      figure, but it was read off the persisted ledger and recomputed only on a run event — and the plan
      turn raises none for its whole 12–42 s, so the number sat frozen beside a static skeleton. A
      one-second timer now advances it while the run is working, and deliberately not while it is parked
      on the user.
      *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **Name the wait at the approval gates.** Already true when checked:
      `Run_Activity_WaitingForToolApproval` reads "Waiting for your approval to use {0}" and
      `Run_Activity_PlanApproval` reads "Waiting for you to approve the plan", both localized. No change
      needed. What is still misattributed is the elapsed figure beside them — see below.
      *Deps:* — · *Effort:* XS · *Value:* Med

## Not yet planned

- The server-side `"fast"` persona mapping. `AgentTurnRouting` pins plan, replan, reasoning and
  verify to it, so it — not the user's persona — sets the plan turn's floor. No client change can
  reach it.
- The elapsed figure beside a parked run may still count the user's own think time as run time. The
  clock no longer ticks while parked, but its base comes from the ledger's `wallClockMs`, and whether
  that excludes parked time was not checked — the ledger also tracks `activeMs`, which may be the
  honest number for that line.
- The chat-sync service is in a sustained 429 loop: 1231 rate-limited `PUT /api/v1/chats` in one day
  against the local dev server, 68 of them inside the seven seconds that starved a triage call. The
  throttle no longer lets that traffic delay the user, but nothing has looked at why sync keeps pushing
  into a wall. Unrelated to agent latency.
- Re-measurement against production rather than a local dev server. Every absolute number in the
  analysis is the owner's own machine against `localhost:8081`.

## Live validation, 2026-09-19

All four appendix prompts run against Pia Cloud with the lever on Agent. Evidence is
`%LOCALAPPDATA%PiaLogspia-2026-09-19.log`.

| Prompt | Triage | Outcome |
|---|---|---|
| 1 `@Memory:SpaceX…` | AnswerDirectly, 2168 ms | No run created. Was ≥ 38.5 s, now one chat turn |
| 2 files/largest | NeedsPlan, 2602 ms | 1-step run, 39.2 s, **verifier skipped** |
| 3 EV networks | NeedsPlan, 1692 ms | 3 steps after a replan, 4 min 46 s |
| 4 `d as asd as` | AnswerDirectly, 1843 ms | Answered in 3 s. Previously a 7.4 s plan turn before declining |

**Batch 1 holds on every run.** `Round 1: a tool handler stopped the loop` appears once per plan turn,
once per replan and once per verify turn across all four runs — no turn spent a second round.

**Batch 2's verify-skip fired in the wild** on prompt 2: `Verifier skipped: one step, no artifact on
either channel`. Spine overhead on that run is now triage 2.6 s + plan 4.5 s ≈ 7 s of its 39.2 s; the
other 34 s is the step itself scanning 22 files. Before this work the same shape cost ~28–30 s of spine.

**Prompt 2 is the case for the unbuilt step 4.** It planned one step, declared no artifact and needed no
write tool — exactly the shape that should have gone to `RunDegradedSingleTurnAsync`. Triage was not
wrong to plan it (it is a tool task), so triage cannot be the thing that catches it.

**Prompt 3's 4 min 46 s is mostly not the spine.** Step 0 ran 99 s and called no tool at all: Pia
Cloud's server-side web search reported its budget exhausted, and the model wrote prose saying so
instead of the file. The critic caught it (`passed: false`), the replan re-ran the work and the file
landed — the spine earning its cost on real agent work. The exhausted search budget is a server-side
quota with no client-side lever; there is no `web_search` among the 53 tools the client sends.
## What remains

Cheapest decisive work first.

1. **Route a 1-step plan to the single-turn path** — the one planned step never authorized, and
   prompt 2 above is a measured instance of exactly the shape it targets. Check what
   `RunDegradedSingleTurnAsync` stamps on the run first: it was built for a FAILED plan, and a
   "degraded" label on a run that worked is its own regression.
2. The two items under **Not yet planned** above.

Verifiable only by eye, since no test reaches them: the clock ticking through a plan turn, the lever
falling back to Chat after a downgrade, the `Answered directly` chip beside `Protected` on a narrow
window, and the new Settings → Assistant toggle.

`AssistantAgentTriageEnabled` has no Settings UI. It was added as an off switch for while
G-triage-accuracy was open; that gate is now closed, so the setting is a candidate for removal.
