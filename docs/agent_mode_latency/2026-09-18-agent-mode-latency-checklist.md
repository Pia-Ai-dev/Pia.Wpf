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
| G-triage-accuracy | Does the classifier misroute real agent work to chat often enough to hurt? | OPEN — answered by the four appendix prompts now that B2 has landed. A wrong "chat" is worse than a slow plan turn, so the classifier defaults to PLAN on anything but the exact word ANSWER, and `AssistantAgentTriageEnabled` turns it off wholesale |

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

- [ ] **Make the Chat/Agent lever per-chat.** Flipping it arms the current chat only; it stops
      writing a global default that every future new chat inherits from one curious click.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **Add an explicit new-chat default setting.** A user who genuinely wants every chat to start in
      Agent mode sets it deliberately in Settings instead of acquiring it as a side effect.
      *Deps:* per-chat lever · *Effort:* XS · *Value:* Med

## Batch 4 — progress display

- [ ] **Stream the plan turn's reasoning into the activity line.** The optional reasoning turn sends
      `tools: null` so its text already streams; it is consumed as an internal string and thrown away,
      in exactly the 12–42 s window that reads as hung.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **Show elapsed time and the round counter on the run card.** "Planning… 14 s" reads as working;
      a static skeleton reads as frozen.
      *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **Name the wait at the approval gates.** Run B sat 3 min 16 s at one tool-approval prompt; a run
      blocked on the user must say so, or the wait is remembered as Pia being slow.
      *Deps:* — · *Effort:* XS · *Value:* Med

## Not yet planned

- The server-side `"fast"` persona mapping. `AgentTurnRouting` pins plan, replan, reasoning and
  verify to it, so it — not the user's persona — sets the plan turn's floor. No client change can
  reach it.
- Concurrent runs to Pia Cloud still serialise: `RateLimitRetryHandler`'s semaphore is `static` and
  keyed by host, so two runs pace against each other even after the 100 ms change.
- Re-measurement against production rather than a local dev server. Every absolute number in the
  analysis is the owner's own machine against `localhost:8081`.

## Suggested order

1. Batch 1, both steps — cheapest, decisive, and they land before any behaviour question is open.
2. Re-measure with `scripts/Measure-AgentRun.mjs` on appendix prompt 1 to confirm the ~12 s.
3. Batch 4's streaming step — it makes every remaining wait legible, and is independent of Batch 2.
4. Batch 2, verify-skip first (XS, no new concept), then triage, then the composer offer.
5. Batch 3 last: it changes who is exposed to all of the above, so it is worth the most once the
   above has made agent mode cheaper.
